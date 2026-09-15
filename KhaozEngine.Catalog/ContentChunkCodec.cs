using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace KhaozEngine.Catalog;

/// <summary>
/// The <c>KECC</c> chunk file of spec 7.2 and 7.3: a 36 byte header that is NEVER compressed, then a body
/// of a row table and the row bodies it points at.
/// <para>
/// <b>The body layout.</b> The table is <c>rowCount</c> entries of
/// <c>[definitionId varint][flags byte][rowLength varint]</c>, strictly ascending by id, and the row bodies
/// follow concatenated in the SAME order. A row's offset is not stored: it is the running sum of the
/// preceding lengths, computed in one pass, because an absolute offset per row would be a second
/// representation of the same fact that could disagree with the first. <c>flags</c> bit 0 is the retired
/// bit, so a reader answers <c>IsRetired(id)</c> from the table with no row decode.
/// </para>
/// <para>
/// <b>Every row body opens with the row's own key</b>, a varint length then that many UTF-8 bytes, and the
/// type's fields follow positionally. Spec 7.9's worked <c>tag</c> chunk, corrected to carry the key the
/// runtime reads out of the body rather than out of a second array (spec 9.1), is the format written out:
/// </para>
/// <code>
/// row 1, "metal", sort 10:        05 6D 65 74 61 6C 0A                          seven bytes
/// row 2, "two_handed", sort 20:   0A 74 77 6F 5F 68 61 6E 64 65 64 14           twelve bytes
/// table:                          01 00 07   02 01 0C                           six bytes
/// body:                           6 + 7 + 12 = 25 bytes
/// canonical:                      36 header + 25 body = 61 bytes
/// </code>
/// <para>
/// <b>The canonical bytes the hash is taken over are the UNCOMPRESSED ones</b>, with <c>compression</c>
/// forced to 0 and <c>storedBytes</c> forced equal to <c>uncompressedBytes</c>. That is what makes a
/// compressor change a no-op for every cached client (spec 6.7): the same rows compressed differently are a
/// different FILE at the same content address. The price is that the integrity check is LATE, because a
/// reader cannot verify a stored file without decompressing it first, so the two SIZE refusals are taken
/// from the 36 header bytes alone, before one body byte is read and before any buffer is sized.
/// </para>
/// <para>
/// Every decode path here is TOTAL. The bytes arrive from a remote peer, so a malformed file returns false
/// with a stable reason token and never throws. The ENCODE path is ours, so it throws on rows a publisher
/// should never have handed it: out of order, duplicated, or outside the chunk's own id range.
/// </para>
/// </summary>
public static class ContentChunkCodec
{
    /// <summary>A file too short to carry the 36 byte header at all.</summary>
    public const string ReasonTruncatedHeader = "chunk-truncated-header";

    /// <summary>The first four bytes are not <c>KECC</c>.</summary>
    public const string ReasonMagic = "chunk-magic";

    /// <summary>The format version is not this reader's, which is a refusal of the whole record.</summary>
    public const string ReasonFormatVersion = "chunk-format-version";

    /// <summary>The <c>reserved</c> field is non-zero, an extension this reader cannot skip.</summary>
    public const string ReasonReservedSet = "chunk-reserved-set";

    /// <summary>The <c>visibility</c> byte is outside the two-value vocabulary of contracts 11.1.</summary>
    public const string ReasonVisibility = "chunk-visibility";

    /// <summary>The <c>compression</c> byte names an algorithm this reader does not have.</summary>
    public const string ReasonCompression = "chunk-compression";

    /// <summary>
    /// The declared range is not self consistent, disagrees with the registry, or a row's id falls outside
    /// the chunk's own slots. All three mean the two sides disagree about what a chunk index means.
    /// </summary>
    public const string ReasonRangeMismatch = "chunk-range-mismatch";

    /// <summary>
    /// The declared <c>uncompressedBytes</c> is over <see cref="ContentPackFormat.MaxChunkUncompressedBytes"/>,
    /// or a Brotli body expanded past the length its own header claims.
    /// </summary>
    public const string ReasonTooLarge = ContentCompression.ReasonTooLarge;

    /// <summary>The declared <c>storedBytes</c> differs from the body actually received.</summary>
    public const string ReasonStoredLength = "chunk-stored-length";

