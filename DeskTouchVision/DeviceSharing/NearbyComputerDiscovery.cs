using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace DeskTouch.DeviceShare;

/// <summary>Discovery-only information. The advertised fingerprint is never
/// proof of authorization. TLS pairing must still verify it and obtain consent.</summary>
internal sealed record NearbyComputer(string Name, string Address, string Fingerprint,
    bool IsAndroid = false, bool CanReceive = true);

/// <summary>
/// Persistent LAN discovery. Respond while paired/busy too, so two PCs and
/// Android phones remain visible in Add devices. A full-capacity PC is clearly marked
/// rather than silently disappearing. No IP address is remembered as identity.
/// </summary>
internal sealed class NearbyComputerDiscovery : IDisposable
{
    internal const int DiscoveryPort = 37378;
    private const string Probe = "DT1-DISCOVER";
    private const string ReadyPrefix = "DT1-READY|";
    private const string BusyPrefix = "DT1-BUSY|";
    private const string MobilePrefix = "DT1-MOBILE|";
    private readonly CancellationTokenSource _stop = new();
    private readonly string _fingerprint;
    private readonly Func<bool> _canAcceptConnections;
    private UdpClient? _listener;
    private Task? _loop;

    internal NearbyComputerDiscovery(string fingerprint, Func<bool>? canAcceptConnections = null)
    {
        if (fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit))
            throw new ArgumentException("A stable TLS fingerprint is required.");
        _fingerprint = fingerprint;
        _canAcceptConnections = canAcceptConnections ?? (() => true);
    }

    private static bool IsPrivateLan(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var x = address.GetAddressBytes();
        return x[0] == 10 || (x[0] == 172 && x[1] is >= 16 and <= 31) ||
            (x[0] == 192 && x[1] == 168) || (x[0] == 169 && x[1] == 254);
    }

    internal void Start()
    {
        _listener = new UdpClient(new IPEndPoint(IPAddress.Any, DiscoveryPort))
        {
            EnableBroadcast = true
        };
        _loop = Task.Run(ReplyLoopAsync);
    }

    private async Task ReplyLoopAsync()
    {
        var name = Environment.MachineName.Replace("|", "-");
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var msg = await _listener!.ReceiveAsync(_stop.Token);
                if (!IsPrivateLan(msg.RemoteEndPoint.Address)) continue;
                if (msg.Buffer.Length != Probe.Length ||
                    Encoding.ASCII.GetString(msg.Buffer) != Probe) continue;

                // IMPORTANT: discovery must survive a TCP session. While a PC
                // controls another PC, report it as busy, not as a missing PC.
                bool accepting = false;
                try { accepting = _canAcceptConnections(); }
                catch (ObjectDisposedException) { }
                var prefix = accepting ? ReadyPrefix : BusyPrefix;
                byte[] reply = Encoding.ASCII.GetBytes(prefix + name + "|" + _fingerprint);
                await _listener.SendAsync(reply, msg.RemoteEndPoint, _stop.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { /* adapter may have changed */ }
            catch (IOException) { }
        }
    }

    internal static NearbyComputer? ParseReply(string response, IPAddress address)
    {
        if (!IsPrivateLan(address) || response.Length is < 75 or > 260) return null;
        bool isMobile = response.StartsWith(MobilePrefix, StringComparison.Ordinal);
        bool busy = response.StartsWith(BusyPrefix, StringComparison.Ordinal);
        if (!isMobile && !busy &&
            !response.StartsWith(ReadyPrefix, StringComparison.Ordinal)) return null;
        string prefix = isMobile ? MobilePrefix : busy ? BusyPrefix : ReadyPrefix;
        var parts = response[prefix.Length..].Split('|');
        if (parts.Length != 2 || parts[0].Length is < 1 or > 100 ||
            parts[0].Any(char.IsControl) ||
            parts[1].Length != 64 || !parts[1].All(Uri.IsHexDigit)) return null;
        // Android currently initiates outgoing TLS toward Windows. It must
        // never be presented as a Windows TCP receiver on port 37377.
        return new NearbyComputer(parts[0], address.ToString(),
            parts[1].ToUpperInvariant(), isMobile, !busy && !isMobile);
    }

    internal static IReadOnlyList<NearbyComputer> DistinctDevices(IEnumerable<NearbyComputer> peers) =>
        peers.GroupBy(p => (p.IsAndroid, p.Fingerprint), 
                EqualityComparer<(bool, string)>.Default)
            .Select(g => g.OrderByDescending(p => p.CanReceive).First())
            .OrderBy(p => p.IsAndroid)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Address, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    internal static async Task<IReadOnlyList<NearbyComputer>> FindAsync(CancellationToken cancel)
    {
        var found = new ConcurrentDictionary<string, NearbyComputer>(StringComparer.OrdinalIgnoreCase);
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            IPAddress.Broadcast.ToString()
        };
        foreach (var net in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (net.OperationalStatus != OperationalStatus.Up ||
                net.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var addr in net.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork ||
                    addr.IPv4Mask == null || !IsPrivateLan(addr.Address)) continue;
                byte[] a = addr.Address.GetAddressBytes(), mask = addr.IPv4Mask.GetAddressBytes();
                if (mask.Length != 4) continue;
                var broadcast = new byte[4];
                for (int i = 0; i < 4; i++)
                    broadcast[i] = (byte)(a[i] | (~mask[i] & 255));
                destinations.Add(new IPAddress(broadcast).ToString());
            }
        }
        byte[] probe = Encoding.ASCII.GetBytes(Probe);
        // Retry two cycles, not just one shot. Both Wi-Fi broadcast filtering
        // and waking Android phones can discard the first datagram.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(3700));
        var sendAt = DateTime.UtcNow;
        int attempts = 0;
        while (!deadline.IsCancellationRequested)
        {
            if (attempts < 3 && DateTime.UtcNow >= sendAt)
            {
                attempts++;
                sendAt = DateTime.UtcNow.AddMilliseconds(900);
                foreach (string host in destinations)
                {
                    try { await udp.SendAsync(probe,
                        new IPEndPoint(IPAddress.Parse(host), DiscoveryPort), deadline.Token); }
                    catch (SocketException) { /* adapter not available */ }
                    catch (OperationCanceledException) { break; }
                }
            }
            using var slice = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            slice.CancelAfter(TimeSpan.FromMilliseconds(300));
            try
            {
                var packet = await udp.ReceiveAsync(slice.Token);
                var peer = ParseReply(Encoding.ASCII.GetString(packet.Buffer),
                    packet.RemoteEndPoint.Address);
                if (peer is not null)
                    found[(peer.IsAndroid ? "android:" : "windows:") + peer.Fingerprint] = peer;
            }
            catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { break; }
        }
        return DistinctDevices(found.Values);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener?.Dispose();
    }
}
