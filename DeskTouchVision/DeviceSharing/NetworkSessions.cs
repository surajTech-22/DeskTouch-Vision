using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using System.Text.Json;

namespace DeskTouch.DeviceShare;

internal sealed record ConnectedPeer(string Name, MonitorTopology Monitors, long SessionId = 0, string? DeviceFingerprint = null);

internal sealed record PeerHello(string Name, string Layout, string? Fingerprint = null);

internal sealed class PeerReceiverSession : IInputSession, IDisposable
{
    private readonly Action<string> _status;
    private readonly CancellationTokenSource _cancel = new();
    private readonly TcpListener _listener = new(IPAddress.Any, Wire.Port);
    private readonly X509Certificate2 _certificate;
    private readonly TrustedDeviceStore? _trusted;
    private Task? _serverTask;
    private int _active;
    private readonly LocalInputPriority _priority;
    private readonly Func<string, CancellationToken, Task<bool>>? _requestApproval;
    private readonly Func<string, CancellationToken, Task<bool>>? _requestTrustApproval;
    private readonly object _inputGate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private InputInjector? _currentInjector;
    private SslStream? _currentStream;
    private TcpClient? _currentClient;
    private MonitorTopology? _connectedRemoteMonitors;
    private Channel<Packet>? _reverseQueue;
    private Packet? _latestReverseMove;
    private int _reverseMoveSignaled;
    private int _forwardLease;
    private int _forwardAppliedLease;
    internal Func<bool>? AllowForwardClaim;
    internal event Action<Packet>? ReverseControlMessage;
    internal event Action<ConnectedPeer>? PeerConnected;
    internal event Action<long>? PeerDisconnected;
    internal event Action<ConnectedPeer>? PeerTopologyChanged;
    internal event Action<MonitorEdge>? PeerPlacementChanged;
    internal string Fingerprint { get; }
    internal string Secret { get; }
    private readonly long _sessionId;
    private readonly SharedPeerAuthority _authority;

    internal PeerReceiverSession(Action<string> status, LocalInputPriority priority,
        Func<string, CancellationToken, Task<bool>>? requestApproval = null,
        TrustedDeviceStore? trusted = null,
        Func<string, CancellationToken, Task<bool>>? requestTrustApproval = null,
        string? identityPath = null,
        long sessionId = 0, SharedPeerAuthority? authority = null, string? sharedSecret = null)
    {
        _sessionId = sessionId;
        _authority = authority ?? new SharedPeerAuthority(priority);
        Secret = sharedSecret ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _status = status;
        _priority = priority;
        _requestApproval = requestApproval;
        _trusted = trusted;
        _requestTrustApproval = requestTrustApproval;
        _priority.PhysicalOverride += OnPhysicalOverride;
        // Stable TLS identity is required for pinning across restart/updates.
        // It lives in a DPAPI-encrypted file scoped to the Windows user.
        _certificate = PersistentReceiverIdentity.LoadOrCreate(identityPath);
        Fingerprint = Convert.ToHexString(SHA256.HashData(_certificate.RawData));
    }

    // Discovery must not advertise a Windows PC whose TCP server is gone,
    // already paired, or transitioning to an outgoing controller session.
    // Being visible over UDP is never proof that port 37377 can be accepted.
    internal bool CanAcceptNewPeer
    {
        get
        {
            if (_cancel.IsCancellationRequested || Volatile.Read(ref _active) != 0)
                return false;
            try { return _listener.Server.IsBound; }
            catch (ObjectDisposedException) { return false; }
        }
    }

    internal void Start()
    {
        _listener.Start(1);
        _serverTask = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (!_cancel.IsCancellationRequested)
        {
            try
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync(_cancel.Token);
                client.NoDelay = true;
                if (Interlocked.Exchange(ref _active, 1) == 1) continue;
                try { await ServeAsync(client, _cancel.Token); }
                finally { Volatile.Write(ref _active, 0); }
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { _status("Receiver connection closed: " + ex.Message); }
        }
    }

