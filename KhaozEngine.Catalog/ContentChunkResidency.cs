using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace KhaozEngine.Catalog;

/// <summary>
/// Decoded chunks held either until snapshot handoff or in a bounded least recently used set. The unbounded
/// form allocates no recency nodes, because server boot reads every chunk once and then clears the set.
/// </summary>
internal sealed class ContentChunkResidency<T> where T : class
{
    readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    readonly LinkedList<Entry>? recency;
    readonly object sync = new();
    readonly int capacity;

    public ContentChunkResidency(int? capacity)
    {
        if (capacity is int bound)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(bound, 1);
            this.capacity = bound;
            recency = new LinkedList<Entry>();
        }
    }

    public int Count
    {
        get
        {
            lock (sync)
            {
                return entries.Count;
            }
        }
    }

    public bool TryGetValue(string key, [NotNullWhen(true)] out T? value)
    {
        lock (sync)
        {
            if (!entries.TryGetValue(key, out Entry? entry))
            {
                value = null;
                return false;
            }

            Touch(entry);
            value = entry.Value;
            return true;
        }
    }

    public T AddOrGetExisting(string key, T value)
    {
        lock (sync)
        {
            if (entries.TryGetValue(key, out Entry? existing))
            {
                Touch(existing);
                return existing.Value;
            }

            var entry = new Entry(key, value);
            entries.Add(key, entry);
            if (recency is null)
            {
                return value;
            }

            entry.Node = recency.AddFirst(entry);
            if (entries.Count <= capacity)
            {
                return value;
            }

            Entry oldest = recency.Last!.Value;
            recency.RemoveLast();
            entries.Remove(oldest.Key);
            return value;
        }
    }

    public void CopyValuesTo(List<T> destination)
    {
        lock (sync)
        {
            foreach (Entry entry in entries.Values)
            {
                destination.Add(entry.Value);
            }
        }
    }

    public void Clear()
    {
        lock (sync)
        {
            entries.Clear();
            recency?.Clear();
        }
    }

    void Touch(Entry entry)
    {
        if (recency is null)
        {
            return;
        }

        recency.Remove(entry.Node!);
        recency.AddFirst(entry.Node!);
    }

    sealed class Entry(string key, T value)
    {
        public string Key { get; } = key;

        public T Value { get; } = value;

        public LinkedListNode<Entry>? Node { get; set; }
    }
}
