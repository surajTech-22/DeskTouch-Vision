using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace DeskTouch.DeviceShare;

internal enum FastUdpOp : byte
{
    ReverseMove = 1,
    Move = 2,
    Probe = 3,
    ProbeAck = 4,
    Warm = 5,
    ReverseClaim = 6,
    ReverseGranted = 7,
    ReverseActive = 8,
    ReverseButton = 9,
    ReverseWheel = 10
}

internal readonly record struct FastUdpMessage(
    FastUdpOp Op,
    ushort PeerId,
    ulong Seq,
    int A,
    int B,
    int Lease,
    int TimestampMs);

/// <summary>
/// Authenticated, replay-protected 48-byte AES-256-GCM UDP codec ("DTU1") for
/// low-latency coordinate relay without TCP head-of-line blocking.
/// </summary>
internal sealed class FastUdpCodec : IDisposable
{
    internal const int KeySize = 32;
    internal const int FrameSize = 48;
    internal const byte DirectionServerToClient = 1;
    internal const byte DirectionClientToServer = 2;

    private static readonly byte[] Magic = [(byte)'D', (byte)'T', (byte)'U', (byte)'1'];
    private static readonly byte[] ServerNoncePrefix = [(byte)'S', (byte)'R', (byte)'V', (byte)'1'];
    private static readonly byte[] ClientNoncePrefix = [(byte)'C', (byte)'L', (byte)'I', (byte)'1'];

    private readonly byte _outboundDirection;
    private readonly byte _inboundDirection;
    private readonly ushort _peerId;
    private readonly object _sendLock = new();
    private readonly object _recvLock = new();
    private readonly AesGcm _sendAes;
    private readonly AesGcm _recvAes;
    private readonly byte[] _sendPlain = new byte[16];
    private readonly byte[] _sendNonce = new byte[12];
    private readonly byte[] _recvPlain = new byte[16];
    private readonly byte[] _recvNonce = new byte[12];
    private ulong _sendSeq;
    private ulong _lastRecvSeq;
    private bool _disposed;

    internal FastUdpCodec(byte[] key, ushort peerId, bool isServer)
    {
        if (key == null || key.Length != KeySize)
            throw new ArgumentException("Invalid UDP session key length.", nameof(key));
        _peerId = peerId;
        _outboundDirection = isServer ? DirectionServerToClient : DirectionClientToServer;
        _inboundDirection = isServer ? DirectionClientToServer : DirectionServerToClient;
        _sendAes = new AesGcm(key, 16);
        _recvAes = new AesGcm(key, 16);
        CopyNoncePrefix(_outboundDirection, _sendNonce);
        CopyNoncePrefix(_inboundDirection, _recvNonce);
    }

