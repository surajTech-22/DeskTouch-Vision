using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace DeskTouch.DeviceShare;

internal enum AudioEndpointRole
{
    Console = 0,
    Multimedia = 1,
    Communications = 2
}

internal readonly record struct WaveOutDeviceInfo(uint WaveOutId, string Name);

internal readonly record struct AudioEndpointInfo(string EndpointId, string FriendlyName, bool IsDefault);

internal readonly record struct VirtualMicMatch(
    bool Installed,
    uint RenderWaveOutId,
    string RenderName,
    string CaptureEndpointId,
    string CaptureName);

/// <summary>
/// Discovers virtual audio cable drivers (VB-Audio Virtual Cable, VoiceMeeter VAIO, VAC),
/// enumerates Core Audio recording/playback endpoints, switches Windows default recording mic
/// safely via IPolicyConfig, and provides a 1-click official VB-Cable driver installer.
/// </summary>
internal static class WindowsAudioDeviceBridge
{
    private const uint CLSCTX_ALL = 23;
    private const uint DEVICE_STATE_ACTIVE = 0x00000001;
    private const uint STGM_READ = 0x00000000;
    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid CLSID_PolicyConfigClient = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");
    private static readonly PROPERTYKEY PKEY_Device_FriendlyName = new()
    {
        fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        pid = 14
    };

    private static string? _previousDefaultCaptureId;
    private static string? _activatedVirtualCaptureId;

