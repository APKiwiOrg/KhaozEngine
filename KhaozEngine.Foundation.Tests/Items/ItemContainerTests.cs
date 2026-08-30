using System;
using KhaozEngine.Items;
using Xunit;

namespace KhaozEngine.Tests.Foundation.Items;

/// <summary>The container kernel's rules, stated as tests: stack-first adds, honest overflow, ordered
/// removes, and the codec door's sanitising.</summary>
public class ItemContainerTests
{
    const int Coins = 1;      // stackable
    const int Sword = 2;      // not
    const int Bread = 3;      // not

    static bool Stackable(int id) => id == Coins;

    static ItemContainer Bag(int slots = 5) => new(slots, Stackable);

    [Fact]
    public void A_stackable_id_merges_into_one_slot_and_saturates_honestly()
    {
        ItemContainer bag = Bag();
        Assert.Equal(25, bag.Add(Coins, 25));
        Assert.Equal(10, bag.Add(Coins, 10));
        // One stack, not two: the second add topped up the first.
        Assert.Equal(new ItemStack(Coins, 35), bag[0]);
        Assert.True(bag[1].IsEmpty);
        Assert.Equal(35, bag.CountOf(Coins));

        // Saturation at the ceiling reports the remainder rather than wrapping or opening a shadow stack.
        bag.SetAt(0, new ItemStack(Coins, int.MaxValue - 3));
        Assert.Equal(3, bag.Add(Coins, 10));
        Assert.Equal(int.MaxValue, bag[0].Count);
        Assert.True(bag[1].IsEmpty, "a saturated stack must not spill into a second slot");
    }

    [Fact]
    public void A_non_stackable_id_takes_one_slot_per_unit_and_overflow_is_the_remainder()
    {
        ItemContainer bag = Bag(slots: 3);
        Assert.Equal(2, bag.Add(Sword, 2));
        Assert.Equal(1, bag.Add(Bread, 1));
        Assert.Equal(0, bag.FreeSlots);
        // Full: the fourth unit does not enter, and the answer says so instead of throwing.
        Assert.Equal(0, bag.Add(Bread, 1));
        Assert.Equal(2, bag.CountOf(Sword));
        Assert.Equal(new ItemStack(Sword, 1), bag[0]);
        Assert.Equal(new ItemStack(Sword, 1), bag[1]);
    }

    [Fact]
    public void Removes_walk_first_to_last_and_swaps_move_anything()
    {
        ItemContainer bag = Bag();
        bag.Add(Sword, 3);
        Assert.Equal(2, bag.Remove(Sword, 2));
        // The first two slots emptied, the third kept: the visible order a player expects.
        Assert.True(bag[0].IsEmpty);
        Assert.True(bag[1].IsEmpty);
        Assert.Equal(new ItemStack(Sword, 1), bag[2]);
        // Removing more than is held answers what actually left.
        Assert.Equal(1, bag.Remove(Sword, 5));
        Assert.Equal(0, bag.CountOf(Sword));

        bag.Add(Coins, 7);
        bag.Swap(0, 4);
        Assert.True(bag[0].IsEmpty);
        Assert.Equal(new ItemStack(Coins, 7), bag[4]);
        ItemStack taken = bag.TakeAt(4);
        Assert.Equal(new ItemStack(Coins, 7), taken);
        Assert.True(bag[4].IsEmpty);

        // Degenerate inputs are no-ops with honest answers, not throws.
        Assert.Equal(0, bag.Add(0, 5));
        Assert.Equal(0, bag.Add(Coins, 0));
        Assert.Equal(0, bag.Remove(0, 5));
        Assert.Equal(0, bag.CountOf(0));
    }

    [Fact]
    public void The_codec_round_trips_sparsely_and_validates_what_it_refuses()
    {
        ItemContainer bag = Bag(slots: 28);
        bag.Add(Coins, 25);
        bag.Add(Sword, 1);
        bag.Swap(1, 27);                        // an occupied high slot, so sparseness is exercised

        byte[] blob = ItemContainerCodec.Encode(bag);
        // Header plus two entries: the 26 empty slots cost nothing.
        Assert.Equal(3 + (2 * 10), blob.Length);

        Assert.Null(ItemContainerCodec.Validate(blob, 28));
        Assert.True(ItemContainerCodec.TryDecode(blob, 28, Stackable, out ItemContainer back));
        Assert.Equal(new ItemStack(Coins, 25), back[0]);
        Assert.Equal(new ItemStack(Sword, 1), back[27]);
        Assert.Equal(26, back.FreeSlots);

        // The refusals a persistence layer quarantines on, each named.
        Assert.NotNull(ItemContainerCodec.Validate(blob, 20));            // geometry mismatch
        byte[] wrongVersion = (byte[])blob.Clone();
        wrongVersion[0] = 9;
        Assert.NotNull(ItemContainerCodec.Validate(wrongVersion, 28));
        byte[] badCount = (byte[])blob.Clone();
        badCount[9] = 0xFF; badCount[10] = 0xFF; badCount[11] = 0xFF; badCount[12] = 0xFF;   // count -1
        Assert.NotNull(ItemContainerCodec.Validate(badCount, 28));
        // No state is not a fault.
        Assert.Null(ItemContainerCodec.Validate(null, 28));
        Assert.False(ItemContainerCodec.TryDecode(null, 28, Stackable, out _));
    }

    [Fact]
    public void The_codec_refuses_disorder_and_the_set_door_sanitises()
    {
        ItemContainer bag = Bag(slots: 4);
        bag.Add(Coins, 5);
        bag.Add(Sword, 1);
        byte[] blob = ItemContainerCodec.Encode(bag);

        // Entries are ascending by construction: swap the two entries' slot bytes and the blob is refused,
        // which is what makes a duplicate slot impossible without a second pass.
        byte[] disordered = (byte[])blob.Clone();
        (disordered[3], disordered[13]) = (disordered[13], disordered[3]);
        Assert.NotNull(ItemContainerCodec.Validate(disordered, 4));

        // SetAt is the codec's door and it sanitises rather than trusting: a non-positive count or the empty
        // id writes the empty slot.
        bag.SetAt(2, new ItemStack(Bread, -3));
        Assert.True(bag[2].IsEmpty);
        bag.SetAt(2, new ItemStack(0, 5));
        Assert.True(bag[2].IsEmpty);
    }
}
