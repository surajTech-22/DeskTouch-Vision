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
    ReverseWheel = 10,
    LinkPing = 11,
    LinkFeedback = 12,
    ReverseEdgePush = 13
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
    private ulong _recvWindowMask;
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

    internal bool TryOpen(ReadOnlySpan<byte> frame, out FastUdpMessage message, AdaptiveLinkTracker? tracker = null)
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
            if (_disposed) return false;
            bool isCoordinateOp = rawOp is (byte)FastUdpOp.Move
                or (byte)FastUdpOp.ReverseMove
                or (byte)FastUdpOp.ReverseEdgePush;
            bool isNewMax = _lastRecvSeq == 0 || seq > _lastRecvSeq;
            ulong backBit = 0;
            if (!isNewMax)
            {
                ulong back = _lastRecvSeq - seq;
                if (back >= 64) return false;
                backBit = 1UL << (int)back;
                if ((_recvWindowMask & backBit) != 0) return false;
            }
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
            if (_lastRecvSeq == 0)
            {
                _lastRecvSeq = seq;
                _recvWindowMask = 1UL;
            }
            else if (isNewMax)
            {
                ulong diff = seq - _lastRecvSeq;
                _recvWindowMask = diff >= 64 ? 1UL : ((_recvWindowMask << (int)diff) | 1UL);
                _lastRecvSeq = seq;
            }
            else
            {
                _recvWindowMask |= backBit;
                if (isCoordinateOp)
                {
                    tracker?.RecordAuthenticatedSeqOnly(seq);
                    return false;
                }
            }
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

internal sealed class AdaptiveLinkTracker
{
    private const int ReorderSlack = 8;
    private const long FastMinRttWindowMs = 3_000;
    private const long SlowBandMinRttWindowMs = 5_000;
    private const int PingRingSize = 16;

    private readonly object _recvGate = new();
    private ulong _maxSeq;
    private ulong _settledSeq;
    private ulong _seenMask;
    private double _ewmaLossRatio;
    private long _reorderedPackets;
    private long _lostPackets;
    private long _lastArriveMs;
    private double _inboundJitterMs;
    private bool _hasLastTransit;
    private int _lastTransitSenderMs;
    private long _lastTransitRecvMs;

    private readonly object _rttGate = new();
    private readonly int[] _pingTokens = new int[PingRingSize];
    private readonly long[] _pingSentMs = new long[PingRingSize];
    private readonly long[] _pingSentTicks = new long[PingRingSize];
    private readonly bool[] _pingRealClock = new bool[PingRingSize];
    private int _pingCounter;
    private double _smoothedRttMs;
    private double _rttJitterMs;
    private double _minRttWindowA = double.MaxValue;
    private double _minRttWindowB = double.MaxValue;
    private long _windowStartMs;
    private double _peerLossPermille;
    private double _peerJitterMs;
    private int _peerRefreshHz;
    private int _peerFrameTenthsMs;
    private string _band = "wireless";
    private int _linkSpeedMbps;
    private int _networkEpoch;
    private int _consecutiveHighRttCount;

    internal AdaptiveLinkTracker(string initialBand = "wireless")
    {
        _band = NormalizeBand(initialBand);
    }

    internal string Band { get { lock (_rttGate) return _band; } }
    internal int LinkSpeedMbps { get { lock (_rttGate) return _linkSpeedMbps; } }
    internal int NetworkEpoch { get { lock (_rttGate) return _networkEpoch; } }
    internal long LastArriveMs { get { lock (_recvGate) return _lastArriveMs; } }

    internal static string NormalizeBand(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "wireless";
        string b = raw.Trim();
        if (b.Equals("cable", StringComparison.OrdinalIgnoreCase)) return "cable";
        if (b.Equals("ethernet", StringComparison.OrdinalIgnoreCase)) return "ethernet";
        if (b.Equals("2.4GHz", StringComparison.OrdinalIgnoreCase)) return "2.4GHz";
        if (b.Equals("5GHz", StringComparison.OrdinalIgnoreCase)) return "5GHz";
        if (b.Equals("6GHz", StringComparison.OrdinalIgnoreCase)) return "6GHz";
        return "wireless";
    }

