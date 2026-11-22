using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeskTouch.DeviceShare;

internal static class PairingCodeHelper
{
    private const string DefaultAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    internal static string PairingCodeFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeskTouchVision", "pairing-code.txt");

    internal static string NormalizeCustomCode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";
        string clean = raw.Trim();
        if (clean.Length is < 4 or > 32)
            return "";
        foreach (char c in clean)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'))
                return "";
        }
        return clean.ToUpperInvariant();
    }

    internal static string ComputeDiscoveryTag(string? fingerprint, string? rawCode)
    {
        string fp = (fingerprint ?? "").Trim().ToUpperInvariant();
        string code = NormalizeCustomCode(rawCode);
        if (fp.Length != 64 || code.Length == 0)
            return "";
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes($"DT1-PAIRTAG-V1|{fp}|{code}"));
        return Convert.ToHexString(digest)[..24];
    }

    internal static string GenerateDefaultCode(int length = 8)
    {
        Span<char> chars = stackalloc char[length];
        for (int i = 0; i < length; i++)
            chars[i] = DefaultAlphabet[RandomNumberGenerator.GetInt32(DefaultAlphabet.Length)];
        return new string(chars);
    }

    internal static string LoadOrCreateCode()
    {
        try
        {
            string path = PairingCodeFilePath;
            if (File.Exists(path))
            {
                string existing = NormalizeCustomCode(File.ReadAllText(path, Encoding.UTF8));
                if (existing.Length >= 4)
                    return existing;
            }
        }
        catch { }

        string created = GenerateDefaultCode();
        TryPersistCode(created);
        return created;
    }

    internal static string SaveCode(string raw)
    {
        string normalized = NormalizeCustomCode(raw);
        if (normalized.Length == 0)
            throw new ArgumentException("Pairing code must be 4–32 characters using letters, digits, '-' or '_'.");
        TryPersistCode(normalized);
        return normalized;
    }

    private static void TryPersistCode(string normalized)
    {
        try
        {
            string path = PairingCodeFilePath;
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, normalized, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch { }
    }
}

