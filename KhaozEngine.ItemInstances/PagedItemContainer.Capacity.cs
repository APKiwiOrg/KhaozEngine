using System;
using System.Diagnostics.CodeAnalysis;
using KhaozEngine.Items;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The capacity half, spec 5.7: the gate and the grants it gates. The four rules, restated as the engine
/// behaviour they are.
/// <list type="number">
/// <item>A grant that opens a NEW slot is refused when occupancy is at or above capacity.</item>
/// <item>A grant that merges entirely into existing stacks is allowed at any occupancy.</item>
/// <item>Lowering capacity below occupancy is LEGAL. The container loads intact, is never trimmed, and is
/// refused new slots until occupancy falls below capacity. That is the generalisation of contracts 8.2
/// kind 4's over-cap stack policy: state that already exists is never destroyed to satisfy a number that
/// moved under it, it may only shrink back into range.</item>
/// <item>Capacity is never read from content. It is a per-owner number the game sets, because it is player
/// progression rather than balance data, which is why nothing in this type's surface names a content
/// type.</item>
/// </list>
/// <para>
/// <b>The gate is consulted by <c>Add</c> and by nothing else.</b> Seating a decoded page, writing a slot
/// through the codec door, taking, swapping and removing all ignore it, which is what makes rule 3 true
/// rather than aspirational: an over-capacity container is a container every door but this one still works
/// on.
/// </para>
/// <para>
/// A separate content-free <c>stackCap</c> predicate supplies the current <c>max_stack</c>. It is read once
/// per add and never cached. A positive cap limits a newly opened stack and the merge arithmetic, while zero
/// or no predicate keeps the engine ceiling. Existing over-cap stacks remain legal and refuse growth until
/// removal brings them back within the cap.
/// </para>
/// </summary>
public sealed partial class PagedItemContainer
{
    int _capacity;

    /// <summary>The occupied-slot gate. It is not a size: the address space is
    /// <see cref="SlotSpace"/> and does not move with it.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int Capacity
    {
        get => _capacity;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _capacity = value;
        }
    }

    /// <summary>Whether a grant that opens a new slot would be refused. An over-capacity container answers
    /// true and keeps everything it holds.</summary>
    public bool IsAtCapacity => Occupancy >= _capacity;

    /// <summary>
    /// Adds units of a definition carrying no instance, stack-first for a stackable definition and one slot
    /// per unit otherwise, which is <c>ItemContainer.Add</c>'s arithmetic under the capacity gate.
    /// </summary>
    /// <param name="definitionId">The definition added. Zero adds nothing.</param>
    /// <param name="count">Units to add. Non-positive adds nothing.</param>
    /// <returns>How many units actually entered. Less than <paramref name="count"/> means the gate refused
    /// a new slot, the address space is full, or a stack reached its content cap or the engine ceiling. The
    /// difference is the caller's to drop, refuse or spill, which is a game rule this kernel does not have.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The stack-cap predicate returns a negative value.</exception>
    public int Add(int definitionId, int count)
    {
        if (definitionId == 0 || count <= 0) return 0;

        // A stackable definition is the grant path: one stack per container, topped up wherever it already
        // sits, and a new slot only when no stack of it exists at all.
        if (_stackable(definitionId)) return Add(new ItemSlot(new ItemStack(definitionId, count), default, false));

        int remaining = count;
        while (remaining > 0 && TryOpenSlot(out ItemContainerPage? page, out int slot))
        {
            page.Write(slot, new ItemSlot(new ItemStack(definitionId, 1), default, false));
            remaining--;
        }

        return count - remaining;
    }

    /// <summary>
    /// Adds one entry, payload and instance id included: the grant an owned item arrives through. It merges
    /// into the first entry spec 4.6 says it may merge with, and otherwise opens ONE slot. An anonymous
    /// stackable entry is limited to the current positive stack cap and the remainder stays with the caller.
    /// An identified grant is all-or-nothing because this type cannot mint a second durable identity.
    /// <para>
    /// A grant that finds a mergeable stack never opens a slot as well, even when that stack saturated
    /// before the grant was spent. One stack per container is the rule, and the remainder is the caller's.
    /// </para>
    /// </summary>
    /// <param name="grant">The entry arriving. An empty one adds nothing.</param>
    /// <returns>How many units actually entered.</returns>
    /// <exception cref="ArgumentException">The grant breaks one of spec 4.7's four invariants, which is a
    /// caller bug rather than a full container.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The stack-cap predicate returns a negative value.</exception>
    public int Add(in ItemSlot grant)
    {
        if (grant.IsEmpty) return 0;

        int remaining = grant.Stack.Count;
        int cap = _stackCap?.Invoke(grant.Stack.ItemId) ?? 0;
        ArgumentOutOfRangeException.ThrowIfNegative(cap);
        bool sawStack = false;
        foreach (ItemContainerPage page in _pages)
        {
            for (int slot = page.FirstSlot; slot < page.FirstSlot + page.SlotCount; slot++)
            {
                if (remaining == 0) break;

                ItemSlot existing = page.SlotAt(slot);
                ItemSlot arriving = grant with { Stack = grant.Stack with { Count = remaining } };
                if (!InstanceStacking.CanMerge(existing, arriving, _stackable)) continue;

                ItemSlot merged = InstanceStacking.Merge(existing, arriving, cap, out int afterMerge);
                if (grant.Stack.HasInstance && afterMerge != 0) return 0;
                sawStack = true;
                page.Write(slot, merged);
                remaining = afterMerge;
            }

            if (remaining == 0) break;
        }

        if (remaining > 0 && !sawStack && TryOpenSlot(out ItemContainerPage? opened, out int free))
        {
            int entering = cap > 0 && InstanceStacking.CanMerge(grant, grant, _stackable)
                ? Math.Min(remaining, cap)
                : remaining;
            if (grant.Stack.HasInstance && entering != remaining) return 0;
            opened.Write(free, grant with { Stack = grant.Stack with { Count = entering } });
            remaining -= entering;
        }

        return grant.Stack.Count - remaining;
    }

    /// <summary>
    /// Finds the first free slot a grant may open, which is rule 1: the gate is asked BEFORE the address
    /// space is searched, so a container with a thousand free slots and no capacity left answers false.
    /// </summary>
    bool TryOpenSlot([NotNullWhen(true)] out ItemContainerPage? page, out int containerSlot)
    {
        page = null;
        containerSlot = -1;
        if (IsAtCapacity) return false;

        foreach (ItemContainerPage candidate in _pages)
        {
            if (candidate.EntryCount == candidate.SlotCount) continue;

            for (int slot = candidate.FirstSlot; slot < candidate.FirstSlot + candidate.SlotCount; slot++)
            {
                if (!candidate.SlotAt(slot).IsEmpty) continue;

                page = candidate;
                containerSlot = slot;
                return true;
            }
        }

        return false;
    }
}
