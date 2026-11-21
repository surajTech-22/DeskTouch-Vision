using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace DeskTouch.DeviceShare;

/// <summary>
/// Captures local Windows webcam frames (MediaCapture + MediaFrameReader) and physical
/// hardware microphone audio (pinned WinMM waveIn PCM16 mono @ 24kHz) for multi-system streaming.
/// Never opens virtual loopback cables (CABLE Output / Stereo Mix) when sharing the PC mic.
/// </summary>
internal sealed class WindowsMediaCaptureService : IDisposable
{
    private const uint WAVE_MAPPER = 0xFFFFFFFF;
    private const int WAVE_FORMAT_PCM = 1;
    private const uint CALLBACK_EVENT = 0x00030000;
    private const uint WHDR_DONE = 0x00000001;
    private const uint WHDR_PREPARED = 0x00000002;
    private const int MicSampleRate = 24000;
    private const int MicChunkBytes = 960; // 20ms PCM16 mono @ 24kHz
    private const int MicSlots = 8;

    private readonly object _gate = new();
    private readonly Func<bool> _isFastUdpActive;

    private volatile bool _cameraRunning;
    private volatile bool _micRunning;
    private volatile bool _disposed;
    private int _cameraLens;
    private long _lastCameraFrameMs;
    private int _cameraEncoding;

    private MediaCapture? _mediaCapture;
    private MediaFrameReader? _frameReader;

    private Thread? _micThread;
    private AutoResetEvent? _micSignal;
    private IntPtr _hWaveIn;

    internal event Action<byte[]>? CameraFrameCaptured;
    internal event Action<int, string>? CameraStatusChanged;
    internal event Action<byte[], int>? MicAudioCaptured;
    internal event Action<int, string>? MicStatusChanged;

