using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace KhaozEngine.Benchmarks.Catalog;

/// <summary>A validator finding: the type, the row, a stable code and a message (spec section 5.2).</summary>
public readonly record struct ContentFinding(ushort TypeId, int Id, string Code, string Message);

/// <summary>What one sweep found, plus the per-pass timings a miss against budget P8 is attributed with.</summary>
public sealed class ValidationReport
{
    public List<ContentFinding> Findings { get; } = [];
    public long RowsSwept { get; set; }
    public long RoundTripBytes { get; set; }
    public double StructureMs { get; set; }
    public double SchemaMs { get; set; }
    public double ReferenceMs { get; set; }
    public double CodecMs { get; set; }
    public double RuleMs { get; set; }

    public bool IsValid => Findings.Count == 0;

    public double TotalMs => StructureMs + SchemaMs + ReferenceMs + CodecMs + RuleMs;

    public void Add(ushort typeId, int id, string code, string message) =>
        Findings.Add(new ContentFinding(typeId, id, code, message));
}

/// <summary>
/// The one validator of spec section 5, running the five passes in order and never stopping early. The
/// spike implements the checks a synthetic set exercises: keys, ids, ranges, references, the schema rules
/// with a numeric answer, the row and chunk size caps, and the codec round trip.
/// </summary>
public sealed class ContentValidator
{
    private readonly IReadOnlyList<ContentTypeDescriptor> _types;
    private readonly HashSet<string> _registeredTypeKeys;

    public ContentValidator(IReadOnlyList<ContentTypeDescriptor> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        _types = types;
        _registeredTypeKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (ContentTypeDescriptor type in types) _registeredTypeKeys.Add(type.TypeKey);
    }

