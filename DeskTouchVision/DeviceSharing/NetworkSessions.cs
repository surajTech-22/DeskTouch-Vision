using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using System.Text.Json;

namespace DeskTouch.DeviceShare;

internal sealed record ConnectedPeer(string Name, MonitorTopology Monitors);

internal sealed record PeerHello(string Name, string Layout);

internal sealed class ReceiverServer : IDisposable
{
    private readonly Action<string> _status;
    private readonly CancellationTokenSource _cancel = new();
    private readonly TcpListener _listener = new(IPAddress.Any, Wire.Port);
    private readonly X509Certificate2 _certificate;
    private readonly RSA _key;
    private Task? _serverTask;
    private int _active;
    private readonly LocalInputPriority _priority;
    private readonly Func<string, CancellationToken, Task<bool>>? _requestApproval;
    private readonly object _inputGate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private InputInjector? _currentInjector;
    private SslStream? _currentStream;
    private TcpClient? _currentClient;
    internal event Action<ConnectedPeer>? PeerConnected;
    internal event Action? PeerDisconnected;
    internal event Action<ConnectedPeer>? PeerTopologyChanged;
    internal event Action<MonitorEdge>? PeerPlacementChanged;
    internal string Fingerprint { get; }
    internal string Secret { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    internal ReceiverServer(Action<string> status, LocalInputPriority priority,
        Func<string, CancellationToken, Task<bool>>? requestApproval = null)
    {
        _status = status;
        _priority = priority;
        _requestApproval = requestApproval;
        _priority.PhysicalOverride += OnPhysicalOverride;
        _key = RSA.Create(2048);
        var req = new CertificateRequest("CN=DeskTouch-LAN", _key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        var serverUsage = new OidCollection();
        serverUsage.Add(new Oid("1.3.6.1.5.5.7.3.1")); // TLS server authentication
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(serverUsage, false));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

        // CRITICAL Windows fix: Schannel cannot use .NET's ephemeral RSA key
        // directly for SslStream server authentication ("platform does not
        // support ephemeral keys"). Reimport a password-protected PKCS#12
        // into CURRENT USER Windows key storage, never machine-wide. The key
        // is NOT kept permanently after the certificate is disposed.
        using var temporaryCertificate = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(3));
        string pfxPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        byte[] pkcs12 = temporaryCertificate.Export(X509ContentType.Pkcs12, pfxPassword);
        try
        {
            _certificate = new X509Certificate2(pkcs12, pfxPassword,
                X509KeyStorageFlags.UserKeySet);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs12);
        }
        if (!_certificate.HasPrivateKey)
            throw new CryptographicException("TLS server certificate private key unavailable.");
        Fingerprint = Convert.ToHexString(SHA256.HashData(_certificate.RawData));
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
        _status("Local physical keyboard/mouse has priority; remote input paused.");
        _ = Task.Run(async () =>
        {
            try { await SendControlAsync(new Packet("local-active"), _cancel.Token); }
            catch (Exception) { } // Source connection may already have ended.
        });
    }

    internal void DisconnectPeer() => Volatile.Read(ref _currentClient)?.Close();

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
        return new ConnectedPeer(name, MonitorTopology.FromWire(hello.Layout));
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
        bool approved = request.Type == "auth" &&
            ConstantTimeSecret(request.Text, Secret);
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
            incoming = peer;
            Volatile.Write(ref _currentClient, client);
            PeerConnected?.Invoke(peer);
        }
        _status("Authenticated Windows peer connected; waiting for requested control.");
        var injector = new InputInjector();
        Volatile.Write(ref _currentInjector, injector);
        Volatile.Write(ref _currentStream, ssl);
        try
        {
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
                if (p.Type == "topology")
                {
                    var peerLayout = MonitorTopology.FromWire(p.Text);
                    incoming = incoming! with { Monitors = peerLayout };
                    PeerTopologyChanged?.Invoke(incoming);
                    continue;
                }
                if (p.Type == "placement" &&
                    Enum.TryParse<MonitorEdge>(p.Text, true, out var sourceEdge))
                {
                    PeerPlacementChanged?.Invoke(sourceEdge);
                    continue;
                }

                if (p.Type == "claim")
                {
                    bool granted = _priority.TryClaim();
                    // Respond explicitly; the source must not capture the
                    // keyboard until an authenticated grant is received.
                    await SendControlAsync(new Packet(granted ? "granted" : "busy"), token);
                    continue;
                }
                if (p.Type == "reset" || p.Type == "release")
                {
                    _priority.ReleaseRemote();
                    lock (_inputGate) injector.ReleaseAll();
                    continue;
                }
                if (p.Type == "ping") continue;
                lock (_inputGate)
                {
                    // Every input is gated by LOCAL native priority. A newly
                    // pressed local keyboard wins even during a live session.
                    if (_priority.CanApplyRemote) injector.Handle(p);
                }
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
            _priority.ReleaseRemote();
            Volatile.Write(ref _currentStream, null);
            lock (_inputGate)
            {
                injector.ReleaseAll();
                Volatile.Write(ref _currentInjector, null);
            }
            Volatile.Write(ref _currentClient, null);
            PeerDisconnected?.Invoke();
            _status("Peer connection ended. Native keyboard/mouse always available.");
        }
    }

    public void Dispose()
    {
        _priority.PhysicalOverride -= OnPhysicalOverride;
        _priority.ReleaseRemote();
        _cancel.Cancel();
        _listener.Stop();
        DisconnectPeer();
        _certificate.Dispose();
        _key.Dispose();
        _cancel.Dispose();
    }
}

internal sealed class HostSession : IDisposable
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

    private static async Task<HostSession> ConnectCoreAsync(
        string ip, string pinnedFingerprint, string secret, bool askReceiver,
        Action<string> onFailure, CancellationToken cancellation)
    {
        pinnedFingerprint = new string(pinnedFingerprint.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        secret = secret.Trim().ToUpperInvariant();
        if (pinnedFingerprint.Length != 64 || !pinnedFingerprint.All(Uri.IsHexDigit))
            throw new ArgumentException("Enter the receiver's complete 64-digit SHA-256 fingerprint.");
        if (!askReceiver && (secret.Length != 32 || !secret.All(Uri.IsHexDigit)))
            throw new ArgumentException("Enter the receiver's 32-digit pairing secret.");
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
                askReceiver ? "auth-request" : "auth",
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
            var hello = JsonSerializer.Serialize(
                new PeerHello(Environment.MachineName, mine.ToWire()));
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
                else if (p.Type is "granted" or "busy" or "local-active")
                    RemoteControlState?.Invoke(p.Type);
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
        _cancel.Cancel();
        _queue.Writer.TryComplete();
        _ssl.Dispose();
        _client.Dispose();
        // Don't dispose the CancellationTokenSource while async workers may still be unwinding.
    }
}