    private enum EDataFlow
    {
        eRender = 0,
        eCapture = 1,
        eAll = 2
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr ptrVal;
        public IntPtr ptrVal2;
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, uint dwStateMask, out IMMDeviceCollection? ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, AudioEndpointRole role, out IMMDevice? ppEndpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice? ppDevice);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr pClient);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr pClient);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint pcDevices);
        [PreserveSig] int Item(uint nDevice, out IMMDevice? ppDevice);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object? ppInterface);
        [PreserveSig] int OpenPropertyStore(uint stgmAccess, out IPropertyStore? ppProperties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string? ppstrId);
        [PreserveSig] int GetState(out uint pdwState);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PROPERTYKEY pkey);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
    }

    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat();
        [PreserveSig] int GetDeviceFormat();
        [PreserveSig] int ResetDeviceFormat();
        [PreserveSig] int SetDeviceFormat();
        [PreserveSig] int GetProcessingPeriod();
        [PreserveSig] int SetProcessingPeriod();
        [PreserveSig] int GetShareMode();
        [PreserveSig] int SetShareMode();
        [PreserveSig] int GetPropertyValue();
        [PreserveSig] int SetPropertyValue();
        [PreserveSig]
        int SetDefaultEndpoint(
            [In, MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId,
            [In, MarshalAs(UnmanagedType.U4)] AudioEndpointRole role);
        [PreserveSig] int SetEndpointVisibility();
    }

    [ComImport]
    [Guid("568B9108-44BF-40B4-9006-86AFE5B5A620")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfigVista
    {
        [PreserveSig] int GetMixFormat();
        [PreserveSig] int GetDeviceFormat();
        [PreserveSig] int ResetDeviceFormat();
        [PreserveSig] int SetDeviceFormat();
        [PreserveSig] int GetProcessingPeriod();
        [PreserveSig] int SetProcessingPeriod();
        [PreserveSig] int GetShareMode();
        [PreserveSig] int SetShareMode();
        [PreserveSig] int GetPropertyValue();
        [PreserveSig] int SetPropertyValue();
        [PreserveSig]
        int SetDefaultEndpoint(
            [In, MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId,
            [In, MarshalAs(UnmanagedType.U4)] AudioEndpointRole role);
        [PreserveSig] int SetEndpointVisibility();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WAVEOUTCAPSW
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public uint dwFormats;
        public ushort wChannels;
        public ushort wReserved1;
        public uint dwSupport;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

    [DllImport("winmm.dll")]
    private static extern uint waveOutGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int waveOutGetDevCapsW(UIntPtr uDeviceID, ref WAVEOUTCAPSW pwoc, int cbwoc);

    internal static List<WaveOutDeviceInfo> ListWaveOutDevices()
    {
        var list = new List<WaveOutDeviceInfo>();
        try
        {
            uint count = waveOutGetNumDevs();
            int size = Marshal.SizeOf<WAVEOUTCAPSW>();
            for (uint i = 0; i < count; i++)
            {
                var caps = new WAVEOUTCAPSW();
                if (waveOutGetDevCapsW(new UIntPtr(i), ref caps, size) == 0 && !string.IsNullOrWhiteSpace(caps.szPname))
                    list.Add(new WaveOutDeviceInfo(i, caps.szPname.TrimEnd('\0', ' ')));
            }
        }
        catch { }
        return list;
    }

    internal static List<AudioEndpointInfo> ListCaptureEndpoints()
    {
        var list = new List<AudioEndpointInfo>();
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? collection = null;
        try
        {
            var type = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator);
            if (type == null) return list;
            enumerator = Activator.CreateInstance(type) as IMMDeviceEnumerator;
            if (enumerator == null) return list;

            string? defaultId = null;
            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.eCapture, AudioEndpointRole.Communications, out var defDev) == 0 && defDev != null)
            {
                try { _ = defDev.GetId(out defaultId); }
                finally { Marshal.FinalReleaseComObject(defDev); }
            }
            if (string.IsNullOrEmpty(defaultId) &&
                enumerator.GetDefaultAudioEndpoint(EDataFlow.eCapture, AudioEndpointRole.Console, out var defConsole) == 0 && defConsole != null)
            {
                try { _ = defConsole.GetId(out defaultId); }
                finally { Marshal.FinalReleaseComObject(defConsole); }
            }

            if (enumerator.EnumAudioEndpoints(EDataFlow.eCapture, DEVICE_STATE_ACTIVE, out collection) != 0 || collection == null)
                return list;
            if (collection.GetCount(out uint count) != 0)
                return list;

            for (uint i = 0; i < count; i++)
            {
                if (collection.Item(i, out var dev) != 0 || dev == null) continue;
                try
                {
                    if (dev.GetId(out string? id) != 0 || string.IsNullOrWhiteSpace(id)) continue;
                    string friendly = ReadEndpointFriendlyName(dev) ?? id;
                    bool isDef = string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase);
                    list.Add(new AudioEndpointInfo(id, friendly, isDef));
                }
                finally
                {
                    Marshal.FinalReleaseComObject(dev);
                }
            }
        }
        catch { }
        finally
        {
            if (collection != null) Marshal.FinalReleaseComObject(collection);
            if (enumerator != null) Marshal.FinalReleaseComObject(enumerator);
        }
        return list;
    }

    private static string? ReadEndpointFriendlyName(IMMDevice dev)
    {
        IPropertyStore? store = null;
        try
        {
            if (dev.OpenPropertyStore(STGM_READ, out store) != 0 || store == null) return null;
            var key = PKEY_Device_FriendlyName;
            if (store.GetValue(ref key, out var pv) != 0) return null;
            try
            {
                if (pv.vt == 31 && pv.ptrVal != IntPtr.Zero) // VT_LPWSTR
                    return Marshal.PtrToStringUni(pv.ptrVal);
                return null;
            }
            finally
            {
                _ = PropVariantClear(ref pv);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (store != null) Marshal.FinalReleaseComObject(store);
        }
    }

    internal static bool IsVirtualLoopbackCaptureName(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return false;
        return deviceName.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase) ||
               deviceName.Contains("CABLE-A Output", StringComparison.OrdinalIgnoreCase) ||
               deviceName.Contains("CABLE-B Output", StringComparison.OrdinalIgnoreCase) ||
               deviceName.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) ||
               deviceName.Contains("VoiceMeeter Output", StringComparison.OrdinalIgnoreCase) ||
               deviceName.Contains("VoiceMeeter Aux Output", StringComparison.OrdinalIgnoreCase) ||
               deviceName.Contains("VoiceMeeter VAIO3 Output", StringComparison.OrdinalIgnoreCase) ||
               deviceName.Contains("Virtual Audio Cable", StringComparison.OrdinalIgnoreCase) ||
               deviceName.Contains("Stereo Mix", StringComparison.OrdinalIgnoreCase) ||
               deviceName.Contains("What U Hear", StringComparison.OrdinalIgnoreCase);
    }

    internal static VirtualMicMatch MatchVirtualMicPair(
        IReadOnlyList<WaveOutDeviceInfo> renderDevices,
        IReadOnlyList<AudioEndpointInfo> captureEndpoints)
    {
        (string RenderToken, string CaptureToken)[] pairs =
        [
            ("CABLE Input", "CABLE Output"),
            ("Speakers (VB-Audio Virtual", "CABLE Output"),
            ("VB-Audio Virtual", "CABLE Output"),
            ("CABLE-A Input", "CABLE-A Output"),
            ("CABLE-B Input", "CABLE-B Output"),
            ("VoiceMeeter Aux Input", "VoiceMeeter Aux Output"),
            ("VoiceMeeter VAIO3 Input", "VoiceMeeter VAIO3 Output"),
            ("VoiceMeeter Input", "VoiceMeeter Output"),
            ("Line 1", "Virtual Audio Cable")
        ];

        foreach (var (renderToken, captureToken) in pairs)
        {
            var render = renderDevices.FirstOrDefault(r =>
                r.Name.Contains(renderToken, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(render.Name)) continue;
            var capture = captureEndpoints.FirstOrDefault(c =>
                c.FriendlyName.Contains(captureToken, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(capture.EndpointId))
            {
                return new VirtualMicMatch(
                    Installed: true,
                    RenderWaveOutId: render.WaveOutId,
                    RenderName: render.Name,
                    CaptureEndpointId: capture.EndpointId,
                    CaptureName: capture.FriendlyName);
            }
        }
        return default;
    }

    internal static VirtualMicMatch DetectVirtualMicPair() =>
        MatchVirtualMicPair(ListWaveOutDevices(), ListCaptureEndpoints());

    internal static string? GetDefaultCaptureEndpointId() =>
        ListCaptureEndpoints().FirstOrDefault(e => e.IsDefault).EndpointId;

    private static bool TryInvokeSetDefaultEndpoint(object policyObj, string endpointId, AudioEndpointRole role)
    {
        if (policyObj is IPolicyConfig policy)
            return policy.SetDefaultEndpoint(endpointId, role) == 0;
        if (policyObj is IPolicyConfigVista vista)
            return vista.SetDefaultEndpoint(endpointId, role) == 0;
        return false;
    }

    internal static bool TrySetDefaultCaptureDevice(string endpointId)
    {
        if (string.IsNullOrWhiteSpace(endpointId)) return false;
        object? policyObj = null;
        try
        {
            var type = Type.GetTypeFromCLSID(CLSID_PolicyConfigClient);
            if (type == null) return false;
            policyObj = Activator.CreateInstance(type);
            if (policyObj == null || (policyObj is not IPolicyConfig && policyObj is not IPolicyConfigVista))
                return false;

            string? currentDef = GetDefaultCaptureEndpointId();
            if (!string.IsNullOrWhiteSpace(currentDef) &&
                !string.Equals(currentDef, endpointId, StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(_previousDefaultCaptureId))
            {
                _previousDefaultCaptureId = currentDef;
            }

            foreach (AudioEndpointRole role in Enum.GetValues<AudioEndpointRole>())
            {
                if (!TryInvokeSetDefaultEndpoint(policyObj, endpointId, role))
                    return false;
            }
            _activatedVirtualCaptureId = endpointId;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (policyObj != null) Marshal.FinalReleaseComObject(policyObj);
        }
    }

    internal static void RestorePreviousDefaultCaptureDeviceIfChanged()
    {
        string? prev = _previousDefaultCaptureId;
        string? activeVirt = _activatedVirtualCaptureId;
        _previousDefaultCaptureId = null;
        _activatedVirtualCaptureId = null;
        if (string.IsNullOrWhiteSpace(prev) || string.IsNullOrWhiteSpace(activeVirt)) return;

        string? curDef = GetDefaultCaptureEndpointId();
        if (!string.Equals(curDef, activeVirt, StringComparison.OrdinalIgnoreCase)) return;

        object? policyObj = null;
        try
        {
            var type = Type.GetTypeFromCLSID(CLSID_PolicyConfigClient);
            if (type == null) return;
            policyObj = Activator.CreateInstance(type);
            if (policyObj == null) return;
            foreach (AudioEndpointRole role in Enum.GetValues<AudioEndpointRole>())
                _ = TryInvokeSetDefaultEndpoint(policyObj, prev, role);
        }
        catch { }
        finally
        {
            if (policyObj != null) Marshal.FinalReleaseComObject(policyObj);
        }
    }

    internal static async Task<(bool Installed, string Message)> InstallVbCableDriverAsync(
        Action<string> reportProgress,
        CancellationToken cancellation = default)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "DeskTouchVision-VBCable-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        string zipPath = Path.Combine(tempDir, "VBCABLE_Driver_Pack.zip");
        try
        {
            reportProgress("Downloading official VB-Audio Virtual Cable driver...");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DeskTouchVision/1.0");

            string? downloadUrl = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack46.zip";
            using var headOrGet = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellation);
            HttpResponseMessage? activeResp = headOrGet;
            if (!headOrGet.IsSuccessStatusCode)
            {
                headOrGet.Dispose();
                activeResp = null;
                string pageHtml = await http.GetStringAsync("https://vb-audio.com/Cable/", cancellation);
                var m = Regex.Match(pageHtml, @"https://[^""'\s]+VBCABLE_Driver_Pack\d*\.zip", RegexOptions.IgnoreCase);
                if (!m.Success)
                {
                    OpenVbCableWebsite();
                    return (false, "Opened vb-audio.com/Cable in your browser. Run VBCABLE_Setup_x64.exe as Administrator to enable native mic routing.");
                }
                downloadUrl = m.Value;
                activeResp = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellation);
            }

            using (activeResp)
            {
                activeResp.EnsureSuccessStatusCode();
                await using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await using var netStream = await activeResp.Content.ReadAsStreamAsync(cancellation);
                await netStream.CopyToAsync(fs, cancellation);
            }

            reportProgress("Extracting driver package...");
            string extractDir = Path.Combine(tempDir, "pkg");
            ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);
            string setupExe = Environment.Is64BitOperatingSystem
                ? Path.Combine(extractDir, "VBCABLE_Setup_x64.exe")
                : Path.Combine(extractDir, "VBCABLE_Setup.exe");
            if (!File.Exists(setupExe))
            {
                setupExe = Directory.GetFiles(extractDir, "VBCABLE_Setup*.exe", SearchOption.AllDirectories)
                    .FirstOrDefault() ?? "";
            }
            if (!File.Exists(setupExe))
                return (false, "Driver setup executable not found in archive.");

            reportProgress("Approve Windows Administrator prompt & click 'Install Driver'...");
            using var proc = Process.Start(new ProcessStartInfo(setupExe)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(setupExe)!
            });
            if (proc != null)
                await proc.WaitForExitAsync(cancellation);

            await Task.Delay(750, cancellation);
            var detected = DetectVirtualMicPair();
            if (detected.Installed)
            {
                return (true, $"Installed! Ready as '{detected.CaptureName}'. (If Zoom/Discord doesn't see it yet, restart that app or Windows once.)");
            }
            return (false, "Setup finished. If 'CABLE Output' doesn't appear yet, click Re-scan or reboot Windows once to finalize the audio driver.");
        }
        catch (System.ComponentModel.Win32Exception canceled) when (canceled.NativeErrorCode == 1223)
        {
            return (false, "Administrator permission cancelled. Approve the UAC prompt to install VB-Audio Virtual Cable.");
        }
        catch (Exception ex)
        {
            return (false, "Could not auto-install VB-Cable: " + ex.Message);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    internal static void OpenVbCableWebsite()
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://vb-audio.com/Cable/") { UseShellExecute = true });
        }
        catch { }
    }
}

