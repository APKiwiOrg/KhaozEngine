using System;
using KhaozEngine.ItemInstances;
using KhaozEngine.Items;
using Xunit;

namespace KhaozEngine.Tests.ItemInstances.Pages;

/// <summary>
/// Spec 4.6, the stacking rule as a byte compare. Rule 4 is the whole of "their properties are identical",
/// and it is a memcmp rather than a structural comparison ONLY because the payload is canonical, so a
/// decode inside <c>CanMerge</c> would be a defect rather than an optimisation.
/// <para>
/// The two belt-and-braces facts are here for the same reason the code carries them: a definition can gain
/// durability or sockets AFTER its items exist, so an entry carrying kind 5 or kind 132 never merges
/// whatever the game's predicate says about the definition today.
/// </para>
/// </summary>
public class InstanceStackingTests
{
    const int Sword = 4200;
    const int Potion = 995;

    /// <summary>A game kind (spec 3.3's 1024 and above) neither side of a merge understands, which is what
    /// contracts 9.4 keeps verbatim and what rule 4 therefore refuses to merge across.</summary>
    const ushort UnknownGameKind = 2000;

    static ItemSlot Slot(
        int definitionId, int count, long instanceId = 0, byte[]? payload = null, bool quarantined = false) =>
        new(new ItemStack(definitionId, count, instanceId), payload ?? Array.Empty<byte>(), quarantined);

    static byte[] Level(ulong itemLevel) =>
        new ItemInstancePayloadBuilder().AddScalar(InstancePropertyKind.ItemLevel, itemLevel).ToArray();

    static byte[] Durability(ulong current, ulong max) =>
        new ItemInstancePayloadBuilder().AddScalars(InstancePropertyKind.Durability, current, max).ToArray();

    static byte[] Socketed(int containedDefinitionId) =>
        new ItemInstancePayloadBuilder()
            .AddSockets([new InstanceSocket(0, containedDefinitionId, 0, default)])
            .ToArray();

    static byte[] LevelPlusUnknown(ulong itemLevel) =>
        new ItemInstancePayloadBuilder()
            .AddScalar(InstancePropertyKind.ItemLevel, itemLevel)
            .Add(UnknownGameKind, [0x07])
            .ToArray();

    static bool Stacks(int definitionId) => definitionId != 0;

    static bool NeverStacks(int definitionId)
    {
        _ = definitionId;
        return false;
    }

    [Fact]
    public void Two_entries_merge_only_when_all_four_of_4_6_hold()
    {
        ItemSlot left = Slot(Potion, 3, 11, Level(68));
        ItemSlot right = Slot(Potion, 5, 12, Level(68));
        Assert.True(InstanceStacking.CanMerge(left, right, Stacks));

        // Rule 1, the definition.
        Assert.False(InstanceStacking.CanMerge(left, Slot(Sword, 5, 12, Level(68)), Stacks));

        // Rule 2, the game's predicate.
        Assert.False(InstanceStacking.CanMerge(left, right, NeverStacks));

        // Rule 3, the quarantine flag, on either side.
        Assert.False(InstanceStacking.CanMerge(left with { Quarantined = true }, right, Stacks));
        Assert.False(InstanceStacking.CanMerge(left, right with { Quarantined = true }, Stacks));

        // Rule 4, the payload bytes.
        Assert.False(InstanceStacking.CanMerge(left, Slot(Potion, 5, 12, Level(69)), Stacks));

        // An empty slot is not an entry at all, so there is nothing to merge with.
        Assert.False(InstanceStacking.CanMerge(ItemSlot.Empty, right, Stacks));
        Assert.False(InstanceStacking.CanMerge(left, ItemSlot.Empty, Stacks));
    }

    [Fact]
    public void Equal_payload_bytes_merge_and_one_differing_byte_does_not()
    {
        byte[] one = Level(68);
        byte[] copy = Level(68);
        Assert.NotSame(one, copy);
        Assert.True(InstanceStacking.CanMerge(Slot(Potion, 1, 11, one), Slot(Potion, 1, 12, copy), Stacks));

        byte[] differing = Level(68);
        differing[^1] ^= 0x01;
        Assert.False(InstanceStacking.CanMerge(Slot(Potion, 1, 11, one), Slot(Potion, 1, 12, differing), Stacks));

        // Two plain stacks carrying no payload at all are byte equal too, which is what makes the ordinary
        // no-instance merge fall out of the same rule rather than needing its own.
        Assert.True(InstanceStacking.CanMerge(Slot(Potion, 1), Slot(Potion, 1), Stacks));
    }

    [Fact]
    public void An_entry_carrying_kind_5_or_132_never_merges_whatever_the_predicate_says()
    {
        // Byte identical on both sides, so rules 1 to 4 all hold and only the runtime belt-and-braces check
        // of 4.6 can refuse these. Publish enforces the same thing, and a definition that GAINS durability
        // after its items exist is the case publish cannot reach.
        ItemSlot durable = Slot(Sword, 1, 11, Durability(40, 60));
        Assert.False(InstanceStacking.CanMerge(durable, Slot(Sword, 1, 12, Durability(40, 60)), Stacks));

        ItemSlot socketed = Slot(Sword, 1, 11, Socketed(900));
        Assert.False(InstanceStacking.CanMerge(socketed, Slot(Sword, 1, 12, Socketed(900)), Stacks));

        // One side alone is enough, in either position.
        Assert.False(InstanceStacking.CanMerge(durable, Slot(Sword, 1, 12, Level(68)), Stacks));
        Assert.False(InstanceStacking.CanMerge(Slot(Sword, 1, 12, Level(68)), durable, Stacks));
    }