    internal Task ServeClientAsync(TcpClient client, CancellationToken token) =>
        ServeAsync(client, token);

    private static bool ConstantTimeSecret(string? got, string expected)
    {
        if (got == null || got.Length != expected.Length) return false;
        byte[] a = System.Text.Encoding.ASCII.GetBytes(got);
        byte[] b = System.Text.Encoding.ASCII.GetBytes(expected);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    private async Task SendControlAsync(Packet message, CancellationToken token)
    {
        var ssl = Volatile.Read(ref _currentStream);
        if (ssl == null) return;
        await _writeGate.WaitAsync(token);
        try { await Wire.WriteAsync(ssl, message, token); }
        finally { _writeGate.Release(); }
    }

    private void OnPhysicalOverride()
    {
        // Native activity immediately blocks new remote inputs before any
        // asynchronous notification. Never await network I/O in an input hook.
        // A physical event always revokes the grant synchronously. Release
        // injected keys away from the low-level keyboard/mouse callback thread.
        _ = Task.Run(() =>
        {
            lock (_inputGate) _currentInjector?.ReleaseAll();
        });
        int revokedLease = Interlocked.Exchange(ref _forwardLease, 0);
        _authority.Release(_sessionId);
        Interlocked.Exchange(ref _forwardAppliedLease, 0);
        _status("Local physical keyboard/mouse has priority; forward authority revoked.");
        _ = Task.Run(async () =>
        {
            try { await SendControlAsync(new Packet("local-active", revokedLease), _cancel.Token); }
            catch (Exception) { } // Source connection may already have ended.
        });
    }

    internal void DisconnectPeer() => Volatile.Read(ref _currentClient)?.Close();

    internal MonitorTopology RemoteMonitors =>
        Volatile.Read(ref _connectedRemoteMonitors) ??
        throw new InvalidOperationException("No authenticated peer monitor layout yet.");

    MonitorTopology IInputSession.RemoteMonitors => RemoteMonitors;
    bool IInputSession.TrySend(Packet packet) => TrySendReverse(packet);

    internal bool TrySendReverse(Packet packet)
    {
        if (Volatile.Read(ref _currentStream) == null) return false;
        var queue = Volatile.Read(ref _reverseQueue);
        if (queue == null) return false;
        // Do not put a packet per pixel into the network; keep the newest
        // coordinate, like HostSession's forward-direction send loop.
        if (packet.Type == "move")
        {
            Interlocked.Exchange(ref _latestReverseMove, packet);
            if (Interlocked.CompareExchange(ref _reverseMoveSignaled, 1, 0) == 0 &&
                !queue.Writer.TryWrite(new Packet("move-flush")))
            {
                Interlocked.Exchange(ref _reverseMoveSignaled, 0);
                return false;
            }
            return true;
        }
        if (queue.Writer.TryWrite(packet)) return true;
        _status("Reverse input control queue stalled; reconnecting to restore local authority.");
        DisconnectPeer();
        return false;
    }

    private async Task ReverseSendLoopAsync(Channel<Packet> queue, CancellationToken token)
    {
        try
        {
            await foreach (var queued in queue.Reader.ReadAllAsync(token))
            {
                if (queued.Type == "move-flush")
                {
                    Interlocked.Exchange(ref _reverseMoveSignaled, 0);
                    var move = Interlocked.Exchange(ref _latestReverseMove, null);
                    if (move != null)
                        await SendControlAsync(ReverseInputProtocol.Encode(move), token);
                    continue;
                }
                var last = Interlocked.Exchange(ref _latestReverseMove, null);
                if (last != null)
                    await SendControlAsync(ReverseInputProtocol.Encode(last), token);
                await SendControlAsync(ReverseInputProtocol.Encode(queued), token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _status("Reverse input transport stopped: " + ex.Message);
            DisconnectPeer(); // cannot leave the remote host holding keys
        }
    }

    internal void PublishPlacement(MonitorEdge side)
    {
        if (Volatile.Read(ref _currentStream) == null) return;
        _ = Task.Run(async () =>
        {
            try { await SendControlAsync(new Packet("placement", Text: side.ToString()), _cancel.Token); }
            catch (Exception) { }
        });
    }

    internal void PublishLocalTopology(MonitorTopology monitors)
    {
        if (Volatile.Read(ref _currentStream) == null) return;
        _ = Task.Run(async () =>
        {
            try { await SendControlAsync(new Packet("topology", Text: monitors.ToWire()), _cancel.Token); }
            catch (Exception) { /* session cleanup handles half-open transport */ }
        });
    }

    private static ConnectedPeer ReadHello(string? helloJson)
    {
        if (string.IsNullOrWhiteSpace(helloJson) || helloJson.Length > 6000)
            throw new InvalidDataException("Peer did not describe its monitor layout.");
        var hello = JsonSerializer.Deserialize<PeerHello>(helloJson)
            ?? throw new InvalidDataException("Unrecognized peer hello.");
        string name = hello.Name.Trim();
        if (name.Length is < 1 or > 60 || name.Any(char.IsControl))
            throw new InvalidDataException("Invalid peer computer name.");
        // The claimed fingerprint is a display-group hint only; never treat
        // it as TLS authentication or use it to grant keyboard/mouse control.
        string? hint = hello.Fingerprint is { Length: 64 } fp &&
            fp.All(Uri.IsHexDigit) ? fp.ToUpperInvariant() : null;
        return new ConnectedPeer(name, MonitorTopology.FromWire(hello.Layout),
            DeviceFingerprint: hint);
    }

    private async Task ServeAsync(TcpClient client, CancellationToken token)
    {
        using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
        using var handshakeLimit = CancellationTokenSource.CreateLinkedTokenSource(token);
        handshakeLimit.CancelAfter(TimeSpan.FromSeconds(12));
        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = _certificate,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ClientCertificateRequired = false,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        }, handshakeLimit.Token);
        var request = await Wire.ReadAsync(ssl, handshakeLimit.Token);
        var trustedSource = request.Type == "auth-trusted"
            ? _trusted?.AuthenticateIncoming(request.Text) : null;
        bool approved = trustedSource != null ||
            (request.Type == "auth" && ConstantTimeSecret(request.Text, Secret));
        if (!approved && request.Type == "auth-request" && _requestApproval != null)
        {
            // Discovery advertises only a public certificate fingerprint.
            // Any first connection must be explicitly approved on this PC.
            using var approvalLimit = CancellationTokenSource.CreateLinkedTokenSource(token);
            approvalLimit.CancelAfter(TimeSpan.FromSeconds(40));
            string peerIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString()
                ?? "Unknown address";
            string claimedName = (request.Text ?? "Unknown PC").Trim();
            if (claimedName.Length > 60) claimedName = claimedName[..60];
            try { approved = await _requestApproval(
                $"{claimedName} ({peerIp})", approvalLimit.Token); }
            catch (OperationCanceledException) { approved = false; }
        }
        if (!approved)
        {
            _status("Unapproved pairing attempt rejected. No remote input granted.");
            await Wire.WriteAsync(ssl, new Packet("denied"), token);
            return;
        }

        var topology = MonitorTopology.Capture();
        var screen = topology.Bounds;
        await Wire.WriteAsync(ssl, new Packet("ready", screen.Left, screen.Top,
            screen.Width, screen.Height, topology.ToWire()), token);
        // BOTH PCs receive their peer's monitor layout. The original sender
        // alone had a remote display group; the receiver never got a hello.
        ConnectedPeer? incoming = null;
        using (var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            helloTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            var helloPacket = await Wire.ReadAsync(ssl, helloTimeout.Token);
            if (helloPacket.Type != "hello")
                throw new InvalidDataException("Missing authenticated peer hello.");
            var peer = ReadHello(helloPacket.Text);
            // Treat advertised names as labels, not identities. For automatic
            // trusted sessions, use the name stored when trust was approved.
            if (trustedSource != null)
                peer = peer with { Name = trustedSource.Name };
            incoming = peer with { SessionId = _sessionId };
            Volatile.Write(ref _connectedRemoteMonitors, incoming.Monitors);
            Volatile.Write(ref _currentClient, client);
        }
        _status("Authenticated Windows peers connected; both computers may request input authority.");
        var injector = new InputInjector();
        var reverseQueue = Channel.CreateBounded<Packet>(
            new BoundedChannelOptions(2048) { SingleReader = true,
                SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
        Volatile.Write(ref _currentInjector, injector);
        Volatile.Write(ref _currentStream, ssl);
        Volatile.Write(ref _reverseQueue, reverseQueue);
        using var sessionCancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        _ = Task.Run(() => ReverseSendLoopAsync(reverseQueue, sessionCancel.Token));
        try
        {
            // Include UI notification in the protected session lifetime.
            // Even a failing subscriber must release the TLS socket, reverse
            // input queue and all synthetic held keys in the finally block.
            PeerConnected?.Invoke(incoming!);
            while (!token.IsCancellationRequested)
            {
                // If Wi-Fi disappears without closing TCP, release any
                // remotely held keys/buttons rather than retaining control.
                // The source sends keepalive every 1.5 seconds.
                using var receiveDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                // A six-second watchdog caused unnecessary disconnects on
                // congested Windows/Wi-Fi systems despite a live pairing.
                // The sender pings every 1.5 s. 18 s permits brief stalls but
                // still releases remote held keys after an actual link failure.
                receiveDeadline.CancelAfter(TimeSpan.FromSeconds(18));
                var p = await Wire.ReadAsync(ssl, receiveDeadline.Token);
                if (p.Type == "goodbye") break;
                if (p.Type == "trust-request")
                {
                    bool allowed = false;
                    if (_trusted != null && _requestTrustApproval != null &&
                        p.Text is { Length: 64 } proposal &&
                        proposal.All(Uri.IsHexDigit))
                    {
                        using var approvalLimit = CancellationTokenSource.CreateLinkedTokenSource(token);
                        approvalLimit.CancelAfter(TimeSpan.FromSeconds(50));
                        try
                        {
                            allowed = await _requestTrustApproval(incoming!.Name,
                                approvalLimit.Token);
                            if (allowed) _trusted.AllowIncoming(incoming!.Name, proposal);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _status("Cannot save device trust: " + ex.Message);
                            allowed = false;
                        }
                        catch (OperationCanceledException) { allowed = false; }
                    }
                    await SendControlAsync(new Packet(
                        allowed ? "trust-granted" : "trust-denied"), token);
                    continue;
                }
                if (p.Type == "topology")
                {
                    var peerLayout = MonitorTopology.FromWire(p.Text);
                    incoming = incoming! with { Monitors = peerLayout };
                    Volatile.Write(ref _connectedRemoteMonitors, peerLayout);
                    PeerTopologyChanged?.Invoke(incoming);
                    continue;
                }
                if (p.Type == "placement" &&
                    Enum.TryParse<MonitorEdge>(p.Text, true, out var sourceEdge))
                {
                    PeerPlacementChanged?.Invoke(sourceEdge);
                    continue;
                }

                if (p.Type is "reverse-granted" or "reverse-busy" or
                    "reverse-local-active" or "reverse-active")
                {
                    ReverseControlMessage?.Invoke(p);
                    continue;
                }
                if (p.Type == "claim")
                {
                    // The receiver may itself be controlling the source PC.
                    // Never issue forward and reverse authority at once.
                    bool allowed = p.A > 0 && (AllowForwardClaim?.Invoke() ?? true);
                    bool granted = allowed && _authority.TryClaim(_sessionId);
                    if (granted)
                    {
                        Volatile.Write(ref _forwardLease, p.A);
                        Volatile.Write(ref _forwardAppliedLease, 0);
                    }
                    await SendControlAsync(new Packet(granted ? "granted" : "busy", p.A), token);
                    continue;
                }
                if (p.Type == "reset" ||
                    (p.Type == "release" && p.A > 0 && p.A == Volatile.Read(ref _forwardLease)))
                {
                    Interlocked.Exchange(ref _forwardLease, 0);
                    Interlocked.Exchange(ref _forwardAppliedLease, 0);
                    _authority.Release(_sessionId);
                    lock (_inputGate) injector.ReleaseAll();
                    continue;
                }
                if (p.Type == "ping")
                {
                    // Android requires a reply to its own heartbeat. Prior
                    // Windows code silently ignored pings; Android's 22-second
                    // read timeout therefore tore down healthy connections.
                    await SendControlAsync(new Packet("pong"), token);
                    continue;
                }
                int lease = Volatile.Read(ref _forwardLease);
                bool cursorApplied = false;
                lock (_inputGate)
                {
                    if (lease > 0 && _authority.IsOwner(_sessionId) && _priority.CanApplyRemote &&
                        p.Type is ("move" or "button" or "key" or "wheel"))
                    {
                        // ACK only after SendInput actually succeeded.
                        // If Windows refused injection, the source must time
                        // out and retain its local mouse/key authority.
                        bool appliedToWindows = injector.Handle(p);
                        cursorApplied = appliedToWindows && p.Type == "move";
                    }
                }
                if (cursorApplied && _authority.IsOwner(_sessionId) && _priority.CanApplyRemote &&
                    Volatile.Read(ref _forwardLease) == lease &&
                    Interlocked.CompareExchange(ref _forwardAppliedLease, lease, 0) == 0)
                    await SendControlAsync(new Packet("active", lease), token);
            }
        }
        catch (EndOfStreamException)
        {
            _status("Remote peer closed the TCP connection.");
        }
        catch (IOException ex)
        {
            _status("Remote peer network stream ended: " + ex.Message);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            _status("No peer heartbeat for 18 seconds; releasing any held input for safety.");
        }
        catch (OperationCanceledException) { } // shutdown
        finally
        {
            sessionCancel.Cancel();
            reverseQueue.Writer.TryComplete();
            Volatile.Write(ref _reverseQueue, null);
            Volatile.Write(ref _connectedRemoteMonitors, null);
            Interlocked.Exchange(ref _latestReverseMove, null);
            Interlocked.Exchange(ref _reverseMoveSignaled, 0);
            Interlocked.Exchange(ref _forwardLease, 0);
            Interlocked.Exchange(ref _forwardAppliedLease, 0);
            _authority.Release(_sessionId);
            Volatile.Write(ref _currentStream, null);
            lock (_inputGate)
            {
                injector.ReleaseAll();
                Volatile.Write(ref _currentInjector, null);
            }
            Volatile.Write(ref _currentClient, null);
            PeerDisconnected?.Invoke(_sessionId);
            _status("Peer connection ended. Native keyboard/mouse always available.");
        }
    }

    public void Dispose()
    {
        _priority.PhysicalOverride -= OnPhysicalOverride;
        _authority.Release(_sessionId);
        _cancel.Cancel();
        _listener.Stop();
        DisconnectPeer();
        _certificate.Dispose();
        _cancel.Dispose();
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
            using var localIdentity = PersistentReceiverIdentity.LoadOrCreate();
            string sourceId = Convert.ToHexString(
                SHA256.HashData(localIdentity.RawData));
            var hello = JsonSerializer.Serialize(
                new PeerHello(Environment.MachineName, mine.ToWire(), sourceId));
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
                if (p.Type == "topology")
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
