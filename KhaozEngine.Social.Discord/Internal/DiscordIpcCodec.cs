using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace KhaozEngine.Social.Discord.Internal;

/// <summary>
/// Discord IPC framing: a 4-byte little-endian opcode, a 4-byte little-endian body length, then the
/// UTF-8 JSON body. Pure and allocation-simple so it is fully unit-testable without a live socket.
/// </summary>
internal static class DiscordIpcCodec
{
    public const int HeaderSize = 8;

    /// <summary>
    /// The largest body a frame may declare, in bytes. Discord rich-presence traffic is small (a presence
    /// payload is a few hundred bytes, and the biggest inbound frame is a READY with the local user in it), so
    /// 64 KiB is far above anything real and far below the point where waiting for the rest of it costs
    /// anything. A declared length outside 0 to this is not backpressure, it is a desynced stream (issue #159).
    /// </summary>
    public const int MaxBodyLength = 64 * 1024;

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
    /// <para>Throws <see cref="InvalidDataException"/> when the header declares a body length outside 0 to
    /// <see cref="MaxBodyLength"/>. That case USED to return false as well, which reads as "keep waiting" and
    /// is unrecoverable: one corrupted or desynced length (a partial-write race, or any local process writing
    /// non-IPC bytes into the same pipe) meant the decoder waited forever for a body that was never coming,
    /// on a socket that stayed perfectly healthy, so the transport-death check added in #655 never fired
    /// either. Presence then silently stopped updating for the rest of the session with no exception anywhere.
    /// Throwing turns that into a disconnect and a reconnect, and it also BOUNDS the caller's accumulation
    /// buffer: whatever is left undecoded is one incomplete frame, so it can never exceed the header plus
    /// this cap (issue #159).</para>
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
        if (length < 0 || length > MaxBodyLength)
        {
            throw new InvalidDataException(
                $"Discord IPC frame declares a body of {length} bytes, outside the valid range 0 to {MaxBodyLength}. " +
                "The stream is desynced, so there is no byte count that would make this frame decodable and waiting for more would wedge the connection.");
        }

        if (buffer.Length < HeaderSize + length)
        {
            return false;
        }

        opcode = (DiscordIpcOpcode)op;
        json = Encoding.UTF8.GetString(buffer.Slice(HeaderSize, length));
        consumed = HeaderSize + length;
        return true;
    }
}
