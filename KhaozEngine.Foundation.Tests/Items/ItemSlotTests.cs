using System;
using System.Collections.Generic;
using KhaozEngine.Items;
using Xunit;

namespace KhaozEngine.Tests.Foundation.Items;

/// <summary>The payload-carrying half of the container kernel, which is spec 4.3 and 4.7 of
/// ITEM-INSTANCES-DESIGN-2026-09-15: a slot is a stack plus opaque bytes plus a quarantine flag, the
/// plain doors clear the bytes, and the payload door refuses the four things a caller must not do.</summary>
public class ItemSlotTests
{
    const int Coins = 1;      // stackable
    const int Sword = 2;      // not
    const int Bread = 3;      // not

    static bool Stackable(int id) => id == Coins;

    /// <summary>Stands in for <c>ItemInstancePayload.IsCanonical</c> until that ships: the real rule is
    /// fields strictly ascending by kind id with minimal varints, and non-decreasing bytes is the same
    /// SHAPE of check, one forward pass with no decode.</summary>
    static bool Canonical(ReadOnlyMemory<byte> payload)
    {
        ReadOnlySpan<byte> span = payload.Span;
        for (int i = 1; i < span.Length; i++)
            if (span[i] < span[i - 1]) return false;
        return true;
    }

    /// <summary>Stands in for <c>QuarantineWrapper.Verify</c> the way <see cref="Canonical"/> stands in for
    /// the payload decoder. The real check is spec 12.4's KECQ header and it lives in
    /// KhaozEngine.ItemInstances, which this project deliberately does not reference (push CI selects test
    /// projects by the reference graph) and which KhaozEngine.Items sits BELOW, so the door takes it as a
    /// predicate. The format is pinned for real in KhaozEngine.ItemInstances.Tests.</summary>
    static bool WellFormedWrapper(ReadOnlyMemory<byte> wrapper)
    {
        ReadOnlySpan<byte> span = wrapper.Span;
        if (span.Length < 9) return false;
        if (span[0] != (byte)'K' || span[1] != (byte)'E' || span[2] != (byte)'C' || span[3] != (byte)'Q') return false;
        if (span[4] != 1 || span[5] != 0) return false;
        if (span[6] == 0 || span[6] > 13) return false;

        int offset = 7;
        return TryReadVarint(span, ref offset, out _)
            && TryReadVarint(span, ref offset, out int length)
            && length == span.Length - offset;
    }

    /// <summary>A KECQ wrapper over <paramref name="originalLength"/> bytes of filler, built by hand because
    /// the writer is in the package above this one.</summary>
    static byte[] Wrapper(byte reasonOrdinal, int stampedVersion, int originalLength)
    {
        var bytes = new List<byte> { (byte)'K', (byte)'E', (byte)'C', (byte)'Q', 1, 0, reasonOrdinal };
        WriteVarint(bytes, stampedVersion);
        WriteVarint(bytes, originalLength);
        for (int i = 0; i < originalLength; i++) bytes.Add((byte)(i & 0xFF));
        return bytes.ToArray();
    }

    // Unsigned LEB128, the same definition ContentVarint holds, written out here only because this project
    // has no reference that reaches it and a stand-in that cannot read a length cannot stand in for Verify.
    static void WriteVarint(List<byte> into, int value)
    {
        uint remaining = (uint)value;
        while (remaining >= 0x80) { into.Add((byte)(remaining | 0x80)); remaining >>= 7; }
        into.Add((byte)remaining);
    }

    static bool TryReadVarint(ReadOnlySpan<byte> span, ref int offset, out int value)
    {
        value = 0;
        int shift = 0;
        while (true)
        {
            if (offset >= span.Length || shift > 28) return false;
            byte b = span[offset++];
            value |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
        }
    }

    static ItemContainer Bag(int slots = 5) => new(slots, Stackable, Canonical, WellFormedWrapper);

    static byte[] Payload(params byte[] bytes) => bytes;

