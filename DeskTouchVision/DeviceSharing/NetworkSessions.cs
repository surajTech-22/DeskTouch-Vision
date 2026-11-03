using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;

namespace DeskTouch.DeviceShare;

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
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        _certificate = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(3));
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
        lock (_inputGate) _currentInjector?.ReleaseAll();
        _status("Local physical keyboard/mouse has priority; remote input paused.");
        _ = Task.Run(async () =>
        {
            try { await SendControlAsync(new Packet("local-active"), _cancel.Token); }
            catch (Exception) { } // Source connection may already have ended.
        });
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
                receiveDeadline.CancelAfter(TimeSpan.FromSeconds(6));
                var p = await Wire.ReadAsync(ssl, receiveDeadline.Token);
                if (p.Type == "goodbye") break;

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
        catch (EndOfStreamException) { }
        catch (IOException) { }
        catch (OperationCanceledException) { } // shut down or heartbeat timed out
        finally
        {
            _priority.ReleaseRemote();
            Volatile.Write(ref _currentStream, null);
            lock (_inputGate)
            {
                injector.ReleaseAll();
                Volatile.Write(ref _currentInjector, null);
            }
            _status("Peer disconnected. Native keyboard/mouse always available.");
        }
    }

    public void Dispose()
    {
        _priority.PhysicalOverride -= OnPhysicalOverride;
        _priority.ReleaseRemote();
        _cancel.Cancel();
        _listener.Stop();
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
    internal MonitorTopology RemoteMonitors { get; }
    internal event Action<string>? RemoteControlState;

    private HostSession(TcpClient client, SslStream ssl,
        MonitorTopology monitors, Action<string> onFailure)
    {
        _client = client; _ssl = ssl; RemoteMonitors = monitors; _onFailure = onFailure;
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
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            await client.ConnectAsync(address, Wire.Port, timeout.Token);
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
            var monitors = MonitorTopology.FromWire(ready.Text);
            if (monitors.Bounds != new System.Drawing.Rectangle(ready.A, ready.B, ready.C, ready.D))
                throw new AuthenticationException("Receiver monitor geometry failed validation.");
            return new HostSession(client, ssl, monitors, onFailure);
        }
        catch
        {
            ssl?.Dispose();
            client.Dispose();
            throw;
        }
    }

    internal bool TrySend(Packet p)
    {
        if (Volatile.Read(ref _closed) != 0) return false;
        if (_queue.Writer.TryWrite(p)) return true;
        _onFailure("Input queue overloaded: local controls restored for safety.");
        return false;
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (!_cancel.IsCancellationRequested)
            {
                var p = await Wire.ReadAsync(_ssl, _cancel.Token);
                if (p.Type is "granted" or "busy" or "local-active")
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
                await Wire.WriteAsync(_ssl, p, _cancel.Token);
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