/// <summary>
/// Ultra-low-latency WinMM PCM16 output engine using permanently pinned unmanaged
/// WAVEHDR & data buffers (`Marshal.AllocHGlobal`), event-driven buffer recycling, linear resampling
/// to 48kHz native stereo/mono rate, 32-packet sequence deduplication, and lock-free RMS metering.
/// </summary>
internal sealed class ManagedAudioOutputEngine : IDisposable
{
    private const uint WAVE_MAPPER = 0xFFFFFFFF;
    private const int WAVE_FORMAT_PCM = 1;
    internal const uint CallbackEventFlag = 0x00050000;
    private const uint WHDR_DONE = 0x00000001;
    private const uint WHDR_PREPARED = 0x00000002;
    private const int SlotCount = 4;
    private const int TargetPrebufferChunks = 2;
    private const int MaxMonoChunkBytes = 8192;
    private const int MaxSlotBytes = 16384;
    private const int MaxQueuedChunks = 4;

    private readonly object _lifecycleGate = new();
    private readonly object _seqGate = new();
    private readonly int[] _recentSeqRing = new int[32];
    private int _recentSeqHead;
    private int _maxSeenSeq;
    private readonly ConcurrentQueue<byte[]> _pcmQueue = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly nint[] _hdrPtrs = new nint[SlotCount];
    private readonly nint[] _dataPtrs = new nint[SlotCount];
    private readonly bool[] _inFlight = new bool[SlotCount];
    private readonly int _hdrSize = Marshal.SizeOf<WAVEHDR>();
    private readonly int _flagsOffset = Marshal.OffsetOf<WAVEHDR>(nameof(WAVEHDR.dwFlags)).ToInt32();
    private readonly Thread _worker;

