using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using BleHid.Core;

namespace DeskTouch.DeviceShare;

/// <summary>
/// Uses an authorized Android Debug Bridge shell and Android's kernel /dev/uhid,
/// the same native HID mechanism used by scrcpy. No Bluetooth hardware,
/// Accessibility pointer overlay, root, or second Android application.
/// Only one already-authorized ADB device may receive input.
///
/// The installed DeskTouch Android APK contains the shell-side Java bridge.
/// Launching its class from app_process does not open its Activity.
/// </summary>
internal sealed class UhidInputSession : INativeHidSession
{
    private readonly Channel<Packet> _packets = Channel.CreateUnbounded<Packet>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly HashSet<int> _keys = [];
    private readonly CancellationTokenSource _stop = new();
    private Process? _server;
    private Stream? _input;
    private Task? _pump;
    private Task? _reader;
    private Packet? _latestMove;
    private int _moveQueued, _lease, _mouseButtons;
    private int _priorX, _priorY;
    private volatile bool _ready, _started;
    private bool _positionKnown, _closed;
    private string _device = "";

    internal event Action<Packet>? Authority;
    internal event Action<string>? Status;

    public bool IsStarted => _started;
    public bool Ready {
        get {
            try { return _ready && _server is { HasExited:false }; }
            catch { return false; }
        }
    }
    public string CurrentState => Ready
        ? "Native Android UHID keyboard/mouse ready over Wi-Fi"
        : "Waiting for an authorized wireless debugging connection";
    public MonitorTopology RemoteMonitors { get; private set; } = new(
        [new MonitorRect("Android UHID", 0, 0, 1080, 2400, true)]);
    public void SetPhoneGeometry(MonitorTopology topology) {
        if (Volatile.Read(ref _lease) == 0) RemoteMonitors = topology;
    }

    internal static string? LocateAdb()
    {
        var names = new List<string>();
        foreach (var root in new [] {
            Environment.GetEnvironmentVariable("ANDROID_HOME"),
            Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Android", "Sdk")
        })
            if (!string.IsNullOrWhiteSpace(root))
                names.Add(Path.Combine(root, "platform-tools", "adb.exe"));
        names.Add(Path.Combine(AppContext.BaseDirectory, "platform-tools", "adb.exe"));
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            names.Add(Path.Combine(dir.Trim('"'), "adb.exe"));
        return names.FirstOrDefault(File.Exists);
    }

