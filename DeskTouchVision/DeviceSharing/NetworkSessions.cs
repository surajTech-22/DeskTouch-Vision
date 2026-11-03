using System.Collections.Concurrent;
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
    string Platform = "Windows");
internal sealed record PeerHello(string Name, string Layout,
    bool? ReverseInputSupported = null, string? Fingerprint = null,
    string? Platform = null);

/// <summary>
/// A small authenticated LAN hub. Up to eight PCs/phones can remain connected
/// through the same existing TLS protocol. Only one peer holds the input lease.
/// The currently selected peer is the target for Windows-to-peer reverse input;
/// switching to another peer is explicit when that peer requests authority.
/// </summary>
internal sealed class ReceiverServer : IInputSession, IDisposable
{
    private const int MaxPeers = 8;
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
        internal int Lease;
        internal int AppliedLease;
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
    private Task? _serverTask;
    private int _nextClientId;
    private int _pendingAndActive;
    private Session? _selected;
    private Session? _owner;
    private bool _disposed;

    internal Func<bool>? AllowForwardClaim;
    internal event Action<Packet>? ReverseControlMessage;
    internal event Action<ConnectedPeer>? PeerConnected;
    internal event Action? PeerListChanged;
    internal event Action? PeerDisconnected;
    internal event Action<ConnectedPeer>? PeerTopologyChanged;
    internal event Action<MonitorEdge>? PeerPlacementChanged;
    internal string Fingerprint { get; }
    internal string Secret { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    internal int ConnectedPeerCount => _sessions.Count;
    internal static bool AllowsForwardInput(ConnectedPeer peer) =>
        peer.Platform != "Android";
    internal IReadOnlyList<(int Id, ConnectedPeer Peer)> ConnectedPeers =>
        _sessions.Values.OrderBy(s => s.Id).Select(s => (s.Id, s.Peer)).ToArray();
    internal int SelectedPeerId => Volatile.Read(ref _selected)?.Id ?? 0;
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
        _listener.Start(MaxPeers);
        _serverTask = Task.Run(LoopAsync);
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

    internal bool TrySendReverse(Packet packet)
    {
        Session? selected = Volatile.Read(ref _selected);
        if (selected == null) return false;
        if (packet.Type == "move")
        {
            Interlocked.Exchange(ref selected.LatestReverseMove, packet);
            if (Interlocked.CompareExchange(ref selected.ReverseMoveSignaled, 1, 0) == 0 &&
                !selected.ReverseQueue.Writer.TryWrite(new Packet("move-flush")))
            {
                Interlocked.Exchange(ref selected.ReverseMoveSignaled, 0);
                return false;
            }
            return true;
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
                // Never allow an old peer's queued reverse input to be sent
                // to the new peer after selection changes.
                if (!ReferenceEquals(Volatile.Read(ref _selected), session)) continue;
                if (p.Type == "move-flush")
                {
                    Interlocked.Exchange(ref session.ReverseMoveSignaled, 0);
                    var move = Interlocked.Exchange(ref session.LatestReverseMove, null);
                    if (move != null)
                        await SendToAsync(session, ReverseInputProtocol.Encode(move), token);
                    continue;
                }
                var prior = Interlocked.Exchange(ref session.LatestReverseMove, null);
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

    private static ConnectedPeer ReadHello(string? value)
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
        return new ConnectedPeer(name, MonitorTopology.FromWire(hello.Layout),
            hello.ReverseInputSupported ?? true, hint, platform);
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
        // The authenticated hello carries the current computer/phone name.
        // A saved trusted-device alias may be stale after the PC was renamed;
        // keep the actual announced system name in the display diagram.
        var session=new Session{Id=id,Client=client,Stream=ssl,Peer=peer};
        if(!_sessions.TryAdd(id,session))
            throw new InvalidOperationException("Duplicate peer session ID");
        PeerListChanged?.Invoke();
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(token);
        var sendTask=Task.Run(()=>ReverseSendLoopAsync(session,linked.Token));
        try
        {
            bool first;
            lock(_authorityGate) first=_selected==null;
            if(first) Select(session);
            _status("Connected: "+peer.Name+" ("+ConnectedPeerCount+" simultaneous devices).");
            // The server advertises its CURRENT system name after successful
            // pairing. Do not rely on a stale saved alias on the other PC.
            await SendToAsync(session,new Packet("peer-name",
                Text:Environment.MachineName),token);
            while(!token.IsCancellationRequested)
            {
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(18));
                var p=await Wire.ReadAsync(ssl,deadline.Token);
                if(p.Type=="goodbye") break;
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
                    session.Peer=session.Peer with {Monitors=MonitorTopology.FromWire(p.Text)};
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
                if(p.Type is "reverse-granted" or "reverse-busy" or
                    "reverse-local-active" or "reverse-active")
                {
                    if(ReferenceEquals(Volatile.Read(ref _selected),session))
                        ReverseControlMessage?.Invoke(p);
                    continue;
                }
                if(p.Type=="claim")
                {
                    bool granted=false;
                    lock(_authorityGate)
                    {
                        // One-way Android mode: never accept Android->Windows
                        // keyboard/mouse claims. Android remains paired for
                        // Windows->Android control via reverse-* packets.
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
                    if(granted && !Select(session))
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
                    // The Android client expects a server response within 22s.
                    // Older Windows releases ignored all mobile heartbeats,
                    // causing a healthy connection to time out after 22s.
                    await SendToAsync(session,new Packet("pong"),token);
                    continue;
                }
                int lease;
                lock(_authorityGate)
                    lease=ReferenceEquals(_owner,session)?session.Lease:0;
                bool applied=false;
                if(lease>0 && _priority.CanApplyRemote &&
                   p.Type is ("move" or "button" or "key" or "wheel"))
                {
                    lock(session.InputLock)
                    {
                        lock(_authorityGate)
                        {
                            if(ReferenceEquals(_owner,session) && lease==session.Lease &&
                               _priority.CanApplyRemote)
                                applied=session.Injector.Handle(p);
                        }
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

    public void Dispose()
    {
        if(_disposed)return;
        _disposed=true;
        _priority.PhysicalOverride -= OnPhysicalOverride;
        _cancel.Cancel();
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
    internal event Action? HeartbeatAcknowledged;
    MonitorTopology IInputSession.RemoteMonitors => RemoteMonitors;
    bool IInputSession.TrySend(Packet packet) => TrySend(packet);

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

    private HostSession(TcpClient client, SslStream ssl,
        MonitorTopology monitors, Action<string> onFailure)
    {
        _client = client; _ssl = ssl; _remoteMonitors = monitors; _onFailure = onFailure;
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
            return new HostSession(client, ssl, monitors, onFailure);
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

    internal bool TrySend(Packet p)
    {
        if (Volatile.Read(ref _closed) != 0) return false;
        if (p.Type == "move")
        {
            // Keep the newest pointer coordinate rather than queuing every
            // intermediate movement. A full move queue previously caused
            // disconnect loops and seconds of unwanted pointer lag.
            Interlocked.Exchange(ref _latestMove, p);
            if (Interlocked.CompareExchange(ref _moveSignaled, 1, 0) == 0 &&
                !_queue.Writer.TryWrite(new Packet("move-flush")))
                Interlocked.Exchange(ref _moveSignaled, 0);
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
                if (p.Type == "peer-name" && p.Text is { Length: > 0 and <= 60 } name &&
                    !name.Any(char.IsControl))
                    RemoteNameChanged?.Invoke(name);
                else if (p.Type == "topology")
                {
                    var layout = MonitorTopology.FromWire(p.Text);
                    Volatile.Write(ref _remoteMonitors, layout);
                    RemoteTopologyChanged?.Invoke(layout);
                }
                else if (p.Type == "placement" &&
                    Enum.TryParse<MonitorEdge>(p.Text, true, out var changedEdge))
                    RemotePlacementChanged?.Invoke(changedEdge);
                else if (p.Type == "pong")
                    HeartbeatAcknowledged?.Invoke();
                else if (p.Type is "trust-granted" or "trust-denied")
                    Volatile.Read(ref _pendingTrust)?.TrySetResult(p.Type == "trust-granted");
                else if (p.Type is "granted" or "busy" or "local-active" or "active")
                {
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

    private async Task HeartbeatAsync()
    {
        try
        {
            while (!_cancel.IsCancellationRequested)
            {
                await Task.Delay(1500, _cancel.Token);
                if (!TrySend(new Packet("ping"))) break;
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
                // Rare full queue race: never lose a final movement.
                var latest = Interlocked.Exchange(ref _latestMove, null);
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        Interlocked.Exchange(ref _pendingTrust, null)?.TrySetResult(false);
        _cancel.Cancel();
        _queue.Writer.TryComplete();
        if (_reversePriority != null)
            _reversePriority.PhysicalOverride -= OnReverseNativeActivity;
        ReleaseReverseAuthority();
        _ssl.Dispose();
        _client.Dispose();
        // Don't dispose the CancellationTokenSource while async workers may still be unwinding.
    }
}