    public ValidationReport Validate(ContentRuntime runtime, IReadOnlyList<ContentRemapRule> rules)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(rules);
        var report = new ValidationReport();
        var clock = Stopwatch.StartNew();
        Structure(runtime, report);
        report.StructureMs = Restart(clock);
        Schema(runtime, report);
        report.SchemaMs = Restart(clock);
        References(runtime, report);
        report.ReferenceMs = Restart(clock);
        Codec(runtime, report);
        report.CodecMs = Restart(clock);
        Rules(rules, report);
        report.RuleMs = Restart(clock);
        return report;
    }

    /// <summary>Pass 1: ids, keys, families, blocks, chunk sizes, type identity.</summary>
    private void Structure(ContentRuntime runtime, ValidationReport report)
    {
        foreach (ContentTypeDescriptor type in _types)
        {
            if (type.ChunkSlots < 256 || type.ChunkSlots > 65_536 || (type.ChunkSlots & (type.ChunkSlots - 1)) != 0)
                report.Add(type.TypeId, 0, "KEC0028", "chunk_slots is not a power of two between 256 and 65536.");
            if (!runtime.Tables.TryGetValue(type.TypeId, out ContentTypeTable? table)) continue;
            // Uniqueness comes from the key index rather than from a HashSet of strings. The index build
            // probes every key once already and records the ids whose key was taken, so this pass reads that
            // list instead of paying a second random probe per row, and the walk below stays sequential.
            table.EnsureKeyIndex();
            for (int id = 0; id < table.Offsets.Length; id++)
            {
                if (table.Offsets[id] < 0) continue;
                report.RowsSwept++;
                if (id <= 0) report.Add(type.TypeId, id, "KEC0009", "A definition id is 0 or negative.");
                ReadOnlySpan<byte> key = table.KeyUtf8(id);
                if (!IsWellFormedKey(key))
                    report.Add(type.TypeId, id, "KEC0001", "A key is not well formed: " + Encoding.UTF8.GetString(key));
            }
            foreach (int id in table.DuplicateKeyIds)
            {
                report.Add(type.TypeId, id, "KEC0002",
                    "A key is not unique within its type: " + Encoding.UTF8.GetString(table.KeyUtf8(id)));
            }
        }
    }

    /// <summary>Pass 2: every field against its schema, required fields, value ranges, the derived key shape.</summary>
    private void Schema(ContentRuntime runtime, ValidationReport report)
    {
        if (runtime.Tables.TryGetValue(ContentTypes.Item, out ContentTypeTable? items))
        {
            var row = new ItemRowData();
            for (int id = 0; id < items.Offsets.Length; id++)
            {
                if (items.Offsets[id] < 0) continue;
                ReadOnlySpan<byte> body = items.Body(id);
                if (!ContentRowCodec.TryDecodeItem(body[ContentRuntime.FieldsOffset(body)..], ref row))
                {
                    report.Add(ContentTypes.Item, id, "KEC0027", "An item row did not decode.");
                    continue;
                }
                if (row.MaxStack < 1 || (row.Stackable && row.MaxStack == 1))
                    report.Add(ContentTypes.Item, id, "KEC0025", "max_stack is below 1, or is 1 on a stackable row.");
                if (row.Stackable && (row.DurabilityMax > 0 || row.SocketMax > 0))
                    report.Add(ContentTypes.Item, id, "KEC0022", "A definition declaring durability or sockets is stackable.");
                CheckDerivedKey(report, ContentTypes.Item, id, "item", items.KeyUtf8(id).Length, "examine");
            }
        }

        if (runtime.Tables.TryGetValue(ContentTypes.Stat, out ContentTypeTable? stats))
        {
            var row = new StatRowData();
            for (int id = 0; id < stats.Offsets.Length; id++)
            {
                if (stats.Offsets[id] < 0) continue;
                ReadOnlySpan<byte> body = stats.Body(id);
                if (!ContentRowCodec.TryDecodeStat(body[ContentRuntime.FieldsOffset(body)..], ref row))
                {
                    report.Add(ContentTypes.Stat, id, "KEC0027", "A stat row did not decode.");
                    continue;
                }
                if (!IsPowerOfTen(row.Scale)) report.Add(ContentTypes.Stat, id, "KEC0020", "A stat scale is not a power of ten.");
                if (row.Min > row.Max) report.Add(ContentTypes.Stat, id, "KEC0021", "A stat min exceeds its max.");
                CheckDerivedKey(report, ContentTypes.Stat, id, "stat", stats.KeyUtf8(id).Length, "display_format");
            }
        }
    }

    /// <summary>Pass 3: key references, tag lists and the loot graph.</summary>
    private void References(ContentRuntime runtime, ValidationReport report)
    {
        runtime.Tables.TryGetValue(ContentTypes.Tag, out ContentTypeTable? tags);
        runtime.Tables.TryGetValue(ContentTypes.Item, out ContentTypeTable? items);
        runtime.Tables.TryGetValue(ContentTypes.LootTable, out ContentTypeTable? lootTables);

        if (items is not null)
        {
            var row = new ItemRowData();
            for (int id = 0; id < items.Offsets.Length; id++)
            {
                if (items.Offsets[id] < 0) continue;
                ReadOnlySpan<byte> body = items.Body(id);
                if (!ContentRowCodec.TryDecodeItem(body[ContentRuntime.FieldsOffset(body)..], ref row)) continue;
                CheckTag(report, tags, ContentTypes.Item, id, row.TagCount > 0 ? row.Tag0 : 0);
                CheckTag(report, tags, ContentTypes.Item, id, row.TagCount > 1 ? row.Tag1 : 0);
                CheckTag(report, tags, ContentTypes.Item, id, row.TagCount > 2 ? row.Tag2 : 0);
                CheckTag(report, tags, ContentTypes.Item, id, row.TagCount > 3 ? row.Tag3 : 0);
                if (row.EquipProfile != 0 && !_registeredTypeKeys.Contains("equip_profile"))
                    report.Add(ContentTypes.Item, id, "KEC0007", "equip_profile names a content type that was never registered.");
            }
        }

        if (runtime.Tables.TryGetValue(ContentTypes.LootEntry, out ContentTypeTable? entries) && lootTables is not null)
        {
            var row = new LootEntryRowData();
            for (int id = 0; id < entries.Offsets.Length; id++)
            {
                if (entries.Offsets[id] < 0) continue;
                ReadOnlySpan<byte> body = entries.Body(id);
                if (!ContentRowCodec.TryDecodeLootEntry(body[ContentRuntime.FieldsOffset(body)..], ref row)) continue;
                if (!lootTables.IsLive(row.Table))
                    report.Add(ContentTypes.LootEntry, id, "KEC0006", "table names a row that is not live.");
                int set = (row.Item != 0 ? 1 : 0) + (row.NestedTable != 0 ? 1 : 0) + (row.RequiredTagCount > 0 ? 1 : 0);
                if (set != 1)
                    report.Add(ContentTypes.LootEntry, id, "KEC0023", "An entry does not set exactly one of item, nested_table and required_tags.");
                if (row.Item != 0 && items is not null && !items.IsLive(row.Item))
                    report.Add(ContentTypes.LootEntry, id, "KEC0006", "item names a row that is not live.");
                if (row.NestedTable != 0 && !lootTables.IsLive(row.NestedTable))
                    report.Add(ContentTypes.LootEntry, id, "KEC0006", "nested_table names a row that is not live.");
                CheckTag(report, tags, ContentTypes.LootEntry, id, row.RequiredTagCount > 0 ? row.RequiredTag0 : 0);
            }
            CheckLootCycles(runtime, report);
        }

        if (runtime.Tables.TryGetValue(ContentTypes.BaseSocket, out ContentTypeTable? sockets) && items is not null)
        {
            var row = new BaseSocketRowData();
            for (int id = 0; id < sockets.Offsets.Length; id++)
            {
                if (sockets.Offsets[id] < 0) continue;
                ReadOnlySpan<byte> body = sockets.Body(id);
                if (!ContentRowCodec.TryDecodeBaseSocket(body[ContentRuntime.FieldsOffset(body)..], ref row)) continue;
                if (!items.IsLive(row.Item))
                    report.Add(ContentTypes.BaseSocket, id, "KEC0006", "item names a row that is not live.");
                if (row.SocketType != 0 && !_registeredTypeKeys.Contains("socket_type"))
                    report.Add(ContentTypes.BaseSocket, id, "KEC0007", "socket_type names a content type that was never registered.");
            }
        }
    }

    /// <summary>Pass 4: the row size cap, the chunk size cap and the codec round trip.</summary>
    private void Codec(ContentRuntime runtime, ValidationReport report)
    {
        Span<byte> scratch = new byte[ContentPackFormat.MaxContentRowBytes];
        foreach (ContentTypeDescriptor type in _types)
        {
            if (!runtime.Tables.TryGetValue(type.TypeId, out ContentTypeTable? table)) continue;
            long chunkBytes = 0;
            int previousChunk = -1;
            for (int id = 0; id < table.Offsets.Length; id++)
            {
                if (table.Offsets[id] < 0) continue;
                int length = table.Lengths[id];
                if (length > type.MaxRowBytes)
                    report.Add(type.TypeId, id, "KEC0026", "A row's encoded bytes exceed its type's maxRowBytes.");
                int chunk = type.ChunkIndexOf(id);
                if (chunk != previousChunk)
                {
                    if (chunkBytes > ContentPackFormat.MaxChunkUncompressedBytes)
                        report.Add(type.TypeId, previousChunk, "KEC0038", "A chunk's canonical bytes exceed MaxChunkUncompressedBytes.");
                    previousChunk = chunk;
                    chunkBytes = ContentPackFormat.ChunkHeaderBytes;
                }
                chunkBytes += length + 8;
                if (!RoundTrips(type.TypeId, table, id, scratch, out int written))
                    report.Add(type.TypeId, id, "KEC0027", "A row's codec round trip is not byte identical.");
                report.RoundTripBytes += written;
            }
            if (chunkBytes > ContentPackFormat.MaxChunkUncompressedBytes)
                report.Add(type.TypeId, previousChunk, "KEC0038", "A chunk's canonical bytes exceed MaxChunkUncompressedBytes.");
        }
    }

    /// <summary>Pass 5: the full ordered remap rule set.</summary>
    private static void Rules(IReadOnlyList<ContentRemapRule> rules, ValidationReport report)
    {
        int expected = 1;
        var targets = new HashSet<(ushort, int)>();
        foreach (ContentRemapRule rule in rules)
        {
            if (rule.Sequence != expected)
                report.Add(rule.TypeId, rule.FromId, "KEC0018", "Remap rule sequences are not contiguous from 1.");
            expected = rule.Sequence + 1;
            if (rule.Payload is { Length: > 64 })
                report.Add(rule.TypeId, rule.FromId, "KEC0019", "A remap rule payload is longer than 64 bytes.");
            if (targets.Contains((rule.TypeId, rule.FromId)))
                report.Add(rule.TypeId, rule.FromId, "KEC0015", "The remap rule set is not idempotent.");
            targets.Add((rule.TypeId, rule.ToId));
        }
    }

    private static bool RoundTrips(ushort typeId, ContentTypeTable table, int id, Span<byte> scratch, out int written)
    {
        ReadOnlySpan<byte> body = table.Body(id);
        int fields = ContentRuntime.FieldsOffset(body);
        ReadOnlySpan<byte> source = body[fields..];
        written = 0;
        switch (typeId)
        {
            case ContentTypes.Tag:
            {
                var row = new TagRowData();
                if (!ContentRowCodec.TryDecodeTag(source, ref row)) return false;
                written = ContentRowCodec.EncodeTag(in row, scratch);
                break;
            }
            case ContentTypes.Item:
            {
                var row = new ItemRowData();
                if (!ContentRowCodec.TryDecodeItem(source, ref row)) return false;
                written = ContentRowCodec.EncodeItem(in row, scratch);
                break;
            }
            case ContentTypes.Stat:
            {
                var row = new StatRowData();
                if (!ContentRowCodec.TryDecodeStat(source, ref row)) return false;
                written = ContentRowCodec.EncodeStat(in row, scratch);
                break;
            }
            case ContentTypes.LootTable:
            {
                var row = new LootTableRowData();
                if (!ContentRowCodec.TryDecodeLootTable(source, ref row)) return false;
                written = ContentRowCodec.EncodeLootTable(in row, scratch);
                break;
            }
            case ContentTypes.LootEntry:
            {
                var row = new LootEntryRowData();
                if (!ContentRowCodec.TryDecodeLootEntry(source, ref row)) return false;
                written = ContentRowCodec.EncodeLootEntry(in row, scratch);
                break;
            }
            case ContentTypes.BaseSocket:
            {
                var row = new BaseSocketRowData();
                if (!ContentRowCodec.TryDecodeBaseSocket(source, ref row)) return false;
                written = ContentRowCodec.EncodeBaseSocket(in row, scratch);
                break;
            }
            default:
            {
                var row = new GameRowData();
                if (!ContentRowCodec.TryDecodeGame(source, ref row)) return false;
                written = ContentRowCodec.EncodeGame(in row, scratch);
                break;
            }
        }
        return source.SequenceEqual(scratch[..written]);
    }

    private static void CheckLootCycles(ContentRuntime runtime, ValidationReport report)
    {
        ContentIndexes indexes = runtime.Indexes;
        int tableCount = indexes.LootTableStart.Length;
        byte[] state = new byte[tableCount];
        var stack = new Stack<(int Table, int Cursor)>();
        for (int table = 0; table < tableCount; table++)
        {
            if (state[table] != 0) continue;
            stack.Push((table, 0));
            state[table] = 1;
            while (stack.Count > 0)
            {
                (int current, int cursor) = stack.Pop();
                if (cursor >= indexes.LootTableCount[current])
                {
                    state[current] = 2;
                    continue;
                }
                stack.Push((current, cursor + 1));
                int nested = indexes.LootEntryNested[indexes.LootTableStart[current] + cursor];
                if (nested <= 0 || nested >= tableCount) continue;
                if (state[nested] == 1)
                {
                    report.Add(ContentTypes.LootTable, nested, "KEC0024", "A loot table graph contains a cycle through nested_table.");
                    continue;
                }
                if (state[nested] == 0)
                {
                    state[nested] = 1;
                    stack.Push((nested, 0));
                }
            }
        }
    }

    private static void CheckTag(ValidationReport report, ContentTypeTable? tags, ushort typeId, int id, int tagId)
    {
        if (tagId == 0) return;
        if (tags is null || !tags.IsLive(tagId))
            report.Add(typeId, id, "KEC0008", "A tag list names a tag id that is not a live tag row.");
    }

    private static void CheckDerivedKey(ValidationReport report, ushort typeId, int id, string typeKey, int contentKeyBytes, string field)
    {
        if (typeKey.Length + contentKeyBytes + field.Length + 2 > 192)
            report.Add(typeId, id, "KEC0030", "A row's derived localized text key exceeds 192 characters.");
    }

    /// <summary>
    /// The key charset of contracts 5.3 is ASCII snake case, so the byte test and the character test are the
    /// same test, and this one runs over the loaded UTF-8 slice without materialising anything.
    /// </summary>
    private static bool IsWellFormedKey(ReadOnlySpan<byte> key)
    {
        if (key.Length is 0 or > 64) return false;
        if (key[0] is >= (byte)'0' and <= (byte)'9' || key[0] == (byte)'_' || key[^1] == (byte)'_') return false;
        for (int i = 0; i < key.Length; i++)
        {
            byte c = key[i];
            bool legal = (c >= (byte)'a' && c <= (byte)'z') || (c >= (byte)'0' && c <= (byte)'9') || c == (byte)'_';
            if (!legal) return false;
            if (c == (byte)'_' && i + 1 < key.Length && key[i + 1] == (byte)'_') return false;
        }
        return true;
    }

    private static bool IsPowerOfTen(int value)
    {
        if (value < 1) return false;
        while (value % 10 == 0) value /= 10;
        return value == 1;
    }

    private static double Restart(Stopwatch clock)
    {
        double elapsed = clock.Elapsed.TotalMilliseconds;
        clock.Restart();
        return elapsed;
    }
}
