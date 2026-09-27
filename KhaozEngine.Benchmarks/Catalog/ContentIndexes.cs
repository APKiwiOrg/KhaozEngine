using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace KhaozEngine.Benchmarks.Catalog;

/// <summary>A family's contiguous id block, so membership is two comparisons (contracts 5.2).</summary>
public readonly record struct FamilyBlock(ushort TypeId, int Base, int Size)
{
    public bool Contains(int id) => (id & ~(Size - 1)) == Base;
}

/// <summary>
/// The five derived indexes the engine builds once at load, spec section 9.4: key to id per type, reverse
/// references, tag to ids, family membership, and the loot candidate arrays with their weights prefix
/// summed. All five are flat arrays after construction. The first lives on the type table it indexes
/// (<see cref="ContentTypeTable.KeyIds"/>), because it is keyed on a slice of that table's blob.
/// <para>
/// The reverse-reference stress arm creates one synthetic satellite edge per item row. That is 50,000
/// one-to-one buckets at the owner figure and measures the allocation shape #1005 exposed without changing
/// the pack-size fixture the other budgets use.
/// </para>
/// </summary>
public sealed class ContentIndexes
{
    private const ushort ReferenceSatelliteType = ushort.MaxValue;

    public static readonly ContentIndexes Empty = new();

    private ContentIndexes()
    {
        ReferenceTargetTypes = [];
        ReferenceTargetIds = [];
        ReferenceSourceTypes = [];
        ReferenceRowStarts = [];
        ReferenceRowCounts = [];
        ReferenceRows = [];
        TagToItemIds = [];
        Families = [];
        LootTableStart = [];
        LootTableCount = [];
        LootEntryItem = [];
        LootEntryNested = [];
        LootEntryPrefixWeight = [];
    }

    public ushort[] ReferenceTargetTypes { get; private init; }

    public int[] ReferenceTargetIds { get; private init; }

    public ushort[] ReferenceSourceTypes { get; private init; }

    public int[] ReferenceRowStarts { get; private init; }

    public int[] ReferenceRowCounts { get; private init; }

    public int[] ReferenceRows { get; private init; }

    public int ReferenceEdgeCount => ReferenceRows.Length;

    public double ReferenceBuildMilliseconds { get; private init; }

    public long ReferenceBuildAllocatedBytes { get; private init; }

    public long ReferenceApproximateBytes =>
        ((long)(ReferenceTargetTypes.Length + ReferenceSourceTypes.Length) * 2)
        + ((long)(ReferenceTargetIds.Length + ReferenceRowStarts.Length
            + ReferenceRowCounts.Length + ReferenceRows.Length) * 4);

    public Dictionary<int, int[]> TagToItemIds { get; private init; }

    public FamilyBlock[] Families { get; private init; }

    public int[] LootTableStart { get; private init; }

    public int[] LootTableCount { get; private init; }

    public int[] LootEntryItem { get; private init; }

    public int[] LootEntryNested { get; private init; }

    /// <summary>Running weight totals, so a weighted draw is one binary search and no summation.</summary>
    public int[] LootEntryPrefixWeight { get; private init; }

    public static ContentIndexes Build(ContentRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        // Key to id is the per-type KeyIds table of section 9.1 rather than an index held here, but it is
        // still built at load and it is still one of 9.4's five, so it is built in this pass and timed with
        // the other four.
        foreach (KeyValuePair<ushort, ContentTypeTable> pair in runtime.Tables) pair.Value.EnsureKeyIndex();

        long referenceAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long referenceStarted = Stopwatch.GetTimestamp();
        (ushort[] targetTypes, int[] targetIds, ushort[] sourceTypes, int[] rowStarts, int[] rowCounts, int[] rows)
            = BuildReferenceIndex(runtime);
        double referenceMilliseconds = Stopwatch.GetElapsedTime(referenceStarted).TotalMilliseconds;
        long referenceAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - referenceAllocatedBefore;
        Dictionary<int, int[]> tagToItems = BuildTagIndex(runtime);
        (int[] start, int[] count, int[] item, int[] nested, int[] prefix) = BuildLootIndex(runtime);
        FamilyBlock[] families = BuildFamilies(runtime);

        return new ContentIndexes
        {
            ReferenceTargetTypes = targetTypes,
            ReferenceTargetIds = targetIds,
            ReferenceSourceTypes = sourceTypes,
            ReferenceRowStarts = rowStarts,
            ReferenceRowCounts = rowCounts,
            ReferenceRows = rows,
            ReferenceBuildMilliseconds = referenceMilliseconds,
            ReferenceBuildAllocatedBytes = referenceAllocatedBytes,
            TagToItemIds = tagToItems,
            Families = families,
            LootTableStart = start,
            LootTableCount = count,
            LootEntryItem = item,
            LootEntryNested = nested,
            LootEntryPrefixWeight = prefix,
        };
    }

