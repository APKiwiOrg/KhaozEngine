using System;
using System.Collections.Generic;
using System.Linq;

namespace KhaozEngine.Attachments;

/// <summary>Owns a bounded snapshot, refusing known excess counts before copying and unknown excess at one witness.</summary>
internal static class CoverageCollections
{
    internal static T[] Snapshot<T>(IEnumerable<T> source, int limit, string parameter, string message)
    {
        int capacity = source.TryGetNonEnumeratedCount(out int known) ? known
            : source is IReadOnlyCollection<T> collection ? collection.Count : 0;
        if (capacity > limit) throw new ArgumentException(message, parameter);
        T[] owned = capacity == 0 ? [] : new T[capacity];
        int count = 0;
        foreach (T item in source)
        {
            if (count == limit) throw new ArgumentException(message, parameter);
            if (count == owned.Length)
                Array.Resize(ref owned, Math.Min(limit, Math.Max(4, owned.Length * 2)));
            owned[count++] = item;
        }
        if (count != owned.Length) Array.Resize(ref owned, count);
        return owned;
    }
}