    internal int Seal(FastUdpOp op, int a, int b, int lease, int timestampMs, byte[] destination)
    {
        if (destination.Length < FrameSize)
            throw new ArgumentException("Destination buffer too small.", nameof(destination));
        lock (_sendLock)
        {
            if (_disposed) return 0;
            ulong seq = ++_sendSeq;
            Span<byte> frame = destination.AsSpan(0, FrameSize);
            Magic.CopyTo(frame);
            frame[4] = _outboundDirection;
            frame[5] = (byte)op;
            BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(6, 2), _peerId);
            BinaryPrimitives.WriteUInt64BigEndian(frame.Slice(8, 8), seq);

            WriteNonceSeq(seq, _sendNonce);
            BinaryPrimitives.WriteInt32BigEndian(_sendPlain.AsSpan(0, 4), a);
            BinaryPrimitives.WriteInt32BigEndian(_sendPlain.AsSpan(4, 4), b);
            BinaryPrimitives.WriteInt32BigEndian(_sendPlain.AsSpan(8, 4), lease);
            BinaryPrimitives.WriteInt32BigEndian(_sendPlain.AsSpan(12, 4), timestampMs);

            _sendAes.Encrypt(
                _sendNonce,
                _sendPlain,
                frame.Slice(16, 16),
                frame.Slice(32, 16),
                frame.Slice(0, 16));
            return FrameSize;
        }
    }

    internal bool TryOpen(ReadOnlySpan<byte> frame, out FastUdpMessage message)
    {
        message = default;
        if (frame.Length != FrameSize) return false;
        if (!frame[..4].SequenceEqual(Magic)) return false;
        if (frame[4] != _inboundDirection) return false;
        ushort peerId = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(6, 2));
        if (peerId != _peerId) return false;
        byte rawOp = frame[5];
        if (!Enum.IsDefined(typeof(FastUdpOp), rawOp)) return false;
        ulong seq = BinaryPrimitives.ReadUInt64BigEndian(frame.Slice(8, 8));
        if (seq == 0) return false;

        lock (_recvLock)
        {
            if (_disposed || seq <= _lastRecvSeq) return false;
            WriteNonceSeq(seq, _recvNonce);
            try
            {
                _recvAes.Decrypt(
                    _recvNonce,
                    frame.Slice(16, 16),
                    frame.Slice(32, 16),
                    _recvPlain,
                    frame[..16]);
            }
            catch (CryptographicException)
            {
                return false;
            }
            _lastRecvSeq = seq;
            message = new FastUdpMessage(
                (FastUdpOp)rawOp,
                peerId,
                seq,
                BinaryPrimitives.ReadInt32BigEndian(_recvPlain.AsSpan(0, 4)),
                BinaryPrimitives.ReadInt32BigEndian(_recvPlain.AsSpan(4, 4)),
                BinaryPrimitives.ReadInt32BigEndian(_recvPlain.AsSpan(8, 4)),
                BinaryPrimitives.ReadInt32BigEndian(_recvPlain.AsSpan(12, 4)));
            return true;
        }
    }

    private static void CopyNoncePrefix(byte direction, byte[] nonce)
    {
        byte[] prefix = direction == DirectionServerToClient ? ServerNoncePrefix : ClientNoncePrefix;
        Buffer.BlockCopy(prefix, 0, nonce, 0, 4);
    }

    private static void WriteNonceSeq(ulong seq, byte[] nonce) =>
        BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4, 8), seq);

    public void Dispose()
    {
        lock (_sendLock)
        {
            lock (_recvLock)
            {
                if (_disposed) return;
                _disposed = true;
                _sendAes.Dispose();
                _recvAes.Dispose();
            }
        }
    }
}

internal static class FastUdpSocketOptions
{
    private const int SioUdpConnReset = -1744830452;

    internal static void ConfigureLowLatency(Socket socket)
    {
        try { socket.IOControl(SioUdpConnReset, new byte[] { 0, 0, 0, 0 }, null); }
        catch (Exception) { }
        try { socket.SendBufferSize = 262144; } catch (Exception) { }
        try { socket.ReceiveBufferSize = 262144; } catch (Exception) { }
        try { socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService, 0x10); }
        catch (Exception) { }
    }

    internal static IPAddress NormalizeAddress(IPAddress ip) =>
        ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
}

internal sealed class FastUdpHub : IDisposable
{
    internal sealed class Peer : IDisposable
    {
        internal readonly int SessionId;
        internal readonly ushort WirePeerId;
        internal readonly IPAddress ExpectedAddress;
        internal readonly byte[] Key;
        internal readonly FastUdpCodec Codec;
        private readonly byte[] _sendBuffer = new byte[FastUdpCodec.FrameSize];
        private IPEndPoint? _remoteEndPoint;
        internal volatile bool Confirmed;
        private int _confirmedLogged;
        internal long LastMoveSentTicks;

        internal bool TryMarkConfirmedLogged() =>
            Interlocked.CompareExchange(ref _confirmedLogged, 1, 0) == 0;

