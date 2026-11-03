using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace DeskTouch.DeviceShare;

/// <summary>
/// One physical Windows input authority, many simultaneously authenticated
/// TLS sessions. Two controllers never inject conflicting keyboard/mouse
/// events into the same desktop. Physical activity always revokes all leases.
/// </summary>
internal sealed class SharedPeerAuthority
{
    private readonly object _gate = new();
    private readonly LocalInputPriority _priority;
    private long _owner;
    internal SharedPeerAuthority(LocalInputPriority priority) => _priority = priority;
    internal long Owner { get { lock (_gate) return _owner; } }
    internal bool TryClaim(long sessionId)
    {
        if (sessionId <= 0) return false;
        lock (_gate)
        {
            if (_owner != 0 || !_priority.TryClaim()) return false;
            _owner = sessionId;
            return true;
        }
    }
    internal bool IsOwner(long sessionId)
    {
        lock (_gate) return sessionId > 0 && _owner == sessionId;
    }
    internal void Release(long sessionId)
    {
        lock (_gate)
        {
            if (_owner != sessionId || sessionId <= 0) return;
            _owner = 0;
            _priority.ReleaseRemote();
        }
    }
    internal void ReleaseAll()
    {
        lock (_gate)
        {
            _owner = 0;
            _priority.ReleaseRemote();
        }
    }
}

/// <summary>
/// Bounded multi-peer acceptor for Windows and Android. The original
/// PeerReceiverSession protocol is reused for each client, so TLS identity,
/// approval, watchdog, reverse handoff, and input safety remain unchanged.
/// A selected peer is only the *outgoing* handoff target; all other peers stay
/// connected and may request input when the exclusive lease is available.
/// </summary>
internal sealed class ReceiverServer : IInputSession, IDisposable
{
    internal const int MaxConcurrentDevices = 8;
    private readonly CancellationTokenSource _stop = new();
    private readonly TcpListener _listener = new(IPAddress.Any, Wire.Port);
    private readonly ConcurrentDictionary<long, PeerReceiverSession> _sessions = new();
    private readonly ConcurrentDictionary<long, ConnectedPeer> _connected = new();
    private readonly LocalInputPriority _priority;
    private readonly SharedPeerAuthority _authority;
    private readonly Action<string> _status;
    private readonly Func<string, CancellationToken, Task<bool>>? _approval;
    private readonly Func<string, CancellationToken, Task<bool>>? _trustApproval;
    private readonly TrustedDeviceStore? _trusted;
    private readonly string? _identityPath;
    private long _lastId;
    private long _selectedId;
    private int _stopped;

    internal Func<bool>? AllowForwardClaim;
    internal event Action<Packet>? ReverseControlMessage;
    internal event Action<ConnectedPeer>? PeerConnected;
    internal event Action<long>? PeerDisconnected;
    internal event Action<ConnectedPeer>? PeerTopologyChanged;
    internal event Action<MonitorEdge>? PeerPlacementChanged;

