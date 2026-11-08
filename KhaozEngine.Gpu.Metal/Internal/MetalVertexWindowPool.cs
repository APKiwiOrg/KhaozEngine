using System;
using System.Collections.Generic;

namespace KhaozEngine.Gpu.Metal.Internal;

/// <summary>Reusable disconnected-window stores, leased only when a key actually needs one.</summary>
internal sealed class MetalVertexWindowPool
{
    internal sealed class Store
    {
        internal readonly int Level, Limit;
        internal readonly Dictionary<ulong, ulong> Values;
        internal Store? Next;

        internal Store(int level)
        {
            Level = level;
            Limit = level == 31 ? int.MaxValue : 1 << level;
            Values = new(Limit);
        }
    }

    // A small demand never takes a large store that another key may need later in the recording.
    // Geometric classes retain the observed per-class concurrency, not the global window peak per key.
    // Stores contain only element ranges and links. No buffer, GPU object or key identity crosses Begin.
    readonly Store?[] _free = new Store?[32];
    internal long RetainedCapacityForTests { get; private set; }

    Store Rent(int level)
    {
        if ((uint)level >= _free.Length) throw new OutOfMemoryException("The Metal window store cannot grow further.");
        if (_free[level] is { } store)
        {
            _free[level] = store.Next;
            store.Next = null;
            return store;
        }
        store = new(level);
        RetainedCapacityForTests += store.Values.EnsureCapacity(0);
        return store;
    }

    internal void Return(Store store)
    {
        store.Values.Clear();
        store.Next = _free[store.Level];
        _free[store.Level] = store;
    }

    internal Store Record(Store? store, ulong first, ulong last)
    {
        if (store is not null && store.Values.TryGetValue(first, out ulong end))
        {
            store.Values[first] = Math.Max(end, last);
            return store;
        }
        if (store is null) store = Rent(0);
        else if (store.Values.Count == store.Limit)
        {
            Store larger = Rent(store.Level + 1);
            foreach (var window in store.Values) larger.Values.Add(window.Key, window.Value);
            Return(store);
            store = larger;
        }
        store.Values.Add(first, last);
        return store;
    }
}