    public long ApproximateBytes()
    {
        // The key to id index is not counted here: it lives on ContentTypeTable and is counted there.
        long bytes = ReferenceApproximateBytes
            + ((long)(LootTableStart.Length + LootTableCount.Length + LootEntryItem.Length
                + LootEntryNested.Length + LootEntryPrefixWeight.Length) * 4);
        foreach (KeyValuePair<int, int[]> pair in TagToItemIds) bytes += (pair.Value.LongLength * 4) + 32;
        return bytes + (Families.LongLength * 16);
    }

    private static (ushort[] TargetTypes, int[] TargetIds, ushort[] SourceTypes,
        int[] RowStarts, int[] RowCounts, int[] Rows) BuildReferenceIndex(ContentRuntime runtime)
    {
        if (!runtime.Tables.TryGetValue(ContentTypes.Item, out ContentTypeTable? table))
            return ([], [], [], [], [], []);

        var edges = new List<ReferenceEdge>(table.RowCount);
        for (int id = 1; id < table.Offsets.Length; id++)
        {
            if (table.Offsets[id] >= 0)
                edges.Add(new ReferenceEdge(ContentTypes.Item, id, ReferenceSatelliteType, id));
        }
        if (edges.Count == 0) return ([], [], [], [], [], []);

        edges.Sort(static (left, right) => left.CompareTo(right));
        int edgeCount = 1;
        for (int read = 1; read < edges.Count; read++)
        {
            ReferenceEdge edge = edges[read];
            if (edge != edges[edgeCount - 1]) edges[edgeCount++] = edge;
        }

        int entryCount = 1;
        for (int i = 1; i < edgeCount; i++)
            if (!edges[i].SameBucket(edges[i - 1])) entryCount++;

        var targetTypes = new ushort[entryCount];
        var targetIds = new int[entryCount];
        var sourceTypes = new ushort[entryCount];
        var rowStarts = new int[entryCount];
        var rowCounts = new int[entryCount];
        var rows = new int[edgeCount];
        int entry = -1;
        for (int i = 0; i < edgeCount; i++)
        {
            ReferenceEdge edge = edges[i];
            if (i == 0 || !edge.SameBucket(edges[i - 1]))
            {
                entry++;
                targetTypes[entry] = edge.TargetType;
                targetIds[entry] = edge.TargetId;
                sourceTypes[entry] = edge.SourceType;
                rowStarts[entry] = i;
            }
            rowCounts[entry]++;
            rows[i] = edge.RowId;
        }

        return (targetTypes, targetIds, sourceTypes, rowStarts, rowCounts, rows);
    }

    private static Dictionary<int, int[]> BuildTagIndex(ContentRuntime runtime)
    {
        var lists = new Dictionary<int, List<int>>();
        if (!runtime.Tables.TryGetValue(ContentTypes.Item, out ContentTypeTable? table)) return [];
        var row = new ItemRowData();
        for (int id = 0; id < table.Offsets.Length; id++)
        {
            if (table.Offsets[id] < 0) continue;
            ReadOnlySpan<byte> body = table.Body(id);
            ReadOnlySpan<byte> fields = body[ContentRuntime.FieldsOffset(body)..];
            if (!ContentRowCodec.TryDecodeItem(fields, ref row)) continue;
            AddTag(lists, row.TagCount > 0 ? row.Tag0 : 0, id);
            AddTag(lists, row.TagCount > 1 ? row.Tag1 : 0, id);
            AddTag(lists, row.TagCount > 2 ? row.Tag2 : 0, id);
            AddTag(lists, row.TagCount > 3 ? row.Tag3 : 0, id);
        }
        var index = new Dictionary<int, int[]>(lists.Count);
        foreach (KeyValuePair<int, List<int>> pair in lists) index[pair.Key] = pair.Value.ToArray();
        return index;
    }