    internal string Fingerprint { get; }
    internal string Secret { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    internal int ConnectedCount => _connected.Count;
    internal int SessionCount => _sessions.Count;
    internal long SelectedPeerId => Interlocked.Read(ref _selectedId);
    internal IReadOnlyList<ConnectedPeer> ConnectedPeers =>
        _connected.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    internal ReceiverServer(Action<string> status, LocalInputPriority priority,
        Func<string, CancellationToken, Task<bool>>? requestApproval = null,
        TrustedDeviceStore? trusted = null,
        Func<string, CancellationToken, Task<bool>>? requestTrustApproval = null,
        string? identityPath = null)
    {
        _status = status;
        _priority = priority;
        _authority = new SharedPeerAuthority(priority);
        _approval = requestApproval;
        _trusted = trusted;
        _trustApproval = requestTrustApproval;
        _identityPath = identityPath;
        // Never use hostname or IP as identity. The same persistent certificate
        // is used in every concurrent child TLS connection.
        using var certificate = PersistentReceiverIdentity.LoadOrCreate(identityPath);
        Fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));
    }

    internal bool CanAcceptNewPeer
    {
        get
        {
            if (_stop.IsCancellationRequested || Volatile.Read(ref _stopped) != 0 ||
                SessionCount >= MaxConcurrentDevices) return false;
            try { return _listener.Server.IsBound; }
            catch (ObjectDisposedException) { return false; }
        }
    }

    internal void Start()
    {
        _listener.Start(MaxConcurrentDevices);
        _ = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient? socket = null;
            try
            {
                socket = await _listener.AcceptTcpClientAsync(_stop.Token);
                if (SessionCount >= MaxConcurrentDevices)
                {
                    socket.Close();
                    continue;
                }
                socket.NoDelay = true;
                var id = Interlocked.Increment(ref _lastId);
                var peer = new PeerReceiverSession(_status, _priority, _approval,
                    _trusted, _trustApproval, _identityPath, id, _authority, Secret);
                peer.AllowForwardClaim = () => AllowForwardClaim?.Invoke() ?? true;
                peer.PeerConnected += connected =>
                {
                    _connected[id] = connected;
                    // First authenticated peer becomes selected. Subsequent
                    // connections never silently steal another device's view.
                    Interlocked.CompareExchange(ref _selectedId, id, 0);
                    PeerConnected?.Invoke(connected);
                };
                peer.PeerDisconnected += disconnected =>
                {
                    _connected.TryRemove(disconnected, out _);
                    if (SelectedPeerId == disconnected)
                        Interlocked.CompareExchange(ref _selectedId, 0, disconnected);
                    PeerDisconnected?.Invoke(disconnected);
                };
                peer.PeerTopologyChanged += topology =>
                {
                    _connected[id] = topology;
                    PeerTopologyChanged?.Invoke(topology);
                };
                peer.PeerPlacementChanged += edge =>
                {
                    if (SelectedPeerId == id) PeerPlacementChanged?.Invoke(edge);
                };
                peer.ReverseControlMessage += packet =>
                {
                    if (SelectedPeerId == id) ReverseControlMessage?.Invoke(packet);
                };
                if (!_sessions.TryAdd(id, peer))
                {
                    peer.Dispose();
                    socket.Close();
                    continue;
                }
                var acceptedSocket = socket;
                socket = null;
                _ = Task.Run(async () =>
                {
                    using (acceptedSocket)
                    {
                        try { await peer.ServeClientAsync(acceptedSocket, _stop.Token); }
                        catch (OperationCanceledException) { }
                        catch (Exception ex)
                        {
                            _status("Device connection ended: " + ex.Message);
                        }
                        finally
                        {
                            _sessions.TryRemove(id, out _);
                            peer.Dispose();
                        }
                    }
                });
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { _status("LAN accept failed: " + ex.Message); }
            finally { socket?.Dispose(); }
        }
    }

    internal bool SelectPeer(long peerId)
    {
        if (peerId <= 0 || !_connected.ContainsKey(peerId)) return false;
        Interlocked.Exchange(ref _selectedId, peerId);
        return true;
    }

    private PeerReceiverSession? Selected =>
        _sessions.TryGetValue(SelectedPeerId, out var peer) &&
        _connected.ContainsKey(SelectedPeerId) ? peer : null;

    internal MonitorTopology RemoteMonitors =>
        Selected?.RemoteMonitors ??
        throw new InvalidOperationException("Select an authenticated device first.");
    MonitorTopology IInputSession.RemoteMonitors => RemoteMonitors;
    bool IInputSession.TrySend(Packet packet) => TrySendReverse(packet);
    internal bool TrySendReverse(Packet packet) => Selected?.TrySendReverse(packet) == true;
    internal void PublishPlacement(MonitorEdge edge) => Selected?.PublishPlacement(edge);
    internal void PublishLocalTopology(MonitorTopology local)
    {
        foreach (var (id, peer) in _sessions)
            if (_connected.ContainsKey(id)) peer.PublishLocalTopology(local);
    }
    internal void DisconnectPeer() => Selected?.DisconnectPeer();
    internal void DisconnectPeer(long peerId)
    {
        if (_sessions.TryGetValue(peerId, out var peer)) peer.DisconnectPeer();
    }
    internal void DisconnectAllPeers()
    {
        foreach (var peer in _sessions.Values) peer.DisconnectPeer();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _stop.Cancel();
        _listener.Stop();
        DisconnectAllPeers();
        _authority.ReleaseAll();
        // Cancellation lifetime is held until all child socket loops terminate.
    }
}
