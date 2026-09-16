using System;
using System.Collections.Generic;
using KhaozEngine.ItemInstances;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.ItemInstances.Generation;
using Xunit;
using static KhaozEngine.Tests.ItemInstances.Crafting.CraftGuardWorld;
using static KhaozEngine.Tests.ItemInstances.Generation.GenerationWorld;

namespace KhaozEngine.Tests.ItemInstances.Crafting;

/// <summary>
/// The three STANDING rules of spec 10.3, which are refusals no currency can opt out of and which
/// therefore cannot live in an authored guard set.
/// <para>
/// They are three rather than one restatement of one: a corrupted item cannot be modified at all, a legacy
/// affix ENTRY is frozen where it stands, and a legacy mod ROW can never be added. Each bites a different
/// craft, and an implementer who writes one of them believes they have written all three.
/// </para>
/// <para>
/// Every registry and snapshot a fact builds is its own, so nothing here writes process-global state.
/// </para>
/// </summary>
public sealed class CraftStandingRuleTests
{
    [Fact]
    public void IsCorruptible_is_STANDING_so_every_primitive_that_writes_the_payload_refuses()
    {
        CraftWorld world = Legacy();
        byte[] corrupted = Target(1u << CorruptedBit);
        byte[] plain = Target(0);
        CraftRefusal standing = new(CraftRefusalKind.GuardFailed, (int)CraftGuardKind.IsCorruptible);

        foreach (Step step in Writers(world))
        {
            CraftWorkingCopy copy = world.Open(corrupted);
            Assert.Equal(standing, step.Apply(ref copy));
            Assert.False(copy.TryEncode(out _));

            // The same step on the same item WITHOUT bit 0 applies, so what refused is the corruption
            // rather than the step.
            CraftWorkingCopy fine = world.Open(plain);
            Assert.Null(step.Apply(ref fine));
        }
    }

    [Fact]
    public void IsCorruptible_stays_in_the_vocabulary_and_the_worked_whetstone_does_NOT_author_it()
    {
        // Spec 10.4's whetstone, as its two content rows author it: one target guard and one step guard.
        CraftGuard[] target = [new CraftGuard(CraftGuardKind.IsIdentified, 1, 0)];
        CraftGuard[] step = [new CraftGuard(CraftGuardKind.QualityBetween, 0, 19)];
        Assert.DoesNotContain(target, static guard => guard.Kind == CraftGuardKind.IsCorruptible);
        Assert.DoesNotContain(step, static guard => guard.Kind == CraftGuardKind.IsCorruptible);

        // And a corrupted item is refused anyway, by a rule the whetstone never named, which is the whole
        // reason the refusal is standing rather than authored.
        CraftWorld world = Legacy();
        CraftWorkingCopy copy = world.Open(Target(1u << CorruptedBit));
        Assert.Equal(
            CraftGuardOutcome.Passed,
            CraftGuardEvaluator.EvaluateSet(CraftGuardScope.Target, target, ref copy, world.Snapshot, out _));
        Assert.Equal(
            new CraftRefusal(CraftRefusalKind.GuardFailed, (int)CraftGuardKind.IsCorruptible),
            CraftPrimitives.Repair(ref copy, 0));

        // It stays in the vocabulary because an author may write it down to document intent, which is
        // legal and redundant rather than an error.
        CraftWorkingCopy authored = world.Open(Target(1u << CorruptedBit));
        Assert.Equal(
            CraftGuardOutcome.Refused,
            CraftGuardEvaluator.EvaluateSet(
                CraftGuardScope.Target,
                [new CraftGuard(CraftGuardKind.IsCorruptible, 0, 0)],
                ref authored,
                world.Snapshot,
                out _));
    }

    [Fact]
    public void A_legacy_affix_entry_is_FROZEN_and_RerollValues_SKIPS_it()
    {
        // Sorted, the list is mod 1 then mod 20, and mod 20 is the legacy row.
        CraftWorld world = Legacy();
        byte[] stored = world.Payload(60, RareRarity, [Affix(1), Affix(AncientMod)]);
        var random = new ScriptedRandomSource([], [1000, 2000]);

        CraftWorkingCopy copy = world.Open(stored);
        Assert.Null(CraftPrimitives.RerollValues(ref copy, random, [0, 1]));
        Assert.True(copy.TryEncode(out byte[] crafted));

        // The ordinary entry took a fresh position and the legacy entry is exactly where it was, which is
        // what stops a reroll farming a legacy tier's preserved range back to the top of itself.
        Assert.Equal([(1, 1000), (AncientMod, RollPosition.Bottom)], Entries(world, crafted));
    }

