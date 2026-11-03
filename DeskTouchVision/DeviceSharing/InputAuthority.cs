namespace DeskTouch.DeviceShare;

/// <summary>
/// The authenticated pairing transports input in BOTH directions. SourceCapture
/// must not care which Windows computer originally opened the TLS connection.
/// </summary>
internal interface IInputSession
{
    MonitorTopology RemoteMonitors { get; }
    bool TrySend(Packet packet);
}

internal static class ReverseInputProtocol
{
    internal static Packet Encode(Packet packet) => packet with
    {
        Type = "reverse-" + packet.Type
    };

    internal static bool TryDecode(Packet packet, out Packet input)
    {
        const string prefix = "reverse-";
        input = packet;
        if (!packet.Type.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        var operation = packet.Type[prefix.Length..];
        // Only known input and lease operations are accepted. No approval,
        // certificate, trust or arbitrary protocol messages can be injected.
        if (operation is not ("claim" or "release" or "reset" or "move" or
            "key" or "button" or "wheel"))
            return false;
        input = packet with { Type = operation };
        return true;
    }
}

/// <summary>Monotonic per-connection IDs prevent late grants from activating
/// the wrong handoff after a canceled request or a local-input override.</summary>
internal sealed class AuthorityRequestTracker
{
    private int _next;
    private int _pending;
    internal int Pending => Volatile.Read(ref _pending);

    internal int Begin()
    {
        int claim = Interlocked.Increment(ref _next);
        if (claim <= 0) // A new session won't ever approach 2^31 claims.
            throw new InvalidOperationException("Authority counter exhausted; reconnect required.");
        Volatile.Write(ref _pending, claim);
        return claim;
    }

    internal bool Accept(int claim)
    {
        if (claim <= 0) return false;
        return Interlocked.CompareExchange(ref _pending, 0, claim) == claim;
    }

    internal void Cancel() => Interlocked.Exchange(ref _pending, 0);
}
