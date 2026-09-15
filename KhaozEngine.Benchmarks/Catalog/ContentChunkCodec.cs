using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Compression;

namespace KhaozEngine.Benchmarks.Catalog;

/// <summary>One row on its way into a chunk: its id, its retired bit and its encoded body.</summary>
public readonly struct ChunkRowInput
{
    public ChunkRowInput(int definitionId, bool retired, ReadOnlyMemory<byte> body)
    {
        DefinitionId = definitionId;
        Retired = retired;
        Body = body;
    }

    public int DefinitionId { get; }
    public bool Retired { get; }
    public ReadOnlyMemory<byte> Body { get; }
}

/// <summary>An encoded chunk: the stored file, its canonical hash, and both lengths.</summary>
public sealed class EncodedChunk
{
    public required ushort TypeId { get; init; }
    public required int ChunkIndex { get; init; }
    public required byte Visibility { get; init; }
    public required string Hash { get; init; }
    public required byte[] StoredFile { get; init; }
    public required int UncompressedBytes { get; init; }
    public required int RowCount { get; init; }

    public int StoredBytes => StoredFile.Length;
}

/// <summary>A decoded chunk: the uncompressed body plus the walked row table.</summary>
public sealed class DecodedChunk
{
    public required ushort TypeId { get; init; }
    public required int ChunkIndex { get; init; }
    public required int SlotBase { get; init; }
    public required int SlotCount { get; init; }
    public required byte Visibility { get; init; }
    public required byte[] Body { get; init; }
    public required int[] Ids { get; init; }
    public required int[] Offsets { get; init; }
    public required int[] Lengths { get; init; }
    public required bool[] Retired { get; init; }
}

/// <summary>
/// The <c>KECC</c> chunk file of spec sections 7.2 and 7.3: a 36 byte never-compressed header, a row table
/// of ascending ids, and the row bodies. The hash is over the canonical UNCOMPRESSED bytes, which is what
/// makes it independent of the compressor. Every decode path is total and returns a reason token.
/// </summary>
public static class ContentChunkCodec
{
    public static readonly byte[] Magic = "KECC"u8.ToArray();

    public static EncodedChunk Encode(
        ContentTypeDescriptor type,
        int chunkIndex,
        byte visibility,
        IReadOnlyList<ChunkRowInput> rows)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(rows);

