using System;
using System.Collections.Generic;

namespace KhaozEngine.MapDoc;

/// <summary>Monotonic numeric identity allocation, including imported IDs and deletion tombstones.</summary>
public static class MapNumericIds
{
    /// <summary>Reserves above all imported placements, supplied IDs and the existing high-water mark.
    /// The supplied sequence must contain distinct positive IDs. It may name an existing placement.
    /// Invalid data leaves the document unchanged.</summary>
    public static void Reserve(MapDocument document, IEnumerable<long> ids)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(ids);
        var errors = new List<string>();
        long maximum = MapNativeValidation.ValidateNumericIds(document, errors, requireHighWater: false);
        ThrowIfInvalid(errors);
        var reserved = new HashSet<long>();
        foreach (long id in ids)
        {
            if (id <= 0 || !reserved.Add(id))
                throw new MapDocumentException("Reserved numeric IDs must be distinct positive int64 values.");
            maximum = Math.Max(maximum, id);
        }
        document.NumericIdHighWaterMark = maximum;
    }

    /// <summary>Allocates above the persisted high-water mark. Exhaustion throws before mutation.
    /// Imported placements must first be reserved so their IDs do not exceed the high-water mark.</summary>
    public static long Allocate(MapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var errors = new List<string>();
        MapNativeValidation.ValidateNumericIds(document, errors);
        ThrowIfInvalid(errors);
        long next = checked(document.NumericIdHighWaterMark + 1);
        document.NumericIdHighWaterMark = next;
        return next;
    }

    static void ThrowIfInvalid(List<string> errors)
    {
        if (errors.Count > 0)
            throw new MapDocumentException("Invalid numeric identities:\n  " + string.Join("\n  ", errors));
    }
}