    private IntPtr _hWaveOut;
    private uint _targetDeviceId = WAVE_MAPPER;
    private int _outputSampleRate = 48000;
    private int _outputChannels = 2;
    private long _firstQueuedAfterIdleMs;
    private volatile bool _deviceDirty = true;
    private volatile bool _hasAudio;
    private volatile bool _disposed;
    private float _gain = 1.0f;
    private float _currentLevel;
    private string _lastError = "";

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

    [DllImport("winmm.dll")]
    private static extern int waveOutOpen(out IntPtr phwo, uint uDeviceID, ref WAVEFORMATEX pwfx,
        IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);
    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);
    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(IntPtr hwo, IntPtr pwh, int cbwh);
    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);
    [DllImport("winmm.dll")]
    private static extern int waveOutReset(IntPtr hwo);
    [DllImport("winmm.dll")]
    private static extern int waveOutClose(IntPtr hwo);

    internal ManagedAudioOutputEngine(uint deviceId = WAVE_MAPPER)
    {
        _targetDeviceId = deviceId;
        for (int i = 0; i < SlotCount; i++)
        {
            _hdrPtrs[i] = Marshal.AllocHGlobal(_hdrSize);
            _dataPtrs[i] = Marshal.AllocHGlobal(MaxSlotBytes);
        }
        _worker = new Thread(PlaybackLoop)
        {
            IsBackground = true,
            Name = "DeskTouch AudioOut",
            Priority = ThreadPriority.AboveNormal
        };
        _worker.Start();
    }

    internal float Gain
    {
        get => Volatile.Read(ref _gain);
        set => Volatile.Write(ref _gain, Math.Clamp(value, 0.25f, 3.0f));
    }

    internal float CurrentLevel => Volatile.Read(ref _currentLevel);
    internal int OutputSampleRate => Volatile.Read(ref _outputSampleRate);
    internal string LastError => Volatile.Read(ref _lastError);

    internal void SetOutputDevice(uint deviceId)
    {
        if (_disposed) return;
        if (Volatile.Read(ref _targetDeviceId) == deviceId) return;
        Volatile.Write(ref _targetDeviceId, deviceId);
        _deviceDirty = true;
        ClearQueue();
        if (_hasAudio)
            _signal.Set();
    }

    internal void ClearQueue()
    {
        while (_pcmQueue.TryDequeue(out _)) { }
        lock (_seqGate)
        {
            Array.Clear(_recentSeqRing, 0, _recentSeqRing.Length);
            _recentSeqHead = 0;
            _maxSeenSeq = 0;
        }
        Volatile.Write(ref _firstQueuedAfterIdleMs, 0);
        Volatile.Write(ref _currentLevel, 0f);
    }

    internal void EnqueuePcm16Mono(byte[] pcm, int sourceSampleRate = 16000, int audioSeq = 0)
    {
        if (_disposed || pcm == null || pcm.Length < 2) return;
        if (audioSeq > 0)
        {
            lock (_seqGate)
            {
                if (_maxSeenSeq > 0 && Math.Abs(audioSeq - _maxSeenSeq) > 1000)
                {
                    Array.Clear(_recentSeqRing, 0, _recentSeqRing.Length);
                    _recentSeqHead = 0;
                    _maxSeenSeq = 0;
                }
                if (_maxSeenSeq > 0 && audioSeq <= _maxSeenSeq - 24)
                    return;
                for (int i = 0; i < _recentSeqRing.Length; i++)
                {
                    if (_recentSeqRing[i] == audioSeq)
                        return;
                }
                _recentSeqRing[_recentSeqHead] = audioSeq;
                _recentSeqHead = (_recentSeqHead + 1) & 31;
                if (audioSeq > _maxSeenSeq)
                    _maxSeenSeq = audioSeq;
            }
        }
        _hasAudio = true;
        int outRate = Volatile.Read(ref _outputSampleRate);
        byte[] processed = ResamplePcm16Mono(pcm, sourceSampleRate, outRate, Gain, out float rmsPeak);

        float prevLevel = Volatile.Read(ref _currentLevel);
        float smoothed = rmsPeak > prevLevel ? rmsPeak : (prevLevel * 0.72f + rmsPeak * 0.28f);
        Volatile.Write(ref _currentLevel, smoothed);

        if (_pcmQueue.IsEmpty)
            Volatile.Write(ref _firstQueuedAfterIdleMs, Environment.TickCount64);
        _pcmQueue.Enqueue(processed);
        while (_pcmQueue.Count > MaxQueuedChunks)
            _pcmQueue.TryDequeue(out _);
        _signal.Set();
    }

    internal static byte[] ExpandMonoToStereoPcm16(ReadOnlySpan<byte> monoPcm16)
    {
        int samples = monoPcm16.Length / 2;
        if (samples <= 0) return Array.Empty<byte>();
        byte[] stereo = new byte[Math.Min(samples * 4, MaxSlotBytes)];
        int outSamples = stereo.Length / 4;
        for (int i = 0; i < outSamples; i++)
        {
            byte lo = monoPcm16[i * 2];
            byte hi = monoPcm16[i * 2 + 1];
            int dst = i * 4;
            stereo[dst] = lo;
            stereo[dst + 1] = hi;
            stereo[dst + 2] = lo;
            stereo[dst + 3] = hi;
        }
        return stereo;
    }

    internal static byte[] ResamplePcm16Mono(
        ReadOnlySpan<byte> pcm16,
        int srcRate,
        int dstRate,
        float gain,
        out float normalizedPeak)
    {
        int srcSamples = pcm16.Length / 2;
        if (srcSamples <= 0)
        {
            normalizedPeak = 0f;
            return Array.Empty<byte>();
        }
        int safeSrc = Math.Clamp(srcRate > 0 ? srcRate : 16000, 8000, 96000);
        int safeDst = Math.Clamp(dstRate > 0 ? dstRate : safeSrc, 8000, 96000);
        int dstSamples = safeSrc == safeDst
            ? srcSamples
            : Math.Max(1, (int)Math.Round(srcSamples * (double)safeDst / safeSrc));

        byte[] output = new byte[Math.Min(dstSamples * 2, MaxMonoChunkBytes)];
        int actualDst = output.Length / 2;
        double ratio = srcSamples / (double)actualDst;
        double sumSq = 0;
        float g = Math.Clamp(gain, 0.25f, 3.0f);

        for (int i = 0; i < actualDst; i++)
        {
            double sVal;
            if (safeSrc == safeDst)
            {
                int idx = i * 2;
                sVal = (short)(pcm16[idx] | (pcm16[idx + 1] << 8));
            }
            else
            {
                double pos = i * ratio;
                int i0 = Math.Clamp((int)pos, 0, srcSamples - 1);
                int i1 = Math.Min(i0 + 1, srcSamples - 1);
                double frac = pos - i0;
                short s0 = (short)(pcm16[i0 * 2] | (pcm16[i0 * 2 + 1] << 8));
                short s1 = (short)(pcm16[i1 * 2] | (pcm16[i1 * 2 + 1] << 8));
                sVal = s0 + (s1 - s0) * frac;
            }
            double amplified = sVal * g;
            // Gentle soft saturation above 92% scale to preserve voice clarity without harsh digital clipping.
            if (amplified > 30000.0) amplified = 30000.0 + (amplified - 30000.0) * 0.25;
            else if (amplified < -30000.0) amplified = -30000.0 + (amplified + 30000.0) * 0.25;
            int clamped = Math.Clamp((int)Math.Round(amplified), -32768, 32767);
            double norm = clamped / 32768.0;
            sumSq += norm * norm;
            short outSample = (short)clamped;
            output[i * 2] = (byte)(outSample & 0xFF);
            output[i * 2 + 1] = (byte)((outSample >> 8) & 0xFF);
        }

        double rms = Math.Sqrt(sumSq / Math.Max(1, actualDst));
        normalizedPeak = (float)Math.Clamp(rms * 2.6, 0.0, 1.0);
        return output;
    }

    private void PlaybackLoop()
    {
        try
        {
            while (!_disposed)
            {
                if (!_hasAudio)
                {
                    _signal.WaitOne(100);
                    continue;
                }
                if (_deviceDirty || _hWaveOut == IntPtr.Zero)
                {
                    lock (_lifecycleGate)
                    {
                        if (_disposed) break;
                        CloseDeviceLocked();
                        OpenDeviceLocked(Volatile.Read(ref _targetDeviceId));
                        _deviceDirty = false;
                    }
                }

                bool submittedAny = false;
                lock (_lifecycleGate)
                {
                    if (_hWaveOut != IntPtr.Zero && !_deviceDirty)
                    {
                        RecycleCompletedSlotsLocked();
                        int inFlight = CountInFlightLocked();
                        long firstWaitMs = Volatile.Read(ref _firstQueuedAfterIdleMs);
                        bool prebufferReady = inFlight > 0 ||
                                              _pcmQueue.Count >= TargetPrebufferChunks ||
                                              (firstWaitMs > 0 && Environment.TickCount64 - firstWaitMs >= 22);
                        if (prebufferReady)
                        {
                            while (!_pcmQueue.IsEmpty)
                            {
                                int freeSlot = FindFreeSlotLocked();
                                if (freeSlot < 0) break;
                                if (!_pcmQueue.TryDequeue(out var pcm) || pcm.Length == 0) continue;
                                SubmitSlotLocked(freeSlot, pcm);
                                submittedAny = true;
                            }
                        }
                    }
                }

                if (_pcmQueue.IsEmpty || !submittedAny)
                    _signal.WaitOne(15);
            }
        }
        catch { }
    }

    private void OpenDeviceLocked(uint deviceId)
    {
        foreach (var (candidateRate, channels) in new (int Rate, ushort Channels)[]
                 { (48000, 2), (48000, 1), (24000, 2), (24000, 1), (16000, 1) })
        {
            ushort blockAlign = (ushort)(channels * 2);
            var fmt = new WAVEFORMATEX
            {
                wFormatTag = WAVE_FORMAT_PCM,
                nChannels = channels,
                nSamplesPerSec = (uint)candidateRate,
                wBitsPerSample = 16,
                nBlockAlign = blockAlign,
                nAvgBytesPerSec = (uint)(candidateRate * blockAlign),
                cbSize = 0
            };
            int res = waveOutOpen(out _hWaveOut, deviceId, ref fmt, _signal.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CallbackEventFlag);
            if (res == 0 && _hWaveOut != IntPtr.Zero)
            {
                Volatile.Write(ref _outputSampleRate, candidateRate);
                Volatile.Write(ref _outputChannels, channels);
                Volatile.Write(ref _lastError, "");
                return;
            }
        }
        _hWaveOut = IntPtr.Zero;
        Volatile.Write(ref _lastError, "Audio output device busy or unavailable");
    }

    private int CountInFlightLocked()
    {
        int count = 0;
        for (int i = 0; i < SlotCount; i++)
            if (_inFlight[i]) count++;
        return count;
    }

    private void RecycleCompletedSlotsLocked()
    {
        for (int i = 0; i < SlotCount; i++)
        {
            if (!_inFlight[i]) continue;
            uint flags = unchecked((uint)Marshal.ReadInt32(_hdrPtrs[i], _flagsOffset));
            if ((flags & WHDR_DONE) == 0) continue;
            if ((flags & WHDR_PREPARED) != 0)
                _ = waveOutUnprepareHeader(_hWaveOut, _hdrPtrs[i], _hdrSize);
            _inFlight[i] = false;
        }
    }

    private int FindFreeSlotLocked()
    {
        for (int i = 0; i < SlotCount; i++)
            if (!_inFlight[i]) return i;
        return -1;
    }

    private void SubmitSlotLocked(int slot, byte[] monoPcm)
    {
        byte[] payload = Volatile.Read(ref _outputChannels) == 2
            ? ExpandMonoToStereoPcm16(monoPcm)
            : monoPcm;
        int len = Math.Min(payload.Length, MaxSlotBytes);
        if (len <= 0) return;
        Marshal.Copy(payload, 0, _dataPtrs[slot], len);
        var hdr = new WAVEHDR
        {
            lpData = _dataPtrs[slot],
            dwBufferLength = (uint)len,
            dwFlags = 0
        };
        Marshal.StructureToPtr(hdr, _hdrPtrs[slot], fDeleteOld: false);
        if (waveOutPrepareHeader(_hWaveOut, _hdrPtrs[slot], _hdrSize) != 0)
            return;
        if (waveOutWrite(_hWaveOut, _hdrPtrs[slot], _hdrSize) != 0)
        {
            _ = waveOutUnprepareHeader(_hWaveOut, _hdrPtrs[slot], _hdrSize);
            return;
        }
        _inFlight[slot] = true;
    }

    private void CloseDeviceLocked()
    {
        if (_hWaveOut == IntPtr.Zero) return;
        try { _ = waveOutReset(_hWaveOut); } catch { }
        for (int i = 0; i < SlotCount; i++)
        {
            if (_inFlight[i])
            {
                try { _ = waveOutUnprepareHeader(_hWaveOut, _hdrPtrs[i], _hdrSize); } catch { }
                _inFlight[i] = false;
            }
        }
        try { _ = waveOutClose(_hWaveOut); } catch { }
        _hWaveOut = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _signal.Set();
        try { _worker.Join(500); } catch { }
        lock (_lifecycleGate)
        {
            CloseDeviceLocked();
            for (int i = 0; i < SlotCount; i++)
            {
                if (_hdrPtrs[i] != nint.Zero)
                {
                    Marshal.FreeHGlobal(_hdrPtrs[i]);
                    _hdrPtrs[i] = nint.Zero;
                }
                if (_dataPtrs[i] != nint.Zero)
                {
                    Marshal.FreeHGlobal(_dataPtrs[i]);
                    _dataPtrs[i] = nint.Zero;
                }
            }
        }
        _signal.Dispose();
    }
}