    /// <summary>The row table cannot fit in the body the header declares.</summary>
    public const string ReasonRowTable = "chunk-row-table";

    /// <summary>The same definition id appears twice in the row table.</summary>
    public const string ReasonRowDuplicate = "chunk-row-duplicate";

    /// <summary>The row table is not strictly ascending by definition id.</summary>
    public const string ReasonRowOrder = "chunk-row-order";

    /// <summary>A row's flags byte carries one of the reserved bits 1 to 7.</summary>
    public const string ReasonRowFlags = "chunk-row-flags";

    /// <summary>A row body runs past the end of the body the header declares.</summary>
    public const string ReasonRowOverflow = "chunk-row-overflow";

    /// <summary>The rows do not reach the end of the body, so the file carries bytes nothing names.</summary>
    public const string ReasonTrailingBytes = "chunk-trailing-bytes";

    /// <summary>The Brotli body is not a stream this decoder can finish reading.</summary>
    public const string ReasonDecompress = ContentCompression.ReasonDecompress;

    /// <summary>A stored file's canonical bytes do not digest to the hash the manifest named.</summary>
    public const string ReasonHashMismatch = "hash-mismatch";

    /// <summary>A lookup for an id this chunk does not carry, which is a miss rather than a malformed file.</summary>
    public const string ReasonRowMissing = "chunk-row-missing";

    /// <summary>The fewest bytes a row table entry can take, a one byte id, the flags byte and a one byte length.</summary>
    const int MinRowTableEntryBytes = 3;

    /// <summary>
    /// Encodes one chunk: the canonical bytes, their hash, and the stored file. A body whose compressed form
    /// is not SMALLER than the body itself is stored uncompressed, so the format never pays a compression
    /// header to grow a file.
    /// </summary>
    /// <param name="type">The registration the id range and the slot count come from.</param>
    /// <param name="chunkIndex">The chunk's index within its type.</param>
    /// <param name="visibility">The side being encoded, which is part of the chunk's identity.</param>
    /// <param name="rows">The rows, strictly ascending by id and inside the chunk's own slots.</param>
    /// <param name="brotliQuality">
    /// The Brotli quality, defaulting to <see cref="ContentPackFormat.BrotliQuality"/>. It is a parameter
    /// because the hash is over the uncompressed bytes, so moving it changes the FILE and never the content
    /// address, and a test has to be able to prove that.
    /// </param>
    /// <exception cref="ArgumentNullException">A reference argument is null.</exception>
    /// <exception cref="ArgumentException">The rows are not strictly ascending, or the chunk is over the ceiling.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A row's id is outside the chunk's range, or an argument is out of range.</exception>
    public static EncodedContentChunk Encode(
        ContentTypeRegistration type,
        int chunkIndex,
        ContentVisibility visibility,
        IReadOnlyList<ContentChunkRow> rows,
        int brotliQuality = ContentPackFormat.BrotliQuality)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentOutOfRangeException.ThrowIfNegative(chunkIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(brotliQuality);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(brotliQuality, 11);

        long slotBase = (long)chunkIndex * type.ChunkSlots;
        if (slotBase + type.ChunkSlots > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(chunkIndex),
                chunkIndex,
                FormattableString.Invariant(
                    $"Chunk {chunkIndex} of type '{type.TypeKey}' covers ids from {slotBase}, which is past the positive int space contracts 5.1 gives a definition id."));
        }