    [Fact]
    public void A_quarantined_entry_never_merges()
    {
        byte[] wrapper = QuarantineWrapper.Wrap(InstanceQuarantineReason.UnknownDefinition, 7, Level(68));
        ItemSlot quarantined = Slot(Potion, 1, 11, wrapper, quarantined: true);

        Assert.False(InstanceStacking.CanMerge(quarantined, Slot(Potion, 1, 12, Level(68)), Stacks));
        Assert.False(InstanceStacking.CanMerge(Slot(Potion, 1, 12, Level(68)), quarantined, Stacks));

        // Not even with itself, byte for byte, which is the point: a wrapper preserves bytes nobody has
        // read, so two of them being identical says nothing about the items inside them.
        Assert.False(InstanceStacking.CanMerge(quarantined, quarantined, Stacks));
    }

    [Fact]
    public void The_predicate_is_consulted_per_operation_and_never_cached()
    {
        int calls = 0;
        bool answer = true;
        bool Counting(int definitionId)
        {
            _ = definitionId;
            calls++;
            return answer;
        }

        ItemSlot left = Slot(Potion, 1, 11, Level(68));
        ItemSlot right = Slot(Potion, 1, 12, Level(68));

        Assert.True(InstanceStacking.CanMerge(left, right, Counting));
        Assert.Equal(1, calls);

        // The game edited its catalog between the two operations, and the second sees the edit.
        answer = false;
        Assert.False(InstanceStacking.CanMerge(left, right, Counting));
        Assert.Equal(2, calls);

        answer = true;
        Assert.True(InstanceStacking.CanMerge(left, right, Counting));
        Assert.Equal(3, calls);
    }

    [Fact]
    public void A_merge_keeps_the_numerically_LOWER_instance_id_so_a_replay_in_either_order_agrees()
    {
        ItemSlot low = Slot(Potion, 3, 11, Level(68));
        ItemSlot high = Slot(Potion, 5, 4_000_000_000, Level(68));

        ItemSlot forward = InstanceStacking.Merge(low, high, out int forwardRemainder);
        ItemSlot backward = InstanceStacking.Merge(high, low, out int backwardRemainder);

        Assert.Equal(11, forward.Stack.InstanceId);
        Assert.Equal(11, backward.Stack.InstanceId);
        Assert.Equal(8, forward.Stack.Count);
        Assert.Equal(8, backward.Stack.Count);
        Assert.Equal(0, forwardRemainder);
        Assert.Equal(0, backwardRemainder);
        Assert.Equal(forward, backward);

        // Zero is the ABSENCE of an instance rather than a low id, so a plain stack never wins the id.
        Assert.Equal(11, InstanceStacking.Merge(low, Slot(Potion, 1, 0, Level(68)), out _).Stack.InstanceId);
        Assert.Equal(11, InstanceStacking.Merge(Slot(Potion, 1, 0, Level(68)), low, out _).Stack.InstanceId);
    }

    [Fact]
    public void A_merge_saturates_at_int_MaxValue_exactly_as_Add_does_today()
    {
        ItemSlot nearlyFull = Slot(Potion, int.MaxValue - 1, 11);
        ItemSlot merged = InstanceStacking.Merge(nearlyFull, Slot(Potion, 5, 12), out int remainder);

        Assert.Equal(int.MaxValue, merged.Stack.Count);
        Assert.Equal(4, remainder);

        // The same arithmetic ItemContainer.Add has always run: one unit of the five fits and the caller
        // owns the four that did not.
        var kernel = new ItemContainer(4, Stacks);
        kernel.SetAt(0, new ItemStack(Potion, int.MaxValue - 1));
        Assert.Equal(1, kernel.Add(Potion, 5));
        Assert.Equal(int.MaxValue, kernel[0].Count);
    }

    [Fact]
    public void Two_items_differing_only_in_an_UNKNOWN_field_do_not_merge()
    {
        // Contracts 9.4's conservative answer. Neither build understands kind 2000, both keep it verbatim,
        // and the merge refuses because refusing costs a slot while merging loses a property.
        ItemSlot plain = Slot(Sword, 1, 11, Level(68));
        ItemSlot carrying = Slot(Sword, 1, 12, LevelPlusUnknown(68));

        Assert.False(InstanceStacking.CanMerge(plain, carrying, Stacks));
        Assert.False(InstanceStacking.CanMerge(carrying, plain, Stacks));

        // Two items carrying the SAME unknown field still merge, because the bytes agree.
        Assert.True(InstanceStacking.CanMerge(carrying, Slot(Sword, 1, 13, LevelPlusUnknown(68)), Stacks));
    }
}