    private static ProcessStartInfo Command(string adb, params string[] args)
    {
        var info = new ProcessStartInfo {
            FileName=adb, UseShellExecute=false, CreateNoWindow=true,
            RedirectStandardInput=true, RedirectStandardOutput=true,
            RedirectStandardError=true
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return info;
    }

    private static async Task<string> RunAdbAsync(string adb, string? serial,
        CancellationToken token, params string[] arguments)
    {
        var args=new List<string>();
        if (serial!=null) { args.Add("-s"); args.Add(serial); }
        args.AddRange(arguments);
        using var process=Process.Start(Command(adb,args.ToArray()))
            ?? throw new IOException("Cannot launch Android platform-tools adb.");
        try {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var stdout=process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr=process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            string output=await stdout;
            string err=await stderr;
            if (process.ExitCode!=0)
                throw new IOException("ADB failed: "+
                    (err.Length>150?err[..150]:err.Trim()));
            return output;
        }
        catch {
            if (!process.HasExited) process.Kill(entireProcessTree:true);
            throw;
        }
    }

    internal static IReadOnlyList<string> AuthorizedDevices(string output)
    {
        // Wireless debugging must be paired using Android's developer settings.
        // Never silently switch the Windows input stream to a second phone.
        var devices=new List<string>();
        foreach (var line in output.Split('\n')) {
            var fields=line.Trim().Split((char[]?)null,
                StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length>=2 && fields[1]=="device" &&
                fields[0]!="List" &&
                Regex.IsMatch(fields[0], @"^[A-Za-z0-9_.:-]{1,120}$"))
                devices.Add(fields[0]);
        }
        return devices;
    }

    public async Task StartAsync()
    {
        if (_closed || _started) return;
        var adb=LocateAdb() ?? throw new FileNotFoundException(
            "Android platform-tools adb.exe not found. Install Android SDK Platform-Tools " +
            "or Android Studio; pair Wireless debugging once in Android Developer options.");
        string devices=await RunAdbAsync(adb,null,_stop.Token,"devices","-l");
        var authorized=AuthorizedDevices(devices);
        if (authorized.Count!=1)
            throw new InvalidOperationException(authorized.Count==0
                ? "No authorized Android ADB connection. Enable Wireless debugging, pair " +
                  "this Windows PC using 'adb pair', then connect using 'adb connect'."
                : "Multiple authorized Android devices. Disconnect others before native HID handoff.");
        _device=authorized[0];

        var installed=await RunAdbAsync(adb,_device,_stop.Token,
            "shell","pm","path","com.desktouch.vision");
        var apk=installed.Split('\n').Select(x=>x.Trim())
            .Where(x=>x.StartsWith("package:",StringComparison.Ordinal))
            .Select(x=>x["package:".Length..])
            .FirstOrDefault(x=>x.EndsWith("/base.apk",StringComparison.Ordinal));
        if (apk==null || !Regex.IsMatch(apk, @"^/[A-Za-z0-9_./~+=-]+$"))
            throw new IOException("DeskTouch Android APK not installed or path is unsafe. " +
                "Install the matching v0.3.23 APK first.");

        // Obtain actual screen dimensions for edge placement when the Android
        // activity is closed. Input reports themselves are relative HID deltas.
        try {
            var size=await RunAdbAsync(adb,_device,_stop.Token,
                "shell","wm","size");
            var match=Regex.Match(size, @"(?:Physical|Override) size:\s*(\d+)x(\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value,out int w) &&
                int.TryParse(match.Groups[2].Value,out int h) &&
                w>150 && h>150 && w<=32768 && h<=32768)
                SetPhoneGeometry(new MonitorTopology(
                    [new MonitorRect("Android UHID",0,0,w,h,true)]));
        }
        catch (Exception) { /* size is optional for relative mouse */ }

        var info=Command(adb,"-s",_device,"shell","-T",
            "CLASSPATH="+apk+" exec app_process / com.desktouch.vision.UhidShellBridge");
        var server=Process.Start(info) ??
            throw new IOException("Could not launch Android shell UHID bridge.");
        _server=server;
        _input=server.StandardInput.BaseStream;
        try {
            string? ready=await server.StandardOutput.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(12),_stop.Token);
            if (ready!="READY")
                throw new IOException("Android shell UHID did not initialize: "+
                    (ready??"bridge terminated"));
            _ready=true; _started=true;
            _pump=Task.Run(PumpAsync);
            _reader=Task.Run(async () => {
                try {
                    while (!_stop.IsCancellationRequested) {
                        var line=await server.StandardOutput.ReadLineAsync(_stop.Token);
                        if (line==null) break;
                        if (line=="ACTIVE" && Volatile.Read(ref _lease)>0)
                            Authority?.Invoke(new Packet("active",Volatile.Read(ref _lease)));
                    }
                }
                catch (Exception) { /* disconnected: release lease below */ }
                if (!_stop.IsCancellationRequested) Disconnect("Android UHID shell ended; restore Windows controls.");
            });
            Status?.Invoke("Android native UHID connected using authorized ADB over Wi-Fi.");
        }
        catch {
            _ready=false; _started=false;
            if (!server.HasExited) server.Kill(entireProcessTree:true);
            server.Dispose();
            _server=null;
            throw;
        }
    }

    public bool TrySend(Packet p)
    {
        if (_closed) return false;
        if (p.Type is "release" or "reset") {
            if (p.Type=="reset" || p.A==Volatile.Read(ref _lease))
                Interlocked.Exchange(ref _lease,0);
            Interlocked.Exchange(ref _latestMove,null);
            return _packets.Writer.TryWrite(new Packet("release"));
        }
        if (!Ready) return false;
        if (p.Type=="claim") {
            if (p.A<=0 || Interlocked.CompareExchange(ref _lease,p.A,0)!=0)
                return false;
            _positionKnown=false;
            Authority?.Invoke(new Packet("granted",p.A));
            return true;
        }
        if (Volatile.Read(ref _lease)==0) return false;
        if (p.Type=="move") {
            Interlocked.Exchange(ref _latestMove,p);
            if (Interlocked.CompareExchange(ref _moveQueued,1,0)==0)
                return _packets.Writer.TryWrite(new Packet("move-flush"));
            return true;
        }
        return _packets.Writer.TryWrite(p);
    }

    private async Task WriteAsync(byte operation, byte[] data)
    {
        var stream=_input ?? throw new IOException("UHID ADB stream closed");
        byte[] packet=new byte[data.Length+1];
        packet[0]=operation;
        Buffer.BlockCopy(data,0,packet,1,data.Length);
        await stream.WriteAsync(packet,_stop.Token);
        await stream.FlushAsync(_stop.Token);
    }

