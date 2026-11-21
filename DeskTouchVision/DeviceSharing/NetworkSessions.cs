using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
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
    int? DensityDpi = null, int? RefreshHz = null, int? RotationDegrees = null,
    int NotificationCount = -1,
    int BatteryPercent = -1,
    bool IsCharging = false,
    string SignalLabel = "",
    bool HasCamera = false,
    bool HasMicrophone = false)
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
    int? DensityDpi = null, int? RefreshHz = null, int? RotationDegrees = null,
    int? NotificationCount = null,
    bool? HasCamera = null,
    bool? HasMicrophone = null);

// Issued exclusively by the authenticated hub over an existing TLS session.
// Id is a connection-scoped route identifier, not a device identity or trust proof.
internal sealed record EnvironmentMember(int Id, string Name, string Platform,
    string Layout, bool SupportsReverseInput, string? StableIdentity=null,
    int NotificationCount = -1,
    int ArrangementOrder = -1,
    double LayoutX = 0,
    double LayoutY = 0,
    double LayoutW = 0,
    double LayoutH = 0,
    int BatteryPercent = -1,
    bool IsCharging = false,
    bool HasCamera = false,
    bool HasMicrophone = false);
internal sealed record EnvironmentPosition(string Key,float X,float Y)
{
    internal static bool Valid(EnvironmentPosition? p) =>
        p!=null && !string.IsNullOrWhiteSpace(p.Key) && p.Key.Length<=150 &&
        !p.Key.Any(char.IsControl) && float.IsFinite(p.X) && float.IsFinite(p.Y) &&
        Math.Abs(p.X)<=80000 && Math.Abs(p.Y)<=80000;
}

internal sealed record SubPeerDescriptor(int LocalId, string StableIdentity, string Name,
    string Platform, string Layout, bool SupportsReverseInput, string? AppVersion = null,
    int NotificationCount = -1,
    int BatteryPercent = -1,
    bool IsCharging = false,
    bool HasCamera = false,
    bool HasMicrophone = false);

internal sealed record SubRouteEnvelope(int LocalId, Packet Payload);

internal sealed record MutualTrustProposal(
    string OutgoingToken,
    string ReciprocalToken,
    string PeerFingerprint,
    string CertBase64,
    string SignatureBase64);

internal static class MutualTrustCodec
{
    internal static bool SupportsBidirectionalProtocol(string? appVersion) =>
        Version.TryParse(appVersion, out var v) && v >= new Version(0, 3, 54);

    internal static string SignProposal(X509Certificate2 ownIdentity,
        string targetFingerprint, string outgoingToken, string reciprocalToken)
    {
        if (string.IsNullOrWhiteSpace(targetFingerprint) || targetFingerprint.Length != 64 ||
            !targetFingerprint.All(Uri.IsHexDigit))
            throw new ArgumentException("Valid 64-hex target fingerprint required.");
        string ownFp = Convert.ToHexString(SHA256.HashData(ownIdentity.RawData)).ToUpperInvariant();
        string targetUpper = targetFingerprint.ToUpperInvariant();
        byte[] message = System.Text.Encoding.UTF8.GetBytes(
            $"DT1-MUTUAL-TRUST|{ownFp}|{targetUpper}|{outgoingToken.ToUpperInvariant()}|{reciprocalToken.ToUpperInvariant()}");
        using RSA? rsa = ownIdentity.GetRSAPrivateKey();
        if (rsa == null)
            throw new InvalidOperationException("Local TLS identity lacks RSA private key.");
        byte[] sig = rsa.SignData(message, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return JsonSerializer.Serialize(new MutualTrustProposal(
            outgoingToken.ToUpperInvariant(),
            reciprocalToken.ToUpperInvariant(),
            ownFp,
            Convert.ToBase64String(ownIdentity.RawData),
            Convert.ToBase64String(sig)));
    }

    internal static bool TryVerifyProposal(string? json, string expectedTargetFingerprint,
        out MutualTrustProposal? verified)
    {
        verified = null;
        if (string.IsNullOrWhiteSpace(json) || json.Length > 7500 ||
            string.IsNullOrWhiteSpace(expectedTargetFingerprint) ||
            expectedTargetFingerprint.Length != 64 || !expectedTargetFingerprint.All(Uri.IsHexDigit))
            return false;
        MutualTrustProposal? proposal;
        try { proposal = JsonSerializer.Deserialize<MutualTrustProposal>(json); }
        catch { return false; }
        if (proposal == null ||
            proposal.OutgoingToken is not { Length: 64 } || !proposal.OutgoingToken.All(Uri.IsHexDigit) ||
            proposal.ReciprocalToken is not { Length: 64 } || !proposal.ReciprocalToken.All(Uri.IsHexDigit) ||
            proposal.PeerFingerprint is not { Length: 64 } || !proposal.PeerFingerprint.All(Uri.IsHexDigit) ||
            string.IsNullOrWhiteSpace(proposal.CertBase64) ||
            string.IsNullOrWhiteSpace(proposal.SignatureBase64))
            return false;
        try
        {
            byte[] certBytes = Convert.FromBase64String(proposal.CertBase64);
            using var cert = new X509Certificate2(certBytes);
            string certFp = Convert.ToHexString(SHA256.HashData(cert.RawData)).ToUpperInvariant();
            if (!certFp.Equals(proposal.PeerFingerprint, StringComparison.OrdinalIgnoreCase))
                return false;
            using RSA? rsa = cert.GetRSAPublicKey();
            if (rsa == null) return false;
            byte[] message = System.Text.Encoding.UTF8.GetBytes(
                $"DT1-MUTUAL-TRUST|{certFp}|{expectedTargetFingerprint.ToUpperInvariant()}|{proposal.OutgoingToken.ToUpperInvariant()}|{proposal.ReciprocalToken.ToUpperInvariant()}");
            byte[] sig = Convert.FromBase64String(proposal.SignatureBase64);
            if (!rsa.VerifyData(message, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                return false;
            verified = proposal with
            {
                OutgoingToken = proposal.OutgoingToken.ToUpperInvariant(),
                ReciprocalToken = proposal.ReciprocalToken.ToUpperInvariant(),
                PeerFingerprint = certFp
            };
            return true;
        }
        catch { return false; }
    }
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
        internal FastUdpHub.Peer? MobileUdpPeer;
        internal int PreferMobileMouse;
        internal int PreferMobileKeyboard;
        internal int DesiredPreferMobileMouse;
        internal int DesiredPreferMobileKeyboard;
        internal int DualPathControls = 1;
        internal int KeepChannelsWarm = 1;
        internal long DualMouseMirrorUntilTicks;
        internal long DualKeyboardMirrorUntilTicks;
        internal FastMovePacer? MovePacer;
        internal long TcpFallbackMoves;
        internal bool IsCableSession;
        internal IPAddress? RemoteAddress;
        internal long CableMoves;
        internal long LastTcpPingMs;
        internal double TcpRttMs;
        internal int CableDetachedOrUnresponsive;
        internal int UrgentControlWaiters;
        internal int CablePingInFlight;
        internal AdaptiveLinkTracker CableTracker = new("cable");
        internal long LastPongTicks;
        internal long LastWriteTicks;
        internal string LinkBand = "wireless";
        internal int LinkSpeedMbps;
        internal int NetworkEpoch;
        internal string? KeyboardInfoJson;
        internal string? TrustedTokenHash;
        internal readonly IDisposable HighResTimer = Win.EnterHighResolutionTimer();
        internal int ActiveReverseLease;
        internal int Lease;
        internal int AppliedLease;
        internal int KeyboardFailureReported;
        internal int EnvironmentTargetId; // 0 is the local Windows hub
        internal readonly object ReverseMoveOrderLock = new();
        internal int ReverseActionSeq;
        internal int ReverseMoveSeq;
        internal int ReverseClaimDispatchSeq;
        internal volatile bool HubGrantedAcked;
        internal volatile bool HubActiveAcked;
        internal int PendingFirstCursorAck;
        internal long LastAndroidWarmEchoTicks;
        internal int LiveRefreshHz;
        internal int LiveFrameTenthsMs;
        internal int PendingTrustRequest;
        internal int PendingOfferedTrustId;
        internal string? PendingOfferedTrustToken;
        internal TaskCompletionSource<bool>? PendingTrustOffer;
        internal string? PendingOfferedTrustResponse;
        internal int BridgedClaimLease;
        internal Func<Packet, Task>? BridgedReplyAsync;
        internal readonly object ForwardDedupLock = new();
        internal int ForwardHighestActionSeq;
        internal long ForwardSeenActionMask;
        internal int ForwardLastButtonSeq;
        internal int ForwardHighestKeySeq;
        internal long ForwardSeenKeyMask;
        internal readonly object TaskbarGate = new();
        internal readonly HashSet<string> SentTaskbarIcons = new(StringComparer.Ordinal);
        internal string LastSentTaskbarSig = "";
        internal int LastSentTaskbarTarget = -1;
        internal bool TaskbarWatchSent;
        internal int ScreenPreviewTarget = -1;
        internal int ScreenPreviewMonitor = -1;
        internal int ScreenPreviewHighRes = 0;
        internal int LowBatteryAlertSent;
        internal int WatchingCameraTargetId = -1;
        internal int WatchingMicTargetId = -1;
        internal int WatchingCameraLens;
        internal bool WatchingCameraTorch;

        internal void ResetForwardDedup()
        {
            lock (ForwardDedupLock)
            {
                ForwardHighestActionSeq = 0;
                ForwardSeenActionMask = 0;
                ForwardLastButtonSeq = 0;
                ForwardHighestKeySeq = 0;
                ForwardSeenKeyMask = 0;
            }
        }

        internal bool TryAcceptForwardAction(int seq, bool isButton)
        {
            if (seq <= 0) return true;
            lock (ForwardDedupLock)
            {
                if (isButton && seq <= ForwardLastButtonSeq) return false;
                if (ForwardHighestActionSeq == 0)
                {
                    ForwardHighestActionSeq = seq;
                    ForwardSeenActionMask = 1L;
                }
                else
                {
                    int diff = seq - ForwardHighestActionSeq;
                    if (diff > 0)
                    {
                        ForwardSeenActionMask = diff >= 64 ? 1L : ((ForwardSeenActionMask << diff) | 1L);
                        ForwardHighestActionSeq = seq;
                    }
                    else
                    {
                        int back = -diff;
                        if (back >= 64) return false;
                        long bit = 1L << back;
                        if ((ForwardSeenActionMask & bit) != 0L) return false;
                        ForwardSeenActionMask |= bit;
                    }
                }
                if (isButton) ForwardLastButtonSeq = seq;
                return true;
            }
        }

        internal bool TryAcceptForwardKeyAction(int seq)
        {
            if (seq <= 0) return true;
            lock (ForwardDedupLock)
            {
                if (ForwardHighestKeySeq == 0)
                {
                    ForwardHighestKeySeq = seq;
                    ForwardSeenKeyMask = 1L;
                }
                else
                {
                    int diff = seq - ForwardHighestKeySeq;
                    if (diff > 0)
                    {
                        ForwardSeenKeyMask = diff >= 64 ? 1L : ((ForwardSeenKeyMask << diff) | 1L);
                        ForwardHighestKeySeq = seq;
                    }
                    else
                    {
                        int back = -diff;
                        if (back >= 64) return false;
                        long bit = 1L << back;
                        if ((ForwardSeenKeyMask & bit) != 0L) return false;
                        ForwardSeenKeyMask |= bit;
                    }
                }
                return true;
            }
        }

        internal int NextReverseActionSeq()
        {
            int seq = Interlocked.Increment(ref ReverseActionSeq);
            if (seq <= 0)
            {
                Interlocked.Exchange(ref ReverseActionSeq, 1);
                return 1;
            }
            return seq;
        }

        internal int NextReverseMoveSeq()
        {
            int seq = Interlocked.Increment(ref ReverseMoveSeq);
            if (seq <= 0)
            {
                Interlocked.Exchange(ref ReverseMoveSeq, 1);
                return 1;
            }
            return seq;
        }

        // Last cursor position actually written to this phone (any transport) and
        // the most recent position that was coalesced away before reaching the wire.
        // Every move packet to a >= 0.3.57 phone carries BOTH the previous (-) and
        // current (+) position so a lost/merged packet is reconstructed, not skipped.
        internal int LastWireMoveX = -1, LastWireMoveY = -1, LastWireMoveLease;
        internal int TcpWaypointX = -1, TcpWaypointY = -1;

        internal bool MatchesLastWireMove(Packet move)
        {
            if (move.Type != "move" || move.D != 0) return false;
            lock (ReverseMoveOrderLock)
            {
                return Volatile.Read(ref ActiveReverseLease) == LastWireMoveLease &&
                       LastWireMoveX == move.A &&
                       LastWireMoveY == move.B;
            }
        }

        internal void SetLatestReverseMoveOrdered(ref Packet? slot, Packet move, bool trackWaypoint = false)
        {
            lock (ReverseMoveOrderLock)
            {
                Packet stamped = move.C > 0 ? move : move with { C = NextReverseMoveSeq() };
                if (slot == null)
                    slot = stamped;
                else if (stamped.C >= slot.C)
                {
                    if (trackWaypoint && (slot.A != stamped.A || slot.B != stamped.B))
                    {
                        TcpWaypointX = slot.A;
                        TcpWaypointY = slot.B;
                    }
                    slot = FastMovePacer.MergeCoalescedMove(slot, stamped);
                }
            }
        }

        /// <summary>Returns the (-) position for a move about to be written and records (+) as sent.</summary>
        internal (int PrevX, int PrevY) TakeWirePrevious(int currX, int currY, bool preferWaypoint)
        {
            lock (ReverseMoveOrderLock)
            {
                int lease = Volatile.Read(ref ActiveReverseLease);
                int prevX = -1, prevY = -1;
                if (lease == LastWireMoveLease)
                {
                    if (preferWaypoint && TcpWaypointX >= 0)
                    {
                        prevX = TcpWaypointX;
                        prevY = TcpWaypointY;
                    }
                    else
                    {
                        prevX = LastWireMoveX;
                        prevY = LastWireMoveY;
                    }
                }
                TcpWaypointX = TcpWaypointY = -1;
                LastWireMoveX = currX;
                LastWireMoveY = currY;
                LastWireMoveLease = lease;
                return (prevX, prevY);
            }
        }

        internal int NextReverseClaimDispatchSeq()
        {
            int seq = Interlocked.Increment(ref ReverseClaimDispatchSeq);
            if (seq <= 0)
            {
                Interlocked.Exchange(ref ReverseClaimDispatchSeq, 1);
                return 1;
            }
            return seq;
        }
    }

    private sealed class RelayLease
    {
        internal required Session Source;
        internal required Session Target;
        internal required int Claim;
        internal bool Granted;
        internal bool GrantedAcked;
        internal bool ActiveAcked;
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
    private readonly TcpListener _listener = CreateDualStackListener(Wire.Port);

    private static TcpListener CreateDualStackListener(int port)
    {
        try
        {
            return TcpListener.Create(port);
        }
        catch (Exception)
        {
            return new TcpListener(IPAddress.Any, port);
        }
    }

    internal static IPAddress? UnmapAddress(IPAddress? address) =>
        address != null && address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    private readonly X509Certificate2 _certificate;
    private readonly TrustedDeviceStore? _trusted;
    private readonly LocalInputPriority _priority;
    private readonly Func<string,CancellationToken,Task<bool>>? _requestApproval;
    private readonly Func<string,CancellationToken,Task<bool>>? _requestTrustApproval;
    private readonly ConcurrentDictionary<int,TcpClient> _clients = new();
    private readonly ConcurrentDictionary<int,Session> _sessions = new();
    private readonly object _authorityGate = new();
    private readonly object _layoutGate=new();
    private readonly object _shellAuthGate = new();
    private (int Port, string Secret)? _shellBridgeAuth;
    private (string State, string Detail, int PairPort, int ConnectPort) _bridgeStatus =
        ("debug_off", "", 0, 0);
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
    private sealed class BridgedSubPeer
    {
        internal required int VirtualId;
        internal required Session BridgeSession;
        internal required SubPeerDescriptor Descriptor;
        internal required MonitorTopology Topology;
    }

    private readonly object _bridgedLock = new();
    private readonly Dictionary<int, List<SubPeerDescriptor>> _subPeersBySession = new();
    private readonly Dictionary<int, BridgedSubPeer> _bridgedByVirtualId = new();
    private readonly Dictionary<string, int> _virtualIdByStableIdentity = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, (int Lease, Session BridgeSession, int LocalSubId)> _virtualHubClaims = new();
    private readonly Dictionary<int, (int VirtualTargetId, int Claim, Session Source, Session BridgeSession, int LocalSubId)> _virtualRelayBySource = new();
    private int _nextVirtualSubId = 10000;
    private int _selectedVirtualSubId;
    private (EnvironmentMember[] Members, int OwnId)? _upstreamRosterOverlay;
    private readonly object _spatialGate = new();
    private Dictionary<string, NormalizedSpatialBox> _environmentSpatialLayout = new(StringComparer.OrdinalIgnoreCase);
    private volatile string _customPairingCode = "";
    private readonly object _customPairAttemptLock = new();
    private long _customPairWindowStartTicks;
    private int _customPairFailedAttempts;
    private sealed class PeerTaskbarCache
    {
        internal string StateJson = "";
        internal string Signature = "";
        internal readonly Dictionary<string, string> Icons = new(StringComparer.Ordinal);
    }
    private readonly object _taskbarGate = new();
    private readonly Dictionary<int, PeerTaskbarCache> _peerTaskbars = new();
    private int _localTaskbarNudge;
    private readonly FileDropHelper _fileDrop = new();
    private readonly Dictionary<int, NotificationPreviewItem[]> _peerNotifications = new();
    private readonly object _notifGate = new();
    private readonly ConcurrentDictionary<string, string[]> _macsByFingerprint = new(StringComparer.OrdinalIgnoreCase);
    private string _lastBroadcastForeground = "";
    private string _latestProfileStateJson = "";
    private readonly FileDropHelper.BufferedChunkAssembler _phoneScreenAssembler = new();
    private readonly FileDropHelper.BufferedChunkAssembler _phoneCameraAssembler = new();
    private readonly FastMediaFrameAssembler _fastMediaAssembler = new();
    private readonly SemaphoreSlim _previewWake = new(0, 1);
    private readonly object _mediaWatchGate = new();
    private int _hubCameraWatchPeerId = -1;
    private int _hubCameraWatchLens;
    private bool _hubCameraWatchTorch;
    private int _hubMicWatchPeerId = -1;
    private WindowsMediaCaptureService? _localMediaCapture;
    private int _localMediaCamFrameSeq;
    private int _localMediaMicAudioSeq;
    private int _localMediaCamStatus;
    private int _localMediaMicStatus;
    private int _phoneTouchSeq;

    internal event Action<string, string>? ClipboardTextReceived;
    internal event Action<string, byte[]>? ClipboardImageReceived;
    internal event Action<string, string>? FileReceived;
    internal event Action<int, NotificationPreviewItem[]>? PeerNotificationsUpdated;
    internal event Action<string, string, string>? OtpOfferReceived;
    internal event Action<string, int, bool>? LowBatteryDetected;
    internal event Action<string>? RemoteProfileApplyRequested;
    internal event Action<string, string>? ForegroundAppChanged;
    internal event Action<int, byte[]>? PhoneScreenFrameReceived;
    internal event Action<int, int, string?>? PhoneScreenStatusReceived;
    internal event Action<int, byte[]>? PhoneCameraFrameReceived;
    internal event Action<int, int, string?>? PhoneCameraStatusReceived;
    internal event Action<int, byte[], int, int>? PhoneMicAudioReceived;
    internal event Action<int, int, string?>? PhoneMicStatusReceived;
    internal event Action<bool, bool, string>? LocalMediaShareStateChanged;
    internal event Action<string?, bool, bool, int, int, bool, bool>? AndroidBridgeHintReceived;
    internal event Action? ShellBridgeRefreshRequested;
    internal event Func<string?, int, string, int, Task<(bool Ok, string Message)>>? WirelessAdbPairRequested;

    private static FastUdpHub.Peer? ActiveMediaUdpPeer(Session session)
    {
        var wifi = session.UdpPeer is { Confirmed: true } u ? u : null;
        var mob = session.MobileUdpPeer is { Confirmed: true } m ? m : null;
        if (wifi == null) return mob;
        if (mob == null) return wifi;

        double wifiLoss = wifi.LinkTracker.CombinedLossPermille;
        double mobLoss = mob.LinkTracker.CombinedLossPermille;
        if (Math.Abs(wifiLoss - mobLoss) >= 15.0)
            return mobLoss < wifiLoss ? mob : wifi;

        double wifiRtt = wifi.LinkTracker.SmoothedRttMs;
        double mobRtt = mob.LinkTracker.SmoothedRttMs;
        if (Volatile.Read(ref session.PreferMobileMouse) != 0 &&
            (mobRtt <= 0 || wifiRtt <= 0 || mobRtt <= wifiRtt + 25.0))
            return mob;

        if (mobRtt > 0 && wifiRtt > 0 && mobRtt + 6.0 < wifiRtt)
            return mob;

        return wifi;
    }

    internal bool IsFastMediaUdpActive(int peerId) =>
        _udpHub != null && _sessions.TryGetValue(peerId, out var s) && ActiveMediaUdpPeer(s) != null;

    internal string GetMediaTransportLabel(int peerId)
    {
        if (_udpHub == null || !_sessions.TryGetValue(peerId, out var s))
            return "TCP Fallback";
        var active = ActiveMediaUdpPeer(s);
        if (active == null) return "TCP Fallback";
        if (s.IsCableSession) return "Fast UDP (Cable)";
        if (ReferenceEquals(active, s.MobileUdpPeer)) return "Fast UDP (Mobile)";
        return s.Peer.Platform == "Android" ? "Fast UDP (Wi-Fi)" : "Fast UDP (LAN)";
    }

    private static bool SendDualOrBestMicUdp(
        FastUdpHub? hub,
        Session watcher,
        int audioSeq,
        int sampleRate,
        int sourcePeerId,
        ReadOnlySpan<byte> pcm)
    {
        if (hub == null || pcm.Length > FastUdpCodec.MaxMediaPayloadBytes)
            return false;
        bool sent = false;
        if (watcher.UdpPeer is { Confirmed: true } wifi)
            sent |= hub.SendMedia(wifi, FastUdpMediaKind.MicAudio, audioSeq, sampleRate, 1, sourcePeerId, pcm);
        if (watcher.MobileUdpPeer is { Confirmed: true } mob && !ReferenceEquals(mob, watcher.UdpPeer))
            sent |= hub.SendMedia(mob, FastUdpMediaKind.MicAudio, audioSeq, sampleRate, 1, sourcePeerId, pcm);
        return sent;
    }

    internal static void SendUdpMediaChunks(
        FastUdpHub hub,
        FastUdpHub.Peer udpPeer,
        FastUdpMediaKind kind,
        int frameId,
        ReadOnlySpan<byte> payload,
        int metaOrRouteId)
    {
        int maxChunk = FastUdpCodec.MaxMediaPayloadBytes;
        int total = (payload.Length + maxChunk - 1) / maxChunk;
        if (total is < 1 or > 256) return;
        for (int i = 0; i < total; i++)
        {
            int offset = i * maxChunk;
            int len = Math.Min(maxChunk, payload.Length - offset);
            hub.SendMedia(udpPeer, kind, frameId, i, total, metaOrRouteId, payload.Slice(offset, len));
            if ((i & 7) == 7) Thread.Yield();
        }
    }

    internal static void SendUdpClientMediaChunks(
        FastUdpClient client,
        FastUdpMediaKind kind,
        int frameId,
        ReadOnlySpan<byte> payload,
        int metaOrRouteId)
    {
        int maxChunk = FastUdpCodec.MaxMediaPayloadBytes;
        int total = (payload.Length + maxChunk - 1) / maxChunk;
        if (total is < 1 or > 256) return;
        for (int i = 0; i < total; i++)
        {
            int offset = i * maxChunk;
            int len = Math.Min(maxChunk, payload.Length - offset);
            client.SendMedia(kind, frameId, i, total, metaOrRouteId, payload.Slice(offset, len));
            if ((i & 7) == 7) Thread.Yield();
        }
    }

    internal void SendPhoneScreenWatch(int targetPeerId, bool enable)
    {
        if (!_sessions.TryGetValue(targetPeerId, out var target)) return;
        _ = Task.Run(async () =>
        {
            try { await SendToAsync(target, new Packet("phone-screen-watch", enable ? 1 : 0, 540), _cancel.Token); }
            catch { }
        });
    }

    internal void SendPhoneTouch(int targetPeerId, int action, int normX, int normY)
    {
        if (!_sessions.TryGetValue(targetPeerId, out var target)) return;
        int seq = Interlocked.Increment(ref _phoneTouchSeq);
        _ = Task.Run(async () =>
        {
            try { await SendToAsync(target, new Packet("phone-touch", action, normX, normY, seq), _cancel.Token); }
            catch { }
        });
    }

    internal void SendPhoneNav(int targetPeerId, int navCode)
    {
        if (!_sessions.TryGetValue(targetPeerId, out var target)) return;
        _ = Task.Run(async () =>
        {
            try { await SendToAsync(target, new Packet("phone-nav", navCode), _cancel.Token); }
            catch { }
        });
    }

    internal void SendPhoneText(int targetPeerId, string text)
    {
        if (string.IsNullOrEmpty(text) || !_sessions.TryGetValue(targetPeerId, out var target)) return;
        string clean = text.Length > 500 ? text[..500] : text;
        _ = Task.Run(async () =>
        {
            try { await SendToAsync(target, new Packet("phone-text", 0, 0, 0, 0, clean), _cancel.Token); }
            catch { }
        });
    }

    internal void SendPhoneCameraWatch(int targetPeerId, bool enable, int lens, bool torch, bool mic)
    {
        int prevCam;
        int prevMic;
        lock (_mediaWatchGate)
        {
            prevCam = _hubCameraWatchPeerId;
            prevMic = _hubMicWatchPeerId;
            _hubCameraWatchPeerId = enable ? targetPeerId : -1;
            _hubCameraWatchLens = lens;
            _hubCameraWatchTorch = torch;
            if (mic)
                _hubMicWatchPeerId = targetPeerId;
            else if (_hubMicWatchPeerId == targetPeerId)
                _hubMicWatchPeerId = -1;
        }
        if (!enable)
            _fastMediaAssembler.ClearSource(targetPeerId);
        if (prevCam > 0 && prevCam != targetPeerId)
            DispatchTargetMediaWatch(prevCam);
        if (prevMic > 0 && prevMic != targetPeerId && prevMic != prevCam)
            DispatchTargetMicWatch(prevMic);
        DispatchTargetMediaWatch(targetPeerId);
    }

    internal void SendPhoneMicWatch(int targetPeerId, bool enable)
    {
        int prevMic;
        lock (_mediaWatchGate)
        {
            prevMic = _hubMicWatchPeerId;
            if (enable)
                _hubMicWatchPeerId = targetPeerId;
            else if (_hubMicWatchPeerId == targetPeerId)
                _hubMicWatchPeerId = -1;
        }
        if (prevMic > 0 && prevMic != targetPeerId)
            DispatchTargetMicWatch(prevMic);
        DispatchTargetMicWatch(targetPeerId);
    }

    internal void StopLocalMediaSharing()
    {
        _localMediaCapture?.StopAll();
        foreach (var s in _sessions.Values)
        {
            bool notifyCam = Interlocked.CompareExchange(ref s.WatchingCameraTargetId, -1, 0) == 0;
            bool notifyMic = Interlocked.CompareExchange(ref s.WatchingMicTargetId, -1, 0) == 0;
            if (notifyCam)
                _ = Task.Run(async () => { try { await SendToAsync(s, new Packet("phone-camera-status", 0, 0, Text: "Camera sharing stopped on host"), _cancel.Token); } catch { } });
            if (notifyMic)
                _ = Task.Run(async () => { try { await SendToAsync(s, new Packet("phone-mic-status", 0, 0, Text: "Mic sharing stopped on host"), _cancel.Token); } catch { } });
        }
        Volatile.Write(ref _localMediaCamStatus, 0);
        Volatile.Write(ref _localMediaMicStatus, 0);
        LocalMediaShareStateChanged?.Invoke(false, false, "");
    }

    private void DispatchTargetMediaWatch(int targetPeerId)
    {
        if (targetPeerId <= 0 || !_sessions.TryGetValue(targetPeerId, out var target)) return;
        bool anyCam;
        bool anyMic;
        int lens;
        bool torch;
        lock (_mediaWatchGate)
        {
            anyCam = _hubCameraWatchPeerId == targetPeerId;
            anyMic = _hubMicWatchPeerId == targetPeerId;
            lens = anyCam ? _hubCameraWatchLens : 0;
            torch = anyCam && _hubCameraWatchTorch;
            foreach (var s in _sessions.Values)
            {
                if (Volatile.Read(ref s.WatchingCameraTargetId) == targetPeerId)
                {
                    if (!anyCam)
                    {
                        lens = s.WatchingCameraLens;
                        torch = s.WatchingCameraTorch;
                    }
                    anyCam = true;
                }
                if (Volatile.Read(ref s.WatchingMicTargetId) == targetPeerId)
                    anyMic = true;
            }
        }
        if ((anyCam || anyMic) && _udpHub is { } hub)
        {
            if (target.UdpPeer is { Confirmed: false } u) hub.Send(u, FastUdpOp.Probe, hub.Port, 0, 0);
            if (target.MobileUdpPeer is { Confirmed: false } m) hub.Send(m, FastUdpOp.Probe, hub.Port, 0, 0);
        }
        _ = Task.Run(async () =>
        {
            try { await SendToAsync(target, new Packet("phone-camera-watch", anyCam ? 1 : 0, lens, torch ? 1 : 0, anyMic ? 1 : 0), _cancel.Token); }
            catch { }
        });
    }

    private void DispatchTargetMicWatch(int targetPeerId)
    {
        if (targetPeerId <= 0 || !_sessions.TryGetValue(targetPeerId, out var target)) return;
        bool anyMic;
        lock (_mediaWatchGate)
        {
            anyMic = _hubMicWatchPeerId == targetPeerId ||
                     _sessions.Values.Any(s => Volatile.Read(ref s.WatchingMicTargetId) == targetPeerId);
        }
        if (anyMic && _udpHub is { } hub)
        {
            if (target.UdpPeer is { Confirmed: false } u) hub.Send(u, FastUdpOp.Probe, hub.Port, 0, 0);
            if (target.MobileUdpPeer is { Confirmed: false } m) hub.Send(m, FastUdpOp.Probe, hub.Port, 0, 0);
        }
        _ = Task.Run(async () =>
        {
            try { await SendToAsync(target, new Packet("phone-mic-watch", anyMic ? 1 : 0, 0), _cancel.Token); }
            catch { }
        });
    }

    private void SyncHubLocalMediaCapture(int requestedLens = 0)
    {
        bool needCam = false;
        bool needMic = false;
        int camLens = requestedLens;
        foreach (var s in _sessions.Values)
        {
            if (Volatile.Read(ref s.WatchingCameraTargetId) == 0)
            {
                if (!needCam) camLens = s.WatchingCameraLens;
                needCam = true;
            }
            if (Volatile.Read(ref s.WatchingMicTargetId) == 0)
                needMic = true;
        }

        WindowsMediaCaptureService? svc;
        lock (_mediaWatchGate)
        {
            svc = _localMediaCapture;
            if (svc == null && (needCam || needMic) && !_disposed)
            {
                svc = new WindowsMediaCaptureService(() =>
                    _sessions.Values.Any(s =>
                        (Volatile.Read(ref s.WatchingCameraTargetId) == 0 ||
                         Volatile.Read(ref s.WatchingMicTargetId) == 0) &&
                        ActiveMediaUdpPeer(s) != null));
                svc.CameraFrameCaptured += OnLocalMediaCameraFrame;
                svc.CameraStatusChanged += OnLocalMediaCameraStatus;
                svc.MicAudioCaptured += OnLocalMediaMicAudio;
                svc.MicStatusChanged += OnLocalMediaMicStatus;
                _localMediaCapture = svc;
            }
        }
        if (svc == null)
        {
            if (!needCam) Volatile.Write(ref _localMediaCamStatus, 0);
            if (!needMic) Volatile.Write(ref _localMediaMicStatus, 0);
            EmitLocalMediaSummary();
            return;
        }

        if (needCam) svc.StartCamera(camLens);
        else
        {
            svc.StopCamera();
            Volatile.Write(ref _localMediaCamStatus, 0);
        }

        if (needMic) svc.StartMic();
        else
        {
            svc.StopMic();
            Volatile.Write(ref _localMediaMicStatus, 0);
        }

        EmitLocalMediaSummary();
    }

    private void EmitLocalMediaSummary()
    {
        bool cam = Volatile.Read(ref _localMediaCamStatus) != 0;
        bool mic = Volatile.Read(ref _localMediaMicStatus) != 0;
        if (!cam && !mic)
        {
            LocalMediaShareStateChanged?.Invoke(false, false, "");
            return;
        }
        var names = _sessions.Values
            .Where(s => Volatile.Read(ref s.WatchingCameraTargetId) == 0 || Volatile.Read(ref s.WatchingMicTargetId) == 0)
            .Select(s => s.Peer.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string what = (cam, mic) switch
        {
            (true, true) => "camera & mic",
            (true, false) => "camera",
            _ => "microphone"
        };
        string who = names.Length > 0 ? string.Join(", ", names) : "peer";
        LocalMediaShareStateChanged?.Invoke(cam, mic, $"Sharing this PC's {what} with {who}");
    }

    private void OnLocalMediaCameraFrame(byte[] jpeg)
    {
        if (_disposed || _cancel.IsCancellationRequested || jpeg.Length == 0) return;
        int frameId = Interlocked.Increment(ref _localMediaCamFrameSeq);
        List<Session>? tcpWatchers = null;
        var hub = _udpHub;
        foreach (var w in _sessions.Values)
        {
            if (Volatile.Read(ref w.WatchingCameraTargetId) != 0) continue;
            if (hub != null && ActiveMediaUdpPeer(w) is { } udpPeer)
                SendUdpMediaChunks(hub, udpPeer, FastUdpMediaKind.CameraFrameChunk, frameId, jpeg, 0);
            else
                (tcpWatchers ??= new List<Session>()).Add(w);
        }
        if (tcpWatchers != null)
            _ = SendTcpCameraChunksAsync(tcpWatchers, frameId, sourcePeerId: 0, jpeg);
    }

    private void OnLocalMediaCameraStatus(int stateCode, string msg)
    {
        bool anyCamWatcher = _sessions.Values.Any(s => Volatile.Read(ref s.WatchingCameraTargetId) == 0);
        int effectiveState = anyCamWatcher ? stateCode : 0;
        if (!anyCamWatcher && stateCode != 0)
            _localMediaCapture?.StopCamera();
        Volatile.Write(ref _localMediaCamStatus, effectiveState);
        EmitLocalMediaSummary();
        foreach (var w in _sessions.Values)
        {
            if (Volatile.Read(ref w.WatchingCameraTargetId) != 0) continue;
            _ = Task.Run(async () => { try { await SendToAsync(w, new Packet("phone-camera-status", effectiveState, 0, Text: msg), _cancel.Token); } catch { } });
        }
    }

    private void OnLocalMediaMicAudio(byte[] pcm, int sampleRate)
    {
        if (_disposed || _cancel.IsCancellationRequested || pcm.Length == 0) return;
        int seq = Interlocked.Increment(ref _localMediaMicAudioSeq);
        var hub = _udpHub;
        string? b64 = null;
        foreach (var w in _sessions.Values)
        {
            if (Volatile.Read(ref w.WatchingMicTargetId) != 0) continue;
            if (!SendDualOrBestMicUdp(hub, w, seq, sampleRate, 0, pcm))
            {
                b64 ??= Convert.ToBase64String(pcm);
                var pkt = new Packet("phone-mic-audio", seq, sampleRate, 1, 0, b64);
                _ = Task.Run(async () => { try { await SendToAsync(w, pkt, _cancel.Token); } catch { } });
            }
        }
    }

    private void OnLocalMediaMicStatus(int stateCode, string msg)
    {
        bool anyMicWatcher = _sessions.Values.Any(s => Volatile.Read(ref s.WatchingMicTargetId) == 0);
        int effectiveState = anyMicWatcher ? stateCode : 0;
        if (!anyMicWatcher && stateCode != 0)
            _localMediaCapture?.StopMic();
        Volatile.Write(ref _localMediaMicStatus, effectiveState);
        EmitLocalMediaSummary();
        foreach (var w in _sessions.Values)
        {
            if (Volatile.Read(ref w.WatchingMicTargetId) != 0) continue;
            _ = Task.Run(async () => { try { await SendToAsync(w, new Packet("phone-mic-status", effectiveState, 0, Text: msg), _cancel.Token); } catch { } });
        }
    }

    private async Task SendTcpCameraChunksAsync(List<Session> watchers, int frameId, int sourcePeerId, byte[] jpeg)
    {
        const int chunkSize = 3600;
        int total = (jpeg.Length + chunkSize - 1) / chunkSize;
        if (total <= 0) return;
        for (int i = 0; i < total; i++)
        {
            if (_disposed || _cancel.IsCancellationRequested) break;
            int offset = i * chunkSize;
            int len = Math.Min(chunkSize, jpeg.Length - offset);
            string b64 = Convert.ToBase64String(jpeg, offset, len);
            var pkt = new Packet("phone-camera-chunk", frameId, i, total, sourcePeerId, b64);
            foreach (var w in watchers)
            {
                if (Volatile.Read(ref w.WatchingCameraTargetId) != sourcePeerId) continue;
                try { await SendToAsync(w, pkt, _cancel.Token); } catch { }
            }
        }
    }

    internal NotificationPreviewItem[] GetNotificationsForPeer(int peerId)
    {
        lock (_notifGate)
            return _peerNotifications.TryGetValue(peerId, out var items) ? items : Array.Empty<NotificationPreviewItem>();
    }

    internal IReadOnlyList<string> GetRememberedMacs(string? fingerprint) =>
        !string.IsNullOrWhiteSpace(fingerprint) && _macsByFingerprint.TryGetValue(fingerprint, out var macs)
            ? macs : Array.Empty<string>();

    internal void BroadcastClipboardTextAsync(string hash, string text, int excludeSessionId = -1)
    {
        string json = SharedClipboardHelper.SerializeClipboardTextPacket(hash, text);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 7800) return;
        foreach (var s in _sessions.Values)
        {
            if (s.Id == excludeSessionId) continue;
            _ = Task.Run(async () =>
            {
                try { await SendToAsync(s, new Packet("clipboard-text", Text: json), _cancel.Token); }
                catch { }
            });
        }
    }

    internal void BroadcastClipboardImageAsync(string hash, byte[] png, int excludeSessionId = -1)
    {
        var chunks = FileDropHelper.BuildImageClipboardChunks(hash, png);
        foreach (var s in _sessions.Values)
        {
            if (s.Id == excludeSessionId) continue;
            _ = Task.Run(async () =>
            {
                try
                {
                    foreach (var pkt in chunks)
                        await SendToAsync(s, pkt, _cancel.Token);
                }
                catch { }
            });
        }
    }

    internal async Task<bool> SendFileToTargetAsync(int targetId, string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists || info.Length <= 0 || info.Length > FileDropHelper.MaxFileSizeBytes)
                return false;
            if (!_sessions.TryGetValue(targetId, out var target))
                return false;
            int txId = FileDropHelper.NextTransferId();
            string safeName = FileDropHelper.SanitizeFileName(info.Name);
            await SendToAsync(target, new Packet("file-offer", txId, (int)info.Length, 0, 0, safeName), _cancel.Token);
            byte[] buf = new byte[FileDropHelper.ChunkRawBytes];
            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192, useAsync: true);
            int seq = 0;
            int read;
            while ((read = await fs.ReadAsync(buf, _cancel.Token)) > 0)
            {
                string b64 = Convert.ToBase64String(buf, 0, read);
                await SendToAsync(target, new Packet("file-chunk", txId, seq++, 0, 0, b64), _cancel.Token);
            }
            await SendToAsync(target, new Packet("file-done", txId, seq), _cancel.Token);
            _status($"Sent '{safeName}' to {target.Peer.Name}.");
            return true;
        }
        catch (Exception ex)
        {
            _status($"File transfer failed: {ex.Message}");
            return false;
        }
    }

