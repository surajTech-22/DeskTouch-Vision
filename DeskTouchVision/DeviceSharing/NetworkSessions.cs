using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using System.Text.Json;

namespace DeskTouch.DeviceShare;

internal sealed record ConnectedPeer(string Name, MonitorTopology Monitors,
    bool SupportsReverseInput = true, string? DeviceFingerprint = null,
    string Platform = "Windows", bool SupportsForwardInput = false,
    string? OsVersion = null, string? AppVersion = null,
    int? DensityDpi = null, int? RefreshHz = null, int? RotationDegrees = null)
{
    internal string FormatScreenInfo()
    {
        var baseSummary = Monitors.DescribeSummary(Platform);
        var extras = new List<string>();
        if (!baseSummary.Contains("dpi", StringComparison.OrdinalIgnoreCase) && DensityDpi is > 0)
            extras.Add($"{DensityDpi} dpi");
        if (!baseSummary.Contains("Hz", StringComparison.OrdinalIgnoreCase) && RefreshHz is > 0)
            extras.Add($"{RefreshHz} Hz");
        if (!string.IsNullOrWhiteSpace(OsVersion) &&
            !baseSummary.Contains(OsVersion, StringComparison.OrdinalIgnoreCase))
            extras.Add(OsVersion);
        if (!string.IsNullOrWhiteSpace(AppVersion) &&
            !baseSummary.Contains("v" + AppVersion, StringComparison.OrdinalIgnoreCase))
            extras.Add($"v{AppVersion}");
        return extras.Count == 0 ? baseSummary : baseSummary + " · " + string.Join(" · ", extras);
    }
}
internal sealed record PeerHello(string Name, string Layout,
    bool? ReverseInputSupported = null, string? Fingerprint = null,
    string? Platform = null, bool? ForwardInputSupported = null,
    string? OsVersion = null, string? AppVersion = null,
    int? DensityDpi = null, int? RefreshHz = null, int? RotationDegrees = null);

// Issued exclusively by the authenticated hub over an existing TLS session.
// Id is a connection-scoped route identifier, not a device identity or trust proof.
internal sealed record EnvironmentMember(int Id, string Name, string Platform,
    string Layout, bool SupportsReverseInput, string? StableIdentity=null);
internal sealed record EnvironmentPosition(string Key,float X,float Y)
{
    internal static bool Valid(EnvironmentPosition? p) =>
        p!=null && !string.IsNullOrWhiteSpace(p.Key) && p.Key.Length<=150 &&
        !p.Key.Any(char.IsControl) && float.IsFinite(p.X) && float.IsFinite(p.Y) &&
        Math.Abs(p.X)<=80000 && Math.Abs(p.Y)<=80000;
}

/// <summary>
/// A small authenticated LAN hub. Up to eight Windows PCs can remain connected
/// through the same existing TLS protocol. Only one peer holds the input lease.
/// The currently selected peer is the target for Windows-to-peer reverse input;
/// switching to another peer is explicit when that peer requests authority.
/// </summary>
internal sealed class ReceiverServer : IInputSession, IDisposable
{
    private const int MaxPeers = 8;
    // Allow Wi-Fi roaming and transient Windows scheduling pauses without
    // retaining a permanently abandoned connection. Input leases revoke on disconnect.
    internal const int PeerIdleGraceSeconds = 50;
    private sealed class Session
    {
        internal required int Id;
        internal required TcpClient Client;
        internal required SslStream Stream;
        internal required ConnectedPeer Peer;
        internal readonly SemaphoreSlim Write = new(1, 1);
        internal readonly InputInjector Injector = new();
        internal readonly object InputLock = new();
        internal readonly Channel<Packet> ReverseQueue = Channel.CreateBounded<Packet>(
            new BoundedChannelOptions(2048) {
                SingleReader = true, SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait });
        internal Packet? LatestReverseMove;
        internal int ReverseMoveSignaled;
        internal FastUdpHub.Peer? UdpPeer;
        internal FastMovePacer? MovePacer;
        internal long TcpFallbackMoves;
        internal readonly IDisposable HighResTimer = Win.EnterHighResolutionTimer();
        internal int ActiveReverseLease;
        internal int Lease;
        internal int AppliedLease;
        internal int KeyboardFailureReported;
        internal int EnvironmentTargetId; // 0 is the local Windows hub
    }

    private sealed class RelayLease
    {
        internal required Session Source;
        internal required Session Target;
        internal required int Claim;
        internal bool Granted;
        // Routed pointer packets are coalesced while key and button events
        // preserve their order. Never block the hub's TCP receive loop on
        // every intermediate remote cursor coordinate.
        internal readonly Channel<Packet> InputQueue=Channel.CreateBounded<Packet>(
            new BoundedChannelOptions(512) {
                SingleReader=true,SingleWriter=false,
                FullMode=BoundedChannelFullMode.Wait });
        internal Packet? LatestMove;
        internal int MoveSignaled;
    }

    private readonly Action<string> _status;
    private readonly CancellationTokenSource _cancel = new();
    private readonly TcpListener _listener = new(IPAddress.Any, Wire.Port);
    private readonly X509Certificate2 _certificate;
    private readonly TrustedDeviceStore? _trusted;
    private readonly LocalInputPriority _priority;
    private readonly Func<string,CancellationToken,Task<bool>>? _requestApproval;
    private readonly Func<string,CancellationToken,Task<bool>>? _requestTrustApproval;
    private readonly ConcurrentDictionary<int,TcpClient> _clients = new();
    private readonly ConcurrentDictionary<int,Session> _sessions = new();
    private readonly object _authorityGate = new();
    private readonly object _layoutGate=new();
    private readonly Dictionary<string,EnvironmentPosition> _environmentPositions=
        new(StringComparer.Ordinal);
    internal event Action<IReadOnlyDictionary<string,System.Drawing.PointF>>?
        EnvironmentLayoutChanged;
    // A single environment can have many authenticated peers but only one
    // controller per destination. Every routed input is pinned to BOTH TLS
    // sessions, not a name, IP address or broadcast group hint.
    private readonly Dictionary<int, RelayLease> _relayBySource = new();
    private readonly Dictionary<int, RelayLease> _relayByTarget = new();
    // The hub itself is also a controller. Its selected reverse-input claim
    // must arbitrate against routed peers targeting the same device.
    private readonly Dictionary<int,int> _hubClaims = new();
    private FastUdpHub? _udpHub;
    private Task? _serverTask;
    private int _nextClientId;
    private int _pendingAndActive;
    private long _lastWarmBroadcastTicks;
    private Session? _selected;
    private Session? _owner;
    private bool _disposed;

