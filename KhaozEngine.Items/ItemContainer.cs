using System;

namespace KhaozEngine.Items;

/// <summary>One slot's contents: an opaque item id and how many of it. The engine never learns what an id
/// means.</summary>
/// <param name="ItemId">The game's own item identity. Zero is the empty slot's id and never a real item.</param>
/// <param name="Count">How many. An occupied slot's count is always at least one.</param>
public readonly record struct ItemStack(int ItemId, int Count)
{
    /// <summary>Whether this slot holds nothing. The empty stack is the default value, so a cleared slot and a
    /// never-filled one are the same value.</summary>
    public bool IsEmpty => ItemId == 0 || Count <= 0;

    /// <summary>The empty slot.</summary>
    public static ItemStack Empty => default;
}

/// <summary>
/// A fixed number of slots holding opaque item stacks: the container KERNEL, in the sense
/// <c>KhaozEngine.Stats</c>'s StatSet is the stat kernel. The engine owns the slot arithmetic (stack-first
/// adds, honest overflow, ordered removes, swaps) and none of the meaning: item identity, icons, equip slots,
/// use effects and the catalog stay game-side, and the ONE game fact the arithmetic needs, whether an id
/// stacks, arrives as a predicate at construction.
/// </summary>
/// <remarks>
/// The rules, stated once. An id the predicate calls STACKABLE merges into one slot per container wherever
/// possible: an add tops up the first existing stack before opening a new slot, saturating at
/// <see cref="int.MaxValue"/> rather than overflowing. A non-stackable id occupies one slot per unit. An add
/// reports how many units actually entered, so a full container answers with a remainder instead of throwing
/// or silently dropping. Removes walk slots first to last, which is the visible, predictable order a player
/// expects. Nothing here is thread-safe, exactly like the stat kernel: one owner, one container.
/// </remarks>
public sealed class ItemContainer
{
    readonly ItemStack[] _slots;
    readonly Func<int, bool> _stackable;

    /// <summary>Builds an empty container.</summary>
    /// <param name="slotCount">How many slots, fixed for the container's life. At least one.</param>
    /// <param name="stackable">The game's rule for whether an item id merges into one slot. Consulted per
    /// operation and never cached, so a game whose rule reads its catalog sees catalog edits live.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slotCount"/> is not positive.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="stackable"/> is null.</exception>
    public ItemContainer(int slotCount, Func<int, bool> stackable)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slotCount);
        ArgumentNullException.ThrowIfNull(stackable);
        _slots = new ItemStack[slotCount];
        _stackable = stackable;
    }

    /// <summary>How many slots this container has.</summary>
    public int SlotCount => _slots.Length;

    /// <summary>One slot's contents. Empty slots answer <see cref="ItemStack.Empty"/>.</summary>
    /// <param name="slot">The slot index.</param>
    public ItemStack this[int slot] => _slots[slot];

    /// <summary>How many slots hold nothing.</summary>
    public int FreeSlots
    {
        get
        {
            int free = 0;
            for (int i = 0; i < _slots.Length; i++)
                if (_slots[i].IsEmpty) free++;
            return free;
        }
    }

    /// <summary>Total units of one item id across every slot.</summary>
    /// <param name="itemId">The id counted.</param>
    public int CountOf(int itemId)
    {
        if (itemId == 0) return 0;
        long total = 0;
        for (int i = 0; i < _slots.Length; i++)
            if (_slots[i].ItemId == itemId) total += _slots[i].Count;
        return total > int.MaxValue ? int.MaxValue : (int)total;
    }

    /// <summary>Adds units of an item, stack-first for a stackable id and one slot per unit otherwise.</summary>
    /// <param name="itemId">The id added. Zero adds nothing.</param>
    /// <param name="count">Units to add. Non-positive adds nothing.</param>
    /// <returns>How many units actually entered. Less than <paramref name="count"/> means the container is
    /// full (or a stack saturated), and the difference is the caller's to drop, refuse, or spill, which is a
    /// game rule this kernel deliberately does not have.</returns>
    public int Add(int itemId, int count)
    {
        if (itemId == 0 || count <= 0) return 0;
        int remaining = count;

        if (_stackable(itemId))
        {
            // Existing stacks top up first, and a NEW slot opens only when no stack of this id exists at all:
            // one stack per container, so a saturated stack answers with the remainder instead of spilling a
            // shadow stack into the next slot (the first draft did, and the no-spill test caught it). The
            // top-up is a loop rather than a find, defensively: Add alone never creates a second stack, but
            // SetAt is the codec's door and restores whatever was stored.
            bool sawStack = false;
            for (int i = 0; i < _slots.Length && remaining > 0; i++)
            {
                if (_slots[i].ItemId != itemId) continue;
                sawStack = true;
                int room = int.MaxValue - _slots[i].Count;
                int moved = Math.Min(room, remaining);
                _slots[i] = _slots[i] with { Count = _slots[i].Count + moved };
                remaining -= moved;
            }
            for (int i = 0; i < _slots.Length && remaining > 0 && !sawStack; i++)
            {
                if (!_slots[i].IsEmpty) continue;
                _slots[i] = new ItemStack(itemId, remaining);
                remaining = 0;
            }
            return count - remaining;
        }

        for (int i = 0; i < _slots.Length && remaining > 0; i++)
        {
            if (!_slots[i].IsEmpty) continue;
            _slots[i] = new ItemStack(itemId, 1);
            remaining--;
        }
        return count - remaining;
    }

    /// <summary>Removes units of an item, walking slots first to last.</summary>
    /// <param name="itemId">The id removed. Zero removes nothing.</param>
    /// <param name="count">Units to remove. Non-positive removes nothing.</param>
    /// <returns>How many units actually left. Less than <paramref name="count"/> means the container held
    /// fewer.</returns>
    public int Remove(int itemId, int count)
    {
        if (itemId == 0 || count <= 0) return 0;
        int remaining = count;
        for (int i = 0; i < _slots.Length && remaining > 0; i++)
        {
            if (_slots[i].ItemId != itemId) continue;
            int taken = Math.Min(_slots[i].Count, remaining);
            int left = _slots[i].Count - taken;
            _slots[i] = left == 0 ? ItemStack.Empty : _slots[i] with { Count = left };
            remaining -= taken;
        }
        return count - remaining;
    }

    /// <summary>Removes everything in one slot.</summary>
    /// <param name="slot">The slot index.</param>
    /// <returns>What the slot held, <see cref="ItemStack.Empty"/> for an already empty one.</returns>
    public ItemStack TakeAt(int slot)
    {
        ItemStack taken = _slots[slot];
        _slots[slot] = ItemStack.Empty;
        return taken;
    }

    /// <summary>Swaps two slots' contents outright, which is the click-to-move a player performs. Swapping a
    /// slot with itself, or two empties, is a no-op rather than an error.</summary>
    /// <param name="a">One slot index.</param>
    /// <param name="b">The other.</param>
    public void Swap(int a, int b) => (_slots[a], _slots[b]) = (_slots[b], _slots[a]);

    /// <summary>Writes one slot outright, for the codec and for nothing else: no stacking rule runs, because
    /// the decoder is restoring a state the rules already produced. A non-positive count or a zero id writes
    /// the empty slot, so a malformed entry cannot smuggle a negative count in.</summary>
    /// <param name="slot">The slot index.</param>
    /// <param name="stack">What to put there.</param>
    public void SetAt(int slot, ItemStack stack) =>
        _slots[slot] = stack.IsEmpty ? ItemStack.Empty : stack;

    /// <summary>Empties every slot.</summary>
    public void Clear() => Array.Clear(_slots);
}
