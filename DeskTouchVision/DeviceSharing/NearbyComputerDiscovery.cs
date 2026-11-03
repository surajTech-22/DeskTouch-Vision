using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace DeskTouch.DeviceShare;

internal sealed record NearbyComputer(string Name, string Address, string Fingerprint);

/// <summary>
/// LAN-only peer detection. Broadcast responses expose machine name and the
/// ephemeral TLS certificate fingerprint; NEVER advertise pairing secrets.
/// Pairing still requires explicit consent on the receiving Windows computer.
/// </summary>
internal sealed class NearbyComputerDiscovery : IDisposable
{
    internal const int DiscoveryPort = 37378;
    private const string Probe = "DT1-DISCOVER";
    private const string ReplyPrefix = "DT1-READY|";
    private readonly CancellationTokenSource _stop = new();
    private readonly string _fingerprint;
    private readonly Func<bool> _canAcceptConnections;
    private UdpClient? _listener;
    private Task? _loop;

    internal NearbyComputerDiscovery(string fingerprint, Func<bool>? canAcceptConnections = null)
    {
        if (fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit))
            throw new ArgumentException("A pinned SHA-256 certificate is required.");
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
        var reply = Encoding.ASCII.GetBytes(ReplyPrefix +
            Environment.MachineName.Replace("|", "-") + "|" + _fingerprint);
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var msg = await _listener!.ReceiveAsync(_stop.Token);
                if (!IsPrivateLan(msg.RemoteEndPoint.Address) || !_canAcceptConnections()) continue;
                if (msg.Buffer.Length != Probe.Length ||
                    Encoding.ASCII.GetString(msg.Buffer) != Probe) continue;
                await _listener.SendAsync(reply, msg.RemoteEndPoint, _stop.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { /* adapter may have changed */ }
            catch (IOException) { }
        }
    }

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
        var query = Encoding.ASCII.GetBytes(Probe);
        foreach (string host in destinations)
        {
            try { await udp.SendAsync(query,
                new IPEndPoint(IPAddress.Parse(host), DiscoveryPort), cancel); }
            catch (SocketException) { /* unavailable adapter */ }
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(1750));
        while (!deadline.IsCancellationRequested)
        {
            try
            {
                var response = await udp.ReceiveAsync(deadline.Token);
                if (!IsPrivateLan(response.RemoteEndPoint.Address) ||
                    response.Buffer.Length is < 75 or > 260) continue;
                string data = Encoding.ASCII.GetString(response.Buffer);
                if (!data.StartsWith(ReplyPrefix, StringComparison.Ordinal)) continue;
                var parts = data[ReplyPrefix.Length..].Split('|');
                if (parts.Length != 2 || parts[0].Length is < 1 or > 100 ||
                    parts[1].Length != 64 || !parts[1].All(Uri.IsHexDigit)) continue;
                var address = response.RemoteEndPoint.Address.ToString();
                found[address] = new NearbyComputer(parts[0], address,
                    parts[1].ToUpperInvariant());
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { break; }
        }
        return found.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener?.Dispose();
        // The receive task ends on cancellation; leave token source alive
        // briefly to avoid racing async callbacks during shutdown.
    }
}
