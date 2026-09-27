using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog;

/// <summary>
/// The reverse index over every <see cref="ContentFieldKind.KeyReference"/> field: a target type and id plus
/// a referencing type to the sorted, distinct ids of the rows that name that target.
/// <para>
/// It is built eagerly with the runtime's other derived indexes. A lookup is one binary search over flat
/// arrays and one span slice, with no row walk and no allocation. A version change builds a new instance
/// beside the old runtime, so each reader keeps the relationships of the version it started with.
/// </para>
/// <para>
/// Retired target rows and retired referencing rows stay indexed. Retirement is the READER's filter, as it
/// is for <see cref="ContentTagIndex"/>, because stored data and admin reads still need those relationships.
/// A reference to a target row the runtime does not carry is not indexed. The validator reports that defect
/// without leaving a phantom target bucket in this read index.
/// </para>
/// </summary>
public sealed class ContentReferenceIndex
{
    /// <summary>The index of a version carrying no resolvable key reference.</summary>
    public static readonly ContentReferenceIndex Empty = new([], [], [], [], [], []);

    readonly ushort[] _targetTypes;
    readonly int[] _targetIds;
    readonly ushort[] _referencingTypes;
    readonly int[] _rowStarts;
    readonly int[] _rowCounts;
    readonly int[] _rows;

    ContentReferenceIndex(
        ushort[] targetTypes,
        int[] targetIds,
        ushort[] referencingTypes,
        int[] rowStarts,
        int[] rowCounts,
        int[] rows)
    {
        _targetTypes = targetTypes;
        _targetIds = targetIds;
        _referencingTypes = referencingTypes;
        _rowStarts = rowStarts;
        _rowCounts = rowCounts;
        _rows = rows;
    }

    /// <summary>
    /// The ids of one referencing type's rows that point at one target row, ASCENDING and distinct. Empty
    /// when the target row is absent or no row of the referencing type points at it.
    /// </summary>
    /// <param name="targetType">The content type the reference field names.</param>
    /// <param name="targetId">The referenced row id.</param>
    /// <param name="referencingType">The content type whose row ids are returned.</param>
    public ReadOnlySpan<int> Ids(
        ContentTypeId targetType,
        int targetId,
        ContentTypeId referencingType)
    {
        int low = 0;
        int high = _targetIds.Length - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            int comparison = Compare(
                _targetTypes[middle],
                _targetIds[middle],
                _referencingTypes[middle],
                targetType.Value,
                targetId,
                referencingType.Value);
            if (comparison < 0)
            {
                low = middle + 1;
            }
            else if (comparison > 0)
            {
                high = middle - 1;
            }
            else
            {
                return _rows.AsSpan(_rowStarts[middle], _rowCounts[middle]);
            }
        }