/// <summary>
/// Shared background-capable remote microphone audio router owned by <see cref="DisplaySettingsForm"/>
/// and shared with <see cref="PhoneWebcamForm"/> so tray Mic toggles stream directly to VB-Cable /
/// Windows Default Mic in the background without opening a UI window.
/// </summary>
internal sealed class RemoteMicBridgeController : IDisposable
{
    internal const uint WaveMapperId = 0xFFFFFFFF;

    private readonly ManagedAudioOutputEngine _primaryAudio = new();
    private readonly ManagedAudioOutputEngine _monitorAudio = new();
    private IReadOnlyList<WaveOutDeviceInfo> _waveOutDevices =
        Array.Empty<WaveOutDeviceInfo>();
    private VirtualMicMatch _virtualMic;
    private int _routingToVirtualCable;
    private int _hearMonitorActive;
    private bool _autoDefaultMicChecked;
    private uint _monitorDeviceId = WaveMapperId;
    private int _activeSourceId = int.MinValue;
    private int _activeViaHost;
    private int _isActive;
    private bool _backgroundMode;
    private string _activeSourceName = "";
    private string _micStatusNote = "";
    private bool _disposed;

    internal bool IsActive => Volatile.Read(ref _isActive) != 0;
    internal int ActiveSourceId => Volatile.Read(ref _activeSourceId);
    internal bool ActiveViaHost => Volatile.Read(ref _activeViaHost) != 0;
    internal string SourceName => _activeSourceName;
    internal bool IsBackgroundMode
    {
        get => _backgroundMode;
        set => _backgroundMode = value;
    }
    internal VirtualMicMatch VirtualMic => _virtualMic;
    internal IReadOnlyList<WaveOutDeviceInfo> WaveOutDevices => _waveOutDevices;
    internal bool IsRoutingToVirtualCable => Volatile.Read(ref _routingToVirtualCable) != 0;
    internal bool HearMonitorActive => Volatile.Read(ref _hearMonitorActive) != 0;
    internal bool AutoDefaultMicChecked => _autoDefaultMicChecked;
    internal float Gain
    {
        get => _primaryAudio.Gain;
        set
        {
            _primaryAudio.Gain = value;
            _monitorAudio.Gain = value;
        }
    }
    internal float CurrentLevel => _primaryAudio.CurrentLevel;
    internal string MicStatusNote
    {
        get => _micStatusNote;
        set => _micStatusNote = value ?? "";
    }

