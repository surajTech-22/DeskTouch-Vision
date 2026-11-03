using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace DeskTouch.DeviceShare;

// Trusted devices are deliberately scoped to this Windows user, never to
// machine-wide accounts. Names and broadcast IPs are UNTRUSTED labels;
// fingerprint + a per-peer 256-bit secret authenticate reconnection.
internal sealed record TrustedOutgoing(string Name, string Fingerprint,
    string Token, bool AutoConnect = true);
internal sealed record TrustedIncoming(string Name, string TokenSha256);

internal sealed class TrustedDeviceStore
{
    private sealed class Data
    {
        public List<TrustedOutgoing> Outgoing { get; set; } = new();
        public List<TrustedIncoming> Incoming { get; set; } = new();
        public bool AutoConnectEnabled { get; set; } = true;
    }

    private readonly object _lock = new();
    private readonly string _path;
    private Data _data;

    internal TrustedDeviceStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeskTouchVision", "trusted-devices.dpapi");
        _data = Load();
    }

    internal static string NewSecret() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    internal static string HashToken(string token)
    {
        if (token.Length != 64 || !token.All(Uri.IsHexDigit))
            throw new ArgumentException("Trust token must contain 64 hexadecimal digits.");
        return Convert.ToHexString(SHA256.HashData(Convert.FromHexString(token)));
    }

    private Data Load()
    {
        if (!File.Exists(_path)) return new Data();
        // Corruption or changed Windows account is NOT interpreted as empty
        // trust, so user security settings are never silently rewritten.
        byte[] encrypted = File.ReadAllBytes(_path);
        byte[] plaintext = ProtectedData.Unprotect(encrypted, null,
            DataProtectionScope.CurrentUser);
        try
        {
            return JsonSerializer.Deserialize<Data>(plaintext)
                ?? throw new InvalidDataException("Trusted devices file is invalid.");
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(_data);
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, encrypted);
            File.Move(temp, _path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal bool AutoConnectEnabled
    {
        get { lock (_lock) return _data.AutoConnectEnabled; }
        set
        {
            lock (_lock)
            {
                _data.AutoConnectEnabled = value;
                Save();
            }
        }
    }

    internal IReadOnlyList<TrustedOutgoing> Outgoing
    {
        get { lock (_lock) return _data.Outgoing.ToArray(); }
    }

    internal IReadOnlyList<TrustedIncoming> Incoming
    {
        get { lock (_lock) return _data.Incoming.ToArray(); }
    }

    internal bool IsTrustedReceiver(string fingerprint)
    {
        lock (_lock) return _data.Outgoing.Any(x =>
            x.Fingerprint.Equals(fingerprint, StringComparison.OrdinalIgnoreCase));
    }

    internal TrustedOutgoing? FindAutoConnect(string fingerprint)
    {
        lock (_lock) return _data.Outgoing.FirstOrDefault(x =>
            x.AutoConnect && x.Fingerprint.Equals(fingerprint, StringComparison.OrdinalIgnoreCase));
    }

    internal TrustedIncoming? AuthenticateIncoming(string? token)
    {
        if (token == null || token.Length != 64 || !token.All(Uri.IsHexDigit))
            return null;
        byte[] receivedHash = SHA256.HashData(Convert.FromHexString(token));
        try
        {
            lock (_lock)
            {
                foreach (var device in _data.Incoming)
                {
                    byte[] storedHash;
                    try { storedHash = Convert.FromHexString(device.TokenSha256); }
                    catch (FormatException) { continue; }
                    if (storedHash.Length == 32 &&
                        CryptographicOperations.FixedTimeEquals(receivedHash, storedHash))
                        return device;
                }
            }
            return null;
        }
        finally { CryptographicOperations.ZeroMemory(receivedHash); }
    }

    internal void AllowIncoming(string name, string token)
    {
        string hash = HashToken(token);
        lock (_lock)
        {
            _data.Incoming.RemoveAll(x =>
                x.TokenSha256.Equals(hash, StringComparison.OrdinalIgnoreCase));
            _data.Incoming.Add(new TrustedIncoming(name, hash));
            Save();
        }
    }

    internal void TrustOutgoing(string name, string fingerprint, string token)
    {
        _ = HashToken(token);
        if (fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid pinned receiver fingerprint.");
        lock (_lock)
        {
            _data.Outgoing.RemoveAll(x => x.Fingerprint.Equals(
                fingerprint, StringComparison.OrdinalIgnoreCase));
            _data.Outgoing.Add(new TrustedOutgoing(name, fingerprint.ToUpperInvariant(), token));
            Save();
        }
    }

    internal void ForgetOutgoing(string fingerprint)
    {
        lock (_lock)
        {
            _data.Outgoing.RemoveAll(x => x.Fingerprint.Equals(
                fingerprint, StringComparison.OrdinalIgnoreCase));
            Save();
        }
    }

    internal void ForgetIncoming(string tokenHash)
    {
        lock (_lock)
        {
            _data.Incoming.RemoveAll(x => x.TokenSha256.Equals(
                tokenHash, StringComparison.OrdinalIgnoreCase));
            Save();
        }
    }

    internal void SetAutoConnect(string fingerprint, bool enabled)
    {
        lock (_lock)
        {
            for (int i = 0; i < _data.Outgoing.Count; i++)
                if (_data.Outgoing[i].Fingerprint.Equals(fingerprint,
                    StringComparison.OrdinalIgnoreCase))
                    _data.Outgoing[i] = _data.Outgoing[i] with { AutoConnect = enabled };
            Save();
        }
    }
}

// Receiver TLS identity survives app close, tray, and signed app updates.
// Re-pairing is NOT required after each restart. All key material is encrypted
// by Windows DPAPI for the current user and never advertised by LAN discovery.
internal static class PersistentReceiverIdentity
{
    private sealed record IdentityFile(string Password, string PfxBase64);

    internal static X509Certificate2 LoadOrCreate(string? path = null)
    {
        path ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeskTouchVision", "receiver-identity.dpapi");
        if (File.Exists(path))
        {
            byte[] clear = ProtectedData.Unprotect(File.ReadAllBytes(path), null,
                DataProtectionScope.CurrentUser);
            try
            {
                var saved = JsonSerializer.Deserialize<IdentityFile>(clear)
                    ?? throw new InvalidDataException("Receiver identity file invalid.");
                var pfx = Convert.FromBase64String(saved.PfxBase64);
                try
                {
                    var loaded = new X509Certificate2(pfx, saved.Password,
                        X509KeyStorageFlags.UserKeySet);
                    if (!loaded.HasPrivateKey || loaded.NotAfter <= DateTime.UtcNow)
                    {
                        loaded.Dispose();
                        throw new CryptographicException(
                            "Saved TLS identity expired or has no private key. Do not auto-trust a replacement.");
                    }
                    return loaded;
                }
                finally { CryptographicOperations.ZeroMemory(pfx); }
            }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }

        // Avoid ever accepting a transient key when persistent identity could
        // not be saved: trusted auto-connect requires stable TLS pinning.
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=DeskTouch-LAN", key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        var serverUsage = new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(serverUsage, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var temporary = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(10));
        string password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        byte[] exported = temporary.Export(X509ContentType.Pkcs12, password);
        try
        {
            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(
                new IdentityFile(password, Convert.ToBase64String(exported)));
            try
            {
                byte[] protectedIdentity = ProtectedData.Protect(plain, null,
                    DataProtectionScope.CurrentUser);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                // Atomic create, not overwriting a concurrent process identity.
                try { using var stream = new FileStream(path, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None); stream.Write(protectedIdentity); }
                catch (IOException) when (File.Exists(path))
                {
                    // Another receiver initialized identity first.
                    return LoadOrCreate(path);
                }
                return new X509Certificate2(exported, password, X509KeyStorageFlags.UserKeySet);
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        finally { CryptographicOperations.ZeroMemory(exported); }
    }
}
