using System;

namespace KhaozEngine.Items;

/// <summary>The payload-carrying half of the container: the parallel arrays an instance rides in, and the
/// two doors a codec uses to seat and lift a whole slot.</summary>
public sealed partial class ItemContainer
{
    // The payload lives in a PARALLEL array rather than on ItemStack, because a byte[] field inside a
    // readonly record struct compares by reference, which would make two stacks holding the same bytes
    // compare unequal and would silently break CountOf, the stacking rule and every existing test. A plain
    // stack allocates nothing here: a null entry IS the empty payload.
    readonly byte[]?[] _payloads;
    readonly bool[] _quarantined;
    readonly Func<ReadOnlyMemory<byte>, bool>? _payloadCanonical;

    /// <summary>One slot's whole state: the stack, its payload and its quarantine flag. Empty slots answer
    /// <see cref="ItemSlot.Empty"/>. The payload is a window over bytes this container owns, so reading one
    /// allocates nothing and a caller holding one cannot change what the container holds.</summary>
    /// <param name="slot">The slot index.</param>
    public ItemSlot SlotAt(int slot) => new(_slots[slot], _payloads[slot], _quarantined[slot]);

    /// <summary>Writes one slot outright, payload included: the codec's payload-carrying door, and the
    /// sibling of <see cref="SetAt"/>. It sanitises in the same spirit and then enforces the invariants
    /// below, throwing for each, because the only caller is a decoder that has already validated and a
    /// violation here is a caller bug rather than bad data.</summary>
    /// <param name="slot">The slot index.</param>
    /// <param name="value">The whole slot to seat. Its payload bytes are COPIED, so the caller may reuse
    /// its buffer.</param>
    /// <exception cref="ArgumentException">The payload is over <see cref="ItemSlot.MaxPayloadBytes"/>, or a
    /// non-empty payload arrives with no instance id, or the payload is not canonical.</exception>
    /// <remarks>
    /// The invariants, in the order they are checked. One, an empty stack writes <see cref="ItemSlot.Empty"/>
    /// and clears everything. Two, a payload is at most <see cref="ItemSlot.MaxPayloadBytes"/>. Three, a
    /// non-empty payload needs a non-zero instance id, while an empty payload WITH one is allowed, because a
    /// definition may declare durability and have it at full with nothing else set. Four, a payload that is
    /// not canonical is refused, through the predicate handed to the constructor, on every call rather than
    /// under a Debug.Assert, because a door that only guards on a developer machine is not a door.
    /// Invariants two and four are SKIPPED when the slot is quarantined, because a wrapper is not a payload:
    /// it is not canonical, it is not meant to be, and it may be larger than the cap because the thing it
    /// preserves was. Invariants one and three still bind whatever the flag says.
    /// </remarks>
    public void SetSlotAt(int slot, ItemSlot value)
    {
        if (value.Stack.IsEmpty)
        {
            _slots[slot] = ItemStack.Empty;
            ClearPayload(slot);
            return;
        }

        ReadOnlySpan<byte> payload = value.Payload.Span;
        if (!payload.IsEmpty && !value.Stack.HasInstance)
            throw new ArgumentException(
                $"slot {slot} carries a {payload.Length} byte payload with instance id 0", nameof(value));

        if (value.Quarantined)
        {
            // TODO(task 6): QuarantineWrapper.Verify stands in for invariants 2 and 4 here, reading the four
            // magic bytes, the version and the declared original length and refusing anything else. Until it
            // ships, a quarantined slot's bytes are taken unchecked, which is the safe direction: the
            // alternative is a door that refuses the wrapper at exactly the moment quarantine is needed.
        }
        else
        {
            if (payload.Length > ItemSlot.MaxPayloadBytes)
                throw new ArgumentException(
                    $"slot {slot} carries {payload.Length} payload bytes over the {ItemSlot.MaxPayloadBytes} byte cap",
                    nameof(value));
            if (!payload.IsEmpty && (_payloadCanonical is null || !_payloadCanonical(value.Payload)))
                throw new ArgumentException(
                    $"slot {slot} carries a payload this container cannot vouch is canonical", nameof(value));
        }

        _slots[slot] = value.Stack;
        _payloads[slot] = payload.IsEmpty ? null : payload.ToArray();
        _quarantined[slot] = value.Quarantined;
    }

    /// <summary>Removes everything in one slot and answers the WHOLE slot: <see cref="TakeAt"/>'s
    /// payload-carrying sibling.</summary>
    /// <param name="slot">The slot index.</param>
    /// <returns>What the slot held, <see cref="ItemSlot.Empty"/> for an already empty one.</returns>
    public ItemSlot TakeSlotAt(int slot)
    {
        ItemSlot taken = SlotAt(slot);
        _slots[slot] = ItemStack.Empty;
        ClearPayload(slot);
        return taken;
    }

    /// <summary>Drops one slot's payload and quarantine flag, which every door that empties or overwrites a
    /// slot owes the next reader.</summary>
    void ClearPayload(int slot)
    {
        _payloads[slot] = null;
        _quarantined[slot] = false;
    }
}
