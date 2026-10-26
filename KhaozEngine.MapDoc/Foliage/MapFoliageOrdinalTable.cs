using System;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>First-occurrence ordinal table. Hashes locate full ordinal-equal strings, never substitute for identities.</summary>
internal sealed class MapFoliageOrdinalTable(MapFoliageColumnBudget budget)
{
    string?[]? _keys;
    int[]? _ordinals;
    int _nullIndex = -1;
    internal int Count { get; private set; }
    internal long WorkspaceBytes => _keys is null ? 0 : Bytes(_keys.Length);
    internal bool Contains(string? value) => value is null ? _nullIndex >= 0 : Find(value, out _);
    internal bool Holds(string value) => Find(value, out int slot) && ReferenceEquals(_keys![slot], value);
    bool Find(string value, out int slot)
    {
        slot = 0;
        if (_keys is null) return false;
        slot = StringComparer.Ordinal.GetHashCode(value) & (_keys.Length - 1);
        while (_keys[slot] is { } candidate)
        {
            if (StringComparer.Ordinal.Equals(value, candidate)) return true;
            slot = (slot + 1) & (_keys.Length - 1);
        }
        return false;
    }
    internal int Intern(string? value)
    {
        if (value is null)
        {
            if (_nullIndex < 0) _nullIndex = Count++;
            return _nullIndex;
        }
        if (Find(value, out int slot)) return _ordinals![slot];
        if (_keys is null || checked((long)(Count + 1) * 2) > _keys.Length)
        {
            int capacity = _keys is null ? 4 : checked(_keys.Length * 2);
            long old = WorkspaceBytes;
            budget.Take(Bytes(capacity));
            Grow(capacity);
            budget.Release(old);
            _ = Find(value, out slot);
        }
        _keys![slot] = value; _ordinals![slot] = Count;
        return Count++;
    }
    void Grow(int capacity)
    {
        var keys = new string?[capacity]; var ordinals = new int[capacity];
        if (_keys is not null)
            for (int i = 0; i < _keys.Length; i++)
                if (_keys[i] is { } key)
                {
                    int slot = StringComparer.Ordinal.GetHashCode(key) & (capacity - 1);
                    while (keys[slot] is not null) slot = (slot + 1) & (capacity - 1);
                    keys[slot] = key; ordinals[slot] = _ordinals![i];
                }
        _keys = keys; _ordinals = ordinals;
    }
    internal string?[] Freeze()
    {
        budget.Take(MapFoliageInventory.ReferenceArray(Count));
        var result = new string?[Count];
        CopyTo(result);
        return result;
    }
    internal string[] FreezeModels()
    {
        if (_nullIndex >= 0) throw new InvalidOperationException("A model ordinal cannot be null.");
        budget.Take(MapFoliageInventory.ReferenceArray(Count));
        var result = new string[Count];
        CopyTo(result);
        return result;
    }
    void CopyTo(string?[] result)
    {
        if (_keys is not null)
            for (int i = 0; i < _keys.Length; i++)
                if (_keys[i] is { } value) result[_ordinals![i]] = value;
    }
    internal void Clear()
    {
        long old = WorkspaceBytes;
        _keys = null; _ordinals = null;
        budget.Release(old);
    }
    static long Bytes(int capacity) => checked(MapFoliageInventory.ReferenceArray(capacity) + MapFoliageInventory.Array(capacity, 4));
}
