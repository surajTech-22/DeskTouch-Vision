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
    internal const uint CallbackEventFlag = 0x00050000;
    private const uint WHDR_DONE = 0x00000001;
    private const uint WHDR_PREPARED = 0x00000002;
    private const int MicSampleRate = 16000;
    private const int MicChunkBytes = 640; // 20ms PCM16 mono @ 16kHz
    private const int FallbackMicSampleRate = 48000;
    private const int FallbackMicChunkBytes = 1920; // 20ms PCM16 mono @ 48kHz (downsampled 3:1 -> 640B @ 16kHz)
    private const int MicSlots = 8;

    private static readonly object _capGate = new();
    private static long _capCacheMs;
    private static (bool HasCamera, bool HasMicrophone) _cachedCaps;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _cameraLifecycleGate = new(1, 1);
    private readonly Func<bool> _isFastUdpActive;

    private volatile bool _cameraRunning;
    private volatile bool _micRunning;
    private volatile bool _disposed;
    private int _cameraLens;
    private int _cameraGeneration;
    private int _cameraFramesDelivered;
    private string? _lastCameraError;
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

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
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
        int gen;
        lock (_gate)
        {
            if (_cameraRunning && _cameraLens == lens) return;
            _cameraLens = lens;
            _cameraRunning = true;
            Volatile.Write(ref _cameraFramesDelivered, 0);
            _lastCameraError = null;
            gen = Interlocked.Increment(ref _cameraGeneration);
        }
        _ = Task.Run(() => InitializeCameraAsync(lens, gen, forceSharedReadOnly: false));
    }

    internal void StopCamera()
    {
        lock (_gate)
        {
            _cameraRunning = false;
            Interlocked.Increment(ref _cameraGeneration);
        }
        _ = Task.Run(DisposeCameraAsync);
    }

    internal static (bool HasCamera, bool HasMicrophone) DetectLocalMediaCapabilities(bool forceRefresh = false)
    {
        long now = Environment.TickCount64;
        lock (_capGate)
        {
            if (!forceRefresh && _capCacheMs != 0 && now - _capCacheMs < 15000)
                return _cachedCaps;
        }

        bool hasCam = false;
        try
        {
            var camTask = Task.Run(async () => await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture));
            if (camTask.Wait(1200))
                hasCam = camTask.Result is { Count: > 0 };
        }
        catch { }

        bool hasMic = false;
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
                        hasMic = true;
                        break;
                    }
                }
            }
        }
        catch { }

        var result = (hasCam, hasMic);
        lock (_capGate)
        {
            _cachedCaps = result;
            _capCacheMs = Environment.TickCount64;
        }
        return result;
    }

    internal void StartMic()
    {
        if (_disposed) return;
        Thread? prevThread = null;
        lock (_gate)
        {
            if (_micRunning) return;
            prevThread = _micThread;
        }
        if (prevThread != null && prevThread != Thread.CurrentThread)
            try { prevThread.Join(300); } catch { }

        lock (_gate)
        {
            if (_micRunning || _disposed) return;
            _micRunning = true;
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
        }
        try { signal?.Set(); } catch (ObjectDisposedException) { }
        if (toJoin != null && toJoin != Thread.CurrentThread)
            try { toJoin.Join(600); } catch { }
    }

    internal void StopAll()
    {
        StopCamera();
        StopMic();
    }

    private static bool IsLikelyInfraredCameraName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        (name.Contains("IR ", StringComparison.OrdinalIgnoreCase) ||
         name.EndsWith(" IR", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("Infrared", StringComparison.OrdinalIgnoreCase));

    private async Task InitializeCameraAsync(int lens, int gen, bool forceSharedReadOnly)
    {
        await _cameraLifecycleGate.WaitAsync();
        try
        {
            await DisposeCameraCoreAsync();
            if (!_cameraRunning || _disposed || gen != Volatile.Read(ref _cameraGeneration)) return;

            var rawCameras = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            if (rawCameras == null || rawCameras.Count == 0)
            {
                if (gen == Volatile.Read(ref _cameraGeneration))
                {
                    _cameraRunning = false;
                    CameraStatusChanged?.Invoke(0, "No webcam detected on this PC");
                }
                return;
            }

            var cameras = rawCameras
                .OrderBy(c => IsLikelyInfraredCameraName(c.Name) ? 1 : 0)
                .ToList();
            int startIdx = Math.Abs(lens) % cameras.Count;

            MediaCapture? capture = null;
            MediaFrameSource? colorSource = null;
            DeviceInformation? chosen = null;
            bool usedSharedReadOnly = forceSharedReadOnly;
            Exception? lastInitError = null;

            for (int attempt = 0; attempt < cameras.Count; attempt++)
            {
                if (!_cameraRunning || _disposed || gen != Volatile.Read(ref _cameraGeneration)) return;
                var candidate = cameras[(startIdx + attempt) % cameras.Count];
                bool candidateShared = forceSharedReadOnly;
                var candidateCapture = new MediaCapture();
                var settings = new MediaCaptureInitializationSettings
                {
                    VideoDeviceId = candidate.Id,
                    StreamingCaptureMode = StreamingCaptureMode.Video,
                    MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                    SharingMode = candidateShared
                        ? MediaCaptureSharingMode.SharedReadOnly
                        : MediaCaptureSharingMode.ExclusiveControl
                };
                try
                {
                    await candidateCapture.InitializeAsync(settings);
                }
                catch (Exception ex1)
                {
                    lastInitError = ex1;
                    candidateCapture.Dispose();
                    if (candidateShared) continue;
                    candidateShared = true;
                    candidateCapture = new MediaCapture();
                    settings.SharingMode = MediaCaptureSharingMode.SharedReadOnly;
                    try
                    {
                        await candidateCapture.InitializeAsync(settings);
                    }
                    catch (Exception ex2)
                    {
                        lastInitError = ex2;
                        candidateCapture.Dispose();
                        continue;
                    }
                }

                var candidateColor = candidateCapture.FrameSources.Values
                    .OrderBy(s => s.Info.MediaStreamType == MediaStreamType.VideoPreview ? 0
                                : s.Info.MediaStreamType == MediaStreamType.VideoRecord ? 1 : 2)
                    .FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color);
                if (candidateColor == null)
                {
                    candidateCapture.Dispose();
                    continue;
                }

                capture = candidateCapture;
                colorSource = candidateColor;
                chosen = candidate;
                usedSharedReadOnly = candidateShared;
                break;
            }

            if (capture == null || colorSource == null || chosen == null)
            {
                if (gen == Volatile.Read(ref _cameraGeneration))
                {
                    _cameraRunning = false;
                    string errDetail = lastInitError != null ? ": " + lastInitError.Message : "";
                    CameraStatusChanged?.Invoke(0, "Webcam color stream unavailable" + errDetail);
                }
                return;
            }

            if (!_cameraRunning || _disposed || gen != Volatile.Read(ref _cameraGeneration))
            {
                capture.Dispose();
                return;
            }

            capture.Failed += (_, args) => OnCameraCaptureFailed(gen, args.Message);

            if (!usedSharedReadOnly)
                await TrySelectOptimalColorFormatAsync(colorSource);

            MediaFrameReader reader;
            try
            {
                reader = await capture.CreateFrameReaderAsync(colorSource, MediaEncodingSubtypes.Bgra8);
            }
            catch
            {
                reader = await capture.CreateFrameReaderAsync(colorSource);
            }
            try
            {
                reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            }
            catch { }

            Volatile.Write(ref _cameraFramesDelivered, 0);
            reader.FrameArrived += OnCameraFrameArrived;
            var startStatus = await reader.StartAsync();
            if (startStatus != MediaFrameReaderStartStatus.Success)
            {
                reader.FrameArrived -= OnCameraFrameArrived;
                try { await reader.StopAsync(); } catch { }
                reader.Dispose();
                capture.Dispose();
                if (!usedSharedReadOnly && _cameraRunning && !_disposed && gen == Volatile.Read(ref _cameraGeneration))
                {
                    _ = Task.Run(() => InitializeCameraAsync(lens, gen, forceSharedReadOnly: true));
                    return;
                }
                if (gen == Volatile.Read(ref _cameraGeneration))
                {
                    _cameraRunning = false;
                    CameraStatusChanged?.Invoke(0, $"Webcam start status: {startStatus}");
                }
                return;
            }

            lock (_gate)
            {
                if (!_cameraRunning || _disposed || gen != Volatile.Read(ref _cameraGeneration))
                {
                    reader.FrameArrived -= OnCameraFrameArrived;
                    try { reader.Dispose(); } catch { }
                    try { capture.Dispose(); } catch { }
                    return;
                }
                _mediaCapture = capture;
                _frameReader = reader;
            }

            CameraStatusChanged?.Invoke(1, $"Live PC webcam ({chosen.Name})");
            _ = MonitorFirstCameraFrameAsync(lens, gen, usedSharedReadOnly, chosen.Name);
        }
        catch (Exception ex)
        {
            if (gen == Volatile.Read(ref _cameraGeneration))
            {
                _cameraRunning = false;
                CameraStatusChanged?.Invoke(0, "PC webcam unavailable: " + ex.Message);
            }
        }
        finally
        {
            _cameraLifecycleGate.Release();
        }
    }

    private static async Task TrySelectOptimalColorFormatAsync(MediaFrameSource colorSource)
    {
        try
        {
            var formats = colorSource.SupportedFormats;
            if (formats == null || formats.Count == 0) return;
            MediaFrameFormat? best = null;
            int bestScore = int.MinValue;
            foreach (var f in formats)
            {
                var vf = f.VideoFormat;
                if (vf == null || vf.Width < 320 || vf.Height < 240) continue;
                int w = (int)vf.Width;
                int h = (int)vf.Height;
                double fps = f.FrameRate != null && f.FrameRate.Denominator > 0
                    ? (double)f.FrameRate.Numerator / f.FrameRate.Denominator
                    : 0;
                int score = 0;
                if (w == 640 && (h == 480 || h == 360)) score += 600;
                else if (w <= 960 && h <= 540) score += 480;
                else if (w <= 1280 && h <= 720) score += 380;
                else score -= (w - 1280);

                if (fps >= 14 && fps <= 31) score += 200;
                else if (fps > 31 && fps <= 60) score += 100;

                string sub = f.Subtype ?? "";
                if (sub.Equals(MediaEncodingSubtypes.Nv12, StringComparison.OrdinalIgnoreCase) ||
                    sub.Equals(MediaEncodingSubtypes.Yuy2, StringComparison.OrdinalIgnoreCase) ||
                    sub.Equals(MediaEncodingSubtypes.Bgra8, StringComparison.OrdinalIgnoreCase))
                    score += 150;
                else if (sub.Equals(MediaEncodingSubtypes.Mjpg, StringComparison.OrdinalIgnoreCase))
                    score += 90;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = f;
                }
            }
            if (best != null)
                await colorSource.SetFormatAsync(best);
        }
        catch { }
    }

    private async Task MonitorFirstCameraFrameAsync(int lens, int gen, bool usedSharedReadOnly, string cameraName)
    {
        try
        {
            await Task.Delay(4000);
            if (_disposed || !_cameraRunning || gen != Volatile.Read(ref _cameraGeneration)) return;
            if (Volatile.Read(ref _cameraFramesDelivered) > 0) return;

            if (!usedSharedReadOnly)
            {
                int retryGen;
                lock (_gate)
                {
                    if (_disposed || !_cameraRunning || gen != Volatile.Read(ref _cameraGeneration)) return;
                    retryGen = Interlocked.Increment(ref _cameraGeneration);
                }
                await InitializeCameraAsync(lens, retryGen, forceSharedReadOnly: true);
                return;
            }

            lock (_gate)
            {
                if (gen != Volatile.Read(ref _cameraGeneration)) return;
                _cameraRunning = false;
                Interlocked.Increment(ref _cameraGeneration);
            }
            await DisposeCameraAsync();
            string reason = !string.IsNullOrWhiteSpace(_lastCameraError)
                ? $"PC webcam ({cameraName}) frame error: {_lastCameraError}"
                : $"PC webcam ({cameraName}) produced no video frames (check camera shutter/privacy or close other camera apps)";
            CameraStatusChanged?.Invoke(0, reason);
        }
        catch { }
    }

    private void OnCameraCaptureFailed(int gen, string? message)
    {
        if (_disposed || gen != Volatile.Read(ref _cameraGeneration)) return;
        lock (_gate)
        {
            if (gen != Volatile.Read(ref _cameraGeneration)) return;
            _cameraRunning = false;
            Interlocked.Increment(ref _cameraGeneration);
        }
        _ = Task.Run(DisposeCameraAsync);
        CameraStatusChanged?.Invoke(0, "PC webcam stopped: " + (string.IsNullOrWhiteSpace(message) ? "device unavailable" : message));
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

        MediaFrameReference? frame = null;
        try
        {
            frame = sender.TryAcquireLatestFrame();
            var vmf = frame?.VideoMediaFrame;
            var sw = vmf?.SoftwareBitmap;
            var surface = sw == null ? vmf?.Direct3DSurface : null;
            if (sw == null && surface == null)
            {
                frame?.Dispose();
                Volatile.Write(ref _cameraEncoding, 0);
                return;
            }

            if (sw != null)
            {
                SoftwareBitmap bgraCopy = sw.BitmapPixelFormat == BitmapPixelFormat.Bgra8
                    ? SoftwareBitmap.Copy(sw)
                    : SoftwareBitmap.Convert(sw, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
                frame?.Dispose();
                frame = null;
                Volatile.Write(ref _lastCameraFrameMs, now);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using (bgraCopy)
                        {
                            byte[]? jpeg = await EncodeSoftwareBitmapToJpegAsync(bgraCopy);
                            if (jpeg != null && jpeg.Length > 0 && jpeg.Length <= 160 * 1024 && _cameraRunning)
                            {
                                Interlocked.Increment(ref _cameraFramesDelivered);
                                CameraFrameCaptured?.Invoke(jpeg);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _lastCameraError ??= ex.Message;
                    }
                    finally
                    {
                        Volatile.Write(ref _cameraEncoding, 0);
                    }
                });
                return;
            }

            // Direct3DSurface fallback when driver delivers GPU-backed frames.
            var heldFrame = frame!;
            frame = null;
            Volatile.Write(ref _lastCameraFrameMs, now);
            _ = Task.Run(async () =>
            {
                try
                {
                    using (heldFrame)
                    {
                        var d3d = heldFrame.VideoMediaFrame?.Direct3DSurface;
                        if (d3d == null) return;
                        using var surfaceBitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(d3d);
                        if (surfaceBitmap == null) return;
                        using SoftwareBitmap bgraCopy = surfaceBitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8
                            ? SoftwareBitmap.Copy(surfaceBitmap)
                            : SoftwareBitmap.Convert(surfaceBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
                        byte[]? jpeg = await EncodeSoftwareBitmapToJpegAsync(bgraCopy);
                        if (jpeg != null && jpeg.Length > 0 && jpeg.Length <= 160 * 1024 && _cameraRunning)
                        {
                            Interlocked.Increment(ref _cameraFramesDelivered);
                            CameraFrameCaptured?.Invoke(jpeg);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _lastCameraError ??= ex.Message;
                }
                finally
                {
                    Volatile.Write(ref _cameraEncoding, 0);
                }
            });
        }
        catch (Exception ex)
        {
            frame?.Dispose();
            _lastCameraError ??= ex.Message;
            Volatile.Write(ref _cameraEncoding, 0);
        }
    }

    private static async Task<byte[]?> EncodeSoftwareBitmapToJpegAsync(SoftwareBitmap bitmap)
    {
        byte[]? primary = await EncodeSoftwareBitmapWithParamsAsync(bitmap, maxWidth: 640, maxHeight: 480, quality: 0.55f);
        if (primary != null && primary.Length <= 48 * 1024)
            return primary;
        byte[]? compact = await EncodeSoftwareBitmapWithParamsAsync(bitmap, maxWidth: 480, maxHeight: 360, quality: 0.42f);
        return compact ?? primary;
    }

    private static async Task<byte[]?> EncodeSoftwareBitmapWithParamsAsync(
        SoftwareBitmap bitmap, int maxWidth, int maxHeight, float quality)
    {
        using var ras = new InMemoryRandomAccessStream();
        var props = new BitmapPropertySet
        {
            { "ImageQuality", new BitmapTypedValue(quality, PropertyType.Single) }
        };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, ras, props);
        try
        {
            encoder.SetSoftwareBitmap(bitmap);
        }
        catch
        {
            using var fallback = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            encoder.SetSoftwareBitmap(fallback);
        }
        if (bitmap.PixelWidth > maxWidth || bitmap.PixelHeight > maxHeight)
        {
            double s = Math.Min((double)maxWidth / bitmap.PixelWidth, (double)maxHeight / bitmap.PixelHeight);
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
        await _cameraLifecycleGate.WaitAsync();
        try
        {
            await DisposeCameraCoreAsync();
        }
        finally
        {
            _cameraLifecycleGate.Release();
        }
    }

    private async Task DisposeCameraCoreAsync()
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
        using var localSignal = new AutoResetEvent(false);
        lock (_gate)
        {
            if (!_micRunning || _disposed) return;
            _micSignal = localSignal;
        }

        int hdrSize = Marshal.SizeOf<WAVEHDR>();
        int flagsOffset = Marshal.OffsetOf<WAVEHDR>(nameof(WAVEHDR.dwFlags)).ToInt32();
        int recordedOffset = Marshal.OffsetOf<WAVEHDR>(nameof(WAVEHDR.dwBytesRecorded)).ToInt32();
        nint[] hdrPtrs = new nint[MicSlots];
        nint[] dataPtrs = new nint[MicSlots];
        bool[] prepared = new bool[MicSlots];

        for (int i = 0; i < MicSlots; i++)
        {
            hdrPtrs[i] = Marshal.AllocHGlobal(hdrSize);
            dataPtrs[i] = Marshal.AllocHGlobal(FallbackMicChunkBytes);
        }

        try
        {
            uint devId = SelectBestHardwareMicDeviceId(out string micName);
            int captureRate = MicSampleRate;
            int captureChunkBytes = MicChunkBytes;
            var fmt = new WAVEFORMATEX
            {
                wFormatTag = WAVE_FORMAT_PCM,
                nChannels = 1,
                nSamplesPerSec = (uint)captureRate,
                wBitsPerSample = 16,
                nBlockAlign = 2,
                nAvgBytesPerSec = (uint)(captureRate * 2),
                cbSize = 0
            };

            if (waveInOpen(out _hWaveIn, devId, ref fmt, localSignal.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CallbackEventFlag) != 0 ||
                _hWaveIn == IntPtr.Zero)
            {
                captureRate = FallbackMicSampleRate;
                captureChunkBytes = FallbackMicChunkBytes;
                fmt.nSamplesPerSec = (uint)captureRate;
                fmt.nAvgBytesPerSec = (uint)(captureRate * 2);
                if (waveInOpen(out _hWaveIn, devId, ref fmt, localSignal.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CallbackEventFlag) != 0 ||
                    _hWaveIn == IntPtr.Zero)
                {
                    _micRunning = false;
                    MicStatusChanged?.Invoke(0, "No hardware microphone available on this PC");
                    return;
                }
            }

            for (int i = 0; i < MicSlots; i++)
            {
                var hdr = new WAVEHDR
                {
                    lpData = dataPtrs[i],
                    dwBufferLength = (uint)captureChunkBytes,
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
            int nextSlot = 0;

            while (_micRunning && !_disposed)
            {
                localSignal.WaitOne(20);
                if (!_micRunning || _disposed) break;

                for (int scanned = 0; scanned < MicSlots; scanned++)
                {
                    int slot = nextSlot;
                    if (!prepared[slot])
                    {
                        nextSlot = (nextSlot + 1) % MicSlots;
                        continue;
                    }
                    uint flags = unchecked((uint)Marshal.ReadInt32(hdrPtrs[slot], flagsOffset));
                    if ((flags & WHDR_DONE) == 0)
                        break;

                    uint recorded = unchecked((uint)Marshal.ReadInt32(hdrPtrs[slot], recordedOffset));
                    int n = (int)Math.Min(recorded, (uint)captureChunkBytes) & ~1;
                    if (n > 0)
                    {
                        byte[] raw = new byte[n];
                        Marshal.Copy(dataPtrs[slot], raw, 0, n);
                        if (captureRate == FallbackMicSampleRate)
                        {
                            byte[] down = Downsample48kTo16kMono(raw);
                            if (down.Length > 0)
                                MicAudioCaptured?.Invoke(down, MicSampleRate);
                        }
                        else
                        {
                            MicAudioCaptured?.Invoke(raw, captureRate);
                        }
                    }

                    if (_micRunning && !_disposed)
                    {
                        Marshal.WriteInt32(hdrPtrs[slot], recordedOffset, 0);
                        Marshal.WriteInt32(hdrPtrs[slot], flagsOffset, unchecked((int)(flags & ~WHDR_DONE)));
                        _ = waveInAddBuffer(_hWaveIn, hdrPtrs[slot], hdrSize);
                    }
                    nextSlot = (nextSlot + 1) % MicSlots;
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
            lock (_gate)
            {
                if (ReferenceEquals(_micSignal, localSignal))
                    _micSignal = null;
            }
            for (int i = 0; i < MicSlots; i++)
            {
                Marshal.FreeHGlobal(hdrPtrs[i]);
                Marshal.FreeHGlobal(dataPtrs[i]);
            }
        }
    }

    private static byte[] Downsample48kTo16kMono(byte[] pcm48k)
    {
        int inSamples = pcm48k.Length / 2;
        int outSamples = inSamples / 3;
        if (outSamples <= 0) return Array.Empty<byte>();
        byte[] dst = new byte[outSamples * 2];
        for (int i = 0; i < outSamples; i++)
        {
            int b = i * 6;
            short s0 = (short)(pcm48k[b] | (pcm48k[b + 1] << 8));
            short s1 = (short)(pcm48k[b + 2] | (pcm48k[b + 3] << 8));
            short s2 = (short)(pcm48k[b + 4] | (pcm48k[b + 5] << 8));
            int avg = (s0 + s1 + s2) / 3;
            short outSample = (short)Math.Clamp(avg, short.MinValue, short.MaxValue);
            int d = i * 2;
            dst[d] = (byte)(outSample & 0xFF);
            dst[d + 1] = (byte)((outSample >> 8) & 0xFF);
        }
        return dst;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cameraRunning = false;
        _micRunning = false;
        Interlocked.Increment(ref _cameraGeneration);
        StopMic();
        try { DisposeCameraCoreAsync().GetAwaiter().GetResult(); } catch { }
    }
}
