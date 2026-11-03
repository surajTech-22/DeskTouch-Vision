using System.Buffers.Binary;
using System.Text.Json;

namespace DeskTouch.DeviceShare;

internal sealed record Packet(string Type, int A = 0, int B = 0, int C = 0,
    int D = 0, string? Text = null);

internal static class Wire
{
    internal const int Port = 37377;
    private const int MaxFrame = 1024;
    internal static async Task WriteAsync(Stream stream, Packet data, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(data);
        if (bytes.Length is < 2 or > MaxFrame) throw new InvalidDataException("Invalid message size");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }

    internal static async Task<Packet> ReadAsync(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        int count = BinaryPrimitives.ReadInt32BigEndian(header);
        if (count is < 2 or > MaxFrame) throw new InvalidDataException("Invalid input frame");
        byte[] body = new byte[count];
        await stream.ReadExactlyAsync(body, token);
        return JsonSerializer.Deserialize<Packet>(body)
            ?? throw new InvalidDataException("Malformed message");
    }
}
