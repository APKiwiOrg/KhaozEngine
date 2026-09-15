using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Benchmarks.Catalog;

/// <summary>
/// The publish pipeline of spec section 6, cut down to what the budgets need: select the affected chunks,
/// encode every row of each in ascending id order, compress, hash over the canonical UNCOMPRESSED bytes,
/// write to the content addressed store, then build both manifests and the version pointer.
/// </summary>
public sealed class CatalogPublisher
{
    private readonly SyntheticContentSet _content;
    private readonly FileSystemPackStore _store;

    public CatalogPublisher(SyntheticContentSet content, FileSystemPackStore store)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(store);
        _content = content;
        _store = store;
    }

    /// <summary>The chunks a type produces, as (chunk index, first id index, id count) over its id array.</summary>
    public static List<(int ChunkIndex, int Start, int Count)> PlanChunks(ContentTypeDescriptor type, int[] ids)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(ids);
        var plan = new List<(int, int, int)>();
        int start = 0;
        while (start < ids.Length)
        {
            int index = type.ChunkIndexOf(ids[start]);
            int end = start;
            while (end < ids.Length && type.ChunkIndexOf(ids[end]) == index) end++;
            plan.Add((index, start, end - start));
            start = end;
        }
        return plan;
    }

    public PublishOutcome PublishFull(int versionNumber, int languages, CancellationToken cancellation)
    {
        var total = Stopwatch.StartNew();
        var encodeClock = Stopwatch.StartNew();
        var chunks = new ConcurrentDictionary<(ushort, int), ChunkRecord>();
        var encoded = new ConcurrentBag<EncodedChunk>();
        long itemBodyBytes = 0;
        long itemRows = 0;
        long totalRows = 0;
        long keyBytes = 0;
        int largestRow = 0;
        int largestChunk = 0;

        var work = new List<(ContentTypeDescriptor Type, int ChunkIndex, int Start, int Count)>();
        foreach (ContentTypeDescriptor type in _content.Types)
        {
            int[] ids = _content.IdsFor(type.TypeId);
            foreach ((int index, int start, int count) in PlanChunks(type, ids))
                work.Add((type, index, start, count));
        }

        var options = new ParallelOptions
        {
            CancellationToken = cancellation,
            MaxDegreeOfParallelism = Environment.ProcessorCount,
        };
        Parallel.ForEach(work, options,
            () => (Assembler: new ChunkAssembler(), Encoder: new SyntheticRowEncoder(_content)),
            (item, _, state) =>
            {
                EncodedChunk chunk = EncodeChunk(item.Type, item.ChunkIndex, item.Start, item.Count,
                    state.Assembler, state.Encoder, out int chunkItemBytes, out int chunkLargestRow);
                encoded.Add(chunk);
                chunks[(item.Type.TypeId, item.ChunkIndex)] = new ChunkRecord(
                    item.Type.TypeId, item.ChunkIndex, item.Type.DefaultVisibility, chunk.Hash,
                    chunk.StoredBytes, chunk.UncompressedBytes, chunk.RowCount);
                Interlocked.Add(ref totalRows, chunk.RowCount);
                if (item.Type.TypeId == ContentTypes.Item)
                {
                    Interlocked.Add(ref itemBodyBytes, chunkItemBytes);
                    Interlocked.Add(ref itemRows, chunk.RowCount);
                }
                InterlockedMax(ref largestRow, chunkLargestRow);
                InterlockedMax(ref largestChunk, chunk.UncompressedBytes);
                return state;
            },
            state => Interlocked.Add(ref keyBytes, state.Encoder.KeyBytes));
        encodeClock.Stop();

        var textClock = Stopwatch.StartNew();
        List<TextChunkRecord> textChunks = EncodeText(languages, cancellation, out List<byte[]> textFiles);
        textClock.Stop();

        (byte[] ruleFile, string ruleHash) = ContentRuleChunkCodec.Encode([]);

        var manifestClock = Stopwatch.StartNew();
        ContentManifest serverManifest = BuildManifest(1, versionNumber, chunks, textChunks, ruleHash, serverSide: true);
        ContentManifest clientManifest = BuildManifest(0, versionNumber, chunks, textChunks, ruleHash, serverSide: false);
        byte[] serverManifestFile = ContentManifestCodec.Encode(serverManifest);
        byte[] clientManifestFile = ContentManifestCodec.Encode(clientManifest);
        string serverHash = serverManifest.ComputeHash();
        string clientHash = clientManifest.ComputeHash();
        manifestClock.Stop();

        var writeClock = Stopwatch.StartNew();
        foreach (EncodedChunk chunk in encoded) _store.Put(chunk.Hash, chunk.StoredFile);
        for (int i = 0; i < textChunks.Count; i++) _store.Put(textChunks[i].Hash, textFiles[i]);
        _store.Put(ruleHash, ruleFile);
        _store.Put(serverHash, serverManifestFile);
        _store.Put(clientHash, clientManifestFile);
        _store.PutVersionPointer(versionNumber, serverHash, clientHash);
        writeClock.Stop();
        total.Stop();

        var outcome = new PublishOutcome
        {
            VersionNumber = versionNumber,
            ServerManifest = serverManifest,
            ClientManifest = clientManifest,
            ServerManifestHash = serverHash,
            ClientManifestHash = clientHash,
            ServerManifestBytes = serverManifestFile.Length,
            ClientManifestBytes = clientManifestFile.Length,
            Chunks = new Dictionary<(ushort, int), ChunkRecord>(chunks),
            TextChunks = textChunks,
            RuleChunkHash = ruleHash,
            RuleChunkStoredBytes = ruleFile.Length,
            TotalRows = totalRows,
            KeyBodyBytes = keyBytes,
            ItemRowBodyBytes = itemBodyBytes,
            ItemRowCount = itemRows,
            LargestRowBytes = largestRow,
            LargestChunkUncompressedBytes = largestChunk,
            EncodeMs = encodeClock.Elapsed.TotalMilliseconds,
            TextMs = textClock.Elapsed.TotalMilliseconds,
            ManifestMs = manifestClock.Elapsed.TotalMilliseconds,
            WriteMs = writeClock.Elapsed.TotalMilliseconds,
            TotalMs = total.Elapsed.TotalMilliseconds,
        };
        Accumulate(outcome);
        return outcome;
    }

    /// <summary>
    /// A one-item edit: exactly the chunks holding an edited id are re-encoded and written, every other
    /// chunk keeps its previous hash, and both manifests are rebuilt. That is section 6.6's carry forward.
    /// </summary>
    public EditOutcome PublishEdit(
        int versionNumber,
        PublishOutcome previous,
        IReadOnlyCollection<int> editedItemIds,
        double validateMs,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(editedItemIds);
        var total = Stopwatch.StartNew();
        ContentTypeDescriptor itemType = _content.TypeOf(ContentTypes.Item);
        int[] ids = _content.ItemIds;
        var edits = new HashSet<int>(editedItemIds);
        var affected = new SortedSet<int>();
        foreach (int id in edits) affected.Add(itemType.ChunkIndexOf(id));

        var assembler = new ChunkAssembler();
        var encoder = new SyntheticRowEncoder(_content) { EditedItemIds = edits };
        var rewritten = new List<EncodedChunk>(affected.Count);
        var encodeClock = Stopwatch.StartNew();
        foreach (int chunkIndex in affected)
        {
            cancellation.ThrowIfCancellationRequested();
            (int start, int count) = RangeOf(itemType, ids, chunkIndex);
            rewritten.Add(EncodeChunk(itemType, chunkIndex, start, count, assembler, encoder, out _, out _));
        }
        encodeClock.Stop();

        var chunks = new Dictionary<(ushort, int), ChunkRecord>(previous.Chunks);
        foreach (EncodedChunk chunk in rewritten)
        {
            chunks[(chunk.TypeId, chunk.ChunkIndex)] = new ChunkRecord(
                chunk.TypeId, chunk.ChunkIndex, chunk.Visibility, chunk.Hash,
                chunk.StoredBytes, chunk.UncompressedBytes, chunk.RowCount);
        }

        var manifestClock = Stopwatch.StartNew();
        var concurrent = new ConcurrentDictionary<(ushort, int), ChunkRecord>(chunks);
        ContentManifest serverManifest = BuildManifest(1, versionNumber, concurrent, previous.TextChunks, previous.RuleChunkHash, true);
        ContentManifest clientManifest = BuildManifest(0, versionNumber, concurrent, previous.TextChunks, previous.RuleChunkHash, false);
        byte[] serverManifestFile = ContentManifestCodec.Encode(serverManifest);
        byte[] clientManifestFile = ContentManifestCodec.Encode(clientManifest);
        string serverHash = serverManifest.ComputeHash();
        string clientHash = clientManifest.ComputeHash();
        manifestClock.Stop();

        var writeClock = Stopwatch.StartNew();
        long bytesWritten = 0;
        foreach (EncodedChunk chunk in rewritten)
        {
            _store.Put(chunk.Hash, chunk.StoredFile);
            bytesWritten += chunk.StoredBytes;
        }
        _store.Put(serverHash, serverManifestFile);
        _store.Put(clientHash, clientManifestFile);
        _store.PutVersionPointer(versionNumber, serverHash, clientHash);
        writeClock.Stop();
        total.Stop();

        return new EditOutcome
        {
            VersionNumber = versionNumber,
            ChunkSlots = itemType.ChunkSlots,
            AffectedChunkCount = affected.Count,
            ChunkBytesWritten = bytesWritten,
            ClientManifestBytes = clientManifestFile.Length,
            ServerManifestBytes = serverManifestFile.Length,
            PublishMs = total.Elapsed.TotalMilliseconds + validateMs,
            ValidateMs = validateMs,
            EncodeMs = encodeClock.Elapsed.TotalMilliseconds,
            WriteMs = writeClock.Elapsed.TotalMilliseconds,
            ManifestMs = manifestClock.Elapsed.TotalMilliseconds,
        };
    }

    private static (int Start, int Count) RangeOf(ContentTypeDescriptor type, int[] ids, int chunkIndex)
    {
        int start = -1;
        int count = 0;
        for (int i = 0; i < ids.Length; i++)
        {
            if (type.ChunkIndexOf(ids[i]) != chunkIndex) continue;
            if (start < 0) start = i;
            count++;
        }
        return (start < 0 ? 0 : start, count);
    }

    private EncodedChunk EncodeChunk(
        ContentTypeDescriptor type,
        int chunkIndex,
        int start,
        int count,
        ChunkAssembler assembler,
        SyntheticRowEncoder encoder,
        out int itemBodyBytes,
        out int largestRow)
    {
        int[] ids = _content.IdsFor(type.TypeId);
        assembler.Reset();
        itemBodyBytes = 0;
        for (int i = 0; i < count; i++)
        {
            int id = ids[start + i];
            Span<byte> destination = assembler.Reserve(type.MaxRowBytes);
            int length = encoder.Encode(type.TypeId, id, destination);
            assembler.Commit(id, encoder.IsRetired(type.TypeId, id), length);
            if (type.TypeId == ContentTypes.Item) itemBodyBytes += length;
        }
        largestRow = assembler.LargestRowBytes();
        return ContentChunkCodec.Encode(type, chunkIndex, type.DefaultVisibility, assembler.Build());
    }

    private List<TextChunkRecord> EncodeText(int languages, CancellationToken cancellation, out List<byte[]> files)
    {
        string[] keys = SyntheticText.BuildKeys(_content);
        var records = new List<TextChunkRecord>();
        var ordered = new List<EncodedTextChunk>();
        files = [];
        for (int language = 0; language < languages; language++)
        {
            string tag = LanguageTag(language);
            List<(int Start, int Count)> shards = SyntheticText.Shard(keys, tag.Length + 6);
            var encodedShards = new EncodedTextChunk[shards.Count];
            var options = new ParallelOptions
            {
                CancellationToken = cancellation,
                MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount),
            };
            Parallel.For(0, shards.Count, options, i =>
            {
                string shardTag = SyntheticText.ShardTag(tag, i, shards.Count);
                List<KeyValuePair<string, string>> entries =
                    SyntheticText.Materialize(keys, shards[i].Start, shards[i].Count, tag);
                encodedShards[i] = ContentTextChunkCodec.Encode(shardTag, entries);
            });
            foreach (EncodedTextChunk shard in encodedShards) ordered.Add(shard);
        }
        // Sort the RECORD and its FILE together. Sorting the record list alone stored every shard past the
        // first reordered one under another shard's hash, which only the client fetch loop's verification
        // could see, because the server load path never fetches a text chunk.
        ordered.Sort(static (a, b) => string.CompareOrdinal(a.LanguageTag, b.LanguageTag));
        foreach (EncodedTextChunk shard in ordered)
        {
            records.Add(new TextChunkRecord(shard.LanguageTag, shard.Hash, shard.StoredBytes,
                shard.UncompressedBytes, shard.EntryCount));
            files.Add(shard.StoredFile);
        }
        return records;
    }

    /// <summary>Ten BCP-47 tags, enough for any <c>--languages</c> value the config permits.</summary>
    public static string LanguageTag(int ordinal) => ordinal switch
    {
        0 => "en",
        1 => "fr",
        2 => "de",
        3 => "es",
        4 => "pt-BR",
        5 => "it",
        6 => "pl",
        7 => "ru",
        8 => "ja",
        9 => "ko",
        _ => "zz-x-l" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private ContentManifest BuildManifest(
        byte side,
        int versionNumber,
        ConcurrentDictionary<(ushort, int), ChunkRecord> chunks,
        List<TextChunkRecord> textChunks,
        string ruleHash,
        bool serverSide)
    {
        var types = new List<ManifestTypeEntry>();
        foreach (ContentTypeDescriptor type in _content.Types.OrderBy(static t => t.TypeId))
        {
            if (!serverSide && type.DefaultVisibility == ContentPackFormat.VisibilityServerOnly) continue;
            var entries = new List<ManifestChunkEntry>();
            foreach (KeyValuePair<(ushort TypeId, int ChunkIndex), ChunkRecord> pair in chunks)
            {
                if (pair.Key.TypeId != type.TypeId) continue;
                entries.Add(new ManifestChunkEntry(pair.Value.ChunkIndex, pair.Value.UncompressedBytes, pair.Value.Hash));
            }
            entries.Sort(static (a, b) => a.ChunkIndex.CompareTo(b.ChunkIndex));
            types.Add(new ManifestTypeEntry(type.TypeId, type.TypeKey, type.ChunkSlots, type.DefaultVisibility, entries));
        }
        var languages = new List<ManifestLanguageEntry>(textChunks.Count);
        foreach (TextChunkRecord text in textChunks) languages.Add(new ManifestLanguageEntry(text.Tag, text.Hash));
        return new ContentManifest
        {
            Side = side,
            VersionNumber = versionNumber,
            FormatGeneration = ContentPackFormat.Generation,
            MinimumServerBuild = 1,
            MinimumClientBuild = 1,
            RemapRuleChunkHash = ruleHash,
            Types = types,
            Languages = languages,
        };
    }

    private static void Accumulate(PublishOutcome outcome)
    {
        foreach (ChunkRecord chunk in outcome.Chunks.Values)
        {
            outcome.ServerChunkStoredBytes += chunk.StoredBytes;
            outcome.ServerUncompressedBytes += chunk.UncompressedBytes;
            if (chunk.Visibility != ContentPackFormat.VisibilityServerOnly)
            {
                outcome.ClientChunkStoredBytes += chunk.StoredBytes;
                outcome.ClientUncompressedBytes += chunk.UncompressedBytes;
            }
        }
        foreach (TextChunkRecord text in outcome.TextChunks)
        {
            outcome.TextStoredBytes += text.StoredBytes;
            outcome.TextUncompressedBytes += text.UncompressedBytes;
        }
        outcome.ServerUncompressedBytes += outcome.TextUncompressedBytes;
        outcome.ClientUncompressedBytes += outcome.TextUncompressedBytes;
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen = Volatile.Read(ref target);
        while (value > seen)
        {
            int previous = Interlocked.CompareExchange(ref target, value, seen);
            if (previous == seen) break;
            seen = previous;
        }
    }
}
