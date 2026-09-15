using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace KhaozEngine.Benchmarks.Catalog;

/// <summary>Per-step timings of one boot, so a miss against budget P3 is attributed rather than argued.</summary>
public sealed class ContentLoadTiming
{
    public double ManifestMs { get; set; }
    public double FetchMs { get; set; }
    public double VerifyMs { get; set; }
    public double DecodeMs { get; set; }
    public double IndexMs { get; set; }
    public double ValidateMs { get; set; }
    public long BytesFetched { get; set; }
    public int ChunksFetched { get; set; }

    /// <summary>Budget P3's own span: boot steps 3 to 8, manifest to validated runtime.</summary>
    public double TotalMs => ManifestMs + FetchMs + VerifyMs + DecodeMs + IndexMs + ValidateMs;
}

/// <summary>
/// The loaded active version, spec section 9.1. Built once at boot, immutable after, and swapped whole
/// when a version changes. Decode is EAGER here because the validator runs on the full snapshot and
/// because a server that decodes lazily pays a first-touch cost inside a tick (section 9.3).
/// </summary>
public sealed class ContentRuntime
{
    private readonly Dictionary<ushort, ContentTypeTable> _tables = [];
    private ContentTypeTable? _itemTable;

    public IReadOnlyDictionary<ushort, ContentTypeTable> Tables => _tables;

    /// <summary>
    /// The <c>item</c> table, resolved once. Budget P7 is "one array index into Offsets, one bounds check,
    /// one span slice", so the table a caller reads through is hoisted out of the hot path rather than
    /// looked up per call.
    /// </summary>
    public ContentTypeTable ItemTable => _itemTable ?? _tables[ContentTypes.Item];

    public ContentIndexes Indexes { get; private set; } = ContentIndexes.Empty;

    public ContentLoadTiming Timing { get; } = new();

    public int VersionNumber { get; private set; }

    /// <summary>
    /// Boot steps 3 to 7: fetch the manifest, fetch and verify every chunk it names, decompress, decode
    /// into the per-type arrays and build the derived indexes. The validator is step 8 and is a separate
    /// call, so a caller can time the two halves apart.
    /// </summary>
    public static ContentRuntime Load(
        FileSystemPackStore store,
        string manifestHash,
        IReadOnlyList<ContentTypeDescriptor> types,
        bool verifyChunkHashes)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(types);
        var runtime = new ContentRuntime();
        var clock = Stopwatch.StartNew();

        byte[] manifestFile = store.Get(manifestHash) ?? throw new InvalidOperationException("Manifest absent from the store.");
        if (!ContentManifestCodec.TryDecode(manifestFile, out ContentManifest? manifest, out string reason) || manifest is null)
            throw new InvalidOperationException("Manifest refused: " + reason);
        runtime.VersionNumber = manifest.VersionNumber;
        runtime.Timing.ManifestMs = clock.Elapsed.TotalMilliseconds;

        var byId = new Dictionary<ushort, ContentTypeDescriptor>();
        foreach (ContentTypeDescriptor type in types) byId[type.TypeId] = type;

        foreach (ManifestTypeEntry entry in manifest.Types)
        {
            if (!byId.TryGetValue(entry.TypeId, out ContentTypeDescriptor? type)) continue;
            runtime.LoadType(store, type, entry, verifyChunkHashes);
        }