    [Fact]
    public void A_plain_stack_still_has_instance_id_zero_and_an_empty_payload()
    {
        ItemContainer bag = Bag();
        Assert.Equal(5, bag.Add(Coins, 5));

        Assert.Equal(new ItemStack(Coins, 5), bag[0]);
        Assert.Equal(0, bag[0].InstanceId);
        Assert.False(bag[0].HasInstance);

        ItemSlot slot = bag.SlotAt(0);
        Assert.Equal(new ItemStack(Coins, 5), slot.Stack);
        Assert.True(slot.Payload.IsEmpty);
        Assert.False(slot.Quarantined);

        // A cleared slot and a never-filled one are still the same value, which is what keeps every
        // existing call site honest.
        Assert.True(ItemSlot.Empty.IsEmpty);
        Assert.Equal(ItemStack.Empty, ItemSlot.Empty.Stack);
        Assert.Equal(ItemSlot.Empty, bag.SlotAt(1));
    }

    [Fact]
    public void SetAt_clears_a_slots_payload_and_quarantine_flag()
    {
        ItemContainer bag = Bag();
        bag.SetSlotAt(0, new ItemSlot(new ItemStack(Sword, 1, 7), Payload(1, 2, 3), Quarantined: false));
        Assert.Equal(3, bag.SlotAt(0).Payload.Length);

        // A version 1 blob decode goes through this door, so writing a plain stack over an instance must
        // not leave the old bytes behind.
        bag.SetAt(0, new ItemStack(Sword, 1));
        Assert.True(bag.SlotAt(0).Payload.IsEmpty);
        Assert.False(bag.SlotAt(0).Quarantined);
        Assert.Equal(0, bag[0].InstanceId);

        bag.SetSlotAt(1, new ItemSlot(new ItemStack(Sword, 1, 8), Wrapper(2, 4, 2), Quarantined: true));
        Assert.True(bag.SlotAt(1).Quarantined);
        bag.SetAt(1, new ItemStack(Sword, 1));
        Assert.False(bag.SlotAt(1).Quarantined);
        Assert.True(bag.SlotAt(1).Payload.IsEmpty);
    }

    [Fact]
    public void SetSlotAt_refuses_a_payload_above_the_cap()
    {
        ItemContainer bag = Bag();
        Assert.Equal(512, ItemSlot.MaxPayloadBytes);

        var overCap = new byte[ItemSlot.MaxPayloadBytes + 1];
        Assert.Throws<ArgumentException>(() =>
            bag.SetSlotAt(0, new ItemSlot(new ItemStack(Sword, 1, 7), overCap, Quarantined: false)));
        Assert.True(bag.SlotAt(0).IsEmpty);

        // Exactly at the cap is legal: the deepest item the v1 field kinds express reaches it by
        // construction (contracts 9.6).
        var atCap = new byte[ItemSlot.MaxPayloadBytes];
        bag.SetSlotAt(0, new ItemSlot(new ItemStack(Sword, 1, 7), atCap, Quarantined: false));
        Assert.Equal(ItemSlot.MaxPayloadBytes, bag.SlotAt(0).Payload.Length);
    }

    [Fact]
    public void SetSlotAt_refuses_a_non_empty_payload_with_a_zero_instance_id()
    {
        ItemContainer bag = Bag();
        Assert.Throws<ArgumentException>(() =>
            bag.SetSlotAt(0, new ItemSlot(new ItemStack(Sword, 1), Payload(1, 2), Quarantined: false)));
        Assert.True(bag.SlotAt(0).IsEmpty);
    }

    [Fact]
    public void SetSlotAt_allows_an_empty_payload_with_a_non_zero_instance_id()
    {
        ItemContainer bag = Bag();
        // A definition may declare durability and have it at full with nothing else set.
        bag.SetSlotAt(0, new ItemSlot(new ItemStack(Sword, 1, 9), default, Quarantined: false));
        Assert.Equal(9, bag[0].InstanceId);
        Assert.True(bag[0].HasInstance);
        Assert.True(bag.SlotAt(0).Payload.IsEmpty);
    }

    [Fact]
    public void SetSlotAt_refuses_a_non_canonical_payload()
    {
        ItemContainer bag = Bag();
        Assert.Throws<ArgumentException>(() =>
            bag.SetSlotAt(0, new ItemSlot(new ItemStack(Sword, 1, 7), Payload(2, 1), Quarantined: false)));
        Assert.True(bag.SlotAt(0).IsEmpty);

        // A container built with no canonical check carries no payload at all, so the door is never
        // half open.
        var noCheck = new ItemContainer(5, Stackable);
        Assert.Throws<ArgumentException>(() =>
            noCheck.SetSlotAt(0, new ItemSlot(new ItemStack(Sword, 1, 7), Payload(1, 2), Quarantined: false)));
        Assert.True(noCheck.SlotAt(0).IsEmpty);
    }

