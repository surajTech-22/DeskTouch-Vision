using System.Security.Cryptography;
using System.Text;

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