    [Fact]
    public void RerollValues_on_a_legacy_entry_ALONE_REFUSES_which_is_the_cost_the_owner_accepted()
    {
        // OWNER DECISION 3, priced: a currency authored as RerollValues(ByIndex) on the frozen entry does
        // nothing it was asked to do, so it refuses rather than reporting a craft that changed nothing.
        CraftWorld world = Legacy();
        CraftWorkingCopy copy = world.Open(world.Payload(60, RareRarity, [Affix(1), Affix(AncientMod)]));

        Assert.Equal(
            new CraftRefusal(CraftRefusalKind.LegacyEntryFrozen, AncientMod),
            CraftPrimitives.RerollValues(ref copy, new ScriptedRandomSource([], [1000]), [1]));
        Assert.False(copy.TryEncode(out _));
    }

    [Fact]
    public void RerollValues_draws_a_position_for_EVERY_selected_entry_and_discards_a_frozen_ones()
    {
        // The frozen entry costs the stream exactly what an ordinary one costs, so the draw count of a
        // reroll is a function of the SELECTION rather than of how many legacy rows the item happens to
        // carry. Without that, a seeded craft session diverges on the first legacy item it meets.
        CraftWorld world = Legacy();
        var frozen = new RecordingRandomSource(new ScriptedRandomSource([], [1000, 2000]));
        var ordinary = new RecordingRandomSource(new ScriptedRandomSource([], [1000, 2000]));

        CraftWorkingCopy legacy = world.Open(world.Payload(60, RareRarity, [Affix(1), Affix(AncientMod)]));
        Assert.Null(CraftPrimitives.RerollValues(ref legacy, frozen, [0, 1]));

        CraftWorkingCopy clean = world.Open(world.Payload(60, RareRarity, [Affix(1), Affix(3)]));
        Assert.Null(CraftPrimitives.RerollValues(ref clean, ordinary, [0, 1]));

        Assert.Equal(["position", "position"], frozen.Calls);
        Assert.Equal(ordinary.Calls, frozen.Calls);
    }

    [Fact]
    public void SetRaritys_trim_leaves_a_legacy_entry_where_it_is_and_COUNTS_it_against_the_limits()
    {
        // Magic permits ONE affix and the sorted END of the list is the legacy entry, so the trim steps
        // over the entry it may not touch and takes the next one instead.
        CraftWorld world = Legacy();
        CraftWorkingCopy copy = world.Open(world.Payload(60, RareRarity, [Affix(1), Affix(AncientMod)]));
        Assert.Null(CraftPrimitives.SetRarity(ref copy, world.Generator(Source()), MagicRarity, fill: false));
        Assert.True(copy.TryEncode(out byte[] crafted));
        Assert.Equal([(AncientMod, RollPosition.Bottom)], Entries(world, crafted));

        // And when every entry left is frozen, the trim removes nothing and the item stays over the rule's
        // count, which is the frozen reading's cost rather than a refusal.
        CraftWorkingCopy stuck = world.Open(world.Payload(60, RareRarity, [Affix(AncientMod), Affix(ElderMod)]));
        Assert.Null(CraftPrimitives.SetRarity(ref stuck, world.Generator(Source()), MagicRarity, fill: false));
        Assert.True(stuck.TryEncode(out byte[] full));
        Assert.Equal(
            [(AncientMod, RollPosition.Bottom), (ElderMod, RollPosition.Bottom)],
            Entries(world, full));
    }

    [Fact]
    public void SetRaritys_fill_COUNTS_a_legacy_entry_against_the_rules_limits()
    {
        // Rare asks for four at the floor and the item already carries one that cannot move, so the fill
        // draws THREE. Counting it is the difference between an item of four affixes and one of five.
        CraftWorld world = Legacy();
        CraftWorkingCopy copy = world.Open(world.Payload(60, MagicRarity, [Affix(AncientMod)]));

        Assert.Null(CraftPrimitives.SetRarity(ref copy, world.Generator(Source()), RareRarity, fill: true));
        Assert.True(copy.TryEncode(out byte[] crafted));

        List<(int ModId, int Position)> entries = Entries(world, crafted);
        Assert.Equal(4, entries.Count);
        Assert.Contains(entries, static entry => entry.ModId == AncientMod && entry.Position == RollPosition.Bottom);
    }