        internal Peer(int sessionId, IPAddress expectedAddress)
        {
            SessionId = sessionId;
            WirePeerId = unchecked((ushort)sessionId);
            ExpectedAddress = FastUdpSocketOptions.NormalizeAddress(expectedAddress);
            Key = RandomNumberGenerator.GetBytes(FastUdpCodec.KeySize);
            Codec = new FastUdpCodec(Key, WirePeerId, isServer: true);
        }

        internal IPEndPoint? RemoteEndPoint
        {
            get => Volatile.Read(ref _remoteEndPoint);
            set => Volatile.Write(ref _remoteEndPoint, value);
        }

        internal bool HasEndpoint => Volatile.Read(ref _remoteEndPoint) != null;

        internal string Base64Key => Convert.ToBase64String(Key);

        internal int SealLocked(FastUdpOp op, int a, int b, int lease, byte[] scratchOut)
        {
            return Codec.Seal(op, a, b, lease, Environment.TickCount, scratchOut);
        }

        internal bool Send(Socket socket, FastUdpOp op, int a, int b, int lease, int? payloadTag = null)
        {
            IPEndPoint? target = Volatile.Read(ref _remoteEndPoint);
            if (target == null) return false;
            lock (_sendBuffer)
            {
                int len = Codec.Seal(op, a, b, lease, payloadTag ?? Environment.TickCount, _sendBuffer);
                if (len <= 0) return false;
                try
                {
                    return socket.SendTo(_sendBuffer, 0, len, SocketFlags.None, target) == len;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(Key);
            Codec.Dispose();
        }
    }

    private readonly Socket _socket;
    private readonly ConcurrentDictionary<ushort, Peer> _peers = new();
    private readonly Thread _recvThread;
    private volatile bool _disposed;

    internal int Port { get; }
    internal event Action<Peer, FastUdpMessage>? MessageReceived;

    internal FastUdpHub()
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        FastUdpSocketOptions.ConfigureLowLatency(_socket);
        _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        Port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
        _recvThread = new Thread(ReceiveLoop)
        {
            IsBackground = true,
            Name = "DeskTouch FastUdpHub",
            Priority = ThreadPriority.AboveNormal
        };
        _recvThread.Start();
    }

    internal Peer? AddPeer(int sessionId, EndPoint? remoteEndPoint)
    {
        if (remoteEndPoint is not IPEndPoint ipEp) return null;
        var peer = new Peer(sessionId, ipEp.Address);
        _peers[peer.WirePeerId] = peer;
        return peer;
    }

    internal void ConfigurePeerPort(Peer peer, int port)
    {
        if (port is <= 0 or > 65535) return;
        peer.RemoteEndPoint = new IPEndPoint(peer.ExpectedAddress, port);
        // Send two quick UDP probes from Windows immediately so Windows Defender
        // Firewall opens a stateful return path without needing elevated rules.
        peer.Send(_socket, FastUdpOp.Probe, Port, 0, 0);
        peer.Send(_socket, FastUdpOp.Probe, Port, 0, 0);
    }

    internal bool Send(Peer peer, FastUdpOp op, int a, int b, int lease, int? payloadTag = null)
    {
        if (_disposed) return false;
        return peer.Send(_socket, op, a, b, lease, payloadTag);
    }

    internal void RemovePeer(Peer? peer)
    {
        if (peer == null) return;
        if (_peers.TryGetValue(peer.WirePeerId, out var current) && ReferenceEquals(current, peer))
            _peers.TryRemove(peer.WirePeerId, out _);
        peer.Dispose();
    }