        int tableBytes = 0;
        int bodyBytes = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            tableBytes += ContentVarint.Size((uint)rows[i].DefinitionId) + 1
                + ContentVarint.Size((uint)rows[i].Body.Length);
            bodyBytes += rows[i].Body.Length;
        }

        byte[] body = new byte[tableBytes + bodyBytes];
        int written = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            written += ContentVarint.Write(body.AsSpan(written), (uint)rows[i].DefinitionId);
            body[written++] = rows[i].Retired ? (byte)1 : (byte)0;
            written += ContentVarint.Write(body.AsSpan(written), (uint)rows[i].Body.Length);
        }
        for (int i = 0; i < rows.Count; i++)
        {
            rows[i].Body.Span.CopyTo(body.AsSpan(written));
            written += rows[i].Body.Length;
        }

        byte[] canonical = new byte[ContentPackFormat.ChunkHeaderBytes + body.Length];
        WriteHeader(canonical, type, chunkIndex, visibility, rows.Count,
            ContentPackFormat.CompressionNone, body.Length, body.Length);
        body.CopyTo(canonical.AsSpan(ContentPackFormat.ChunkHeaderBytes));
        string hash = ContentHash.OfChunk(canonical);

        byte[] storedBody = Compress(body);
        byte[] stored;
        if (storedBody.Length >= body.Length)
        {
            stored = canonical;
        }
        else
        {
            stored = new byte[ContentPackFormat.ChunkHeaderBytes + storedBody.Length];
            WriteHeader(stored, type, chunkIndex, visibility, rows.Count,
                ContentPackFormat.CompressionBrotli, body.Length, storedBody.Length);
            storedBody.CopyTo(stored.AsSpan(ContentPackFormat.ChunkHeaderBytes));
        }

        return new EncodedChunk
        {
            TypeId = type.TypeId,
            ChunkIndex = chunkIndex,
            Visibility = visibility,
            Hash = hash,
            StoredFile = stored,
            UncompressedBytes = body.Length,
            RowCount = rows.Count,
        };
    }

    /// <summary>
    /// Decodes a stored chunk file. Returns false with a reason token rather than throwing, and takes the
    /// two header-level refusals of section 7.2 BEFORE any allocation.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> file, out DecodedChunk? chunk, out string reason)
    {
        chunk = null;
        if (file.Length < ContentPackFormat.ChunkHeaderBytes) { reason = "chunk-truncated-header"; return false; }
        if (!file[..4].SequenceEqual(Magic)) { reason = "chunk-magic"; return false; }
        ushort formatVersion = BinaryPrimitives.ReadUInt16LittleEndian(file[4..]);
        if (formatVersion != ContentPackFormat.ChunkFormatVersion) { reason = "chunk-format-version"; return false; }
        ushort typeId = BinaryPrimitives.ReadUInt16LittleEndian(file[6..]);
        uint chunkIndex = BinaryPrimitives.ReadUInt32LittleEndian(file[8..]);
        uint slotBase = BinaryPrimitives.ReadUInt32LittleEndian(file[12..]);
        uint slotCount = BinaryPrimitives.ReadUInt32LittleEndian(file[16..]);
        uint rowCount = BinaryPrimitives.ReadUInt32LittleEndian(file[20..]);
        byte visibility = file[24];
        byte compression = file[25];
        ushort reserved = BinaryPrimitives.ReadUInt16LittleEndian(file[26..]);
        uint uncompressed = BinaryPrimitives.ReadUInt32LittleEndian(file[28..]);
        uint storedBytes = BinaryPrimitives.ReadUInt32LittleEndian(file[32..]);

        if (reserved != 0) { reason = "chunk-reserved-set"; return false; }
        if (uncompressed > ContentPackFormat.MaxChunkUncompressedBytes) { reason = "chunk-too-large"; return false; }
        if (storedBytes != file.Length - ContentPackFormat.ChunkHeaderBytes) { reason = "chunk-stored-length"; return false; }
        if ((ulong)slotBase != (ulong)chunkIndex * slotCount) { reason = "chunk-range-mismatch"; return false; }
        if (compression > ContentPackFormat.CompressionBrotli) { reason = "chunk-compression"; return false; }

        byte[] body = new byte[uncompressed];
        ReadOnlySpan<byte> storedBody = file[ContentPackFormat.ChunkHeaderBytes..];
        if (compression == ContentPackFormat.CompressionNone)
        {
            if (storedBody.Length != uncompressed) { reason = "chunk-stored-length"; return false; }
            storedBody.CopyTo(body);
        }
        else if (!BrotliDecoder.TryDecompress(storedBody, body, out int decompressed) || decompressed != uncompressed)
        {
            reason = "chunk-decompress";
            return false;
        }

        int[] ids = new int[rowCount];
        int[] offsets = new int[rowCount];
        int[] lengths = new int[rowCount];
        bool[] retired = new bool[rowCount];
        int cursor = 0;
        int previousId = -1;
        for (int i = 0; i < rowCount; i++)
        {
            if (!ContentVarint.TryRead(body, ref cursor, out uint id)) { reason = "chunk-row-table"; return false; }
            if (cursor >= body.Length) { reason = "chunk-row-table"; return false; }
            byte flags = body[cursor++];
            if ((flags & 0xFE) != 0) { reason = "chunk-row-flags"; return false; }
            if (!ContentVarint.TryRead(body, ref cursor, out uint length)) { reason = "chunk-row-table"; return false; }
            if ((int)id == previousId) { reason = "chunk-row-duplicate"; return false; }
            if ((int)id < previousId) { reason = "chunk-row-order"; return false; }
            previousId = (int)id;
            ids[i] = (int)id;
            lengths[i] = (int)length;
            retired[i] = (flags & 1) != 0;
        }
        int running = cursor;
        for (int i = 0; i < rowCount; i++)
        {
            offsets[i] = running;
            running += lengths[i];
            if (running > body.Length) { reason = "chunk-row-overflow"; return false; }
        }
        if (running != body.Length) { reason = "chunk-trailing-bytes"; return false; }

        chunk = new DecodedChunk
        {
            TypeId = typeId,
            ChunkIndex = (int)chunkIndex,
            SlotBase = (int)slotBase,
            SlotCount = (int)slotCount,
            Visibility = visibility,
            Body = body,
            Ids = ids,
            Offsets = offsets,
            Lengths = lengths,
            Retired = retired,
        };
        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Verifies a stored file against a hash without decoding its rows: decompress, rebuild the canonical
    /// header, digest. That is the client's per-chunk cost in budget P4.
    /// </summary>
    public static bool TryVerify(ReadOnlySpan<byte> file, string expectedHash, out string reason)
    {
        if (file.Length < ContentPackFormat.ChunkHeaderBytes) { reason = "chunk-truncated-header"; return false; }
        uint uncompressed = BinaryPrimitives.ReadUInt32LittleEndian(file[28..]);
        if (uncompressed > ContentPackFormat.MaxChunkUncompressedBytes) { reason = "chunk-too-large"; return false; }
        byte compression = file[25];
        byte[] header = file[..ContentPackFormat.ChunkHeaderBytes].ToArray();
        header[25] = ContentPackFormat.CompressionNone;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(32), uncompressed);
        byte[] body;
        if (compression == ContentPackFormat.CompressionNone)
        {
            body = file[ContentPackFormat.ChunkHeaderBytes..].ToArray();
        }
        else
        {
            body = new byte[uncompressed];
            if (!BrotliDecoder.TryDecompress(file[ContentPackFormat.ChunkHeaderBytes..], body, out int written)
                || written != uncompressed)
            {
                reason = "chunk-decompress";
                return false;
            }
        }
        string actual = ContentHash.OfChunkStreaming(header, body, ContentHash.ChunkDomain);
        if (!string.Equals(actual, expectedHash, StringComparison.Ordinal)) { reason = "chunk-hash-mismatch"; return false; }
        reason = string.Empty;
        return true;
    }

    internal static byte[] Compress(ReadOnlySpan<byte> source)
    {
        byte[] scratch = new byte[BrotliEncoder.GetMaxCompressedLength(source.Length)];
        if (!BrotliEncoder.TryCompress(source, scratch, out int written,
                ContentPackFormat.BrotliQuality, ContentPackFormat.BrotliWindow))
        {
            return scratch;   // never smaller than the source, so the caller stores uncompressed
        }
        return scratch.AsSpan(0, written).ToArray();
    }

    private static void WriteHeader(
        Span<byte> destination,
        ContentTypeDescriptor type,
        int chunkIndex,
        byte visibility,
        int rowCount,
        byte compression,
        int uncompressedBytes,
        int storedBytes)
    {
        Magic.CopyTo(destination);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], ContentPackFormat.ChunkFormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], type.TypeId);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], (uint)chunkIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], (uint)((long)chunkIndex * type.ChunkSlots));
        BinaryPrimitives.WriteUInt32LittleEndian(destination[16..], (uint)type.ChunkSlots);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[20..], (uint)rowCount);
        destination[24] = visibility;
        destination[25] = compression;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[26..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[28..], (uint)uncompressedBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[32..], (uint)storedBytes);
    }
}