    internal void SendMediaAction(int targetId, string action, int value = 0)
    {
        if (!SystemMediaPowerHelper.IsValidMediaAction(action)) return;
        if (targetId <= 0)
        {
            SystemMediaPowerHelper.ExecuteMediaAction(action, value);
            return;
        }
        if (_sessions.TryGetValue(targetId, out var target))
        {
            _ = Task.Run(async () =>
            {
                try { await SendToAsync(target, new Packet("media-action", 0, value, 0, 0, action), _cancel.Token); }
                catch { }
            });
        }
    }

    internal void SendPowerAction(int targetId, string action)
    {
        if (!SystemMediaPowerHelper.IsValidPowerAction(action)) return;
        if (string.Equals(action, "lock-all", StringComparison.Ordinal))
        {
            foreach (var s in _sessions.Values)
            {
                if (s.Peer.Platform == "Windows")
                {
                    _ = Task.Run(async () =>
                    {
                        try { await SendToAsync(s, new Packet("power-action", 0, 0, 0, 0, "lock"), _cancel.Token); }
                        catch { }
                    });
                }
            }
            _ = Task.Run(() =>
            {
                Thread.Sleep(220);
                SystemMediaPowerHelper.ExecutePowerAction("lock");
            });
            return;
        }
        if (targetId <= 0)
        {
            SystemMediaPowerHelper.ExecutePowerAction(action);
            return;
        }
        if (_sessions.TryGetValue(targetId, out var target))
        {
            _ = Task.Run(async () =>
            {
                try { await SendToAsync(target, new Packet("power-action", 0, 0, 0, 0, action), _cancel.Token); }
                catch { }
            });
        }
    }

    internal void SendNotificationDismiss(int targetId, string key)
    {
        if (_sessions.TryGetValue(targetId, out var s))
        {
            _ = Task.Run(async () =>
            {
                try { await SendToAsync(s, new Packet("notif-dismiss", targetId, Text: key), _cancel.Token); }
                catch { }
            });
        }
    }

    internal void SendNotificationReply(int targetId, string key, string reply)
    {
        if (_sessions.TryGetValue(targetId, out var s))
        {
            string payload = JsonSerializer.Serialize(new { k = key, r = reply });
            _ = Task.Run(async () =>
            {
                try { await SendToAsync(s, new Packet("notif-reply", targetId, Text: payload), _cancel.Token); }
                catch { }
            });
        }
    }

    internal void BroadcastProfileState(string summaryJson)
    {
        _latestProfileStateJson = summaryJson;
        if (System.Text.Encoding.UTF8.GetByteCount(summaryJson) > 7800) return;
        foreach (var s in _sessions.Values)
        {
            _ = Task.Run(async () =>
            {
                try { await SendToAsync(s, new Packet("profile-state", Text: summaryJson), _cancel.Token); }
                catch { }
            });
        }
    }