    internal bool IsActiveFor(int sourceId, bool viaHost) =>
        Volatile.Read(ref _isActive) != 0 &&
        Volatile.Read(ref _activeSourceId) == sourceId &&
        (Volatile.Read(ref _activeViaHost) != 0) == viaHost;

    internal VirtualMicMatch RefreshRouting(bool initialAutoSwitch)
    {
        _waveOutDevices = WindowsAudioDeviceBridge.ListWaveOutDevices();
        var captures = WindowsAudioDeviceBridge.ListCaptureEndpoints();
        _virtualMic = WindowsAudioDeviceBridge.MatchVirtualMicPair(_waveOutDevices, captures);

        if (_virtualMic.Installed)
        {
            Volatile.Write(ref _routingToVirtualCable, 1);
            _primaryAudio.SetOutputDevice(_virtualMic.RenderWaveOutId);
            _monitorAudio.SetOutputDevice(_monitorDeviceId);
            if (initialAutoSwitch)
            {
                _autoDefaultMicChecked = WindowsAudioDeviceBridge.TrySetDefaultCaptureDevice(_virtualMic.CaptureEndpointId);
            }
        }
        else
        {
            Volatile.Write(ref _routingToVirtualCable, 0);
            _primaryAudio.SetOutputDevice(_monitorDeviceId);
            _autoDefaultMicChecked = false;
            Volatile.Write(ref _hearMonitorActive, 1);
        }
        return _virtualMic;
    }

