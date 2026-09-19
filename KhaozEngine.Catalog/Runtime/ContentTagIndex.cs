using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog;

/// <summary>
/// The tag-to-ids index of spec 9.4, second of the engine's four: per content type, per tag id, the sorted
/// ids of the rows carrying that tag. A drop table with a <c>required_tags</c> filter walks it, and so does a
/// store pricing by item class.
/// <para>
/// FLAT ARRAYS rather than a dictionary of lists, because at the stress figure a list per tag is a hundred
/// thousand allocations holding four entries each. Three levels of slices: the types, each type's tag ids
/// ascending, and each tag's row ids ascending. A lookup is two binary searches over small sorted runs and
/// hands back a span, so it allocates nothing.
/// </para>
/// <para>
/// It indexes EVERY registered type carrying a tag-list field rather than just <c>item</c>, because a tag
/// list is a schema kind rather than an item feature and a game type declaring one gets the same index for
/// free. It indexes retired rows too: the retired bit is the READER's filter (a drop draw excludes them, an
/// admin listing does not), and an index that had dropped them could not answer the second question at all.
/// </para>
/// </summary>
public sealed class ContentTagIndex
{
    /// <summary>The index of a version carrying no tagged row at all.</summary>
    public static readonly ContentTagIndex Empty = new([], [], [], [], [], [], []);

    readonly ushort[] _types;
    readonly int[] _typeStart;
    readonly int[] _typeCount;
    readonly int[] _tagIds;
    readonly int[] _rowStart;
    readonly int[] _rowCount;
    readonly int[] _rows;

    ContentTagIndex(
        ushort[] types,
        int[] typeStart,
        int[] typeCount,
        int[] tagIds,
        int[] rowStart,
        int[] rowCount,
        int[] rows)
    {
        _types = types;
        _typeStart = typeStart;
        _typeCount = typeCount;
        _tagIds = tagIds;
        _rowStart = rowStart;
        _rowCount = rowCount;
        _rows = rows;
    }

    /// <summary>How many distinct (type, tag) pairs the version carries a row under.</summary>
    public int TagCount => _tagIds.Length;

    /// <summary>Every tag id this type has a tagged row under, ASCENDING.</summary>
    public ReadOnlySpan<int> TagsOf(ContentTypeId type)
    {
        int slot = IndexOfType(type.Value);
        return slot < 0 ? default : _tagIds.AsSpan(_typeStart[slot], _typeCount[slot]);
    }

    /// <summary>
    /// The ids of one type's rows carrying one tag, ASCENDING and distinct. Empty for a tag no row of that
    /// type carries, which is a miss rather than a failure.
    /// </summary>
    public ReadOnlySpan<int> Ids(ContentTypeId type, int tagId)
    {
        int slot = IndexOfType(type.Value);
        if (slot < 0)
        {
            return default;
        }

        int start = _typeStart[slot];
        int found = _tagIds.AsSpan(start, _typeCount[slot]).BinarySearch(tagId);
        if (found < 0)
        {
            return default;
        }

        int entry = start + found;
        return _rows.AsSpan(_rowStart[entry], _rowCount[entry]);
    }

    /// <summary>True when one row of one type carries one tag.</summary>
    public bool Carries(ContentTypeId type, int tagId, int id) => Ids(type, tagId).BinarySearch(id) >= 0;

    /// <summary>The managed bytes this index holds, for the memory line of spec 9.2.</summary>
    public long ApproximateBytes()
        => ((long)_types.Length * 2)
            + ((long)(_typeStart.Length + _typeCount.Length + _tagIds.Length) * 4)
            + ((long)(_rowStart.Length + _rowCount.Length + _rows.Length) * 4);

    /// <summary>
    /// One pass over every row of every type declaring a tag-list field, at load. The rows arrive ascending
    /// by id, so each tag's id list comes out sorted without a sort.
    /// </summary>
    internal static ContentTagIndex Build(ContentRuntime runtime)
    {
        var byType = new SortedDictionary<ushort, SortedDictionary<int, List<int>>>();
        IReadOnlyList<ContentTypeId> types = runtime.Types;
        for (int t = 0; t < types.Count; t++)
        {
            ContentTypeId type = types[t];
            if (!runtime.Registry.TryGet(type, out ContentTypeRegistration? registration))
            {
                continue;
            }

            int[] tagFields = TagFieldIndexes(registration.Schema);
            if (tagFields.Length == 0)
            {
                continue;
            }

            IReadOnlyList<ContentRow> rows = runtime.Rows(type);
            int claimedId = 0;
            for (int r = 0; r < rows.Count; r++)
            {
                ContentRow row = rows[r];
                if (row.Id < 1 || row.Id == claimedId)
                {
                    continue;
                }

                claimedId = row.Id;

                for (int f = 0; f < tagFields.Length; f++)
                {
                    Collect(byType, type.Value, row, tagFields[f]);
                }
            }
        }

        return byType.Count == 0 ? Empty : Flatten(byType);
    }