    internal void PublishLocalNotifications(NotificationPreviewItem[] items)
    {
        string json = DeviceTelemetryAndNotifModel.SerializeNotificationList(items);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 7800) return;
        foreach (var s in _sessions.Values)
        {
            _ = Task.Run(async () =>
            {
                try { await SendToAsync(s, new Packet("notif-preview-state", 0, Text: json), _cancel.Token); }
                catch { }
            });
        }
    }

    internal void SetCustomPairingCode(string? code) =>
        _customPairingCode = PairingCodeHelper.NormalizeCustomCode(code);

    internal bool TryVerifyCustomPairingCode(string? got)
    {
        string expected = _customPairingCode;
        string actual = PairingCodeHelper.NormalizeCustomCode(got);
        if (expected.Length == 0 || actual.Length == 0)
            return false;
        long now = Environment.TickCount64;
        lock (_customPairAttemptLock)
        {
            if (now - _customPairWindowStartTicks > 30000)
            {
                _customPairWindowStartTicks = now;
                _customPairFailedAttempts = 0;
            }
            if (_customPairFailedAttempts >= 6)
                return false;
            bool match = CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(actual),
                System.Text.Encoding.UTF8.GetBytes(expected));
            if (!match)
            {
                _customPairFailedAttempts++;
                return false;
            }
            _customPairFailedAttempts = 0;
            return true;
        }
    }

    internal void SetEnvironmentSpatialLayout(
        IReadOnlyDictionary<string, NormalizedSpatialBox> spatialByIdentity)
    {
        bool changed = false;
        lock (_spatialGate)
        {
            if (spatialByIdentity.Count != _environmentSpatialLayout.Count)
            {
                changed = true;
            }
            else
            {
                foreach (var kv in spatialByIdentity)
                {
                    if (!_environmentSpatialLayout.TryGetValue(kv.Key, out var existing) ||
                        existing != kv.Value)
                    {
                        changed = true;
                        break;
                    }
                }
            }
            if (changed)
                _environmentSpatialLayout = new Dictionary<string, NormalizedSpatialBox>(
                    spatialByIdentity, StringComparer.OrdinalIgnoreCase);
        }
        if (changed && _sessions.Count > 0)
        {
            _ = Task.Run(async () =>
            {
                try { await PublishEnvironmentAsync(_cancel.Token); }
                catch (Exception) { }
            });
        }
    }

    private List<EnvironmentMember> ApplyAndOrderSpatialMembers(List<EnvironmentMember> members)
    {
        Dictionary<string, NormalizedSpatialBox> snapshot;
        lock (_spatialGate)
            snapshot = _environmentSpatialLayout;
        if (snapshot.Count == 0)
            return members;

        var decorated = new List<(EnvironmentMember Member, int Index, int Order)>(members.Count);
        for (int i = 0; i < members.Count; i++)
        {
            var m = members[i];
            NormalizedSpatialBox box = default;
            bool matched = false;
            if (!string.IsNullOrWhiteSpace(m.StableIdentity) &&
                snapshot.TryGetValue(m.StableIdentity, out box))
            {
                matched = true;
            }
            else if (m.Id == 0 && snapshot.TryGetValue("LOCAL", out box))
            {
                matched = true;
            }
            else if (snapshot.TryGetValue(m.Platform + "|" + m.Name, out box))
            {
                matched = true;
            }

            if (matched)
            {
                m = m with
                {
                    ArrangementOrder = box.Order,
                    LayoutX = box.X,
                    LayoutY = box.Y,
                    LayoutW = box.W,
                    LayoutH = box.H
                };
                decorated.Add((m, i, box.Order));
            }
            else
            {
                decorated.Add((m, i, int.MaxValue - 1000 + i));
            }
        }

        return decorated
            .OrderBy(x => x.Order)
            .ThenBy(x => x.Index)
            .Select(x => x.Member)
            .ToList();
    }

    internal event Action<string, int>? HubYieldRequested;
    internal IReadOnlyList<(int VirtualId, ConnectedPeer Peer, int BridgeSessionId)> BridgedPeers
    {
        get
        {
            lock (_bridgedLock)
                return _bridgedByVirtualId.Values
                    .OrderBy(b => b.VirtualId)
                    .Select(b => (b.VirtualId, BuildBridgedPeer(b), b.BridgeSession.Id))
                    .ToArray();
        }
    }

    private static ConnectedPeer BuildBridgedPeer(BridgedSubPeer b)
    {
        var mon = b.Topology.Monitors.FirstOrDefault();
        return new ConnectedPeer(
            b.Descriptor.Name,
            b.Topology,
            b.Descriptor.SupportsReverseInput,
            b.Descriptor.StableIdentity,
            b.Descriptor.Platform,
            true,
            mon?.OsVersion,
            b.Descriptor.AppVersion ?? mon?.AppVersion,
            mon?.DensityDpi,
            mon?.RefreshHz,
            mon?.RotationDegrees,
            b.Descriptor.NotificationCount);
    }

    internal IReadOnlyList<SubPeerDescriptor> GetSubPeersSnapshotForUpstream()
    {
        var result = new List<SubPeerDescriptor>();
        foreach (var s in _sessions.Values.OrderBy(p => p.Id))
        {
            if (s.Peer.Platform != "Android") continue;
            result.Add(new SubPeerDescriptor(
                s.Id,
                LayoutIdentity(s.Peer),
                s.Peer.Name,
                s.Peer.Platform,
                s.Peer.Monitors.ToWire(),
                s.Peer.SupportsReverseInput,
                s.Peer.AppVersion,
                s.Peer.NotificationCount,
                s.Peer.BatteryPercent,
                s.Peer.IsCharging,
                s.Peer.HasCamera,
                s.Peer.HasMicrophone));
        }
        return result;
    }

    internal void ReleaseAllBridgedClaims()
    {
        var toReset = new List<Session>();
        foreach (var s in _sessions.Values)
        {
            int lease = Interlocked.Exchange(ref s.BridgedClaimLease, 0);
            if (lease > 0)
            {
                s.BridgedReplyAsync = null;
                Volatile.Write(ref s.ActiveReverseLease, 0);
                s.MovePacer?.SetWarmActive(false);
                s.MovePacer?.ClearPending();
                toReset.Add(s);
            }
        }
        foreach (var s in toReset)
        {
            _ = Task.Run(async () =>
            {
                try { await SendToAsync(s, new Packet("reverse-reset"), _cancel.Token); }
                catch (Exception) { }
            });
        }
    }

    internal int ActiveEnvironmentDeviceCount
    {
        get
        {
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in _sessions.Values)
            {
                string key = !string.IsNullOrWhiteSpace(s.Peer.DeviceFingerprint)
                    ? s.Peer.DeviceFingerprint
                    : s.Peer.Platform + "|" + s.Peer.Name;
                unique.Add(key);
            }
            lock (_bridgedLock)
            {
                foreach (var b in _bridgedByVirtualId.Values)
                {
                    if (!string.IsNullOrWhiteSpace(b.Descriptor.StableIdentity))
                        unique.Add(b.Descriptor.StableIdentity);
                }
            }
            return 1 + unique.Count;
        }
    }

    internal void SetUpstreamRosterOverlay(EnvironmentMember[]? upstreamMembers, int? ownUpstreamId)
    {
        lock (_bridgedLock)
        {
            _upstreamRosterOverlay = upstreamMembers is { Length: > 0 }
                ? (upstreamMembers.ToArray(), ownUpstreamId ?? 0)
                : null;
        }
        _ = Task.Run(async () =>
        {
            try { await PublishEnvironmentAsync(_cancel.Token); }
            catch (Exception) { }
        });
    }
    private Task? _serverTask;
    private int _nextClientId;
    private int _pendingAndActive;
    private long _lastWarmBroadcastTicks;
    private Session? _selected;
    private Session? _owner;
    private bool _disposed;

    internal Func<bool>? AllowForwardClaim;
    internal Action? ReleaseIdleOutboundForPhoneClaim;
    internal event Action<Packet>? ReverseControlMessage;
    internal event Action<string>? DiagnosticLog;
    internal event Action<ConnectedPeer>? PeerConnected;
    internal event Action? PeerListChanged;
    internal event Action? PeerDisconnected;
    internal event Action<ConnectedPeer>? PeerTopologyChanged;
    internal event Action<MonitorEdge>? PeerPlacementChanged;
    internal event Action? PeerKeyboardInfoChanged;
    internal string Fingerprint { get; }
    internal string Secret { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    internal int ConnectedPeerCount => _sessions.Count;
    internal static bool AllowsForwardInput(ConnectedPeer peer) =>
        peer.Platform != "Android" || peer.SupportsForwardInput;
    internal IReadOnlyList<(int Id, ConnectedPeer Peer)> ConnectedPeers =>
        _sessions.Values.OrderBy(s => s.Id).Select(s => (s.Id, s.Peer)).ToArray();
    internal int SelectedPeerId => Volatile.Read(ref _selected)?.Id ?? Volatile.Read(ref _selectedVirtualSubId);

    internal static string LayoutIdentity(ConnectedPeer peer) =>
        string.IsNullOrWhiteSpace(peer.DeviceFingerprint) ?
            peer.Platform+"|"+peer.Name : peer.DeviceFingerprint;
    internal bool OwnsAnyPlacement(string key) =>
        _sessions.Values.Any(x => OwnsPlacement(x.Peer, key)) ||
        BridgedPeers.Any(b => OwnsPlacement(b.Peer, key));
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
        if(!OwnsAnyPlacement(key))return false;
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
    internal static bool CanPauseAndroidForPayments(ConnectedPeer? peer) =>
        peer != null && peer.Platform == "Android" && peer.SupportsReverseInput;

    internal sealed record ActiveLinkSnapshot(
        bool Connected,
        string PeerName,
        string TransportBand,
        int LinkSpeedMbps,
        double RttMs,
        double JitterMs,
        double LossPermille,
        string? KeyboardInfoJson,
        string? SplitPathsSummary = null);

    private static string? FormatSplitPathsSummary(Session session)
    {
        if (session.IsCableSession || session.MobileUdpPeer is not { Confirmed: true } mob)
            return null;
        string mouseName = ReferenceEquals(ActiveMouseUdpPeer(session), mob) ? "Mobile" : "Wireless";
        string kbName = ReferenceEquals(ActiveKeyboardUdpPeer(session), mob) ? "Mobile" : "Wireless";
        string dualTag = Volatile.Read(ref session.DualPathControls) != 0 ? " + dual keys" : "";
        return $"Mouse: {mouseName}  •  Keys: {kbName}{dualTag}";
    }

    internal ActiveLinkSnapshot GetActiveLinkSummary()
    {
        var selected = Volatile.Read(ref _selected);
        Session? activeLeaseSession = (selected != null && Volatile.Read(ref selected.ActiveReverseLease) > 0)
            ? selected
            : _sessions.Values.FirstOrDefault(s => Volatile.Read(ref s.ActiveReverseLease) > 0);
        var session = activeLeaseSession ??
            _sessions.Values
                .OrderByDescending(s => string.Equals(s.Peer.Platform, "Windows", StringComparison.Ordinal))
                .ThenByDescending(s => s.IsCableSession)
                .ThenBy(s => s.Id)
                .FirstOrDefault();
        if (session == null)
            return new ActiveLinkSnapshot(false, "", "offline", 0, 0, 0, 0, null);
        if (session.IsCableSession)
        {
            return new ActiveLinkSnapshot(
                true,
                session.Peer.Name,
                "cable",
                Volatile.Read(ref session.LinkSpeedMbps),
                0,
                0,
                0,
                Volatile.Read(ref session.KeyboardInfoJson));
        }
        var peer = session.UdpPeer;
        string band = session.LinkBand;
        if (band != "ethernet" && band != "cable")
        {
            string? trackerBand = peer?.LinkTracker.Band;
            if (!string.IsNullOrWhiteSpace(trackerBand) && trackerBand != "wireless")
                band = trackerBand;
            else if (string.IsNullOrWhiteSpace(band))
                band = "wireless";
        }
        int speed = Volatile.Read(ref session.LinkSpeedMbps);
        if (speed <= 0 && peer != null && peer.LinkTracker.LinkSpeedMbps > 0)
            speed = peer.LinkTracker.LinkSpeedMbps;

        return new ActiveLinkSnapshot(
            true,
            session.Peer.Name,
            band,
            speed,
            peer?.LinkTracker.SmoothedRttMs ?? 0,
            peer?.LinkTracker.EffectiveJitterMs ?? 0,
            peer?.LinkTracker.CombinedLossPermille ?? 0,
            Volatile.Read(ref session.KeyboardInfoJson),
            FormatSplitPathsSummary(session));
    }

    internal void SetShellBridgeAuth(int port, string secret, bool forceBroadcast = false)
    {
        if (port <= 0 || port > 65535 || string.IsNullOrWhiteSpace(secret)) return;
        lock (_shellAuthGate)
        {
            if (!forceBroadcast &&
                _shellBridgeAuth is { } cur && cur.Port == port &&
                string.Equals(cur.Secret, secret, StringComparison.Ordinal))
                return;
            _shellBridgeAuth = (port, secret);
        }
        _ = Task.Run(async () =>
        {
            foreach (var session in _sessions.Values)
            {
                if (session.Peer.Platform != "Android") continue;
                try
                {
                    await SendToAsync(session,
                        new Packet("shell-input-bridge", port, Text: secret),
                        _cancel.Token);
                }
                catch (Exception) { /* peer may disconnect concurrently */ }
            }
        });
    }

    internal void SetBridgeStatus(string state, string detail, int pairPort, int connectPort)
    {
        string cleanState = string.IsNullOrWhiteSpace(state) ? "debug_off" : state.Trim();
        string cleanDetail = detail ?? "";
        lock (_shellAuthGate)
        {
            if (_bridgeStatus.State == cleanState &&
                _bridgeStatus.Detail == cleanDetail &&
                _bridgeStatus.PairPort == pairPort &&
                _bridgeStatus.ConnectPort == connectPort)
            {
                return;
            }
            _bridgeStatus = (cleanState, cleanDetail, pairPort, connectPort);
        }
        string wireText = $"{cleanState}|{cleanDetail}";
        _ = Task.Run(async () =>
        {
            foreach (var session in _sessions.Values)
            {
                if (session.Peer.Platform != "Android") continue;
                try
                {
                    await SendToAsync(session,
                        new Packet("bridge-status", pairPort, connectPort, Text: wireText),
                        _cancel.Token);
                }
                catch (Exception) { }
            }
        });
    }

    internal void NotifyCableBridgeReady()
    {
        _ = Task.Run(async () =>
        {
            foreach (var session in _sessions.Values)
            {
                if (session.Peer.Platform != "Android" || session.IsCableSession) continue;
                try
                {
                    await SendToAsync(session, new Packet("cable-bridge-ready"), _cancel.Token);
                }
                catch (Exception) { /* peer may disconnect concurrently */ }
            }
        });
    }

    internal void NotifyCableDetached()
    {
        foreach (var session in _sessions.Values)
        {
            if (!session.IsCableSession) continue;
            Interlocked.Exchange(ref session.CableDetachedOrUnresponsive, 1);
            TryPromoteWirelessBackup(session);
        }
    }

    internal void BroadcastKeyboardLayoutAsync()
    {
        string layout = KeyboardLayoutHelper.DescribeCurrentWindowsLayout();
        _ = Task.Run(async () =>
        {
            foreach (var session in _sessions.Values)
            {
                if (session.Peer.Platform != "Android") continue;
                try
                {
                    await SendToAsync(session, new Packet("keyboard-layout", Text: layout), _cancel.Token);
                }
                catch (Exception) { }
            }
        });
    }

    private static bool IsSameDevicePeer(ConnectedPeer a, ConnectedPeer b) =>
        (!string.IsNullOrWhiteSpace(a.DeviceFingerprint) &&
         string.Equals(a.DeviceFingerprint, b.DeviceFingerprint, StringComparison.OrdinalIgnoreCase)) ||
        (string.Equals(a.Platform, b.Platform, StringComparison.Ordinal) &&
         string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

    private bool TryPromoteWirelessBackup(Session cableSession)
    {
        Session? backup = _sessions.Values.FirstOrDefault(other =>
            !other.IsCableSession &&
            other.Id != cableSession.Id &&
            IsSameDevicePeer(other.Peer, cableSession.Peer));
        if (backup == null) return false;
        return Select(backup);
    }

    private bool EvictStaleDuplicateTrustedWindowsSessions(Session incoming, bool isLoopbackRemote)
    {
        if (isLoopbackRemote ||
            string.IsNullOrWhiteSpace(incoming.TrustedTokenHash) ||
            incoming.Peer.Platform != "Windows" ||
            string.IsNullOrWhiteSpace(incoming.Peer.DeviceFingerprint))
            return false;

        bool replacedSelected = false;
        foreach (var existing in _sessions.Values)
        {
            if (existing.Id == incoming.Id ||
                existing.Peer.Platform != "Windows" ||
                !string.Equals(existing.TrustedTokenHash, incoming.TrustedTokenHash, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(existing.Peer.DeviceFingerprint, incoming.Peer.DeviceFingerprint, StringComparison.OrdinalIgnoreCase))
                continue;

            lock (_authorityGate)
            {
                if (ReferenceEquals(_selected, existing))
                    replacedSelected = true;
            }
            _sessions.TryRemove(existing.Id, out _);
            try { existing.Client.Close(); }
            catch (Exception) { }
        }
        return replacedSelected;
    }

    internal bool HasReadyAndroidPeer =>
        _sessions.Values.Any(s => CanPauseAndroidForPayments(s.Peer));

    private int _localNotificationCount = -1;
    internal int LocalNotificationCount => Volatile.Read(ref _localNotificationCount);

    internal void SetLocalNotificationCount(int count)
    {
        int normalized = count >= 0 ? count : -1;
        if (Interlocked.Exchange(ref _localNotificationCount, normalized) == normalized)
            return;
        _ = Task.Run(async () =>
        {
            try { await PublishEnvironmentAsync(_cancel.Token); }
            catch (Exception) { }
        });
        PeerListChanged?.Invoke();
    }

    internal int PauseConnectedAndroidForPayments(int? targetPeerId = null)
    {
        int sent = 0;
        foreach (var session in _sessions.Values)
        {
            if (targetPeerId.HasValue && session.Id != targetPeerId.Value) continue;
            if (!CanPauseAndroidForPayments(session.Peer)) continue;
            TrySendReverseTo(session.Id, new Packet("reset"));
            if (TrySendReverseTo(session.Id, new Packet("pause-accessibility")))
                sent++;
        }
        if (sent > 0)
            _status("Asked connected Android phone to turn off Accessibility for payments.");
        return sent;
    }

    internal static bool CanRoute(ConnectedPeer source, ConnectedPeer target,
        bool differentPeers, int claim) =>
        claim > 0 && differentPeers && AllowsForwardInput(source) &&
        target.SupportsReverseInput;
    internal bool SelectConnectedPeer(int id)
    {
        if (_sessions.TryGetValue(id, out var session))
        {
            // Selection is ONLY routing over the already authenticated TLS stream.
            // It cannot grant authority and it never initiates another connection.
            return Select(session);
        }
        BridgedSubPeer? bridged;
        lock (_bridgedLock)
            _bridgedByVirtualId.TryGetValue(id, out bridged);
        if (bridged == null) return false;
        return SelectVirtualPeer(bridged);
    }

    private void SetSessionSubPeers(Session bridgeSession, string? json)
    {
        SubPeerDescriptor[] parsed;
        if (string.IsNullOrWhiteSpace(json))
            parsed = [];
        else
        {
            if (json.Length > 7500) return;
            try { parsed = JsonSerializer.Deserialize<SubPeerDescriptor[]>(json) ?? []; }
            catch { return; }
        }
        if (parsed.Length > 4) return;
        lock (_bridgedLock)
        {
            var oldVirtuals = _bridgedByVirtualId.Values
                .Where(b => b.BridgeSession.Id == bridgeSession.Id)
                .Select(b => b.VirtualId)
                .ToList();
            foreach (int vid in oldVirtuals)
                _bridgedByVirtualId.Remove(vid);

            var validList = new List<SubPeerDescriptor>();
            foreach (var item in parsed)
            {
                if (item.LocalId <= 0 || string.IsNullOrWhiteSpace(item.Name) ||
                    item.Name.Length > 60 || item.Name.Any(char.IsControl) ||
                    string.IsNullOrWhiteSpace(item.StableIdentity) ||
                    item.Platform is not ("Windows" or "Android"))
                    continue;
                MonitorTopology topo;
                try { topo = MonitorTopology.FromWire(item.Layout); }
                catch { continue; }
                validList.Add(item);
                if (!_virtualIdByStableIdentity.TryGetValue(item.StableIdentity, out int vid))
                {
                    vid = Interlocked.Increment(ref _nextVirtualSubId);
                    _virtualIdByStableIdentity[item.StableIdentity] = vid;
                }
                _bridgedByVirtualId[vid] = new BridgedSubPeer
                {
                    VirtualId = vid,
                    BridgeSession = bridgeSession,
                    Descriptor = item,
                    Topology = topo
                };
            }
            _subPeersBySession[bridgeSession.Id] = validList;
            int selectedVirt = Volatile.Read(ref _selectedVirtualSubId);
            if (selectedVirt > 0 && !_bridgedByVirtualId.ContainsKey(selectedVirt))
                Volatile.Write(ref _selectedVirtualSubId, 0);
        }
        lock (_authorityGate)
        {
            foreach (int key in _virtualHubClaims.Where(kv => kv.Value.BridgeSession.Id == bridgeSession.Id).Select(kv => kv.Key).ToArray())
            {
                bool stillPresent;
                lock (_bridgedLock) stillPresent = _bridgedByVirtualId.ContainsKey(key);
                if (!stillPresent) _virtualHubClaims.Remove(key);
            }
        }
        PeerListChanged?.Invoke();
    }

    private void ClearSessionSubPeers(Session bridgeSession)
    {
        List<int> removedVirtuals = [];
        lock (_bridgedLock)
        {
            _subPeersBySession.Remove(bridgeSession.Id);
            foreach (var b in _bridgedByVirtualId.Values.Where(v => v.BridgeSession.Id == bridgeSession.Id).ToArray())
            {
                _bridgedByVirtualId.Remove(b.VirtualId);
                removedVirtuals.Add(b.VirtualId);
            }
            int selectedVirt = Volatile.Read(ref _selectedVirtualSubId);
            if (removedVirtuals.Contains(selectedVirt))
                Volatile.Write(ref _selectedVirtualSubId, 0);
        }
        List<(Session Source, int Claim)> orphanedRelays = [];
        lock (_authorityGate)
        {
            foreach (int vid in removedVirtuals)
                _virtualHubClaims.Remove(vid);
            foreach (var kv in _virtualRelayBySource.Where(kv => kv.Value.BridgeSession.Id == bridgeSession.Id).ToArray())
            {
                _virtualRelayBySource.Remove(kv.Key);
                orphanedRelays.Add((kv.Value.Source, kv.Value.Claim));
            }
        }
        foreach (var (source, claim) in orphanedRelays)
        {
            if (source.EnvironmentTargetId >= 10000)
                source.EnvironmentTargetId = 0;
            _ = Task.Run(async () =>
            {
                try { await SendToAsync(source, new Packet("local-active", claim), _cancel.Token); }
                catch (Exception) { }
            });
        }
    }

    internal async Task DispatchBridgedCommandAsync(
        int localSubId, Packet inner, Func<Packet, Task> replyAsync)
    {
        if (!_sessions.TryGetValue(localSubId, out var target))
        {
            if (inner.Type == "claim" && inner.A > 0)
            {
                try { await replyAsync(new Packet("reverse-busy", inner.A)); }
                catch (Exception) { }
            }
            return;
        }
        if (inner.Type == "claim")
        {
            bool granted = false;
            int dispatchSeq = 0;
            lock (_authorityGate)
            {
                if (inner.A > 0 && !_hubClaims.ContainsKey(target.Id) &&
                    !_relayByTarget.ContainsKey(target.Id) &&
                    Volatile.Read(ref target.BridgedClaimLease) == 0 &&
                    target.Peer.SupportsReverseInput)
                {
                    Volatile.Write(ref target.BridgedClaimLease, inner.A);
                    target.BridgedReplyAsync = replyAsync;
                    target.HubGrantedAcked = false;
                    target.HubActiveAcked = false;
                    Volatile.Write(ref target.PendingFirstCursorAck, 1);
                    Volatile.Write(ref target.ActiveReverseLease, inner.A);
                    target.MovePacer?.SetWarmActive(true);
                    dispatchSeq = target.NextReverseClaimDispatchSeq();
                    granted = true;
                }
            }
            if (!granted)
            {
                try { await replyAsync(new Packet("reverse-busy", inner.A)); }
                catch (Exception) { }
                return;
            }
            var claimPacket = inner with { C = dispatchSeq };
            if (!target.ReverseQueue.Writer.TryWrite(claimPacket))
            {
                Volatile.Write(ref target.BridgedClaimLease, 0);
                target.BridgedReplyAsync = null;
                Volatile.Write(ref target.ActiveReverseLease, 0);
                target.MovePacer?.SetWarmActive(false);
                try { await replyAsync(new Packet("reverse-busy", inner.A)); }
                catch (Exception) { }
            }
            return;
        }
        if (inner.Type is "release" or "reset")
        {
            int cur = Volatile.Read(ref target.BridgedClaimLease);
            if (inner.Type == "reset" || (inner.A > 0 && inner.A == cur))
            {
                Volatile.Write(ref target.BridgedClaimLease, 0);
                target.BridgedReplyAsync = null;
                Volatile.Write(ref target.ActiveReverseLease, 0);
                Volatile.Write(ref target.PendingFirstCursorAck, 0);
                target.MovePacer?.SetWarmActive(false);
                target.MovePacer?.ClearPending();
                target.ReverseQueue.Writer.TryWrite(inner);
            }
            return;
        }
        if (Volatile.Read(ref target.BridgedClaimLease) <= 0)
            return;
        if (inner.Type == "move")
        {
            if (target.MovePacer?.TryHandleMove(inner) == true)
            {
                if (!target.IsCableSession)
                    Interlocked.Exchange(ref target.LatestReverseMove, null);
                return;
            }
            QueueTcpReverseMove(target, inner, countAsFallback: !target.IsCableSession);
            return;
        }
        if (inner.Type is "button" or "wheel" or "key")
            SendFastControlAndAssignSeq(target, Volatile.Read(ref target.BridgedClaimLease), ref inner);
        if (inner.Type is ("button" or "wheel" or "key" or "text"))
            target.ReverseQueue.Writer.TryWrite(inner);
    }

    private async Task HandleSubReplyAsync(Session bridgeSession, string? json, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 4096) return;
        SubRouteEnvelope? env;
        try { env = JsonSerializer.Deserialize<SubRouteEnvelope>(json); }
        catch { return; }
        if (env == null || env.LocalId <= 0) return;
        BridgedSubPeer? bridged = null;
        lock (_bridgedLock)
        {
            bridged = _bridgedByVirtualId.Values.FirstOrDefault(b =>
                b.BridgeSession.Id == bridgeSession.Id && b.Descriptor.LocalId == env.LocalId);
        }
        if (bridged == null) return;
        var inner = env.Payload;
        Session? relaySource = null;
        bool notifyHub = false;
        lock (_authorityGate)
        {
            var relayKv = _virtualRelayBySource.FirstOrDefault(kv =>
                kv.Value.VirtualTargetId == bridged.VirtualId &&
                (kv.Value.Claim == inner.A || inner.Type == "reverse-local-active"));
            if (relayKv.Value.Source != null)
            {
                relaySource = relayKv.Value.Source;
                if (inner.Type is ("reverse-busy" or "reverse-local-active"))
                    _virtualRelayBySource.Remove(relayKv.Key);
            }
            else if (_virtualHubClaims.TryGetValue(bridged.VirtualId, out var hubClaim) &&
                (hubClaim.Lease == inner.A || inner.Type == "reverse-local-active"))
            {
                if (inner.Type is ("reverse-busy" or "reverse-local-active"))
                    _virtualHubClaims.Remove(bridged.VirtualId);
                notifyHub = true;
            }
        }
        if (relaySource != null && inner.Type.StartsWith("reverse-", StringComparison.Ordinal))
        {
            string response = inner.Type["reverse-".Length..];
            try { await SendToAsync(relaySource, new Packet(response, inner.A), token); }
            catch (Exception) { }
        }
        else if (notifyHub)
        {
            ReverseControlMessage?.Invoke(inner);
        }
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
                _udpHub.MediaDatagramReceived += OnUdpHubMediaDatagram;
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
        _ = Task.Run(() => TaskbarMonitorLoopAsync(_cancel.Token));
        _ = Task.Run(() => ScreenPreviewLoopAsync(_cancel.Token));
    }

    private async Task DeliverTaskbarToAndroidAsync(
        Session recipient,
        int targetId,
        string stateJson,
        string signature,
        IReadOnlyDictionary<string, string>? icons,
        CancellationToken token)
    {
        if (recipient.Peer.Platform != "Android" || string.IsNullOrEmpty(stateJson))
            return;
        List<KeyValuePair<string, string>> pendingIcons = new();
        lock (recipient.TaskbarGate)
        {
            if (recipient.LastSentTaskbarTarget == targetId &&
                string.Equals(recipient.LastSentTaskbarSig, signature, StringComparison.Ordinal))
            {
                return;
            }
            if (icons != null)
            {
                if (recipient.SentTaskbarIcons.Count > 128)
                    recipient.SentTaskbarIcons.Clear();
                foreach (var kv in icons)
                {
                    if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrEmpty(kv.Value) &&
                        recipient.SentTaskbarIcons.Add(kv.Key))
                    {
                        pendingIcons.Add(kv);
                    }
                }
            }
            recipient.LastSentTaskbarTarget = targetId;
            recipient.LastSentTaskbarSig = signature;
        }
        try
        {
            foreach (var kv in pendingIcons)
            {
                while (Volatile.Read(ref recipient.UrgentControlWaiters) > 0 && !token.IsCancellationRequested)
                    await Task.Yield();
                string payload = kv.Key + "|" + kv.Value;
                if (System.Text.Encoding.UTF8.GetByteCount(payload) <= 7800)
                    await SendToAsync(recipient, new Packet("taskbar-icon", targetId, Text: payload), token);
            }
            while (Volatile.Read(ref recipient.UrgentControlWaiters) > 0 && !token.IsCancellationRequested)
                await Task.Yield();
            if (System.Text.Encoding.UTF8.GetByteCount(stateJson) <= 7800)
                await SendToAsync(recipient, new Packet("taskbar-state", targetId, Text: stateJson), token);
        }
        catch (Exception)
        {
            lock (recipient.TaskbarGate)
            {
                if (recipient.LastSentTaskbarTarget == targetId)
                    recipient.LastSentTaskbarSig = "";
            }
        }
    }

    private async Task TaskbarMonitorLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var androidSessions = _sessions.Values
                    .Where(s => s.Peer.Platform == "Android")
                    .ToArray();
                if (androidSessions.Length == 0)
                {
                    await Task.Delay(1200, token);
                    continue;
                }

                var watchedTargets = new HashSet<int>();
                foreach (var android in androidSessions)
                    watchedTargets.Add(android.EnvironmentTargetId);

                foreach (var peer in _sessions.Values)
                {
                    if (peer.Peer.Platform != "Windows") continue;
                    bool shouldWatch = watchedTargets.Contains(peer.Id);
                    if (peer.TaskbarWatchSent != shouldWatch)
                    {
                        peer.TaskbarWatchSent = shouldWatch;
                        try { await SendToAsync(peer, new Packet("taskbar-watch", shouldWatch ? 1 : 0), token); }
                        catch (Exception) { }
                    }
                }

                if (watchedTargets.Contains(0))
                {
                    var capture = WindowsTaskbarHelper.CaptureSnapshot(0);
                    foreach (var android in androidSessions)
                    {
                        if (android.EnvironmentTargetId == 0)
                        {
                            await DeliverTaskbarToAndroidAsync(
                                android, 0, capture.StateJson, capture.Signature, capture.IconsByHash, token);
                        }
                    }
                }

                // Foreground active window tracking for per-app switching rules.
                var (fgProc, fgTitle) = SystemMediaPowerHelper.GetForegroundAppInfo();
                string fgSig = fgProc + "|" + (fgTitle.Length > 60 ? fgTitle[..60] : fgTitle);
                if (!string.IsNullOrEmpty(fgProc) && !string.Equals(fgSig, _lastBroadcastForeground, StringComparison.Ordinal))
                {
                    _lastBroadcastForeground = fgSig;
                    ForegroundAppChanged?.Invoke(fgProc, fgTitle);
                    foreach (var android in androidSessions)
                    {
                        if (android.EnvironmentTargetId == 0)
                        {
                            try { await SendToAsync(android, new Packet("foreground-app", 0, Text: fgSig), token); }
                            catch { }
                        }
                    }
                }

                for (int step = 0; step < 5 && !token.IsCancellationRequested; step++)
                {
                    if (Interlocked.Exchange(ref _localTaskbarNudge, 0) != 0)
                        break;
                    await Task.Delay(200, token);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ScreenPreviewLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && !_disposed)
            {
                List<Session>? watchers = null;
                foreach (var s in _sessions.Values)
                {
                    if (s.Peer.Platform == "Android" &&
                        Volatile.Read(ref s.ScreenPreviewTarget) == 0 &&
                        Volatile.Read(ref s.ScreenPreviewMonitor) >= 0 &&
                        Volatile.Read(ref s.UrgentControlWaiters) == 0)
                    {
                        (watchers ??= new List<Session>()).Add(s);
                    }
                }
                if (watchers == null)
                {
                    try { await _previewWake.WaitAsync(1000, token); }
                    catch (ObjectDisposedException) { return; }
                    continue;
                }

                bool anyUdp = false;
                bool anyTcp = false;
                Dictionary<(int Mon, bool HighRes), (int FrameId, int PackedDims, byte[] JpegBytes)>? cache = null;
                var hub = _udpHub;
                foreach (var android in watchers)
                {
                    if (token.IsCancellationRequested || Volatile.Read(ref android.ScreenPreviewMonitor) < 0) continue;
                    int monIdx = Volatile.Read(ref android.ScreenPreviewMonitor);
                    bool highRes = Volatile.Read(ref android.ScreenPreviewHighRes) != 0;
                    cache ??= new();
                    if (!cache.TryGetValue((monIdx, highRes), out var shot))
                    {
                        shot = ScreenPreviewHelper.CaptureMonitorPreviewRaw(monIdx, highRes);
                        cache[(monIdx, highRes)] = shot;
                    }
                    if (shot.JpegBytes == null || shot.JpegBytes.Length == 0) continue;

                    if (hub != null && ActiveMediaUdpPeer(android) is { } udpPeer)
                    {
                        SendUdpMediaChunks(hub, udpPeer, FastUdpMediaKind.PcScreenPreviewChunk,
                            shot.FrameId, shot.JpegBytes, shot.PackedDims);
                        anyUdp = true;
                    }
                    else
                    {
                        const int chunkSize = 3600;
                        int total = (shot.JpegBytes.Length + chunkSize - 1) / chunkSize;
                        for (int i = 0; i < total; i++)
                        {
                            if (Volatile.Read(ref android.ScreenPreviewMonitor) < 0) break;
                            int offset = i * chunkSize;
                            int len = Math.Min(chunkSize, shot.JpegBytes.Length - offset);
                            string b64 = Convert.ToBase64String(shot.JpegBytes, offset, len);
                            try
                            {
                                await SendToAsync(android,
                                    new Packet("screen-preview-chunk", shot.FrameId, i, total, shot.PackedDims, b64),
                                    token);
                            }
                            catch { break; }
                        }
                        anyTcp = true;
                    }
                }

                await Task.Delay(anyTcp && !anyUdp ? 220 : 65, token);
            }
        }
        catch (OperationCanceledException) { }
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

    private static FastUdpHub.Peer? ActiveMouseUdpPeer(Session session)
    {
        if (Volatile.Read(ref session.PreferMobileMouse) != 0 &&
            session.MobileUdpPeer is { Confirmed: true } mob)
            return mob;
        return session.UdpPeer;
    }

    private static FastUdpHub.Peer? ActiveKeyboardUdpPeer(Session session)
    {
        if (Volatile.Read(ref session.PreferMobileKeyboard) != 0 &&
            session.MobileUdpPeer is { Confirmed: true } mob)
            return mob;
        return session.UdpPeer;
    }

    private void ApplyStreamPreference(
        Session session, bool desiredMouse, bool desiredKeyboard,
        int dualPathCode = 0, int keepWarmCode = 0)
    {
        if (dualPathCode is 1 or 2)
            Volatile.Write(ref session.DualPathControls, dualPathCode == 1 ? 1 : 0);
        if (keepWarmCode is 1 or 2)
            Volatile.Write(ref session.KeepChannelsWarm, keepWarmCode == 1 ? 1 : 0);
        Volatile.Write(ref session.DesiredPreferMobileMouse, desiredMouse ? 1 : 0);
        Volatile.Write(ref session.DesiredPreferMobileKeyboard, desiredKeyboard ? 1 : 0);
        bool mobileConfirmed = session.MobileUdpPeer is { Confirmed: true };
        bool activeMouse = desiredMouse && mobileConfirmed;
        bool activeKb = desiredKeyboard && mobileConfirmed;
        int prevMouse = Interlocked.Exchange(ref session.PreferMobileMouse, activeMouse ? 1 : 0);
        int prevKb = Interlocked.Exchange(ref session.PreferMobileKeyboard, activeKb ? 1 : 0);
        bool mouseChanged = prevMouse != (activeMouse ? 1 : 0);
        bool kbChanged = prevKb != (activeKb ? 1 : 0);
        if (mouseChanged)
        {
            Volatile.Write(ref session.DualMouseMirrorUntilTicks,
                Stopwatch.GetTimestamp() + Stopwatch.Frequency / 4);
            UpdateSessionAdaptivePacing(session);
        }
        if (kbChanged)
        {
            Volatile.Write(ref session.DualKeyboardMirrorUntilTicks,
                Stopwatch.GetTimestamp() + Stopwatch.Frequency / 4);
        }
        if (mouseChanged || kbChanged)
        {
            DiagnosticLog?.Invoke(
                $"Stream preference updated for {session.Peer.Name}: mouse={(activeMouse ? "Mobile network" : "Wi-Fi")}, keyboard={(activeKb ? "Mobile network" : "Wi-Fi")} (Wi-Fi stays connected)");
        }
    }

    private void UpdateSessionAdaptivePacing(Session session)
    {
        var pacer = session.MovePacer;
        if (pacer == null) return;
        int liveHz = Volatile.Read(ref session.LiveRefreshHz);
        int? effectiveHz = liveHz >= 24 ? liveHz : session.Peer.RefreshHz;
        if (session.IsCableSession)
        {
            pacer.SetMinIntervalMs(
                ComputeAdaptiveCableIntervalMs(effectiveHz, Volatile.Read(ref session.TcpRttMs)));
            return;
        }
        var peer = ActiveMouseUdpPeer(session);
        string activeBand = ReferenceEquals(peer, session.MobileUdpPeer) ? "cellular" : session.LinkBand;
        double rtt = peer?.LinkTracker.SmoothedRttMs ?? 0;
        double minRtt = peer?.LinkTracker.MinRttMs ?? 0;
        double jitter = peer?.LinkTracker.EffectiveJitterMs ?? 0;
        double loss = peer?.LinkTracker.CombinedLossPermille ?? 0;
        double queueDelay = peer?.LinkTracker.QueueDelayMs ?? 0;
        double paceMs = ComputeAdaptiveUdpIntervalMs(
            session.Peer.Platform, effectiveHz, rtt, minRtt, jitter, loss, activeBand);
        pacer.SetMinIntervalMs(paceMs);
        pacer.SetWarmIntervalMs(ComputeWarmIntervalMs(activeBand, queueDelay, loss));
    }

    internal string? ConnectedPeerName =>
        (Volatile.Read(ref _selected) ?? _sessions.Values.FirstOrDefault())?.Peer.Name;

    internal string? ConnectedPeerFingerprint =>
        (Volatile.Read(ref _selected) ?? _sessions.Values.FirstOrDefault())?.Peer.DeviceFingerprint;

    internal async Task<bool> OfferTrustToConnectedAsync(
        TrustedDeviceStore trusted, CancellationToken cancellation)
    {
        Session? target = Volatile.Read(ref _selected) ?? _sessions.Values.FirstOrDefault();
        if (target == null) return false;
        string tokenForClient = TrustedDeviceStore.NewSecret();
        string tokenForHub = TrustedDeviceStore.NewSecret();
        bool isWindowsMutual = string.Equals(target.Peer.Platform, "Windows", StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(target.Peer.DeviceFingerprint) &&
            MutualTrustCodec.SupportsBidirectionalProtocol(target.Peer.AppVersion);
        int offerId = Interlocked.Increment(ref target.PendingOfferedTrustId);
        if (offerId <= 0)
        {
            Interlocked.Exchange(ref target.PendingOfferedTrustId, 1);
            offerId = 1;
        }
        var offerTcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_authorityGate)
        {
            target.PendingTrustOffer?.TrySetResult(false);
            target.PendingOfferedTrustToken = tokenForClient;
            target.PendingOfferedTrustResponse = null;
            target.PendingTrustOffer = offerTcs;
        }
        try
        {
            string payload = isWindowsMutual
                ? MutualTrustCodec.SignProposal(_certificate, target.Peer.DeviceFingerprint!, tokenForClient, tokenForHub)
                : $"{Fingerprint}\n{tokenForClient}";
            await SendToAsync(target, new Packet("trust-offer", offerId, 0, 0, 0, payload), cancellation);
            using var cancelReg = cancellation.Register(() => offerTcs.TrySetCanceled(cancellation));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            using var timeoutReg = timeout.Token.Register(() => offerTcs.TrySetResult(false));
            bool accepted = await offerTcs.Task;
            if (!accepted) return false;
            trusted.AllowIncoming(target.Peer.Name, tokenForClient, target.Peer.DeviceFingerprint);
            if (isWindowsMutual)
            {
                string? counterJson;
                lock (_authorityGate)
                    counterJson = target.PendingOfferedTrustResponse;
                if (MutualTrustCodec.TryVerifyProposal(counterJson, Fingerprint, out var counter) && counter != null)
                {
                    trusted.TrustOutgoing(target.Peer.Name, counter.PeerFingerprint, counter.ReciprocalToken);
                    trusted.AutoConnectEnabled = true;
                    _status($"Two-way trusted device saved: {target.Peer.Name}");
                    return true;
                }
            }
            _status($"Trusted incoming device saved: {target.Peer.Name}");
            return true;
        }
        finally
        {
            lock (_authorityGate)
            {
                if (ReferenceEquals(target.PendingTrustOffer, offerTcs))
                {
                    target.PendingTrustOffer = null;
                    target.PendingOfferedTrustToken = null;
                    target.PendingOfferedTrustResponse = null;
                }
            }
        }
    }

    private async Task HandleTrustRequestAsync(
        Session session, int requestId, string? proposalText, CancellationToken token)
    {
        bool allowed = false;
        MutualTrustProposal? mutual = null;
        if (_trusted != null && _requestTrustApproval != null &&
            MutualTrustCodec.TryVerifyProposal(proposalText, Fingerprint, out var parsedMutual) &&
            parsedMutual != null)
        {
            mutual = parsedMutual;
        }
        bool isLegacyToken = mutual == null &&
            proposalText is { Length: 64 } legacy && legacy.All(Uri.IsHexDigit);

        if (_trusted != null && _requestTrustApproval != null && (mutual != null || isLegacyToken))
        {
            if (Interlocked.CompareExchange(ref session.PendingTrustRequest, 1, 0) != 0)
            {
                try
                {
                    await SendToAsync(session, new Packet("trust-denied", requestId), token);
                }
                catch (Exception) { }
                return;
            }
            using var trustLimit = CancellationTokenSource.CreateLinkedTokenSource(token);
            trustLimit.CancelAfter(TimeSpan.FromSeconds(50));
            try
            {
                allowed = await _requestTrustApproval(session.Peer.Name, trustLimit.Token);
                if (allowed)
                {
                    if (mutual != null)
                    {
                        _trusted.AllowIncoming(session.Peer.Name, mutual.OutgoingToken, mutual.PeerFingerprint);
                        _trusted.TrustOutgoing(session.Peer.Name, mutual.PeerFingerprint, mutual.ReciprocalToken);
                        _trusted.AutoConnectEnabled = true;
                        _status($"Two-way trusted device saved: {session.Peer.Name}");
                    }
                    else
                    {
                        _trusted.AllowIncoming(session.Peer.Name, proposalText!, session.Peer.DeviceFingerprint);
                        _status($"Trusted incoming device saved: {session.Peer.Name}");
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _status("Cannot save device trust: " + ex.Message);
            }
            catch (OperationCanceledException) { allowed = false; }
            finally
            {
                Volatile.Write(ref session.PendingTrustRequest, 0);
            }
        }
        if (token.IsCancellationRequested) return;
        try
        {
            await SendToAsync(session,
                new Packet(allowed ? "trust-granted" : "trust-denied", requestId),
                token);
        }
        catch (Exception) { }
    }

    private void ScheduleReverseClaimUdpRetry(Session session, int claimLease, int dispatchSeq)
    {
        var hub = _udpHub;
        var retryPeer = session.UdpPeer;
        if (hub == null || retryPeer is not { Confirmed: true }) return;
        double rtt = retryPeer.LinkTracker.SmoothedRttMs;
        int delayMs = (int)Math.Clamp(Math.Round(rtt > 0 ? rtt * 1.4 : 60.0), 45, 220);
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delayMs, _cancel.Token); }
            catch (OperationCanceledException) { return; }
            if (!session.HubGrantedAcked &&
                Volatile.Read(ref session.ActiveReverseLease) == claimLease &&
                retryPeer.Confirmed)
            {
                hub.Send(retryPeer, FastUdpOp.ReverseClaim, claimLease, 0, claimLease, dispatchSeq);
            }
        }, _cancel.Token);
    }

    private async Task ReportTelemetryLoopAsync(CancellationToken token)
    {
        try
        {
            int tick = 0;
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(20, token);
                long nowMs = Environment.TickCount64;
                foreach (var session in _sessions.Values)
                {
                    var peer = session.UdpPeer;
                    bool controlActive = Volatile.Read(ref session.ActiveReverseLease) > 0 || session.Lease > 0;
                    if (session.IsCableSession)
                    {
                        long nowTicks = Stopwatch.GetTimestamp();
                        if (session.LastPongTicks > 0 &&
                            (nowTicks - session.LastPongTicks) * 1000.0 / Stopwatch.Frequency >= 4000.0 &&
                            Interlocked.CompareExchange(ref session.CableDetachedOrUnresponsive, 1, 0) == 0)
                        {
                            TryPromoteWirelessBackup(session);
                        }
                        int cableGapMs = session.CableTracker.ComputeProbeIntervalMs(controlActive);
                        long lastWriteTicks = Volatile.Read(ref session.LastWriteTicks);
                        double sinceWriteMs = lastWriteTicks > 0
                            ? (nowTicks - lastWriteTicks) * 1000.0 / Stopwatch.Frequency
                            : double.MaxValue;
                        bool busyStreaming = controlActive && sinceWriteMs < 28.0 && nowMs - session.LastTcpPingMs < 95;
                        if (!busyStreaming && nowMs - session.LastTcpPingMs >= cableGapMs)
                        {
                            session.LastTcpPingMs = nowMs;
                            _ = SendCablePingAsync(session, token);
                        }
                    }
                    else if (_udpHub != null)
                    {
                        bool isAndroidPeer = string.Equals(session.Peer.Platform, "Android", StringComparison.Ordinal);
                        SendPeriodicUdpPingIfNeeded(session, peer, nowMs, controlActive, isAndroidPeer);
                        if (controlActive && Volatile.Read(ref session.KeepChannelsWarm) != 0
                            && peer is { Confirmed: true }
                            && nowMs - Volatile.Read(ref peer.LastAnySendMs) >= 85)
                        {
                            _udpHub.Send(peer, FastUdpOp.Warm, 0, 0, 0);
                        }
                        if (session.MobileUdpPeer is { Confirmed: true } mobPeer)
                        {
                            SendPeriodicUdpPingIfNeeded(session, mobPeer, nowMs, controlActive, isAndroidPeer);
                        }
                    }

                    if ((tick % 150) != 149) continue;
                    int liveHz = Volatile.Read(ref session.LiveRefreshHz);
                    int effectiveHz = liveHz >= 24 ? liveHz : (session.Peer.RefreshHz ?? 0);
                    if (session.IsCableSession)
                    {
                        long cableMoves = Interlocked.Exchange(ref session.CableMoves, 0);
                        long cableFallback = Interlocked.Exchange(ref session.TcpFallbackMoves, 0);
                        if (cableMoves == 0 && cableFallback == 0) continue;
                        DiagnosticLog?.Invoke(
                            $"Telemetry [hub->{session.Peer.Name}]: app={session.Peer.AppVersion ?? "?"} transport=cable live_hz={effectiveHz} cable_moves={cableMoves} tcp_fallback={cableFallback}");
                        continue;
                    }
                    var pacer = session.MovePacer;
                    if (pacer == null) continue;
                    var s = pacer.TakeStats();
                    long fallback = Interlocked.Exchange(ref session.TcpFallbackMoves, 0);
                    if (s.UdpDirect == 0 && s.UdpPacedFlush == 0 && fallback == 0)
                        continue;
                    var activePeer = ActiveMouseUdpPeer(session);
                    var activeKbPeer = ActiveKeyboardUdpPeer(session);
                    string mousePath = ReferenceEquals(activePeer, session.MobileUdpPeer) ? "mobile" : "wifi";
                    string kbPath = ReferenceEquals(activeKbPeer, session.MobileUdpPeer) ? "mobile" : "wifi";
                    double rtt = activePeer?.LinkTracker.SmoothedRttMs ?? 0;
                    double jitter = activePeer?.LinkTracker.EffectiveJitterMs ?? 0;
                    double loss = activePeer?.LinkTracker.CombinedLossPermille ?? 0;
                    long lateTotal = activePeer?.LinkTracker.LatePackets ?? 0;
                    long reorderedTotal = activePeer?.LinkTracker.ReorderedPackets ?? 0;
                    string mobSuffix = session.MobileUdpPeer is { Confirmed: true } mob && !ReferenceEquals(activePeer, mob)
                        ? $" mobile_rtt={mob.LinkTracker.SmoothedRttMs:F1} mobile_loss={mob.LinkTracker.CombinedLossPermille / 10.0:F2}"
                        : (peer is { Confirmed: true } && ReferenceEquals(activePeer, session.MobileUdpPeer)
                            ? $" wifi_rtt={peer.LinkTracker.SmoothedRttMs:F1} wifi_loss={peer.LinkTracker.CombinedLossPermille / 10.0:F2}"
                            : "");
                    DiagnosticLog?.Invoke(
                        $"Telemetry [hub->{session.Peer.Name}]: app={session.Peer.AppVersion ?? "?"} mouse={mousePath} kb={kbPath} fast_confirmed={activePeer?.Confirmed == true} live_hz={effectiveHz} pace_ms={pacer.EffectiveMinIntervalMs:F1} rtt_ms={rtt:F1} jitter_ms={jitter:F1} loss_pct={loss / 10.0:F2}{mobSuffix} fast_direct={s.UdpDirect} fast_paced={s.UdpPacedFlush} trailing_sync={s.TrailingTcpSync} tcp_fallback={fallback} late_total={lateTotal} reordered_total={reorderedTotal}");
                }
                tick++;
            }
        }
        catch (OperationCanceledException) { }
    }

    private void SendPeriodicUdpPingIfNeeded(
        Session session, FastUdpHub.Peer? peer, long nowMs, bool controlActive, bool isAndroidPeer)
    {
        if (_udpHub == null || peer is not { Confirmed: true }) return;
        double sinceMoveMs = double.MaxValue;
        if (controlActive)
        {
            long nowTicks = Stopwatch.GetTimestamp();
            long lastSentTicks = Volatile.Read(ref peer.LastMoveSentTicks);
            sinceMoveMs = lastSentTicks > 0
                ? (nowTicks - lastSentTicks) * 1000.0 / Stopwatch.Frequency
                : double.MaxValue;
        }
        long probeGapMs = peer.LinkTracker.ComputeProbeIntervalMs(controlActive);
        if (nowMs - peer.LastLinkPingMs < probeGapMs) return;

        long lastArriveMs = peer.LinkTracker.LastArriveMs;
        bool radioRecentlyAwake = !isAndroidPeer ||
            (!peer.IsAuxiliary && session.LinkBand == "ethernet") ||
            (lastArriveMs > 0 && nowMs - lastArriveMs <= 25) ||
            (controlActive && sinceMoveMs < 35.0);
        if (radioRecentlyAwake)
        {
            Volatile.Write(ref peer.PendingWakePing, 0);
            peer.LastLinkPingMs = nowMs;
            int paceTenths = (int)Math.Round((session.MovePacer?.EffectiveMinIntervalMs ?? 0) * 10.0);
            _udpHub.Send(peer, FastUdpOp.LinkPing,
                peer.LinkTracker.NextPingToken(nowMs), paceTenths,
                Volatile.Read(ref session.ActiveReverseLease));
        }
        else if (Volatile.Read(ref peer.PendingWakePing) == 0)
        {
            Volatile.Write(ref peer.PendingWakePing, 1);
            peer.LastWakePrimerMs = nowMs;
            _udpHub.Send(peer, FastUdpOp.Warm, 1, 0, 0);
        }
        else if (nowMs - peer.LastWakePrimerMs >= 140)
        {
            Volatile.Write(ref peer.PendingWakePing, 0);
            peer.LastLinkPingMs = nowMs;
            int paceTenths = (int)Math.Round((session.MovePacer?.EffectiveMinIntervalMs ?? 0) * 10.0);
            _udpHub.Send(peer, FastUdpOp.LinkPing,
                peer.LinkTracker.NextPingToken(nowMs), paceTenths,
                Volatile.Read(ref session.ActiveReverseLease));
        }
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
                try
                {
                    if (client.Client.AddressFamily == AddressFamily.InterNetworkV6)
                        client.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.TypeOfService, 0xB8);
                    else
                        client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService, 0xB8);
                }
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
        try
        {
            await Wire.WriteAsync(session.Stream, packet, token);
            Volatile.Write(ref session.LastWriteTicks, Stopwatch.GetTimestamp());
        }
        finally { session.Write.Release(); }
    }

    private static async Task SendMoveToAsync(Session session, Packet packet,
        CancellationToken token)
    {
        while (Volatile.Read(ref session.UrgentControlWaiters) > 0 && !token.IsCancellationRequested)
            await Task.Yield();
        await SendToAsync(session, packet, token);
    }

    private static async Task SendUrgentAsync(Session session, Packet packet,
        CancellationToken token)
    {
        bool waiterCounted = true;
        Interlocked.Increment(ref session.UrgentControlWaiters);
        try
        {
            await session.Write.WaitAsync(token);
            Interlocked.Decrement(ref session.UrgentControlWaiters);
            waiterCounted = false;
            try { await Wire.WriteAsync(session.Stream, packet, token); }
            finally { session.Write.Release(); }
        }
        finally
        {
            if (waiterCounted) Interlocked.Decrement(ref session.UrgentControlWaiters);
        }
    }

    private static async Task SendCablePingAsync(Session session, CancellationToken token)
    {
        if (Volatile.Read(ref session.UrgentControlWaiters) > 0)
            return;
        if (Interlocked.CompareExchange(ref session.CablePingInFlight, 1, 0) != 0)
            return;
        try
        {
            await session.Write.WaitAsync(token);
            try
            {
                int pingToken = session.CableTracker.NextPingToken(Environment.TickCount64);
                await Wire.WriteAsync(session.Stream, new Packet("ping", pingToken), token);
            }
            finally { session.Write.Release(); }
        }
        catch (Exception) { }
        finally
        {
            Interlocked.Exchange(ref session.CablePingInFlight, 0);
        }
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
        if (relay.Target.IsCableSession)
            Interlocked.Increment(ref relay.Target.CableMoves);
        else if (countAsFallback)
            Interlocked.Increment(ref relay.Target.TcpFallbackMoves);
        relay.Target.SetLatestReverseMoveOrdered(ref relay.LatestMove, packet);
        if(Interlocked.CompareExchange(ref relay.MoveSignaled,1,0)==0 &&
            !relay.InputQueue.Writer.TryWrite(new Packet("move-flush")))
        {
            Interlocked.Exchange(ref relay.MoveSignaled,0);
            return false;
        }
        return true;
    }

    internal static bool SupportsWindowsUdpReverseKey(string? platform, string? appVersion) =>
        string.Equals(platform, "Windows", StringComparison.Ordinal) &&
        Version.TryParse(appVersion, out var parsed) && parsed >= new Version(0, 3, 71);

    internal static Packet PrepareReverseControlPacket(string? platform, int actionSeq, Packet packet)
    {
        if (packet.Type == "key")
        {
            return string.Equals(platform, "Android", StringComparison.Ordinal)
                ? packet with { A = actionSeq }
                : packet with { Text = actionSeq.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        }
        return packet with { C = actionSeq };
    }

    private void SendFastControlAndAssignSeq(Session target, int lease, ref Packet packet)
    {
        if (packet.Type is not ("button" or "wheel" or "key")) return;
        int actionSeq = target.NextReverseActionSeq();
        bool isKey = packet.Type == "key";
        bool targetIsAndroid = string.Equals(target.Peer.Platform, "Android", StringComparison.Ordinal);
        packet = PrepareReverseControlPacket(target.Peer.Platform, actionSeq, packet);
        if (isKey && !targetIsAndroid && !SupportsWindowsUdpReverseKey(target.Peer.Platform, target.Peer.AppVersion))
            return;
        var activeUdp = isKey ? ActiveKeyboardUdpPeer(target) : ActiveMouseUdpPeer(target);
        if (_udpHub == null || activeUdp is not { Confirmed: true } || lease <= 0)
            return;
        if (!isKey) target.MovePacer?.FlushThrottledUdpMoveNow();
        FastUdpOp op = packet.Type switch
        {
            "button" => FastUdpOp.ReverseButton,
            "wheel" => FastUdpOp.ReverseWheel,
            _ => FastUdpOp.ReverseKey
        };
        int udpA = isKey
            ? (targetIsAndroid
                ? packet.D
                : ((packet.D & 0xFF) | ((packet.A & 0xFF) << 8) | ((packet.B != 0 ? 1 : 0) << 16)))
            : packet.A;
        int udpB = isKey ? (packet.C != 0 ? 1 : 0) : packet.B;
        _udpHub.Send(activeUdp, op, udpA, udpB, lease, actionSeq);
        bool dualControls = Volatile.Read(ref target.DualPathControls) != 0;
        long mirrorUntil = Volatile.Read(ref isKey ? ref target.DualKeyboardMirrorUntilTicks : ref target.DualMouseMirrorUntilTicks);
        if (dualControls || mirrorUntil > Stopwatch.GetTimestamp())
        {
            var backup = ReferenceEquals(activeUdp, target.MobileUdpPeer) ? target.UdpPeer : target.MobileUdpPeer;
            if (backup is { Confirmed: true })
                _udpHub.Send(backup, op, udpA, udpB, lease, actionSeq);
        }
    }

    private bool QueueRoutedInput(RelayLease relay,Packet packet)
    {
        if(packet.Type=="move")
        {
            if(relay.Target.MovePacer?.TryHandleMove(packet) == true)
            {
                if (!relay.Target.IsCableSession)
                    Interlocked.Exchange(ref relay.LatestMove, null);
                return true;
            }
            return QueueTcpRelayMove(relay, packet, countAsFallback: !relay.Target.IsCableSession);
        }
        if(packet.Type is "button" or "wheel" or "key")
            SendFastControlAndAssignSeq(relay.Target, relay.Claim, ref packet);
        return relay.InputQueue.Writer.TryWrite(packet);
    }

    private RelayLease? RemoveRelayFromSource(int sourceId)
    {
        lock (_authorityGate)
        {
            if (!_relayBySource.Remove(sourceId, out var relay)) return null;
            _relayByTarget.Remove(relay.Target.Id);
            Volatile.Write(ref relay.Target.ActiveReverseLease, 0);
            Volatile.Write(ref relay.Target.PendingFirstCursorAck, 0);
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
        relay.Target.MovePacer?.SetMinIntervalMs(
            relay.Target.Peer.Platform == "Android"
                ? RecommendedUdpMinIntervalMs(relay.Target.Peer.Platform, relay.Target.Peer.RefreshHz)
                : 4.5);
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
        var (localCam, localMic) = WindowsMediaCaptureService.DetectLocalMediaCapabilities();
        var members = new List<EnvironmentMember> {
            new(0, Environment.MachineName, "Windows",
                MonitorTopology.Capture().WithPrimaryAppVersion(MonitorTopology.CurrentAppVersion()).ToWire(),
                true, Fingerprint, Volatile.Read(ref _localNotificationCount),
                HasCamera: localCam, HasMicrophone: localMic)
        };
        foreach (var peer in _sessions.Values.OrderBy(p => p.Id))
            members.Add(new(peer.Id, peer.Peer.Name, peer.Peer.Platform,
                peer.Peer.Monitors.ToWire(), peer.Peer.SupportsReverseInput,
                LayoutIdentity(peer.Peer), peer.Peer.NotificationCount,
                BatteryPercent: peer.Peer.BatteryPercent, IsCharging: peer.Peer.IsCharging,
                HasCamera: peer.Peer.HasCamera, HasMicrophone: peer.Peer.HasMicrophone));
        var knownIdentities = new HashSet<string>(
            members.Select(m => m.StableIdentity).OfType<string>().Where(s => !string.IsNullOrWhiteSpace(s)),
            StringComparer.OrdinalIgnoreCase);
        foreach (var bridged in BridgedPeers)
        {
            string identity = LayoutIdentity(bridged.Peer);
            if (!knownIdentities.Add(identity)) continue;
            members.Add(new(bridged.VirtualId, bridged.Peer.Name, bridged.Peer.Platform,
                bridged.Peer.Monitors.ToWire(), bridged.Peer.SupportsReverseInput,
                identity, bridged.Peer.NotificationCount));
        }
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
        members = ApplyAndOrderSpatialMembers(members);
        string payload = SerializeCompactRoster(members);
        if (System.Text.Encoding.UTF8.GetByteCount(payload) > 7800)
        {
            _status("Environment roster too large; refusing to advertise incomplete routes.");
            return;
        }
        (EnvironmentMember[] Members, int OwnId)? upstreamOverlay;
        lock (_bridgedLock)
            upstreamOverlay = _upstreamRosterOverlay;
        foreach (var recipient in _sessions.Values)
        {
            string sendPayload = payload;
            if (recipient.Peer.Platform == "Android" && upstreamOverlay is { } overlay)
            {
                var combined = new List<EnvironmentMember>
                {
                    new(0, Environment.MachineName, "Windows",
                        MonitorTopology.Capture().WithPrimaryAppVersion(MonitorTopology.CurrentAppVersion()).ToWire(),
                        true, Fingerprint, Volatile.Read(ref _localNotificationCount),
                        HasCamera: localCam, HasMicrophone: localMic)
                };
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Fingerprint };
                foreach (var p in _sessions.Values.OrderBy(s => s.Id))
                {
                    string id = LayoutIdentity(p.Peer);
                    if (seen.Add(id))
                        combined.Add(new(p.Id, p.Peer.Name, p.Peer.Platform,
                            p.Peer.Monitors.ToWire(), p.Peer.SupportsReverseInput, id, p.Peer.NotificationCount,
                            BatteryPercent: p.Peer.BatteryPercent, IsCharging: p.Peer.IsCharging,
                            HasCamera: p.Peer.HasCamera, HasMicrophone: p.Peer.HasMicrophone));
                }
                int extraId = 9900;
                foreach (var up in overlay.Members)
                {
                    if (string.IsNullOrWhiteSpace(up.StableIdentity) || !seen.Add(up.StableIdentity))
                        continue;
                    combined.Add(up with { Id = ++extraId, SupportsReverseInput = false, HasCamera = false, HasMicrophone = false });
                }
                combined = ApplyAndOrderSpatialMembers(combined);
                string candidate = SerializeCompactRoster(combined);
                if (System.Text.Encoding.UTF8.GetByteCount(candidate) <= 7800)
                    sendPayload = candidate;
            }
            try { await SendToAsync(recipient,
                new Packet("environment-roster", recipient.Id, Text: sendPayload), token); }
            catch (Exception) { /* peer may have disconnected */ }
        }
        await PublishEnvironmentLayoutAsync(token);
    }

    private static string SerializeCompactRoster(IReadOnlyList<EnvironmentMember> members)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartArray();
            foreach (var m in members)
            {
                w.WriteStartObject();
                w.WriteNumber("Id", m.Id);
                w.WriteString("Name", m.Name);
                w.WriteString("Platform", m.Platform);
                w.WriteString("Layout", m.Layout);
                w.WriteBoolean("SupportsReverseInput", m.SupportsReverseInput);
                if (m.StableIdentity != null) w.WriteString("StableIdentity", m.StableIdentity);
                if (m.NotificationCount >= 0) w.WriteNumber("NotificationCount", m.NotificationCount);
                if (m.ArrangementOrder >= 0) w.WriteNumber("ArrangementOrder", m.ArrangementOrder);
                if (m.LayoutW > 0 || m.LayoutH > 0)
                {
                    w.WriteNumber("LayoutX", Math.Round(m.LayoutX, 3));
                    w.WriteNumber("LayoutY", Math.Round(m.LayoutY, 3));
                    w.WriteNumber("LayoutW", Math.Round(m.LayoutW, 3));
                    w.WriteNumber("LayoutH", Math.Round(m.LayoutH, 3));
                }
                if (m.BatteryPercent >= 0) w.WriteNumber("BatteryPercent", m.BatteryPercent);
                if (m.IsCharging) w.WriteBoolean("IsCharging", true);
                if (m.HasCamera) w.WriteBoolean("HasCamera", true);
                if (m.HasMicrophone) w.WriteBoolean("HasMicrophone", true);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private void NotifySelection(Session? previous, Session? next)
    {
        if (ReferenceEquals(previous, next)) return;
        if (previous != null) PeerDisconnected?.Invoke();
        if (next != null) PeerConnected?.Invoke(next.Peer);
    }

    private bool SelectVirtualPeer(BridgedSubPeer bridged)
    {
        Session? previousDirect;
        int prevVirtual;
        lock (_authorityGate)
        {
            lock (_bridgedLock)
            {
                if (!_bridgedByVirtualId.ContainsKey(bridged.VirtualId)) return false;
            }
            previousDirect = _selected;
            prevVirtual = _selectedVirtualSubId;
            if (previousDirect == null && prevVirtual == bridged.VirtualId) return true;
            _selected = null;
            Volatile.Write(ref _selectedVirtualSubId, bridged.VirtualId);
        }
        if (previousDirect != null)
        {
            TrySendReverseTo(previousDirect.Id, new Packet("reset"));
            PeerDisconnected?.Invoke();
        }
        else if (prevVirtual > 0)
        {
            TrySendVirtualReverse(prevVirtual, new Packet("reset"));
            PeerDisconnected?.Invoke();
        }
        PeerConnected?.Invoke(BuildBridgedPeer(bridged));
        return true;
    }

    private bool Select(Session candidate)
    {
        Session? previous;
        int prevVirtual;
        lock (_authorityGate)
        {
            if (!_sessions.ContainsKey(candidate.Id)) return false;
            previous = _selected;
            prevVirtual = _selectedVirtualSubId;
            if (ReferenceEquals(previous, candidate)) return true;
            _selected = candidate;
            Volatile.Write(ref _selectedVirtualSubId, 0);
        }
        // A target change must revoke the previous reverse input lease on
        // the PREVIOUS authenticated TLS stream, not on the newly selected PC
        // or phone. This prevents keyboard ownership lingering after switching.
        if (previous != null) TrySendReverseTo(previous.Id, new Packet("reset"));
        else if (prevVirtual > 0)
        {
            TrySendVirtualReverse(prevVirtual, new Packet("reset"));
            PeerDisconnected?.Invoke();
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

    internal void DisconnectPeer()
    {
        Session? direct = Volatile.Read(ref _selected);
        if (direct != null)
        {
            direct.Client.Close();
            return;
        }
        int virtualId = Volatile.Read(ref _selectedVirtualSubId);
        if (virtualId > 0)
        {
            lock (_bridgedLock)
            {
                if (_bridgedByVirtualId.TryGetValue(virtualId, out var bridged))
                    bridged.BridgeSession.Client.Close();
            }
        }
    }

    internal bool DisconnectPeerByFingerprint(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint)) return false;
        bool closed = false;
        foreach (var s in _sessions.Values)
        {
            if (string.Equals(s.Peer.DeviceFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                try { s.Client.Close(); closed = true; }
                catch { }
            }
        }
        return closed;
    }

    internal MonitorTopology RemoteMonitors
    {
        get
        {
            Session? direct = Volatile.Read(ref _selected);
            if (direct != null) return direct.Peer.Monitors;
            int virtualId = Volatile.Read(ref _selectedVirtualSubId);
            if (virtualId > 0)
            {
                lock (_bridgedLock)
                {
                    if (_bridgedByVirtualId.TryGetValue(virtualId, out var bridged))
                        return bridged.Topology;
                }
            }
            throw new InvalidOperationException("No authenticated peer layout.");
        }
    }
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

    private sealed class BoundVirtualSubPeerSession : IInputSession
    {
        private readonly ReceiverServer _server;
        private readonly int _virtualId;
        internal BoundVirtualSubPeerSession(ReceiverServer server, int virtualId)
        {
            _server = server; _virtualId = virtualId;
        }
        public MonitorTopology RemoteMonitors
        {
            get
            {
                lock (_server._bridgedLock)
                {
                    if (_server._bridgedByVirtualId.TryGetValue(_virtualId, out var bridged))
                        return bridged.Topology;
                }
                throw new InvalidOperationException("Selected bridged peer disconnected.");
            }
        }
        public bool TrySend(Packet packet) =>
            _server.TrySendVirtualReverse(_virtualId, packet);
    }

    internal IInputSession BindSelectedInput()
    {
        Session? selected = Volatile.Read(ref _selected);
        if (selected != null) return new BoundPeerInputSession(this, selected.Id);
        int virtualId = Volatile.Read(ref _selectedVirtualSubId);
        if (virtualId > 0)
        {
            lock (_bridgedLock)
            {
                if (_bridgedByVirtualId.ContainsKey(virtualId))
                    return new BoundVirtualSubPeerSession(this, virtualId);
            }
        }
        throw new InvalidOperationException("No authenticated input target.");
    }

    internal bool TrySendReverse(Packet packet)
    {
        Session? selected = Volatile.Read(ref _selected);
        if (selected != null) return TrySendReverseTo(selected.Id, packet);
        int virtualId = Volatile.Read(ref _selectedVirtualSubId);
        return virtualId > 0 && TrySendVirtualReverse(virtualId, packet);
    }

    private bool TrySendVirtualReverse(int virtualId, Packet packet)
    {
        BridgedSubPeer? bridged;
        lock (_bridgedLock)
            _bridgedByVirtualId.TryGetValue(virtualId, out bridged);
        if (bridged == null || !_sessions.ContainsKey(bridged.BridgeSession.Id))
            return false;
        if (packet.Type == "warm")
        {
            if (_udpHub != null && bridged.BridgeSession.UdpPeer is { HasEndpoint: true } udpPeer)
                _udpHub.Send(udpPeer, FastUdpOp.Warm, 0, 0, 0);
            return true;
        }
        lock (_authorityGate)
        {
            if (packet.Type == "claim")
            {
                if (packet.A <= 0 || _virtualHubClaims.ContainsKey(virtualId))
                    return false;
                if (_virtualRelayBySource.Values.Any(r => r.VirtualTargetId == virtualId))
                    return false;
                _virtualHubClaims[virtualId] = (packet.A, bridged.BridgeSession, bridged.Descriptor.LocalId);
            }
            else if (packet.Type is ("reset" or "release"))
            {
                if (_virtualHubClaims.TryGetValue(virtualId, out var active) &&
                    (packet.Type == "reset" || packet.A == active.Lease))
                    _virtualHubClaims.Remove(virtualId);
            }
            else if (!_virtualHubClaims.ContainsKey(virtualId))
            {
                return false;
            }
        }
        string envelope = JsonSerializer.Serialize(new SubRouteEnvelope(bridged.Descriptor.LocalId, packet));
        return bridged.BridgeSession.ReverseQueue.Writer.TryWrite(
            new Packet("sub-route", bridged.VirtualId, Text: envelope));
    }

    private static bool QueueTcpReverseMove(Session selected, Packet packet,
        bool countAsFallback = true)
    {
        if (selected.IsCableSession)
            Interlocked.Increment(ref selected.CableMoves);
        else if (countAsFallback)
            Interlocked.Increment(ref selected.TcpFallbackMoves);
        selected.SetLatestReverseMoveOrdered(ref selected.LatestReverseMove, packet, trackWaypoint: true);
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
            if (_hubClaims.ContainsKey(session.Id) || Volatile.Read(ref session.BridgedClaimLease) > 0)
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
        int fastClaimLease = 0;
        lock (_authorityGate)
        {
            if (packet.Type=="claim")
            {
                if (packet.A<=0 || _relayByTarget.ContainsKey(id) ||
                    _hubClaims.ContainsKey(id) || Volatile.Read(ref selected.BridgedClaimLease) > 0)
                    return false;
                _hubClaims[id]=packet.A;
                selected.HubGrantedAcked = false;
                selected.HubActiveAcked = false;
                Volatile.Write(ref selected.PendingFirstCursorAck, 1);
                Volatile.Write(ref selected.ActiveReverseLease, packet.A);
                selected.MovePacer?.SetWarmActive(true);
                int dispatchSeq = selected.NextReverseClaimDispatchSeq();
                packet = packet with { C = dispatchSeq };
                if (_udpHub != null && selected.UdpPeer is { HasEndpoint: true } udpPeer)
                {
                    if (udpPeer.Confirmed)
                    {
                        _udpHub.Send(udpPeer, FastUdpOp.ReverseClaim, packet.A, 0, packet.A, dispatchSeq);
                        ScheduleReverseClaimUdpRetry(selected, packet.A, dispatchSeq);
                    }
                    else
                    {
                        _udpHub.Send(udpPeer, FastUdpOp.Probe, _udpHub.Port, 0, 0);
                    }
                }
            }
            else if(packet.Type is "reset" or "release")
            {
                if(_hubClaims.TryGetValue(id,out int claim) &&
                    (packet.Type=="reset" || claim==packet.A))
                {
                    _hubClaims.Remove(id);
                    selected.HubGrantedAcked = false;
                    selected.HubActiveAcked = false;
                    Volatile.Write(ref selected.PendingFirstCursorAck, 0);
                    Volatile.Write(ref selected.ActiveReverseLease, 0);
                    selected.MovePacer?.SetWarmActive(false);
                    selected.MovePacer?.ClearPending();
                }
                else
                    // An already-revoked RELEASE is an idempotent success,
                    // but must NEVER send a reset to another routed owner.
                    return packet.Type=="release";
            }
            else if (!_hubClaims.TryGetValue(id, out fastClaimLease))
                return false;
        }
        if (packet.Type == "move")
        {
            if (selected.MovePacer?.TryHandleMove(packet) == true)
            {
                if (!selected.IsCableSession)
                    Interlocked.Exchange(ref selected.LatestReverseMove, null);
                return true;
            }
            return QueueTcpReverseMove(selected, packet, countAsFallback: !selected.IsCableSession);
        }
        if (packet.Type is "button" or "wheel" or "key")
            SendFastControlAndAssignSeq(selected, fastClaimLease, ref packet);
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
                if (p.Type == "sub-route")
                {
                    await SendToAsync(session, p, token);
                    continue;
                }
                // Old target movements/buttons must never leak after a
                // selection change. Its pending RELEASE/RESET must still be
                // delivered on the ORIGINAL authenticated TLS stream.
                bool stillSelected = ReferenceEquals(Volatile.Read(ref _selected), session) ||
                    Volatile.Read(ref session.BridgedClaimLease) > 0;
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
                        await SendMoveToAsync(session, EncodeMoveForWire(session, move), token);
                    continue;
                }
                if (p.Type is "release" or "reset")
                {
                    session.MovePacer?.ClearPending();
                    var staleMove = Interlocked.Exchange(ref session.LatestReverseMove, null);
                    if (staleMove != null)
                        await SendMoveToAsync(session, EncodeMoveForWire(session, staleMove), token);
                    await SendToAsync(session, ReverseInputProtocol.Encode(p), token);
                    continue;
                }
                bool isImmediateControl = p.Type is "button" or "wheel" or "key" or "text";
                var udpSync = session.MovePacer?.TakePendingReliableSync(
                    skipIfUdpDelivered: isImmediateControl);
                var tcpPrior = Interlocked.Exchange(ref session.LatestReverseMove, null);
                var prior = udpSync ?? (isImmediateControl && ActiveMouseUdpPeer(session) is { Confirmed: true }
                    ? null : tcpPrior);
                if (prior != null && !(isImmediateControl && session.MatchesLastWireMove(prior)))
                    await SendUrgentAsync(session, EncodeMoveForWire(session, prior), token);
                if (isImmediateControl)
                    await SendUrgentAsync(session, ReverseInputProtocol.Encode(p), token);
                else
                    await SendMoveToAsync(session, ReverseInputProtocol.Encode(p), token);
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
            hello.RotationDegrees ?? primaryMon.RotationDegrees,
            hello.NotificationCount is >= 0 and <= 9999 ? hello.NotificationCount.Value : -1,
            HasCamera: hello.HasCamera ?? (platform == "Android"),
            HasMicrophone: hello.HasMicrophone ?? (platform == "Android"));
    }

    internal static (string Band, int SpeedMbps) DetectInterfaceForLocalAddress(IPAddress? localAddress)
    {
        localAddress = UnmapAddress(localAddress);
        if (localAddress == null || IPAddress.IsLoopback(localAddress))
            return ("wireless", 0);
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;
                var ipProps = nic.GetIPProperties();
                foreach (var unicast in ipProps.UnicastAddresses)
                {
                    if (unicast.Address.Equals(localAddress))
                    {
                        int speedMbps = nic.Speed > 0 ? (int)(nic.Speed / 1_000_000L) : 0;
                        if (nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet
                            or NetworkInterfaceType.GigabitEthernet
                            or NetworkInterfaceType.FastEthernetFx
                            or NetworkInterfaceType.FastEthernetT)
                        {
                            return ("ethernet", speedMbps);
                        }
                        if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                        {
                            return ("wireless", speedMbps);
                        }
                        return (nic.NetworkInterfaceType.ToString().ToLowerInvariant(), speedMbps);
                    }
                }
            }
        }
        catch
        {
        }
        return ("wireless", 0);
    }

    internal static IReadOnlyList<string> CollectReachableGlobalIpv6Addresses()
    {
        var primary = new List<string>();
        var secondary = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback
                    or NetworkInterfaceType.Tunnel)
                    continue;
                var ipProps = nic.GetIPProperties();
                if (ipProps.GatewayAddresses.Count == 0)
                    continue;
                foreach (var uni in ipProps.UnicastAddresses)
                {
                    var ip = uni.Address;
                    if (ip.AddressFamily != AddressFamily.InterNetworkV6)
                        continue;
                    if (ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.IsIPv6SiteLocal ||
                        ip.IsIPv6Teredo || ip.IsIPv4MappedToIPv6 || IPAddress.IsLoopback(ip))
                        continue;
                    byte[] bytes = ip.GetAddressBytes();
                    // Skip ULA fc00::/7 (only globally routable 2000::/3 or non-ULA).
                    if ((bytes[0] & 0xFE) == 0xFC)
                        continue;
                    if (uni.DuplicateAddressDetectionState != DuplicateAddressDetectionState.Preferred)
                        continue;
                    string text = ip.ToString();
                    int zoneIdx = text.IndexOf('%');
                    if (zoneIdx >= 0) text = text[..zoneIdx];
                    if (!seen.Add(text))
                        continue;
                    if (uni.SuffixOrigin == SuffixOrigin.Random)
                        secondary.Add(text);
                    else
                        primary.Add(text);
                }
            }
        }
        catch
        {
        }
        primary.AddRange(secondary);
        if (primary.Count > 3)
            primary.RemoveRange(3, primary.Count - 3);
        return primary;
    }

    internal static string? BuildEndpointsJson()
    {
        var v6 = CollectReachableGlobalIpv6Addresses();
        if (v6.Count == 0) return null;
        return JsonSerializer.Serialize(new { v6, port = Wire.Port });
    }

    internal static IPEndPoint[] ParseMobileCandidateEndPoints(string? json, int port)
    {
        if (string.IsNullOrWhiteSpace(json) || port is <= 0 or > 65535)
            return Array.Empty<IPEndPoint>();
        var list = new List<IPEndPoint>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) continue;
                    if (!IPAddress.TryParse(item.GetString(), out var ip)) continue;
                    if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6Multicast ||
                        ip.IsIPv6SiteLocal || ip.IsIPv4MappedToIPv6) continue;
                    if (ip.AddressFamily == AddressFamily.InterNetworkV6 && (ip.GetAddressBytes()[0] & 0xFE) == 0xFC)
                        continue;
                    list.Add(new IPEndPoint(ip, port));
                    if (list.Count >= 4) break;
                }
            }
        }
        catch
        {
        }
        return list.ToArray();
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
        bool requestProbe = request.Type == "auth-trusted-probe";
        var trustedSource = request.Type is "auth-trusted" or "auth-trusted-probe"
            ? _trusted?.AuthenticateIncoming(request.Text)
            : null;
        if (requestProbe)
        {
            if (trustedSource != null)
            {
                string probeIp = UnmapAddress((client.Client.RemoteEndPoint as IPEndPoint)?.Address)?.ToString() ?? "?";
                _status?.Invoke("Trusted path probe from " + probeIp);
                await Wire.WriteAsync(ssl,
                    new Packet("probe-ok", Wire.Port, Text: _udpHub?.Port > 0 ? _udpHub.Port.ToString() : ""),
                    token);
                if (request.A == 2)
                {
                    using var echoLimit = CancellationTokenSource.CreateLinkedTokenSource(token);
                    echoLimit.CancelAfter(TimeSpan.FromMilliseconds(1500));
                    for (int s = 1; s < 3; s++)
                    {
                        try
                        {
                            var echoReq = await Wire.ReadAsync(ssl, echoLimit.Token);
                            if (echoReq.Type != "probe-echo") break;
                            await Wire.WriteAsync(ssl, new Packet("probe-ok", Wire.Port), echoLimit.Token);
                        }
                        catch (Exception) { break; }
                    }
                }
            }
            else
            {
                await Wire.WriteAsync(ssl, new Packet("denied"), token);
            }
            return;
        }
        bool approved=trustedSource!=null ||
            (request.Type=="auth" && (
                ConstantTimeSecret(request.Text,Secret) ||
                TryVerifyCustomPairingCode(request.Text)));
        if (!approved && request.Type=="auth-request" && _requestApproval!=null)
        {
            using var approvalLimit = CancellationTokenSource.CreateLinkedTokenSource(token);
            approvalLimit.CancelAfter(TimeSpan.FromSeconds(40));
            string ip = UnmapAddress((client.Client.RemoteEndPoint as IPEndPoint)?.Address)?.ToString() ?? "?";
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
        var topology=MonitorTopology.Capture().WithPrimaryAppVersion(MonitorTopology.CurrentAppVersion());
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
        IPAddress? remoteAddress = UnmapAddress((client.Client.RemoteEndPoint as IPEndPoint)?.Address);
        bool isLoopbackRemote = remoteAddress != null && IPAddress.IsLoopback(remoteAddress);
        bool isCable = string.Equals(peer.Platform, "Android", StringComparison.Ordinal) &&
            isLoopbackRemote;
        var (localBand, localSpeed) = DetectInterfaceForLocalAddress(
            client.Client.LocalEndPoint is IPEndPoint localEp ? UnmapAddress(localEp.Address) : null);
        string initialBand = isCable
            ? "cable"
            : (localBand == "ethernet" && string.Equals(peer.Platform, "Windows", StringComparison.Ordinal)
                ? "ethernet"
                : "wireless");
        int initialSpeed = isCable ? 0 : localSpeed;
        var session=new Session{
            Id=id,Client=client,Stream=ssl,Peer=peer,IsCableSession=isCable,
            RemoteAddress=remoteAddress,
            LinkBand=initialBand,
            LinkSpeedMbps=initialSpeed,
            LastPongTicks=Stopwatch.GetTimestamp(),
            TrustedTokenHash=trustedSource?.TokenSha256
        };
        session.UdpPeer = isCable ? null : _udpHub?.AddPeer(id, client.Client.RemoteEndPoint, initialBand, initialSpeed);
        session.MobileUdpPeer = (!isCable && string.Equals(peer.Platform, "Android", StringComparison.Ordinal) && _udpHub != null && remoteAddress != null)
            ? _udpHub.AddAuxiliaryPeer(id, remoteAddress, "cellular")
            : null;
        session.MovePacer = isCable
            ? new FastMovePacer(
                canSendUdp: () => Volatile.Read(ref session.ActiveReverseLease) > 0,
                sendUdp: move =>
                {
                    QueueReliableReverseMoveForSession(session, move, countAsFallback: false);
                    return true;
                },
                queueReliableTcp: move => QueueReliableReverseMoveForSession(session, move, countAsFallback: false),
                sendWarm: null,
                queueTrailingSync: _ => { },
                nextSeq: session.NextReverseMoveSeq,
                enableTrailingSync: false)
            : new FastMovePacer(
                canSendUdp: () => _udpHub != null && ActiveMouseUdpPeer(session) is { Confirmed: true } &&
                    Volatile.Read(ref session.ActiveReverseLease) > 0,
                sendUdp: move =>
                {
                    var peerUdp = ActiveMouseUdpPeer(session);
                    int lease = Volatile.Read(ref session.ActiveReverseLease);
                    return SendReverseMoveUdp(session, peerUdp, lease, move);
                },
                queueReliableTcp: move => QueueReliableReverseMoveForSession(session, move, countAsFallback: true),
                sendWarm: () =>
                {
                    var peerUdp = ActiveMouseUdpPeer(session);
                    if (_udpHub != null && peerUdp is { Confirmed: true } && _sessions.ContainsKey(id))
                        _udpHub.Send(peerUdp, FastUdpOp.Warm, 0, 0, 0);
                },
                queueTrailingSync: move =>
                {
                    var peerUdp = ActiveMouseUdpPeer(session);
                    int lease = Volatile.Read(ref session.ActiveReverseLease);
                    bool udpDelivered = SendReverseMoveUdp(session, peerUdp, lease, move);
                    if (!udpDelivered || Volatile.Read(ref session.PendingFirstCursorAck) != 0)
                        QueueReliableReverseMoveForSession(session, move, countAsFallback: false);
                },
                nextSeq: session.NextReverseMoveSeq);
        UpdateSessionAdaptivePacing(session);
        if(!_sessions.TryAdd(id,session))
        {
            session.MovePacer?.Dispose();
            session.HighResTimer.Dispose();
            _udpHub?.RemovePeer(session.UdpPeer);
            _udpHub?.RemovePeer(session.MobileUdpPeer);
            throw new InvalidOperationException("Duplicate peer session ID");
        }
        bool replacedSelectedStaleSession = EvictStaleDuplicateTrustedWindowsSessions(session, isLoopbackRemote);
        PeerListChanged?.Invoke();
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(token);
        var sendTask=Task.Run(()=>ReverseSendLoopAsync(session,linked.Token));
        try
        {
            bool shouldSelect;
            lock(_authorityGate)
                shouldSelect = _selected==null || replacedSelectedStaleSession ||
                    (string.Equals(peer.Platform, "Windows", StringComparison.Ordinal) &&
                     !string.Equals(_selected.Peer.Platform, "Windows", StringComparison.Ordinal) &&
                     Volatile.Read(ref _selected.ActiveReverseLease) == 0) ||
                    (isCable && !_selected.IsCableSession && IsSameDevicePeer(_selected.Peer, peer)) ||
                    (!isCable && _selected.IsCableSession &&
                     Volatile.Read(ref _selected.CableDetachedOrUnresponsive) != 0 &&
                     IsSameDevicePeer(_selected.Peer, peer));
            if(shouldSelect) Select(session);
            string versionSuffix = string.IsNullOrWhiteSpace(peer.AppVersion)
                ? "" : " [v" + peer.AppVersion + "]";
            string transportSuffix = isCable ? " via data cable" : "";
            _status("Connected: "+peer.Name+versionSuffix+transportSuffix+" ("+ConnectedPeerCount+" simultaneous devices).");
            // The server advertises its CURRENT system name after successful
            // pairing. Do not rely on a stale saved alias on the other PC.
            await SendToAsync(session,new Packet("peer-name",
                Text:Environment.MachineName),token);
            if (initialBand == "ethernet")
            {
                await SendToAsync(session, new Packet("link-info", Text: initialBand, A: initialSpeed), token);
            }
            if (_udpHub != null && session.UdpPeer != null)
            {
                await SendToAsync(session, new Packet("udp-channel",
                    _udpHub.Port, session.Id, Text: session.UdpPeer.Base64Key), token);
            }
            if (string.Equals(peer.Platform, "Android", StringComparison.Ordinal))
            {
                string? endpointsJson = BuildEndpointsJson();
                if (!string.IsNullOrEmpty(endpointsJson))
                    await SendToAsync(session, new Packet("endpoints", Wire.Port, Text: endpointsJson), token);
                if (_udpHub != null && session.MobileUdpPeer != null)
                {
                    await SendToAsync(session, new Packet("mobile-udp-channel",
                        _udpHub.Port, session.MobileUdpPeer.WirePeerId, Text: session.MobileUdpPeer.Base64Key), token);
                }
            }
            if (peer.Platform == "Android")
            {
                (int Port, string Secret)? shellAuth;
                (string State, string Detail, int PairPort, int ConnectPort) bridgeSnapshot;
                lock (_shellAuthGate)
                {
                    shellAuth = _shellBridgeAuth;
                    bridgeSnapshot = _bridgeStatus;
                }
                if (shellAuth is { } auth)
                {
                    await SendToAsync(session,
                        new Packet("shell-input-bridge", auth.Port, Text: auth.Secret),
                        token);
                }
                if (!string.IsNullOrWhiteSpace(bridgeSnapshot.State))
                {
                    await SendToAsync(session,
                        new Packet("bridge-status", bridgeSnapshot.PairPort, bridgeSnapshot.ConnectPort,
                            Text: $"{bridgeSnapshot.State}|{bridgeSnapshot.Detail}"),
                        token);
                }
                await SendToAsync(session,
                    new Packet("keyboard-layout", Text: KeyboardLayoutHelper.DescribeCurrentWindowsLayout()),
                    token);
                if (!string.IsNullOrEmpty(_latestProfileStateJson))
                    await SendToAsync(session, new Packet("profile-state", Text: _latestProfileStateJson), token);
            }
            if (peer.Platform == "Windows")
            {
                string[] macs = SystemMediaPowerHelper.CollectLocalMacAddresses();
                if (macs.Length > 0)
                    await SendToAsync(session, new Packet("mac-addresses", Text: string.Join(",", macs)), token);
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
                            ComputeAdaptiveUdpIntervalMs(session.Peer.Platform, session.Peer.RefreshHz, 0, 0, 0, 0, session.LinkBand));
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
                    if (p.Type=="udp-ack")
                {
                    if (session.UdpPeer != null)
                    {
                        session.UdpPeer.Confirmed = true;
                        if (session.UdpPeer.TryMarkConfirmedLogged())
                            _status($"Low-latency input confirmed for {session.Peer.Name}.");
                    }
                    continue;
                }
                if (p.Type == "mobile-udp-punch")
                {
                    if (!session.IsCableSession && _udpHub != null && session.MobileUdpPeer != null &&
                        p.A is > 0 and <= 65535 && !string.IsNullOrWhiteSpace(p.Text))
                    {
                        var candidateEps = ParseMobileCandidateEndPoints(p.Text, p.A);
                        if (candidateEps.Length > 0)
                        {
                            var mobPeer = session.MobileUdpPeer;
                            var hub = _udpHub;
                            hub.ConfigurePeerEndPoints(mobPeer, candidateEps);
                            DiagnosticLog?.Invoke($"Started two-way mobile receiver punch for {session.Peer.Name} ({candidateEps[0]})");
                            hub.Send(mobPeer, FastUdpOp.Probe, hub.Port, 0, 0);
                            _ = Task.Run(async () =>
                            {
                                int[] delaysMs = [40, 80, 140, 220, 350, 550, 800];
                                foreach (int ms in delaysMs)
                                {
                                    if (token.IsCancellationRequested || mobPeer.Confirmed) return;
                                    try { await Task.Delay(ms, token); }
                                    catch (OperationCanceledException) { return; }
                                    if (!mobPeer.Confirmed)
                                        hub.Send(mobPeer, FastUdpOp.Probe, hub.Port, 0, 0);
                                }
                                if (!mobPeer.Confirmed && !token.IsCancellationRequested)
                                    DiagnosticLog?.Invoke($"Mobile UDP punch did not complete for {session.Peer.Name} (blocked by router or carrier); keeping Wi-Fi");
                            }, token);
                        }
                    }
                    continue;
                }
                if (p.Type == "mobile-udp-close")
                {
                    Volatile.Write(ref session.DesiredPreferMobileMouse, 0);
                    Volatile.Write(ref session.DesiredPreferMobileKeyboard, 0);
                    Volatile.Write(ref session.PreferMobileMouse, 0);
                    Volatile.Write(ref session.PreferMobileKeyboard, 0);
                    if (session.MobileUdpPeer != null)
                    {
                        session.MobileUdpPeer.Confirmed = false;
                        session.MobileUdpPeer.RemoteEndPoint = null;
                        session.MobileUdpPeer.SetCandidateEndPoints(null);
                    }
                    UpdateSessionAdaptivePacing(session);
                    continue;
                }
                if (p.Type == "stream-prefer")
                {
                    bool wantMobileMouse = p.A != 0 || string.Equals(p.Text, "cellular", StringComparison.OrdinalIgnoreCase);
                    bool wantMobileKb = p.B != 0;
                    ApplyStreamPreference(session, wantMobileMouse, wantMobileKb, p.C, p.D);
                    continue;
                }
                if(p.Type=="trust-request")
                {
                    // Run approval dialog off the read loop so pings continue answering.
                    _ = HandleTrustRequestAsync(session, p.A, p.Text, token);
                    continue;
                }
                if(p.Type is "trust-accepted" or "trust-denied")
                {
                    TaskCompletionSource<bool>? pendingOffer;
                    lock (_authorityGate)
                    {
                        pendingOffer = session.PendingTrustOffer;
                        if (p.Type == "trust-accepted" && !string.IsNullOrWhiteSpace(p.Text))
                            session.PendingOfferedTrustResponse = p.Text;
                    }
                    pendingOffer?.TrySetResult(p.Type == "trust-accepted");
                    continue;
                }
                if (p.Type == "mutual-trust-sync")
                {
                    // Auto-upgrade 1-way trust ONLY over an already-authenticated
                    // trusted-token connection ("auth-trusted") from a Windows peer.
                    if (session.TrustedTokenHash != null &&
                        _trusted != null &&
                        session.Peer.Platform == "Windows" &&
                        MutualTrustCodec.TryVerifyProposal(p.Text, Fingerprint, out var proposal) &&
                        proposal != null)
                    {
                        _trusted.TrustOutgoing(session.Peer.Name, proposal.PeerFingerprint, proposal.ReciprocalToken);
                        _trusted.AutoConnectEnabled = true;
                        _status($"Two-way trusted device upgraded: {session.Peer.Name}");
                        await SendToAsync(session, new Packet("mutual-trust-synced", Text: proposal.OutgoingToken), token);
                    }
                    continue;
                }
                if (p.Type == "hub-yield-invite")
                {
                    if (session.TrustedTokenHash != null &&
                        _trusted?.AutoConnectEnabled == true &&
                        !string.IsNullOrWhiteSpace(session.Peer.DeviceFingerprint) &&
                        _trusted.FindAutoConnect(session.Peer.DeviceFingerprint) != null &&
                        p.A >= ActiveEnvironmentDeviceCount)
                    {
                        HubYieldRequested?.Invoke(session.Peer.DeviceFingerprint, p.A);
                    }
                    continue;
                }
                if (p.Type == "sub-peers")
                {
                    SetSessionSubPeers(session, p.Text);
                    await PublishEnvironmentAsync(token);
                    continue;
                }
                if (p.Type == "sub-reply")
                {
                    await HandleSubReplyAsync(session, p.Text, token);
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
                    if (primaryMon.RefreshHz is >= 24 and <= 360)
                        Volatile.Write(ref session.LiveRefreshHz, primaryMon.RefreshHz.Value);
                    UpdateSessionAdaptivePacing(session);
                    await PublishEnvironmentAsync(token);
                    PeerListChanged?.Invoke();
                    if(ReferenceEquals(Volatile.Read(ref _selected),session))
                        PeerTopologyChanged?.Invoke(session.Peer);
                    continue;
                }
                if(p.Type=="display-metrics")
                {
                    if(p.A is >= 24 and <= 360)
                    {
                        Volatile.Write(ref session.LiveRefreshHz, p.A);
                        if (p.B > 0) Volatile.Write(ref session.LiveFrameTenthsMs, p.B);
                        session.Peer = session.Peer with { RefreshHz = p.A };
                        UpdateSessionAdaptivePacing(session);
                        RelayLease? upstream = null;
                        lock (_authorityGate)
                            _relayByTarget.TryGetValue(session.Id, out upstream);
                        if (upstream != null)
                        {
                            try { await SendToAsync(upstream.Source, new Packet("display-metrics", p.A, p.B), token); }
                            catch (Exception) { }
                        }
                    }
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
                    await DispatchReverseStateAsync(session, p.Type, p.A, token);
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
                        : "Android Accessibility turned off for payments or paused; phone stays connected.");
                    continue;
                }
                if(p.Type=="link-info")
                {
                    if(!session.IsCableSession)
                    {
                        string band = AdaptiveLinkTracker.NormalizeBand(p.Text);
                        int speedMbps = p.A > 0 ? p.A : 0;
                        int epoch = p.C > 0 ? p.C : 0;
                        session.LinkBand = band;
                        session.LinkSpeedMbps = speedMbps;
                        if (epoch > 0) session.NetworkEpoch = epoch;
                        bool changed = session.UdpPeer?.LinkTracker.OnLinkProfileChanged(band, speedMbps, epoch) ?? false;
                        UpdateSessionAdaptivePacing(session);
                        if (changed)
                        {
                            string desc = speedMbps > 0 ? $"{band} ({speedMbps} Mbps)" : band;
                            DiagnosticLog?.Invoke($"Peer link profile changed [{session.Peer.Name}]: {desc} rssi={p.B}dBm freq={p.D}MHz epoch={epoch}");
                        }
                    }
                    else if (p.A > 0)
                    {
                        session.LinkSpeedMbps = p.A;
                    }
                    continue;
                }
                if(p.Type=="notify-count")
                {
                    int nextCount = p.A is >= 0 and <= 9999 ? p.A : -1;
                    if (session.Peer.NotificationCount != nextCount)
                    {
                        session.Peer = session.Peer with { NotificationCount = nextCount };
                        await PublishEnvironmentAsync(token);
                        PeerListChanged?.Invoke();
                    }
                    continue;
                }
                if(p.Type=="keyboard-info")
                {
                    if (p.Text is { Length: > 1 and <= 8192 })
                    {
                        Volatile.Write(ref session.KeyboardInfoJson, p.Text);
                        PeerKeyboardInfoChanged?.Invoke();
                    }
                    continue;
                }
                if(p.Type=="environment-placement-update")
                {
                    if(p.Text is not { Length: > 1 and <= 512 })continue;
                    EnvironmentPosition? placement;
                    try { placement=JsonSerializer.Deserialize<EnvironmentPosition>(p.Text); }
                    catch(JsonException){continue;}
                    if(!EnvironmentPosition.Valid(placement) ||
                        !OwnsAnyPlacement(placement!.Key))continue;
                    ApplyEnvironmentPosition(placement!);
                    continue;
                }
                if(p.Type=="environment-select")
                {
                    // The hub owns routing decisions. Clients may request
                    // only connection IDs issued on this authenticated link.
                    Session? target=p.A==0 ? null :
                        _sessions.TryGetValue(p.A,out var targetPeer)?targetPeer:null;
                    BridgedSubPeer? bridgedTarget = null;
                    if (target == null && p.A >= 10000)
                    {
                        lock (_bridgedLock)
                            _bridgedByVirtualId.TryGetValue(p.A, out bridgedTarget);
                    }
                    bool valid = p.A == 0 ||
                        (target != null && !ReferenceEquals(target, session) &&
                         target.Peer.SupportsReverseInput) ||
                        (bridgedTarget != null && !ReferenceEquals(bridgedTarget.BridgeSession, session) &&
                         bridgedTarget.Descriptor.SupportsReverseInput);
                    if(!valid)
                    {
                        await SendToAsync(session,new Packet("environment-unavailable",p.A),token);
                        continue;
                    }
                    var previous=RemoveRelayFromSource(session.Id);
                    if(previous!=null)
                    {
                        var revokedRelay = previous;
                        _ = Task.Run(() => RevokeRelayAsync(revokedRelay, false, token));
                    }
                    (int VirtualTargetId, int Claim, Session Source, Session BridgeSession, int LocalSubId)? prevVirtual = null;
                    bool localOwner=false;
                    lock(_authorityGate)
                    {
                        if (_virtualRelayBySource.Remove(session.Id, out var vr))
                            prevVirtual = vr;
                        if(ReferenceEquals(_owner,session))
                        {
                            _owner=null;session.Lease=0;session.AppliedLease=0;
                            _priority.ReleaseRemote();localOwner=true;
                        }
                        session.EnvironmentTargetId=p.A;
                    }
                    if (prevVirtual is { } oldV)
                    {
                        string resetEnv = JsonSerializer.Serialize(new SubRouteEnvelope(oldV.LocalSubId, new Packet("reset")));
                        oldV.BridgeSession.ReverseQueue.Writer.TryWrite(new Packet("sub-route", oldV.VirtualTargetId, Text: resetEnv));
                    }
                    if(localOwner)lock(session.InputLock)session.Injector.ReleaseAll();
                    if(target?.UdpPeer is { Confirmed: true } targetUdp)
                        _udpHub?.Send(targetUdp, FastUdpOp.Warm, 0, 0, 0);
                    await SendUrgentAsync(session,new Packet("environment-selected",p.A),token);
                    if (session.Peer.Platform == "Android")
                    {
                        int selectedTarget = p.A;
                        lock (session.TaskbarGate)
                        {
                            session.LastSentTaskbarTarget = -1;
                            session.LastSentTaskbarSig = "";
                        }
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                // Let the follow-up claim/granted/active handshake complete first with zero head-of-line delay.
                                await Task.Delay(35, token);
                                if (session.EnvironmentTargetId != selectedTarget) return;
                                if (selectedTarget == 0)
                                {
                                    var cap = WindowsTaskbarHelper.CaptureSnapshot(0);
                                    await DeliverTaskbarToAndroidAsync(
                                        session, 0, cap.StateJson, cap.Signature, cap.IconsByHash, token);
                                }
                                else if (selectedTarget > 0)
                                {
                                    if (_sessions.TryGetValue(selectedTarget, out var watchPeer) &&
                                        watchPeer.Peer.Platform == "Windows" && !watchPeer.TaskbarWatchSent)
                                    {
                                        watchPeer.TaskbarWatchSent = true;
                                        try { await SendToAsync(watchPeer, new Packet("taskbar-watch", 1), token); }
                                        catch (Exception) { }
                                    }
                                    string? cachedJson = null;
                                    string cachedSig = "";
                                    Dictionary<string, string>? cachedIcons = null;
                                    lock (_taskbarGate)
                                    {
                                        if (_peerTaskbars.TryGetValue(selectedTarget, out var peerCache) &&
                                            !string.IsNullOrEmpty(peerCache.StateJson))
                                        {
                                            cachedJson = peerCache.StateJson;
                                            cachedSig = peerCache.Signature;
                                            cachedIcons = new Dictionary<string, string>(peerCache.Icons, StringComparer.Ordinal);
                                        }
                                    }
                                    if (!string.IsNullOrEmpty(cachedJson))
                                    {
                                        await DeliverTaskbarToAndroidAsync(
                                            session, selectedTarget, cachedJson, cachedSig, cachedIcons, token);
                                    }
                                }
                            }
                            catch (Exception) { }
                        }, token);
                    }
                    continue;
                }
                if (p.Type == "claim" && session.EnvironmentTargetId >= 10000)
                {
                    BridgedSubPeer? bTarget = null;
                    lock (_bridgedLock)
                        _bridgedByVirtualId.TryGetValue(session.EnvironmentTargetId, out bTarget);
                    bool queued = false;
                    lock (_authorityGate)
                    {
                        if (bTarget != null && p.A > 0 &&
                            AllowsForwardInput(session.Peer) &&
                            bTarget.Descriptor.SupportsReverseInput &&
                            !ReferenceEquals(bTarget.BridgeSession, session) &&
                            !_relayBySource.ContainsKey(session.Id) &&
                            !_relayByTarget.ContainsKey(session.Id) &&
                            !_virtualRelayBySource.ContainsKey(session.Id) &&
                            !_virtualRelayBySource.Values.Any(v => v.VirtualTargetId == bTarget.VirtualId) &&
                            !_virtualHubClaims.ContainsKey(bTarget.VirtualId) &&
                            !ReferenceEquals(_owner, session))
                        {
                            _virtualRelayBySource[session.Id] = (bTarget.VirtualId, p.A, session, bTarget.BridgeSession, bTarget.Descriptor.LocalId);
                            string env = JsonSerializer.Serialize(new SubRouteEnvelope(bTarget.Descriptor.LocalId, p));
                            queued = bTarget.BridgeSession.ReverseQueue.Writer.TryWrite(
                                new Packet("sub-route", bTarget.VirtualId, Text: env));
                            if (!queued)
                                _virtualRelayBySource.Remove(session.Id);
                        }
                    }
                    if (!queued)
                        await SendToAsync(session, new Packet("busy", p.A), token);
                    continue;
                }
                if(p.Type=="claim" && session.EnvironmentTargetId!=0)
                {
                    if (session.Peer.Platform == "Android" && _priority.CanApplyRemote)
                    {
                        ReleaseIdleOutboundForPhoneClaim?.Invoke();
                    }
                    RelayLease? relay=null;
                    lock(_authorityGate)
                    {
                        if (_sessions.TryGetValue(session.EnvironmentTargetId, out var destCheck) &&
                            session.Peer.Platform == "Android" &&
                            _priority.CanApplyRemote &&
                            _hubClaims.Remove(destCheck.Id))
                        {
                            Volatile.Write(ref destCheck.ActiveReverseLease, 0);
                            destCheck.MovePacer?.SetWarmActive(false);
                            destCheck.MovePacer?.ClearPending();
                        }
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
                            Volatile.Write(ref destination.PendingFirstCursorAck, 1);
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
                            ComputeAdaptiveUdpIntervalMs(
                                relay.Target.Peer.Platform,
                                Volatile.Read(ref relay.Target.LiveRefreshHz) is >= 24 and var liveHz
                                    ? liveHz
                                    : relay.Target.Peer.RefreshHz,
                                relay.Target.UdpPeer?.LinkTracker.SmoothedRttMs ?? 0,
                                relay.Target.UdpPeer?.LinkTracker.MinRttMs ?? 0,
                                relay.Target.UdpPeer?.LinkTracker.EffectiveJitterMs ?? 0,
                                relay.Target.UdpPeer?.LinkTracker.CombinedLossPermille ?? 0));
                        int dispatchSeq = relay.Target.NextReverseClaimDispatchSeq();
                        if (relay.Target.UdpPeer is { HasEndpoint: true } relayUdp && _udpHub != null)
                        {
                            if (relayUdp.Confirmed)
                            {
                                _udpHub.Send(relayUdp, FastUdpOp.ReverseClaim, p.A, 0, p.A, dispatchSeq);
                                ScheduleReverseClaimUdpRetry(relay.Target, p.A, dispatchSeq);
                            }
                            else
                            {
                                _udpHub.Send(relayUdp, FastUdpOp.Warm, 0, 0, 0);
                            }
                        }
                        try { await SendToAsync(relay.Target,
                            new Packet("reverse-claim", p.A, C: dispatchSeq), token); }
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
                    if (session.Peer.Platform == "Android" && _priority.CanApplyRemote &&
                        !(AllowForwardClaim?.Invoke() ?? true))
                    {
                        ReleaseIdleOutboundForPhoneClaim?.Invoke();
                    }
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
                            session.ResetForwardDedup();
                            granted=true;
                        }
                    }
                    if(granted && !_sessions.ContainsKey(session.Id))
                    {
                        lock(_authorityGate)
                        {
                            if(ReferenceEquals(_owner,session))
                            { _owner=null;session.Lease=0;_priority.ReleaseRemote(); }
                        }
                        granted=false;
                    }
                    await SendUrgentAsync(session,new Packet(granted?"granted":"busy",p.A),token);
                    continue;
                }
                if(p.Type=="reset" || p.Type=="release")
                {
                    (int VirtualTargetId, int Claim, Session Source, Session BridgeSession, int LocalSubId)? activeVirtual = null;
                    RelayLease? activeRelay=null;
                    lock(_authorityGate)
                    {
                        if (_virtualRelayBySource.TryGetValue(session.Id, out var vCandidate) &&
                            (p.Type == "reset" || p.A == vCandidate.Claim))
                        {
                            _virtualRelayBySource.Remove(session.Id);
                            activeVirtual = vCandidate;
                        }
                        else if(_relayBySource.TryGetValue(session.Id,out var candidate) &&
                            (p.Type=="reset" || p.A==candidate.Claim))
                            activeRelay=candidate;
                    }
                    if (activeVirtual is { } vRel)
                    {
                        string env = JsonSerializer.Serialize(new SubRouteEnvelope(vRel.LocalSubId, p));
                        vRel.BridgeSession.ReverseQueue.Writer.TryWrite(
                            new Packet("sub-route", vRel.VirtualTargetId, Text: env));
                        continue;
                    }
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
                    session.LastPongTicks = Stopwatch.GetTimestamp();
                    await SendUrgentAsync(session,new Packet("pong",p.A,p.B),token);
                    continue;
                }
                if(p.Type=="pong")
                {
                    session.LastPongTicks = Stopwatch.GetTimestamp();
                    if (session.IsCableSession)
                    {
                        bool wasDetached = Interlocked.Exchange(ref session.CableDetachedOrUnresponsive, 0) != 0;
                        if (session.CableTracker.OnFeedbackReceived(p.A, -1, -1, Environment.TickCount64))
                        {
                            Volatile.Write(ref session.TcpRttMs, session.CableTracker.SmoothedRttMs);
                            UpdateSessionAdaptivePacing(session);
                        }
                        if (wasDetached && !ReferenceEquals(Volatile.Read(ref _selected), session))
                            Select(session);
                    }
                    continue;
                }
                if(p.Type=="warm")
                {
                    BroadcastWarmToConfirmedTargets(session.Id);
                    continue;
                }
                if (p.Type == "taskbar-icon" && session.Peer.Platform == "Windows" &&
                    p.Text is { Length: > 10 and <= 7800 } tbIconPayload)
                {
                    int sep = tbIconPayload.IndexOf('|');
                    if (sep is >= 4 and <= 16 && sep + 1 < tbIconPayload.Length)
                    {
                        string hash = tbIconPayload[..sep];
                        string b64 = tbIconPayload[(sep + 1)..];
                        lock (_taskbarGate)
                        {
                            if (!_peerTaskbars.TryGetValue(session.Id, out var cache))
                            {
                                cache = new PeerTaskbarCache();
                                _peerTaskbars[session.Id] = cache;
                            }
                            if (cache.Icons.Count > 64) cache.Icons.Clear();
                            cache.Icons[hash] = b64;
                        }
                        foreach (var android in _sessions.Values)
                        {
                            if (android.Peer.Platform != "Android" || android.EnvironmentTargetId != session.Id)
                                continue;
                            bool shouldSend;
                            lock (android.TaskbarGate)
                            {
                                if (android.SentTaskbarIcons.Count > 128) android.SentTaskbarIcons.Clear();
                                shouldSend = android.SentTaskbarIcons.Add(hash);
                            }
                            if (shouldSend)
                            {
                                try { await SendToAsync(android, new Packet("taskbar-icon", session.Id, Text: tbIconPayload), token); }
                                catch (Exception) { }
                            }
                        }
                    }
                    continue;
                }
                if (p.Type == "taskbar-state" && session.Peer.Platform == "Windows" &&
                    p.Text is { Length: > 2 and <= 7800 } tbStateJson)
                {
                    string sig = tbStateJson;
                    Dictionary<string, string>? iconsCopy = null;
                    lock (_taskbarGate)
                    {
                        if (!_peerTaskbars.TryGetValue(session.Id, out var cache))
                        {
                            cache = new PeerTaskbarCache();
                            _peerTaskbars[session.Id] = cache;
                        }
                        cache.StateJson = tbStateJson;
                        cache.Signature = sig;
                        if (cache.Icons.Count > 0)
                            iconsCopy = new Dictionary<string, string>(cache.Icons, StringComparer.Ordinal);
                    }
                    foreach (var android in _sessions.Values)
                    {
                        if (android.Peer.Platform == "Android" && android.EnvironmentTargetId == session.Id)
                        {
                            await DeliverTaskbarToAndroidAsync(
                                android, session.Id, tbStateJson, sig, iconsCopy, token);
                        }
                    }
                    continue;
                }
                if (p.Type == "taskbar-need-icons" && session.Peer.Platform == "Android")
                {
                    int reqTarget = session.EnvironmentTargetId;
                    lock (session.TaskbarGate)
                    {
                        session.SentTaskbarIcons.Clear();
                        session.LastSentTaskbarTarget = -1;
                        session.LastSentTaskbarSig = "";
                    }
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (reqTarget == 0)
                            {
                                var cap = WindowsTaskbarHelper.CaptureSnapshot(0);
                                await DeliverTaskbarToAndroidAsync(
                                    session, 0, cap.StateJson, cap.Signature, cap.IconsByHash, token);
                            }
                            else if (reqTarget > 0)
                            {
                                if (_sessions.TryGetValue(reqTarget, out var watchPeer) &&
                                    watchPeer.Peer.Platform == "Windows")
                                {
                                    watchPeer.TaskbarWatchSent = true;
                                    try { await SendToAsync(watchPeer, new Packet("taskbar-watch", 2), token); }
                                    catch (Exception) { }
                                }
                                string? cachedJson = null;
                                string cachedSig = "";
                                Dictionary<string, string>? cachedIcons = null;
                                lock (_taskbarGate)
                                {
                                    if (_peerTaskbars.TryGetValue(reqTarget, out var peerCache) &&
                                        !string.IsNullOrEmpty(peerCache.StateJson))
                                    {
                                        cachedJson = peerCache.StateJson;
                                        cachedSig = peerCache.Signature;
                                        cachedIcons = new Dictionary<string, string>(peerCache.Icons, StringComparer.Ordinal);
                                    }
                                }
                                if (!string.IsNullOrEmpty(cachedJson))
                                {
                                    await DeliverTaskbarToAndroidAsync(
                                        session, reqTarget, cachedJson, cachedSig, cachedIcons, token);
                                }
                            }
                        }
                        catch (Exception) { }
                    }, token);
                    continue;
                }
                if (p.Type == "taskbar-action" && session.Peer.Platform == "Android" &&
                    AllowsForwardInput(session.Peer) &&
                    p.A == session.EnvironmentTargetId &&
                    WindowsTaskbarHelper.IsValidActionKeyFormat(p.Text))
                {
                    string actionKey = p.Text!;
                    if (p.A == 0)
                    {
                        _ = Task.Run(() =>
                        {
                            WindowsTaskbarHelper.ExecuteAction(actionKey);
                            Thread.Sleep(180);
                            Interlocked.Exchange(ref _localTaskbarNudge, 1);
                        });
                    }
                    else if (_sessions.TryGetValue(p.A, out var targetWin) &&
                             targetWin.Peer.Platform == "Windows")
                    {
                        try { await SendToAsync(targetWin, new Packet("taskbar-action", Text: actionKey), token); }
                        catch (Exception) { }
                    }
                    continue;
                }
                if (p.Type == "adb-bridge-hint" && session.Peer.Platform == "Android" &&
                    p.Text is { Length: > 1 and <= 2048 })
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(p.Text);
                        var root = doc.RootElement;
                        bool usb = root.TryGetProperty("usb", out var u) && u.ValueKind == JsonValueKind.True;
                        bool wifi = root.TryGetProperty("wifi", out var w) && w.ValueKind == JsonValueKind.True;
                        int cPort = root.TryGetProperty("connectPort", out var cp) && cp.TryGetInt32(out int cpVal) ? cpVal : 0;
                        int pPort = root.TryGetProperty("pairPort", out var pp) && pp.TryGetInt32(out int ppVal) ? ppVal : 0;
                        bool allowed = !root.TryGetProperty("allowed", out var al) || al.ValueKind != JsonValueKind.False;
                        bool ready = root.TryGetProperty("ready", out var rd) && rd.ValueKind == JsonValueKind.True;
                        string? remoteIp = AdbCableBridge.NormalizeIpv4Host(session.RemoteAddress);
                        AndroidBridgeHintReceived?.Invoke(remoteIp, usb, wifi, cPort, pPort, allowed, ready);
                        if (allowed && !ready)
                        {
                            (int Port, string Secret)? auth;
                            lock (_shellAuthGate) auth = _shellBridgeAuth;
                            if (auth is { } existingAuth)
                            {
                                await SendToAsync(session,
                                    new Packet("shell-input-bridge", existingAuth.Port, Text: existingAuth.Secret),
                                    token);
                            }
                        }
                    }
                    catch (JsonException) { }
                    continue;
                }
                if (p.Type == "request-shell-bridge" && session.Peer.Platform == "Android")
                {
                    ShellBridgeRefreshRequested?.Invoke();
                    (int Port, string Secret)? auth;
                    (string State, string Detail, int PairPort, int ConnectPort) bStat;
                    lock (_shellAuthGate)
                    {
                        auth = _shellBridgeAuth;
                        bStat = _bridgeStatus;
                    }
                    if (auth is { } existingAuth)
                    {
                        await SendToAsync(session,
                            new Packet("shell-input-bridge", existingAuth.Port, Text: existingAuth.Secret),
                            token);
                    }
                    if (!string.IsNullOrWhiteSpace(bStat.State))
                    {
                        await SendToAsync(session,
                            new Packet("bridge-status", bStat.PairPort, bStat.ConnectPort,
                                Text: $"{bStat.State}|{bStat.Detail}"),
                            token);
                    }
                    continue;
                }
                if (p.Type == "adb-pair-request" && session.Peer.Platform == "Android")
                {
                    string? remoteIp = AdbCableBridge.NormalizeIpv4Host(session.RemoteAddress);
                    int pairPortArg = p.A;
                    int connectPortArg = p.B;
                    string codeArg = p.Text ?? "";
                    var pairHandler = WirelessAdbPairRequested;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            (bool ok, string msg) = pairHandler != null
                                ? await pairHandler(remoteIp, pairPortArg, codeArg, connectPortArg)
                                : (false, "Wireless ADB bridge helper is not initialized yet on Windows.");
                            await SendToAsync(session, new Packet("adb-pair-result", ok ? 1 : 0, Text: msg), token);
                        }
                        catch (Exception ex)
                        {
                            try
                            {
                                await SendToAsync(session, new Packet("adb-pair-result", 0, Text: ex.Message), token);
                            }
                            catch { }
                        }
                    }, token);
                    continue;
                }
                if (p.Type == "device-battery")
                {
                    int pct = p.A is >= -1 and <= 100 ? p.A : -1;
                    bool charging = p.B != 0;
                    string sig = (p.Text ?? "").Trim();
                    session.Peer = session.Peer with { BatteryPercent = pct, IsCharging = charging, SignalLabel = sig };
                    if (pct is >= 1 and <= 15 && !charging &&
                        Interlocked.CompareExchange(ref session.LowBatteryAlertSent, 1, 0) == 0)
                    {
                        LowBatteryDetected?.Invoke(session.Peer.Name, pct, charging);
                    }
                    else if (pct > 20 || charging)
                    {
                        Interlocked.Exchange(ref session.LowBatteryAlertSent, 0);
                    }
                    await PublishEnvironmentAsync(token);
                    PeerListChanged?.Invoke();
                    continue;
                }
                if (p.Type == "mac-addresses" && !string.IsNullOrWhiteSpace(session.Peer.DeviceFingerprint) &&
                    !string.IsNullOrWhiteSpace(p.Text))
                {
                    var parsedMacs = p.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(m => m.Length <= 20).Take(4).ToArray();
                    if (parsedMacs.Length > 0)
                        _macsByFingerprint[session.Peer.DeviceFingerprint!] = parsedMacs;
                    continue;
                }
                if (p.Type == "clipboard-text" && p.Text is { Length: > 2 and <= 7800 } cbJson)
                {
                    var parsedCb = SharedClipboardHelper.ParseClipboardTextPacket(cbJson);
                    if (parsedCb is { } cb)
                    {
                        ClipboardTextReceived?.Invoke(cb.Hash, cb.Text);
                        BroadcastClipboardTextAsync(cb.Hash, cb.Text, excludeSessionId: session.Id);
                    }
                    continue;
                }
                if (p.Type == "clipboard-img-chunk" && p.Text is { Length: > 2 and <= 7800 })
                {
                    var imgDone = _fileDrop.AcceptClipboardImgChunk(p.A, p.B, p.C, p.Text);
                    if (imgDone is { } img)
                        ClipboardImageReceived?.Invoke(img.Hash, img.Png);
                    foreach (var other in _sessions.Values)
                    {
                        if (other.Id == session.Id) continue;
                        try { await SendToAsync(other, p, token); } catch { }
                    }
                    continue;
                }
                if (p.Type == "file-offer")
                {
                    int targetRoute = p.C;
                    if (targetRoute > 0 && _sessions.TryGetValue(targetRoute, out var fwdTarget))
                    {
                        try { await SendToAsync(fwdTarget, p with { D = session.Id }, token); } catch { }
                    }
                    else
                    {
                        if (!_fileDrop.BeginInbound(session.Id, p.A, p.B, p.Text, out string? err))
                        {
                            try { await SendToAsync(session, new Packet("file-reject", p.A, Text: err ?? "Rejected"), token); }
                            catch { }
                        }
                    }
                    continue;
                }
                if (p.Type == "file-chunk")
                {
                    int targetRoute = p.C;
                    if (targetRoute > 0 && _sessions.TryGetValue(targetRoute, out var fwdTarget))
                    {
                        try { await SendToAsync(fwdTarget, p with { D = session.Id }, token); } catch { }
                    }
                    else
                    {
                        _ = _fileDrop.AppendInboundChunk(session.Id, p.A, p.Text);
                    }
                    continue;
                }
                if (p.Type == "file-done")
                {
                    int targetRoute = p.C;
                    if (targetRoute > 0 && _sessions.TryGetValue(targetRoute, out var fwdTarget))
                    {
                        try { await SendToAsync(fwdTarget, p with { D = session.Id }, token); } catch { }
                    }
                    else
                    {
                        string? saved = _fileDrop.CompleteInbound(session.Id, p.A);
                        if (!string.IsNullOrEmpty(saved))
                        {
                            _status($"Received file from {session.Peer.Name}: {Path.GetFileName(saved)}");
                            FileReceived?.Invoke(session.Peer.Name, saved);
                        }
                    }
                    continue;
                }
                if (p.Type == "media-action" && AllowsForwardInput(session.Peer) &&
                    SystemMediaPowerHelper.IsValidMediaAction(p.Text))
                {
                    int targetRoute = p.A == 0 && session.EnvironmentTargetId > 0 ? session.EnvironmentTargetId : p.A;
                    if (targetRoute <= 0)
                        _ = Task.Run(() => SystemMediaPowerHelper.ExecuteMediaAction(p.Text, p.B));
                    else if (_sessions.TryGetValue(targetRoute, out var mTarget))
                        try { await SendToAsync(mTarget, new Packet("media-action", 0, p.B, 0, 0, p.Text), token); } catch { }
                    continue;
                }
                if (p.Type == "power-action" && AllowsForwardInput(session.Peer) &&
                    SystemMediaPowerHelper.IsValidPowerAction(p.Text))
                {
                    if (string.Equals(p.Text, "lock-all", StringComparison.Ordinal))
                    {
                        SendPowerAction(0, "lock-all");
                    }
                    else
                    {
                        int targetRoute = p.A == 0 && session.EnvironmentTargetId > 0 ? session.EnvironmentTargetId : p.A;
                        SendPowerAction(targetRoute, p.Text!);
                    }
                    continue;
                }
                if (p.Type == "screen-preview-watch" && session.Peer.Platform == "Android")
                {
                    int targetRoute = p.A;
                    int mon = p.B;
                    int highRes = p.C != 0 ? 1 : 0;
                    int prevTarget = Volatile.Read(ref session.ScreenPreviewTarget);
                    if (prevTarget > 0 && prevTarget != targetRoute && _sessions.TryGetValue(prevTarget, out var oldWin))
                    {
                        try { await SendToAsync(oldWin, new Packet("screen-preview-watch", 0, -1, 0), token); } catch { }
                    }
                    Volatile.Write(ref session.ScreenPreviewTarget, targetRoute);
                    Volatile.Write(ref session.ScreenPreviewMonitor, mon);
                    Volatile.Write(ref session.ScreenPreviewHighRes, highRes);
                    if (targetRoute == 0 && mon >= 0 && _previewWake.CurrentCount == 0)
                    {
                        try { _previewWake.Release(); } catch { }
                    }
                    if (targetRoute > 0 && _sessions.TryGetValue(targetRoute, out var newWin))
                    {
                        try { await SendToAsync(newWin, new Packet("screen-preview-watch", 0, mon, highRes), token); } catch { }
                    }
                    continue;
                }
                if (p.Type == "screen-preview-chunk" && session.Peer.Platform == "Windows")
                {
                    foreach (var android in _sessions.Values)
                    {
                        if (android.Peer.Platform == "Android" &&
                            Volatile.Read(ref android.ScreenPreviewTarget) == session.Id &&
                            Volatile.Read(ref android.ScreenPreviewMonitor) >= 0 &&
                            Volatile.Read(ref android.UrgentControlWaiters) == 0)
                        {
                            try { await SendToAsync(android, p, token); } catch { }
                        }
                    }
                    continue;
                }
                if (p.Type == "foreground-app" && session.Peer.Platform == "Windows" && !string.IsNullOrWhiteSpace(p.Text))
                {
                    foreach (var android in _sessions.Values)
                    {
                        if (android.Peer.Platform == "Android" && android.EnvironmentTargetId == session.Id)
                        {
                            try { await SendToAsync(android, new Packet("foreground-app", session.Id, Text: p.Text), token); } catch { }
                        }
                    }
                    continue;
                }
                if (p.Type == "notif-preview-state" && p.Text is { Length: >= 2 and <= 7800 })
                {
                    var list = DeviceTelemetryAndNotifModel.ParseNotificationList(p.Text);
                    lock (_notifGate)
                        _peerNotifications[session.Id] = list;
                    PeerNotificationsUpdated?.Invoke(session.Id, list);
                    if (session.Peer.Platform == "Android")
                    {
                        foreach (var winPeer in _sessions.Values)
                        {
                            if (winPeer.Peer.Platform == "Windows")
                                try { await SendToAsync(winPeer, p with { A = session.Id }, token); } catch { }
                        }
                    }
                    continue;
                }
                if (p.Type == "notif-dismiss" && !string.IsNullOrWhiteSpace(p.Text))
                {
                    if (p.A == 0)
                        WindowsNotificationCounter.DismissLocalNotification(p.Text);
                    else if (_sessions.TryGetValue(p.A, out var nTarget))
                        try { await SendToAsync(nTarget, p, token); } catch { }
                    continue;
                }
                if (p.Type == "notif-reply" && !string.IsNullOrWhiteSpace(p.Text))
                {
                    if (_sessions.TryGetValue(p.A, out var nTarget))
                        try { await SendToAsync(nTarget, p, token); } catch { }
                    continue;
                }
                if (p.Type == "otp-offer" && !string.IsNullOrWhiteSpace(p.Text))
                {
                    try
                    {
                        var offer = JsonSerializer.Deserialize<OtpOfferPayload>(p.Text);
                        if (offer != null && !string.IsNullOrWhiteSpace(offer.Code))
                            OtpOfferReceived?.Invoke(offer.App, offer.Code, offer.Title);
                    }
                    catch { }
                    continue;
                }
                if (p.Type == "profile-apply" && !string.IsNullOrWhiteSpace(p.Text))
                {
                    RemoteProfileApplyRequested?.Invoke(p.Text.Trim());
                    continue;
                }
                if (p.Type == "phone-screen-status" && session.Peer.Platform == "Android")
                {
                    PhoneScreenStatusReceived?.Invoke(session.Id, p.A, p.Text);
                    continue;
                }
                if (p.Type == "phone-screen-chunk" && session.Peer.Platform == "Android" && !string.IsNullOrEmpty(p.Text))
                {
                    var assembled = _phoneScreenAssembler.AddChunk((session.Id << 16) ^ (p.A & 0xFFFF), p.B, p.C, p.Text);
                    if (assembled.Complete && assembled.Data.Length > 0)
                        PhoneScreenFrameReceived?.Invoke(session.Id, assembled.Data);
                    continue;
                }
                if (p.Type == "phone-camera-watch" && session.Peer.Platform == "Windows")
                {
                    int targetRoute = int.TryParse(p.Text, out int tid) ? tid : 0;
                    if (targetRoute != session.Id)
                    {
                        bool camWanted = p.A != 0;
                        int lens = p.B;
                        bool torch = p.C != 0;
                        bool micWanted = p.D != 0;
                        int oldCam = Interlocked.Exchange(ref session.WatchingCameraTargetId, camWanted ? targetRoute : -1);
                        session.WatchingCameraLens = lens;
                        session.WatchingCameraTorch = torch;
                        int oldMic = -1;
                        if (micWanted)
                            oldMic = Interlocked.Exchange(ref session.WatchingMicTargetId, targetRoute);
                        else if (Volatile.Read(ref session.WatchingMicTargetId) == targetRoute)
                            oldMic = Interlocked.Exchange(ref session.WatchingMicTargetId, -1);

                        if (oldCam >= 0 && oldCam != targetRoute)
                        {
                            if (oldCam == 0) SyncHubLocalMediaCapture(0);
                            else DispatchTargetMediaWatch(oldCam);
                        }
                        if (oldMic >= 0 && oldMic != targetRoute && oldMic != oldCam)
                        {
                            if (oldMic == 0) SyncHubLocalMediaCapture(0);
                            else DispatchTargetMicWatch(oldMic);
                        }

                        if (targetRoute <= 0)
                            SyncHubLocalMediaCapture(lens);
                        else
                            DispatchTargetMediaWatch(targetRoute);
                    }
                    continue;
                }
                if (p.Type == "phone-mic-watch" && session.Peer.Platform == "Windows")
                {
                    int targetRoute = p.B > 0 ? p.B : 0;
                    if (targetRoute != session.Id)
                    {
                        bool micWanted = p.A != 0;
                        int oldMic = Interlocked.Exchange(ref session.WatchingMicTargetId, micWanted ? targetRoute : -1);
                        if (oldMic >= 0 && oldMic != targetRoute)
                        {
                            if (oldMic == 0) SyncHubLocalMediaCapture(0);
                            else DispatchTargetMicWatch(oldMic);
                        }
                        if (targetRoute <= 0)
                            SyncHubLocalMediaCapture(0);
                        else
                            DispatchTargetMicWatch(targetRoute);
                    }
                    continue;
                }
                if (p.Type == "phone-camera-status" && session.Peer.Platform is ("Android" or "Windows"))
                {
                    PhoneCameraStatusReceived?.Invoke(session.Id, p.A, p.Text);
                    foreach (var w in _sessions.Values)
                    {
                        if (Volatile.Read(ref w.WatchingCameraTargetId) == session.Id)
                            try { await SendToAsync(w, new Packet("phone-camera-status", p.A, session.Id, 0, 0, p.Text), token); } catch { }
                    }
                    continue;
                }
                if (p.Type == "phone-mic-status" && session.Peer.Platform is ("Android" or "Windows"))
                {
                    PhoneMicStatusReceived?.Invoke(session.Id, p.A, p.Text);
                    foreach (var w in _sessions.Values)
                    {
                        if (Volatile.Read(ref w.WatchingMicTargetId) == session.Id)
                            try { await SendToAsync(w, new Packet("phone-mic-status", p.A, session.Id, 0, 0, p.Text), token); } catch { }
                    }
                    continue;
                }
                if (p.Type == "phone-camera-chunk" && session.Peer.Platform is ("Android" or "Windows") && !string.IsNullOrEmpty(p.Text))
                {
                    var assembled = _phoneCameraAssembler.AddChunk((session.Id << 16) ^ (p.A & 0xFFFF), p.B, p.C, p.Text);
                    if (assembled.Complete && assembled.Data.Length > 0)
                        PhoneCameraFrameReceived?.Invoke(session.Id, assembled.Data);
                    var hub = _udpHub;
                    foreach (var w in _sessions.Values)
                    {
                        if (Volatile.Read(ref w.WatchingCameraTargetId) != session.Id) continue;
                        if (hub != null && ActiveMediaUdpPeer(w) is { } wUdp)
                        {
                            if (assembled.Complete && assembled.Data.Length > 0)
                                SendUdpMediaChunks(hub, wUdp, FastUdpMediaKind.CameraFrameChunk, p.A > 0 ? p.A : 1, assembled.Data, session.Id);
                        }
                        else
                        {
                            try { await SendToAsync(w, p with { D = session.Id }, token); } catch { }
                        }
                    }
                    continue;
                }
                if (p.Type == "phone-mic-audio" && session.Peer.Platform is ("Android" or "Windows") && !string.IsNullOrEmpty(p.Text))
                {
                    try
                    {
                        byte[] pcm = Convert.FromBase64String(p.Text);
                        int sr = p.B > 0 ? p.B : 16000;
                        if (pcm.Length > 0)
                        {
                            PhoneMicAudioReceived?.Invoke(session.Id, pcm, sr, p.A);
                            var hub = _udpHub;
                            foreach (var w in _sessions.Values)
                            {
                                if (Volatile.Read(ref w.WatchingMicTargetId) != session.Id) continue;
                                if (!SendDualOrBestMicUdp(hub, w, p.A, sr, session.Id, pcm))
                                    try { await SendToAsync(w, p with { D = session.Id }, token); } catch { }
                            }
                        }
                    }
                    catch { }
                    continue;
                }
                (int VirtualTargetId, int Claim, Session Source, Session BridgeSession, int LocalSubId)? virtualForward = null;
                RelayLease? forward=null;
                lock(_authorityGate)
                {
                    if (!_virtualRelayBySource.TryGetValue(session.Id, out var vFwd))
                        _relayBySource.TryGetValue(session.Id,out forward);
                    else
                        virtualForward = vFwd;
                }
                if (virtualForward is { } vForward)
                {
                    if (p.Type is ("move" or "button" or "key" or "wheel" or "text"))
                    {
                        string env = JsonSerializer.Serialize(new SubRouteEnvelope(vForward.LocalSubId, p));
                        vForward.BridgeSession.ReverseQueue.Writer.TryWrite(
                            new Packet("sub-route", vForward.VirtualTargetId, Text: env));
                    }
                    continue;
                }
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
                    if (p.Type is "button" or "wheel" &&
                        !session.TryAcceptForwardAction(p.C, p.Type == "button"))
                        continue;
                    if (p.Type == "key" &&
                        int.TryParse(p.Text, System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out int fwdKeySeq) &&
                        !session.TryAcceptForwardKeyAction(fwdKeySeq))
                        continue;
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
            ClearSessionSubPeers(session);
            _fileDrop.CancelAllForPeer(id);
            lock (_notifGate)
                _peerNotifications.Remove(id);
            lock (_taskbarGate)
                _peerTaskbars.Remove(id);
            session.ReverseQueue.Writer.TryComplete();
            _sessions.TryRemove(id,out _);
            lock(_authorityGate)
            {
                _hubClaims.Remove(id);
                if (_virtualRelayBySource.Remove(id, out var closedVRelay))
                {
                    string env = JsonSerializer.Serialize(new SubRouteEnvelope(closedVRelay.LocalSubId, new Packet("reset")));
                    closedVRelay.BridgeSession.ReverseQueue.Writer.TryWrite(
                        new Packet("sub-route", closedVRelay.VirtualTargetId, Text: env));
                }
                Volatile.Write(ref session.ActiveReverseLease, 0);
                Volatile.Write(ref session.PendingFirstCursorAck, 0);
                Volatile.Write(ref session.BridgedClaimLease, 0);
                session.BridgedReplyAsync = null;
            }
            session.MovePacer?.Dispose();
            session.HighResTimer.Dispose();
            _udpHub?.RemovePeer(session.UdpPeer);
            _udpHub?.RemovePeer(session.MobileUdpPeer);
            _fastMediaAssembler.ClearSource(id);
            bool notifyDisconnectedCam = false;
            bool notifyDisconnectedMic = false;
            lock (_mediaWatchGate)
            {
                if (_hubCameraWatchPeerId == id)
                {
                    _hubCameraWatchPeerId = -1;
                    notifyDisconnectedCam = true;
                }
                if (_hubMicWatchPeerId == id)
                {
                    _hubMicWatchPeerId = -1;
                    notifyDisconnectedMic = true;
                }
            }
            _fastMediaAssembler.ClearSource(id);
            if (notifyDisconnectedCam)
                PhoneCameraStatusReceived?.Invoke(id, 0, "Peer disconnected");
            if (notifyDisconnectedMic)
                PhoneMicStatusReceived?.Invoke(id, 0, "Peer disconnected");
            if (session.Peer.Platform == "Android")
                PhoneScreenStatusReceived?.Invoke(id, 0, "Peer disconnected");
            int watchedCam = Interlocked.Exchange(ref session.WatchingCameraTargetId, -1);
            int watchedMic = Interlocked.Exchange(ref session.WatchingMicTargetId, -1);
            if (watchedCam == 0 || watchedMic == 0)
                SyncHubLocalMediaCapture(0);
            if (watchedCam > 0)
                DispatchTargetMediaWatch(watchedCam);
            if (watchedMic > 0 && watchedMic != watchedCam)
                DispatchTargetMicWatch(watchedMic);
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
                    next=_sessions.Values
                        .OrderByDescending(s=>string.Equals(s.Peer.Platform, "Windows", StringComparison.Ordinal))
                        .ThenByDescending(s=>s.IsCableSession)
                        .ThenBy(s=>s.Id).FirstOrDefault();
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

    private async Task DispatchReverseStateAsync(Session session, string stateType,
        int claim, CancellationToken token)
    {
        RelayLease? relay = null;
        bool forwardRelay = false;
        bool clearRelay = false;
        bool notifySelected = false;
        lock (_authorityGate)
        {
            _relayByTarget.TryGetValue(session.Id, out relay);
            if (relay != null && relay.Claim != claim) relay = null;
            if (relay != null)
            {
                if (stateType == "reverse-granted")
                {
                    if (relay.GrantedAcked) return;
                    relay.GrantedAcked = true;
                    relay.Granted = true;
                    Volatile.Write(ref session.ActiveReverseLease, relay.Claim);
                    session.MovePacer?.SetWarmActive(true);
                    forwardRelay = true;
                }
                else if (stateType == "reverse-active")
                {
                    if (!relay.Granted || relay.ActiveAcked) return;
                    relay.ActiveAcked = true;
                    Volatile.Write(ref session.PendingFirstCursorAck, 0);
                    forwardRelay = true;
                }
                else
                {
                    clearRelay = true;
                    forwardRelay = true;
                    Volatile.Write(ref session.ActiveReverseLease, 0);
                    Volatile.Write(ref session.PendingFirstCursorAck, 0);
                    session.MovePacer?.SetWarmActive(false);
                    session.MovePacer?.ClearPending();
                    _relayBySource.Remove(relay.Source.Id);
                    _relayByTarget.Remove(relay.Target.Id);
                    Interlocked.Exchange(ref relay.LatestMove, null);
                    relay.InputQueue.Writer.TryComplete();
                }
            }
            else if (ReferenceEquals(Volatile.Read(ref _selected), session))
            {
                bool matchesClaim = _hubClaims.TryGetValue(session.Id, out int hubClaim) &&
                    hubClaim == claim;
                if (stateType == "reverse-granted")
                {
                    if (!matchesClaim || session.HubGrantedAcked) return;
                    session.HubGrantedAcked = true;
                }
                else if (stateType == "reverse-active")
                {
                    if (!matchesClaim || session.HubActiveAcked) return;
                    session.HubActiveAcked = true;
                    Volatile.Write(ref session.PendingFirstCursorAck, 0);
                }
                else
                {
                    if (matchesClaim)
                    {
                        _hubClaims.Remove(session.Id);
                        session.HubGrantedAcked = false;
                        session.HubActiveAcked = false;
                        Volatile.Write(ref session.PendingFirstCursorAck, 0);
                        Volatile.Write(ref session.ActiveReverseLease, 0);
                        session.MovePacer?.SetWarmActive(false);
                        session.MovePacer?.ClearPending();
                    }
                    else if (stateType != "reverse-local-active")
                        return;
                }
                notifySelected = true;
            }
            else if (Volatile.Read(ref session.BridgedClaimLease) is int bridgedClaim && bridgedClaim > 0)
            {
                bool matchesBridged = bridgedClaim == claim;
                if (stateType == "reverse-granted")
                {
                    if (!matchesBridged || session.HubGrantedAcked) return;
                    session.HubGrantedAcked = true;
                }
                else if (stateType == "reverse-active")
                {
                    if (!matchesBridged || session.HubActiveAcked) return;
                    session.HubActiveAcked = true;
                    Volatile.Write(ref session.PendingFirstCursorAck, 0);
                }
                else if (matchesBridged || stateType == "reverse-local-active")
                {
                    Volatile.Write(ref session.BridgedClaimLease, 0);
                    Volatile.Write(ref session.ActiveReverseLease, 0);
                    Volatile.Write(ref session.PendingFirstCursorAck, 0);
                    session.MovePacer?.SetWarmActive(false);
                    session.MovePacer?.ClearPending();
                }
                else return;
                if (session.BridgedReplyAsync is { } bridgedReply)
                {
                    var captureReply = bridgedReply;
                    if (stateType is ("reverse-busy" or "reverse-local-active"))
                        session.BridgedReplyAsync = null;
                    _ = Task.Run(async () =>
                    {
                        try { await captureReply(new Packet(stateType, claim)); }
                        catch (Exception) { }
                    });
                }
                return;
            }
        }
        if (forwardRelay && relay != null)
        {
            _ = clearRelay;
            string response = stateType["reverse-".Length..];
            try { await SendToAsync(relay.Source, new Packet(response, claim), token); }
            catch (Exception) { }
        }
        else if (notifySelected)
        {
            ReverseControlMessage?.Invoke(new Packet(stateType, claim));
        }
    }

    private async Task SendTcpPreviewChunksAsync(List<Session> watchers, int frameId, int packedDims, byte[] jpeg)
    {
        const int chunkSize = 3600;
        int total = (jpeg.Length + chunkSize - 1) / chunkSize;
        if (total <= 0) return;
        for (int i = 0; i < total; i++)
        {
            if (_disposed || _cancel.IsCancellationRequested) break;
            int offset = i * chunkSize;
            int len = Math.Min(chunkSize, jpeg.Length - offset);
            string b64 = Convert.ToBase64String(jpeg, offset, len);
            var pkt = new Packet("screen-preview-chunk", frameId, i, total, packedDims, b64);
            foreach (var android in watchers)
            {
                if (Volatile.Read(ref android.ScreenPreviewMonitor) < 0) continue;
                try { await SendToAsync(android, pkt, _cancel.Token); } catch { }
            }
        }
    }

    private void OnUdpHubMediaDatagram(FastUdpHub.Peer peer, FastUdpMediaDatagram media)
    {
        if (!_sessions.TryGetValue(peer.SessionId, out var session)) return;
        if (peer.Confirmed && peer.TryMarkConfirmedLogged() && !peer.IsAuxiliary)
            _status($"Low-latency input confirmed for {session.Peer.Name}.");

        var hub = _udpHub;
        switch (media.Kind)
        {
            case FastUdpMediaKind.MicAudio:
            {
                if (media.Payload.Length == 0) return;
                int sr = media.IndexOrSampleRate is >= 8000 and <= 48000 ? media.IndexOrSampleRate : 16000;
                PhoneMicAudioReceived?.Invoke(session.Id, media.Payload, sr, media.FrameOrAudioId);
                string? b64 = null;
                foreach (var w in _sessions.Values)
                {
                    if (Volatile.Read(ref w.WatchingMicTargetId) != session.Id) continue;
                    if (!SendDualOrBestMicUdp(hub, w, media.FrameOrAudioId, sr, session.Id, media.Payload))
                    {
                        b64 ??= Convert.ToBase64String(media.Payload);
                        var pkt = new Packet("phone-mic-audio", media.FrameOrAudioId, sr, 1, session.Id, b64);
                        _ = Task.Run(async () => { try { await SendToAsync(w, pkt, _cancel.Token); } catch { } });
                    }
                }
                break;
            }
            case FastUdpMediaKind.CameraFrameChunk:
            {
                List<Session>? tcpWatchers = null;
                foreach (var w in _sessions.Values)
                {
                    if (Volatile.Read(ref w.WatchingCameraTargetId) != session.Id) continue;
                    if (hub != null && ActiveMediaUdpPeer(w) is { } wUdp)
                    {
                        hub.SendMedia(wUdp, FastUdpMediaKind.CameraFrameChunk, media.FrameOrAudioId,
                            media.IndexOrSampleRate, media.TotalOrChannels, session.Id, media.Payload);
                    }
                    else
                    {
                        (tcpWatchers ??= new List<Session>()).Add(w);
                    }
                }
                if (_fastMediaAssembler.AddChunk(session.Id, (byte)media.Kind,
                    media.FrameOrAudioId, media.IndexOrSampleRate, media.TotalOrChannels,
                    media.MetaOrRouteId, media.Payload, out byte[] frame, out _))
                {
                    PhoneCameraFrameReceived?.Invoke(session.Id, frame);
                    if (tcpWatchers != null)
                        _ = SendTcpCameraChunksAsync(tcpWatchers, media.FrameOrAudioId, session.Id, frame);
                }
                break;
            }
            case FastUdpMediaKind.PhoneScreenChunk:
            {
                if (_fastMediaAssembler.AddChunk(session.Id, (byte)media.Kind,
                    media.FrameOrAudioId, media.IndexOrSampleRate, media.TotalOrChannels,
                    media.MetaOrRouteId, media.Payload, out byte[] frame, out _))
                {
                    PhoneScreenFrameReceived?.Invoke(session.Id, frame);
                }
                break;
            }
            case FastUdpMediaKind.PcScreenPreviewChunk:
            {
                if (session.Peer.Platform != "Windows") return;
                List<Session>? tcpAndroids = null;
                foreach (var android in _sessions.Values)
                {
                    if (android.Peer.Platform != "Android" ||
                        Volatile.Read(ref android.ScreenPreviewTarget) != session.Id ||
                        Volatile.Read(ref android.ScreenPreviewMonitor) < 0 ||
                        Volatile.Read(ref android.UrgentControlWaiters) != 0)
                        continue;

                    if (hub != null && ActiveMediaUdpPeer(android) is { } aUdp)
                    {
                        hub.SendMedia(aUdp, FastUdpMediaKind.PcScreenPreviewChunk,
                            media.FrameOrAudioId, media.IndexOrSampleRate, media.TotalOrChannels,
                            media.MetaOrRouteId, media.Payload);
                    }
                    else
                    {
                        (tcpAndroids ??= new List<Session>()).Add(android);
                    }
                }
                if (tcpAndroids != null && _fastMediaAssembler.AddChunk(session.Id, (byte)media.Kind,
                    media.FrameOrAudioId, media.IndexOrSampleRate, media.TotalOrChannels,
                    media.MetaOrRouteId, media.Payload, out byte[] shot, out int packedDims))
                {
                    _ = SendTcpPreviewChunksAsync(tcpAndroids, media.FrameOrAudioId, packedDims, shot);
                }
                break;
            }
        }
    }

    private void OnUdpHubMessage(FastUdpHub.Peer peer, FastUdpMessage msg)
    {
        if (!_sessions.TryGetValue(peer.SessionId, out var session)) return;
        if (peer.Confirmed && peer.TryMarkConfirmedLogged())
        {
            if (peer.IsAuxiliary)
            {
                DiagnosticLog?.Invoke($"Mobile network stream ready for {session.Peer.Name} ({peer.RemoteEndPoint?.Address}). Wi-Fi stays connected.");
                bool desiredMouse = Volatile.Read(ref session.DesiredPreferMobileMouse) != 0;
                bool desiredKb = Volatile.Read(ref session.DesiredPreferMobileKeyboard) != 0;
                if (desiredMouse || desiredKb)
                    ApplyStreamPreference(session, desiredMouse, desiredKb);
            }
            else
            {
                _status($"Low-latency input confirmed for {session.Peer.Name}.");
            }
        }
        if (msg.Op == FastUdpOp.LinkPing)
        {
            _udpHub?.Send(peer, FastUdpOp.LinkFeedback, msg.A,
                peer.LinkTracker.GetInboundPackedLossJitter(), 0, payloadTag: 0);
            if (!peer.IsAuxiliary || Volatile.Read(ref session.PreferMobileMouse) != 0)
                UpdateSessionAdaptivePacing(session);
            return;
        }
        if (msg.Op == FastUdpOp.LinkFeedback)
        {
            int reportedHz = peer.LinkTracker.PeerRefreshHz;
            if (reportedHz is >= 24 and <= 360)
            {
                Volatile.Write(ref session.LiveRefreshHz, reportedHz);
                Volatile.Write(ref session.LiveFrameTenthsMs, peer.LinkTracker.PeerFrameTenthsMs);
            }
            if (!peer.IsAuxiliary || Volatile.Read(ref session.PreferMobileMouse) != 0)
                UpdateSessionAdaptivePacing(session);
            return;
        }
        if (msg.Op is FastUdpOp.ReverseGranted or FastUdpOp.ReverseActive)
        {
            if (msg.A > 0)
            {
                string stateType = msg.Op == FastUdpOp.ReverseGranted
                    ? (msg.B != 0 ? "reverse-granted" : "reverse-busy")
                    : "reverse-active";
                _ = DispatchReverseStateAsync(session, stateType, msg.A, _cancel.Token);
            }
            return;
        }
        if (msg.Op is not (FastUdpOp.Move or FastUdpOp.Warm or FastUdpOp.ForwardButton or FastUdpOp.ForwardWheel or FastUdpOp.ForwardKey)) return;

        (int VirtualTargetId, int Claim, Session Source, Session BridgeSession, int LocalSubId)? virtualForward = null;
        RelayLease? forward = null;
        lock (_authorityGate)
        {
            if (_virtualRelayBySource.TryGetValue(session.Id, out var vFwd))
                virtualForward = vFwd;
            else
                _relayBySource.TryGetValue(session.Id, out forward);
        }
        Packet? forwardPacket = msg.Op switch
        {
            FastUdpOp.Move => new Packet("move", msg.A, msg.B),
            FastUdpOp.ForwardButton => new Packet("button", msg.A, msg.B, C: msg.TimestampMs),
            FastUdpOp.ForwardWheel => new Packet("wheel", msg.A, msg.B, C: msg.TimestampMs),
            FastUdpOp.ForwardKey => new Packet("key", (msg.A >> 8) & 0xFF, (msg.A >> 16) & 1, msg.B != 0 ? 1 : 0, msg.A & 0xFF,
                Text: msg.TimestampMs > 0 ? msg.TimestampMs.ToString(System.Globalization.CultureInfo.InvariantCulture) : null),
            _ => null
        };
        if (virtualForward is { } vForward)
        {
            if (msg.Op == FastUdpOp.Warm)
            {
                var bridgeUdp = vForward.BridgeSession.UdpPeer;
                if (bridgeUdp is { HasEndpoint: true } && _udpHub != null)
                    _udpHub.Send(bridgeUdp, FastUdpOp.Warm, 0, 0, 0);
                return;
            }
            if (forwardPacket != null)
            {
                string env = JsonSerializer.Serialize(new SubRouteEnvelope(vForward.LocalSubId, forwardPacket));
                vForward.BridgeSession.ReverseQueue.Writer.TryWrite(
                    new Packet("sub-route", vForward.VirtualTargetId, Text: env));
            }
            return;
        }
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
            if (forwardPacket != null)
                QueueRoutedInput(forward, forwardPacket);
            return;
        }
        if (msg.Op == FastUdpOp.Warm)
        {
            if (msg.A == 1 && _udpHub != null && peer.Confirmed)
            {
                _udpHub.Send(peer, FastUdpOp.Warm, 2, 0, 0);
            }
            if (Interlocked.Exchange(ref peer.PendingWakePing, 0) != 0 && _udpHub != null && peer.Confirmed)
            {
                long nowMs = Environment.TickCount64;
                peer.LastLinkPingMs = nowMs;
                int paceTenths = (int)Math.Round((session.MovePacer?.EffectiveMinIntervalMs ?? 0) * 10.0);
                _udpHub.Send(peer, FastUdpOp.LinkPing,
                    peer.LinkTracker.NextPingToken(nowMs), paceTenths,
                    Volatile.Read(ref session.ActiveReverseLease));
                return;
            }
            if (session.Peer.Platform == "Windows")
                BroadcastWarmToConfirmedTargets(session.Id);
            else if (ShouldEchoAndroidWarm(session.Peer.Platform) &&
                msg.A == 0 &&
                Volatile.Read(ref session.ActiveReverseLease) == 0 &&
                CanEmitAndroidWarmEcho(Stopwatch.GetTimestamp(), ref session.LastAndroidWarmEchoTicks) &&
                _udpHub != null && session.UdpPeer is { Confirmed: true } peerUdp)
                _udpHub.Send(peerUdp, FastUdpOp.Warm, 0, 0, 0);
            return;
        }
        if (forwardPacket == null || session.EnvironmentTargetId != 0) return;

        int lease;
        lock (_authorityGate)
            lease = ReferenceEquals(_owner, session) ? session.Lease : 0;
        if (lease <= 0 || lease != msg.Lease || !_priority.CanApplyRemote) return;

        if (msg.Op is FastUdpOp.ForwardButton or FastUdpOp.ForwardWheel &&
            !session.TryAcceptForwardAction(msg.TimestampMs, msg.Op == FastUdpOp.ForwardButton))
            return;
        if (msg.Op == FastUdpOp.ForwardKey &&
            !session.TryAcceptForwardKeyAction(msg.TimestampMs))
            return;

        bool applied = false;
        lock (session.InputLock)
        {
            lock (_authorityGate)
            {
                if (ReferenceEquals(_owner, session) && lease == session.Lease &&
                    _priority.CanApplyRemote)
                {
                    if (msg.Op == FastUdpOp.Move)
                    {
                        var (_, currX) = FastMovePacer.UnpackDualCoord(msg.A);
                        var (_, currY) = FastMovePacer.UnpackDualCoord(msg.B);
                        applied = session.Injector.Handle(new Packet("move", currX, currY));
                    }
                    else
                    {
                        applied = session.Injector.Handle(forwardPacket);
                    }
                }
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

    internal const double AndroidUdpMinIntervalMs = 7.0;
    internal const long AndroidWarmEchoMinIntervalMs = 240;

    internal static double RecommendedUdpMinIntervalMs(string? platform, int? refreshHz)
    {
        if (!string.Equals(platform, "Android", StringComparison.Ordinal))
            return 2.5;
        if (refreshHz is >= 115) return 6.5;
        if (refreshHz is >= 85) return 8.0;
        if (refreshHz is > 0 and < 85) return 10.0;
        return AndroidUdpMinIntervalMs;
    }

    internal static double ComputeAdaptiveUdpIntervalMs(
        string? platform, int? liveRefreshHz, double rttMs, double minRttMs,
        double jitterMs, double lossPermille) =>
        ComputeAdaptiveUdpIntervalMs(platform, liveRefreshHz, rttMs, minRttMs, jitterMs, lossPermille, null);

    internal static double ComputeAdaptiveUdpIntervalMs(
        string? platform, int? liveRefreshHz, double rttMs, double minRttMs,
        double jitterMs, double lossPermille, string? linkBand)
    {
        bool isAndroid = string.Equals(platform, "Android", StringComparison.Ordinal);
        string? normalizedBand = string.IsNullOrWhiteSpace(linkBand)
            ? null
            : AdaptiveLinkTracker.NormalizeBand(linkBand);
        bool isSlow24Ghz = isAndroid && string.Equals(normalizedBand, "2.4GHz", StringComparison.Ordinal);
        bool isCellular = isAndroid && string.Equals(normalizedBand, "cellular", StringComparison.Ordinal);
        double baseMs;
        if (isAndroid)
        {
            if (isSlow24Ghz)
            {
                bool cleanRadioTiming = rttMs > 0 && rttMs <= 18.0 && jitterMs <= 5.5 &&
                    (minRttMs <= 0 || rttMs - minRttMs <= 6.0);
                baseMs = liveRefreshHz is >= 24 and < 65
                    ? 13.0
                    : (liveRefreshHz is >= 90 && (lossPermille < 20.0 || cleanRadioTiming) ? 8.5 : 11.0);
            }
            else if (isCellular)
            {
                baseMs = liveRefreshHz is >= 24 and < 65 ? 13.0 : 9.5;
            }
            else if (liveRefreshHz is >= 24)
            {
                baseMs = Math.Clamp(1000.0 / (Math.Min(240, liveRefreshHz.Value) * 1.55), 4.2, 13.0);
            }
            else
            {
                baseMs = AndroidUdpMinIntervalMs;
            }
        }
        else
        {
            baseMs = liveRefreshHz is >= 24 and < 75
                ? Math.Clamp(1000.0 / (liveRefreshHz.Value * 2.0), 2.5, 6.0)
                : 2.5;
        }

        if (rttMs <= 0 || double.IsNaN(rttMs) || double.IsInfinity(rttMs))
            return baseMs;

        double queueDelay = (minRttMs > 0 && rttMs > minRttMs) ? (rttMs - minRttMs) : 0.0;
        double floorMs = isSlow24Ghz ? 7.5 : (isCellular ? 6.5 : (isAndroid ? 4.2 : 1.8));
        if (!isSlow24Ghz && !isCellular && rttMs <= 7.0 && queueDelay <= 2.5 && jitterMs <= 3.5 && lossPermille < 2.0)
            return Math.Max(floorMs, baseMs * 0.85);
        if (isCellular && queueDelay <= 6.0 && jitterMs <= 4.5 && lossPermille < 2.0)
            return liveRefreshHz is >= 90 ? 6.8 : Math.Min(baseMs, 11.0);
        if (isSlow24Ghz && rttMs <= 12.0 && queueDelay <= 4.0 && jitterMs <= 4.5 && lossPermille < 2.0)
            return Math.Max(floorMs, baseMs * 0.78);
        if (!isCellular && rttMs <= 18.0 && queueDelay <= 6.0 && jitterMs <= 5.5 && lossPermille >= 4.0)
        {
            double dropPenalty = Math.Min(2.0, Math.Max(0.0, lossPermille - 8.0) * 0.015);
            return Math.Clamp(baseMs + dropPenalty, Math.Max(floorMs, baseMs), isSlow24Ghz ? 10.5 : Math.Max(baseMs + 2.0, 10.0));
        }

        double rttPressure = isCellular ? 0.0 : Math.Max(0.0, rttMs - 18.0) * 0.08;
        double bloatPressure = Math.Max(0.0, queueDelay - 8.0) * 0.16;
        double jitterPressure = Math.Max(0.0, jitterMs - 8.0) * 0.11;
        double lossPressure = Math.Max(0.0, lossPermille - 8.0) * 0.10;
        double maxCap = !isAndroid
            ? 12.0
            : (isSlow24Ghz
                ? 16.5
                : (isCellular
                    ? 18.0
                    : (liveRefreshHz is >= 115 ? 14.0
                        : liveRefreshHz is >= 85 ? 16.0
                        : liveRefreshHz is >= 55 ? 18.5
                        : 20.0)));
        maxCap = Math.Max(baseMs, maxCap);
        return Math.Clamp(baseMs + rttPressure + bloatPressure + jitterPressure + lossPressure, baseMs, maxCap);
    }

    internal static double ComputeAdaptiveCableIntervalMs(int? liveRefreshHz, double tcpRttMs)
    {
        double baseMs = liveRefreshHz is >= 24
            ? Math.Clamp(340.0 / Math.Min(240, liveRefreshHz.Value), 2.0, 5.5)
            : 3.0;
        if (tcpRttMs <= 0 || double.IsNaN(tcpRttMs) || double.IsInfinity(tcpRttMs))
            return baseMs;
        double rttPressure = Math.Max(0.0, tcpRttMs - 15.0) * 0.12;
        double maxCap = Math.Max(baseMs, 5.5);
        return Math.Clamp(baseMs + rttPressure, baseMs, maxCap);
    }

    internal static int ComputeWarmIntervalMs(string? linkBand, double queueDelayMs, double lossPermille)
    {
        string normalized = AdaptiveLinkTracker.NormalizeBand(linkBand);
        bool isSlow24 = string.Equals(normalized, "2.4GHz", StringComparison.Ordinal);
        bool isCellular = string.Equals(normalized, "cellular", StringComparison.Ordinal);
        // On 2.4 GHz idle pauses, keep CAM awake below the 50-100 ms power-save threshold
        // without causing 100 Hz half-duplex keepalive storms.
        if (isSlow24)
        {
            return Math.Clamp((int)Math.Round(22.0 + queueDelayMs * 0.55 + lossPermille * 0.15), 20, 36);
        }
        if (isCellular)
        {
            return Math.Clamp((int)Math.Round(22.0 + queueDelayMs * 0.50 + lossPermille * 0.12), 20, 38);
        }
        int minMs = 24;
        int maxMs = 52;
        double baseMs = 24.0;
        return Math.Clamp((int)Math.Round(baseMs + queueDelayMs * 0.65 + lossPermille * 0.25), minMs, maxMs);
    }

    internal static bool SupportsUdpEdgePush(string? platform, string? appVersion) =>
        string.Equals(platform, "Android", StringComparison.Ordinal) &&
        Version.TryParse(appVersion, out var parsed) && parsed >= new Version(0, 3, 50);

    internal static bool SupportsUdpEdgePush(string? appVersion) =>
        SupportsUdpEdgePush("Android", appVersion);

    internal static bool SupportsDualMovePacket(string? platform, string? appVersion) =>
        string.Equals(platform, "Android", StringComparison.Ordinal) &&
        Version.TryParse(appVersion, out var parsed) && parsed >= new Version(0, 3, 57);

    internal static bool SupportsDualMovePacket(string? appVersion) =>
        SupportsDualMovePacket("Android", appVersion);
    internal static bool SupportsFastReverseKey(string? platform, string? appVersion) =>
        string.Equals(platform, "Android", StringComparison.Ordinal) &&
        Version.TryParse(appVersion, out var parsed) && parsed >= new Version(0, 3, 67);

    /// <summary>Encodes a reliable (wired / fallback) move carrying both (-) and (+) positions.</summary>
    private static Packet EncodeMoveForWire(Session session, Packet move)
    {
        var encoded = ReverseInputProtocol.Encode(move);
        if (move.Type != "move") return encoded;
        var (prevX, prevY) = session.TakeWirePrevious(move.A, move.B, preferWaypoint: true);
        if (!SupportsDualMovePacket(session.Peer.Platform, session.Peer.AppVersion)) return encoded;
        return encoded with
        {
            A = FastMovePacer.PackDualCoord(prevX, move.A),
            B = FastMovePacer.PackDualCoord(prevY, move.B)
        };
    }

    private bool SendReverseMoveUdp(Session session, FastUdpHub.Peer? peerUdp, int lease, Packet move)
    {
        if (_udpHub == null || peerUdp is not { Confirmed: true } || lease <= 0)
            return false;
        int? tag = move.C > 0 ? move.C : null;
        var (wirePrevX, wirePrevY) = session.TakeWirePrevious(move.A, move.B, preferWaypoint: false);
        FastUdpHub.Peer? mirrorPeer = null;
        if (Volatile.Read(ref session.DualMouseMirrorUntilTicks) > Stopwatch.GetTimestamp())
        {
            var candidate = ReferenceEquals(peerUdp, session.MobileUdpPeer) ? session.UdpPeer : session.MobileUdpPeer;
            if (candidate is { Confirmed: true }) mirrorPeer = candidate;
        }
        bool Dispatch(FastUdpOp op, int a, int b)
        {
            bool ok = _udpHub.Send(peerUdp, op, a, b, lease, tag);
            if (mirrorPeer != null)
                _udpHub.Send(mirrorPeer, op, a, b, lease, tag);
            return ok;
        }
        if (move.D != 0 &&
            move.A is >= short.MinValue and <= short.MaxValue &&
            move.B is >= short.MinValue and <= short.MaxValue &&
            SupportsUdpEdgePush(session.Peer.Platform, session.Peer.AppVersion))
        {
            var (pushX, pushY) = FastMovePacer.UnpackEdgePush(move.D);
            if (pushX != 0 || pushY != 0)
            {
                return Dispatch(FastUdpOp.ReverseEdgePush,
                    ((pushX & 0xFFFF) << 16) | (move.A & 0xFFFF),
                    ((pushY & 0xFFFF) << 16) | (move.B & 0xFFFF));
            }
        }
        if (SupportsDualMovePacket(session.Peer.Platform, session.Peer.AppVersion))
        {
            return Dispatch(FastUdpOp.ReverseMove,
                FastMovePacer.PackDualCoord(wirePrevX, move.A),
                FastMovePacer.PackDualCoord(wirePrevY, move.B));
        }
        return Dispatch(FastUdpOp.ReverseMove, move.A, move.B);
    }

    internal static bool ShouldEchoAndroidWarm(string? platform) =>
        string.Equals(platform, "Android", StringComparison.Ordinal);

    internal static bool CanEmitAndroidWarmEcho(long nowTicks, ref long lastEchoTicks)
    {
        long previous = Interlocked.Read(ref lastEchoTicks);
        if (previous != 0 &&
            (nowTicks - previous) * 1000.0 / Stopwatch.Frequency < AndroidWarmEchoMinIntervalMs)
            return false;
        return Interlocked.CompareExchange(ref lastEchoTicks, nowTicks, previous) == previous;
    }

    public void Dispose()
    {
        if(_disposed)return;
        _disposed=true;
        _priority.PhysicalOverride -= OnPhysicalOverride;
        _cancel.Cancel();
        try { _previewWake.Release(); } catch { }
        try { _localMediaCapture?.Dispose(); } catch { }
        try { _previewWake.Dispose(); } catch { }
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
    private readonly string _pinnedFingerprint;
    private readonly bool _isTrustedConnection;
    private string? _remoteAppVersion;
    private Packet? _latestMove;
    private int _moveSignaled;
    private TaskCompletionSource<bool>? _pendingTrust;
    private TaskCompletionSource<string?>? _pendingMutualSync;
    private int _mutualSyncStarted;
    internal event Action<int, Packet, Func<Packet, Task>>? SubRoutePacketReceived;
    internal Func<string, string, string, string, CancellationToken, Task<bool>>? MutualTrustOfferRequested;
    private LocalInputPriority? _reversePriority;
    private Func<bool>? _allowReverseClaim;
    private readonly object _reverseInputGate = new();
    private volatile string _linkBand;
    private int _linkSpeedMbps;
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
    private int _liveRemoteRefreshHz;
    private int _udpConfirmedLogged;
    private long _lastTcpWarmTicks;
    private string _lastLoggedVersions = "";
    private readonly object _reverseDedupLock = new();
    private int _reverseHighestClaimDispatchSeq;
    private int _reverseHighestActionSeq;
    private long _reverseSeenActionMask;
    private int _reverseLastButtonSeq;
    private int _reverseHighestKeySeq;
    private long _reverseSeenKeyMask;
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
        Volatile.Write(ref _liveRemoteRefreshHz, 0);
        UpdateAdaptivePacing();
        RemoteTopologyChanged?.Invoke(topology);
        RemoteNameChanged?.Invoke(name);
        return true;
    }

    private void UpdateAdaptivePacing()
    {
        var pacer = _movePacer;
        if (pacer == null) return;
        int targetId = SelectedEnvironmentTargetId;
        var member = targetId == 0
            ? null
            : Volatile.Read(ref _environmentMembers).FirstOrDefault(m => m.Id == targetId);
        string platform = member?.Platform ?? "Windows";
        int? monHz = Volatile.Read(ref _remoteMonitors).Monitors.FirstOrDefault()?.RefreshHz;
        int liveHz = Volatile.Read(ref _liveRemoteRefreshHz);
        int? effectiveHz = liveHz >= 24 ? liveHz : monHz;

        var client = Volatile.Read(ref _udpClient);
        double udpRtt = client?.LinkTracker.SmoothedRttMs ?? 0;
        double rtt = udpRtt > 0 ? udpRtt : Volatile.Read(ref _lastRttMs);
        double minRtt = client?.LinkTracker.MinRttMs ?? 0;
        double jitter = client?.LinkTracker.EffectiveJitterMs ?? 0;
        double loss = client?.LinkTracker.CombinedLossPermille ?? 0;
        double queueDelay = client?.LinkTracker.QueueDelayMs ?? 0;

        pacer.SetMinIntervalMs(
            ReceiverServer.ComputeAdaptiveUdpIntervalMs(
                platform, effectiveHz, rtt, minRtt, jitter, loss, client?.LinkTracker.Band));
        pacer.SetWarmIntervalMs(
            ReceiverServer.ComputeWarmIntervalMs(client?.LinkTracker.Band, queueDelay, loss));
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

    private int _hostTaskbarNudge;
    private volatile bool _taskbarWatched;
    private string _lastHostTaskbarSig = "";
    private readonly HashSet<string> _sentHostTaskbarIcons = new(StringComparer.Ordinal);
    private int _hostScreenPreviewMonitor = -1;
    private int _hostScreenPreviewHighRes = 0;
    private readonly SemaphoreSlim _hostPreviewWake = new(0, 1);
    private string _lastHostForegroundSig = "";
    private readonly FileDropHelper _hostFileDrop = new();
    private readonly FastMediaFrameAssembler _hostMediaAssembler = new();
    private readonly FileDropHelper.BufferedChunkAssembler _hostCameraAssembler = new();
    private readonly object _hostMediaGate = new();
    private WindowsMediaCaptureService? _hostMediaCapture;
    private int _hostCamWatchActive;
    private int _hostMicWatchActive;
    private int _hostMediaCamFrameSeq;
    private int _hostMediaMicAudioSeq;
    private int _hostCamStatus;
    private int _hostMicStatus;
    private readonly Dictionary<int, NotificationPreviewItem[]> _hostPeerNotifications = new();
    private readonly object _hostNotifGate = new();

    internal event Action<string, string>? ClipboardTextReceived;
    internal event Action<string, byte[]>? ClipboardImageReceived;
    internal event Action<string, string>? FileReceived;
    internal event Action<int, NotificationPreviewItem[]>? PeerNotificationsUpdated;
    internal event Action<string, string, string>? OtpOfferReceived;
    internal event Action<int, byte[]>? RemoteCameraFrameReceived;
    internal event Action<int, int, string?>? RemoteCameraStatusReceived;
    internal event Action<int, byte[], int, int>? RemoteMicAudioReceived;
    internal event Action<int, int, string?>? RemoteMicStatusReceived;
    internal event Action<bool, bool, string>? LocalMediaShareStateChanged;

    internal bool IsFastMediaUdpActive => Volatile.Read(ref _udpClient) is { Confirmed: true };

    internal string MediaTransportLabel =>
        Volatile.Read(ref _udpClient) is { Confirmed: true }
            ? (_linkBand is "ethernet" or "cable" ? "Fast UDP (Cable)" : "Fast UDP (LAN)")
            : "TCP Fallback";

    internal void SendRemoteCameraWatch(int targetPeerId, bool enable, int lens, bool torch, bool mic)
    {
        if (!enable)
            _hostMediaAssembler.ClearSource(targetPeerId);
        if (enable && Volatile.Read(ref _udpClient) is { Confirmed: false } unconfirmed)
            unconfirmed.SendProbe();
        TrySend(new Packet("phone-camera-watch", enable ? 1 : 0, lens, torch ? 1 : 0, mic ? 1 : 0,
            targetPeerId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    internal void SendRemoteMicWatch(int targetPeerId, bool enable)
    {
        if (enable && Volatile.Read(ref _udpClient) is { Confirmed: false } unconfirmed)
            unconfirmed.SendProbe();
        TrySend(new Packet("phone-mic-watch", enable ? 1 : 0, targetPeerId));
    }

    internal void StopLocalMediaSharing()
    {
        Volatile.Write(ref _hostCamWatchActive, 0);
        Volatile.Write(ref _hostMicWatchActive, 0);
        _hostMediaCapture?.StopAll();
        TrySend(new Packet("phone-camera-status", 0, 0, Text: "Camera sharing stopped on this PC"));
        TrySend(new Packet("phone-mic-status", 0, 0, Text: "Mic sharing stopped on this PC"));
        Volatile.Write(ref _hostCamStatus, 0);
        Volatile.Write(ref _hostMicStatus, 0);
        LocalMediaShareStateChanged?.Invoke(false, false, "");
    }

    private void SyncHostMediaCapture(int lens = 0)
    {
        bool needCam = Volatile.Read(ref _hostCamWatchActive) != 0;
        bool needMic = Volatile.Read(ref _hostMicWatchActive) != 0;
        WindowsMediaCaptureService? svc;
        lock (_hostMediaGate)
        {
            svc = _hostMediaCapture;
            if (svc == null && (needCam || needMic) && Volatile.Read(ref _closed) == 0)
            {
                svc = new WindowsMediaCaptureService(() => IsFastMediaUdpActive);
                svc.CameraFrameCaptured += OnHostLocalCameraFrame;
                svc.CameraStatusChanged += OnHostLocalCameraStatus;
                svc.MicAudioCaptured += OnHostLocalMicAudio;
                svc.MicStatusChanged += OnHostLocalMicStatus;
                _hostMediaCapture = svc;
            }
        }
        if (svc == null)
        {
            if (!needCam) Volatile.Write(ref _hostCamStatus, 0);
            if (!needMic) Volatile.Write(ref _hostMicStatus, 0);
            EmitHostLocalMediaSummary();
            return;
        }
        if (needCam) svc.StartCamera(lens);
        else
        {
            svc.StopCamera();
            Volatile.Write(ref _hostCamStatus, 0);
        }
        if (needMic) svc.StartMic();
        else
        {
            svc.StopMic();
            Volatile.Write(ref _hostMicStatus, 0);
        }
        EmitHostLocalMediaSummary();
    }

    private void EmitHostLocalMediaSummary()
    {
        bool cam = Volatile.Read(ref _hostCamStatus) != 0;
        bool mic = Volatile.Read(ref _hostMicStatus) != 0;
        if (!cam && !mic)
        {
            LocalMediaShareStateChanged?.Invoke(false, false, "");
            return;
        }
        string what = (cam, mic) switch
        {
            (true, true) => "camera & mic",
            (true, false) => "camera",
            _ => "microphone"
        };
        string hostName = RemoteSystemName ?? "host";
        LocalMediaShareStateChanged?.Invoke(cam, mic, $"Sharing this PC's {what} via {hostName}");
    }

    private void OnHostLocalCameraFrame(byte[] jpeg)
    {
        if (Volatile.Read(ref _closed) != 0 || jpeg.Length == 0) return;
        int frameId = Interlocked.Increment(ref _hostMediaCamFrameSeq);
        var client = Volatile.Read(ref _udpClient);
        if (client is { Confirmed: true })
        {
            ReceiverServer.SendUdpClientMediaChunks(client, FastUdpMediaKind.CameraFrameChunk, frameId, jpeg, 0);
            return;
        }
        const int chunkSize = 3600;
        int total = (jpeg.Length + chunkSize - 1) / chunkSize;
        for (int i = 0; i < total; i++)
        {
            if (Volatile.Read(ref _closed) != 0) break;
            int offset = i * chunkSize;
            int len = Math.Min(chunkSize, jpeg.Length - offset);
            string b64 = Convert.ToBase64String(jpeg, offset, len);
            if (!TrySend(new Packet("phone-camera-chunk", frameId, i, total, 0, b64))) break;
        }
    }

    private void OnHostLocalCameraStatus(int stateCode, string msg)
    {
        bool watchActive = Volatile.Read(ref _hostCamWatchActive) != 0;
        int effectiveState = watchActive ? stateCode : 0;
        if (!watchActive && stateCode != 0)
            _hostMediaCapture?.StopCamera();
        Volatile.Write(ref _hostCamStatus, effectiveState);
        EmitHostLocalMediaSummary();
        TrySend(new Packet("phone-camera-status", effectiveState, 0, Text: msg));
    }

    private void OnHostLocalMicAudio(byte[] pcm, int sampleRate)
    {
        if (Volatile.Read(ref _closed) != 0 || pcm.Length == 0) return;
        int seq = Interlocked.Increment(ref _hostMediaMicAudioSeq);
        var client = Volatile.Read(ref _udpClient);
        if (client is { Confirmed: true } && pcm.Length <= FastUdpCodec.MaxMediaPayloadBytes)
        {
            client.SendMedia(FastUdpMediaKind.MicAudio, seq, sampleRate, 1, 0, pcm);
            return;
        }
        TrySend(new Packet("phone-mic-audio", seq, sampleRate, 1, 0, Convert.ToBase64String(pcm)));
    }

    private void OnHostLocalMicStatus(int stateCode, string msg)
    {
        bool watchActive = Volatile.Read(ref _hostMicWatchActive) != 0;
        int effectiveState = watchActive ? stateCode : 0;
        if (!watchActive && stateCode != 0)
            _hostMediaCapture?.StopMic();
        Volatile.Write(ref _hostMicStatus, effectiveState);
        EmitHostLocalMediaSummary();
        TrySend(new Packet("phone-mic-status", effectiveState, 0, Text: msg));
    }

    internal NotificationPreviewItem[] GetNotificationsForTarget(int memberId)
    {
        lock (_hostNotifGate)
            return _hostPeerNotifications.TryGetValue(memberId, out var arr) ? arr : Array.Empty<NotificationPreviewItem>();
    }

    internal void SendClipboardText(string hash, string text)
    {
        string json = SharedClipboardHelper.SerializeClipboardTextPacket(hash, text);
        if (System.Text.Encoding.UTF8.GetByteCount(json) <= 7800)
            TrySend(new Packet("clipboard-text", Text: json));
    }

    internal void SendClipboardImage(string hash, byte[] png)
    {
        foreach (var pkt in FileDropHelper.BuildImageClipboardChunks(hash, png))
            TrySend(pkt);
    }

    internal async Task<bool> SendFileToMemberAsync(int memberId, string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists || info.Length <= 0 || info.Length > FileDropHelper.MaxFileSizeBytes)
                return false;
            int txId = FileDropHelper.NextTransferId();
            string safeName = FileDropHelper.SanitizeFileName(info.Name);
            TrySend(new Packet("file-offer", txId, (int)info.Length, memberId, 0, safeName));
            byte[] buf = new byte[FileDropHelper.ChunkRawBytes];
            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192, useAsync: true);
            int seq = 0;
            int read;
            while ((read = await fs.ReadAsync(buf, _cancel.Token)) > 0)
            {
                TrySend(new Packet("file-chunk", txId, seq++, memberId, 0, Convert.ToBase64String(buf, 0, read)));
                await Task.Delay(2, _cancel.Token);
            }
            TrySend(new Packet("file-done", txId, seq, memberId));
            return true;
        }
        catch { return false; }
    }

    internal void SendPowerAction(int targetMemberId, string action) =>
        TrySend(new Packet("power-action", targetMemberId, Text: action));

    internal void SendMediaAction(int targetMemberId, string action, int value = 0) =>
        TrySend(new Packet("media-action", targetMemberId, value, 0, 0, action));

    internal void SendNotificationDismiss(int targetMemberId, string key) =>
        TrySend(new Packet("notif-dismiss", targetMemberId, Text: key));

    internal void SendNotificationReply(int targetMemberId, string key, string reply) =>
        TrySend(new Packet("notif-reply", targetMemberId, Text: JsonSerializer.Serialize(new { k = key, r = reply })));

    private void ReleaseReverseAuthority()
    {
        Interlocked.Exchange(ref _reverseLease, 0);
        Interlocked.Exchange(ref _reverseAppliedLease, 0);
        _reversePriority?.ReleaseRemote();
        lock (_reverseInputGate) _reverseInjector?.ReleaseAll();
    }

    private bool TryAcceptReverseClaimDispatch(int claim, int dispatchSeq)
    {
        if (claim <= 0) return false;
        lock (_reverseDedupLock)
        {
            if (dispatchSeq > 0)
            {
                if (dispatchSeq <= _reverseHighestClaimDispatchSeq) return false;
                _reverseHighestClaimDispatchSeq = dispatchSeq;
            }
            else if (Volatile.Read(ref _reverseLease) == claim)
            {
                return false;
            }
            _reverseHighestActionSeq = 0;
            _reverseSeenActionMask = 0;
            _reverseLastButtonSeq = 0;
            _reverseHighestKeySeq = 0;
            _reverseSeenKeyMask = 0;
            return true;
        }
    }

    private bool TryAcceptReverseKeyAction(int seq)
    {
        if (seq <= 0) return true;
        lock (_reverseDedupLock)
        {
            if (_reverseHighestKeySeq == 0)
            {
                _reverseHighestKeySeq = seq;
                _reverseSeenKeyMask = 1L;
            }
            else
            {
                int diff = seq - _reverseHighestKeySeq;
                if (diff > 0)
                {
                    _reverseSeenKeyMask = diff >= 64 ? 1L : ((_reverseSeenKeyMask << diff) | 1L);
                    _reverseHighestKeySeq = seq;
                }
                else
                {
                    int back = -diff;
                    if (back >= 64) return false;
                    long bit = 1L << back;
                    if ((_reverseSeenKeyMask & bit) != 0L) return false;
                    _reverseSeenKeyMask |= bit;
                }
            }
            return true;
        }
    }

    private bool TryAcceptReverseControlAction(int seq, bool isButton)
    {
        if (seq <= 0) return true;
        lock (_reverseDedupLock)
        {
            if (isButton && seq <= _reverseLastButtonSeq) return false;
            if (_reverseHighestActionSeq == 0)
            {
                _reverseHighestActionSeq = seq;
                _reverseSeenActionMask = 1L;
            }
            else
            {
                int diff = seq - _reverseHighestActionSeq;
                if (diff > 0)
                {
                    _reverseSeenActionMask = diff >= 64 ? 1L : ((_reverseSeenActionMask << diff) | 1L);
                    _reverseHighestActionSeq = seq;
                }
                else
                {
                    int back = -diff;
                    if (back >= 64) return false;
                    long bit = 1L << back;
                    if ((_reverseSeenActionMask & bit) != 0L) return false;
                    _reverseSeenActionMask |= bit;
                }
            }
            if (isButton) _reverseLastButtonSeq = seq;
            return true;
        }
    }

    private void ProcessReverseInput(Packet packet)
    {
        if (!ReverseInputProtocol.TryDecode(packet, out var request)) return;
        if (request.Type == "claim")
        {
            if (!TryAcceptReverseClaimDispatch(request.A, request.C)) return;
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
            var udpGrant = Volatile.Read(ref _udpClient);
            if (udpGrant is { Confirmed: true })
                udpGrant.Send(FastUdpOp.ReverseGranted, request.A, permitted ? 1 : 0, request.A);
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
        if (request.Type is "button" or "wheel" &&
            !TryAcceptReverseControlAction(request.C, request.Type == "button"))
            return;
        if (request.Type == "key" &&
            int.TryParse(request.Text, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int keySeq) &&
            !TryAcceptReverseKeyAction(keySeq))
            return;
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
        {
            var udpActive = Volatile.Read(ref _udpClient);
            if (udpActive is { Confirmed: true })
                udpActive.Send(FastUdpOp.ReverseActive, active, 0, active);
            TrySend(new Packet("reverse-active", active));
        }
    }

    private HostSession(TcpClient client, SslStream ssl, IPAddress serverAddress,
        string pinnedFingerprint, bool trustedConnection,
        MonitorTopology monitors, Action<string> onFailure,
        string linkBand = "wireless", int linkSpeedMbps = 0)
    {
        _client = client; _ssl = ssl; _serverAddress = serverAddress;
        _pinnedFingerprint = pinnedFingerprint;
        _isTrustedConnection = trustedConnection;
        _remoteAppVersion = monitors.Monitors.FirstOrDefault()?.AppVersion;
        _remoteMonitors = monitors;
        _environmentHubMonitors = monitors; _onFailure = onFailure;
        _linkBand = linkBand;
        _linkSpeedMbps = linkSpeedMbps;
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
            queueTrailingSync: move =>
            {
                var udp = _udpClient;
                int lease = Volatile.Read(ref _forwardClaimLease);
                if (udp is { Confirmed: true } && lease > 0 && Volatile.Read(ref _closed) == 0 &&
                    udp.Send(FastUdpOp.Move, move.A, move.B, lease))
                    return;
                QueueTcpMove(move, countAsFallback: false);
            });
        _ = Task.Run(SendLoopAsync);
        _ = Task.Run(HeartbeatAsync);
        _ = Task.Run(ReceiveLoopAsync);
        _ = Task.Run(HostScreenPreviewLoopAsync);
    }

    internal ReceiverServer.ActiveLinkSnapshot GetActiveLinkSummary()
    {
        if (Volatile.Read(ref _closed) != 0)
            return new ReceiverServer.ActiveLinkSnapshot(false, "", "offline", 0, 0, 0, 0, null);
        string name = RemoteSystemName ?? _environmentHubName ?? _serverAddress.ToString();
        var udp = Volatile.Read(ref _udpClient);
        string band = _linkBand;
        if (band != "ethernet" && band != "cable")
        {
            string? trackerBand = udp?.LinkTracker.Band;
            if (!string.IsNullOrWhiteSpace(trackerBand) && trackerBand != "wireless")
                band = trackerBand;
            else if (string.IsNullOrWhiteSpace(band))
                band = "wireless";
        }
        int speed = _linkSpeedMbps;
        if (speed <= 0 && udp != null && udp.LinkTracker.LinkSpeedMbps > 0)
            speed = udp.LinkTracker.LinkSpeedMbps;
        double rtt = udp?.LinkTracker.SmoothedRttMs ?? Volatile.Read(ref _lastRttMs);
        double jitter = udp?.LinkTracker.EffectiveJitterMs ?? 0;
        double loss = udp?.LinkTracker.CombinedLossPermille ?? 0;
        return new ReceiverServer.ActiveLinkSnapshot(
            true,
            name,
            band,
            speed,
            rtt,
            jitter,
            loss,
            null);
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

    internal bool SupportsBidirectionalProtocol =>
        MutualTrustCodec.SupportsBidirectionalProtocol(_remoteAppVersion);

    internal string RemotePinnedFingerprint => _pinnedFingerprint;

    internal async Task<bool> RequestMutualTrustAsync(
        TrustedDeviceStore trusted, string remoteName, CancellationToken cancellation,
        string? identityPath = null)
    {
        if (Volatile.Read(ref _closed) != 0) return false;
        if (!SupportsBidirectionalProtocol)
        {
            string? token = await RequestTrustAsync(cancellation);
            if (token == null) return false;
            trusted.TrustOutgoing(remoteName, _pinnedFingerprint, token);
            return true;
        }
        string outgoingToken = TrustedDeviceStore.NewSecret();
        string reciprocalToken = TrustedDeviceStore.NewSecret();
        string payload;
        using (var ownIdentity = PersistentReceiverIdentity.LoadOrCreate(identityPath))
            payload = MutualTrustCodec.SignProposal(ownIdentity, _pinnedFingerprint, outgoingToken, reciprocalToken);
        var pending = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _pendingTrust, pending, null) != null)
            throw new InvalidOperationException("A trust request is already pending.");
        try
        {
            if (!TrySend(new Packet("trust-request", Text: payload))) return false;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(55));
            if (!await pending.Task.WaitAsync(deadline.Token))
                return false;
            trusted.TrustOutgoing(remoteName, _pinnedFingerprint, outgoingToken);
            trusted.AllowIncoming(remoteName, reciprocalToken, _pinnedFingerprint);
            trusted.AutoConnectEnabled = true;
            return true;
        }
        finally { Interlocked.CompareExchange(ref _pendingTrust, null, pending); }
    }

    internal async Task<bool> TryEnsureMutualTrustAsync(
        TrustedDeviceStore trusted, string remoteName, CancellationToken cancellation,
        string? identityPath = null)
    {
        if (Volatile.Read(ref _closed) != 0 || !_isTrustedConnection || !SupportsBidirectionalProtocol)
            return false;
        if (trusted.HasBidirectionalTrust(remoteName, _pinnedFingerprint))
            return true;
        if (Interlocked.CompareExchange(ref _mutualSyncStarted, 1, 0) != 0)
            return false;
        string existingOutgoing = trusted.FindAutoConnect(_pinnedFingerprint)?.Token ?? TrustedDeviceStore.NewSecret();
        string reciprocalToken = TrustedDeviceStore.NewSecret();
        var syncTcs = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _pendingMutualSync, syncTcs);
        try
        {
            string payload;
            using (var ownIdentity = PersistentReceiverIdentity.LoadOrCreate(identityPath))
                payload = MutualTrustCodec.SignProposal(ownIdentity, _pinnedFingerprint, reciprocalToken, existingOutgoing);
            if (!TrySend(new Packet("mutual-trust-sync", Text: payload)))
                return false;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(12));
            string? ackToken = await syncTcs.Task.WaitAsync(deadline.Token);
            if (!string.Equals(ackToken, reciprocalToken, StringComparison.OrdinalIgnoreCase))
                return false;
            trusted.AllowIncoming(remoteName, reciprocalToken, _pinnedFingerprint);
            trusted.AutoConnectEnabled = true;
            DiagnosticLog?.Invoke($"Two-way trusted pairing synchronized automatically with {remoteName}.");
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            Interlocked.CompareExchange(ref _pendingMutualSync, null, syncTcs);
        }
    }

    internal bool PublishSubPeers(IReadOnlyList<SubPeerDescriptor> peers)
    {
        if (Volatile.Read(ref _closed) != 0) return false;
        string payload = JsonSerializer.Serialize(peers);
        if (System.Text.Encoding.UTF8.GetByteCount(payload) > 7500) return false;
        return TrySend(new Packet("sub-peers", peers.Count, Text: payload));
    }

    internal bool SendSubReply(int localId, Packet inner)
    {
        if (Volatile.Read(ref _closed) != 0) return false;
        string payload = JsonSerializer.Serialize(new SubRouteEnvelope(localId, inner));
        return TrySend(new Packet("sub-reply", localId, Text: payload));
    }

    internal bool SendHubYieldInvite(int localMemberCount) =>
        Volatile.Read(ref _closed) == 0 &&
        TrySend(new Packet("hub-yield-invite", localMemberCount));

    private async Task HandleIncomingTrustOfferAsync(int offerId, string? payload)
    {
        bool accepted = false;
        string? counterPayload = null;
        try
        {
            if (MutualTrustCodec.TryVerifyProposal(payload, _pinnedFingerprint, out var proposal) &&
                proposal != null &&
                MutualTrustOfferRequested != null)
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);
                limit.CancelAfter(TimeSpan.FromSeconds(45));
                string displayName = _remoteSystemName ?? "Remote computer";
                string tokenForRemoteToLocal = TrustedDeviceStore.NewSecret();
                accepted = await MutualTrustOfferRequested(
                    displayName,
                    proposal.PeerFingerprint,
                    proposal.OutgoingToken,
                    tokenForRemoteToLocal,
                    limit.Token);
                if (accepted)
                {
                    using var ownIdentity = PersistentReceiverIdentity.LoadOrCreate();
                    counterPayload = MutualTrustCodec.SignProposal(
                        ownIdentity, _pinnedFingerprint, proposal.OutgoingToken, tokenForRemoteToLocal);
                }
            }
            else if (MutualTrustOfferRequested != null &&
                !string.IsNullOrWhiteSpace(payload) &&
                payload.Contains('\n'))
            {
                // Backward-compatible legacy trust-offer fallback.
                int nl = payload.IndexOf('\n');
                string fp = payload[..nl].Trim();
                string tok = payload[(nl + 1)..].Trim();
                if (fp.Equals(_pinnedFingerprint, StringComparison.OrdinalIgnoreCase) &&
                    tok.Length == 64 && tok.All(Uri.IsHexDigit) &&
                    MutualTrustOfferRequested != null)
                {
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);
                    limit.CancelAfter(TimeSpan.FromSeconds(45));
                    string displayName = _remoteSystemName ?? "Remote computer";
                    accepted = await MutualTrustOfferRequested(
                        displayName, fp, tok, "", limit.Token);
                }
            }
        }
        catch (Exception)
        {
            accepted = false;
        }
        TrySend(new Packet(accepted ? "trust-accepted" : "trust-denied", offerId, Text: counterPayload));
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
        bool validFixedSecret = secret.Length == (trustedConnection ? 64 : 32) && secret.All(Uri.IsHexDigit);
        bool validCustomCode = !trustedConnection && !string.IsNullOrEmpty(PairingCodeHelper.NormalizeCustomCode(secret));
        if (!askReceiver && !validFixedSecret && !validCustomCode)
            throw new ArgumentException("Invalid authenticated pairing credential.");
        var candidateAddresses = CrossNetworkRendezvous.ParseCandidateAddresses(ip);
        if (candidateAddresses.Count == 0)
            throw new ArgumentException("Enter the receiver's IPv4 or IPv6 address.");

        TcpClient? client = null;
        IPAddress address = candidateAddresses[0];
        SslStream? ssl = null;
        string stage = "TCP connection";
        try
        {
            Exception? lastConnectError = null;
            int perAttemptSeconds = candidateAddresses.Count > 1 ? 5 : 15;
            foreach (var candidateAddr in candidateAddresses)
            {
                var attemptClient = new TcpClient(candidateAddr.AddressFamily) { NoDelay = true };
                try
                {
                    if (candidateAddr.AddressFamily == AddressFamily.InterNetwork)
                        attemptClient.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService, 0x10);
                    else if (candidateAddr.AddressFamily == AddressFamily.InterNetworkV6)
                        attemptClient.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.TypeOfService, 0x10);
                }
                catch (Exception) { }
                try
                {
                    using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    attemptTimeout.CancelAfter(TimeSpan.FromSeconds(perAttemptSeconds));
                    await attemptClient.ConnectAsync(candidateAddr, Wire.Port, attemptTimeout.Token);
                    client = attemptClient;
                    address = candidateAddr;
                    break;
                }
                catch (Exception ex)
                {
                    lastConnectError = ex;
                    attemptClient.Dispose();
                }
            }
            if (client == null)
                throw lastConnectError ?? new SocketException((int)SocketError.HostUnreachable);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
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
            var mine = MonitorTopology.Capture().WithPrimaryAppVersion(MonitorTopology.CurrentAppVersion());
            using var identity = PersistentReceiverIdentity.LoadOrCreate();
            string sourceId = Convert.ToHexString(
                SHA256.HashData(identity.RawData));
            var (localCam, localMic) = WindowsMediaCaptureService.DetectLocalMediaCapabilities();
            var hello = JsonSerializer.Serialize(
                new PeerHello(Environment.MachineName, mine.ToWire(), true, sourceId, "Windows",
                    AppVersion: MonitorTopology.CurrentAppVersion(),
                    HasCamera: localCam,
                    HasMicrophone: localMic));
            await Wire.WriteAsync(ssl, new Packet("hello", Text: hello),
                acceptedLimit.Token);
            var (outBand, outSpeed) = ReceiverServer.DetectInterfaceForLocalAddress(
                client.Client.LocalEndPoint is IPEndPoint outEp ? ReceiverServer.UnmapAddress(outEp.Address) : null);
            if (outBand == "ethernet")
            {
                await Wire.WriteAsync(ssl, new Packet("link-info", Text: outBand, A: outSpeed), acceptedLimit.Token);
            }
            string[] hostMacs = SystemMediaPowerHelper.CollectLocalMacAddresses();
            if (hostMacs.Length > 0)
            {
                await Wire.WriteAsync(ssl, new Packet("mac-addresses", Text: string.Join(",", hostMacs)), acceptedLimit.Token);
            }
            return new HostSession(client, ssl, address, pinnedFingerprint, trustedConnection, monitors, onFailure, outBand, outSpeed);
        }
        catch (Exception ex)
        {
            ssl?.Dispose();
            client?.Dispose();
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

    internal void PublishNotificationCount(int count) =>
        TrySend(new Packet("notify-count", count >= 0 ? count : -1));

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
                else if (p.Type == "display-metrics")
                {
                    if (p.A is >= 24 and <= 360)
                    {
                        Volatile.Write(ref _liveRemoteRefreshHz, p.A);
                        UpdateAdaptivePacing();
                    }
                }
                else if (p.Type == "pong")
                {
                    long sent = Interlocked.Exchange(ref _lastPingSentTicks, 0);
                    if (sent > 0)
                    {
                        double rtt = (System.Diagnostics.Stopwatch.GetTimestamp() - sent) * 1000.0
                            / System.Diagnostics.Stopwatch.Frequency;
                        Volatile.Write(ref _lastRttMs, rtt);
                        Volatile.Read(ref _udpClient)?.LinkTracker.RecordRttSample(rtt, Environment.TickCount64);
                        UpdateAdaptivePacing();
                    }
                    HeartbeatAcknowledged?.Invoke();
                }
                else if (p.Type is "trust-granted" or "trust-denied")
                    Volatile.Read(ref _pendingTrust)?.TrySetResult(p.Type == "trust-granted");
                else if (p.Type == "mutual-trust-synced")
                    Volatile.Read(ref _pendingMutualSync)?.TrySetResult(p.Text);
                else if (p.Type == "trust-offer")
                    _ = HandleIncomingTrustOfferAsync(p.A, p.Text);
                else if (p.Type == "sub-route" && p.Text is { Length: > 1 and <= 4096 })
                {
                    try
                    {
                        var env = JsonSerializer.Deserialize<SubRouteEnvelope>(p.Text);
                        if (env != null && env.LocalId > 0)
                            SubRoutePacketReceived?.Invoke(env.LocalId, env.Payload, reply =>
                            {
                                SendSubReply(env.LocalId, reply);
                                return Task.CompletedTask;
                            });
                    }
                    catch { }
                }
                else if (p.Type == "android-keyboard-unavailable")
                    InputDeliveryWarning?.Invoke(
                        "Android has no supported focused text field. Tap an editable field on the phone, then type. Check DeskTouch Accessibility if paused.");
                else if (p.Type == "keyboard-unavailable")
                    InputDeliveryWarning?.Invoke("The receiving Windows PC rejected keyboard " +
                        "input. Click a normal text field on that PC and check whether " +
                        "the target program is running as Administrator.");
                else if (p.Type == "link-info")
                {
                    string band = AdaptiveLinkTracker.NormalizeBand(p.Text);
                    int speedMbps = p.A > 0 ? p.A : 0;
                    if (band == "ethernet")
                    {
                        _linkBand = band;
                        if (speedMbps > 0) _linkSpeedMbps = speedMbps;
                        Volatile.Read(ref _udpClient)?.LinkTracker.OnLinkProfileChanged(band, speedMbps, 0);
                    }
                }
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
                else if (p.Type == "taskbar-watch")
                {
                    bool watch = p.A != 0;
                    _taskbarWatched = watch;
                    if (watch)
                    {
                        _lastHostTaskbarSig = "";
                        _sentHostTaskbarIcons.Clear();
                        Interlocked.Exchange(ref _hostTaskbarNudge, 1);
                    }
                }
                else if (p.Type == "taskbar-action" && WindowsTaskbarHelper.IsValidActionKeyFormat(p.Text))
                {
                    string actionKey = p.Text!;
                    _ = Task.Run(() =>
                    {
                        WindowsTaskbarHelper.ExecuteAction(actionKey);
                        Thread.Sleep(180);
                        Interlocked.Exchange(ref _hostTaskbarNudge, 1);
                    });
                }
                else if (p.Type == "clipboard-text" && p.Text is { Length: > 2 and <= 7800 })
                {
                    var parsedCb = SharedClipboardHelper.ParseClipboardTextPacket(p.Text);
                    if (parsedCb is { } cb)
                        ClipboardTextReceived?.Invoke(cb.Hash, cb.Text);
                }
                else if (p.Type == "clipboard-img-chunk" && p.Text is { Length: > 2 and <= 7800 })
                {
                    var imgDone = _hostFileDrop.AcceptClipboardImgChunk(p.A, p.B, p.C, p.Text);
                    if (imgDone is { } img)
                        ClipboardImageReceived?.Invoke(img.Hash, img.Png);
                }
                else if (p.Type == "file-offer")
                {
                    if (!_hostFileDrop.BeginInbound(p.D, p.A, p.B, p.Text, out string? err))
                        TrySend(new Packet("file-reject", p.A, Text: err ?? "Rejected"));
                }
                else if (p.Type == "file-chunk")
                {
                    _ = _hostFileDrop.AppendInboundChunk(p.D, p.A, p.Text);
                }
                else if (p.Type == "file-done")
                {
                    string? saved = _hostFileDrop.CompleteInbound(p.D, p.A);
                    if (!string.IsNullOrEmpty(saved))
                        FileReceived?.Invoke(_remoteSystemName ?? "Connected device", saved);
                }
                else if (p.Type == "media-action" && SystemMediaPowerHelper.IsValidMediaAction(p.Text))
                {
                    _ = Task.Run(() => SystemMediaPowerHelper.ExecuteMediaAction(p.Text, p.B));
                }
                else if (p.Type == "power-action" && SystemMediaPowerHelper.IsValidPowerAction(p.Text))
                {
                    _ = Task.Run(() => SystemMediaPowerHelper.ExecutePowerAction(p.Text));
                }
                else if (p.Type == "screen-preview-watch")
                {
                    Volatile.Write(ref _hostScreenPreviewMonitor, p.B);
                    Volatile.Write(ref _hostScreenPreviewHighRes, p.C != 0 ? 1 : 0);
                    if (p.B >= 0 && _hostPreviewWake.CurrentCount == 0)
                    {
                        try { _hostPreviewWake.Release(); } catch { }
                    }
                }
                else if (p.Type == "phone-camera-watch")
                {
                    Volatile.Write(ref _hostCamWatchActive, p.A != 0 ? 1 : 0);
                    Volatile.Write(ref _hostMicWatchActive, p.D != 0 ? 1 : 0);
                    SyncHostMediaCapture(p.B);
                }
                else if (p.Type == "phone-mic-watch")
                {
                    Volatile.Write(ref _hostMicWatchActive, p.A != 0 ? 1 : 0);
                    SyncHostMediaCapture(0);
                }
                else if (p.Type == "phone-camera-status")
                {
                    RemoteCameraStatusReceived?.Invoke(p.B, p.A, p.Text);
                }
                else if (p.Type == "phone-mic-status")
                {
                    RemoteMicStatusReceived?.Invoke(p.B, p.A, p.Text);
                }
                else if (p.Type == "phone-camera-chunk" && !string.IsNullOrEmpty(p.Text))
                {
                    var assembled = _hostCameraAssembler.AddChunk((p.D << 16) ^ (p.A & 0xFFFF), p.B, p.C, p.Text);
                    if (assembled.Complete && assembled.Data.Length > 0)
                        RemoteCameraFrameReceived?.Invoke(p.D, assembled.Data);
                }
                else if (p.Type == "phone-mic-audio" && !string.IsNullOrEmpty(p.Text))
                {
                    try
                    {
                        byte[] pcm = Convert.FromBase64String(p.Text);
                        if (pcm.Length > 0)
                            RemoteMicAudioReceived?.Invoke(p.D, pcm, p.B > 0 ? p.B : 16000, p.A);
                    }
                    catch { }
                }
                else if (p.Type == "notif-preview-state" && p.Text is { Length: >= 2 and <= 7800 })
                {
                    var list = DeviceTelemetryAndNotifModel.ParseNotificationList(p.Text);
                    lock (_hostNotifGate)
                        _hostPeerNotifications[p.A] = list;
                    PeerNotificationsUpdated?.Invoke(p.A, list);
                }
                else if (p.Type == "notif-dismiss" && !string.IsNullOrWhiteSpace(p.Text))
                {
                    WindowsNotificationCounter.DismissLocalNotification(p.Text);
                }
                else if (p.Type == "otp-offer" && !string.IsNullOrWhiteSpace(p.Text))
                {
                    try
                    {
                        var offer = JsonSerializer.Deserialize<OtpOfferPayload>(p.Text);
                        if (offer != null && !string.IsNullOrWhiteSpace(offer.Code))
                            OtpOfferReceived?.Invoke(offer.App, offer.Code, offer.Title);
                    }
                    catch { }
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
        var client = Volatile.Read(ref _udpClient);
        bool udpConfirmed = client?.Confirmed == true;
        double rtt = (client?.LinkTracker.SmoothedRttMs ?? 0) > 0
            ? client!.LinkTracker.SmoothedRttMs
            : Volatile.Read(ref _lastRttMs);
        double jitter = client?.LinkTracker.EffectiveJitterMs ?? 0;
        double loss = client?.LinkTracker.CombinedLossPermille ?? 0;
        int liveHz = Volatile.Read(ref _liveRemoteRefreshHz);
        if (liveHz < 24)
            liveHz = Volatile.Read(ref _remoteMonitors).Monitors.FirstOrDefault()?.RefreshHz ?? 0;
        double paceMs = pacer?.EffectiveMinIntervalMs ?? 2.5;
        long lateTotal = client?.LinkTracker.LatePackets ?? 0;
        long reorderedTotal = client?.LinkTracker.ReorderedPackets ?? 0;
        DiagnosticLog?.Invoke(
            $"Telemetry [out->{_remoteSystemName ?? "hub"}]: fast_confirmed={udpConfirmed} live_hz={liveHz} pace_ms={paceMs:F1} rtt_ms={rtt:F1} jitter_ms={jitter:F1} loss_pct={loss / 10.0:F2} fast_direct={s.UdpDirect} fast_paced={s.UdpPacedFlush} trailing_sync={s.TrailingTcpSync} tcp_fallback={tcpFallback} late_total={lateTotal} reordered_total={reorderedTotal}");
    }

    private async Task HeartbeatAsync()
    {
        try
        {
            int tick = 0;
            bool loggedInitial = false;
            while (!_cancel.IsCancellationRequested)
            {
                await Task.Delay(100, _cancel.Token);
                if (!loggedInitial)
                {
                    loggedInitial = true;
                    EmitEnvironmentVersionsDiagnostic();
                }

                var client = Volatile.Read(ref _udpClient);
                if (client is { Confirmed: true })
                {
                    long nowMs = Environment.TickCount64;
                    bool controlActive = Volatile.Read(ref _forwardClaimLease) > 0 ||
                        Volatile.Read(ref _reverseLease) > 0;
                    long probeGap = client.LinkTracker.ComputeProbeIntervalMs(controlActive);
                    if (nowMs - client.LastLinkPingMs >= probeGap)
                    {
                        client.LastLinkPingMs = nowMs;
                        int paceTenths = (int)Math.Round((_movePacer?.EffectiveMinIntervalMs ?? 2.5) * 10.0);
                        int lease = Volatile.Read(ref _forwardClaimLease);
                        if (lease <= 0) lease = Volatile.Read(ref _reverseLease);
                        client.Send(FastUdpOp.LinkPing,
                            client.LinkTracker.NextPingToken(nowMs), paceTenths, lease);
                    }
                }

                tick++;
                if ((tick % 15) == 14)
                {
                    if (Interlocked.Read(ref _lastPingSentTicks) == 0)
                        Volatile.Write(ref _lastPingSentTicks, System.Diagnostics.Stopwatch.GetTimestamp());
                    if (!TrySend(new Packet("ping"))) break;
                }
                if ((tick % 30) == 29)
                    EmitTelemetryIfActive();
                if (_taskbarWatched && (Interlocked.Exchange(ref _hostTaskbarNudge, 0) != 0 || (tick % 10) == 0))
                {
                    var capture = WindowsTaskbarHelper.CaptureSnapshot(0);
                    if (!string.Equals(capture.Signature, _lastHostTaskbarSig, StringComparison.Ordinal))
                    {
                        if (_sentHostTaskbarIcons.Count > 128)
                            _sentHostTaskbarIcons.Clear();
                        foreach (var kv in capture.IconsByHash)
                        {
                            if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrEmpty(kv.Value) &&
                                _sentHostTaskbarIcons.Add(kv.Key))
                            {
                                string iconPayload = kv.Key + "|" + kv.Value;
                                if (System.Text.Encoding.UTF8.GetByteCount(iconPayload) <= 7800)
                                    TrySend(new Packet("taskbar-icon", Text: iconPayload));
                            }
                        }
                        if (System.Text.Encoding.UTF8.GetByteCount(capture.StateJson) <= 7800)
                        {
                            TrySend(new Packet("taskbar-state", 0, Text: capture.StateJson));
                            _lastHostTaskbarSig = capture.Signature;
                        }
                    }
                }
                if ((tick % 8) == 0)
                {
                    var (fgProc, fgTitle) = SystemMediaPowerHelper.GetForegroundAppInfo();
                    string fgSig = fgProc + "|" + (fgTitle.Length > 60 ? fgTitle[..60] : fgTitle);
                    if (!string.IsNullOrEmpty(fgProc) && !string.Equals(fgSig, _lastHostForegroundSig, StringComparison.Ordinal))
                    {
                        _lastHostForegroundSig = fgSig;
                        TrySend(new Packet("foreground-app", 0, Text: fgSig));
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task HostScreenPreviewLoopAsync()
    {
        try
        {
            while (!_cancel.IsCancellationRequested && Volatile.Read(ref _closed) == 0)
            {
                int previewMon = Volatile.Read(ref _hostScreenPreviewMonitor);
                if (previewMon < 0)
                {
                    try { await _hostPreviewWake.WaitAsync(1000, _cancel.Token); }
                    catch (ObjectDisposedException) { return; }
                    continue;
                }
                bool highRes = Volatile.Read(ref _hostScreenPreviewHighRes) != 0;
                var shot = ScreenPreviewHelper.CaptureMonitorPreviewRaw(previewMon, highRes);
                bool usedUdp = false;
                if (shot.JpegBytes != null && shot.JpegBytes.Length > 0)
                {
                    var client = Volatile.Read(ref _udpClient);
                    if (client is { Confirmed: true })
                    {
                        ReceiverServer.SendUdpClientMediaChunks(client, FastUdpMediaKind.PcScreenPreviewChunk,
                            shot.FrameId, shot.JpegBytes, shot.PackedDims);
                        usedUdp = true;
                    }
                    else
                    {
                        const int chunkSize = 3600;
                        int total = (shot.JpegBytes.Length + chunkSize - 1) / chunkSize;
                        for (int i = 0; i < total; i++)
                        {
                            if (Volatile.Read(ref _hostScreenPreviewMonitor) < 0 || Volatile.Read(ref _closed) != 0) break;
                            int offset = i * chunkSize;
                            int len = Math.Min(chunkSize, shot.JpegBytes.Length - offset);
                            string b64 = Convert.ToBase64String(shot.JpegBytes, offset, len);
                            if (!TrySend(new Packet("screen-preview-chunk", shot.FrameId, i, total, shot.PackedDims, b64)))
                                break;
                        }
                    }
                }
                await Task.Delay(usedUdp ? 65 : 220, _cancel.Token);
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
            var client = new FastUdpClient(_serverAddress, serverPort, peerId, key, _linkBand, _linkSpeedMbps);
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
                else if (msg.Op == FastUdpOp.LinkPing)
                {
                    client.Send(FastUdpOp.LinkFeedback, msg.A,
                        client.LinkTracker.GetInboundPackedLossJitter(), 0, payloadTag: 0);
                    UpdateAdaptivePacing();
                }
                else if (msg.Op == FastUdpOp.LinkFeedback)
                {
                    if (client.LinkTracker.PeerRefreshHz is >= 24 and <= 360)
                        Volatile.Write(ref _liveRemoteRefreshHz, client.LinkTracker.PeerRefreshHz);
                    UpdateAdaptivePacing();
                }
                else if (msg.Op == FastUdpOp.ReverseClaim)
                {
                    if (msg.A > 0 && msg.Lease == msg.A)
                        ProcessReverseInput(new Packet("reverse-claim", msg.A, C: msg.TimestampMs));
                }
                else if (msg.Op == FastUdpOp.ReverseMove)
                {
                    int active = Volatile.Read(ref _reverseLease);
                    if (active > 0 && msg.Lease == active)
                    {
                        var (_, currX) = FastMovePacer.UnpackDualCoord(msg.A);
                        var (_, currY) = FastMovePacer.UnpackDualCoord(msg.B);
                        ProcessReverseInput(new Packet("reverse-move", currX, currY));
                    }
                }
                else if (msg.Op == FastUdpOp.ReverseEdgePush)
                {
                    int active = Volatile.Read(ref _reverseLease);
                    if (active > 0 && msg.Lease == active)
                    {
                        int currX = (short)(msg.A & 0xFFFF);
                        int currY = (short)(msg.B & 0xFFFF);
                        ProcessReverseInput(new Packet("reverse-move", currX, currY));
                    }
                }
                else if (msg.Op == FastUdpOp.ReverseButton)
                {
                    int active = Volatile.Read(ref _reverseLease);
                    if (active > 0 && msg.Lease == active)
                        ProcessReverseInput(new Packet("reverse-button", msg.A, msg.B, C: msg.TimestampMs));
                }
                else if (msg.Op == FastUdpOp.ReverseWheel)
                {
                    int active = Volatile.Read(ref _reverseLease);
                    if (active > 0 && msg.Lease == active)
                        ProcessReverseInput(new Packet("reverse-wheel", msg.A, msg.B, C: msg.TimestampMs));
                }
                else if (msg.Op == FastUdpOp.ReverseKey)
                {
                    int active = Volatile.Read(ref _reverseLease);
                    if (active > 0 && msg.Lease == active)
                    {
                        int vk = msg.A & 0xFF;
                        int scan = (msg.A >> 8) & 0xFF;
                        int ext = (msg.A >> 16) & 1;
                        int down = msg.B != 0 ? 1 : 0;
                        ProcessReverseInput(new Packet("reverse-key", scan, ext, down, vk,
                            Text: msg.TimestampMs > 0
                                ? msg.TimestampMs.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                : null));
                    }
                }
            };
            client.MediaDatagramReceived += media =>
            {
                if (Volatile.Read(ref _closed) != 0) return;
                int sourcePeerId = media.MetaOrRouteId;
                if (media.Kind == FastUdpMediaKind.MicAudio)
                {
                    if (media.Payload.Length == 0) return;
                    int sr = media.IndexOrSampleRate is >= 8000 and <= 48000 ? media.IndexOrSampleRate : 16000;
                    RemoteMicAudioReceived?.Invoke(sourcePeerId, media.Payload, sr, media.FrameOrAudioId);
                }
                else if (media.Kind == FastUdpMediaKind.CameraFrameChunk)
                {
                    if (_hostMediaAssembler.AddChunk(sourcePeerId, (byte)media.Kind,
                        media.FrameOrAudioId, media.IndexOrSampleRate, media.TotalOrChannels,
                        sourcePeerId, media.Payload, out byte[] frame, out _))
                    {
                        RemoteCameraFrameReceived?.Invoke(sourcePeerId, frame);
                    }
                }
            };
            Interlocked.Exchange(ref _highResTimer, Win.EnterHighResolutionTimer())?.Dispose();
            UpdateAdaptivePacing();
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
        Volatile.Write(ref _hostCamWatchActive, 0);
        Volatile.Write(ref _hostMicWatchActive, 0);
        Volatile.Write(ref _hostCamStatus, 0);
        Volatile.Write(ref _hostMicStatus, 0);
        try { LocalMediaShareStateChanged?.Invoke(false, false, ""); } catch { }
        Interlocked.Exchange(ref _pendingTrust, null)?.TrySetResult(false);
        _cancel.Cancel();
        try { _hostPreviewWake.Release(); } catch { }
        try { _hostMediaCapture?.Dispose(); } catch { }
        try { _hostPreviewWake.Dispose(); } catch { }
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
