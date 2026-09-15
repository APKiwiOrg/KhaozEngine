using System;
using System.Diagnostics.CodeAnalysis;

namespace KhaozEngine.Catalog;

/// <summary>
/// One row on its way INTO a chunk: the identity the row TABLE owns and the body its codec produced. The
/// id and the retired bit live in the table rather than in the body (spec 7.3), which is what lets a reader
/// answer <c>IsRetired(id)</c> with no row decode at all.
/// </summary>
public readonly struct ContentChunkRow : IEquatable<ContentChunkRow>
{
    /// <summary>Builds one table entry and the body it points at.</summary>
    public ContentChunkRow(int definitionId, bool isRetired, ReadOnlyMemory<byte> body)
    {
        DefinitionId = definitionId;
        IsRetired = isRetired;
        Body = body;
    }

    /// <summary>The definition id, which is strictly ascending across a chunk's rows.</summary>
    public int DefinitionId { get; }

    /// <summary>The retired bit, <c>flags</c> bit 0 in the table.</summary>
    public bool IsRetired { get; }

    /// <summary>The encoded row body: the key, length prefixed, then the type's fields positionally.</summary>
    public ReadOnlyMemory<byte> Body { get; }

    /// <inheritdoc />
    public bool Equals(ContentChunkRow other)
        => DefinitionId == other.DefinitionId
            && IsRetired == other.IsRetired
            && Body.Span.SequenceEqual(other.Body.Span);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ContentChunkRow other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(DefinitionId, IsRetired, Body.Length);

    /// <summary>Value equality, with the body compared by content rather than by reference.</summary>
    public static bool operator ==(ContentChunkRow left, ContentChunkRow right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(ContentChunkRow left, ContentChunkRow right) => !left.Equals(right);
}

/// <summary>
/// The 36 fixed bytes at the front of a <c>KECC</c> file (spec 7.2), which are NEVER compressed. A reader
/// learns the type, the id range, the row count and the two lengths from them without touching the
/// compressor, which is what makes the size refusals of 7.2 cheap enough to take before any allocation.
/// </summary>
public readonly struct ContentChunkHeader
{
    internal ContentChunkHeader(
        ushort formatVersion,
        ContentTypeId type,
        uint chunkIndex,
        uint slotBase,
        uint slotCount,
        uint rowCount,
        byte visibility,
        byte compression,
        uint uncompressedBytes,
        uint storedBytes)
    {
        FormatVersion = formatVersion;
        Type = type;
        ChunkIndex = chunkIndex;
        SlotBase = slotBase;
        SlotCount = slotCount;
        RowCount = rowCount;
        Visibility = visibility;
        Compression = compression;
        UncompressedBytes = uncompressedBytes;
        StoredBytes = storedBytes;
    }

    /// <summary>The format version, which a reader refuses rather than partially reads when it differs.</summary>
    public ushort FormatVersion { get; }

    /// <summary>The content type whose rows this chunk carries.</summary>
    public ContentTypeId Type { get; }

    /// <summary>The chunk's index within its type, <c>definitionId / slotCount</c>.</summary>
    public uint ChunkIndex { get; }

    /// <summary>The first definition id in the chunk's range, carried explicitly rather than derived.</summary>
    public uint SlotBase { get; }

    /// <summary>The id slots in the range, a power of two between 256 and 65,536 (contracts 4.5).</summary>
    public uint SlotCount { get; }

    /// <summary>The number of rows in the table, which is not the number of slots.</summary>
    public uint RowCount { get; }

    /// <summary>The raw visibility byte, 0 client and 1 server only.</summary>
    public byte Visibility { get; }

    /// <summary>The raw compression byte, 0 none and 1 Brotli.</summary>
    public byte Compression { get; }

    /// <summary>The body's length BEFORE decompression, and the size the reader's buffer is bounded to.</summary>
    public uint UncompressedBytes { get; }

    /// <summary>The body's length AS STORED, which the delivered body length has to match exactly.</summary>
    public uint StoredBytes { get; }

    /// <summary>True when the body is a Brotli stream rather than the canonical bytes themselves.</summary>
    public bool IsCompressed => Compression != ContentPackFormat.CompressionNone;
}

/// <summary>
/// An encoded chunk: the canonical bytes the hash is taken over, the content address, and the stored file
/// as it goes to the pack store.
/// <para>
/// <see cref="Canonical"/> and <see cref="StoredFile"/> differ in exactly two header fields when the body
/// compressed, the <c>compression</c> byte and <c>storedBytes</c>. That is the whole mechanism behind the
/// hash being independent of the compressor (spec 6.7): a later engine build that compresses better
/// produces a different FILE at the same content address, so a client already holding the chunk fetches
/// nothing.
/// </para>
/// </summary>
public sealed class EncodedContentChunk
{
    internal EncodedContentChunk(
        ContentTypeId type,
        int chunkIndex,
        ContentVisibility visibility,
        int rowCount,
        string hash,
        byte[] canonical,
        byte[] storedFile,
        bool isCompressed)
    {
        Type = type;
        ChunkIndex = chunkIndex;
        Visibility = visibility;
        RowCount = rowCount;
        Hash = hash;
        Canonical = canonical;
        StoredFile = storedFile;
        IsCompressed = isCompressed;
    }

    /// <summary>The content type this chunk carries.</summary>
    public ContentTypeId Type { get; }

    /// <summary>The chunk's index within its type.</summary>
    public int ChunkIndex { get; }

    /// <summary>The side this chunk was encoded for, which is part of its identity and not a flag on it.</summary>
    public ContentVisibility Visibility { get; }

    /// <summary>The rows in the table.</summary>
    public int RowCount { get; }

    /// <summary>The chunk's content address, lower hex, over <see cref="Canonical"/>.</summary>
    public string Hash { get; }

    /// <summary>
    /// The UNCOMPRESSED canonical bytes: the header with <c>compression</c> forced to 0 and
    /// <c>storedBytes</c> forced equal to <c>uncompressedBytes</c>, then the uncompressed body.
    /// </summary>
    public ReadOnlyMemory<byte> Canonical { get; }

    /// <summary>The file as it is written to the pack store and served to a client.</summary>
    public ReadOnlyMemory<byte> StoredFile { get; }

    /// <summary>True when the body compressed smaller and is therefore stored as a Brotli stream.</summary>
    public bool IsCompressed { get; }

    /// <summary>The body's length before decompression, which is the manifest's per-chunk figure.</summary>
    public int UncompressedBytes => Canonical.Length - ContentPackFormat.ChunkHeaderBytes;

    /// <summary>The whole stored file's length, header included.</summary>
    public int StoredBytes => StoredFile.Length;
}

/// <summary>
/// A decoded chunk: the uncompressed body and the walked row table beside it.
/// <para>
/// A row's OFFSET is not stored in the file. It is the running sum of the preceding lengths, computed in
/// ONE pass at decode and held here, because storing an absolute offset per row would give a second
/// representation of the same fact that could disagree with the first (spec 7.3).
/// </para>
/// </summary>
public sealed class ContentChunk
{
    readonly byte[] _body;
    readonly int[] _ids;
    readonly int[] _offsets;
    readonly int[] _lengths;
    readonly bool[] _retired;

    internal ContentChunk(
        ContentTypeId type,
        int chunkIndex,
        int slotBase,
        int slotCount,
        ContentVisibility visibility,
        byte[] body,
        int[] ids,
        int[] offsets,
        int[] lengths,
        bool[] retired)
    {
        Type = type;
        ChunkIndex = chunkIndex;
        SlotBase = slotBase;
        SlotCount = slotCount;
        Visibility = visibility;
        _body = body;
        _ids = ids;
        _offsets = offsets;
        _lengths = lengths;
        _retired = retired;
    }

    /// <summary>The content type this chunk carries.</summary>
    public ContentTypeId Type { get; }

    /// <summary>The chunk's index within its type.</summary>
    public int ChunkIndex { get; }

    /// <summary>The first definition id in the chunk's range.</summary>
    public int SlotBase { get; }

    /// <summary>The id slots in the range.</summary>
    public int SlotCount { get; }

    /// <summary>The side this chunk was encoded for.</summary>
    public ContentVisibility Visibility { get; }

    /// <summary>The rows in the table.</summary>
    public int RowCount => _ids.Length;

    /// <summary>The uncompressed body, the row table then the row bodies.</summary>
    public ReadOnlySpan<byte> Body => _body;

    /// <summary>The definition ids, ascending, one per row.</summary>
    public ReadOnlySpan<int> Ids => _ids;

    /// <summary>Each row body's offset into <see cref="Body"/>, parallel to <see cref="Ids"/>.</summary>
    public ReadOnlySpan<int> Offsets => _offsets;

    /// <summary>Each row body's length, parallel to <see cref="Ids"/>.</summary>
    public ReadOnlySpan<int> Lengths => _lengths;

    /// <summary>Finds a row's index by definition id, a binary search over the ascending ids.</summary>
    public bool TryGetIndex(int definitionId, out int index)
    {
        int low = 0;
        int high = _ids.Length - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            int candidate = _ids[middle];
            if (candidate == definitionId)
            {
                index = middle;
                return true;
            }

            if (candidate < definitionId)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        index = -1;
        return false;
    }

    /// <summary>
    /// Whether a row is retired, answered from the row TABLE with no row decode at all (spec 3.9). An id
    /// this chunk does not carry is not retired, because it is not a row.
    /// </summary>
    public bool IsRetired(int definitionId) => TryGetIndex(definitionId, out int index) && _retired[index];

    /// <summary>The definition id at a table index.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the table.</exception>
    public int DefinitionIdAt(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _ids.Length);
        return _ids[index];
    }

    /// <summary>The retired bit at a table index.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the table.</exception>
    public bool IsRetiredAt(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _ids.Length);
        return _retired[index];
    }

    /// <summary>One row's body as a slice of <see cref="Body"/>, with no copy.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the table.</exception>
    public ReadOnlySpan<byte> RowBodyAt(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _ids.Length);
        return _body.AsSpan(_offsets[index], _lengths[index]);
    }

    /// <summary>
    /// One row's body as a <see cref="ReadOnlyMemory{T}"/> slice of <see cref="Body"/>, with no copy. It is
    /// the same bytes <see cref="RowBodyAt"/> gives, in the form that can be STORED: the chunk owns the
    /// array, so a caller keeping the slice (the pack reader hands it to
    /// <see cref="ContentSnapshotBuilder.AddRow(ContentRow, ReadOnlyMemory{byte})"/>) keeps the chunk alive
    /// rather than copying out of it. Spec 9.2 asks for no copy beyond the decompress, and a per-row
    /// <c>ToArray</c> on the load path is exactly the copy it names.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the table.</exception>
    public ReadOnlyMemory<byte> RowBodyMemoryAt(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _ids.Length);
        return _body.AsMemory(_offsets[index], _lengths[index]);
    }

    /// <summary>One row's body by definition id, with no copy.</summary>
    public bool TryGetRowBody(int definitionId, out ReadOnlySpan<byte> body)
    {
        if (!TryGetIndex(definitionId, out int index))
        {
            body = default;
            return false;
        }

        body = _body.AsSpan(_offsets[index], _lengths[index]);
        return true;
    }

    /// <summary>
    /// Decodes one row at a table index and puts the identity the TABLE owns back on it: the definition id
    /// and the retired bit, neither of which is in the body. Returns false with a reason token rather than
    /// throwing, because the bytes arrived from a remote peer.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the table.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="codec"/> is null.</exception>
    public bool TryDecodeRowAt(
        int index,
        IContentRowCodec codec,
        [MaybeNullWhen(false)] out ContentRow row,
        out string? reason)
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _ids.Length);

        if (!codec.TryDecode(_body.AsSpan(_offsets[index], _lengths[index]), out ContentRow? decoded, out reason))
        {
            row = null;
            return false;
        }

        row = decoded.WithIdentity(_ids[index], _retired[index]);
        reason = null;
        return true;
    }

    /// <summary>
    /// Decodes one row by definition id. An id this chunk does not carry returns false with
    /// <see cref="ContentChunkCodec.ReasonRowMissing"/>, which is a lookup miss rather than a malformed
    /// file.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="codec"/> is null.</exception>
    public bool TryDecodeRow(
        int definitionId,
        IContentRowCodec codec,
        [MaybeNullWhen(false)] out ContentRow row,
        out string? reason)
    {
        ArgumentNullException.ThrowIfNull(codec);
        if (!TryGetIndex(definitionId, out int index))
        {
            row = null;
            reason = ContentChunkCodec.ReasonRowMissing;
            return false;
        }

        return TryDecodeRowAt(index, codec, out row, out reason);
    }
}