    internal bool OnLinkProfileChanged(string? band, int linkSpeedMbps, int epoch)
    {
        string normalized = NormalizeBand(band);
        bool changed;
        lock (_rttGate)
        {
            changed = !string.Equals(_band, normalized, StringComparison.Ordinal)
                || (epoch > 0 && epoch != _networkEpoch);
            _band = normalized;
            if (linkSpeedMbps >= 0) _linkSpeedMbps = linkSpeedMbps;
            if (epoch > 0) _networkEpoch = epoch;
            if (changed)
            {
                Array.Clear(_pingTokens);
                Array.Clear(_pingSentMs);
                Array.Clear(_pingSentTicks);
                Array.Clear(_pingRealClock);
                _smoothedRttMs = 0;
                _rttJitterMs *= 0.25;
                _minRttWindowA = double.MaxValue;
                _minRttWindowB = double.MaxValue;
                _windowStartMs = 0;
                _peerLossPermille = 0;
                _peerJitterMs = 0;
                _consecutiveHighRttCount = 0;
            }
        }
        if (changed)
        {
            lock (_recvGate)
            {
                _ewmaLossRatio *= 0.20;
                _inboundJitterMs *= 0.25;
                _hasLastTransit = false;
            }
        }
        return changed;
    }

    internal int ComputeProbeIntervalMs(bool controlActive)
    {
        string band = Band;
        return band switch
        {
            "cable" => controlActive ? 38 : 120,
            "5GHz" or "6GHz" => controlActive ? 120 : 500,
            "2.4GHz" => controlActive ? 350 : 900,
            _ => controlActive ? 220 : 750
        };
    }

    internal static bool HasSenderTimestamp(FastUdpOp op) =>
        op is FastUdpOp.Move or FastUdpOp.Warm or FastUdpOp.LinkPing;

    internal int NextPingToken(long nowMs)
    {
        long nowTicks = Stopwatch.GetTimestamp();
        bool isRealClock = Math.Abs(nowMs - Environment.TickCount64) < 5_000;
        lock (_rttGate)
        {
            _pingCounter = (_pingCounter + 1) & 0x3FFFFFFF;
            if (_pingCounter == 0) _pingCounter = 1;
            int token = _pingCounter;
            int slot = token % PingRingSize;
            _pingTokens[slot] = token;
            _pingSentMs[slot] = nowMs;
            _pingSentTicks[slot] = nowTicks;
            _pingRealClock[slot] = isRealClock;
            return token;
        }
    }

    internal bool OnFeedbackReceived(int echoToken, int packedLossJitter, int packedDisplayRate, long nowMs)
    {
        bool matched = false;
        long nowTicks = Stopwatch.GetTimestamp();
        lock (_rttGate)
        {
            if (echoToken > 0)
            {
                int slot = echoToken % PingRingSize;
                if (_pingTokens[slot] == echoToken)
                {
                    double sample = -1;
                    long elapsedMs = nowMs - _pingSentMs[slot];
                    if (_pingRealClock[slot] && _pingSentTicks[slot] > 0 && nowTicks >= _pingSentTicks[slot])
                    {
                        double swMs = (nowTicks - _pingSentTicks[slot]) * 1000.0 / Stopwatch.Frequency;
                        sample = (elapsedMs >= 0 && Math.Abs(swMs - elapsedMs) <= 25.0) ? swMs : elapsedMs;
                    }
                    else if (_pingSentMs[slot] > 0 && elapsedMs >= 0)
                    {
                        sample = elapsedMs;
                    }
                    if (sample >= 0)
                    {
                        _pingTokens[slot] = 0;
                        RecordRttSampleLocked(Math.Max(0.2, sample), nowMs);
                        matched = true;
                    }
                }
            }
            if (packedLossJitter >= 0)
            {
                _peerLossPermille = UnpackLossPermille(packedLossJitter);
                _peerJitterMs = UnpackJitterMs(packedLossJitter);
            }
            if (packedDisplayRate > 0)
            {
                int hz = UnpackRefreshHz(packedDisplayRate);
                int tenths = UnpackFrameTenthsMs(packedDisplayRate);
                if (hz is >= 24 and <= 360) _peerRefreshHz = hz;
                if (tenths is >= 25 and <= 420) _peerFrameTenthsMs = tenths;
            }
        }
        return matched;
    }