    [Fact]
    public void A_game_operations_working_copy_REFUSES_a_write_that_rewrites_a_legacy_entry()
    {
        // The door is the one affix write every primitive and every ICraftOperation goes through, so the
        // rule holds for code the engine has never seen.
        CraftWorld world = Legacy();
        byte[] stored = world.Payload(60, RareRarity, [Affix(1), Affix(AncientMod)]);
        CraftRefusal frozen = new(CraftRefusalKind.LegacyEntryFrozen, AncientMod);

        CraftWorkingCopy rolled = world.Open(stored);
        Assert.False(rolled.SetAffixes(InstancePropertyKind.Affixes, [Affix(1), Affix(AncientMod, 1, 9000)]));
        Assert.Equal(frozen, rolled.Refusal);

        CraftWorkingCopy tiered = world.Open(stored);
        Assert.False(tiered.SetAffixes(InstancePropertyKind.Affixes, [Affix(1), Affix(AncientMod, 2)]));
        Assert.Equal(frozen, tiered.Refusal);

        // REMOVING it is the one thing a craft may still do with it, and rewriting the entry beside it is
        // untouched, so the rule is about the frozen entry rather than about the list.
        CraftWorkingCopy removed = world.Open(stored);
        Assert.True(removed.SetAffixes(InstancePropertyKind.Affixes, [Affix(1)]));

        CraftWorkingCopy neighbour = world.Open(stored);
        Assert.True(neighbour.SetAffixes(InstancePropertyKind.Affixes, [Affix(1, 1, 9000), Affix(AncientMod)]));
    }

    [Fact]
    public void NotLegacy_is_STANDING_so_every_primitive_that_would_ADD_a_mod_refuses_a_legacy_row()
    {
        CraftWorld world = Legacy();
        CraftRefusal standing = new(CraftRefusalKind.GuardFailed, (int)CraftGuardKind.NotLegacy);

        CraftWorkingCopy enchant = world.Open(world.Payload(60, RareRarity, [Affix(1)]));
        Assert.Equal(standing, CraftPrimitives.ApplyEnchant(ref enchant, AncientMod, 1));

        // And through the write door, so an ICraftOperation cannot add one either, regardless of what the
        // currency's guard set says.
        CraftWorkingCopy written = world.Open(world.Payload(60, RareRarity, [Affix(1)]));
        Assert.False(written.SetAffixes(InstancePropertyKind.Affixes, [Affix(1), Affix(AncientMod)]));
        Assert.Equal(standing, written.Refusal);

        // An entry the item ALREADY carries, written back unchanged, is not an addition.
        CraftWorkingCopy held = world.Open(world.Payload(60, RareRarity, [Affix(1), Affix(AncientMod)]));
        Assert.True(held.SetAffixes(InstancePropertyKind.Affixes, [Affix(1), Affix(AncientMod)]));
    }

    [Fact]
    public void NotLegacy_as_an_AUTHORED_guard_is_the_stronger_statement_that_the_item_carries_NONE()
    {
        CraftWorld world = Legacy();

        // The STANDING rule is about the ROW a step would add, so it says nothing at all about a craft
        // that adds none: this item carries a legacy affix and its quality still moves.
        CraftWorkingCopy carrying = world.Open(world.Payload(60, RareRarity, [Affix(AncientMod)]));
        Assert.Null(CraftPrimitives.SetQuality(ref carrying, 5, absolute: true));

        // The AUTHORED guard is the other statement entirely: this item must carry NONE, whatever the
        // steps do, so the same item refuses before any step runs.
        CraftWorkingCopy guarded = world.Open(world.Payload(60, RareRarity, [Affix(AncientMod)]));
        Assert.Equal(
            CraftGuardOutcome.Refused,
            CraftGuardEvaluator.EvaluateSet(
                CraftGuardScope.Target,
                [new CraftGuard(CraftGuardKind.NotLegacy, 0, 0)],
                ref guarded,
                world.Snapshot,
                out _));

        // And the standing rule bites exactly where the authored guard passes: an item carrying no legacy
        // row at all still cannot have one added.
        CraftWorkingCopy clean = world.Open(world.Payload(60, RareRarity, [Affix(1)]));
        Assert.True(CraftGuardEvaluator.Evaluate(CraftGuardKind.NotLegacy, 0, 0, in clean, world.Snapshot));
        Assert.Equal(
            new CraftRefusal(CraftRefusalKind.GuardFailed, (int)CraftGuardKind.NotLegacy),
            CraftPrimitives.ApplyEnchant(ref clean, AncientMod, 1));
    }