    [Fact]
    public void SetSlotAt_skips_the_cap_and_canonical_checks_when_quarantined()
    {
        ItemContainer bag = Bag();
        // A wrapper is the original bytes verbatim plus its own header, so an entry that quarantined for
        // payload-too-long is by construction larger than the cap it broke, and it is not canonical.
        byte[] wrapper = Wrapper(1, 7, ItemSlot.MaxPayloadBytes + 1);
        Assert.True(wrapper.Length > ItemSlot.MaxPayloadBytes);
        Assert.False(Canonical(wrapper));
        bag.SetSlotAt(0, new ItemSlot(new ItemStack(Sword, 1, 7), wrapper, Quarantined: true));
        Assert.True(bag.SlotAt(0).Quarantined);
        Assert.Equal(wrapper.Length, bag.SlotAt(0).Payload.Length);

        // Invariants 1 and 3 still bind whatever the flag says.
        Assert.Throws<ArgumentException>(() =>
            bag.SetSlotAt(1, new ItemSlot(new ItemStack(Sword, 1), wrapper, Quarantined: true)));
        bag.SetSlotAt(2, new ItemSlot(ItemStack.Empty, wrapper, Quarantined: true));
        Assert.Equal(ItemSlot.Empty, bag.SlotAt(2));
    }

    [Fact]
    public void SetSlotAt_refuses_quarantined_bytes_that_are_not_a_wrapper()
    {
        // The quarantined branch skips the cap and the canonical check and is NOT unchecked: the wrapper's
        // own header stands in for both (spec 4.7), so a bare payload carrying the flag is refused even
        // though the same bytes would be accepted without it.
        ItemContainer bag = Bag();
        Assert.Throws<ArgumentException>(() =>
            bag.SetSlotAt(0, new ItemSlot(new ItemStack(Sword, 1, 7), Payload(1, 2, 3), Quarantined: true)));
        Assert.True(bag.SlotAt(0).IsEmpty);
        bag.SetSlotAt(0, new ItemSlot(new ItemStack(Sword, 1, 7), Payload(1, 2, 3), Quarantined: false));
        Assert.False(bag.SlotAt(0).Quarantined);

        // A wrapper whose declared original length lies is refused for the same reason a truncated one is.
        byte[] lying = Wrapper(1, 7, 4);
        lying[^5] = 9;
        Assert.Throws<ArgumentException>(() =>
            bag.SetSlotAt(1, new ItemSlot(new ItemStack(Sword, 1, 8), lying, Quarantined: true)));

        // A container built with no wrapper check carries no quarantined payload at all, so that door is
        // never half open either.
        var noCheck = new ItemContainer(5, Stackable, Canonical);
        Assert.Throws<ArgumentException>(() =>
            noCheck.SetSlotAt(0, new ItemSlot(new ItemStack(Sword, 1, 7), Wrapper(1, 7, 3), Quarantined: true)));
        Assert.True(noCheck.SlotAt(0).IsEmpty);
    }

    [Fact]
    public void SetSlotAt_refuses_a_quarantined_slot_carrying_no_payload_at_all()
    {
        // The quarantined branch used to short circuit on an empty payload, so a slot could be flagged
        // quarantined and hold nothing. A wrapper is at least nine bytes (its magic, its version, its
        // reason and two varints), so no legal wrapper is empty: an empty quarantined slot is a slot
        // claiming to preserve bytes it does not hold, and the flag would ride on into every reader.
        ItemContainer bag = Bag();
        Assert.Throws<ArgumentException>(() =>
            bag.SetSlotAt(0, new ItemSlot(new ItemStack(Sword, 1, 7), default, Quarantined: true)));
        Assert.True(bag.SlotAt(0).IsEmpty);

        // The same empty payload with the flag CLEAR still seats, because a definition may declare
        // durability and have it at full with nothing else set.
        bag.SetSlotAt(0, new ItemSlot(new ItemStack(Sword, 1, 7), default, Quarantined: false));
        Assert.False(bag.SlotAt(0).Quarantined);
        Assert.True(bag.SlotAt(0).Payload.IsEmpty);
    }