    internal void RecordRttSample(double rttMs, long nowMs)
    {
        if (rttMs <= 0 || double.IsNaN(rttMs) || double.IsInfinity(rttMs)) return;
        lock (_rttGate)
            RecordRttSampleLocked(Math.Max(0.5, rttMs), nowMs);
    }

    private void RecordRttSampleLocked(double sampleRtt, long nowMs)
    {
        long windowLimit = string.Equals(_band, "2.4GHz", StringComparison.Ordinal)
            ? SlowBandMinRttWindowMs
            : FastMinRttWindowMs;
        if (_windowStartMs == 0)
        {
            _windowStartMs = nowMs;
        }
        else if (nowMs - _windowStartMs >= windowLimit)
        {
            _minRttWindowB = _minRttWindowA;
            _minRttWindowA = double.MaxValue;
            _windowStartMs = nowMs;
        }
        if (sampleRtt < _minRttWindowA) _minRttWindowA = sampleRtt;
        double minBest = Math.Min(_minRttWindowA, _minRttWindowB);

        double effectiveRtt = sampleRtt;
        if (minBest < 25.0 && sampleRtt > minBest + 10.0)
        {
            _consecutiveHighRttCount++;
            if (_consecutiveHighRttCount <= 3)
            {
                effectiveRtt = minBest + Math.Min(6.0, (sampleRtt - minBest) * 0.12);
            }
        }
        else
        {
            _consecutiveHighRttCount = 0;
        }

        if (_smoothedRttMs <= 0)
        {
            _smoothedRttMs = effectiveRtt;
            _rttJitterMs = 0;
        }
        else if (effectiveRtt <= _smoothedRttMs)
        {
            double baseDev = minBest < double.MaxValue ? Math.Abs(effectiveRtt - minBest) : 0.0;
            double clampedDev = Math.Min(baseDev, 6.0);
            _rttJitterMs += 0.42 * (clampedDev - _rttJitterMs);
            // Adapt down rapidly when an awake radio sample arrives without counting recovery as jitter.
            double alpha = effectiveRtt < _smoothedRttMs * 0.65 ? 0.82 : 0.55;
            _smoothedRttMs += alpha * (effectiveRtt - _smoothedRttMs);
        }
        else
        {
            double rawDev = Math.Abs(effectiveRtt - _smoothedRttMs);
            double maxDevCap = minBest < 25.0 ? 8.0 : Math.Max(25.0, _smoothedRttMs * 1.8);
            double clampedDev = Math.Min(rawDev, maxDevCap);
            _rttJitterMs += (clampedDev > _rttJitterMs ? 0.10 : 0.35) * (clampedDev - _rttJitterMs);
            double alpha = (minBest < 25.0 && _consecutiveHighRttCount <= 3) ? 0.08 : 0.15;
            _smoothedRttMs += alpha * (effectiveRtt - _smoothedRttMs);
        }
    }

    internal void RecordAuthenticatedSeqOnly(ulong seq)
    {
        lock (_recvGate)
            RecordSeqLocked(seq);
    }

    internal void RecordInboundPacket(ulong seq, bool streamTimingOp, long nowMs) =>
        RecordInboundPacket(seq, false, 0, nowMs);