    internal void SetActiveSource(int sourceId, bool viaHost, string sourceName, bool backgroundMode, bool autoSwitchDefault = true)
    {
        if (_disposed) return;
        _primaryAudio.ClearQueue();
        _monitorAudio.ClearQueue();
        RefreshRouting(initialAutoSwitch: autoSwitchDefault);
        Volatile.Write(ref _activeSourceId, sourceId);
        Volatile.Write(ref _activeViaHost, viaHost ? 1 : 0);
        _activeSourceName = string.IsNullOrWhiteSpace(sourceName) ? "Remote Device" : sourceName;
        _backgroundMode = backgroundMode;
        _micStatusNote = "Mic Live";
        Volatile.Write(ref _isActive, 1);
    }

    internal void Stop(bool restoreDefaultMic = true)
    {
        Volatile.Write(ref _isActive, 0);
        _backgroundMode = false;
        _micStatusNote = "";
        _primaryAudio.ClearQueue();
        _monitorAudio.ClearQueue();
        if (restoreDefaultMic)
        {
            WindowsAudioDeviceBridge.RestorePreviousDefaultCaptureDeviceIfChanged();
            _autoDefaultMicChecked = false;
        }
    }

    internal void HandleIncomingAudio(int sourceId, bool viaHost, byte[] pcm, int sampleRate, int audioSeq = 0)
    {
        if (_disposed || Volatile.Read(ref _isActive) == 0 || pcm == null || pcm.Length < 2) return;
        if (Volatile.Read(ref _activeSourceId) != sourceId || (Volatile.Read(ref _activeViaHost) != 0) != viaHost)
            return;

        if (Volatile.Read(ref _routingToVirtualCable) != 0)
        {
            _primaryAudio.EnqueuePcm16Mono(pcm, sampleRate, audioSeq);
            if (Volatile.Read(ref _hearMonitorActive) != 0)
                _monitorAudio.EnqueuePcm16Mono(pcm, sampleRate, audioSeq);
        }
        else if (Volatile.Read(ref _hearMonitorActive) != 0)
        {
            _primaryAudio.EnqueuePcm16Mono(pcm, sampleRate, audioSeq);
        }
    }