/// <summary>
/// Zero-config cross-network (off-Wi-Fi / Mobile Data / separate router / VPN / global IPv6 / STUN IPv4)
/// endpoint collector and end-to-end AES-256-GCM encrypted rendezvous resolver.
/// Never transmits plaintext IPs or secrets: records are sealed with a key derived from the
/// user's Pairing Code (or 64-hex Trusted Certificate Fingerprint), and connections still
/// verify the pinned X.509 TLS certificate fingerprint and pairing secret/token.
/// </summary>
internal static class CrossNetworkRendezvous
{
    private const string RecordPrefix = "DTR1|";
    private const string KnockPrefix = "DTK1|";
    private const string RelayBaseUrl = "https://ntfy.sh";
    private static readonly byte[] RecordAad = "DTR1"u8.ToArray();
    private static readonly byte[] KnockAad = "DTK1"u8.ToArray();
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };
    private static readonly HttpClient SseHttp = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private static readonly object StunCacheGate = new();
    private static string? _cachedStunIpv4;
    private static long _cachedStunAtMs;

    internal sealed record RendezvousPayload(
        string Name,
        string Fingerprint,
        string Addresses,
        string PairingTag,
        bool IsHub,
        int MemberCount,
        long TimestampUnixSeconds);

    internal sealed record KnockPayload(
        string SessionId,
        string BrokerHost,
        int BrokerPort,
        string RequesterFingerprint,
        long TimestampUnixSeconds);

    internal static string ComputeTopic(string? lookupKey)
    {
        string clean = (lookupKey ?? "").Trim().ToUpperInvariant();
        if (clean.Length < 4) return "";
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes($"DT1-WAN-TOPIC-V1|{clean}"));
        return "dtv-wan-" + Convert.ToHexString(digest)[..24].ToLowerInvariant();
    }

    internal static byte[] DeriveAesKey(string? lookupKey)
    {
        string clean = (lookupKey ?? "").Trim().ToUpperInvariant();
        if (clean.Length < 4)
            throw new ArgumentException("Lookup key must be at least 4 characters.", nameof(lookupKey));
        return SHA256.HashData(Encoding.UTF8.GetBytes($"DT1-WAN-KEY-V1|{clean}"));
    }

    internal static IReadOnlyList<IPAddress> ParseCandidateAddresses(string? rawAddressList, bool allowLoopback = false)
    {
        if (string.IsNullOrWhiteSpace(rawAddressList))
            return Array.Empty<IPAddress>();
        var result = new List<IPAddress>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string token in rawAddressList.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = token.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']') && trimmed.Length > 2)
                trimmed = trimmed[1..^1];
            int zoneIdx = trimmed.IndexOf('%');
            if (zoneIdx > 0)
                trimmed = trimmed[..zoneIdx];
            if (!IPAddress.TryParse(trimmed, out var ip))
                continue;
            if (ip.IsIPv4MappedToIPv6)
                ip = ip.MapToIPv4();
            if (ip.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
                continue;
            if ((!allowLoopback && IPAddress.IsLoopback(ip)) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) ||
                ip.Equals(IPAddress.Broadcast) || ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.IsIPv6SiteLocal)
                continue;
            string canonical = ip.ToString();
            if (seen.Add(canonical))
                result.Add(ip);
            if (result.Count >= 6)
                break;
        }
        return result;
    }

    internal static IReadOnlyList<string> CollectLocalAndGlobalAddresses(string? extraPublicIpv4 = null)
    {
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddCandidate(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return;
            foreach (var ip in ParseCandidateAddresses(candidate))
            {
                string text = ip.ToString();
                if (seen.Add(text) && ordered.Count < 4)
                    ordered.Add(text);
            }
        }

        // 1. Primary LAN / Hotspot / Overlay (Tailscale 100.x, ZeroTier, Private Wi-Fi/Ethernet) IPv4
        try
        {
            var ipv4s = new List<(string Ip, int Priority)>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                var props = nic.GetIPProperties();
                bool hasGw = props.GatewayAddresses.Count > 0;
                foreach (var uni in props.UnicastAddresses)
                {
                    var ip = uni.Address;
                    if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip))
                        continue;
                    byte[] b = ip.GetAddressBytes();
                    if (b[0] == 169 && b[1] == 254) continue;
                    bool isLan = b[0] == 192 && b[1] == 168 || b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31);
                    bool isOverlay = b[0] == 100 && b[1] is >= 64 and <= 127; // Tailscale / CGNAT overlay
                    int score = (hasGw ? 100 : 0) + (isLan ? 50 : 0) + (isOverlay ? 70 : 0);
                    ipv4s.Add((ip.ToString(), score));
                }
            }
            foreach (var item in ipv4s.OrderByDescending(x => x.Priority))
                AddCandidate(item.Ip);
        }
        catch { }

        // 2. Globally routable Public IPv6 (direct peer-to-peer across Mobile 5G & Fiber ISPs with zero NAT)
        foreach (string v6 in ReceiverServer.CollectReachableGlobalIpv6Addresses())
            AddCandidate(v6);

        // 3. STUN-discovered Public IPv4 (if available and not already listed)
        AddCandidate(extraPublicIpv4 ?? GetCachedStunIpv4());

        return ordered;
    }

    internal static async Task<string> CollectMultiEndpointListAsync(CancellationToken cancellation = default)
    {
        string? stunIp = await DiscoverPublicIpv4ViaStunAsync(cancellation).ConfigureAwait(false);
        var list = CollectLocalAndGlobalAddresses(stunIp);
        return string.Join(",", list);
    }

    private static string? GetCachedStunIpv4()
    {
        lock (StunCacheGate)
        {
            if (_cachedStunIpv4 != null && Environment.TickCount64 - _cachedStunAtMs < 300_000)
                return _cachedStunIpv4;
            return null;
        }
    }

    internal static async Task<string?> DiscoverPublicIpv4ViaStunAsync(CancellationToken cancellation = default)
    {
        string? cached = GetCachedStunIpv4();
        if (cached != null) return cached;

        (string Host, int Port)[] servers =
        [
            ("stun.cloudflare.com", 3478),
            ("stun.l.google.com", 19302)
        ];

        foreach (var (host, port) in servers)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(850);
                var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, timeout.Token)
                    .ConfigureAwait(false);
                var serverIp = addresses.FirstOrDefault();
                if (serverIp == null) continue;

                using var udp = new UdpClient(AddressFamily.InterNetwork);
                byte[] req = new byte[20];
                // RFC 5389 Binding Request (0x0001), message length 0x0000, magic cookie 0x2112A442
                req[0] = 0x00; req[1] = 0x01;
                req[2] = 0x00; req[3] = 0x00;
                req[4] = 0x21; req[5] = 0x12; req[6] = 0xA4; req[7] = 0x42;
                RandomNumberGenerator.Fill(req.AsSpan(8, 12));

                await udp.SendAsync(req, new IPEndPoint(serverIp, port), timeout.Token).ConfigureAwait(false);
                var recv = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                string? parsed = TryParseStunXorMappedIpv4(recv.Buffer, req.AsSpan(8, 12));
                if (!string.IsNullOrEmpty(parsed))
                {
                    lock (StunCacheGate)
                    {
                        _cachedStunIpv4 = parsed;
                        _cachedStunAtMs = Environment.TickCount64;
                    }
                    return parsed;
                }
            }
            catch { }
        }
        return null;
    }

    internal static string? TryParseStunXorMappedIpv4(ReadOnlySpan<byte> response, ReadOnlySpan<byte> txId)
    {
        if (response.Length < 20) return null;
        // Binding Success Response = 0x0101, magic cookie = 0x2112A442
        if (response[0] != 0x01 || response[1] != 0x01) return null;
        if (response[4] != 0x21 || response[5] != 0x12 || response[6] != 0xA4 || response[7] != 0x42)
            return null;
        if (txId.Length == 12 && !response.Slice(8, 12).SequenceEqual(txId))
            return null;

        int msgLen = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(2, 2));
        int end = Math.Min(response.Length, 20 + msgLen);
        int offset = 20;
        Span<byte> ipBytes = stackalloc byte[4];
        while (offset + 4 <= end)
        {
            ushort attrType = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(offset, 2));
            ushort attrLen = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(offset + 2, 2));
            offset += 4;
            if (offset + attrLen > end) break;
            var val = response.Slice(offset, attrLen);
            // 0x0020 = XOR-MAPPED-ADDRESS, 0x0001 = MAPPED-ADDRESS
            if ((attrType == 0x0020 || attrType == 0x0001) && attrLen >= 8 && val[1] == 0x01)
            {
                if (attrType == 0x0020)
                {
                    ipBytes[0] = (byte)(val[4] ^ 0x21);
                    ipBytes[1] = (byte)(val[5] ^ 0x12);
                    ipBytes[2] = (byte)(val[6] ^ 0xA4);
                    ipBytes[3] = (byte)(val[7] ^ 0x42);
                }
                else
                {
                    val.Slice(4, 4).CopyTo(ipBytes);
                }
                var ip = new IPAddress(ipBytes);
                if (!IPAddress.IsLoopback(ip) && !ip.Equals(IPAddress.Any))
                    return ip.ToString();
            }
            offset += (attrLen + 3) & ~3;
        }
        return null;
    }

    internal static string SealRecord(RendezvousPayload payload, string lookupKey)
    {
        byte[] key = DeriveAesKey(lookupKey);
        string json = JsonSerializer.Serialize(new
        {
            name = payload.Name,
            fp = payload.Fingerprint.Trim().ToUpperInvariant(),
            addrs = payload.Addresses,
            tag = payload.PairingTag,
            hub = payload.IsHub,
            members = payload.MemberCount,
            ts = payload.TimestampUnixSeconds
        });
        byte[] plain = Encoding.UTF8.GetBytes(json);
        byte[] nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[16];
        using (var aes = new AesGcm(key, 16))
        {
            aes.Encrypt(nonce, plain, cipher, tag, RecordAad);
        }
        byte[] combined = new byte[12 + cipher.Length + 16];
        Buffer.BlockCopy(nonce, 0, combined, 0, 12);
        Buffer.BlockCopy(cipher, 0, combined, 12, cipher.Length);
        Buffer.BlockCopy(tag, 0, combined, 12 + cipher.Length, 16);
        return RecordPrefix + Convert.ToBase64String(combined);
    }

    internal static bool TryOpenRecord(string? envelope, string? lookupKey, out RendezvousPayload? payload)
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(envelope) || string.IsNullOrWhiteSpace(lookupKey))
            return false;
        string trimmed = envelope.Trim();
        if (!trimmed.StartsWith(RecordPrefix, StringComparison.Ordinal))
            return false;
        try
        {
            byte[] combined = Convert.FromBase64String(trimmed[RecordPrefix.Length..]);
            if (combined.Length < 12 + 16 + 2 || combined.Length > 4096)
                return false;
            byte[] key = DeriveAesKey(lookupKey);
            int cipherLen = combined.Length - 28;
            byte[] plain = new byte[cipherLen];
            using (var aes = new AesGcm(key, 16))
            {
                aes.Decrypt(
                    combined.AsSpan(0, 12),
                    combined.AsSpan(12, cipherLen),
                    combined.AsSpan(12 + cipherLen, 16),
                    plain,
                    RecordAad);
            }
            using var doc = JsonDocument.Parse(plain);
            var root = doc.RootElement;
            string name = root.TryGetProperty("name", out var nEl) ? (nEl.GetString() ?? "DeskTouch PC") : "DeskTouch PC";
            string fp = root.TryGetProperty("fp", out var fpEl) ? (fpEl.GetString() ?? "").Trim().ToUpperInvariant() : "";
            string addrs = root.TryGetProperty("addrs", out var aEl) ? (aEl.GetString() ?? "").Trim() : "";
            string tag = root.TryGetProperty("tag", out var tEl) ? (tEl.GetString() ?? "").Trim().ToUpperInvariant() : "";
            bool hub = !root.TryGetProperty("hub", out var hEl) || hEl.ValueKind != JsonValueKind.False;
            int members = root.TryGetProperty("members", out var mEl) && mEl.TryGetInt32(out int mc) ? Math.Max(1, mc) : 1;
            long ts = root.TryGetProperty("ts", out var tsEl) && tsEl.TryGetInt64(out long unix) ? unix : 0;

            if (fp.Length != 64 || !fp.All(Uri.IsHexDigit) || ParseCandidateAddresses(addrs).Count == 0)
                return false;
            long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (ts > 0 && Math.Abs(nowUnix - ts) > 86400)
                return false;

            var validAddrs = string.Join(",", ParseCandidateAddresses(addrs).Select(a => a.ToString()));
            payload = new RendezvousPayload(name, fp, validAddrs, tag, hub, members, ts);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static string SealKnock(KnockPayload knock, string lookupKey)
    {
        byte[] key = DeriveAesKey(lookupKey);
        string json = JsonSerializer.Serialize(new
        {
            sid = knock.SessionId,
            broker = knock.BrokerHost,
            port = knock.BrokerPort,
            req = (knock.RequesterFingerprint ?? "").Trim().ToUpperInvariant(),
            ts = knock.TimestampUnixSeconds
        });
        byte[] plain = Encoding.UTF8.GetBytes(json);
        byte[] nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[16];
        using (var aes = new AesGcm(key, 16))
        {
            aes.Encrypt(nonce, plain, cipher, tag, KnockAad);
        }
        byte[] combined = new byte[12 + cipher.Length + 16];
        Buffer.BlockCopy(nonce, 0, combined, 0, 12);
        Buffer.BlockCopy(cipher, 0, combined, 12, cipher.Length);
        Buffer.BlockCopy(tag, 0, combined, 12 + cipher.Length, 16);
        return KnockPrefix + Convert.ToBase64String(combined);
    }

    internal static bool TryOpenKnock(string? envelope, string? lookupKey, out KnockPayload? knock)
    {
        knock = null;
        if (string.IsNullOrWhiteSpace(envelope) || string.IsNullOrWhiteSpace(lookupKey))
            return false;
        string trimmed = envelope.Trim();
        if (!trimmed.StartsWith(KnockPrefix, StringComparison.Ordinal))
            return false;
        try
        {
            byte[] combined = Convert.FromBase64String(trimmed[KnockPrefix.Length..]);
            if (combined.Length < 12 + 16 + 2 || combined.Length > 2048)
                return false;
            byte[] key = DeriveAesKey(lookupKey);
            int cipherLen = combined.Length - 28;
            byte[] plain = new byte[cipherLen];
            using (var aes = new AesGcm(key, 16))
            {
                aes.Decrypt(
                    combined.AsSpan(0, 12),
                    combined.AsSpan(12, cipherLen),
                    combined.AsSpan(12 + cipherLen, 16),
                    plain,
                    KnockAad);
            }
            using var doc = JsonDocument.Parse(plain);
            var root = doc.RootElement;
            string sid = root.TryGetProperty("sid", out var sEl) ? (sEl.GetString() ?? "").Trim() : "";
            string broker = root.TryGetProperty("broker", out var bEl) ? (bEl.GetString() ?? "").Trim() : MqttTlsRelayTunnel.DefaultBrokerHost;
            int port = root.TryGetProperty("port", out var pEl) && pEl.TryGetInt32(out int bp) ? bp : MqttTlsRelayTunnel.DefaultBrokerPort;
            string req = root.TryGetProperty("req", out var rEl) ? (rEl.GetString() ?? "").Trim().ToUpperInvariant() : "";
            long ts = root.TryGetProperty("ts", out var tsEl) && tsEl.TryGetInt64(out long unix) ? unix : 0;

            if (sid.Length is < 16 or > 48 || !sid.All(char.IsAsciiLetterOrDigit))
                return false;
            if (string.IsNullOrWhiteSpace(broker) || port is <= 0 or > 65535)
                return false;
            long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (ts > 0 && Math.Abs(nowUnix - ts) > 600)
                return false;

            knock = new KnockPayload(sid, broker, port, req, ts);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static string OrderEndpointsForCrossNetwork(string? rawAddressList)
    {
        var parsed = ParseCandidateAddresses(rawAddressList);
        if (parsed.Count == 0) return "";
        static int Score(IPAddress ip)
        {
            if (ip.AddressFamily == AddressFamily.InterNetworkV6) return 0; // Global IPv6 first
            byte[] b = ip.GetAddressBytes();
            bool isPriv = (b[0] == 10) || (b[0] == 172 && b[1] is >= 16 and <= 31) ||
                          (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
            bool isOverlay = b[0] == 100 && b[1] is >= 64 and <= 127;
            if (!isPriv && !isOverlay) return 1; // Public STUN IPv4 second
            if (isOverlay) return 2;             // Tailscale/overlay third
            return 3;                            // Private LAN last
        }
        return string.Join(",", parsed.OrderBy(Score).Select(a => a.ToString()));
    }

    internal static async Task SendKnockAsync(
        string lookupKey,
        string sessionId,
        string? requesterFingerprint = null,
        CancellationToken cancellation = default)
    {
        string topic = ComputeTopic(lookupKey);
        if (string.IsNullOrEmpty(topic)) return;
        try
        {
            var knock = new KnockPayload(
                sessionId,
                MqttTlsRelayTunnel.DefaultBrokerHost,
                MqttTlsRelayTunnel.DefaultBrokerPort,
                requesterFingerprint ?? "",
                DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            string sealedBody = SealKnock(knock, lookupKey);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            cts.CancelAfter(3000);
            using var content = new StringContent(sealedBody, Encoding.UTF8, "text/plain");
            using var _ = await Http.PostAsync($"{RelayBaseUrl}/{topic}", content, cts.Token).ConfigureAwait(false);
        }
        catch { }
    }

    internal static async Task PublishPresenceAsync(
        string fingerprint,
        string? customPairingCode,
        bool isHub = true,
        int memberCount = 1,
        CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(fingerprint) || fingerprint.Length != 64)
            return;
        try
        {
            string addrs = await CollectMultiEndpointListAsync(cancellation).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(addrs))
                return;
            string cleanCode = PairingCodeHelper.NormalizeCustomCode(customPairingCode);
            string tag = cleanCode.Length > 0
                ? PairingCodeHelper.ComputeDiscoveryTag(fingerprint, cleanCode)
                : "";
            var record = new RendezvousPayload(
                Environment.MachineName.Replace("|", "-"),
                fingerprint.ToUpperInvariant(),
                addrs,
                tag,
                isHub,
                Math.Max(1, memberCount),
                DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            var keysToPublish = new List<string> { fingerprint.ToUpperInvariant() };
            if (cleanCode.Length >= 4)
                keysToPublish.Add(cleanCode);

            foreach (string key in keysToPublish)
            {
                string topic = ComputeTopic(key);
                if (string.IsNullOrEmpty(topic)) continue;
                string sealedBody = SealRecord(record, key);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                cts.CancelAfter(3500);
                using var content = new StringContent(sealedBody, Encoding.UTF8, "text/plain");
                using var resp = await Http.PostAsync($"{RelayBaseUrl}/{topic}", content, cts.Token).ConfigureAwait(false);
            }
        }
        catch { }
    }

    internal static Task<NearbyComputer?> ResolveByCodeAsync(string? rawCode, CancellationToken cancellation = default) =>
        ResolveByCodeAsync(rawCode, excludeFingerprint: null, cancellation);

    internal static Task<NearbyComputer?> ResolveByCodeAsync(
        string? rawCode,
        string? excludeFingerprint,
        CancellationToken cancellation = default)
    {
        string code = PairingCodeHelper.NormalizeCustomCode(rawCode);
        if (code.Length < 4) return Task.FromResult<NearbyComputer?>(null);
        return ResolveCoreAsync(code, excludeFingerprint, cancellation);
    }

    internal static Task<NearbyComputer?> ResolveByFingerprintAsync(string? fingerprint, CancellationToken cancellation = default) =>
        ResolveByFingerprintAsync(fingerprint, excludeFingerprint: null, cancellation);

    internal static Task<NearbyComputer?> ResolveByFingerprintAsync(
        string? fingerprint,
        string? excludeFingerprint,
        CancellationToken cancellation = default)
    {
        string fp = (fingerprint ?? "").Trim().ToUpperInvariant();
        if (fp.Length != 64 || !fp.All(Uri.IsHexDigit))
            return Task.FromResult<NearbyComputer?>(null);
        return ResolveCoreAsync(fp, excludeFingerprint, cancellation);
    }

    private static async Task<NearbyComputer?> ResolveCoreAsync(
        string lookupKey,
        string? excludeFingerprint,
        CancellationToken cancellation)
    {
        string topic = ComputeTopic(lookupKey);
        if (string.IsNullOrEmpty(topic)) return null;
        string excludeNorm = (excludeFingerprint ?? "").Trim().ToUpperInvariant();
        string relaySid = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        // Wake up the receiving PC immediately via encrypted DTK1 Knock so it both
        // republishes fresh endpoints and stands up an outbound MQTT TLS relay bridge if needed.
        _ = SendKnockAsync(lookupKey, relaySid, excludeNorm, cancellation);

        int[] retryDelaysMs = [0, 900, 1500];
        foreach (int waitMs in retryDelaysMs)
        {
            if (waitMs > 0)
            {
                try { await Task.Delay(waitMs, cancellation).ConfigureAwait(false); }
                catch { break; }
            }
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                cts.CancelAfter(3800);
                string body = await Http.GetStringAsync($"{RelayBaseUrl}/{topic}/json?poll=1&since=12h", cts.Token)
                    .ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(body))
                    continue;

                RendezvousPayload? newest = null;
                foreach (string line in body.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        if (!doc.RootElement.TryGetProperty("message", out var msgEl))
                            continue;
                        string? msg = msgEl.GetString();
                        if (TryOpenRecord(msg, lookupKey, out var opened) && opened != null)
                        {
                            if (excludeNorm.Length == 64 &&
                                string.Equals(opened.Fingerprint, excludeNorm, StringComparison.OrdinalIgnoreCase))
                                continue;
                            if (newest == null || opened.TimestampUnixSeconds >= newest.TimestampUnixSeconds)
                                newest = opened;
                        }
                    }
                    catch { }
                }

                if (newest != null)
                {
                    string orderedAddrs = OrderEndpointsForCrossNetwork(newest.Addresses);
                    if (string.IsNullOrEmpty(orderedAddrs))
                        orderedAddrs = newest.Addresses;
                    return new NearbyComputer(
                        newest.Name,
                        orderedAddrs,
                        newest.Fingerprint,
                        IsAndroid: false,
                        CanReceive: true,
                        IsHub: newest.IsHub,
                        MemberCount: newest.MemberCount,
                        PairingTag: newest.PairingTag,
                        RelaySessionId: relaySid,
                        RelayBroker: MqttTlsRelayTunnel.DefaultBrokerHost);
                }
            }
            catch { }
        }
        return null;
    }

    internal static async Task RunReceiverRendezvousAgentAsync(
        Func<(string Fingerprint, string CustomCode, bool IsHub, int MemberCount)?> getState,
        Action<int> registerRelayPort,
        Action<int> unregisterRelayPort,
        Action<string>? log,
        CancellationToken cancellation)
    {
        var seenKnocks = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        var heartbeatTask = Task.Run(async () =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    var st = getState();
                    if (st is { } state && state.Fingerprint.Length == 64)
                    {
                        await PublishPresenceAsync(
                            state.Fingerprint, state.CustomCode, state.IsHub, state.MemberCount, cancellation)
                            .ConfigureAwait(false);
                    }
                }
                catch { }
                try { await Task.Delay(TimeSpan.FromMinutes(5), cancellation).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        });

        var sseTask = Task.Run(async () =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                string subscribedTopics = "";
                try
                {
                    var st = getState();
                    if (st is not { } state || state.Fingerprint.Length != 64)
                    {
                        await Task.Delay(1500, cancellation).ConfigureAwait(false);
                        continue;
                    }
                    var topics = new List<string> { ComputeTopic(state.Fingerprint) };
                    string codeTopic = ComputeTopic(PairingCodeHelper.NormalizeCustomCode(state.CustomCode));
                    if (!string.IsNullOrEmpty(codeTopic) && !topics.Contains(codeTopic))
                        topics.Add(codeTopic);
                    subscribedTopics = string.Join(",", topics);

                    using var sseCts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    using var req = new HttpRequestMessage(HttpMethod.Get, $"{RelayBaseUrl}/{subscribedTopics}/sse?since=15s");
                    req.Headers.Accept.ParseAdd("text/event-stream");
                    using var resp = await SseHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, sseCts.Token)
                        .ConfigureAwait(false);
                    resp.EnsureSuccessStatusCode();
                    await using var stream = await resp.Content.ReadAsStreamAsync(sseCts.Token).ConfigureAwait(false);
                    using var reader = new StreamReader(stream, Encoding.UTF8);

                    while (!sseCts.IsCancellationRequested)
                    {
                        using var idleWatch = new CancellationTokenSource(TimeSpan.FromSeconds(110));
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(sseCts.Token, idleWatch.Token);
                        string? line = await reader.ReadLineAsync(linked.Token).ConfigureAwait(false);
                        if (line == null) break;
                        var cur = getState();
                        string curCode = PairingCodeHelper.NormalizeCustomCode(cur?.CustomCode);
                        string curTopics = ComputeTopic(cur?.Fingerprint) +
                            (curCode.Length >= 4 ? "," + ComputeTopic(curCode) : "");
                        if (!string.Equals(curTopics, subscribedTopics, StringComparison.Ordinal))
                            break;

                        if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                        string json = line[5..].Trim();
                        if (json.Length == 0 || !json.StartsWith('{')) continue;

                        string? msg = null;
                        try
                        {
                            using var doc = JsonDocument.Parse(json);
                            if (doc.RootElement.TryGetProperty("message", out var msgEl))
                                msg = msgEl.GetString();
                        }
                        catch { }
                        if (string.IsNullOrWhiteSpace(msg) || !msg.StartsWith(KnockPrefix, StringComparison.Ordinal))
                            continue;

                        var live = getState();
                        if (live is not { } ls || ls.Fingerprint.Length != 64) continue;

                        KnockPayload? knock = null;
                        string liveCode = PairingCodeHelper.NormalizeCustomCode(ls.CustomCode);
                        if (liveCode.Length >= 4 && TryOpenKnock(msg, liveCode, out var byCode))
                            knock = byCode;
                        else if (TryOpenKnock(msg, ls.Fingerprint, out var byFp))
                            knock = byFp;
                        if (knock == null) continue;

                        if (!string.IsNullOrEmpty(knock.RequesterFingerprint) &&
                            string.Equals(knock.RequesterFingerprint, ls.Fingerprint, StringComparison.OrdinalIgnoreCase))
                            continue;

                        long nowMs = Environment.TickCount64;
                        if (!seenKnocks.TryAdd(knock.SessionId, nowMs))
                            continue;
                        if (seenKnocks.Count > 128)
                        {
                            foreach (var kv in seenKnocks)
                            {
                                if (nowMs - kv.Value > 120_000)
                                    seenKnocks.TryRemove(kv.Key, out _);
                            }
                        }

                        log?.Invoke($"Cross-network pairing request received; refreshing endpoints and arming encrypted relay bridge.");
                        _ = Task.Run(() => PublishPresenceAsync(ls.Fingerprint, ls.CustomCode, ls.IsHub, ls.MemberCount, cancellation));
                        var capturedKnock = knock;
                        _ = Task.Run(() => MqttTlsRelayTunnel.RunServerBridgeAsync(
                            capturedKnock.SessionId,
                            capturedKnock.BrokerHost,
                            capturedKnock.BrokerPort,
                            Wire.Port,
                            registerRelayPort,
                            unregisterRelayPort,
                            cancellation));
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
                catch
                {
                    try { await Task.Delay(5000, cancellation).ConfigureAwait(false); }
                    catch { break; }
                }
            }
        });

        await Task.WhenAll(heartbeatTask, sseTask).ConfigureAwait(false);
    }
}