    internal void RecordInboundPacket(ulong seq, bool hasSenderTimestamp, int senderTimestampMs, long nowMs)
    {
        lock (_recvGate)
        {
            if (nowMs > 0 && _lastArriveMs > 0 && nowMs > _lastArriveMs)
            {
                long gapMs = nowMs - _lastArriveMs;
                if (gapMs > 800)
                    _ewmaLossRatio *= Math.Pow(0.75, Math.Min(8.0, gapMs / 1000.0));
            }
            RecordSeqLocked(seq);
            if (nowMs <= 0) return;
            if (hasSenderTimestamp)
            {
                if (_hasLastTransit)
                {
                    long recvDelta = nowMs - _lastTransitRecvMs;
                    long sendDelta = unchecked(senderTimestampMs - _lastTransitSenderMs);
                    if (recvDelta is >= 0 and <= 250 && sendDelta is >= 0 and <= 45)
                    {
                        double transitDiff = Math.Min(110.0, Math.Abs((double)recvDelta - (double)sendDelta));
                        _inboundJitterMs += (transitDiff > _inboundJitterMs ? 0.22 : 0.36) * (transitDiff - _inboundJitterMs);
                    }
                    else if (recvDelta > 50 || sendDelta > 45)
                    {
                        _inboundJitterMs *= 0.78;
                    }
                }
                _lastTransitSenderMs = senderTimestampMs;
                _lastTransitRecvMs = nowMs;
                _hasLastTransit = true;
                _lastArriveMs = nowMs;
                return;
            }
            if (_lastArriveMs > 0 && nowMs >= _lastArriveMs)
            {
                long dt = nowMs - _lastArriveMs;
                if (dt > 400)
                    _inboundJitterMs *= 0.85;
            }
            _lastArriveMs = nowMs;
        }
    }

    private void RecordSeqLocked(ulong seq)
    {
        if (seq == 0) return;
        if (_maxSeq == 0)
        {
            _maxSeq = seq;
            _settledSeq = seq;
            _seenMask = 1UL;
            return;
        }
        if (seq > _maxSeq)
        {
            ulong diff = seq - _maxSeq;
            if (diff >= 64)
            {
                _seenMask = 1UL;
                ulong skipped = diff >= 65 ? 64UL : diff - 1;
                _lostPackets += (long)skipped;
                ulong ewmaSteps = Math.Min(8UL, skipped);
                for (ulong i = 0; i < ewmaSteps; i++)
                {
                    _ewmaLossRatio = _ewmaLossRatio * 0.94 + 0.06;
                }
                _settledSeq = seq;
            }
            else
            {
                _seenMask = (_seenMask << (int)diff) | 1UL;
            }
            _maxSeq = seq;
            SettleOldSlotsLocked();
        }
        else
        {
            ulong back = _maxSeq - seq;
            if (back < 64)
            {
                ulong bit = 1UL << (int)back;
                if ((_seenMask & bit) == 0)
                {
                    _seenMask |= bit;
                    if (back >= 1) _reorderedPackets++;
                }
            }
        }
    }

    private void SettleOldSlotsLocked()
    {
        if (_maxSeq <= ReorderSlack) return;
        ulong targetSettle = _maxSeq - ReorderSlack;
        if (targetSettle <= _settledSeq) return;
        ulong step = Math.Min(64UL, targetSettle - _settledSeq);
        ulong start = _settledSeq + 1;
        for (ulong i = 0; i < step; i++)
        {
            ulong s = start + i;
            ulong back = _maxSeq - s;
            bool seen = back < 64 && ((_seenMask & (1UL << (int)back)) != 0);
            if (seen)
            {
                _ewmaLossRatio *= 0.94;
            }
            else
            {
                _ewmaLossRatio = _ewmaLossRatio * 0.94 + 0.06;
                _lostPackets++;
            }
        }
        _settledSeq = targetSettle;
    }

    internal int GetInboundPackedLossJitter()
    {
        lock (_recvGate)
            return PackLossJitter(Math.Clamp(_ewmaLossRatio * 1000.0, 0.0, 1000.0), _inboundJitterMs);
    }

    internal double InboundLossPermille
    {
        get { lock (_recvGate) return Math.Clamp(_ewmaLossRatio * 1000.0, 0.0, 1000.0); }
    }

    internal double InboundJitterMs
    {
        get { lock (_recvGate) return _inboundJitterMs; }
    }

    internal long ReorderedPackets { get { lock (_recvGate) return _reorderedPackets; } }
    internal long LostPackets { get { lock (_recvGate) return _lostPackets; } }