        clock.Restart();
        runtime.Indexes = ContentIndexes.Build(runtime);
        runtime.Timing.IndexMs = clock.Elapsed.TotalMilliseconds;
        return runtime;
    }

    public ContentTypeTable Table(ushort typeId) => _tables[typeId];

    public bool TryGetItem(int id, out ItemRowView row)
    {
        ContentTypeTable table = ItemTable;
        if ((uint)id >= (uint)table.Offsets.Length || table.Offsets[id] < 0)
        {
            row = default;
            return false;
        }
        row = ItemRowView.Decode(table.Bodies.AsSpan(table.Offsets[id], table.Lengths[id]), table.IsRetired(id));
        return row.IsValid;
    }

    /// <summary>The one array read and span slice budget P7 is taken against.</summary>
    public ReadOnlySpan<byte> Row(ushort typeId, int id) => _tables[typeId].Body(id);

    public long ApproximateBytes()
    {
        long bytes = 0;
        foreach (ContentTypeTable table in _tables.Values) bytes += table.ApproximateBytes();
        return bytes + Indexes.ApproximateBytes();
    }

    private void LoadType(
        FileSystemPackStore store,
        ContentTypeDescriptor type,
        ManifestTypeEntry entry,
        bool verifyChunkHashes)
    {
        var fetch = Stopwatch.StartNew();
        var files = new List<byte[]>(entry.Chunks.Count);
        foreach (ManifestChunkEntry chunk in entry.Chunks)
        {
            byte[] file = store.Get(chunk.Hash) ?? throw new InvalidOperationException("Chunk absent: " + chunk.Hash);
            files.Add(file);
            Timing.BytesFetched += file.Length;
            Timing.ChunksFetched++;
        }
        fetch.Stop();
        Timing.FetchMs += fetch.Elapsed.TotalMilliseconds;

        if (verifyChunkHashes)
        {
            var verify = Stopwatch.StartNew();
            for (int i = 0; i < files.Count; i++)
            {
                if (!ContentChunkCodec.TryVerify(files[i], entry.Chunks[i].Hash, out string verifyReason))
                    throw new InvalidOperationException("Chunk refused: " + verifyReason);
            }
            verify.Stop();
            Timing.VerifyMs += verify.Elapsed.TotalMilliseconds;
        }

        var decode = Stopwatch.StartNew();
        var decoded = new List<DecodedChunk>(files.Count);
        int highestId = 0;
        long bodyBytes = 0;
        foreach (byte[] file in files)
        {
            if (!ContentChunkCodec.TryDecode(file, out DecodedChunk? chunk, out string chunkReason) || chunk is null)
                throw new InvalidOperationException("Chunk refused: " + chunkReason);
            decoded.Add(chunk);
            for (int i = 0; i < chunk.Ids.Length; i++)
            {
                if (chunk.Ids[i] > highestId) highestId = chunk.Ids[i];
                bodyBytes += chunk.Lengths[i];
            }
        }

        var table = new ContentTypeTable(type.TypeId, type.TypeKey, type.ChunkSlots, highestId, (int)bodyBytes);
        int cursor = 0;
        int rows = 0;
        foreach (DecodedChunk chunk in decoded)
        {
            for (int i = 0; i < chunk.Ids.Length; i++)
            {
                int id = chunk.Ids[i];
                int length = chunk.Lengths[i];
                chunk.Body.AsSpan(chunk.Offsets[i], length).CopyTo(table.Bodies.AsSpan(cursor));
                table.Offsets[id] = cursor;
                table.Lengths[id] = length;
                table.Keys[id] = ReadKey(table.Bodies.AsSpan(cursor, length));
                if (chunk.Retired[i]) table.MarkRetired(id);
                cursor += length;
                rows++;
            }
        }
        table.RowCount = rows;
        decode.Stop();
        Timing.DecodeMs += decode.Elapsed.TotalMilliseconds;
        _tables[type.TypeId] = table;
        if (type.TypeId == ContentTypes.Item) _itemTable = table;
    }

    /// <summary>A row body opens with its content key, length prefixed UTF-8 (see the row encoder).</summary>
    public static string ReadKey(ReadOnlySpan<byte> body)
    {
        int offset = 0;
        if (!ContentVarint.TryRead(body, ref offset, out uint length)) return string.Empty;
        if (offset + (int)length > body.Length) return string.Empty;
        return Encoding.UTF8.GetString(body.Slice(offset, (int)length));
    }

    /// <summary>The offset of the first schema field, past the key prefix.</summary>
    public static int FieldsOffset(ReadOnlySpan<byte> body)
    {
        int offset = 0;
        if (!ContentVarint.TryRead(body, ref offset, out uint length)) return body.Length;
        return Math.Min(body.Length, offset + (int)length);
    }
}
