using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog;

/// <summary>
/// The load-time build of <see cref="ContentLootIndex"/>, kept apart from the index it produces: the index is
/// read on the tick and the build runs once at boot, and the two have nothing in common but the arrays that
/// pass between them.
/// <para>
/// Two passes over the <c>loot_entry</c> rows, so every array is sized exactly and nothing grows: count the
/// entries per table, then fill. The weights are prefix summed in the fill's last step, which is what turns a
/// draw into one binary search, and a GUARANTEED entry contributes zero to that sum because it rolls its own
/// chance instead of competing in the draw.
/// </para>
/// </summary>
internal static class ContentLootIndexBuilder
{
    internal static ContentLootIndex Build(ContentRuntime runtime, ContentTagIndex tags)
    {
        if (!TryResolveTypes(runtime, out LootSchema schema))
        {
            return ContentLootIndex.Empty;
        }

        IReadOnlyList<ContentRow> tableRows = runtime.Rows(schema.TableType);
        IReadOnlyList<ContentRow> entryRows = runtime.Rows(schema.EntryType);
        if (tableRows.Count == 0)
        {
            return ContentLootIndex.Empty;
        }

        int highestTable = 0;
        for (int i = 0; i < tableRows.Count; i++)
        {
            if (tableRows[i].Id > highestTable)
            {
                highestTable = tableRows[i].Id;
            }
        }

        int slots = highestTable + 1;
        var start = new int[slots];
        var count = new int[slots];
        var rollCount = new int[slots];
        var seen = new bool[slots];

        for (int i = 0; i < tableRows.Count; i++)
        {
            ContentRow row = tableRows[i];
            if (row.Id < 1 || seen[row.Id])
            {
                // A duplicate id is KEC0036's finding, and the FIRST row in id order wins here exactly as it
                // wins the type table's lookup.
                continue;
            }

            seen[row.Id] = true;
            rollCount[row.Id] = (int)Number(row, schema.RollCountField);
        }

        // Pass one: how many entries each table owns, so the flat arrays are sized rather than grown.
        var ordered = new List<Ordered>(entryRows.Count);
        for (int i = 0; i < entryRows.Count; i++)
        {
            ContentRow row = entryRows[i];
            if (row.Id < 1)
            {
                continue;
            }

            int tableId = (int)Number(row, schema.TableField);
            if (tableId < 1 || tableId >= slots || !seen[tableId])
            {
                // An entry naming no table, or one this version does not carry, is KEC0005 or KEC0006. The
                // index drops it rather than failing the load, because the validator is what reports it.
                // The test is the table ROW, not the array bound: the arrays are sized to the highest table
                // id, so an absent id below it would otherwise become a table nothing authored, counted in
                // TableCount and rolled through the guaranteed pass.
                continue;
            }

            count[tableId]++;
            ordered.Add(new Ordered(tableId, (int)Number(row, schema.SortField), row.Id, i));
        }

        int running = 0;
        for (int id = 0; id < slots; id++)
        {
            start[id] = running;
            running += count[id];
        }

        // By table, then by the authored sort order, then by id, which is the order spec 3.5 fixes for the
        // guaranteed pass and a deterministic one for the weighted pass.
        ordered.Sort(static (left, right) =>
        {
            if (left.TableId != right.TableId)
            {
                return left.TableId.CompareTo(right.TableId);
            }

            return left.Sort != right.Sort ? left.Sort.CompareTo(right.Sort) : left.EntryId.CompareTo(right.EntryId);
        });

        var entries = new ContentLootEntry[running];
        var prefixWeights = new int[running];
        var candidateStart = new int[running];
        var candidateCount = new int[running];
        var candidates = new List<int>();

        var cursor = new int[slots];
        for (int i = 0; i < ordered.Count; i++)
        {
            Ordered each = ordered[i];
            ContentRow row = entryRows[each.RowIndex];
            int slot = start[each.TableId] + cursor[each.TableId]++;

            bool guaranteed = Number(row, schema.GuaranteedField) != 0;
            int weight = (int)Number(row, schema.WeightField);
            entries[slot] = new ContentLootEntry(
                row.Id,
                (int)Number(row, schema.ItemField),
                (int)Number(row, schema.NestedTableField),
                guaranteed,
                0,
                (int)Number(row, schema.ChanceField),
                (int)Number(row, schema.MinCountField),
                (int)Number(row, schema.MaxCountField),
                each.Sort);

            // A guaranteed entry is out of the weighted pool, so it widens the running total by nothing and a
            // pick can never land on it.
            prefixWeights[slot] = guaranteed || weight < 0 ? 0 : weight;

            candidateStart[slot] = candidates.Count;
            ResolveCandidates(runtime, tags, row, schema, candidates);
            candidateCount[slot] = candidates.Count - candidateStart[slot];
        }

        int tableCount = 0;
        for (int id = 0; id < slots; id++)
        {
            if (count[id] == 0)
            {
                continue;
            }

            tableCount++;
            long total = 0;
            for (int i = 0; i < count[id]; i++)
            {
                int slot = start[id] + i;

                // Saturating, because a non-monotonic prefix array is unsearchable and an overflowed one
                // would wrap negative. The authored weights are the validator's to bound.
                total = Math.Min(total + prefixWeights[slot], int.MaxValue);
                prefixWeights[slot] = (int)total;
                entries[slot] = entries[slot] with { PrefixWeight = (int)total };
            }
        }

        return new ContentLootIndex(
            tableCount,
            start,
            count,
            rollCount,
            entries,
            prefixWeights,
            candidateStart,
            candidateCount,
            candidates.ToArray());
    }