    /// <summary>Every tag-list field of one schema, by position, because a type may declare more than one.</summary>
    static int[] TagFieldIndexes(ContentFieldSchema schema)
    {
        List<int>? found = null;
        IReadOnlyList<ContentFieldEntry> fields = schema.Fields;
        for (int i = 0; i < fields.Count; i++)
        {
            if (fields[i].Kind == ContentFieldKind.TagList)
            {
                (found ??= []).Add(i);
            }
        }

        return found is null ? [] : found.ToArray();
    }

    static void Collect(
        SortedDictionary<ushort, SortedDictionary<int, List<int>>> byType,
        ushort type,
        ContentRow row,
        int fieldIndex)
    {
        if (fieldIndex >= row.Fields.Count)
        {
            return;
        }

        ContentFieldValue value = row.Fields[fieldIndex];
        if (value.IsAbsent || value.Bytes.Length == 0)
        {
            return;
        }

        // A tag list is the varint ids in AUTHORED order with no count of its own, which is what the row walk
        // wrote. A malformed one stops the walk rather than failing the load: the bytes already decoded into
        // a row, and a tag list nothing can read is the validator's finding.
        ReadOnlySpan<byte> bytes = value.Bytes.Span;
        int offset = 0;
        while (offset < bytes.Length)
        {
            if (!ContentVarint.TryRead(bytes, ref offset, out uint raw, out _))
            {
                return;
            }

            int tagId = unchecked((int)raw);
            if (tagId < 1)
            {
                continue;
            }

            if (!byType.TryGetValue(type, out SortedDictionary<int, List<int>>? tags))
            {
                tags = [];
                byType.Add(type, tags);
            }

            if (!tags.TryGetValue(tagId, out List<int>? ids))
            {
                ids = [];
                tags.Add(tagId, ids);
            }

            // The rows arrive ascending, so a repeat here is the same row listing the tag twice.
            if (ids.Count == 0 || ids[^1] != row.Id)
            {
                ids.Add(row.Id);
            }
        }
    }

    static ContentTagIndex Flatten(SortedDictionary<ushort, SortedDictionary<int, List<int>>> byType)
    {
        int tagTotal = 0;
        int rowTotal = 0;
        foreach (KeyValuePair<ushort, SortedDictionary<int, List<int>>> type in byType)
        {
            tagTotal += type.Value.Count;
            foreach (KeyValuePair<int, List<int>> tag in type.Value)
            {
                rowTotal += tag.Value.Count;
            }
        }

        var types = new ushort[byType.Count];
        var typeStart = new int[byType.Count];
        var typeCount = new int[byType.Count];
        var tagIds = new int[tagTotal];
        var rowStart = new int[tagTotal];
        var rowCount = new int[tagTotal];
        var rows = new int[rowTotal];

        int typeSlot = 0;
        int tagSlot = 0;
        int rowSlot = 0;
        foreach (KeyValuePair<ushort, SortedDictionary<int, List<int>>> type in byType)
        {
            types[typeSlot] = type.Key;
            typeStart[typeSlot] = tagSlot;
            typeCount[typeSlot] = type.Value.Count;
            typeSlot++;

            foreach (KeyValuePair<int, List<int>> tag in type.Value)
            {
                tagIds[tagSlot] = tag.Key;
                rowStart[tagSlot] = rowSlot;
                rowCount[tagSlot] = tag.Value.Count;
                tagSlot++;

                for (int i = 0; i < tag.Value.Count; i++)
                {
                    rows[rowSlot++] = tag.Value[i];
                }
            }
        }

        return new ContentTagIndex(types, typeStart, typeCount, tagIds, rowStart, rowCount, rows);
    }

    int IndexOfType(ushort type)
    {
        // Ascending, and there are a handful of types rather than a handful of thousands, so a linear scan
        // beats a binary search on the sizes this ever sees.
        for (int i = 0; i < _types.Length; i++)
        {
            if (_types[i] == type)
            {
                return i;
            }
        }

        return -1;
    }
}
