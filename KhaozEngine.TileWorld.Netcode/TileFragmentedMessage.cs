using System;
using System.Buffers.Binary;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// Splits a logical payload too large for one game message into a run of chunks, each of which fits inside one
/// <see cref="TileProtocol.EncodeGameMessage"/> envelope, and reads one back off the wire.
/// <para>ITEM AGNOSTIC, and deliberately so: this type moves bytes and never looks at them. What a stream carries,
/// what kind the game gives the frames, and what decodes the reassembled bytes are all the caller's, exactly as
/// the game message envelope itself keeps a payload opaque.</para>
/// <para>The header is five bytes and every field is fixed width, so nothing here needs a varint reader:</para>
/// <code>
/// [StreamId: byte]        // which logical stream, the GAME assigns these
/// [Sequence: uint16 LE]   // increments per transmission of that stream, wraps
/// [ChunkIndex: byte]
/// [ChunkCount: byte]      // 1 to 255
/// [Bytes: the rest]
/// </code>
/// <para>A chunk is what goes IN a game message payload, not a frame: the caller wraps it with
/// <see cref="TileProtocol.EncodeGameMessage"/> under its own kind and sends it on the reliable ordered channel.
/// So a chunk carries <see cref="TileProtocol.MaxGameMessageBytes"/> less the four byte envelope less this header,
/// which is <see cref="MaxChunkPayloadBytes"/>, and the whole datagram still lands on the cap. The envelope width
/// is READ from <see cref="TileProtocol"/> rather than written here, so the two cannot drift.</para>
/// <para><see cref="Fragment"/> THROWS above <see cref="MaxPayloadBytes"/>, on the same grounds as the game
/// message cap throw: a payload that long is a local caller bug and worth the stack. Everything on the READING
/// side is total and never throws, because those bytes came from a remote peer. See
/// <see cref="TileFragmentReassembler"/>.</para>
/// </summary>
public static class TileFragmentedMessage
{
    /// <summary>Width of the chunk header, in bytes: stream id, sequence, chunk index, chunk count.</summary>
    public const int HeaderBytes = 5;

    /// <summary>The most chunks one transmission can be split into, because <c>ChunkCount</c> is a byte.</summary>
    public const int MaxChunks = 255;

    /// <summary>How many payload bytes one chunk carries. Derived from <see cref="TileProtocol.MaxGameMessageBytes"/>
    /// less the game message envelope less <see cref="HeaderBytes"/>, so raising the frame cap raises this with no
    /// second edit.</summary>
    public const int MaxChunkPayloadBytes = TileProtocol.MaxGameMessageBytes - TileProtocol.GameMessageHeader - HeaderBytes;

    /// <summary>The largest logical payload this format can carry, <see cref="MaxChunks"/> chunks of
    /// <see cref="MaxChunkPayloadBytes"/>. About 258 KB, which is forty times the largest container page the item
    /// design sizes against.</summary>
    public const int MaxPayloadBytes = MaxChunks * MaxChunkPayloadBytes;

    /// <summary>How many chunks a payload of this length becomes. An empty payload is ONE chunk carrying nothing,
    /// so a caller with nothing to say is not a special case on either side.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="payloadLength"/> is negative.</exception>
    /// <exception cref="ArgumentException"><paramref name="payloadLength"/> is above
    /// <see cref="MaxPayloadBytes"/>.</exception>
    public static int ChunkCount(int payloadLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(payloadLength);
        if (payloadLength > MaxPayloadBytes)
            throw new ArgumentException(
                $"A fragmented payload is capped at {MaxPayloadBytes} bytes ({MaxChunks} chunks of {MaxChunkPayloadBytes}).",
                nameof(payloadLength));
        return payloadLength == 0 ? 1 : (payloadLength + MaxChunkPayloadBytes - 1) / MaxChunkPayloadBytes;
    }

    /// <summary>
    /// Splits a payload into chunks, each one a game message payload ready for
    /// <see cref="TileProtocol.EncodeGameMessage"/>. Every chunk but the last carries a FULL
    /// <see cref="MaxChunkPayloadBytes"/>, which is what lets a reader tell a truncated chunk from a legitimately
    /// short final one.
    /// <para><paramref name="sequence"/> names this transmission of <paramref name="streamId"/> and is the caller's
    /// to increment, wrapping freely. A reader uses it for CONSISTENCY rather than ordering: the channel is
    /// reliable ordered, so a change of sequence means the sender restarted the stream, never that something
    /// arrived late.</para>
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="payload"/> is longer than
    /// <see cref="MaxPayloadBytes"/>.</exception>
    public static byte[][] Fragment(byte streamId, ushort sequence, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayloadBytes)
            throw new ArgumentException(
                $"A fragmented payload is capped at {MaxPayloadBytes} bytes ({MaxChunks} chunks of {MaxChunkPayloadBytes}).",
                nameof(payload));

        int count = ChunkCount(payload.Length);
        var chunks = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            int offset = i * MaxChunkPayloadBytes;
            int take = Math.Min(MaxChunkPayloadBytes, payload.Length - offset);
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
    /// Reads one chunk's header and slices its bytes. NEVER throws, on the rule every frame decoder in
    /// <see cref="TileProtocol"/> follows: the bytes came from a remote peer, so a malformed one is a false and a
    /// dropped chunk rather than an exception out of the receive loop.
    /// <para>False for a chunk shorter than <see cref="HeaderBytes"/>, a chunk count of zero, an index at or past
    /// the count, a chunk carrying more than <see cref="MaxChunkPayloadBytes"/>, a NON final chunk that is not
    /// full, and a final chunk of a multi chunk transmission carrying nothing. The last two are the strictness
    /// that makes truncation detectable: <see cref="Fragment"/> emits exactly one wire form per payload, so a
    /// short non final chunk is a chunk that lost bytes on the way. A final chunk cut in its BODY is NOT
    /// detectable here, because the header declares no total length, and it is the caller's decoder that refuses
    /// the short payload.</para>
    /// <para><paramref name="bytes"/> is a SLICE of <paramref name="chunk"/>, so nothing is copied and nothing is
    /// interpreted.</para>
    /// </summary>
    public static bool TryReadChunk(ReadOnlySpan<byte> chunk, out byte streamId, out ushort sequence,
        out int chunkIndex, out int chunkCount, out ReadOnlySpan<byte> bytes)
    {
        streamId = 0;
        sequence = 0;
        chunkIndex = 0;
        chunkCount = 0;
        bytes = default;
        if (chunk.Length < HeaderBytes) return false;

        int index = chunk[3];
        int count = chunk[4];
        int length = chunk.Length - HeaderBytes;
        if (count == 0 || index >= count || length > MaxChunkPayloadBytes) return false;
        bool last = index == count - 1;
        if (!last && length != MaxChunkPayloadBytes) return false;
        if (last && count > 1 && length == 0) return false;

        streamId = chunk[0];
        sequence = BinaryPrimitives.ReadUInt16LittleEndian(chunk.Slice(1, 2));
        chunkIndex = index;
        chunkCount = count;
        bytes = chunk.Slice(HeaderBytes, length);
        return true;
    }
}
