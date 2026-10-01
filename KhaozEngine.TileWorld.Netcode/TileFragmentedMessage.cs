using System;
using KhaozEngine.Netcode;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// The tile protocol's fragment format: <see cref="MessageFragmenter"/> at the width one
/// <see cref="TileProtocol.EncodeGameMessage"/> envelope leaves, <see cref="MaxChunkPayloadBytes"/>. The header,
/// the chunk rules and what throws are the core type's.
/// <para>A chunk is what goes IN a game message payload: the caller wraps it with
/// <see cref="TileProtocol.EncodeGameMessage"/> under its own kind and sends it on the reliable ordered channel.
/// <see cref="TileFragmentReassembler"/> reads it back.</para>
/// </summary>
public static class TileFragmentedMessage
{
    /// <inheritdoc cref="MessageFragmenter.HeaderBytes"/>
    public const int HeaderBytes = MessageFragmenter.HeaderBytes;

    /// <inheritdoc cref="MessageFragmenter.MaxChunks"/>
    public const int MaxChunks = MessageFragmenter.MaxChunks;

    /// <summary>Conservative unpadded frame budget, including both headers. Separate from
    /// <see cref="TileProtocol.MaxGameMessageBytes"/>, which caps only the game payload. This preserves the
    /// 1015-byte non-final chunk width that readers require exactly, so using the four spare payload bytes
    /// would need a protocol migration. This is a fragment-format budget, not a transport datagram limit.</summary>
    public const int MaxUnpaddedFrameBytes = 1024;

    /// <summary>How many logical payload bytes one chunk carries: <see cref="MaxUnpaddedFrameBytes"/> less the
    /// game message envelope less <see cref="HeaderBytes"/>. A full chunk occupies 1020 bytes of the game payload,
    /// preserving the existing 1015-byte body and leaving four bytes below its payload cap. The envelope width is
    /// READ from <see cref="TileProtocol"/> rather than copied here.</summary>
    public const int MaxChunkPayloadBytes = MaxUnpaddedFrameBytes - TileProtocol.GameMessageHeader - HeaderBytes;

    /// <summary>The largest logical payload this format can carry, <see cref="MaxChunks"/> chunks of
    /// <see cref="MaxChunkPayloadBytes"/>.</summary>
    public const int MaxPayloadBytes = MaxChunks * MaxChunkPayloadBytes;

    /// <summary><see cref="MessageFragmenter.ChunkCount"/> at <see cref="MaxChunkPayloadBytes"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="payloadLength"/> is negative.</exception>
    /// <exception cref="ArgumentException"><paramref name="payloadLength"/> is above
    /// <see cref="MaxPayloadBytes"/>.</exception>
    public static int ChunkCount(int payloadLength) =>
        MessageFragmenter.ChunkCount(payloadLength, MaxChunkPayloadBytes);

    /// <summary><see cref="MessageFragmenter.Fragment"/> at <see cref="MaxChunkPayloadBytes"/>. Each chunk is a
    /// game message payload ready for <see cref="TileProtocol.EncodeGameMessage"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="payload"/> is longer than
    /// <see cref="MaxPayloadBytes"/>.</exception>
    public static byte[][] Fragment(byte streamId, ushort sequence, ReadOnlySpan<byte> payload) =>
        MessageFragmenter.Fragment(streamId, sequence, payload, MaxChunkPayloadBytes);

    /// <summary><see cref="MessageFragmenter.TryReadChunk"/> at <see cref="MaxChunkPayloadBytes"/>. Never throws,
    /// because the width is this type's own constant.</summary>
    public static bool TryReadChunk(ReadOnlySpan<byte> chunk, out byte streamId, out ushort sequence,
        out int chunkIndex, out int chunkCount, out ReadOnlySpan<byte> bytes) =>
        MessageFragmenter.TryReadChunk(chunk, MaxChunkPayloadBytes, out streamId, out sequence, out chunkIndex,
            out chunkCount, out bytes);
}