    internal double SmoothedRttMs { get { lock (_rttGate) return _smoothedRttMs; } }

    internal double MinRttMs
    {
        get
        {
            lock (_rttGate)
            {
                double best = Math.Min(_minRttWindowA, _minRttWindowB);
                return best == double.MaxValue ? 0.0 : best;
            }
        }
    }

    internal double QueueDelayMs
    {
        get
        {
            lock (_rttGate)
            {
                double min = Math.Min(_minRttWindowA, _minRttWindowB);
                return _smoothedRttMs > 0 && min != double.MaxValue && _smoothedRttMs > min
                    ? _smoothedRttMs - min
                    : 0.0;
            }
        }
    }

    internal double EffectiveJitterMs
    {
        get
        {
            double inJ = InboundJitterMs;
            lock (_rttGate) return Math.Max(inJ, Math.Max(_rttJitterMs, _peerJitterMs));
        }
    }

    internal double CombinedLossPermille
    {
        get
        {
            double inL = InboundLossPermille;
            lock (_rttGate) return Math.Max(inL, _peerLossPermille);
        }
    }

    internal int PeerRefreshHz { get { lock (_rttGate) return _peerRefreshHz; } }
    internal int PeerFrameTenthsMs { get { lock (_rttGate) return _peerFrameTenthsMs; } }

    internal static int PackLossJitter(double lossPermille, double jitterMs)
    {
        int loss = Math.Clamp((int)Math.Round(lossPermille), 0, 65535);
        int jitterTenths = Math.Clamp((int)Math.Round(jitterMs * 10.0), 0, 65535);
        return (loss << 16) | jitterTenths;
    }

    internal static double UnpackLossPermille(int packed) => (uint)packed >> 16;
    internal static double UnpackJitterMs(int packed) => (packed & 0xFFFF) / 10.0;

    internal static int PackDisplayRate(int refreshHz, int frameTenthsMs)
    {
        int hz = Math.Clamp(refreshHz, 0, 65535);
        int ft = Math.Clamp(frameTenthsMs, 0, 65535);
        return (hz << 16) | ft;
    }