    internal bool IsCameraActive => _cameraRunning;
    internal bool IsMicActive => _micRunning;

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WAVEINCAPSW
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public uint dwFormats;
        public ushort wChannels;
        public ushort wReserved1;
    }

    [DllImport("winmm.dll")]
    private static extern uint waveInGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int waveInGetDevCapsW(UIntPtr uDeviceID, ref WAVEINCAPSW pwic, int cbwic);

    [DllImport("winmm.dll")]
    private static extern int waveInOpen(out IntPtr phwi, uint uDeviceID, ref WAVEFORMATEX pwfx,
        IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);

    [DllImport("winmm.dll")]
    private static extern int waveInPrepareHeader(IntPtr hwi, IntPtr pwh, int cbwh);

    [DllImport("winmm.dll")]
    private static extern int waveInUnprepareHeader(IntPtr hwi, IntPtr pwh, int cbwh);

    [DllImport("winmm.dll")]
    private static extern int waveInAddBuffer(IntPtr hwi, IntPtr pwh, int cbwh);

    [DllImport("winmm.dll")]
    private static extern int waveInStart(IntPtr hwi);

    [DllImport("winmm.dll")]
    private static extern int waveInReset(IntPtr hwi);

    [DllImport("winmm.dll")]
    private static extern int waveInClose(IntPtr hwi);

    internal WindowsMediaCaptureService(Func<bool>? isFastUdpActive = null)
    {
        _isFastUdpActive = isFastUdpActive ?? (() => false);
    }

    internal void StartCamera(int lens = 0)
    {
        if (_disposed) return;
        lock (_gate)
        {
            if (_cameraRunning && _cameraLens == lens) return;
            _cameraLens = lens;
            _cameraRunning = true;
        }
        _ = Task.Run(() => InitializeCameraAsync(lens));
    }

    internal void StopCamera()
    {
        _cameraRunning = false;
        _ = Task.Run(DisposeCameraAsync);
    }

    internal void StartMic()
    {
        if (_disposed) return;
        lock (_gate)
        {
            if (_micRunning) return;
            _micRunning = true;
            _micSignal?.Dispose();
            _micSignal = new AutoResetEvent(false);
            _micThread = new Thread(MicCaptureLoop)
            {
                IsBackground = true,
                Name = "DeskTouch MicCapture",
                Priority = ThreadPriority.AboveNormal
            };
            _micThread.Start();
        }
    }

    internal void StopMic()
    {
        Thread? toJoin;
        AutoResetEvent? signal;
        lock (_gate)
        {
            _micRunning = false;
            toJoin = _micThread;
            _micThread = null;
            signal = _micSignal;
            signal?.Set();
        }
        if (toJoin != null && toJoin != Thread.CurrentThread)
            try { toJoin.Join(600); } catch { }
        lock (_gate)
        {
            _micSignal?.Dispose();
            _micSignal = null;
        }
    }

    internal void StopAll()
    {
        StopCamera();
        StopMic();
    }

    private async Task InitializeCameraAsync(int lens)
    {
        await DisposeCameraAsync();
        if (!_cameraRunning || _disposed) return;
        try
        {
            var cameras = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            if (cameras == null || cameras.Count == 0)
            {
                CameraStatusChanged?.Invoke(0, "No webcam detected on this PC");
                _cameraRunning = false;
                return;
            }
            int idx = Math.Abs(lens) % cameras.Count;
            var chosen = cameras[idx];

            var capture = new MediaCapture();
            var settings = new MediaCaptureInitializationSettings
            {
                VideoDeviceId = chosen.Id,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                SharingMode = MediaCaptureSharingMode.SharedReadOnly
            };
            try
            {
                await capture.InitializeAsync(settings);
            }
            catch
            {
                capture.Dispose();
                capture = new MediaCapture();
                settings.SharingMode = MediaCaptureSharingMode.ExclusiveControl;
                await capture.InitializeAsync(settings);
            }
            if (!_cameraRunning || _disposed)
            {
                capture.Dispose();
                return;
            }

            var colorSource = capture.FrameSources.Values
                .FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color);
            if (colorSource == null)
            {
                capture.Dispose();
                CameraStatusChanged?.Invoke(0, "Webcam color stream unavailable");
                _cameraRunning = false;
                return;
            }

            MediaFrameReader reader;
            try
            {
                reader = await capture.CreateFrameReaderAsync(colorSource, MediaEncodingSubtypes.Bgra8);
            }
            catch
            {
                reader = await capture.CreateFrameReaderAsync(colorSource);
            }
            reader.FrameArrived += OnCameraFrameArrived;
            var startStatus = await reader.StartAsync();
            if (startStatus != MediaFrameReaderStartStatus.Success || !_cameraRunning || _disposed)
            {
                reader.FrameArrived -= OnCameraFrameArrived;
                try { await reader.StopAsync(); } catch { }
                reader.Dispose();
                capture.Dispose();
                CameraStatusChanged?.Invoke(0, $"Webcam start status: {startStatus}");
                _cameraRunning = false;
                return;
            }

            lock (_gate)
            {
                if (!_cameraRunning || _disposed)
                {
                    reader.FrameArrived -= OnCameraFrameArrived;
                    reader.Dispose();
                    capture.Dispose();
                    return;
                }
                _mediaCapture = capture;
                _frameReader = reader;
            }
            CameraStatusChanged?.Invoke(1, $"Live PC webcam ({chosen.Name})");
        }
        catch (Exception ex)
        {
            _cameraRunning = false;
            CameraStatusChanged?.Invoke(0, "PC webcam unavailable: " + ex.Message);
        }
    }

    private void OnCameraFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (!_cameraRunning || _disposed) return;
        long now = Environment.TickCount64;
        long minInterval = _isFastUdpActive() ? 45L : 110L;
        if (now - Volatile.Read(ref _lastCameraFrameMs) < minInterval)
            return;
        if (Interlocked.CompareExchange(ref _cameraEncoding, 1, 0) != 0)
            return;

        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            var sw = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (sw == null)
            {
                Volatile.Write(ref _cameraEncoding, 0);
                return;
            }

            SoftwareBitmap? bgraCopy = sw.BitmapPixelFormat == BitmapPixelFormat.Bgra8 &&
                                     sw.BitmapAlphaMode == BitmapAlphaMode.Premultiplied
                ? SoftwareBitmap.Copy(sw)
                : SoftwareBitmap.Convert(sw, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

            Volatile.Write(ref _lastCameraFrameMs, now);
            _ = Task.Run(async () =>
            {
                try
                {
                    using (bgraCopy)
                    {
                        byte[]? jpeg = await EncodeSoftwareBitmapToJpegAsync(bgraCopy);
                        if (jpeg != null && jpeg.Length > 0 && jpeg.Length <= 160 * 1024 && _cameraRunning)
                            CameraFrameCaptured?.Invoke(jpeg);
                    }
                }
                catch { }
                finally
                {
                    Volatile.Write(ref _cameraEncoding, 0);
                }
            });
        }
        catch
        {
            Volatile.Write(ref _cameraEncoding, 0);
        }
    }

    private static async Task<byte[]?> EncodeSoftwareBitmapToJpegAsync(SoftwareBitmap bitmap)
    {
        using var ras = new InMemoryRandomAccessStream();
        var props = new BitmapPropertySet
        {
            { "ImageQuality", new BitmapTypedValue(0.62f, PropertyType.Single) }
        };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, ras, props);
        encoder.SetSoftwareBitmap(bitmap);
        if (bitmap.PixelWidth > 640 || bitmap.PixelHeight > 480)
        {
            double s = Math.Min(640.0 / bitmap.PixelWidth, 480.0 / bitmap.PixelHeight);
            uint tw = Math.Max(160, ((uint)Math.Round(bitmap.PixelWidth * s)) & ~1u);
            uint th = Math.Max(120, ((uint)Math.Round(bitmap.PixelHeight * s)) & ~1u);
            encoder.BitmapTransform.ScaledWidth = tw;
            encoder.BitmapTransform.ScaledHeight = th;
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Linear;
        }
        await encoder.FlushAsync();
        if (ras.Size is 0 or > 160 * 1024) return null;
        ras.Seek(0);
        using var ms = new MemoryStream((int)ras.Size);
        await ras.AsStreamForRead().CopyToAsync(ms);
        return ms.ToArray();
    }

    private async Task DisposeCameraAsync()
    {
        MediaFrameReader? reader;
        MediaCapture? capture;
        lock (_gate)
        {
            reader = _frameReader;
            capture = _mediaCapture;
            _frameReader = null;
            _mediaCapture = null;
        }
        if (reader != null)
        {
            try
            {
                reader.FrameArrived -= OnCameraFrameArrived;
                await reader.StopAsync();
            }
            catch { }
            try { reader.Dispose(); } catch { }
        }
        if (capture != null)
        {
            try { capture.Dispose(); } catch { }
        }
    }

    private static uint SelectBestHardwareMicDeviceId(out string chosenName)
    {
        chosenName = "Default Microphone";
        try
        {
            uint count = waveInGetNumDevs();
            int size = Marshal.SizeOf<WAVEINCAPSW>();
            for (uint i = 0; i < count; i++)
            {
                var caps = new WAVEINCAPSW();
                if (waveInGetDevCapsW(new UIntPtr(i), ref caps, size) == 0 && !string.IsNullOrWhiteSpace(caps.szPname))
                {
                    string name = caps.szPname.TrimEnd('\0', ' ');
                    if (!WindowsAudioDeviceBridge.IsVirtualLoopbackCaptureName(name))
                    {
                        chosenName = name;
                        return i;
                    }
                }
            }
        }
        catch { }
        return WAVE_MAPPER;
    }

    private void MicCaptureLoop()
    {
        var signal = _micSignal;
        if (signal == null) return;

        int hdrSize = Marshal.SizeOf<WAVEHDR>();
        int flagsOffset = Marshal.OffsetOf<WAVEHDR>(nameof(WAVEHDR.dwFlags)).ToInt32();
        int recordedOffset = Marshal.OffsetOf<WAVEHDR>(nameof(WAVEHDR.dwBytesRecorded)).ToInt32();
        nint[] hdrPtrs = new nint[MicSlots];
        nint[] dataPtrs = new nint[MicSlots];
        bool[] prepared = new bool[MicSlots];

        for (int i = 0; i < MicSlots; i++)
        {
            hdrPtrs[i] = Marshal.AllocHGlobal(hdrSize);
            dataPtrs[i] = Marshal.AllocHGlobal(MicChunkBytes);
        }

        try
        {
            uint devId = SelectBestHardwareMicDeviceId(out string micName);
            var fmt = new WAVEFORMATEX
            {
                wFormatTag = WAVE_FORMAT_PCM,
                nChannels = 1,
                nSamplesPerSec = MicSampleRate,
                wBitsPerSample = 16,
                nBlockAlign = 2,
                nAvgBytesPerSec = MicSampleRate * 2,
                cbSize = 0
            };

            if (waveInOpen(out _hWaveIn, devId, ref fmt, signal.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CALLBACK_EVENT) != 0 ||
                _hWaveIn == IntPtr.Zero)
            {
                _micRunning = false;
                MicStatusChanged?.Invoke(0, "No hardware microphone available on this PC");
                return;
            }

            for (int i = 0; i < MicSlots; i++)
            {
                var hdr = new WAVEHDR
                {
                    lpData = dataPtrs[i],
                    dwBufferLength = MicChunkBytes,
                    dwFlags = 0
                };
                Marshal.StructureToPtr(hdr, hdrPtrs[i], fDeleteOld: false);
                if (waveInPrepareHeader(_hWaveIn, hdrPtrs[i], hdrSize) == 0)
                {
                    prepared[i] = true;
                    _ = waveInAddBuffer(_hWaveIn, hdrPtrs[i], hdrSize);
                }
            }

            if (waveInStart(_hWaveIn) != 0)
            {
                _micRunning = false;
                MicStatusChanged?.Invoke(0, "Could not start PC microphone capture");
                return;
            }

            MicStatusChanged?.Invoke(1, $"Live PC mic ({micName})");

            while (_micRunning && !_disposed)
            {
                signal.WaitOne(50);
                if (!_micRunning || _disposed) break;

                for (int i = 0; i < MicSlots; i++)
                {
                    if (!prepared[i]) continue;
                    uint flags = unchecked((uint)Marshal.ReadInt32(hdrPtrs[i], flagsOffset));
                    if ((flags & WHDR_DONE) == 0) continue;

                    uint recorded = unchecked((uint)Marshal.ReadInt32(hdrPtrs[i], recordedOffset));
                    int n = (int)Math.Min(recorded, MicChunkBytes);
                    if (n > 0)
                    {
                        byte[] pkt = new byte[n];
                        Marshal.Copy(dataPtrs[i], pkt, 0, n);
                        MicAudioCaptured?.Invoke(pkt, MicSampleRate);
                    }

                    if (_micRunning && !_disposed)
                    {
                        _ = waveInUnprepareHeader(_hWaveIn, hdrPtrs[i], hdrSize);
                        prepared[i] = false;
                        var hdr = new WAVEHDR
                        {
                            lpData = dataPtrs[i],
                            dwBufferLength = MicChunkBytes,
                            dwFlags = 0
                        };
                        Marshal.StructureToPtr(hdr, hdrPtrs[i], fDeleteOld: false);
                        if (waveInPrepareHeader(_hWaveIn, hdrPtrs[i], hdrSize) == 0)
                        {
                            prepared[i] = true;
                            _ = waveInAddBuffer(_hWaveIn, hdrPtrs[i], hdrSize);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            MicStatusChanged?.Invoke(0, "PC mic capture error: " + ex.Message);
        }
        finally
        {
            _micRunning = false;
            if (_hWaveIn != IntPtr.Zero)
            {
                try { _ = waveInReset(_hWaveIn); } catch { }
                for (int i = 0; i < MicSlots; i++)
                {
                    if (prepared[i])
                    {
                        try { _ = waveInUnprepareHeader(_hWaveIn, hdrPtrs[i], hdrSize); } catch { }
                    }
                }
                try { _ = waveInClose(_hWaveIn); } catch { }
                _hWaveIn = IntPtr.Zero;
            }
            for (int i = 0; i < MicSlots; i++)
            {
                Marshal.FreeHGlobal(hdrPtrs[i]);
                Marshal.FreeHGlobal(dataPtrs[i]);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cameraRunning = false;
        _micRunning = false;
        StopMic();
        try { DisposeCameraAsync().GetAwaiter().GetResult(); } catch { }
    }
}