    private void ReceiveLoop()
    {
        byte[] buffer = new byte[256];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        while (!_disposed)
        {
            try
            {
                int received = _socket.ReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref remote);
                if (received != FastUdpCodec.FrameSize || remote is not IPEndPoint srcIp) continue;
                ushort wireId = (ushort)((buffer[6] << 8) | buffer[7]);
                if (!_peers.TryGetValue(wireId, out var peer)) continue;
                IPAddress normalized = FastUdpSocketOptions.NormalizeAddress(srcIp.Address);
                if (!normalized.Equals(peer.ExpectedAddress)) continue;
                if (!peer.Codec.TryOpen(buffer.AsSpan(0, received), out var msg)) continue;

                if (peer.RemoteEndPoint == null)
                    peer.RemoteEndPoint = new IPEndPoint(normalized, srcIp.Port);

                if (msg.Op == FastUdpOp.Probe)
                {
                    peer.Send(_socket, FastUdpOp.ProbeAck, Port, 0, 0);
                    MessageReceived?.Invoke(peer, msg);
                }
                else if (msg.Op == FastUdpOp.ProbeAck)
                {
                    peer.Confirmed = true;
                    MessageReceived?.Invoke(peer, msg);
                }
                else
                {
                    MessageReceived?.Invoke(peer, msg);
                }
            }
            catch (SocketException)
            {
                if (_disposed) break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception)
            {
                if (_disposed) break;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _socket.Close(); } catch (Exception) { }
        try { _socket.Dispose(); } catch (Exception) { }
        foreach (var kv in _peers)
            kv.Value.Dispose();
        _peers.Clear();
    }
}

internal sealed class FastUdpClient : IDisposable
{
    private readonly Socket _socket;
    private readonly IPEndPoint _serverEndPoint;
    private readonly FastUdpCodec _codec;
    private readonly byte[] _sendBuffer = new byte[FastUdpCodec.FrameSize];
    private readonly Thread _recvThread;
    private volatile bool _disposed;
    internal volatile bool Confirmed;

    internal int LocalPort { get; }
    internal event Action<FastUdpMessage>? MessageReceived;
    internal event Action? ConfirmedChanged;

    internal FastUdpClient(IPAddress serverAddress, int serverPort, int peerId, byte[] key)
    {
        IPAddress normalized = FastUdpSocketOptions.NormalizeAddress(serverAddress);
        _serverEndPoint = new IPEndPoint(normalized, serverPort);
        _codec = new FastUdpCodec(key, unchecked((ushort)peerId), isServer: false);
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        FastUdpSocketOptions.ConfigureLowLatency(_socket);
        _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        LocalPort = ((IPEndPoint)_socket.LocalEndPoint!).Port;
        _recvThread = new Thread(ReceiveLoop)
        {
            IsBackground = true,
            Name = "DeskTouch FastUdpClient",
            Priority = ThreadPriority.AboveNormal
        };
        _recvThread.Start();
        Send(FastUdpOp.Probe, LocalPort, 0, 0);
        Send(FastUdpOp.Probe, LocalPort, 0, 0);
    }

    internal void SendProbe()
    {
        if (!_disposed)
            Send(FastUdpOp.Probe, LocalPort, 0, 0);
    }

