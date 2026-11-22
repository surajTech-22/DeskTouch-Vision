using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;

namespace DeskTouch.DeviceShare;

/// <summary>
/// Zero-config, outbound-only end-to-end encrypted relay bridge over MQTT 3.1.1 TLS (port 8883).
/// Used ONLY as an automatic fallback when direct LAN / Global IPv6 / STUN TCP is blocked by
/// router NAT, CGNAT, or stateful IPv6 firewalls.
/// Every byte transported through the broker is DeskTouch's pinned X.509 TLS 1.2/1.3 ciphertext
/// on a single-use random 128-bit session topic — the broker never sees plaintext traffic.
/// </summary>
internal sealed class MqttTlsRelayTunnel : IDisposable
{
    internal const string DefaultBrokerHost = "broker.emqx.io";
    internal const int DefaultBrokerPort = 8883;
    private const int MaxChunkBytes = 12288;
    private const int KeepAliveSeconds = 25;

    private static readonly byte[] CtrlSyn = "SYN1"u8.ToArray();
    private static readonly byte[] CtrlRdy = "RDY1"u8.ToArray();
    private static readonly byte[] CtrlEof = "EOF1"u8.ToArray();

    private readonly TcpClient _brokerTcp;
    private readonly SslStream _brokerSsl;
    private readonly TcpListener? _localListener;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly string _ctrlTopic;
    private int _disposed;

    internal int LocalPort { get; }

    private MqttTlsRelayTunnel(
        TcpClient brokerTcp,
        SslStream brokerSsl,
        TcpListener? localListener,
        int localPort,
        string ctrlTopic)
    {
        _brokerTcp = brokerTcp;
        _brokerSsl = brokerSsl;
        _localListener = localListener;
        LocalPort = localPort;
        _ctrlTopic = ctrlTopic;
    }