    /// <summary>
    /// A tag-filtered entry's candidates: every LIVE item carrying EVERY listed tag, ascending by id. The
    /// tag index is already sorted per tag, so this walks the shortest list and tests the rest.
    /// </summary>
    static void ResolveCandidates(
        ContentRuntime runtime,
        ContentTagIndex tags,
        ContentRow row,
        in LootSchema schema,
        List<int> candidates)
    {
        if (schema.RequiredTagsField < 0 || schema.RequiredTagsField >= row.Fields.Count)
        {
            return;
        }

        ContentFieldValue value = row.Fields[schema.RequiredTagsField];
        if (value.IsAbsent || value.Bytes.Length == 0)
        {
            return;
        }

        var required = new List<int>();
        ReadOnlySpan<byte> bytes = value.Bytes.Span;
        int offset = 0;
        while (offset < bytes.Length)
        {
            if (!ContentVarint.TryRead(bytes, ref offset, out uint raw, out _))
            {
                return;
            }

            int tagId = unchecked((int)raw);
            if (tagId >= 1 && !required.Contains(tagId))
            {
                required.Add(tagId);
            }
        }

        if (required.Count == 0)
        {
            return;
        }

        var itemType = new ContentTypeId(EngineContentTypes.ItemTypeId);
        int narrowest = 0;
        for (int i = 1; i < required.Count; i++)
        {
            if (tags.Ids(itemType, required[i]).Length < tags.Ids(itemType, required[narrowest]).Length)
            {
                narrowest = i;
            }
        }

        ReadOnlySpan<int> walk = tags.Ids(itemType, required[narrowest]);
        for (int i = 0; i < walk.Length; i++)
        {
            int itemId = walk[i];
            if (runtime.IsRetired(itemType, itemId))
            {
                continue;
            }

            bool all = true;
            for (int t = 0; t < required.Count && all; t++)
            {
                all = t == narrowest || tags.Carries(itemType, required[t], itemId);
            }

            if (all)
            {
                candidates.Add(itemId);
            }
        }
    }

    /// <summary>
    /// The two engine loot types and their field positions, or false when this build does not register them.
    /// Resolved by NAME against the registered schema rather than by a constant position, so the index does
    /// not silently read the wrong field if the schema's order ever moves.
    /// </summary>
    static bool TryResolveTypes(ContentRuntime runtime, out LootSchema schema)
    {
        schema = default;
        var tableType = new ContentTypeId(EngineContentTypes.LootTableTypeId);
        var entryType = new ContentTypeId(EngineContentTypes.LootEntryTypeId);
        if (!runtime.Registry.TryGet(tableType, out ContentTypeRegistration? tables)
            || !runtime.Registry.TryGet(entryType, out ContentTypeRegistration? entries)
            || !string.Equals(tables.TypeKey, EngineContentTypes.LootTableTypeKey, StringComparison.Ordinal)
            || !string.Equals(entries.TypeKey, EngineContentTypes.LootEntryTypeKey, StringComparison.Ordinal))
        {
            return false;
        }

        schema = new LootSchema(
            tableType,
            entryType,
            IndexOf(tables.Schema, LootTableContentType.RollCountField),
            IndexOf(entries.Schema, LootEntryContentType.TableField),
            IndexOf(entries.Schema, LootEntryContentType.ItemField),
            IndexOf(entries.Schema, LootEntryContentType.NestedTableField),
            IndexOf(entries.Schema, LootEntryContentType.WeightField),
            IndexOf(entries.Schema, LootEntryContentType.ChanceBasisPointsField),
            IndexOf(entries.Schema, LootEntryContentType.GuaranteedField),
            IndexOf(entries.Schema, LootEntryContentType.MinCountField),
            IndexOf(entries.Schema, LootEntryContentType.MaxCountField),
            IndexOf(entries.Schema, LootEntryContentType.SortField),
            IndexOf(entries.Schema, LootEntryContentType.RequiredTagsField));

        return schema.TableField >= 0 && schema.WeightField >= 0;
    }

    static int IndexOf(ContentFieldSchema schema, string name)
    {
        IReadOnlyList<ContentFieldEntry> fields = schema.Fields;
        for (int i = 0; i < fields.Count; i++)
        {
            if (string.Equals(fields[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>One field's number, or 0 for an absent field or a position this schema does not have.</summary>
    static long Number(ContentRow row, int fieldIndex)
    {
        if (fieldIndex < 0 || fieldIndex >= row.Fields.Count)
        {
            return 0;
        }

        ContentFieldValue value = row.Fields[fieldIndex];
        return value.IsAbsent ? 0 : value.Number;
    }

    /// <summary>The two loot types and where every field this index reads sits in their schemas.</summary>
    readonly record struct LootSchema(
        ContentTypeId TableType,
        ContentTypeId EntryType,
        int RollCountField,
        int TableField,
        int ItemField,
        int NestedTableField,
        int WeightField,
        int ChanceField,
        int GuaranteedField,
        int MinCountField,
        int MaxCountField,
        int SortField,
        int RequiredTagsField);

    /// <summary>One entry on its way into the flat arrays, carrying what its position is decided by.</summary>
    readonly record struct Ordered(int TableId, int Sort, int EntryId, int RowIndex);
}
