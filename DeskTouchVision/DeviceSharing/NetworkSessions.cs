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
    internal string Fingerprint { get; }
    internal string Secret { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    internal ReceiverServer(Action<string> status)
    {
        _status = status;
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
        if (request.Type != "auth" || !ConstantTimeSecret(request.Text, Secret))
        {
            _status("Pairing rejected; check the 32-character secret.");
            await Task.Delay(250, token);
            return;
        }

        var topology = MonitorTopology.Capture();
        var screen = topology.Bounds;
        await Wire.WriteAsync(ssl, new Packet("ready", screen.Left, screen.Top,
            screen.Width, screen.Height, topology.ToWire()), token);
        _status("Authenticated Windows peer connected.");
        var injector = new InputInjector();
        try
        {
            while (!token.IsCancellationRequested)
            {
                var p = await Wire.ReadAsync(ssl, token);
                if (p.Type == "goodbye") break;
                if (p.Type == "reset") { injector.ReleaseAll(); continue; }
                injector.Handle(p);
            }
        }
        catch (EndOfStreamException) { }
        catch (IOException) { }
        finally
        {
            injector.ReleaseAll();
            _status("Peer disconnected. All held keys/buttons released.");
        }
    }

    public void Dispose()
    {
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

    private HostSession(TcpClient client, SslStream ssl,
        MonitorTopology monitors, Action<string> onFailure)
    {
        _client = client; _ssl = ssl; RemoteMonitors = monitors; _onFailure = onFailure;
        _ = Task.Run(SendLoopAsync);
        _ = Task.Run(HeartbeatAsync);
    }

    internal static async Task<HostSession> ConnectAsync(
        string ip, string pinnedFingerprint, string secret, Action<string> onFailure,
        CancellationToken cancellation)
    {
        pinnedFingerprint = new string(pinnedFingerprint.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        secret = secret.Trim().ToUpperInvariant();
        if (pinnedFingerprint.Length != 64 || !pinnedFingerprint.All(Uri.IsHexDigit))
            throw new ArgumentException("Enter the receiver's complete 64-digit SHA-256 fingerprint.");
        if (secret.Length != 32 || !secret.All(Uri.IsHexDigit))
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
            await Wire.WriteAsync(ssl, new Packet("auth", Text: secret), timeout.Token);
            var ready = await Wire.ReadAsync(ssl, timeout.Token);
            if (ready.Type != "ready" || ready.C < 200 || ready.D < 100 ||
                ready.C > 50000 || ready.D > 50000)
                throw new AuthenticationException("Receiver did not accept pairing.");
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
