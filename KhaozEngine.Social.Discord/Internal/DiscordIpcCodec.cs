using System;
using System.Buffers.Binary;
using System.Text;

namespace KhaozEngine.Social.Discord.Internal;

/// <summary>
/// Discord IPC framing: a 4-byte little-endian opcode, a 4-byte little-endian body length, then the
/// UTF-8 JSON body. Pure and allocation-simple so it is fully unit-testable without a live socket.
/// </summary>
internal static class DiscordIpcCodec
{
    public const int HeaderSize = 8;

    /// <summary>Encode one frame (header + UTF-8 body).</summary>
    public static byte[] EncodeFrame(DiscordIpcOpcode opcode, string json)
    {
        byte[] body = Encoding.UTF8.GetBytes(json ?? string.Empty);
        byte[] frame = new byte[HeaderSize + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), (int)opcode);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4, 4), body.Length);
        body.CopyTo(frame, HeaderSize);
        return frame;
    }

    /// <summary>
    /// Try to decode one frame from the front of <paramref name="buffer"/>. Returns false (and
    /// consumed=0) if a full header+body is not yet present, so a caller can accumulate more bytes.
    /// </summary>
    public static bool TryDecodeFrame(ReadOnlySpan<byte> buffer, out DiscordIpcOpcode opcode, out string json, out int consumed)
    {
        opcode = default;
        json = string.Empty;
        consumed = 0;

        if (buffer.Length < HeaderSize)
        {
            return false;
        }

        int op = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(0, 4));
        int length = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(4, 4));
        if (length < 0 || buffer.Length < HeaderSize + length)
        {
            return false;
        }

        opcode = (DiscordIpcOpcode)op;
        json = Encoding.UTF8.GetString(buffer.Slice(HeaderSize, length));
        consumed = HeaderSize + length;
        return true;
    }
}