        return default;
    }

    /// <summary>The managed bytes the six flat arrays hold.</summary>
    public long ApproximateBytes()
        => ((long)(_targetTypes.Length + _referencingTypes.Length) * 2)
            + ((long)(_targetIds.Length + _rowStarts.Length + _rowCounts.Length + _rows.Length) * 4);

    /// <summary>
    /// One pass over every row of every type declaring a key-reference field. Rows and registry types arrive
    /// ascending, and the sorted build maps make that ordering independent of registration or field order.
    /// </summary>
    internal static ContentReferenceIndex Build(ContentRuntime runtime)
    {
        var byTarget = new SortedDictionary<
            ushort,
            SortedDictionary<int, SortedDictionary<ushort, List<int>>>>();

        IReadOnlyList<ContentTypeId> types = runtime.Types;
        for (int t = 0; t < types.Count; t++)
        {
            ContentTypeId referencingType = types[t];
            if (!runtime.Registry.TryGet(referencingType, out ContentTypeRegistration? registration))
            {
                continue;
            }

            ReferenceField[] fields = ReferenceFields(runtime.Registry, registration.Schema);
            if (fields.Length == 0)
            {
                continue;
            }

            IReadOnlyList<ContentRow> rows = runtime.Rows(referencingType);
            int claimedId = 0;
            for (int r = 0; r < rows.Count; r++)
            {
                ContentRow row = rows[r];
                if (row.Id < 1 || row.Id == claimedId)
                {
                    continue;
                }

                claimedId = row.Id;
                for (int f = 0; f < fields.Length; f++)
                {
                    Collect(byTarget, runtime, referencingType, row, fields[f]);
                }
            }
        }

        return byTarget.Count == 0 ? Empty : Flatten(byTarget);
    }

    static ReferenceField[] ReferenceFields(ContentTypeRegistry registry, ContentFieldSchema schema)
    {
        List<ReferenceField>? found = null;
        IReadOnlyList<ContentFieldEntry> fields = schema.Fields;
        for (int i = 0; i < fields.Count; i++)
        {
            ContentFieldEntry field = fields[i];
            if (field.Kind == ContentFieldKind.KeyReference
                && field.ReferenceTarget is string target
                && registry.TryGetByKey(target, out ContentTypeRegistration? registration))
            {
                (found ??= []).Add(new ReferenceField(i, registration.Type));
            }
        }

        return found is null ? [] : found.ToArray();
    }

    static void Collect(
        SortedDictionary<ushort, SortedDictionary<int, SortedDictionary<ushort, List<int>>>> byTarget,
        ContentRuntime runtime,
        ContentTypeId referencingType,
        ContentRow row,
        ReferenceField field)
    {
        if (field.Index >= row.Fields.Count)
        {
            return;
        }

        ContentFieldValue value = row.Fields[field.Index];
        if (value.IsAbsent
            || value.Kind != ContentFieldKind.KeyReference
            || value.Number < 1
            || value.Number > int.MaxValue)
        {
            return;
        }

        int targetId = (int)value.Number;
        if (!runtime.HasRow(field.TargetType, targetId))
        {
            return;
        }

        if (!byTarget.TryGetValue(
                field.TargetType.Value,
                out SortedDictionary<int, SortedDictionary<ushort, List<int>>>? ids))
        {
            ids = [];
            byTarget.Add(field.TargetType.Value, ids);
        }

        if (!ids.TryGetValue(targetId, out SortedDictionary<ushort, List<int>>? types))
        {
            types = [];
            ids.Add(targetId, types);
        }

        if (!types.TryGetValue(referencingType.Value, out List<int>? rows))
        {
            rows = [];
            types.Add(referencingType.Value, rows);
        }

        // Rows arrive ascending. A repeat is the same row naming one target through a second field.
        if (rows.Count == 0 || rows[^1] != row.Id)
        {
            rows.Add(row.Id);
        }
    }

    static ContentReferenceIndex Flatten(
        SortedDictionary<ushort, SortedDictionary<int, SortedDictionary<ushort, List<int>>>> byTarget)
    {
        int entryTotal = 0;
        int rowTotal = 0;
        foreach (KeyValuePair<ushort, SortedDictionary<int, SortedDictionary<ushort, List<int>>>> target in byTarget)
        {
            foreach (KeyValuePair<int, SortedDictionary<ushort, List<int>>> id in target.Value)
            {
                entryTotal += id.Value.Count;
                foreach (KeyValuePair<ushort, List<int>> type in id.Value)
                {
                    rowTotal += type.Value.Count;
                }
            }
        }

        var targetTypes = new ushort[entryTotal];
        var targetIds = new int[entryTotal];
        var referencingTypes = new ushort[entryTotal];
        var rowStarts = new int[entryTotal];
        var rowCounts = new int[entryTotal];
        var rows = new int[rowTotal];

        int entrySlot = 0;
        int rowSlot = 0;
        foreach (KeyValuePair<ushort, SortedDictionary<int, SortedDictionary<ushort, List<int>>>> target in byTarget)
        {
            foreach (KeyValuePair<int, SortedDictionary<ushort, List<int>>> id in target.Value)
            {
                foreach (KeyValuePair<ushort, List<int>> type in id.Value)
                {
                    targetTypes[entrySlot] = target.Key;
                    targetIds[entrySlot] = id.Key;
                    referencingTypes[entrySlot] = type.Key;
                    rowStarts[entrySlot] = rowSlot;
                    rowCounts[entrySlot] = type.Value.Count;
                    entrySlot++;

                    for (int i = 0; i < type.Value.Count; i++)
                    {
                        rows[rowSlot++] = type.Value[i];
                    }
                }
            }
        }

        return new ContentReferenceIndex(
            targetTypes, targetIds, referencingTypes, rowStarts, rowCounts, rows);
    }

    static int Compare(
        ushort leftTargetType,
        int leftTargetId,
        ushort leftReferencingType,
        ushort rightTargetType,
        int rightTargetId,
        ushort rightReferencingType)
    {
        int type = leftTargetType.CompareTo(rightTargetType);
        if (type != 0)
        {
            return type;
        }

        int id = leftTargetId.CompareTo(rightTargetId);
        return id != 0 ? id : leftReferencingType.CompareTo(rightReferencingType);
    }

    readonly record struct ReferenceField(int Index, ContentTypeId TargetType);
}