    private async Task MouseAsync(int dx,int dy,int wheel=0,int pan=0)
    {
        // Use standard physical HID relative mouse reports with signed bytes.
        do {
            int mx=Math.Clamp(dx,-127,127),my=Math.Clamp(dy,-127,127);
            int mw=Math.Clamp(wheel,-127,127),mp=Math.Clamp(pan,-127,127);
            await WriteAsync(2,[(byte)_mouseButtons,unchecked((byte)(sbyte)mx),
                unchecked((byte)(sbyte)my),unchecked((byte)(sbyte)mw),
                unchecked((byte)(sbyte)mp)]);
            dx-=mx;dy-=my;wheel-=mw;pan-=mp;
        } while (dx!=0 || dy!=0 || wheel!=0 || pan!=0);
    }

    private async Task MoveAsync(Packet p)
    {
        if (!_positionKnown) {
            _priorX=p.A;_priorY=p.B;_positionKnown=true;
            await MouseAsync(0,0); // remote shell acknowledges first real UHID write
            return;
        }
        int dx=p.A-_priorX,dy=p.B-_priorY;
        _priorX=p.A;_priorY=p.B;
        if (dx!=0 || dy!=0) await MouseAsync(dx,dy);
    }

    private async Task PumpAsync()
    {
        try {
            await foreach(var p in _packets.Reader.ReadAllAsync(_stop.Token)) {
                if (p.Type=="release") {
                    _keys.Clear(); _mouseButtons=0; _positionKnown=false;
                    Interlocked.Exchange(ref _moveQueued,0);
                    await WriteAsync(1,new byte[8]);
                    await MouseAsync(0,0);
                    continue;
                }
                if (p.Type=="move-flush") {
                    Interlocked.Exchange(ref _moveQueued,0);
                    var latest=Interlocked.Exchange(ref _latestMove,null);
                    if (latest!=null && Volatile.Read(ref _lease)>0)
                        await MoveAsync(latest);
                    continue;
                }
                if (!Ready || Volatile.Read(ref _lease)==0) continue;
                var before=Interlocked.Exchange(ref _latestMove,null);
                if (before!=null) await MoveAsync(before);
                if (p.Type=="button") {
                    int flag=p.A switch {1=>1,2=>2,3=>4,_=>0};
                    if(p.B!=0) _mouseButtons|=flag;
                    else _mouseButtons&=~flag;
                    await MouseAsync(0,0);
                }
                else if(p.Type=="wheel") {
                    if(p.B==0) await MouseAsync(0,0,Math.Clamp(p.A/120,-127,127));
                    else await MouseAsync(0,0,0,Math.Clamp(p.A/120,-127,127));
                }
                else if(p.Type=="key") {
                    if(p.C!=0) _keys.Add(p.D); else _keys.Remove(p.D);
                    byte[] report=HidReports.Keyboard(NativeHidKeyMap.Modifiers(_keys),
                        _keys.Where(k=>!NativeHidKeyMap.IsModifier(k))
                            .Select(NativeHidKeyMap.Usage)
                            .Where(k=>k!=0).Take(6).ToArray());
                    await WriteAsync(1,report);
                }
            }
        }
        catch(Exception ex) when (!_stop.IsCancellationRequested) {
            Disconnect("Android native UHID input disconnected: "+ex.GetType().Name);
        }
    }

    private void Disconnect(string reason)
    {
        _ready=false;_started=false;
        if (Interlocked.Exchange(ref _lease,0)>0)
            Authority?.Invoke(new Packet("local-active"));
        Status?.Invoke(reason);
    }

    public async ValueTask DisposeAsync()
    {
        if (_closed) return;
        _closed=true; _ready=false; _started=false;
        Interlocked.Exchange(ref _lease,0);
        _packets.Writer.TryWrite(new Packet("release"));
        _packets.Writer.TryComplete();
        try { if (_pump!=null) await _pump.WaitAsync(TimeSpan.FromSeconds(1)); }
        catch { /* close adb to unblock */ }
        _stop.Cancel();
        try { if (_server is {HasExited:false}) _server.Kill(entireProcessTree:true); }
        catch { }
        try { if (_reader!=null) await _reader.WaitAsync(TimeSpan.FromSeconds(1)); }
        catch { }
        _server?.Dispose(); _server=null;
        _stop.Dispose();
    }
}