        int bodyBytes = MeasureBody(type, rows, slotBase);
        long canonicalBytes = (long)ContentPackFormat.ChunkHeaderBytes + bodyBytes;
        if (canonicalBytes > ContentPackFormat.MaxChunkUncompressedBytes)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"Chunk {chunkIndex} of type '{type.TypeKey}' assembles to {canonicalBytes} canonical bytes, over the {ContentPackFormat.MaxChunkUncompressedBytes} byte ceiling, so no reader would load it."),
                nameof(rows));
        }

        byte[] canonical = new byte[canonicalBytes];
        WriteHeader(
            canonical,
            type.Type,
            chunkIndex,
            (uint)slotBase,
            (uint)type.ChunkSlots,
            rows.Count,
            visibility,
            ContentPackFormat.CompressionNone,
            bodyBytes,
            bodyBytes);
        WriteBody(canonical.AsSpan(ContentPackFormat.ChunkHeaderBytes), rows);

        string hash = ContentHash.OfChunk(canonical);
        ReadOnlySpan<byte> body = canonical.AsSpan(ContentPackFormat.ChunkHeaderBytes);
        byte[] scratch = new byte[body.Length];
        if (!ContentCompression.TryCompress(body, brotliQuality, scratch, out int compressed))
        {
            return new EncodedContentChunk(
                type.Type, chunkIndex, visibility, rows.Count, hash, canonical, canonical, false);
        }

        byte[] stored = new byte[ContentPackFormat.ChunkHeaderBytes + compressed];
        canonical.AsSpan(0, ContentPackFormat.ChunkHeaderBytes).CopyTo(stored);
        stored[25] = ContentPackFormat.CompressionBrotli;
        BinaryPrimitives.WriteUInt32LittleEndian(stored.AsSpan(32), (uint)compressed);
        scratch.AsSpan(0, compressed).CopyTo(stored.AsSpan(ContentPackFormat.ChunkHeaderBytes));

        return new EncodedContentChunk(
            type.Type, chunkIndex, visibility, rows.Count, hash, canonical, stored, true);
    }

    /// <summary>
    /// Reads the 36 header bytes and takes every refusal derivable from them ALONE, which is what lets a
    /// reader bound the resource cost of a file whose integrity it cannot check until after decompressing
    /// it. Allocates nothing.
    /// </summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> file, out ContentChunkHeader header, out string? reason)
    {
        header = default;
        if (file.Length < ContentPackFormat.ChunkHeaderBytes)
        {
            reason = ReasonTruncatedHeader;
            return false;
        }

        if (!file[..ContentPackFormat.MagicBytes].SequenceEqual(ContentPackFormat.ChunkMagic))
        {
            reason = ReasonMagic;
            return false;
        }

        ushort formatVersion = BinaryPrimitives.ReadUInt16LittleEndian(file[4..]);
        if (formatVersion != ContentPackFormat.ChunkFormatVersion)
        {
            reason = ReasonFormatVersion;
            return false;
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(file[26..]) != 0)
        {
            reason = ReasonReservedSet;
            return false;
        }

        byte visibility = file[24];
        if (visibility > ContentPackFormat.VisibilityServerOnly)
        {
            reason = ReasonVisibility;
            return false;
        }

        byte compression = file[25];
        if (compression > ContentPackFormat.CompressionBrotli)
        {
            reason = ReasonCompression;
            return false;
        }

        uint uncompressedBytes = BinaryPrimitives.ReadUInt32LittleEndian(file[28..]);
        if (uncompressedBytes > ContentPackFormat.MaxChunkUncompressedBytes)
        {
            reason = ReasonTooLarge;
            return false;
        }

        header = new ContentChunkHeader(
            formatVersion,
            new ContentTypeId(BinaryPrimitives.ReadUInt16LittleEndian(file[6..])),
            BinaryPrimitives.ReadUInt32LittleEndian(file[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(file[12..]),
            BinaryPrimitives.ReadUInt32LittleEndian(file[16..]),
            BinaryPrimitives.ReadUInt32LittleEndian(file[20..]),
            visibility,
            compression,
            uncompressedBytes,
            BinaryPrimitives.ReadUInt32LittleEndian(file[32..]));
        reason = null;
        return true;
    }

    /// <summary>
    /// Decodes a stored chunk file. Returns false with a stable reason token rather than throwing.
    /// <para>
    /// <paramref name="registry"/> is what the declared slot count is checked against, which a reader
    /// validating a chunk it just downloaded needs so the two sides cannot disagree about what a chunk
    /// index means. Null skips that one check, for a tool reading a chunk of a type it does not register,
    /// and so does a type the registry does not carry: an unknown type is the CALLER's decision to take,
    /// never this decoder's, and every structural refusal is taken either way.
    /// </para>
    /// </summary>
    public static bool TryDecode(
        ReadOnlySpan<byte> file,
        ContentTypeRegistry? registry,
        [MaybeNullWhen(false)] out ContentChunk chunk,
        out string? reason)
    {
        chunk = null;
        if (!TryReadHeader(file, out ContentChunkHeader header, out reason)
            || !CheckRange(header, file.Length, registry, out reason))
        {
            return false;
        }

        byte[] body = new byte[header.UncompressedBytes];
        if (!TryFillBody(file[ContentPackFormat.ChunkHeaderBytes..], header, body, out reason))
        {
            return false;
        }

        return TryWalkBody(header, body, out chunk, out reason);
    }

    /// <summary>
    /// The LOAD path's one pass: verify against a content address and decode, over a SINGLE decompression.
    /// Calling <see cref="TryVerify"/> and then <see cref="TryDecode"/> on the same file decompresses the
    /// same stored bytes twice and allocates the uncompressed body twice, which is what boot used to do for
    /// every chunk of every type.
    /// <para>
    /// <b>Hash before trust, and that ordering is the whole licence for the saving.</b> The digest over the
    /// rebuilt canonical header plus the body is compared BEFORE the row table is walked, so no row ever
    /// escapes a buffer nothing signed. Folding the two calls together would otherwise quietly drop
    /// <see cref="TryVerify"/>'s weaker header check for <see cref="TryDecode"/>'s, so the full range set
    /// runs here, including the row-count guard that bounds the four parallel arrays.
    /// </para>
    /// <para>
    /// <see cref="TryVerify"/> and <see cref="TryDecode"/> both stay as they are, for the callers that hold
    /// only one of the two questions: a store filing an object it cannot decode, and a tool reading a chunk
    /// it was handed without an address.
    /// </para>
    /// </summary>
    /// <param name="file">The stored chunk file.</param>
    /// <param name="registry">The local registry, or null to skip the per-type slot-count cross-check.</param>
    /// <param name="expectedHash">The content address the file was fetched under.</param>
    /// <param name="chunk">The decoded chunk, or null on any refusal.</param>
    /// <param name="reason">The stable reason token, or null on success.</param>
    /// <exception cref="ArgumentNullException"><paramref name="expectedHash"/> is null.</exception>
    public static bool TryDecodeVerified(
        ReadOnlySpan<byte> file,
        ContentTypeRegistry? registry,
        string expectedHash,
        [MaybeNullWhen(false)] out ContentChunk chunk,
        out string? reason)
    {
        ArgumentNullException.ThrowIfNull(expectedHash);

        chunk = null;
        if (!TryReadHeader(file, out ContentChunkHeader header, out reason)
            || !CheckRange(header, file.Length, registry, out reason))
        {
            return false;
        }

        byte[] body = new byte[header.UncompressedBytes];
        if (!TryFillBody(file[ContentPackFormat.ChunkHeaderBytes..], header, body, out reason)
            || !MatchesHash(file, header, body, expectedHash, out reason))
        {
            return false;
        }

        return TryWalkBody(header, body, out chunk, out reason);
    }

    /// <summary>
    /// Verifies a stored file against a content address without decoding its rows: decompress, rebuild the
    /// canonical header, digest. That is one header rewrite of 36 bytes per verification, which is the
    /// price of a hash that a compressor change cannot move.
    /// </summary>
    public static bool TryVerify(ReadOnlySpan<byte> file, string expectedHash, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(expectedHash);
        if (!TryReadHeader(file, out ContentChunkHeader header, out reason)
            || !CheckLengths(header, file.Length, out reason))
        {
            return false;
        }

        byte[] body = new byte[header.UncompressedBytes];
        if (!TryFillBody(file[ContentPackFormat.ChunkHeaderBytes..], header, body, out reason))
        {
            return false;
        }

        return MatchesHash(file, header, body, expectedHash, out reason);
    }

    /// <summary>
    /// Digests the decompressed body under the REBUILT canonical header, which is the 36 stored bytes with
    /// <c>compression</c> forced to 0 and <c>storedBytes</c> forced equal to <c>uncompressedBytes</c>, and
    /// compares it to the address the file arrived under. That is what makes a compressor change a no-op for
    /// every cached client.
    /// </summary>
    static bool MatchesHash(
        ReadOnlySpan<byte> file,
        in ContentChunkHeader header,
        ReadOnlySpan<byte> body,
        string expectedHash,
        out string? reason)
    {
        Span<byte> canonicalHeader = stackalloc byte[ContentPackFormat.ChunkHeaderBytes];
        file[..ContentPackFormat.ChunkHeaderBytes].CopyTo(canonicalHeader);
        canonicalHeader[25] = ContentPackFormat.CompressionNone;
        BinaryPrimitives.WriteUInt32LittleEndian(canonicalHeader[32..], header.UncompressedBytes);

        string actual = ContentHash.OfChunkStreaming(canonicalHeader, body, ContentHash.ChunkDomain);
        if (!string.Equals(actual, expectedHash, StringComparison.Ordinal))
        {
            reason = ReasonHashMismatch;
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// Walks the row table of an already-decompressed body and builds the chunk over it, with no further
    /// copy: the body array becomes the chunk's own.
    /// </summary>
    static bool TryWalkBody(
        in ContentChunkHeader header,
        byte[] body,
        [MaybeNullWhen(false)] out ContentChunk chunk,
        out string? reason)
    {
        chunk = null;
        int rowCount = (int)header.RowCount;
        int[] ids = new int[rowCount];
        int[] offsets = new int[rowCount];
        int[] lengths = new int[rowCount];
        bool[] retired = new bool[rowCount];
        if (!TryWalkRowTable(body, header, ids, offsets, lengths, retired, out reason))
        {
            return false;
        }

        chunk = new ContentChunk(
            header.Type,
            (int)header.ChunkIndex,
            (int)header.SlotBase,
            (int)header.SlotCount,
            header.Visibility == ContentPackFormat.VisibilityServerOnly
                ? ContentVisibility.ServerOnly
                : ContentVisibility.Client,
            body,
            ids,
            offsets,
            lengths,
            retired);
        reason = null;
        return true;
    }

    static int MeasureBody(ContentTypeRegistration type, IReadOnlyList<ContentChunkRow> rows, long slotBase)
    {
        long total = 0;
        long previousId = -1;
        for (int i = 0; i < rows.Count; i++)
        {
            ContentChunkRow row = rows[i];
            if (row.DefinitionId < slotBase || row.DefinitionId >= slotBase + type.ChunkSlots)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(rows),
                    row.DefinitionId,
                    FormattableString.Invariant(
                        $"Row {row.DefinitionId} is outside chunk {slotBase / type.ChunkSlots} of type '{type.TypeKey}', which covers ids {slotBase} to {slotBase + type.ChunkSlots - 1}."));
            }

            if (row.DefinitionId == previousId)
            {
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"Row {row.DefinitionId} of type '{type.TypeKey}' appears twice, and a chunk's row table is strictly ascending by id."),
                    nameof(rows));
            }

            if (row.DefinitionId < previousId)
            {
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"Row {row.DefinitionId} of type '{type.TypeKey}' follows {previousId}, and a chunk's row table is strictly ascending by id. Sorting is the publisher's, so that two processes registering the same types in different orders produce byte-identical packs."),
                    nameof(rows));
            }

            previousId = row.DefinitionId;
            total += ContentVarint.Size((uint)row.DefinitionId)
                + 1
                + ContentVarint.Size((uint)row.Body.Length)
                + row.Body.Length;
        }

        return (int)total;
    }

    static void WriteBody(Span<byte> destination, IReadOnlyList<ContentChunkRow> rows)
    {
        int written = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            ContentChunkRow row = rows[i];
            written += ContentVarint.Write(destination[written..], (uint)row.DefinitionId);
            destination[written++] = row.IsRetired ? (byte)1 : (byte)0;
            written += ContentVarint.Write(destination[written..], (uint)row.Body.Length);
        }

        for (int i = 0; i < rows.Count; i++)
        {
            rows[i].Body.Span.CopyTo(destination[written..]);
            written += rows[i].Body.Length;
        }
    }

    static void WriteHeader(
        Span<byte> destination,
        ContentTypeId type,
        int chunkIndex,
        uint slotBase,
        uint slotCount,
        int rowCount,
        ContentVisibility visibility,
        byte compression,
        int uncompressedBytes,
        int storedBytes)
    {
        ContentPackFormat.ChunkMagic.CopyTo(destination);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], ContentPackFormat.ChunkFormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], type.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], (uint)chunkIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], slotBase);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[16..], slotCount);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[20..], (uint)rowCount);
        destination[24] = visibility == ContentVisibility.ServerOnly
            ? ContentPackFormat.VisibilityServerOnly
            : ContentPackFormat.VisibilityClient;
        destination[25] = compression;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[26..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[28..], (uint)uncompressedBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[32..], (uint)storedBytes);
    }

    static bool CheckLengths(in ContentChunkHeader header, int fileLength, out string? reason)
    {
        if (header.StoredBytes != (uint)(fileLength - ContentPackFormat.ChunkHeaderBytes))
        {
            reason = ReasonStoredLength;
            return false;
        }

        if (!header.IsCompressed && header.StoredBytes != header.UncompressedBytes)
        {
            reason = ReasonStoredLength;
            return false;
        }

        reason = null;
        return true;
    }

    static bool CheckRange(
        in ContentChunkHeader header,
        int fileLength,
        ContentTypeRegistry? registry,
        out string? reason)
    {
        if (!CheckLengths(header, fileLength, out reason))
        {
            return false;
        }

        // A row table entry is three bytes at its smallest, so a row count the body cannot hold is refused
        // before the four parallel arrays it would otherwise size are allocated.
        if ((long)header.RowCount * MinRowTableEntryBytes > header.UncompressedBytes)
        {
            reason = ReasonRowTable;
            return false;
        }

        uint slotCount = header.SlotCount;
        if (slotCount < ContentTypeRegistry.MinChunkSlots
            || slotCount > ContentTypeRegistry.MaxChunkSlots
            || (slotCount & (slotCount - 1)) != 0
            || (ulong)header.SlotBase != (ulong)header.ChunkIndex * slotCount
            || (ulong)header.SlotBase + slotCount > int.MaxValue)
        {
            reason = ReasonRangeMismatch;
            return false;
        }

        if (registry is not null
            && registry.TryGet(header.Type, out ContentTypeRegistration? registration)
            && registration.ChunkSlots != (int)slotCount)
        {
            reason = ReasonRangeMismatch;
            return false;
        }

        reason = null;
        return true;
    }

    static bool TryFillBody(
        ReadOnlySpan<byte> stored,
        in ContentChunkHeader header,
        Span<byte> body,
        out string? reason)
    {
        if (!header.IsCompressed)
        {
            stored.CopyTo(body);
            reason = null;
            return true;
        }

        // The stream expanding past the length its own container claims is the case the bounded buffer
        // exists for, and ContentCompression is what keeps it a resource refusal rather than a corrupt
        // stream in all three chunk formats.
        return ContentCompression.TryFill(stored, body, out reason);
    }

    static bool TryWalkRowTable(
        ReadOnlySpan<byte> body,
        in ContentChunkHeader header,
        int[] ids,
        int[] offsets,
        int[] lengths,
        bool[] retired,
        out string? reason)
    {
        long slotBase = header.SlotBase;
        long slotLimit = slotBase + header.SlotCount;
        int cursor = 0;
        long previousId = -1;
        for (int i = 0; i < ids.Length; i++)
        {
            if (!ContentVarint.TryRead(body, ref cursor, out uint rawId, out reason))
            {
                return false;
            }

            if (cursor >= body.Length)
            {
                reason = ContentVarint.ReasonTruncated;
                return false;
            }

            byte flags = body[cursor++];
            if ((flags & 0xFE) != 0)
            {
                reason = ReasonRowFlags;
                return false;
            }

            if (!ContentVarint.TryRead(body, ref cursor, out uint rawLength, out reason))
            {
                return false;
            }

            if (rawId == previousId)
            {
                reason = ReasonRowDuplicate;
                return false;
            }

            if (rawId < previousId)
            {
                reason = ReasonRowOrder;
                return false;
            }

            if (rawId < slotBase || rawId >= slotLimit)
            {
                reason = ReasonRangeMismatch;
                return false;
            }

            if (rawLength > (uint)body.Length)
            {
                reason = ReasonRowOverflow;
                return false;
            }

            previousId = rawId;
            ids[i] = (int)rawId;
            lengths[i] = (int)rawLength;
            retired[i] = (flags & 1) != 0;
        }

        long running = cursor;
        for (int i = 0; i < ids.Length; i++)
        {
            offsets[i] = (int)running;
            running += lengths[i];
            if (running > body.Length)
            {
                reason = ReasonRowOverflow;
                return false;
            }
        }

        if (running != body.Length)
        {
            reason = ReasonTrailingBytes;
            return false;
        }

        reason = null;
        return true;
    }
}