    internal static int UnpackRefreshHz(int packed) => (int)((uint)packed >> 16);
    internal static int UnpackFrameTenthsMs(int packed) => packed & 0xFFFF;
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
        try { socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService, 0xB8); }
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
        private IPAddress _expectedAddress;
        internal readonly byte[] Key;
        internal readonly FastUdpCodec Codec;
        internal readonly AdaptiveLinkTracker LinkTracker = new();
        private readonly byte[] _sendBuffer = new byte[FastUdpCodec.FrameSize];
        private IPEndPoint? _remoteEndPoint;
        internal volatile bool Confirmed;
        private int _confirmedLogged;
        internal long LastMoveSentTicks;
        internal long LastLinkPingMs;
        internal int PendingWakePing;
        internal long LastWakePrimerMs;
        internal ulong HighestMoveSeq;

        internal bool TryMarkConfirmedLogged() =>
            Interlocked.CompareExchange(ref _confirmedLogged, 1, 0) == 0;

        internal IPAddress ExpectedAddress => Volatile.Read(ref _expectedAddress);

        internal Peer(int sessionId, IPAddress expectedAddress)
        {
            SessionId = sessionId;
            WirePeerId = unchecked((ushort)sessionId);
            _expectedAddress = FastUdpSocketOptions.NormalizeAddress(expectedAddress);
            Key = RandomNumberGenerator.GetBytes(FastUdpCodec.KeySize);
            Codec = new FastUdpCodec(Key, WirePeerId, isServer: true);
        }

        internal void UpdateEndpointOnAuthenticatedPacket(IPAddress normalized, int port)
        {
            var prevExpected = Volatile.Read(ref _expectedAddress);
            if (!normalized.Equals(prevExpected))
                Volatile.Write(ref _expectedAddress, normalized);
            var currentEp = Volatile.Read(ref _remoteEndPoint);
            if (currentEp == null || !currentEp.Address.Equals(normalized) || currentEp.Port != port)
                Volatile.Write(ref _remoteEndPoint, new IPEndPoint(normalized, port));
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
                if (!peer.Codec.TryOpen(buffer.AsSpan(0, received), out var msg, peer.LinkTracker)) continue;

                peer.UpdateEndpointOnAuthenticatedPacket(normalized, srcIp.Port);

                long nowMs = Environment.TickCount64;
                peer.LinkTracker.RecordInboundPacket(
                    msg.Seq, AdaptiveLinkTracker.HasSenderTimestamp(msg.Op), msg.TimestampMs, nowMs);

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
                else if (msg.Op == FastUdpOp.LinkFeedback)
                {
                    peer.Confirmed = true;
                    peer.LinkTracker.OnFeedbackReceived(msg.A, msg.B, msg.TimestampMs, nowMs);
                    MessageReceived?.Invoke(peer, msg);
                }
                else if (msg.Op is FastUdpOp.Move or FastUdpOp.ReverseMove)
                {
                    if (msg.Seq <= peer.HighestMoveSeq) continue;
                    peer.HighestMoveSeq = msg.Seq;
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
    private volatile IPEndPoint _serverEndPoint;
    private readonly FastUdpCodec _codec;
    internal readonly AdaptiveLinkTracker LinkTracker = new();
    private readonly byte[] _sendBuffer = new byte[FastUdpCodec.FrameSize];
    private readonly Thread _recvThread;
    private volatile bool _disposed;
    internal volatile bool Confirmed;
    private ulong _highestMoveSeq;
    internal long LastLinkPingMs;

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
                if (!_codec.TryOpen(buffer.AsSpan(0, received), out var msg)) continue;
                var currentEp = _serverEndPoint;
                if (!normalized.Equals(currentEp.Address) || srcIp.Port != currentEp.Port)
                    _serverEndPoint = new IPEndPoint(normalized, srcIp.Port);
                long nowMs = Environment.TickCount64;
                LinkTracker.RecordInboundPacket(
                    msg.Seq, AdaptiveLinkTracker.HasSenderTimestamp(msg.Op), msg.TimestampMs, nowMs);
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
                else if (msg.Op == FastUdpOp.LinkFeedback)
                {
                    if (!Confirmed)
                    {
                        Confirmed = true;
                        ConfirmedChanged?.Invoke();
                    }
                    LinkTracker.OnFeedbackReceived(msg.A, msg.B, msg.TimestampMs, nowMs);
                    MessageReceived?.Invoke(msg);
                }
                else if (msg.Op is FastUdpOp.Move or FastUdpOp.ReverseMove)
                {
                    if (msg.Seq <= _highestMoveSeq) continue;
                    _highestMoveSeq = msg.Seq;
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
    internal static int PackEdgePush(int pushX, int pushY)
    {
        int cx = Math.Clamp(pushX, -240, 240);
        int cy = Math.Clamp(pushY, -240, 240);
        return ((cx & 0xFFFF) << 16) | (cy & 0xFFFF);
    }

    internal static (int PushX, int PushY) UnpackEdgePush(int packed) =>
        ((short)(packed >> 16), (short)(packed & 0xFFFF));

    /// <summary>
    /// Packs the previous (-) and current (+) screen coordinate of one axis into one int:
    /// low 16 bits = current, high 16 bits = previous + 1 (0 = no previous). A plain
    /// non-negative coordinate decodes unchanged, so older senders stay compatible.
    /// </summary>
    internal static int PackDualCoord(int prevCoord, int currCoord)
    {
        if (currCoord is < 0 or > 16383) return currCoord;
        if (prevCoord is < 0 or > 16383 || prevCoord == currCoord) return currCoord;
        return ((prevCoord + 1) << 16) | currCoord;
    }

    internal static (int Prev, int Curr) UnpackDualCoord(int packed)
    {
        int hi = (int)((uint)packed >> 16);
        if (hi == 0 || hi > 16384) return (packed, packed);
        int curr = packed & 0xFFFF;
        return (hi - 1, curr);
    }

    private const double MinUdpIntervalMs = 1.5;
    private const int TrailingTcpSyncDelayMs = 130;
    private const int WarmKeepaliveIntervalMs = 22;
    private const int WarmCooldownSeconds = 2;

    private readonly object _gate = new();
    private readonly object _udpSendGate = new();
    private readonly Func<bool> _canSendUdp;
    private readonly Func<Packet, bool> _sendUdp;
    private readonly Action<Packet> _queueReliableTcp;
    private readonly Action<Packet> _queueTrailingSync;
    private readonly bool _enableTrailingSync;
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
        Func<int>? nextSeq = null,
        bool enableTrailingSync = true)
    {
        _canSendUdp = canSendUdp;
        _sendUdp = sendUdp;
        _queueReliableTcp = queueReliableTcp;
        _queueTrailingSync = queueTrailingSync ?? queueReliableTcp;
        _enableTrailingSync = enableTrailingSync;
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

    internal static Packet MergeCoalescedMove(Packet previous, Packet incoming)
    {
        var (oldPx, oldPy) = UnpackEdgePush(previous.D);
        var (newPx, newPy) = UnpackEdgePush(incoming.D);
        if (oldPx == 0 && oldPy == 0)
            return incoming;
        int mergedX = previous.A == incoming.A && newPx != 0
            ? Math.Clamp(oldPx + newPx, -240, 240)
            : newPx;
        int mergedY = previous.B == incoming.B && newPy != 0
            ? Math.Clamp(oldPy + newPy, -240, 240)
            : newPy;
        return incoming with { D = PackEdgePush(mergedX, mergedY) };
    }

    internal bool TryHandleMove(Packet move)
    {
        if (!_canSendUdp()) return false;
        bool sendNow = false;
        long order;
        Packet dispatchPacket = move;
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
            if (elapsedMs >= minInterval - 0.45)
            {
                if (_throttledUdp != null)
                    dispatchPacket = MergeCoalescedMove(_throttledUdp, move);
                _throttledUdp = null;
                _throttledOrder = 0;
                _pendingTcpSync = _enableTrailingSync ? dispatchPacket : null;
                _lastMoveUdpTicks = now;
                sendNow = true;
            }
            else
            {
                _throttledUdp = _throttledUdp != null
                    ? MergeCoalescedMove(_throttledUdp, move)
                    : move;
                _throttledOrder = order;
                _pendingTcpSync = _enableTrailingSync ? _throttledUdp : null;
            }
            UpdateTimerLocked();
        }
        if (sendNow && DispatchUdpMove(dispatchPacket, order))
            Interlocked.Increment(ref _udpDirectCount);
        return true;
    }

    private double _effectiveMinIntervalMs = -1;
    private int _effectiveWarmIntervalMs = WarmKeepaliveIntervalMs;
    private long _udpDirectCount, _udpFlushedCount, _trailingTcpSyncCount;

    internal double EffectiveMinIntervalMs
    {
        get { lock (_gate) return ActiveMinIntervalMsLocked(); }
    }

    internal void SetMinIntervalMs(double ms)
    {
        lock (_gate)
            _effectiveMinIntervalMs = ms > 0 ? ms : -1;
    }

    internal void SetWarmIntervalMs(int ms)
    {
        int clamped = Math.Clamp(ms, 18, 120);
        lock (_gate)
        {
            if (_effectiveWarmIntervalMs == clamped) return;
            _effectiveWarmIntervalMs = clamped;
            if (!_disposed && _throttledUdp == null && _pendingTcpSync == null &&
                IsWarmWindowOpenLocked(Stopwatch.GetTimestamp()) && _sendWarm != null)
            {
                UpdateTimerLocked();
            }
        }
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
                if (idleWarmMs >= _effectiveWarmIntervalMs - 2)
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
            dueMs = Math.Max(1, (int)Math.Round(minInterval - elapsedMs));
        }
        else
        {
            long now = Stopwatch.GetTimestamp();
            dueMs = _pendingTcpSync != null
                ? TrailingTcpSyncDelayMs
                : IsWarmWindowOpenLocked(now) && _sendWarm != null
                    ? _effectiveWarmIntervalMs
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