    private static void AddTag(Dictionary<int, List<int>> lists, int tagId, int itemId)
    {
        if (tagId <= 0) return;
        if (!lists.TryGetValue(tagId, out List<int>? list))
        {
            list = [];
            lists[tagId] = list;
        }
        list.Add(itemId);
    }

    private static (int[] Start, int[] Count, int[] Item, int[] Nested, int[] Prefix) BuildLootIndex(ContentRuntime runtime)
    {
        if (!runtime.Tables.TryGetValue(ContentTypes.LootTable, out ContentTypeTable? tables)
            || !runtime.Tables.TryGetValue(ContentTypes.LootEntry, out ContentTypeTable? entries))
        {
            return ([], [], [], [], []);
        }

        int[] start = new int[tables.Offsets.Length];
        int[] count = new int[tables.Offsets.Length];
        var row = new LootEntryRowData();

        for (int id = 0; id < entries.Offsets.Length; id++)
        {
            if (entries.Offsets[id] < 0) continue;
            ReadOnlySpan<byte> body = entries.Body(id);
            if (!ContentRowCodec.TryDecodeLootEntry(body[ContentRuntime.FieldsOffset(body)..], ref row)) continue;
            if ((uint)row.Table < (uint)count.Length) count[row.Table]++;
        }
        int running = 0;
        for (int t = 0; t < count.Length; t++)
        {
            start[t] = running;
            running += count[t];
        }

        int[] item = new int[running];
        int[] nested = new int[running];
        int[] prefix = new int[running];
        int[] cursor = (int[])start.Clone();
        for (int id = 0; id < entries.Offsets.Length; id++)
        {
            if (entries.Offsets[id] < 0) continue;
            ReadOnlySpan<byte> body = entries.Body(id);
            if (!ContentRowCodec.TryDecodeLootEntry(body[ContentRuntime.FieldsOffset(body)..], ref row)) continue;
            if ((uint)row.Table >= (uint)count.Length) continue;
            int slot = cursor[row.Table]++;
            item[slot] = row.Item;
            nested[slot] = row.NestedTable;
            prefix[slot] = row.Weight;
        }
        for (int t = 0; t < count.Length; t++)
        {
            int total = 0;
            for (int i = 0; i < count[t]; i++)
            {
                total += prefix[start[t] + i];
                prefix[start[t] + i] = total;
            }
        }
        return (start, count, item, nested, prefix);
    }

    private static FamilyBlock[] BuildFamilies(ContentRuntime runtime)
    {
        if (!runtime.Tables.TryGetValue(ContentTypes.Item, out ContentTypeTable? table)) return [];
        var blocks = new List<FamilyBlock>();
        const int size = 65_536;
        int previous = -1;
        for (int id = 0; id < table.Offsets.Length; id++)
        {
            if (table.Offsets[id] < 0) continue;
            int blockBase = id & ~(size - 1);
            if (blockBase != previous && blockBase > 0)
            {
                blocks.Add(new FamilyBlock(ContentTypes.Item, blockBase, size));
                previous = blockBase;
            }
        }
        return blocks.ToArray();
    }

    private readonly record struct ReferenceEdge(
        ushort TargetType,
        int TargetId,
        ushort SourceType,
        int RowId)
    {
        public int CompareTo(ReferenceEdge other)
        {
            int targetType = TargetType.CompareTo(other.TargetType);
            if (targetType != 0) return targetType;
            int targetId = TargetId.CompareTo(other.TargetId);
            if (targetId != 0) return targetId;
            int sourceType = SourceType.CompareTo(other.SourceType);
            return sourceType != 0 ? sourceType : RowId.CompareTo(other.RowId);
        }

        public bool SameBucket(ReferenceEdge other) =>
            TargetType == other.TargetType && TargetId == other.TargetId && SourceType == other.SourceType;
    }
}