    [Fact]
    public void SetSlotAt_accepts_a_quarantined_slot_carrying_a_well_formed_wrapper()
    {
        // The path quarantine exists for: the bytes that failed are seated verbatim, flag set, and nothing
        // on the way in reshapes them.
        ItemContainer bag = Bag();
        byte[] wrapper = Wrapper(8, 41, 6);
        bag.SetSlotAt(0, new ItemSlot(new ItemStack(Sword, 1, 7), wrapper, Quarantined: true));

        Assert.True(bag.SlotAt(0).Quarantined);
        Assert.Equal(wrapper, bag.SlotAt(0).Payload.ToArray());
        Assert.Equal(wrapper, bag.TakeSlotAt(0).Payload.ToArray());
        Assert.Equal(ItemSlot.Empty, bag.SlotAt(0));
    }

    [Fact]
    public void TakeSlotAt_returns_the_whole_slot_and_seats_ItemSlot_Empty()
    {
        ItemContainer bag = Bag();
        bag.SetSlotAt(3, new ItemSlot(new ItemStack(Sword, 1, 7), Payload(1, 2, 3), Quarantined: false));

        ItemSlot taken = bag.TakeSlotAt(3);
        Assert.Equal(new ItemStack(Sword, 1, 7), taken.Stack);
        Assert.Equal(Payload(1, 2, 3), taken.Payload.ToArray());
        Assert.False(taken.Quarantined);

        Assert.Equal(ItemSlot.Empty, bag.SlotAt(3));
        Assert.True(bag[3].IsEmpty);
        // The plain take keeps answering the identity it always did, and loses only the payload.
        bag.SetSlotAt(3, new ItemSlot(new ItemStack(Sword, 1, 7), Payload(1, 2, 3), Quarantined: false));
        Assert.Equal(new ItemStack(Sword, 1, 7), bag.TakeAt(3));
        Assert.Equal(ItemSlot.Empty, bag.SlotAt(3));
    }

    [Fact]
    public void Swap_moves_the_payload_with_the_stack()
    {
        ItemContainer bag = Bag();
        bag.SetSlotAt(0, new ItemSlot(new ItemStack(Sword, 1, 7), Payload(1, 2, 3), Quarantined: false));
        byte[] wrapper = Wrapper(3, 2, 2);
        bag.SetSlotAt(4, new ItemSlot(new ItemStack(Bread, 2, 8), wrapper, Quarantined: true));

        bag.Swap(0, 4);

        Assert.Equal(new ItemStack(Bread, 2, 8), bag[0]);
        Assert.Equal(wrapper, bag.SlotAt(0).Payload.ToArray());
        Assert.True(bag.SlotAt(0).Quarantined);
        Assert.Equal(new ItemStack(Sword, 1, 7), bag[4]);
        Assert.Equal(Payload(1, 2, 3), bag.SlotAt(4).Payload.ToArray());
        Assert.False(bag.SlotAt(4).Quarantined);
    }

    [Fact]
    public void Two_slots_with_equal_payload_bytes_compare_equal_through_ItemSlot()
    {
        // Two DIFFERENT arrays holding the same bytes. Reference equality here would silently break the
        // stacking rule, which is a byte compare over a canonical payload (spec 4.6).
        var left = new ItemSlot(new ItemStack(Sword, 1, 7), Payload(1, 2, 3), Quarantined: false);
        var right = new ItemSlot(new ItemStack(Sword, 1, 7), Payload(1, 2, 3), Quarantined: false);
        Assert.Equal(left, right);
        Assert.True(left == right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());

        var differs = new ItemSlot(new ItemStack(Sword, 1, 7), Payload(1, 2, 4), Quarantined: false);
        Assert.NotEqual(left, differs);

        ItemContainer bag = Bag();
        bag.SetSlotAt(0, left);
        bag.SetSlotAt(1, right);
        Assert.Equal(bag.SlotAt(0), bag.SlotAt(1));
        Assert.NotEqual(bag.SlotAt(0), bag.SlotAt(2));
    }
}
