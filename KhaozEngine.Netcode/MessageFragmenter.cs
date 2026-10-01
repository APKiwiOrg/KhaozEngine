using System;
using System.Buffers.Binary;

namespace KhaozEngine.Netcode;

/// <summary>
/// Splits a logical payload too large for one message into a run of chunks, each of which fits inside the
/// caller's own message envelope, and reads one back off the wire.
/// <para>PAYLOAD AGNOSTIC, and deliberately so: this type moves bytes and never looks at them. What a stream
/// carries, what message kind the host gives the chunks, and what decodes the reassembled bytes are all the
/// caller's, exactly as a message envelope keeps its payload opaque.</para>
/// <para>The header is five bytes and every field is fixed width, so nothing here needs a varint reader:</para>
/// <code>
/// [StreamId: byte]        // which logical stream, the CALLER assigns these
/// [Sequence: uint16 LE]   // increments per transmission of that stream, wraps
/// [ChunkIndex: byte]
/// [ChunkCount: byte]      // 1 to 255
/// [Bytes: the rest]
/// </code>
/// <para>A chunk is what goes IN a message payload, not a frame: the caller wraps it in its own envelope under its
/// own kind and sends it on a reliable ordered channel. The chunk payload width is the CALLER's, passed to every
/// call as <c>chunkPayloadBytes</c>, because only the host knows how much room its envelope leaves. It is the
/// body a chunk carries, NOT counting <see cref="HeaderBytes"/>, so a full chunk is
/// <see cref="HeaderBytes"/> plus the width. Both ends of a connection must agree on it exactly: a reader refuses a
/// non final chunk of any other length, so the width is part of the wire contract and changing it is a protocol
/// migration.</para>
/// <para>The width must be in <c>1</c> to <see cref="ushort.MaxValue"/>. Anything else is a LOCAL caller bug and
/// every call that takes a width throws <see cref="ArgumentOutOfRangeException"/> for it, including
/// <see cref="TryReadChunk"/>, because the width never comes from the wire.</para>
/// <para><see cref="Fragment"/> THROWS above <see cref="MaxPayloadBytes"/>, on the same grounds: a payload that
/// long is a local caller bug and worth the stack. Everything that reads REMOTE bytes is total and never throws,
/// because those bytes came from a peer. See <see cref="MessageReassembler"/>.</para>
/// </summary>
public static class MessageFragmenter
{
    /// <summary>Width of the chunk header, in bytes: stream id, sequence, chunk index, chunk count.</summary>
    public const int HeaderBytes = 5;

    /// <summary>The most chunks one transmission can be split into, because <c>ChunkCount</c> is a byte.</summary>
    public const int MaxChunks = 255;

    /// <summary>The largest logical payload this format can carry at a width: <see cref="MaxChunks"/> chunks of
    /// <paramref name="chunkPayloadBytes"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chunkPayloadBytes"/> is outside <c>1</c> to
    /// <see cref="ushort.MaxValue"/>.</exception>
    public static int MaxPayloadBytes(int chunkPayloadBytes)
    {
        ThrowIfWidthOutOfRange(chunkPayloadBytes);
        return MaxChunks * chunkPayloadBytes;
    }

