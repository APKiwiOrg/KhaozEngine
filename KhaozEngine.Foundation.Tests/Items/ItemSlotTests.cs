using System;
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

    static ItemContainer Bag(int slots = 5) => new(slots, Stackable, Canonical);

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

        bag.SetSlotAt(1, new ItemSlot(new ItemStack(Sword, 1, 8), Payload(9, 1), Quarantined: true));
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
        // A wrapper is the original bytes verbatim plus eleven of its own, so an entry that quarantined
        // for payload-too-long is by construction larger than the cap it broke, and it is not canonical.
        var wrapper = new byte[ItemSlot.MaxPayloadBytes + 11];
        wrapper[0] = 0xFF;
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
        bag.SetSlotAt(4, new ItemSlot(new ItemStack(Bread, 2, 8), Payload(4, 5), Quarantined: true));

        bag.Swap(0, 4);

        Assert.Equal(new ItemStack(Bread, 2, 8), bag[0]);
        Assert.Equal(Payload(4, 5), bag.SlotAt(0).Payload.ToArray());
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