    /// <summary>One primitive, named, so a fact can run the same craft over two items.</summary>
    /// <param name="Name">What it is, which is what a failed assertion reads as.</param>
    /// <param name="Apply">The call.</param>
    internal sealed record Step(string Name, StepApply Apply);

    /// <summary>The shape of a primitive call a fact drives, which a <c>ref struct</c> needs a name for.</summary>
    /// <param name="copy">The craft in progress.</param>
    internal delegate CraftRefusal? StepApply(ref CraftWorkingCopy copy);

    /// <summary>
    /// One primitive per WRITE shape: a scalar, a pair, a flag bit, kind 128, the affix list, the
    /// enchantment list and the socket list. Every one of them goes through the working copy's one write
    /// door, which is where the standing refusal sits.
    /// </summary>
    /// <param name="world">The authored world, for the generator the affix primitives draw through.</param>
    static IEnumerable<Step> Writers(CraftWorld world)
    {
        yield return new Step(nameof(CraftPrimitives.SetQuality), (ref CraftWorkingCopy copy) => CraftPrimitives.SetQuality(ref copy, 5, absolute: true));
        yield return new Step(nameof(CraftPrimitives.Repair), (ref CraftWorkingCopy copy) => CraftPrimitives.Repair(ref copy, 0));
        yield return new Step(nameof(CraftPrimitives.SetFlag), (ref CraftWorkingCopy copy) => CraftPrimitives.SetFlag(ref copy, MirroredBit, value: true));
        yield return new Step(nameof(CraftPrimitives.Identify), (ref CraftWorkingCopy copy) => CraftPrimitives.Identify(ref copy));
        yield return new Step(nameof(CraftPrimitives.RemoveMod), (ref CraftWorkingCopy copy) => CraftPrimitives.RemoveMod(ref copy, [0]));
        yield return new Step(nameof(CraftPrimitives.ApplyEnchant), (ref CraftWorkingCopy copy) => CraftPrimitives.ApplyEnchant(ref copy, 1, 1));
        yield return new Step(nameof(CraftPrimitives.AddSocket), (ref CraftWorkingCopy copy) => CraftPrimitives.AddSocket(ref copy, GemSocket));
        yield return new Step(nameof(CraftPrimitives.AddRandomMod), (ref CraftWorkingCopy copy) => CraftPrimitives.AddRandomMod(ref copy, world.Generator(Source()), ModContentType.SuffixKind, 0));
    }

    /// <summary>One target carrying every field the writers touch, with the flags a fact hands in.</summary>
    static byte[] Target(uint flags)
        => Encoded(builder =>
        {
            if (flags != 0)
            {
                _ = builder.AddScalar(InstancePropertyKind.Flags, flags);
            }

            _ = builder.AddScalar(InstancePropertyKind.ItemLevel, 60);
            _ = builder.AddScalar(InstancePropertyKind.Quality, 10);
            _ = builder.AddScalars(InstancePropertyKind.Durability, 30, 120);
            _ = builder.AddIdentification(identified: true, revealedMask: 0);
            _ = builder.AddByte(InstancePropertyKind.Rarity, RareRarity);
            _ = builder.AddAffixes(InstancePropertyKind.Affixes, [Affix(1)]);
        });

    /// <summary>A source that answers the bottom of every range, which is enough for a fact about shape.</summary>
    static IRandomSource Source() => new ScriptedRandomSource([]);

    /// <summary>One payload's affix entries as mod id and roll position, in the order it holds them.</summary>
    static List<(int ModId, int Position)> Entries(CraftWorld world, byte[] payload)
    {
        CraftWorkingCopy copy = world.Open(payload);
        var affixes = new InstanceAffix[CraftWorkingCopy.MaxAffixes];
        int count = copy.ReadAffixes(InstancePropertyKind.Affixes, affixes);
        var entries = new List<(int, int)>(count);
        for (int index = 0; index < count; index++)
        {
            entries.Add((affixes[index].ModId, affixes[index].Position));
        }

        return entries;
    }
}