    internal bool Send(FastUdpOp op, int a, int b, int lease, int? payloadTag = null)
    {
        if (_disposed) return false;
        lock (_sendBuffer)
        {
            int len = _codec.Seal(op, a, b, lease, payloadTag ?? Environment.TickCount, _sendBuffer);
            if (len <= 0) return false;
            try
            {
                return _socket.SendTo(_sendBuffer, 0, len, SocketFlags.None, _serverEndPoint) == len;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    private void ReceiveLoop()
    {
        byte[] buffer = new byte[256];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        while (!_disposed)
        {
            try
            {
                int received = _socket.ReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref remote);
                if (received != FastUdpCodec.FrameSize || remote is not IPEndPoint srcIp) continue;
                IPAddress normalized = FastUdpSocketOptions.NormalizeAddress(srcIp.Address);
                if (!normalized.Equals(_serverEndPoint.Address) || srcIp.Port != _serverEndPoint.Port) continue;
                if (!_codec.TryOpen(buffer.AsSpan(0, received), out var msg)) continue;
                if (msg.Op == FastUdpOp.Probe)
                {
                    Send(FastUdpOp.ProbeAck, LocalPort, 0, 0);
                    // Also send our own Probe once the hub's stateful pinhole is open
                    // so Client -> Server -> Client is confirmed promptly with ProbeAck.
                    if (!Confirmed)
                        Send(FastUdpOp.Probe, LocalPort, 0, 0);
                    MessageReceived?.Invoke(msg);
                }
                else if (msg.Op == FastUdpOp.ProbeAck)
                {
                    bool newlyConfirmed = !Confirmed;
                    Confirmed = true;
                    if (newlyConfirmed)
                        ConfirmedChanged?.Invoke();
                    MessageReceived?.Invoke(msg);
                }
                else
                {
                    MessageReceived?.Invoke(msg);
                }
            }
            catch (SocketException)
            {
                if (_disposed) break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception)
            {
                if (_disposed) break;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _socket.Close(); } catch (Exception) { }
        try { _socket.Dispose(); } catch (Exception) { }
        _codec.Dispose();
    }
}

/// <summary>
/// Coalesces sub-3ms mouse bursts to avoid flooding mobile Wi-Fi RX queues while
/// delivering the first move instantaneously, scheduling a 16ms trailing TCP
/// settle sync, and pulsing a tiny keep-awake packet only while leased.
/// </summary>
internal sealed class FastMovePacer : IDisposable
{
    private const double MinUdpIntervalMs = 1.5;
    private const int TrailingTcpSyncDelayMs = 130;
    private const int WarmKeepaliveIntervalMs = 22;
    private const int WarmCooldownSeconds = 4;

    private readonly object _gate = new();
    private readonly object _udpSendGate = new();
    private readonly Func<bool> _canSendUdp;
    private readonly Func<Packet, bool> _sendUdp;
    private readonly Action<Packet> _queueReliableTcp;
    private readonly Action<Packet> _queueTrailingSync;
    private readonly Action? _sendWarm;
    private readonly Func<int>? _nextSeq;
    private readonly System.Threading.Timer _timer;
    private Packet? _throttledUdp;
    private Packet? _pendingTcpSync;
    private long _nextMoveOrder;
    private long _throttledOrder;
    private long _lastSentUdpOrder;
    private long _lastMoveUdpTicks;
    private long _lastMoveTicks;
    private long _lastWarmTicks;
    private long _warmCooldownUntilTicks;
    private bool _warmActive;
    private bool _disposed;

    internal FastMovePacer(
        Func<bool> canSendUdp,
        Func<Packet, bool> sendUdp,
        Action<Packet> queueReliableTcp,
        Action? sendWarm = null,
        Action<Packet>? queueTrailingSync = null,
        Func<int>? nextSeq = null)
    {
        _canSendUdp = canSendUdp;
        _sendUdp = sendUdp;
        _queueReliableTcp = queueReliableTcp;
        _queueTrailingSync = queueTrailingSync ?? queueReliableTcp;
        _sendWarm = sendWarm;
        _nextSeq = nextSeq;
        _timer = new System.Threading.Timer(OnTimer, null, Timeout.Infinite, Timeout.Infinite);
    }

    private Packet StampSeq(Packet move) =>
        _nextSeq != null && move.C <= 0 ? move with { C = _nextSeq() } : move;

    private bool DispatchUdpMove(Packet move, long order)
    {
        lock (_udpSendGate)
        {
            if (_disposed || order <= _lastSentUdpOrder)
                return false;
            _lastSentUdpOrder = order;
            return _sendUdp(StampSeq(move));
        }
    }

    internal bool TryHandleMove(Packet move)
    {
        if (!_canSendUdp()) return false;
        bool sendNow = false;
        long order;
        lock (_gate)
        {
            if (_disposed) return false;
            long now = Stopwatch.GetTimestamp();
            _lastMoveTicks = now;
            order = ++_nextMoveOrder;
            double minInterval = ActiveMinIntervalMsLocked();
            double elapsedMs = _lastMoveUdpTicks == 0
                ? double.MaxValue
                : (now - _lastMoveUdpTicks) * 1000.0 / Stopwatch.Frequency;
            if (elapsedMs >= minInterval)
            {
                _throttledUdp = null;
                _throttledOrder = 0;
                _pendingTcpSync = move;
                _lastMoveUdpTicks = now;
                sendNow = true;
            }
            else
            {
                _throttledUdp = move;
                _throttledOrder = order;
                _pendingTcpSync = move;
            }
            UpdateTimerLocked();
        }
        if (sendNow && DispatchUdpMove(move, order))
            Interlocked.Increment(ref _udpDirectCount);
        return true;
    }

    private double _effectiveMinIntervalMs = -1;
    private long _udpDirectCount, _udpFlushedCount, _trailingTcpSyncCount;

    internal void SetMinIntervalMs(double ms)
    {
        lock (_gate)
            _effectiveMinIntervalMs = ms > 0 ? ms : -1;
    }

    internal (long UdpDirect, long UdpPacedFlush, long TrailingTcpSync) TakeStats() => (
        Interlocked.Exchange(ref _udpDirectCount, 0),
        Interlocked.Exchange(ref _udpFlushedCount, 0),
        Interlocked.Exchange(ref _trailingTcpSyncCount, 0));

    private double ActiveMinIntervalMsLocked() =>
        _effectiveMinIntervalMs > 0 ? _effectiveMinIntervalMs : MinUdpIntervalMs;

    internal Packet? TakeThrottledUdpMove()
    {
        lock (_gate)
        {
            var packet = _throttledUdp;
            if (packet == null) return null;
            _throttledUdp = null;
            _throttledOrder = 0;
            _lastMoveUdpTicks = Stopwatch.GetTimestamp();
            UpdateTimerLocked();
            return packet;
        }
    }

    internal bool FlushThrottledUdpMoveNow()
    {
        Packet? move;
        long order;
        lock (_gate)
        {
            if (_disposed || !_canSendUdp() || _throttledUdp == null) return false;
            move = _throttledUdp;
            order = _throttledOrder;
            _throttledUdp = null;
            _throttledOrder = 0;
            _lastMoveUdpTicks = Stopwatch.GetTimestamp();
            UpdateTimerLocked();
        }
        if (!DispatchUdpMove(move, order)) return false;
        Interlocked.Increment(ref _udpFlushedCount);
        return true;
    }

    internal Packet? TakePendingReliableSync(bool skipIfUdpDelivered = false)
    {
        Packet? packet;
        lock (_gate)
        {
            packet = skipIfUdpDelivered && _canSendUdp()
                ? _throttledUdp
                : (_throttledUdp ?? _pendingTcpSync);
            _throttledUdp = null;
            _throttledOrder = 0;
            _pendingTcpSync = null;
            UpdateTimerLocked();
        }
        if (packet == null) return null;
        lock (_udpSendGate)
            return StampSeq(packet);
    }

    internal void ClearPending()
    {
        lock (_gate)
        {
            _throttledUdp = null;
            _throttledOrder = 0;
            _pendingTcpSync = null;
            UpdateTimerLocked();
        }
    }

    internal void SetWarmActive(bool active)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (active) _warmCooldownUntilTicks = 0;
            else if (_warmActive)
                _warmCooldownUntilTicks = Stopwatch.GetTimestamp() + WarmCooldownSeconds * Stopwatch.Frequency;
            if (_warmActive == active && !active) { UpdateTimerLocked(); return; }
            _warmActive = active;
            UpdateTimerLocked();
        }
    }

    private bool IsWarmWindowOpenLocked(long now)
    {
        if (_warmActive) return true;
        if (_warmCooldownUntilTicks <= 0) return false;
        if (now < _warmCooldownUntilTicks) return true;
        _warmCooldownUntilTicks = 0;
        return false;
    }

    private void OnTimer(object? state)
    {
        Packet? udpMove = null;
        long udpOrder = 0;
        Packet? tcpSync = null;
        bool isTrailingSync = false;
        bool sendWarm = false;

        lock (_gate)
        {
            if (_disposed) return;
            long now = Stopwatch.GetTimestamp();

            if (_throttledUdp != null)
            {
                if (_canSendUdp())
                {
                    double minInterval = ActiveMinIntervalMsLocked();
                    double udpElapsed = _lastMoveUdpTicks == 0
                        ? double.MaxValue
                        : (now - _lastMoveUdpTicks) * 1000.0 / Stopwatch.Frequency;
                    if (udpElapsed >= minInterval - 0.5)
                    {
                        udpMove = _throttledUdp;
                        udpOrder = _throttledOrder;
                        _throttledUdp = null;
                        _throttledOrder = 0;
                        _lastMoveUdpTicks = now;
                    }
                }
                else
                {
                    tcpSync = _throttledUdp;
                    _throttledUdp = null;
                    _throttledOrder = 0;
                    _pendingTcpSync = null;
                }
            }
            else if (_pendingTcpSync != null)
            {
                double idleMs = (now - _lastMoveTicks) * 1000.0 / Stopwatch.Frequency;
                if (idleMs >= TrailingTcpSyncDelayMs - 1)
                {
                    tcpSync = _pendingTcpSync;
                    _pendingTcpSync = null;
                    isTrailingSync = true;
                }
            }

            if (IsWarmWindowOpenLocked(now) && udpMove == null && tcpSync == null && _sendWarm != null)
            {
                long lastActivityTicks = Math.Max(_lastMoveUdpTicks, _lastWarmTicks);
                double idleWarmMs = lastActivityTicks == 0
                    ? double.MaxValue
                    : (now - lastActivityTicks) * 1000.0 / Stopwatch.Frequency;
                if (idleWarmMs >= WarmKeepaliveIntervalMs - 2)
                {
                    _lastWarmTicks = now;
                    sendWarm = true;
                }
            }

            UpdateTimerLocked();
        }

        if (udpMove != null && DispatchUdpMove(udpMove, udpOrder))
            Interlocked.Increment(ref _udpFlushedCount);
        if (tcpSync != null)
        {
            Packet stampedSync;
            lock (_udpSendGate)
            {
                stampedSync = StampSeq(tcpSync);
                if (isTrailingSync)
                    _queueTrailingSync(stampedSync);
                else
                    _queueReliableTcp(stampedSync);
            }
            if (isTrailingSync)
                Interlocked.Increment(ref _trailingTcpSyncCount);
        }
        if (sendWarm) _sendWarm!();
    }

    private void UpdateTimerLocked()
    {
        if (_disposed) return;
        int dueMs;
        if (_throttledUdp != null)
        {
            double minInterval = ActiveMinIntervalMsLocked();
            double elapsedMs = _lastMoveUdpTicks == 0
                ? minInterval
                : (Stopwatch.GetTimestamp() - _lastMoveUdpTicks) * 1000.0 / Stopwatch.Frequency;
            dueMs = (int)Math.Ceiling(Math.Max(1.0, minInterval - elapsedMs));
        }
        else
        {
            long now = Stopwatch.GetTimestamp();
            dueMs = _pendingTcpSync != null
                ? TrailingTcpSyncDelayMs
                : IsWarmWindowOpenLocked(now) && _sendWarm != null
                    ? WarmKeepaliveIntervalMs
                    : Timeout.Infinite;
        }
        try { _timer.Change(dueMs, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _throttledUdp = null;
            _pendingTcpSync = null;
            _warmActive = false;
            _warmCooldownUntilTicks = 0;
        }
        _timer.Dispose();
    }
}

