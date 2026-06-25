using System;

namespace KhaozEngine.Netcode;

/// <summary>
/// Hands out the lowest free player slot in <c>[0, maxSlots)</c>, recycles released slots, and refuses when
/// full. Same small-integer slot model <see cref="RemoteCommandQueue{TCommand}"/> keys commands on.
/// </summary>
public sealed class SlotAllocator
{
    private readonly bool[] used;

    public SlotAllocator(int maxSlots)
    {
        if (maxSlots <= 0) throw new ArgumentOutOfRangeException(nameof(maxSlots), maxSlots, "must be positive");
        used = new bool[maxSlots];
    }

    /// <summary>Max concurrent slots.</summary>
    public int Capacity => used.Length;

    /// <summary>Allocates the lowest free slot. Returns false (slot = -1) when full.</summary>
    public bool TryAllocate(out int slot)
    {
        for (int i = 0; i < used.Length; i++)
        {
            if (!used[i]) { used[i] = true; slot = i; return true; }
        }
        slot = -1;
        return false;
    }

    /// <summary>Frees a slot for reuse. Ignores an already-free or out-of-range slot.</summary>
    public void Release(int slot)
    {
        if (slot >= 0 && slot < used.Length) used[slot] = false;
    }
}