    internal static async Task<MqttTlsRelayTunnel> OpenClientBridgeAsync(
        string sessionId,
        string? brokerHost,
        int brokerPort,
        CancellationToken cancellation)
    {
        string host = string.IsNullOrWhiteSpace(brokerHost) ? DefaultBrokerHost : brokerHost.Trim();
        int port = brokerPort is > 0 and <= 65535 ? brokerPort : DefaultBrokerPort;
        string sid = NormalizeSessionId(sessionId);
        if (sid.Length < 16)
            throw new ArgumentException("Invalid relay session ID.", nameof(sessionId));

        string c2sTopic = $"dtv/r/{sid}/c2s";
        string s2cTopic = $"dtv/r/{sid}/s2c";
        string ctrlTopic = $"dtv/r/{sid}/ctrl";

        var brokerTcp = new TcpClient { NoDelay = true };
        SslStream? brokerSsl = null;
        TcpListener? localListener = null;
        using var synStopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        SemaphoreSlim? initWriteLock = null;
        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(7));
            await brokerTcp.ConnectAsync(host, port, connectTimeout.Token).ConfigureAwait(false);
            brokerSsl = new SslStream(brokerTcp.GetStream(), leaveInnerStreamOpen: false);
            await brokerSsl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            }, connectTimeout.Token).ConfigureAwait(false);

            string clientId = $"dtv-c-{sid[..10]}-{RandomNumberGenerator.GetInt32(1000, 9999)}";
            initWriteLock = new SemaphoreSlim(1, 1);
            await SendConnectAsync(brokerSsl, initWriteLock, clientId, connectTimeout.Token).ConfigureAwait(false);
            var (connType, connBody) = await ReadMqttPacketAsync(brokerSsl, connectTimeout.Token).ConfigureAwait(false);
            if (connType != 2 || connBody.Length < 2 || connBody[1] != 0)
                throw new IOException("Relay broker rejected CONNECT.");

            await SendSubscribeAsync(brokerSsl, initWriteLock, 1, s2cTopic, connectTimeout.Token).ConfigureAwait(false);
            await SendSubscribeAsync(brokerSsl, initWriteLock, 2, ctrlTopic, connectTimeout.Token).ConfigureAwait(false);

            bool readyReceived = false;
            var readyDeadline = Environment.TickCount64 + 6500;
            var synTask = Task.Run(async () =>
            {
                for (int i = 0; i < 14 && !synStopCts.IsCancellationRequested; i++)
                {
                    try { await SendPublishAsync(brokerSsl, initWriteLock, ctrlTopic, CtrlSyn, synStopCts.Token).ConfigureAwait(false); }
                    catch { return; }
                    try { await Task.Delay(400, synStopCts.Token).ConfigureAwait(false); }
                    catch { return; }
                }
            });

            var bufferedS2c = new Queue<byte[]>();
            while (!readyReceived && Environment.TickCount64 < readyDeadline && !connectTimeout.IsCancellationRequested)
            {
                var (pktType, body) = await ReadMqttPacketAsync(brokerSsl, connectTimeout.Token).ConfigureAwait(false);
                if (pktType == 3 && TryParsePublish(body, out string topic, out byte[] payload))
                {
                    if (topic == ctrlTopic && payload.AsSpan().SequenceEqual(CtrlRdy))
                    {
                        readyReceived = true;
                        break;
                    }
                    if (topic == s2cTopic && payload.Length > 0)
                    {
                        bufferedS2c.Enqueue(payload);
                        readyReceived = true;
                        break;
                    }
                }
            }
            synStopCts.Cancel();
            try { await synTask.ConfigureAwait(false); } catch { }
            if (!readyReceived)
                throw new TimeoutException("Remote computer did not respond on encrypted relay.");

            initWriteLock.Dispose();
            initWriteLock = null;

            localListener = new TcpListener(IPAddress.Loopback, 0);
            localListener.Start(1);
            int localPort = ((IPEndPoint)localListener.LocalEndpoint).Port;

            var tunnel = new MqttTlsRelayTunnel(brokerTcp, brokerSsl, localListener, localPort, ctrlTopic);
            tunnel.StartClientPump(c2sTopic, s2cTopic, bufferedS2c);
            return tunnel;
        }
        catch
        {
            try { initWriteLock?.Dispose(); } catch { }
            try { localListener?.Stop(); } catch { }
            try { brokerSsl?.Dispose(); } catch { }
            try { brokerTcp.Dispose(); } catch { }
            throw;
        }
    }

    private void StartClientPump(string c2sTopic, string s2cTopic, Queue<byte[]> bufferedS2c)
    {
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            TcpClient? localClient = null;
            try
            {
                using var acceptLimit = CancellationTokenSource.CreateLinkedTokenSource(token);
                acceptLimit.CancelAfter(TimeSpan.FromSeconds(12));
                localClient = await _localListener!.AcceptTcpClientAsync(acceptLimit.Token).ConfigureAwait(false);
                localClient.NoDelay = true;
                try { _localListener.Stop(); } catch { }

                var localStream = localClient.GetStream();
                while (bufferedS2c.Count > 0)
                {
                    byte[] chunk = bufferedS2c.Dequeue();
                    await localStream.WriteAsync(chunk, token).ConfigureAwait(false);
                }

                var keepAliveTask = RunKeepAliveLoopAsync(_brokerSsl, _writeLock, token);
                var uploadTask = PumpLocalToMqttAsync(localStream, _brokerSsl, _writeLock, c2sTopic, _ctrlTopic, token);
                var downloadTask = PumpMqttToLocalAsync(_brokerSsl, localStream, s2cTopic, _ctrlTopic, null, token);
                await Task.WhenAny(uploadTask, downloadTask).ConfigureAwait(false);
                _cts.Cancel();
                try { await Task.WhenAll(uploadTask, downloadTask, keepAliveTask).ConfigureAwait(false); } catch { }
            }
            catch { }
            finally
            {
                try { localClient?.Dispose(); } catch { }
                Dispose();
            }
        });
    }

    internal static async Task RunServerBridgeAsync(
        string sessionId,
        string? brokerHost,
        int brokerPort,
        int targetLocalPort,
        Action<int>? registerRelayPort,
        Action<int>? unregisterRelayPort,
        CancellationToken stop)
    {
        string host = string.IsNullOrWhiteSpace(brokerHost) ? DefaultBrokerHost : brokerHost.Trim();
        int port = brokerPort is > 0 and <= 65535 ? brokerPort : DefaultBrokerPort;
        string sid = NormalizeSessionId(sessionId);
        if (sid.Length < 16) return;

        string c2sTopic = $"dtv/r/{sid}/c2s";
        string s2cTopic = $"dtv/r/{sid}/s2c";
        string ctrlTopic = $"dtv/r/{sid}/ctrl";

        using var bridgeCts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        using var writeLock = new SemaphoreSlim(1, 1);
        using var brokerTcp = new TcpClient { NoDelay = true };
        SslStream? brokerSsl = null;
        TcpClient? localClient = null;
        int registeredPort = 0;

        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(bridgeCts.Token);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(7));
            await brokerTcp.ConnectAsync(host, port, connectTimeout.Token).ConfigureAwait(false);
            brokerSsl = new SslStream(brokerTcp.GetStream(), leaveInnerStreamOpen: false);
            await brokerSsl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            }, connectTimeout.Token).ConfigureAwait(false);

            string clientId = $"dtv-s-{sid[..10]}-{RandomNumberGenerator.GetInt32(1000, 9999)}";
            await SendConnectAsync(brokerSsl, writeLock, clientId, connectTimeout.Token).ConfigureAwait(false);
            var (connType, connBody) = await ReadMqttPacketAsync(brokerSsl, connectTimeout.Token).ConfigureAwait(false);
            if (connType != 2 || connBody.Length < 2 || connBody[1] != 0)
                return;

            await SendSubscribeAsync(brokerSsl, writeLock, 1, c2sTopic, connectTimeout.Token).ConfigureAwait(false);
            await SendSubscribeAsync(brokerSsl, writeLock, 2, ctrlTopic, connectTimeout.Token).ConfigureAwait(false);
            await SendPublishAsync(brokerSsl, writeLock, ctrlTopic, CtrlRdy, connectTimeout.Token).ConfigureAwait(false);

            using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(bridgeCts.Token);
            idleCts.CancelAfter(TimeSpan.FromSeconds(40));
            byte[]? firstChunk = null;
            var keepAliveIdle = RunKeepAliveLoopAsync(brokerSsl, writeLock, bridgeCts.Token);

            while (firstChunk == null && !idleCts.IsCancellationRequested)
            {
                var (pktType, body) = await ReadMqttPacketAsync(brokerSsl, idleCts.Token).ConfigureAwait(false);
                if (pktType == 3 && TryParsePublish(body, out string topic, out byte[] payload))
                {
                    if (topic == ctrlTopic)
                    {
                        if (payload.AsSpan().SequenceEqual(CtrlSyn))
                            await SendPublishAsync(brokerSsl, writeLock, ctrlTopic, CtrlRdy, idleCts.Token).ConfigureAwait(false);
                        else if (payload.AsSpan().SequenceEqual(CtrlEof))
                            return;
                    }
                    else if (topic == c2sTopic && payload.Length > 0)
                    {
                        firstChunk = payload;
                        break;
                    }
                }
            }

            if (firstChunk == null) return;

            localClient = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
            await localClient.ConnectAsync(IPAddress.Loopback, targetLocalPort, bridgeCts.Token).ConfigureAwait(false);
            if (localClient.Client.LocalEndPoint is IPEndPoint ep)
            {
                registeredPort = ep.Port;
                registerRelayPort?.Invoke(registeredPort);
            }

            var localStream = localClient.GetStream();
            await localStream.WriteAsync(firstChunk, bridgeCts.Token).ConfigureAwait(false);

            var uploadTask = PumpLocalToMqttAsync(localStream, brokerSsl, writeLock, s2cTopic, ctrlTopic, bridgeCts.Token);
            var downloadTask = PumpMqttToLocalAsync(
                brokerSsl, localStream, c2sTopic, ctrlTopic,
                onSyn: () => _ = SendPublishAsync(brokerSsl, writeLock, ctrlTopic, CtrlRdy, bridgeCts.Token),
                bridgeCts.Token);
            await Task.WhenAny(uploadTask, downloadTask).ConfigureAwait(false);
            bridgeCts.Cancel();
            try { await Task.WhenAll(uploadTask, downloadTask, keepAliveIdle).ConfigureAwait(false); } catch { }
        }
        catch { }
        finally
        {
            if (registeredPort > 0)
            {
                try { unregisterRelayPort?.Invoke(registeredPort); } catch { }
            }
            try { localClient?.Dispose(); } catch { }
            try { brokerSsl?.Dispose(); } catch { }
        }
    }

    private static async Task PumpLocalToMqttAsync(
        NetworkStream localStream,
        SslStream brokerSsl,
        SemaphoreSlim writeLock,
        string publishTopic,
        string ctrlTopic,
        CancellationToken token)
    {
        byte[] buf = new byte[MaxChunkBytes];
        try
        {
            while (!token.IsCancellationRequested)
            {
                int n = await localStream.ReadAsync(buf.AsMemory(0, buf.Length), token).ConfigureAwait(false);
                if (n <= 0) break;
                await SendPublishAsync(brokerSsl, writeLock, publishTopic, buf.AsMemory(0, n), token).ConfigureAwait(false);
            }
        }
        catch { }
        finally
        {
            try
            {
                using var eofCts = new CancellationTokenSource(1200);
                await SendPublishAsync(brokerSsl, writeLock, ctrlTopic, CtrlEof, eofCts.Token).ConfigureAwait(false);
            }
            catch { }
        }
    }

    private static async Task PumpMqttToLocalAsync(
        SslStream brokerSsl,
        NetworkStream localStream,
        string expectedDataTopic,
        string ctrlTopic,
        Action? onSyn,
        CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var (pktType, body) = await ReadMqttPacketAsync(brokerSsl, token).ConfigureAwait(false);
                if (pktType == 3 && TryParsePublish(body, out string topic, out byte[] payload))
                {
                    if (topic == expectedDataTopic && payload.Length > 0)
                    {
                        await localStream.WriteAsync(payload, token).ConfigureAwait(false);
                    }
                    else if (topic == ctrlTopic)
                    {
                        if (payload.AsSpan().SequenceEqual(CtrlEof))
                            break;
                        if (payload.AsSpan().SequenceEqual(CtrlSyn))
                            onSyn?.Invoke();
                    }
                }
            }
        }
        catch { }
    }

    private static async Task RunKeepAliveLoopAsync(SslStream brokerSsl, SemaphoreSlim writeLock, CancellationToken token)
    {
        byte[] ping = [0xC0, 0x00];
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
                await writeLock.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    await brokerSsl.WriteAsync(ping, token).ConfigureAwait(false);
                    await brokerSsl.FlushAsync(token).ConfigureAwait(false);
                }
                finally { writeLock.Release(); }
            }
        }
        catch { }
    }

    private static string NormalizeSessionId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw.Trim())
        {
            if (char.IsAsciiLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        }
        return sb.Length > 48 ? sb.ToString(0, 48) : sb.ToString();
    }

    private static byte[] EncodeVarInt(int value)
    {
        var bytes = new List<byte>(4);
        int v = value;
        do
        {
            byte b = (byte)(v & 0x7F);
            v >>= 7;
            if (v > 0) b |= 0x80;
            bytes.Add(b);
        } while (v > 0);
        return bytes.ToArray();
    }

    private static async Task SendConnectAsync(SslStream ssl, SemaphoreSlim writeLock, string clientId, CancellationToken token)
    {
        byte[] cid = Encoding.UTF8.GetBytes(clientId);
        byte[] body = new byte[10 + 2 + cid.Length];
        body[0] = 0; body[1] = 4; body[2] = (byte)'M'; body[3] = (byte)'Q'; body[4] = (byte)'T'; body[5] = (byte)'T';
        body[6] = 4; body[7] = 0x02;
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(8, 2), (ushort)KeepAliveSeconds);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(10, 2), (ushort)cid.Length);
        Buffer.BlockCopy(cid, 0, body, 12, cid.Length);
        await WriteMqttFrameAsync(ssl, writeLock, 0x10, body, token).ConfigureAwait(false);
    }

    private static async Task SendSubscribeAsync(SslStream ssl, SemaphoreSlim writeLock, ushort packetId, string topic, CancellationToken token)
    {
        byte[] tb = Encoding.UTF8.GetBytes(topic);
        byte[] body = new byte[2 + 2 + tb.Length + 1];
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(0, 2), packetId);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(2, 2), (ushort)tb.Length);
        Buffer.BlockCopy(tb, 0, body, 4, tb.Length);
        body[^1] = 0;
        await WriteMqttFrameAsync(ssl, writeLock, 0x82, body, token).ConfigureAwait(false);
    }

    private static async Task SendPublishAsync(SslStream ssl, SemaphoreSlim writeLock, string topic, ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        byte[] tb = Encoding.UTF8.GetBytes(topic);
        int remLen = 2 + tb.Length + payload.Length;
        byte[] varInt = EncodeVarInt(remLen);
        byte[] header = new byte[1 + varInt.Length + 2 + tb.Length];
        header[0] = 0x30;
        Buffer.BlockCopy(varInt, 0, header, 1, varInt.Length);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(1 + varInt.Length, 2), (ushort)tb.Length);
        Buffer.BlockCopy(tb, 0, header, 1 + varInt.Length + 2, tb.Length);

        await writeLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await ssl.WriteAsync(header, token).ConfigureAwait(false);
            if (!payload.IsEmpty)
                await ssl.WriteAsync(payload, token).ConfigureAwait(false);
            await ssl.FlushAsync(token).ConfigureAwait(false);
        }
        finally { writeLock.Release(); }
    }

    private static async Task WriteMqttFrameAsync(SslStream ssl, SemaphoreSlim writeLock, byte firstByte, byte[] body, CancellationToken token)
    {
        byte[] varInt = EncodeVarInt(body.Length);
        byte[] frame = new byte[1 + varInt.Length + body.Length];
        frame[0] = firstByte;
        Buffer.BlockCopy(varInt, 0, frame, 1, varInt.Length);
        Buffer.BlockCopy(body, 0, frame, 1 + varInt.Length, body.Length);
        await writeLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await ssl.WriteAsync(frame, token).ConfigureAwait(false);
            await ssl.FlushAsync(token).ConfigureAwait(false);
        }
        finally { writeLock.Release(); }
    }

    private static async Task<(int PacketType, byte[] Body)> ReadMqttPacketAsync(SslStream ssl, CancellationToken token)
    {
        byte[] one = new byte[1];
        await ReadExactAsync(ssl, one, token).ConfigureAwait(false);
        int pktType = (one[0] >> 4) & 0x0F;
        int mult = 1, remLen = 0;
        for (int i = 0; i < 4; i++)
        {
            await ReadExactAsync(ssl, one, token).ConfigureAwait(false);
            remLen += (one[0] & 0x7F) * mult;
            if ((one[0] & 0x80) == 0) break;
            mult *= 128;
        }
        if (remLen is < 0 or > 262144)
            throw new IOException("Invalid MQTT packet length.");
        byte[] body = remLen == 0 ? Array.Empty<byte>() : new byte[remLen];
        if (remLen > 0)
            await ReadExactAsync(ssl, body, token).ConfigureAwait(false);
        return (pktType, body);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), token).ConfigureAwait(false);
            if (n <= 0) throw new EndOfStreamException();
            offset += n;
        }
    }

    internal static bool TryParsePublish(byte[] body, out string topic, out byte[] payload)
    {
        topic = "";
        payload = Array.Empty<byte>();
        if (body.Length < 2) return false;
        int tLen = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(0, 2));
        if (2 + tLen > body.Length) return false;
        topic = Encoding.UTF8.GetString(body, 2, tLen);
        int pLen = body.Length - (2 + tLen);
        if (pLen > 0)
        {
            payload = new byte[pLen];
            Buffer.BlockCopy(body, 2 + tLen, payload, 0, pLen);
        }
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _cts.Cancel(); } catch { }
        try { _localListener?.Stop(); } catch { }
        try { _brokerSsl.Dispose(); } catch { }
        try { _brokerTcp.Dispose(); } catch { }
        try { _writeLock.Dispose(); } catch { }
        try { _cts.Dispose(); } catch { }
    }
}