    internal Func<bool>? AllowForwardClaim;
    internal event Action<Packet>? ReverseControlMessage;
    internal event Action<string>? DiagnosticLog;
    internal event Action<ConnectedPeer>? PeerConnected;
    internal event Action? PeerListChanged;
    internal event Action? PeerDisconnected;
    internal event Action<ConnectedPeer>? PeerTopologyChanged;
    internal event Action<MonitorEdge>? PeerPlacementChanged;
    internal string Fingerprint { get; }
    internal string Secret { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    internal int ConnectedPeerCount => _sessions.Count;
    internal static bool AllowsForwardInput(ConnectedPeer peer) =>
        peer.Platform != "Android" || peer.SupportsForwardInput;
    internal IReadOnlyList<(int Id, ConnectedPeer Peer)> ConnectedPeers =>
        _sessions.Values.OrderBy(s => s.Id).Select(s => (s.Id, s.Peer)).ToArray();
    internal int SelectedPeerId => Volatile.Read(ref _selected)?.Id ?? 0;

    internal static string LayoutIdentity(ConnectedPeer peer) =>
        string.IsNullOrWhiteSpace(peer.DeviceFingerprint) ?
            peer.Platform+"|"+peer.Name : peer.DeviceFingerprint;
    internal static bool OwnsPlacement(ConnectedPeer peer,string key)=>
        key==LayoutIdentity(peer) || key=="hub@"+LayoutIdentity(peer);

    internal void InitializeEnvironmentLayout(
        IReadOnlyDictionary<string,System.Drawing.PointF> saved)
    {
        lock(_layoutGate)
        {
            _environmentPositions.Clear();
            foreach(var pair in saved)
            {
                var position=new EnvironmentPosition(pair.Key,pair.Value.X,pair.Value.Y);
                if(EnvironmentPosition.Valid(position))
                    _environmentPositions[position.Key]=position;
            }
        }
    }
    internal IReadOnlyDictionary<string,System.Drawing.PointF> EnvironmentPositions
    {
        get {lock(_layoutGate) return _environmentPositions.Values.ToDictionary(
            p=>p.Key,p=>new System.Drawing.PointF(p.X,p.Y),StringComparer.Ordinal);}
    }
    private void ApplyEnvironmentPosition(EnvironmentPosition placement)
    {
        lock(_layoutGate)
        {
            // Replace a legacy local-offset value when a single hub-relative
            // position arrives. Two competing conventions must not survive.
            if(placement.Key.StartsWith("hub@",StringComparison.Ordinal))
                _environmentPositions.Remove(placement.Key["hub@".Length..]);
            _environmentPositions[placement.Key]=placement;
        }
        EnvironmentLayoutChanged?.Invoke(EnvironmentPositions);
        _=Task.Run(async()=>{
            try{await PublishEnvironmentLayoutAsync(_cancel.Token);}
            catch(OperationCanceledException){}
            catch(Exception ex){_status("Environment placement synchronization failed: "+ex.Message);}
        });
    }
    internal bool UpdateEnvironmentPlacement(string key,System.Drawing.PointF point)
    {
        var placement=new EnvironmentPosition(key,point.X,point.Y);
        if(!EnvironmentPosition.Valid(placement))return false;
        // Only a current, authenticated member can acquire a drag position.
        if(!_sessions.Values.Any(x=>OwnsPlacement(x.Peer,key)))return false;
        ApplyEnvironmentPosition(placement);
        return true;
    }
    private async Task PublishEnvironmentLayoutAsync(CancellationToken token)
    {
        EnvironmentPosition[] snapshot;
        lock(_layoutGate)snapshot=_environmentPositions.Values.ToArray();
        string payload=JsonSerializer.Serialize(snapshot);
        if(System.Text.Encoding.UTF8.GetByteCount(payload)>4096)return;
        foreach(var recipient in _sessions.Values)
            try{await SendToAsync(recipient,
                new Packet("environment-layout",Text:payload),token);}
            catch(Exception) { /* peer may disconnect while receiving layout */ }
    }


    internal static bool AllowsAndroidCapability(ConnectedPeer source, Packet packet) =>
        source.Platform=="Android" && packet.Type=="android-capability" &&
        packet.A is 0 or 1;

    internal static bool CanRoute(ConnectedPeer source, ConnectedPeer target,
        bool differentPeers, int claim) =>
        claim > 0 && differentPeers && AllowsForwardInput(source) &&
        target.SupportsReverseInput;
    internal bool SelectConnectedPeer(int id)
    {
        if (!_sessions.TryGetValue(id, out var session)) return false;
        // Selection is ONLY routing over the already authenticated TLS stream.
        // It cannot grant authority and it never initiates another connection.
        return Select(session);
    }
    // Only labels for LAN display grouping; individual peer trust remains
    // pinned to its own approved TLS certificate/fingerprint.
    internal IReadOnlyList<string> ConnectedDeviceFingerprints =>
        _sessions.Values.Select(s=>s.Peer.DeviceFingerprint)
            .Where(id=>!string.IsNullOrWhiteSpace(id)).Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    internal ReceiverServer(Action<string> status, LocalInputPriority priority,
        Func<string,CancellationToken,Task<bool>>? requestApproval = null,
        TrustedDeviceStore? trusted = null,
        Func<string,CancellationToken,Task<bool>>? requestTrustApproval = null,
        string? identityPath = null)
    {
        _status = status; _priority = priority;
        _requestApproval = requestApproval; _trusted = trusted;
        _requestTrustApproval = requestTrustApproval;
        _priority.PhysicalOverride += OnPhysicalOverride;
        _certificate = PersistentReceiverIdentity.LoadOrCreate(identityPath);
        Fingerprint = Convert.ToHexString(SHA256.HashData(_certificate.RawData));
    }

    internal bool CanAcceptNewPeer
    {
        get
        {
            if (_cancel.IsCancellationRequested ||
                Volatile.Read(ref _pendingAndActive) >= MaxPeers) return false;
            try { return _listener.Server.IsBound; }
            catch (ObjectDisposedException) { return false; }
        }
    }

    internal void Start()
    {
        if (_udpHub == null)
        {
            try
            {
                _udpHub = new FastUdpHub();
                _udpHub.MessageReceived += OnUdpHubMessage;
            }
            catch (Exception)
            {
                _udpHub?.Dispose();
                _udpHub = null;
            }
        }
        _listener.Start(MaxPeers);
        _serverTask = Task.Run(LoopAsync);
        _ = Task.Run(() => ReportTelemetryLoopAsync(_cancel.Token));
    }

    private void BroadcastWarmToConfirmedTargets(int excludingSessionId)
    {
        var hub = _udpHub;
        if (hub == null) return;
        long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        long prevTicks = Interlocked.Read(ref _lastWarmBroadcastTicks);
        double elapsedMs = prevTicks == 0 ? double.MaxValue
            : (nowTicks - prevTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (elapsedMs < 60) return;
        if (Interlocked.CompareExchange(ref _lastWarmBroadcastTicks, nowTicks, prevTicks) != prevTicks)
            return;
        foreach (var peer in _sessions.Values)
        {
            if (peer.Id == excludingSessionId) continue;
            if (peer.UdpPeer is { Confirmed: true } udpPeer)
                hub.Send(udpPeer, FastUdpOp.Warm, 0, 0, 0);
        }
    }

    private async Task ReportTelemetryLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(3000, token);
                foreach (var session in _sessions.Values)
                {
                    var pacer = session.MovePacer;
                    if (pacer == null) continue;
                    var s = pacer.TakeStats();
                    long fallback = Interlocked.Exchange(ref session.TcpFallbackMoves, 0);
                    if (s.UdpDirect == 0 && s.UdpPacedFlush == 0 && fallback == 0)
                        continue;
                    DiagnosticLog?.Invoke(
                        $"Telemetry [hub->{session.Peer.Name}]: app={session.Peer.AppVersion ?? "?"} fast_confirmed={session.UdpPeer?.Confirmed == true} fast_direct={s.UdpDirect} fast_paced={s.UdpPacedFlush} trailing_sync={s.TrailingTcpSync} tcp_fallback={fallback}");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task LoopAsync()
    {
        while (!_cancel.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(_cancel.Token);
                if (Interlocked.Increment(ref _pendingAndActive) > MaxPeers)
                {
                    Interlocked.Decrement(ref _pendingAndActive);
                    client.Dispose();
                    continue;
                }
                var id = Interlocked.Increment(ref _nextClientId);
                _clients[id] = client;
                client.NoDelay = true;
                try { client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService, 0x10); }
                catch (Exception) { }
                client.Client.SetSocketOption(SocketOptionLevel.Socket,
                    SocketOptionName.KeepAlive,true);
                // Do not block accept on another client's TLS, pairing prompt,
                // heartbeat, or long-lived authenticated input session.
                _ = Task.Run(async () =>
                {
                    try { await ServeAsync(id, client, _cancel.Token); }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { _status("Peer session ended: " + ex.Message); }
                    finally
                    {
                        _clients.TryRemove(id, out _);
                        client.Dispose();
                        Interlocked.Decrement(ref _pendingAndActive);
                    }
                });
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (Exception ex) { _status("LAN accept error: " + ex.Message); }
        }
    }

    private static bool ConstantTimeSecret(string? got, string expected)
    {
        if (got == null || got.Length != expected.Length) return false;
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(got),
            System.Text.Encoding.ASCII.GetBytes(expected));
    }

    private static async Task SendToAsync(Session session, Packet packet,
        CancellationToken token)
    {
        await session.Write.WaitAsync(token);
        try { await Wire.WriteAsync(session.Stream, packet, token); }
        finally { session.Write.Release(); }
    }

    private async Task RelayForwardLoopAsync(RelayLease relay,CancellationToken token)
    {
        try
        {
            await foreach(var packet in relay.InputQueue.Reader.ReadAllAsync(token))
            {
                lock(_authorityGate)
                    if(!_relayBySource.TryGetValue(relay.Source.Id,out var current) ||
                        !ReferenceEquals(current,relay))break;
                if(packet.Type=="move-flush")
                {
                    Interlocked.Exchange(ref relay.MoveSignaled,0);
                    var latest=Interlocked.Exchange(ref relay.LatestMove,null);
                    if(latest!=null)
                        await SendToAsync(relay.Target,
                            ReverseInputProtocol.Encode(latest),token);
                    continue;
                }
                bool isImmediateControl = packet.Type is "button" or "wheel";
                var udpSync = packet.Type is ("release" or "reset")
                    ? null
                    : relay.Target.MovePacer?.TakePendingReliableSync(
                        skipIfUdpDelivered: isImmediateControl);
                var tcpPreceding = Interlocked.Exchange(ref relay.LatestMove,null);
                var preceding = udpSync ?? (isImmediateControl &&
                    relay.Target.UdpPeer is { Confirmed: true } ? null : tcpPreceding);
                if(preceding!=null)
                    await SendToAsync(relay.Target,
                        ReverseInputProtocol.Encode(preceding),token);
                await SendToAsync(relay.Target,
                    ReverseInputProtocol.Encode(packet),token);
            }
        }
        catch(OperationCanceledException){}
        catch(Exception ex)
        {
            _status("Routed input stream ended: "+ex.Message);
            var removed=RemoveRelayFromSource(relay.Source.Id);
            if(removed!=null)
                await RevokeRelayAsync(removed,true,token);
        }
    }

    private static bool QueueTcpRelayMove(RelayLease relay, Packet packet,
        bool countAsFallback = true)
    {
        if (countAsFallback)
            Interlocked.Increment(ref relay.Target.TcpFallbackMoves);
        Interlocked.Exchange(ref relay.LatestMove,packet);
        if(Interlocked.CompareExchange(ref relay.MoveSignaled,1,0)==0 &&
            !relay.InputQueue.Writer.TryWrite(new Packet("move-flush")))
        {
            Interlocked.Exchange(ref relay.MoveSignaled,0);
            return false;
        }
        return true;
    }

    private static bool QueueRoutedInput(RelayLease relay,Packet packet)
    {
        if(packet.Type=="move")
        {
            if(relay.Target.MovePacer?.TryHandleMove(packet) == true)
            {
                Interlocked.Exchange(ref relay.LatestMove, null);
                return true;
            }
            return QueueTcpRelayMove(relay, packet, countAsFallback: true);
        }
        return relay.InputQueue.Writer.TryWrite(packet);
    }

    private RelayLease? RemoveRelayFromSource(int sourceId)
    {
        lock (_authorityGate)
        {
            if (!_relayBySource.Remove(sourceId, out var relay)) return null;
            _relayByTarget.Remove(relay.Target.Id);
            Volatile.Write(ref relay.Target.ActiveReverseLease, 0);
            relay.Target.MovePacer?.SetWarmActive(false);
            relay.Target.MovePacer?.ClearPending();
            Interlocked.Exchange(ref relay.LatestMove,null);
            relay.InputQueue.Writer.TryComplete();
            return relay;
        }
    }

    private async Task RevokeRelayAsync(RelayLease relay, bool notifySource,
        CancellationToken token)
    {
        relay.Target.MovePacer?.SetMinIntervalMs(relay.Target.Peer.Platform == "Android" ? AndroidUdpMinIntervalMs : 4.5);
        if (_sessions.ContainsKey(relay.Target.Id))
        {
            try { await SendToAsync(relay.Target,
                new Packet("reverse-reset", relay.Claim), token); }
            catch (Exception) { }
        }
        if (notifySource && _sessions.ContainsKey(relay.Source.Id))
        {
            try { await SendToAsync(relay.Source,
                new Packet("local-active", relay.Claim), token); }
            catch (Exception) { }
        }
    }

    private async Task PublishEnvironmentAsync(CancellationToken token)
    {
        if (token.IsCancellationRequested) return;
        // Roster is an authenticated statement from this hub. Individual
        // session IDs are ephemeral and may never authorize a new pairing.
        var members = new List<EnvironmentMember> {
            new(0, Environment.MachineName, "Windows",
                MonitorTopology.Capture().ToWire(), true,Fingerprint)
        };
        foreach (var peer in _sessions.Values.OrderBy(p => p.Id))
            members.Add(new(peer.Id, peer.Peer.Name, peer.Peer.Platform,
                peer.Peer.Monitors.ToWire(), peer.Peer.SupportsReverseInput,
                LayoutIdentity(peer.Peer)));
        // Migrate names written by v0.3.16's local-only UI into stable
        // authenticated certificate identities whenever one is available.
        lock(_layoutGate)
        {
            foreach(var peer in _sessions.Values)
            {
                string legacy=peer.Peer.Platform+"|"+peer.Peer.Name;
                string stable=LayoutIdentity(peer.Peer);
                if(legacy==stable ||
                    !_environmentPositions.TryGetValue(legacy,out var old))continue;
                if(!_environmentPositions.ContainsKey(stable))
                    _environmentPositions[stable]=old with {Key=stable};
                _environmentPositions.Remove(legacy);
            }
        }
        string payload = JsonSerializer.Serialize(members);
        if (System.Text.Encoding.UTF8.GetByteCount(payload) > 7800)
        {
            _status("Environment roster too large; refusing to advertise incomplete routes.");
            return;
        }
        foreach (var recipient in _sessions.Values)
            try { await SendToAsync(recipient,
                new Packet("environment-roster", recipient.Id, Text: payload), token); }
            catch (Exception) { /* peer may have disconnected */ }
        await PublishEnvironmentLayoutAsync(token);
    }

    private void NotifySelection(Session? previous, Session? next)
    {
        if (ReferenceEquals(previous, next)) return;
        if (previous != null) PeerDisconnected?.Invoke();
        if (next != null) PeerConnected?.Invoke(next.Peer);
    }

    private bool Select(Session candidate)
    {
        Session? previous;
        lock (_authorityGate)
        {
            if (!_sessions.ContainsKey(candidate.Id)) return false;
            previous = _selected;
            if (ReferenceEquals(previous, candidate)) return true;
            _selected = candidate;
        }
        // A target change must revoke the previous reverse input lease on
        // the PREVIOUS authenticated TLS stream, not on the newly selected PC
        // or phone. This prevents keyboard ownership lingering after switching.
        if (previous != null) TrySendReverseTo(previous.Id, new Packet("reset"));
        NotifySelection(previous, candidate);
        return true;
    }

    private void OnPhysicalOverride()
    {
        Session? owner;
        int revoked;
        lock (_authorityGate)
        {
            owner = _owner;
            _owner = null;
            revoked = owner == null ? 0 : Interlocked.Exchange(ref owner.Lease, 0);
            if (owner != null) Interlocked.Exchange(ref owner.AppliedLease, 0);
        }
        if (owner == null) return;
        _ = Task.Run(() =>
        {
            lock (owner.InputLock) owner.Injector.ReleaseAll();
        });
        _ = Task.Run(async () =>
        {
            try { await SendToAsync(owner, new Packet("local-active", revoked), _cancel.Token); }
            catch (Exception) { }
        });
        _status("Physical keyboard/mouse reclaimed control from remote device.");
    }

    internal void DisconnectPeer() => Volatile.Read(ref _selected)?.Client.Close();
    internal MonitorTopology RemoteMonitors =>
        Volatile.Read(ref _selected)?.Peer.Monitors ??
        throw new InvalidOperationException("No authenticated peer layout.");
    MonitorTopology IInputSession.RemoteMonitors => RemoteMonitors;
    bool IInputSession.TrySend(Packet packet) => TrySendReverse(packet);

    private sealed class BoundPeerInputSession : IInputSession
    {
        private readonly ReceiverServer _server;
        private readonly int _peerId;
        internal BoundPeerInputSession(ReceiverServer server, int peerId)
        {
            _server = server; _peerId = peerId;
        }
        public MonitorTopology RemoteMonitors =>
            _server._sessions.TryGetValue(_peerId, out var peer)
                ? peer.Peer.Monitors
                : throw new InvalidOperationException("Selected input peer disconnected.");
        public bool TrySend(Packet packet) =>
            _server.TrySendReverseTo(_peerId, packet);
    }

    internal IInputSession BindSelectedInput()
    {
        Session selected = Volatile.Read(ref _selected) ??
            throw new InvalidOperationException("No authenticated input target.");
        return new BoundPeerInputSession(this, selected.Id);
    }

    internal bool TrySendReverse(Packet packet)
    {
        Session? selected = Volatile.Read(ref _selected);
        return selected != null && TrySendReverseTo(selected.Id, packet);
    }

    private static bool QueueTcpReverseMove(Session selected, Packet packet,
        bool countAsFallback = true)
    {
        if (countAsFallback)
            Interlocked.Increment(ref selected.TcpFallbackMoves);
        Interlocked.Exchange(ref selected.LatestReverseMove, packet);
        if (Interlocked.CompareExchange(ref selected.ReverseMoveSignaled, 1, 0) == 0 &&
            !selected.ReverseQueue.Writer.TryWrite(new Packet("move-flush")))
        {
            Interlocked.Exchange(ref selected.ReverseMoveSignaled, 0);
            return false;
        }
        return true;
    }

    private void QueueReliableReverseMoveForSession(Session session, Packet move,
        bool countAsFallback = true)
    {
        RelayLease? relay = null;
        bool hubOwned = false;
        lock (_authorityGate)
        {
            if (!_sessions.ContainsKey(session.Id)) return;
            if (_hubClaims.ContainsKey(session.Id))
                hubOwned = true;
            else if (_relayByTarget.TryGetValue(session.Id, out relay) && relay.Granted)
            {
            }
            else return;
        }
        if (hubOwned)
            QueueTcpReverseMove(session, move, countAsFallback);
        else if (relay != null)
            QueueTcpRelayMove(relay, move, countAsFallback);
    }

    private bool TrySendReverseTo(int id, Packet packet)
    {
        if (!_sessions.TryGetValue(id, out var selected)) return false;
        if (packet.Type == "warm")
        {
            if (_udpHub != null && selected.UdpPeer is { HasEndpoint: true } peer)
                _udpHub.Send(peer, FastUdpOp.Warm, 0, 0, 0);
            return true;
        }
        lock (_authorityGate)
        {
            if (packet.Type=="claim")
            {
                if (packet.A<=0 || _relayByTarget.ContainsKey(id) ||
                    _hubClaims.ContainsKey(id))
                    return false;
                _hubClaims[id]=packet.A;
                Volatile.Write(ref selected.ActiveReverseLease, packet.A);
                selected.MovePacer?.SetWarmActive(true);
                if (_udpHub != null && selected.UdpPeer is { HasEndpoint: true } udpPeer)
                    _udpHub.Send(udpPeer, udpPeer.Confirmed ? FastUdpOp.Warm : FastUdpOp.Probe,
                        udpPeer.Confirmed ? 0 : _udpHub.Port, 0, 0);
            }
            else if(packet.Type is "reset" or "release")
            {
                if(_hubClaims.TryGetValue(id,out int claim) &&
                    (packet.Type=="reset" || claim==packet.A))
                {
                    _hubClaims.Remove(id);
                    Volatile.Write(ref selected.ActiveReverseLease, 0);
                    selected.MovePacer?.SetWarmActive(false);
                    selected.MovePacer?.ClearPending();
                }
                else
                    // An already-revoked RELEASE is an idempotent success,
                    // but must NEVER send a reset to another routed owner.
                    return packet.Type=="release";
            }
            else if (!_hubClaims.ContainsKey(id))
                return false;
        }
        if (packet.Type == "move")
        {
            if (selected.MovePacer?.TryHandleMove(packet) == true)
            {
                Interlocked.Exchange(ref selected.LatestReverseMove, null);
                return true;
            }
            return QueueTcpReverseMove(selected, packet, countAsFallback: true);
        }
        if (selected.ReverseQueue.Writer.TryWrite(packet)) return true;
        _status("Peer input queue stalled. Disconnecting that peer safely.");
        selected.Client.Close();
        return false;
    }

    private async Task ReverseSendLoopAsync(Session session, CancellationToken token)
    {
        try
        {
            await foreach (var p in session.ReverseQueue.Reader.ReadAllAsync(token))
            {
                // Old target movements/buttons must never leak after a
                // selection change. Its pending RELEASE/RESET must still be
                // delivered on the ORIGINAL authenticated TLS stream.
                bool stillSelected=ReferenceEquals(Volatile.Read(ref _selected),session);
                if(!stillSelected)
                {
                    session.MovePacer?.ClearPending();
                    Interlocked.Exchange(ref session.LatestReverseMove,null);
                    Interlocked.Exchange(ref session.ReverseMoveSignaled,0);
                    if(p.Type is "release" or "reset")
                        await SendToAsync(session,
                            ReverseInputProtocol.Encode(p),token);
                    continue;
                }
                if (p.Type == "move-flush")
                {
                    Interlocked.Exchange(ref session.ReverseMoveSignaled, 0);
                    var move = Interlocked.Exchange(ref session.LatestReverseMove, null);
                    if (move != null)
                        await SendToAsync(session, ReverseInputProtocol.Encode(move), token);
                    continue;
                }
                if (p.Type is "release" or "reset")
                {
                    session.MovePacer?.ClearPending();
                    var staleMove = Interlocked.Exchange(ref session.LatestReverseMove, null);
                    if (staleMove != null)
                        await SendToAsync(session, ReverseInputProtocol.Encode(staleMove), token);
                    await SendToAsync(session, ReverseInputProtocol.Encode(p), token);
                    continue;
                }
                bool isImmediateControl = p.Type is "button" or "wheel";
                var udpSync = session.MovePacer?.TakePendingReliableSync(
                    skipIfUdpDelivered: isImmediateControl);
                var tcpPrior = Interlocked.Exchange(ref session.LatestReverseMove, null);
                var prior = udpSync ?? (isImmediateControl && session.UdpPeer is { Confirmed: true }
                    ? null : tcpPrior);
                if (prior != null)
                    await SendToAsync(session, ReverseInputProtocol.Encode(prior), token);
                await SendToAsync(session, ReverseInputProtocol.Encode(p), token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _status("Reverse control stream ended: " + ex.Message);
            session.Client.Close();
        }
    }

    internal void PublishPlacement(MonitorEdge side)
    {
        Session? peer = Volatile.Read(ref _selected);
        if (peer == null) return;
        _ = Task.Run(async () => {
            try { await SendToAsync(peer,new Packet("placement", Text: side.ToString()),_cancel.Token); }
            catch (Exception) { }
        });
    }

    internal void PublishLocalTopology(MonitorTopology monitors)
    {
        foreach (var peer in _sessions.Values)
        {
            _ = Task.Run(async () => {
                try { await SendToAsync(peer,
                    new Packet("topology", Text: monitors.ToWire()),_cancel.Token); }
                catch (Exception) { }
            });
        }
    }

    internal static ConnectedPeer ReadHello(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 6000)
            throw new InvalidDataException("Missing peer monitor layout.");
        var hello = JsonSerializer.Deserialize<PeerHello>(value)
            ?? throw new InvalidDataException("Unrecognized peer hello.");
        string name = hello.Name.Trim();
        if (name.Length is < 1 or > 60 || name.Any(char.IsControl))
            throw new InvalidDataException("Invalid peer name.");
        // A claimed fingerprint is a network-display hint only; NEVER use
        // it to authenticate the source or grant remote keyboard authority.
        string? hint = hello.Fingerprint is { Length:64 } id &&
            id.All(Uri.IsHexDigit) ? id.ToUpperInvariant() : null;
        string platform = string.Equals(hello.Platform, "Android",
            StringComparison.OrdinalIgnoreCase) ? "Android" : "Windows";
        var monitors = MonitorTopology.FromWire(hello.Layout);
        var primaryMon = monitors.Monitors.FirstOrDefault(m => m.Primary) ?? monitors.Monitors[0];
        return new ConnectedPeer(name, monitors,
            hello.ReverseInputSupported ?? true, hint, platform,
            platform != "Android" || hello.ForwardInputSupported == true,
            !string.IsNullOrWhiteSpace(hello.OsVersion) ? hello.OsVersion : primaryMon.OsVersion,
            !string.IsNullOrWhiteSpace(hello.AppVersion) ? hello.AppVersion : primaryMon.AppVersion,
            hello.DensityDpi ?? primaryMon.DensityDpi,
            hello.RefreshHz ?? primaryMon.RefreshHz,
            hello.RotationDegrees ?? primaryMon.RotationDegrees);
    }

    private async Task ServeAsync(int id, TcpClient client, CancellationToken token)
    {
        using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen:false);
        using var handshakeLimit = CancellationTokenSource.CreateLinkedTokenSource(token);
        handshakeLimit.CancelAfter(TimeSpan.FromSeconds(12));
        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate=_certificate,
            EnabledSslProtocols=SslProtocols.Tls12 | SslProtocols.Tls13,
            ClientCertificateRequired=false,
            CertificateRevocationCheckMode=X509RevocationMode.NoCheck
        },handshakeLimit.Token);
        var request=await Wire.ReadAsync(ssl,handshakeLimit.Token);
        var trustedSource=request.Type=="auth-trusted" ?
            _trusted?.AuthenticateIncoming(request.Text) : null;
        bool approved=trustedSource!=null ||
            (request.Type=="auth" && ConstantTimeSecret(request.Text,Secret));
        if (!approved && request.Type=="auth-request" && _requestApproval!=null)
        {
            using var approvalLimit = CancellationTokenSource.CreateLinkedTokenSource(token);
            approvalLimit.CancelAfter(TimeSpan.FromSeconds(40));
            string ip=(client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
            string name=(request.Text ?? "Unknown device").Trim();
            if (name.Length>60)name=name[..60];
            try { approved=await _requestApproval(name+" ("+ip+")",approvalLimit.Token); }
            catch (OperationCanceledException) { approved=false; }
        }
        if(!approved)
        {
            await Wire.WriteAsync(ssl,new Packet("denied"),token);
            return;
        }
        var topology=MonitorTopology.Capture();
        var bounds=topology.Bounds;
        await Wire.WriteAsync(ssl,new Packet("ready",bounds.Left,bounds.Top,
            bounds.Width,bounds.Height,topology.ToWire()),token);

        using var helloTimeout=CancellationTokenSource.CreateLinkedTokenSource(token);
        helloTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        var hello=await Wire.ReadAsync(ssl,helloTimeout.Token);
        if(hello.Type!="hello") throw new InvalidDataException("Missing authenticated hello.");
        var peer=ReadHello(hello.Text);
        // The authenticated hello carries the current Windows computer name.
        // A saved trusted-device alias may be stale after the PC was renamed;
        // keep the actual announced system name in the display diagram.
        var session=new Session{Id=id,Client=client,Stream=ssl,Peer=peer};
        session.UdpPeer = _udpHub?.AddPeer(id, client.Client.RemoteEndPoint);
        session.MovePacer = new FastMovePacer(
            canSendUdp: () => _udpHub != null && session.UdpPeer is { Confirmed: true } &&
                Volatile.Read(ref session.ActiveReverseLease) > 0,
            sendUdp: move =>
            {
                var peerUdp = session.UdpPeer;
                int lease = Volatile.Read(ref session.ActiveReverseLease);
                return _udpHub != null && peerUdp != null && peerUdp.Confirmed && lease > 0 &&
                    _udpHub.Send(peerUdp, FastUdpOp.ReverseMove, move.A, move.B, lease);
            },
            queueReliableTcp: move => QueueReliableReverseMoveForSession(session, move, countAsFallback: true),
            sendWarm: () =>
            {
                var peerUdp = session.UdpPeer;
                if (_udpHub != null && peerUdp is { Confirmed: true } && _sessions.ContainsKey(id))
                    _udpHub.Send(peerUdp, FastUdpOp.Warm, 0, 0, 0);
            },
            queueTrailingSync: move => QueueReliableReverseMoveForSession(session, move, countAsFallback: false));
        if(!_sessions.TryAdd(id,session))
        {
            session.MovePacer?.Dispose();
            session.HighResTimer.Dispose();
            _udpHub?.RemovePeer(session.UdpPeer);
            throw new InvalidOperationException("Duplicate peer session ID");
        }
        PeerListChanged?.Invoke();
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(token);
        var sendTask=Task.Run(()=>ReverseSendLoopAsync(session,linked.Token));
        try
        {
            bool first;
            lock(_authorityGate) first=_selected==null;
            if(first) Select(session);
            string versionSuffix = string.IsNullOrWhiteSpace(peer.AppVersion)
                ? "" : " [v" + peer.AppVersion + "]";
            _status("Connected: "+peer.Name+versionSuffix+" ("+ConnectedPeerCount+" simultaneous devices).");
            // The server advertises its CURRENT system name after successful
            // pairing. Do not rely on a stale saved alias on the other PC.
            await SendToAsync(session,new Packet("peer-name",
                Text:Environment.MachineName),token);
            if (_udpHub != null && session.UdpPeer != null)
            {
                await SendToAsync(session, new Packet("udp-channel",
                    _udpHub.Port, session.Id, Text: session.UdpPeer.Base64Key), token);
            }
            await PublishEnvironmentAsync(token);
            while(!token.IsCancellationRequested)
            {
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(PeerIdleGraceSeconds));
                var p=await Wire.ReadAsync(ssl,deadline.Token);
                if(p.Type=="goodbye") break;
                if(p.Type=="udp-ready")
                {
                    if (_udpHub != null && session.UdpPeer != null && p.A is > 0 and <= 65535)
                    {
                        _udpHub.ConfigurePeerPort(session.UdpPeer, p.A);
                        session.MovePacer?.SetMinIntervalMs(
                            session.Peer.Platform == "Android" ? AndroidUdpMinIntervalMs : 2.5);
                        var retryPeer = session.UdpPeer;
                        var hub = _udpHub;
                        _ = Task.Run(async () =>
                        {
                            for (int i = 0; i < 4 && !token.IsCancellationRequested && !retryPeer.Confirmed; i++)
                            {
                                try { await Task.Delay(200, token); }
                                catch (OperationCanceledException) { return; }
                                if (!retryPeer.Confirmed)
                                    hub.Send(retryPeer, FastUdpOp.Probe, hub.Port, 0, 0);
                            }
                        }, token);
                    }
                    continue;
                }
                if(p.Type=="udp-ack")
                {
                    if (session.UdpPeer != null)
                    {
                        session.UdpPeer.Confirmed = true;
                        if (session.UdpPeer.TryMarkConfirmedLogged())
                            _status($"Low-latency input confirmed for {session.Peer.Name}.");
                    }
                    continue;
                }
                if(p.Type=="trust-request")
                {
                    bool allowed=false;
                    if(_trusted!=null && _requestTrustApproval!=null &&
                       p.Text is {Length:64} proposal && proposal.All(Uri.IsHexDigit))
                    {
                        using var trustLimit=CancellationTokenSource.CreateLinkedTokenSource(token);
                        trustLimit.CancelAfter(TimeSpan.FromSeconds(50));
                        try
                        {
                            allowed=await _requestTrustApproval(peer.Name,trustLimit.Token);
                            if(allowed)_trusted.AllowIncoming(peer.Name,proposal);
                        }
                        catch(Exception ex) when (ex is not OperationCanceledException)
                        {
                            _status("Cannot save device trust: "+ex.Message);
                        }
                        catch(OperationCanceledException){allowed=false;}
                    }
                    await SendToAsync(session,new Packet(allowed?"trust-granted":"trust-denied"),token);
                    continue;
                }
                if(p.Type=="topology")
                {
                    var updatedTopology=MonitorTopology.FromWire(p.Text);
                    var primaryMon=updatedTopology.Monitors.FirstOrDefault(m=>m.Primary)
                        ?? updatedTopology.Monitors[0];
                    session.Peer=session.Peer with {
                        Monitors=updatedTopology,
                        DensityDpi=primaryMon.DensityDpi ?? session.Peer.DensityDpi,
                        RefreshHz=primaryMon.RefreshHz ?? session.Peer.RefreshHz,
                        RotationDegrees=primaryMon.RotationDegrees ?? session.Peer.RotationDegrees,
                        OsVersion=!string.IsNullOrWhiteSpace(primaryMon.OsVersion)
                            ? primaryMon.OsVersion : session.Peer.OsVersion,
                        AppVersion=!string.IsNullOrWhiteSpace(primaryMon.AppVersion)
                            ? primaryMon.AppVersion : session.Peer.AppVersion
                    };
                    await PublishEnvironmentAsync(token);
                    PeerListChanged?.Invoke();
                    if(ReferenceEquals(Volatile.Read(ref _selected),session))
                        PeerTopologyChanged?.Invoke(session.Peer);
                    continue;
                }
                if(p.Type=="placement")
                {
                    if(ReferenceEquals(Volatile.Read(ref _selected),session) &&
                       Enum.TryParse<MonitorEdge>(p.Text,true,out var edge))
                        PeerPlacementChanged?.Invoke(edge);
                    continue;
                }
                if(p.Type=="reverse-key-unavailable")
                {
                    RelayLease? relay;
                    lock(_authorityGate)
                    {
                        _relayByTarget.TryGetValue(session.Id,out relay);
                        if(relay!=null && relay.Claim!=p.A)relay=null;
                    }
                    if(relay!=null)
                    {
                        try { await SendToAsync(relay.Source,
                            new Packet("android-keyboard-unavailable"),token); }
                        catch(Exception){}
                    }
                    else if(ReferenceEquals(Volatile.Read(ref _selected),session))
                        _status("Android keyboard input unavailable: tap an editable field on the phone and verify DeskTouch Accessibility is enabled.");
                    continue;
                }
                if(p.Type is "reverse-granted" or "reverse-busy" or
                    "reverse-local-active" or "reverse-active")
                {
                    RelayLease? relay;
                    lock (_authorityGate)
                    {
                        _relayByTarget.TryGetValue(session.Id, out relay);
                        if (relay != null && relay.Claim != p.A) relay=null;
                        if (relay != null && p.Type=="reverse-granted")
                        {
                            relay.Granted=true;
                            Volatile.Write(ref session.ActiveReverseLease, relay.Claim);
                            session.MovePacer?.SetWarmActive(true);
                        }
                    }
                    if (relay != null)
                    {
                        // Deliver the destination's real grant/applied ACK to
                        // the original authenticated controller, not the hub UI.
                        if (p.Type is "reverse-busy" or "reverse-local-active")
                        {
                            Volatile.Write(ref session.ActiveReverseLease, 0);
                            session.MovePacer?.SetWarmActive(false);
                            session.MovePacer?.ClearPending();
                            RemoveRelayFromSource(relay.Source.Id);
                        }
                        string response=p.Type["reverse-".Length..];
                        if (p.Type!="reverse-active" || relay.Granted)
                            try { await SendToAsync(relay.Source,
                                new Packet(response,p.A),token); }
                            catch (Exception) { }
                    }
                    else if(ReferenceEquals(Volatile.Read(ref _selected),session))
                    {
                        if(p.Type is "reverse-busy" or "reverse-local-active")
                        {
                            lock(_authorityGate)
                            {
                                _hubClaims.Remove(session.Id);
                                Volatile.Write(ref session.ActiveReverseLease, 0);
                            }
                            session.MovePacer?.SetWarmActive(false);
                            session.MovePacer?.ClearPending();
                        }
                        ReverseControlMessage?.Invoke(p);
                    }
                    continue;
                }
                if(p.Type=="android-capability")
                {
                    if(!AllowsAndroidCapability(session.Peer,p))continue;
                    bool canReceive=p.A==1;
                    if(session.Peer.SupportsReverseInput==canReceive)continue;
                    session.Peer=session.Peer with {SupportsReverseInput=canReceive};
                    if(!canReceive)
                    {
                        // Revoking or pausing Android Accessibility must immediately
                        // stop any hub-local or routed keyboard/mouse lease.
                        TrySendReverseTo(session.Id,new Packet("reset"));
                        RelayLease? relay;
                        lock(_authorityGate)
                            _relayByTarget.TryGetValue(session.Id,out relay);
                        if(relay!=null)
                        {
                            var removed=RemoveRelayFromSource(relay.Source.Id);
                            if(removed!=null)
                                await RevokeRelayAsync(removed,true,token);
                        }
                    }
                    await PublishEnvironmentAsync(token);
                    PeerListChanged?.Invoke();
                    _status(canReceive
                        ? "Android Accessibility active on an existing secure connection."
                        : "Android control paused for banking or helper turned off; phone stays connected.");
                    continue;
                }
                if(p.Type=="environment-placement-update")
                {
                    if(p.Text is not { Length: > 1 and <= 512 })continue;
                    EnvironmentPosition? placement;
                    try { placement=JsonSerializer.Deserialize<EnvironmentPosition>(p.Text); }
                    catch(JsonException){continue;}
                    if(!EnvironmentPosition.Valid(placement) ||
                        !_sessions.Values.Any(other=>
                            OwnsPlacement(other.Peer,placement!.Key)))continue;
                    ApplyEnvironmentPosition(placement!);
                    continue;
                }
                if(p.Type=="environment-select")
                {
                    // The hub owns routing decisions. Clients may request
                    // only connection IDs issued on this authenticated link.
                    Session? target=p.A==0 ? null :
                        _sessions.TryGetValue(p.A,out var targetPeer)?targetPeer:null;
                    bool valid=p.A==0 ||
                        (target!=null && !ReferenceEquals(target,session) &&
                         target.Peer.SupportsReverseInput);
                    if(!valid)
                    {
                        await SendToAsync(session,new Packet("environment-unavailable",p.A),token);
                        continue;
                    }
                    var previous=RemoveRelayFromSource(session.Id);
                    if(previous!=null)
                        await RevokeRelayAsync(previous,false,token);
                    bool localOwner=false;
                    lock(_authorityGate)
                    {
                        if(ReferenceEquals(_owner,session))
                        {
                            _owner=null;session.Lease=0;session.AppliedLease=0;
                            _priority.ReleaseRemote();localOwner=true;
                        }
                        session.EnvironmentTargetId=p.A;
                    }
                    if(localOwner)lock(session.InputLock)session.Injector.ReleaseAll();
                    if(target?.UdpPeer is { Confirmed: true } targetUdp)
                        _udpHub?.Send(targetUdp, FastUdpOp.Warm, 0, 0, 0);
                    await SendToAsync(session,new Packet("environment-selected",p.A),token);
                    continue;
                }
                if(p.Type=="claim" && session.EnvironmentTargetId!=0)
                {
                    RelayLease? relay=null;
                    lock(_authorityGate)
                    {
                        if(_sessions.TryGetValue(session.EnvironmentTargetId,
                                out var destination) &&
                           CanRoute(session.Peer,destination.Peer,
                                !ReferenceEquals(session,destination),p.A) &&
                           !_relayBySource.ContainsKey(session.Id) &&
                           !_relayByTarget.ContainsKey(destination.Id) &&
                           !_hubClaims.ContainsKey(destination.Id) &&
                           !_relayByTarget.ContainsKey(session.Id) &&
                           !_relayBySource.ContainsKey(destination.Id) &&
                           !ReferenceEquals(_owner,session))
                        {
                            relay=new RelayLease { Source=session,
                                Target=destination,Claim=p.A };
                            _relayBySource.Add(session.Id,relay);
                            _relayByTarget.Add(destination.Id,relay);
                            _=Task.Run(()=>RelayForwardLoopAsync(relay,_cancel.Token));
                        }
                    }
                    if(relay==null)
                        await SendToAsync(session,new Packet("busy",p.A),token);
                    else
                    {
                        // Relayed moves are already paced by the source PC; keep a short
                        // anti-burst cap on the hub so Wi-Fi jitter never double-stalls frames.
                        relay.Target.MovePacer?.SetMinIntervalMs(
                            relay.Target.Peer.Platform == "Android" ? AndroidUdpMinIntervalMs : 2.0);
                        if (relay.Target.UdpPeer is { Confirmed: true } relayUdp)
                            _udpHub?.Send(relayUdp, FastUdpOp.Warm, 0, 0, 0);
                        try { await SendToAsync(relay.Target,
                            new Packet("reverse-claim",p.A),token); }
                        catch(Exception)
                        {
                            RemoveRelayFromSource(session.Id);
                            await SendToAsync(session,new Packet("busy",p.A),token);
                        }
                    }
                    continue;
                }
                if(p.Type=="claim")
                {
                    bool granted=false;
                    lock(_authorityGate)
                    {
                        // Only an authenticated Windows or opt-in Android input controller can claim authority.
                        if(p.A>0 && AllowsForwardInput(session.Peer) &&
                           (_owner==null || ReferenceEquals(_owner,session)) &&
                           (AllowForwardClaim?.Invoke() ?? true) && _priority.TryClaim())
                        {
                            _owner=session;
                            session.Lease=p.A;
                            session.AppliedLease=0;
                            granted=true;
                        }
                    }
                    // A forward Windows INPUT CLAIM controls the
                    // local hub only. It is NOT a request to change which
                    // remote destination the hub operator is controlling.
                    // Selecting the claimant here revoked the selected PC's
                    // reverse keyboard lease whenever the Windows peer used native input.
                    // Forward owner (_owner) and reverse target (_selected)
                    // must remain independent while all peers stay connected.
                    if(granted && !_sessions.ContainsKey(session.Id))
                    {
                        lock(_authorityGate)
                        {
                            if(ReferenceEquals(_owner,session))
                            { _owner=null;session.Lease=0;_priority.ReleaseRemote(); }
                        }
                        granted=false;
                    }
                    await SendToAsync(session,new Packet(granted?"granted":"busy",p.A),token);
                    continue;
                }
                if(p.Type=="reset" || p.Type=="release")
                {
                    RelayLease? activeRelay=null;
                    lock(_authorityGate)
                        if(_relayBySource.TryGetValue(session.Id,out var candidate) &&
                            (p.Type=="reset" || p.A==candidate.Claim))
                            activeRelay=candidate;
                    if(activeRelay!=null)
                    {
                        var removed=RemoveRelayFromSource(session.Id);
                        if(removed!=null)await RevokeRelayAsync(removed,false,token);
                        continue;
                    }
                    bool wasOwner=false;
                    lock(_authorityGate)
                    {
                        if(ReferenceEquals(_owner,session) &&
                           (p.Type=="reset" || (p.A>0 && p.A==session.Lease)))
                        {
                            _owner=null;session.Lease=0;session.AppliedLease=0;
                            _priority.ReleaseRemote();wasOwner=true;
                        }
                    }
                    if(wasOwner) lock(session.InputLock)session.Injector.ReleaseAll();
                    continue;
                }
                if(p.Type=="ping")
                {
                    // Answer authenticated heartbeats so Windows Wi-Fi roaming
                    // does not leave the input lease in an ambiguous state.
                    await SendToAsync(session,new Packet("pong"),token);
                    continue;
                }
                if(p.Type=="warm")
                {
                    BroadcastWarmToConfirmedTargets(session.Id);
                    continue;
                }
                RelayLease? forward=null;
                lock(_authorityGate)
                    _relayBySource.TryGetValue(session.Id,out forward);
                if(forward!=null)
                {
                    if(forward.Granted &&
                        p.Type is ("move" or "button" or "key" or "wheel" or "text"))
                    {
                        if(!QueueRoutedInput(forward,p))
                        {
                            var removed=RemoveRelayFromSource(session.Id);
                            if(removed!=null)
                                await RevokeRelayAsync(removed,true,token);
                        }
                    }
                    continue;
                }
                if(session.EnvironmentTargetId!=0)
                    continue; // Never accidentally inject an unrouted packet locally.
                int lease;
                lock(_authorityGate)
                    lease=ReferenceEquals(_owner,session)?session.Lease:0;
                bool applied=false;
                bool injectionAttempted=false;
                if(lease>0 && _priority.CanApplyRemote &&
                   p.Type is ("move" or "button" or "key" or "wheel" or "text"))
                {
                    lock(session.InputLock)
                    {
                        lock(_authorityGate)
                        {
                            if(ReferenceEquals(_owner,session) && lease==session.Lease &&
                               _priority.CanApplyRemote)
                            {
                                injectionAttempted=true;
                                applied=session.Injector.Handle(p);
                            }
                        }
                    }
                    if(injectionAttempted && !applied && (p.Type=="key" || p.Type=="text") &&
                       Interlocked.CompareExchange(ref session.KeyboardFailureReported,1,0)==0)
                    {
                        // Never log characters, scan codes, or personal input.
                        await SendToAsync(session,new Packet("keyboard-unavailable"),token);
                        _status("Remote PC keyboard injection rejected; verify focus and elevation.");
                    }
                    if(applied && p.Type=="move" && _priority.CanApplyRemote &&
                       Interlocked.CompareExchange(ref session.AppliedLease,lease,0)==0)
                        await SendToAsync(session,new Packet("active",lease),token);
                }
            }
        }
        catch(EndOfStreamException) { _status("Peer closed connection: "+peer.Name); }
        catch(IOException ex) { _status("Peer network ended: "+ex.Message); }
        catch(OperationCanceledException) when (!token.IsCancellationRequested)
            { _status("Peer heartbeat expired; releasing input: "+peer.Name); }
        catch(OperationCanceledException) { }
        finally
        {
            linked.Cancel();
            session.ReverseQueue.Writer.TryComplete();
            _sessions.TryRemove(id,out _);
            lock(_authorityGate)
            {
                _hubClaims.Remove(id);
                Volatile.Write(ref session.ActiveReverseLease, 0);
            }
            session.MovePacer?.Dispose();
            session.HighResTimer.Dispose();
            _udpHub?.RemovePeer(session.UdpPeer);
            var disconnectedRoutes=new List<RelayLease>();
            lock(_authorityGate)
            {
                if(_relayBySource.TryGetValue(id,out var from))
                    disconnectedRoutes.Add(from);
                if(_relayByTarget.TryGetValue(id,out var to) &&
                    !disconnectedRoutes.Contains(to))
                    disconnectedRoutes.Add(to);
                foreach(var route in disconnectedRoutes)
                {
                    _relayBySource.Remove(route.Source.Id);
                    _relayByTarget.Remove(route.Target.Id);
                }
            }
            foreach(var route in disconnectedRoutes)
            {
                Interlocked.Exchange(ref route.LatestMove,null);
                route.InputQueue.Writer.TryComplete();
                await RevokeRelayAsync(route,route.Target.Id==id,token);
            }
            await PublishEnvironmentAsync(token);
            PeerListChanged?.Invoke();
            Session? previous=null, next=null;
            bool released=false;
            lock(_authorityGate)
            {
                if(ReferenceEquals(_owner,session))
                {
                    _owner=null;session.Lease=0;session.AppliedLease=0;
                    _priority.ReleaseRemote();released=true;
                }
                if(ReferenceEquals(_selected,session))
                {
                    previous=session;
                    next=_sessions.Values.OrderBy(s=>s.Id).FirstOrDefault();
                    _selected=next;
                }
            }
            lock(session.InputLock)session.Injector.ReleaseAll();
            if(previous!=null)NotifySelection(previous,next);
            if(released)_status("Input authority released on peer disconnect.");
            _status("Disconnected: "+peer.Name+" ("+ConnectedPeerCount+" devices remain).");
            try { await sendTask; } catch(Exception) { }
            session.Write.Dispose();
        }
    }

    private void OnUdpHubMessage(FastUdpHub.Peer peer, FastUdpMessage msg)
    {
        if (!_sessions.TryGetValue(peer.SessionId, out var session)) return;
        if (peer.Confirmed && peer.TryMarkConfirmedLogged())
            _status($"Low-latency input confirmed for {session.Peer.Name}.");
        if (msg.Op is not (FastUdpOp.Move or FastUdpOp.Warm)) return;

        RelayLease? forward = null;
        lock (_authorityGate)
            _relayBySource.TryGetValue(session.Id, out forward);
        if (forward != null)
        {
            if (!forward.Granted) return;
            if (msg.Op == FastUdpOp.Warm)
            {
                var targetUdp = forward.Target.UdpPeer;
                if (targetUdp is { HasEndpoint: true } && _udpHub != null)
                    _udpHub.Send(targetUdp, FastUdpOp.Warm, 0, 0, 0);
                return;
            }
            QueueRoutedInput(forward, new Packet("move", msg.A, msg.B));
            return;
        }
        if (msg.Op == FastUdpOp.Warm)
        {
            if (session.Peer.Platform == "Windows")
                BroadcastWarmToConfirmedTargets(session.Id);
            else if (ShouldEchoAndroidWarm(session.Peer.Platform) &&
                _udpHub != null && session.UdpPeer is { Confirmed: true } peerUdp)
                _udpHub.Send(peerUdp, FastUdpOp.Warm, 0, 0, 0);
            return;
        }
        if (msg.Op != FastUdpOp.Move) return;
        if (session.EnvironmentTargetId != 0) return;

        int lease;
        lock (_authorityGate)
            lease = ReferenceEquals(_owner, session) ? session.Lease : 0;
        if (lease <= 0 || lease != msg.Lease || !_priority.CanApplyRemote) return;

        bool applied = false;
        lock (session.InputLock)
        {
            lock (_authorityGate)
            {
                if (ReferenceEquals(_owner, session) && lease == session.Lease &&
                    _priority.CanApplyRemote)
                    applied = session.Injector.Handle(new Packet("move", msg.A, msg.B));
            }
        }
        if (applied && _priority.CanApplyRemote &&
            Interlocked.CompareExchange(ref session.AppliedLease, lease, 0) == 0)
        {
            _ = Task.Run(async () =>
            {
                try { await SendToAsync(session, new Packet("active", lease), _cancel.Token); }
                catch (Exception) { }
            });
        }
    }

    internal const double AndroidUdpMinIntervalMs = 3.5;

    internal static bool ShouldEchoAndroidWarm(string? platform) =>
        string.Equals(platform, "Android", StringComparison.Ordinal);

    public void Dispose()
    {
        if(_disposed)return;
        _disposed=true;
        _priority.PhysicalOverride -= OnPhysicalOverride;
        _cancel.Cancel();
        _udpHub?.Dispose();
        _listener.Stop();
        foreach(var client in _clients.Values) client.Close();
        _priority.ReleaseRemote();
        _certificate.Dispose();
        // Cancellation source is intentionally retained until all accepted
        // session tasks unwind; they may still observe its token.
    }
}