    internal void SetHearMonitor(bool enabled)
    {
        Volatile.Write(ref _hearMonitorActive, enabled ? 1 : 0);
        if (!enabled)
        {
            if (Volatile.Read(ref _routingToVirtualCable) != 0)
                _monitorAudio.ClearQueue();
            else
                _primaryAudio.ClearQueue();
        }
    }

    internal void SetMonitorOutputDevice(uint waveOutId)
    {
        _monitorDeviceId = waveOutId;
        if (Volatile.Read(ref _routingToVirtualCable) != 0)
            _monitorAudio.SetOutputDevice(waveOutId);
        else
            _primaryAudio.SetOutputDevice(waveOutId);
    }

    internal bool SetDefaultWindowsMic(bool enable)
    {
        if (enable && _virtualMic.Installed)
        {
            _autoDefaultMicChecked = WindowsAudioDeviceBridge.TrySetDefaultCaptureDevice(_virtualMic.CaptureEndpointId);
            return _autoDefaultMicChecked;
        }
        WindowsAudioDeviceBridge.RestorePreviousDefaultCaptureDeviceIfChanged();
        _autoDefaultMicChecked = false;
        return false;
    }

    internal void ClearQueues()
    {
        _primaryAudio.ClearQueue();
        _monitorAudio.ClearQueue();
    }

    internal string DescribeActiveRoute() =>
        _virtualMic.Installed
            ? $"Native Mic: {_virtualMic.CaptureName}"
            : "PC Speaker Monitor";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop(restoreDefaultMic: true);
        _primaryAudio.Dispose();
        _monitorAudio.Dispose();
    }
}
