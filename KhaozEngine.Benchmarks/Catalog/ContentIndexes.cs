using System;
using System.Collections.Generic;

namespace KhaozEngine.Benchmarks.Catalog;

/// <summary>A family's contiguous id block, so membership is two comparisons (contracts 5.2).</summary>
public readonly record struct FamilyBlock(ushort TypeId, int Base, int Size)
{
    public bool Contains(int id) => (id & ~(Size - 1)) == Base;
}

/// <summary>
/// The four derived indexes the engine builds once at load, spec section 9.4: key to id per type, tag to
/// ids, family membership, and the loot candidate arrays with their weights prefix summed. All four are
/// flat arrays rather than a collection per row, because at the stress figure a dictionary per table is
/// a hundred thousand allocations for four entries each. The first of the four lives on the type table it
/// indexes (<see cref="ContentTypeTable.KeyIds"/>), because it is keyed on a slice of that table's blob.
/// </summary>
public sealed class ContentIndexes
{
    public static readonly ContentIndexes Empty = new();

    private ContentIndexes()
    {
        TagToItemIds = [];
        Families = [];
        LootTableStart = [];
        LootTableCount = [];
        LootEntryItem = [];
        LootEntryNested = [];
        LootEntryPrefixWeight = [];
    }

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
        // still built at load and it is still one of 9.4's four, so it is built in this pass and timed with
        // the other three.
        foreach (KeyValuePair<ushort, ContentTypeTable> pair in runtime.Tables) pair.Value.EnsureKeyIndex();

        Dictionary<int, int[]> tagToItems = BuildTagIndex(runtime);
        (int[] start, int[] count, int[] item, int[] nested, int[] prefix) = BuildLootIndex(runtime);
        FamilyBlock[] families = BuildFamilies(runtime);

        return new ContentIndexes
        {
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
        long bytes = (long)(LootTableStart.Length + LootTableCount.Length + LootEntryItem.Length
            + LootEntryNested.Length + LootEntryPrefixWeight.Length) * 4;
        foreach (KeyValuePair<int, int[]> pair in TagToItemIds) bytes += (pair.Value.LongLength * 4) + 32;
        return bytes + (Families.LongLength * 16);
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
}