    /// <summary>How many chunks a payload of this length becomes at a width. An empty payload is ONE chunk
    /// carrying nothing, so a caller with nothing to say is not a special case on either side.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="payloadLength"/> is negative, or
    /// <paramref name="chunkPayloadBytes"/> is outside <c>1</c> to <see cref="ushort.MaxValue"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="payloadLength"/> is above
    /// <see cref="MaxPayloadBytes"/>.</exception>
    public static int ChunkCount(int payloadLength, int chunkPayloadBytes)
    {
        ThrowIfWidthOutOfRange(chunkPayloadBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(payloadLength);
        ThrowIfOverCap(payloadLength, chunkPayloadBytes, nameof(payloadLength));
        return payloadLength == 0 ? 1 : (payloadLength + chunkPayloadBytes - 1) / chunkPayloadBytes;
    }

    /// <summary>
    /// Splits a payload into chunks, each one a message payload ready for the caller's envelope. Every chunk but
    /// the last carries a FULL <paramref name="chunkPayloadBytes"/>, which is what lets a reader tell a truncated
    /// chunk from a legitimately short final one.
    /// <para><paramref name="sequence"/> names this transmission of <paramref name="streamId"/> and is the caller's
    /// to increment, wrapping freely. A reader uses it for CONSISTENCY rather than ordering: the channel is
    /// reliable ordered, so a change of sequence means the sender restarted the stream, never that something
    /// arrived late.</para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chunkPayloadBytes"/> is outside <c>1</c> to
    /// <see cref="ushort.MaxValue"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="payload"/> is longer than
    /// <see cref="MaxPayloadBytes"/>.</exception>
    public static byte[][] Fragment(byte streamId, ushort sequence, ReadOnlySpan<byte> payload, int chunkPayloadBytes)
    {
        ThrowIfWidthOutOfRange(chunkPayloadBytes);
        ThrowIfOverCap(payload.Length, chunkPayloadBytes, nameof(payload));

        int count = ChunkCount(payload.Length, chunkPayloadBytes);
        var chunks = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            int offset = i * chunkPayloadBytes;
            int take = Math.Min(chunkPayloadBytes, payload.Length - offset);
            var chunk = new byte[HeaderBytes + take];
            chunk[0] = streamId;
            BinaryPrimitives.WriteUInt16LittleEndian(chunk.AsSpan(1, 2), sequence);
            chunk[3] = (byte)i;
            chunk[4] = (byte)count;
            payload.Slice(offset, take).CopyTo(chunk.AsSpan(HeaderBytes));
            chunks[i] = chunk;
        }
        return chunks;
    }

    /// <summary>
    /// Reads one chunk's header and slices its bytes. NEVER throws on the chunk, on the rule every frame decoder
    /// follows: the bytes came from a remote peer, so a malformed one is a false and a dropped chunk rather than an
    /// exception out of the receive loop. Only an out of range <paramref name="chunkPayloadBytes"/> throws, because
    /// that is the local caller's.
    /// <para>False for a chunk shorter than <see cref="HeaderBytes"/>, a chunk count of zero, an index at or past
    /// the count, a chunk carrying more than <paramref name="chunkPayloadBytes"/>, a NON final chunk that is not
    /// exactly full, and a final chunk of a multi chunk transmission carrying nothing. The last two are the
    /// strictness that makes truncation detectable: <see cref="Fragment"/> emits exactly one wire form per payload
    /// and width, so a short non final chunk is a chunk that lost bytes on the way. A final chunk cut in its BODY
    /// is NOT detectable here, because the header declares no total length, and it is the caller's decoder that
    /// refuses the short payload.</para>
    /// <para><paramref name="bytes"/> is a SLICE of <paramref name="chunk"/>, so nothing is copied and nothing is
    /// interpreted.</para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chunkPayloadBytes"/> is outside <c>1</c> to
    /// <see cref="ushort.MaxValue"/>.</exception>
    public static bool TryReadChunk(ReadOnlySpan<byte> chunk, int chunkPayloadBytes, out byte streamId,
        out ushort sequence, out int chunkIndex, out int chunkCount, out ReadOnlySpan<byte> bytes)
    {
        ThrowIfWidthOutOfRange(chunkPayloadBytes);
        streamId = 0;
        sequence = 0;
        chunkIndex = 0;
        chunkCount = 0;
        bytes = default;
        if (chunk.Length < HeaderBytes) return false;

        int index = chunk[3];
        int count = chunk[4];
        int length = chunk.Length - HeaderBytes;
        if (count == 0 || index >= count || length > chunkPayloadBytes) return false;
        bool last = index == count - 1;
        if (!last && length != chunkPayloadBytes) return false;
        if (last && count > 1 && length == 0) return false;

        streamId = chunk[0];
        sequence = BinaryPrimitives.ReadUInt16LittleEndian(chunk.Slice(1, 2));
        chunkIndex = index;
        chunkCount = count;
        bytes = chunk.Slice(HeaderBytes, length);
        return true;
    }

    /// <summary>The one width check every entry point shares, so the reassembler refuses at construction exactly
    /// what the static calls refuse.</summary>
    internal static void ThrowIfWidthOutOfRange(int chunkPayloadBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkPayloadBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(chunkPayloadBytes, (int)ushort.MaxValue);
    }

    static void ThrowIfOverCap(int payloadLength, int chunkPayloadBytes, string paramName)
    {
        int max = MaxChunks * chunkPayloadBytes;
        if (payloadLength > max)
            throw new ArgumentException(
                $"A fragmented payload is capped at {max} bytes ({MaxChunks} chunks of {chunkPayloadBytes}).",
                paramName);
    }
}