internal sealed class HostSession : IInputSession, IDisposable
{
    private readonly TcpClient _client;
    private readonly SslStream _ssl;
    private readonly IPAddress _serverAddress;
    private FastUdpClient? _udpClient;
    private FastMovePacer? _movePacer;
    private int _forwardClaimLease;
    private int _udpAckSent;
    private readonly Channel<Packet> _queue = Channel.CreateBounded<Packet>(
        new BoundedChannelOptions(2048) { SingleReader = true, SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _cancel = new();
    private readonly Action<string> _onFailure;
    private int _closed;
    internal System.Drawing.Rectangle Screen => RemoteMonitors.Bounds;
    internal MonitorTopology RemoteMonitors => Volatile.Read(ref _remoteMonitors);
    internal event Action<string>? RemoteControlState;
    internal event Action<MonitorTopology>? RemoteTopologyChanged;
    internal event Action<string>? RemoteNameChanged;
    private string? _remoteSystemName;
    private string? _environmentHubName;
    private readonly MonitorTopology _environmentHubMonitors;
    private EnvironmentMember[] _environmentMembers = [];
    private int _environmentOwnId;
    private int _environmentTargetId;
    internal IReadOnlyList<EnvironmentMember> EnvironmentMembers =>
        Volatile.Read(ref _environmentMembers);
    internal int SelectedEnvironmentTargetId => Volatile.Read(ref _environmentTargetId);
    internal int OwnEnvironmentMemberId => Volatile.Read(ref _environmentOwnId);
    internal event Action? EnvironmentRosterChanged;
    internal event Action<IReadOnlyDictionary<string,System.Drawing.PointF>>?
        EnvironmentLayoutChanged;
    private IReadOnlyDictionary<string,System.Drawing.PointF> _sharedLayout =
        new Dictionary<string,System.Drawing.PointF>(StringComparer.Ordinal);
    internal IReadOnlyDictionary<string,System.Drawing.PointF> SharedLayout =>
        Volatile.Read(ref _sharedLayout);
    internal bool PublishEnvironmentPlacement(string key,
        System.Drawing.PointF point)
    {
        var placement=new EnvironmentPosition(key,point.X,point.Y);
        return EnvironmentPosition.Valid(placement) &&
            TrySend(new Packet("environment-placement-update",
                Text:JsonSerializer.Serialize(placement)));
    }
    internal string? RemoteSystemName => Volatile.Read(ref _remoteSystemName);
    internal event Action<MonitorEdge>? RemotePlacementChanged;
    private MonitorTopology _remoteMonitors;
    private Packet? _latestMove;
    private int _moveSignaled;
    private TaskCompletionSource<bool>? _pendingTrust;
    private LocalInputPriority? _reversePriority;
    private Func<bool>? _allowReverseClaim;
    private readonly object _reverseInputGate = new();
    private InputInjector? _reverseInjector;
    private int _reverseLease;
    private int _reverseAppliedLease;
    internal event Action<Packet>? AuthorityControlPacket;
    internal event Action<string>? AuthorityDiagnostic;
    internal event Action<string>? DiagnosticLog;
    internal event Action? HeartbeatAcknowledged;
    internal event Action<string>? InputDeliveryWarning;
    private IDisposable? _highResTimer;
    private long _tcpFallbackMoves;
    private long _lastPingSentTicks;
    private double _lastRttMs;
    private int _udpConfirmedLogged;
    private long _lastTcpWarmTicks;
    private string _lastLoggedVersions = "";
    MonitorTopology IInputSession.RemoteMonitors => RemoteMonitors;
    bool IInputSession.TrySend(Packet packet) => TrySend(packet);

    internal bool SelectEnvironmentTarget(int id)
    {
        if (Volatile.Read(ref _closed)!=0 || id<0) return false;
        var members=Volatile.Read(ref _environmentMembers);
        EnvironmentMember? chosen=members.FirstOrDefault(m=>m.Id==id);
        if (id!=0 && (chosen==null || id==_environmentOwnId ||
            !chosen.SupportsReverseInput)) return false;
        // The hub's real monitor information is retained separately from the
        // chosen member's display geometry. Changing targets never opens TCP.
        MonitorTopology topology=id==0 ? _environmentHubMonitors :
            MonitorTopology.FromWire(chosen!.Layout);
        string name=id==0 ? _environmentHubName??"Environment hub" : chosen!.Name;
        if (id!=SelectedEnvironmentTargetId)
        {
            if (!TrySend(new Packet("environment-select",id))) return false;
            Volatile.Write(ref _environmentTargetId,id);
        }
        Volatile.Write(ref _remoteMonitors,topology);
        Volatile.Write(ref _remoteSystemName,name);
        RemoteTopologyChanged?.Invoke(topology);
        RemoteNameChanged?.Invoke(name);
        return true;
    }

    internal void AttachReverseInput(LocalInputPriority priority, Func<bool> allowClaim)
    {
        if (_reversePriority != null)
            throw new InvalidOperationException("Reverse authority monitor already installed.");
        _reversePriority = priority;
        _allowReverseClaim = allowClaim;
        _reverseInjector = new InputInjector();
        priority.PhysicalOverride += OnReverseNativeActivity;
    }

    private void OnReverseNativeActivity()
    {
        int revoked = Interlocked.Exchange(ref _reverseLease, 0);
        Interlocked.Exchange(ref _reverseAppliedLease, 0);
        if (revoked <= 0) return;
        // Native input immediately revokes permissions on the same thread;
        // release injected held keys asynchronously outside the hook callback.
        _ = Task.Run(() =>
        {
            lock (_reverseInputGate) _reverseInjector?.ReleaseAll();
        });
        TrySend(new Packet("reverse-local-active", revoked));
        AuthorityDiagnostic?.Invoke("Local Windows keyboard/mouse reclaimed authority from remote computer.");
    }

    private void ReleaseReverseAuthority()
    {
        Interlocked.Exchange(ref _reverseLease, 0);
        Interlocked.Exchange(ref _reverseAppliedLease, 0);
        _reversePriority?.ReleaseRemote();
        lock (_reverseInputGate) _reverseInjector?.ReleaseAll();
    }

    private void ProcessReverseInput(Packet packet)
    {
        if (!ReverseInputProtocol.TryDecode(packet, out var request)) return;
        if (request.Type == "claim")
        {
            bool permitted = request.A > 0 &&
                (_allowReverseClaim?.Invoke() ?? false) &&
                (_reversePriority?.TryClaim() ?? false);
            if (permitted)
            {
                Volatile.Write(ref _reverseLease, request.A);
                Volatile.Write(ref _reverseAppliedLease, 0);
                permitted = _reversePriority?.CanApplyRemote == true;
                if (!permitted) ReleaseReverseAuthority();
            }
            TrySend(new Packet(permitted ? "reverse-granted" : "reverse-busy", request.A));
            return;
        }
        if (request.Type == "reset" ||
            (request.Type == "release" && request.A > 0 &&
                request.A == Volatile.Read(ref _reverseLease)))
        {
            ReleaseReverseAuthority();
            return;
        }
        if (Volatile.Read(ref _reverseLease) <= 0 ||
            _reversePriority?.CanApplyRemote != true) return;
        int active = Volatile.Read(ref _reverseLease);
        bool cursorApplied = false;
        lock (_reverseInputGate)
        {
            if (active > 0 && _reversePriority?.CanApplyRemote == true)
            {
                bool injected = _reverseInjector?.Handle(request) == true;
                cursorApplied = injected && request.Type == "move";
            }
        }
        if (cursorApplied && _reversePriority?.CanApplyRemote == true &&
            Volatile.Read(ref _reverseLease) == active &&
            Interlocked.CompareExchange(ref _reverseAppliedLease, active, 0) == 0)
            TrySend(new Packet("reverse-active", active));
    }

    private HostSession(TcpClient client, SslStream ssl, IPAddress serverAddress,
        MonitorTopology monitors, Action<string> onFailure)
    {
        _client = client; _ssl = ssl; _serverAddress = serverAddress;
        _remoteMonitors = monitors;
        _environmentHubMonitors = monitors; _onFailure = onFailure;
        _movePacer = new FastMovePacer(
            canSendUdp: () => Volatile.Read(ref _closed) == 0 &&
                _udpClient is { Confirmed: true } && Volatile.Read(ref _forwardClaimLease) > 0,
            sendUdp: move =>
            {
                var udp = _udpClient;
                int lease = Volatile.Read(ref _forwardClaimLease);
                return udp != null && udp.Confirmed && lease > 0 &&
                    udp.Send(FastUdpOp.Move, move.A, move.B, lease);
            },
            queueReliableTcp: move => QueueTcpMove(move, countAsFallback: true),
            sendWarm: () =>
            {
                var udp = _udpClient;
                if (udp is { Confirmed: true } && Volatile.Read(ref _closed) == 0)
                    udp.Send(FastUdpOp.Warm, 0, 0, 0);
            },
            queueTrailingSync: move => QueueTcpMove(move, countAsFallback: false));
        _ = Task.Run(SendLoopAsync);
        _ = Task.Run(HeartbeatAsync);
        _ = Task.Run(ReceiveLoopAsync);
    }

    internal static Task<HostSession> ConnectAsync(
        string ip, string pinnedFingerprint, string secret, Action<string> onFailure,
        CancellationToken cancellation) =>
            ConnectCoreAsync(ip, pinnedFingerprint, secret, false, onFailure, cancellation);

    internal static Task<HostSession> ConnectWithApprovalAsync(
        string ip, string pinnedFingerprint, Action<string> onFailure,
        CancellationToken cancellation) =>
            ConnectCoreAsync(ip, pinnedFingerprint, "", true, onFailure, cancellation);

    internal static Task<HostSession> ConnectTrustedAsync(
        string ip, string pinnedFingerprint, string trustToken,
        Action<string> onFailure, CancellationToken cancellation) =>
            ConnectCoreAsync(ip, pinnedFingerprint, trustToken, false,
                onFailure, cancellation, trustedConnection: true);

    internal async Task<string?> RequestTrustAsync(CancellationToken cancellation)
    {
        if (Volatile.Read(ref _closed) != 0) return null;
        string token = TrustedDeviceStore.NewSecret();
        var pending = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _pendingTrust, pending, null) != null)
            throw new InvalidOperationException("A trust request is already pending.");
        try
        {
            if (!TrySend(new Packet("trust-request", Text: token))) return null;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(55));
            return await pending.Task.WaitAsync(deadline.Token) ? token : null;
        }
        finally { Interlocked.CompareExchange(ref _pendingTrust, null, pending); }
    }

    private static async Task<HostSession> ConnectCoreAsync(
        string ip, string pinnedFingerprint, string secret, bool askReceiver,
        Action<string> onFailure, CancellationToken cancellation,
        bool trustedConnection = false)
    {
        pinnedFingerprint = new string(pinnedFingerprint.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        secret = secret.Trim().ToUpperInvariant();
        if (pinnedFingerprint.Length != 64 || !pinnedFingerprint.All(Uri.IsHexDigit))
            throw new ArgumentException("Enter the receiver's complete 64-digit SHA-256 fingerprint.");
        if (!askReceiver && (secret.Length != (trustedConnection ? 64 : 32) ||
            !secret.All(Uri.IsHexDigit)))
            throw new ArgumentException("Invalid authenticated pairing credential.");
        if (!IPAddress.TryParse(ip.Trim(), out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("Enter the receiver's IPv4 address.");

        var client = new TcpClient { NoDelay = true };
        try { client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService, 0x10); }
        catch (Exception) { }
        SslStream? ssl = null;
        string stage = "TCP connection";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await client.ConnectAsync(address, Wire.Port, timeout.Token);
            stage = "TLS certificate verification";
            ssl = new SslStream(client.GetStream(), false, (_, cert, _, _) =>
            {
                if (cert == null) return false;
                string actual = Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));
                return CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actual), Convert.FromHexString(pinnedFingerprint));
            });
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "DeskTouch-LAN",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, timeout.Token);
            stage = "receiver approval";
            await Wire.WriteAsync(ssl, new Packet(
                askReceiver ? "auth-request" :
                    trustedConnection ? "auth-trusted" : "auth",
                Text: askReceiver ? Environment.MachineName : secret), timeout.Token);
            // The receiver may need time for the owner to click Approve.
            using var acceptedLimit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            acceptedLimit.CancelAfter(TimeSpan.FromSeconds(45));
            var ready = await Wire.ReadAsync(ssl, acceptedLimit.Token);
            if (ready.Type != "ready" || ready.C < 200 || ready.D < 100 ||
                ready.C > 50000 || ready.D > 50000)
                throw new AuthenticationException("The other computer did not approve the connection.");
            stage = "monitor layout";
            var monitors = MonitorTopology.FromWire(ready.Text);
            if (monitors.Bounds != new System.Drawing.Rectangle(ready.A, ready.B, ready.C, ready.D))
                throw new AuthenticationException("Receiver monitor geometry failed validation.");
            // Send the SOURCE topology too so the receiver can display an
            // identical two-computer arrangement without initiating a second
            // TCP connection (which would race the existing receiver).
            var mine = MonitorTopology.Capture();
            using var identity = PersistentReceiverIdentity.LoadOrCreate();
            string sourceId = Convert.ToHexString(
                SHA256.HashData(identity.RawData));
            var hello = JsonSerializer.Serialize(
                new PeerHello(Environment.MachineName, mine.ToWire(), true, sourceId, "Windows"));
            await Wire.WriteAsync(ssl, new Packet("hello", Text: hello),
                acceptedLimit.Token);
            return new HostSession(client, ssl, address, monitors, onFailure);
        }
        catch (Exception ex)
        {
            ssl?.Dispose();
            client.Dispose();
            string guidance = stage switch
            {
                "TCP connection" =>
                    $"The computer was discovered, but the keyboard/mouse connection on TCP {Wire.Port} could not be opened. " +
                    "On the receiving PC, allow DeskTouch.DeviceShare.exe through Windows Defender Firewall on PRIVATE networks only.",
                "TLS certificate verification" =>
                    "The secure connection failed. Both PCs must use the corrected DeskTouch build, and the receiver may need restarting to refresh its certificate.",
                "receiver approval" =>
                    "The receiving computer did not approve pairing, the approval timed out, or the connection was interrupted. " +
                    "Keep DeskTouch open on that PC and approve the request.",
                "monitor layout" =>
                    "Secure pairing succeeded, but Windows display information could not be exchanged.",
                _ => "Windows pairing could not complete."
            };
            if (ex is OperationCanceledException && cancellation.IsCancellationRequested)
                throw;
            if (ex is AuthenticationException && stage == "receiver approval")
                throw new IOException(guidance, ex);
            throw new IOException($"DeskTouch connection failed during {stage}: {guidance} ({ex.Message})", ex);
        }
    }

    private void QueueTcpMove(Packet p, bool countAsFallback = true)
    {
        if (Volatile.Read(ref _closed) != 0) return;
        if (countAsFallback)
            Interlocked.Increment(ref _tcpFallbackMoves);
        Interlocked.Exchange(ref _latestMove, p);
        if (Interlocked.CompareExchange(ref _moveSignaled, 1, 0) == 0 &&
            !_queue.Writer.TryWrite(new Packet("move-flush")))
            Interlocked.Exchange(ref _moveSignaled, 0);
    }

    internal bool TrySend(Packet p)
    {
        if (Volatile.Read(ref _closed) != 0) return false;
        if (p.Type == "warm")
        {
            var udp = Volatile.Read(ref _udpClient);
            if (udp is { Confirmed: true })
                udp.Send(FastUdpOp.Warm, 0, 0, 0);
            // Also wake relayed Android targets through the hub at most once per 100ms
            // while approaching an edge so the phone radio is awake before handoff.
            long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            long prevTicks = Interlocked.Read(ref _lastTcpWarmTicks);
            double sinceMs = prevTicks == 0 ? double.MaxValue
                : (nowTicks - prevTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (sinceMs >= 100 &&
                EnvironmentMembers.Any(m => m.Platform == "Android" && m.SupportsReverseInput) &&
                Interlocked.CompareExchange(ref _lastTcpWarmTicks, nowTicks, prevTicks) == prevTicks)
            {
                _queue.Writer.TryWrite(new Packet("warm"));
            }
            return true;
        }
        if (p.Type == "claim")
            Volatile.Write(ref _forwardClaimLease, p.A);
        else if (p.Type is ("release" or "reset"))
        {
            Volatile.Write(ref _forwardClaimLease, 0);
            _movePacer?.SetWarmActive(false);
            _movePacer?.ClearPending();
        }
        if (p.Type == "move")
        {
            // Keep the newest pointer coordinate rather than queuing every
            // intermediate movement. A full move queue previously caused
            // disconnect loops and seconds of unwanted pointer lag.
            if (_movePacer?.TryHandleMove(p) == true)
            {
                Interlocked.Exchange(ref _latestMove, null);
                return true;
            }
            QueueTcpMove(p, countAsFallback: true);
            return true;
        }
        if (_queue.Writer.TryWrite(p)) return true;
        _onFailure("Control queue overloaded; local control restored safely.");
        return false;
    }

    internal void PublishSourceTopology(MonitorTopology layout) =>
        TrySend(new Packet("topology", Text: layout.ToWire()));

    internal void PublishSourcePlacement(MonitorEdge edge) =>
        TrySend(new Packet("placement", Text: edge.ToString()));

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (!_cancel.IsCancellationRequested)
            {
                var p = await Wire.ReadAsync(_ssl, _cancel.Token);
                if (p.Type == "environment-roster" &&
                    p.Text is { Length: > 1 and <= 7800 } roster)
                {
                    var members = JsonSerializer.Deserialize<EnvironmentMember[]>(roster)
                        ?? Array.Empty<EnvironmentMember>();
                    if (members.Length is < 1 or > 9 ||
                        !members.Any(m=>m.Id==0 && m.Platform=="Windows") ||
                        members.Select(m=>m.Id).Distinct().Count()!=members.Length ||
                        members.Any(m=>m.Id<0 || m.Name.Length is < 1 or > 100 ||
                            m.Name.Any(char.IsControl) ||
                            m.Platform is not ("Windows" or "Android")))
                        throw new InvalidDataException("Invalid authenticated environment roster.");
                    Volatile.Write(ref _environmentMembers,members);
                    Volatile.Write(ref _environmentOwnId,p.A);
                    int current=SelectedEnvironmentTargetId;
                    if (current!=0 && !members.Any(m=>m.Id==current &&
                        m.SupportsReverseInput))
                        SelectEnvironmentTarget(0);
                    EmitEnvironmentVersionsDiagnostic();
                    EnvironmentRosterChanged?.Invoke();
                }
                else if (p.Type=="environment-layout" &&
                    p.Text is { Length: > 1 and <= 4096 } serializedPositions)
                {
                    EnvironmentPosition[]? list;
                    try {list=JsonSerializer.Deserialize<EnvironmentPosition[]>(serializedPositions);}
                    catch(JsonException){continue;}
                    if(list==null || list.Length>8 ||
                        list.Any(x=>!EnvironmentPosition.Valid(x)) ||
                        list.Select(x=>x.Key).Distinct(StringComparer.Ordinal).Count()!=list.Length)
                        continue;
                    var positions=list.ToDictionary(x=>x.Key,
                        x=>new System.Drawing.PointF(x.X,x.Y),StringComparer.Ordinal);
                    Volatile.Write(ref _sharedLayout,positions);
                    EnvironmentLayoutChanged?.Invoke(positions);
                }
                else if (p.Type == "environment-unavailable")
                {
                    if (p.A==SelectedEnvironmentTargetId)
                        SelectEnvironmentTarget(0);
                    InputDeliveryWarning?.Invoke(
                        "Selected environment member is unavailable; returned to the hub.");
                }
                else if (p.Type == "peer-name" && p.Text is { Length: > 0 and <= 60 } name &&
                    !name.Any(char.IsControl))
                {
                    _environmentHubName=name;
                    if (SelectedEnvironmentTargetId==0)
                    {
                        Volatile.Write(ref _remoteSystemName,name);
                        RemoteNameChanged?.Invoke(name);
                    }
                }
                else if (p.Type == "topology")
                {
                    var layout = MonitorTopology.FromWire(p.Text);
                    if (SelectedEnvironmentTargetId==0)
                    {
                        Volatile.Write(ref _remoteMonitors, layout);
                        RemoteTopologyChanged?.Invoke(layout);
                    }
                }
                else if (p.Type == "placement" &&
                    Enum.TryParse<MonitorEdge>(p.Text, true, out var changedEdge))
                    RemotePlacementChanged?.Invoke(changedEdge);
                else if (p.Type == "pong")
                {
                    long sent = Interlocked.Exchange(ref _lastPingSentTicks, 0);
                    if (sent > 0)
                    {
                        double rtt = (System.Diagnostics.Stopwatch.GetTimestamp() - sent) * 1000.0
                            / System.Diagnostics.Stopwatch.Frequency;
                        Volatile.Write(ref _lastRttMs, rtt);
                    }
                    HeartbeatAcknowledged?.Invoke();
                }
                else if (p.Type is "trust-granted" or "trust-denied")
                    Volatile.Read(ref _pendingTrust)?.TrySetResult(p.Type == "trust-granted");
                else if (p.Type == "android-keyboard-unavailable")
                    InputDeliveryWarning?.Invoke(
                        "Android has no supported focused text field. Tap an editable field on the phone, then type. Check DeskTouch Accessibility if paused.");
                else if (p.Type == "keyboard-unavailable")
                    InputDeliveryWarning?.Invoke("The receiving Windows PC rejected keyboard " +
                        "input. Click a normal text field on that PC and check whether " +
                        "the target program is running as Administrator.");
                else if (p.Type == "udp-channel" && p.A is > 0 and <= 65535 &&
                    !string.IsNullOrEmpty(p.Text))
                {
                    SetupFastUdpClient(p.A, p.B, p.Text);
                }
                else if (p.Type is "granted" or "busy" or "local-active" or "active")
                {
                    if (p.Type == "granted" && Volatile.Read(ref _forwardClaimLease) == p.A)
                        _movePacer?.SetWarmActive(true);
                    else if (p.Type is ("busy" or "local-active"))
                    {
                        Volatile.Write(ref _forwardClaimLease, 0);
                        _movePacer?.SetWarmActive(false);
                        _movePacer?.ClearPending();
                    }
                    AuthorityControlPacket?.Invoke(p);
                    RemoteControlState?.Invoke(p.Type);
                }
                else if (ReverseInputProtocol.TryDecode(p, out _))
                    ProcessReverseInput(p);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (Volatile.Read(ref _closed) == 0)
                _onFailure("Connection closed while monitoring local input priority: " + ex.Message);
        }
    }

    private void EmitEnvironmentVersionsDiagnostic()
    {
        var members = Volatile.Read(ref _environmentMembers);
        string hubVer = _environmentHubMonitors.Monitors.FirstOrDefault()?.AppVersion ?? "?";
        string summary;
        if (members.Length > 0)
        {
            summary = "hub=" + hubVer + "; " + string.Join(", ", members.Select(m =>
            {
                string? ver = null;
                try { ver = MonitorTopology.FromWire(m.Layout).Monitors.FirstOrDefault()?.AppVersion; }
                catch { }
                return $"{m.Name}={ver ?? "?"}";
            }));
        }
        else
        {
            summary = $"{_remoteSystemName ?? "remote"}={hubVer}";
        }
        if (summary == _lastLoggedVersions) return;
        _lastLoggedVersions = summary;
        DiagnosticLog?.Invoke("Peer versions: " + summary);
    }

    private void EmitTelemetryIfActive()
    {
        var pacer = _movePacer;
        var s = pacer?.TakeStats() ?? (UdpDirect: 0L, UdpPacedFlush: 0L, TrailingTcpSync: 0L);
        long tcpFallback = Interlocked.Exchange(ref _tcpFallbackMoves, 0);
        if (s.UdpDirect == 0 && s.UdpPacedFlush == 0 && tcpFallback == 0)
            return;
        bool udpConfirmed = Volatile.Read(ref _udpClient)?.Confirmed == true;
        double rtt = Volatile.Read(ref _lastRttMs);
        DiagnosticLog?.Invoke(
            $"Telemetry [out->{_remoteSystemName ?? "hub"}]: fast_confirmed={udpConfirmed} fast_direct={s.UdpDirect} fast_paced={s.UdpPacedFlush} trailing_sync={s.TrailingTcpSync} tcp_fallback={tcpFallback} rtt_ms={rtt:F1}");
    }

    private async Task HeartbeatAsync()
    {
        try
        {
            int beat = 0;
            bool loggedInitial = false;
            while (!_cancel.IsCancellationRequested)
            {
                await Task.Delay(1500, _cancel.Token);
                if (!loggedInitial)
                {
                    loggedInitial = true;
                    EmitEnvironmentVersionsDiagnostic();
                }
                if (Interlocked.Read(ref _lastPingSentTicks) == 0)
                    Volatile.Write(ref _lastPingSentTicks, System.Diagnostics.Stopwatch.GetTimestamp());
                if (!TrySend(new Packet("ping"))) break;
                if ((++beat & 1) == 0)
                    EmitTelemetryIfActive();
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (Packet p in _queue.Reader.ReadAllAsync(_cancel.Token))
            {
                if (p.Type == "move-flush")
                {
                    Interlocked.Exchange(ref _moveSignaled, 0);
                    var move = Interlocked.Exchange(ref _latestMove, null);
                    if (move != null)
                        await Wire.WriteAsync(_ssl, move, _cancel.Token);
                    continue;
                }
                bool isImmediateControl = p.Type is "button" or "wheel";
                // Rare full queue race: never lose a final movement.
                var udpSync = p.Type is ("release" or "reset" or "claim")
                    ? null
                    : _movePacer?.TakePendingReliableSync(skipIfUdpDelivered: isImmediateControl);
                var tcpLatest = Interlocked.Exchange(ref _latestMove, null);
                var latest = udpSync ?? (isImmediateControl && Volatile.Read(ref _udpAckSent) == 1
                    ? null : tcpLatest);
                if (latest != null)
                    await Wire.WriteAsync(_ssl, latest, _cancel.Token);
                await Wire.WriteAsync(_ssl, p, _cancel.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (Volatile.Read(ref _closed) == 0) _onFailure("Local connection failed: " + ex.Message);
        }
    }

    private void SetupFastUdpClient(int serverPort, int peerId, string encodedKey)
    {
        try
        {
            byte[] key = Convert.FromBase64String(encodedKey);
            if (key.Length != FastUdpCodec.KeySize) return;
            var client = new FastUdpClient(_serverAddress, serverPort, peerId, key);
            client.ConfirmedChanged += () =>
            {
                if (Interlocked.CompareExchange(ref _udpConfirmedLogged, 1, 0) == 0)
                    DiagnosticLog?.Invoke($"Low-latency direct input confirmed ({_serverAddress}:{serverPort}).");
            };
            client.MessageReceived += msg =>
            {
                if (Volatile.Read(ref _closed) != 0) return;
                if (msg.Op is FastUdpOp.Probe or FastUdpOp.ProbeAck)
                {
                    if (Interlocked.CompareExchange(ref _udpAckSent, 1, 0) == 0)
                        TrySend(new Packet("udp-ack"));
                }
                else if (msg.Op == FastUdpOp.ReverseMove)
                {
                    int active = Volatile.Read(ref _reverseLease);
                    if (active > 0 && msg.Lease == active)
                        ProcessReverseInput(new Packet("reverse-move", msg.A, msg.B));
                }
            };
            Interlocked.Exchange(ref _highResTimer, Win.EnterHighResolutionTimer())?.Dispose();
            _movePacer?.SetMinIntervalMs(2.5);
            Interlocked.Exchange(ref _udpClient, client)?.Dispose();
            TrySend(new Packet("udp-ready", client.LocalPort));
            var probeCancel = _cancel.Token;
            _ = Task.Run(async () =>
            {
                // Wait briefly so TCP "udp-ready" lets the hub open its firewall pinhole
                // before our probes arrive, then retry until bidirectional ProbeAck confirms.
                for (int i = 0; i < 20 && !probeCancel.IsCancellationRequested && !client.Confirmed; i++)
                {
                    try { await Task.Delay(i == 0 ? 15 : 120, probeCancel); }
                    catch (OperationCanceledException) { return; }
                    if (!client.Confirmed && ReferenceEquals(Volatile.Read(ref _udpClient), client))
                        client.SendProbe();
                }
                if (!probeCancel.IsCancellationRequested && !client.Confirmed &&
                    ReferenceEquals(Volatile.Read(ref _udpClient), client))
                {
                    DiagnosticLog?.Invoke(
                        $"Low-latency direct channel to {_serverAddress}:{serverPort} did not confirm; using reliable TCP fallback.");
                }
            }, probeCancel);
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        Interlocked.Exchange(ref _pendingTrust, null)?.TrySetResult(false);
        _cancel.Cancel();
        _queue.Writer.TryComplete();
        _movePacer?.Dispose();
        Interlocked.Exchange(ref _udpClient, null)?.Dispose();
        Interlocked.Exchange(ref _highResTimer, null)?.Dispose();
        if (_reversePriority != null)
            _reversePriority.PhysicalOverride -= OnReverseNativeActivity;
        ReleaseReverseAuthority();
        _ssl.Dispose();
        _client.Dispose();
        // Don't dispose the CancellationTokenSource while async workers may still be unwinding.
    }
}
