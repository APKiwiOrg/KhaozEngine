using System;
using System.Collections.Generic;
using System.Reflection;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.ItemInstances.Generation;
using Xunit;
using static KhaozEngine.Tests.ItemInstances.Content.InstanceContentTypeFixtures;
using static KhaozEngine.Tests.ItemInstances.Content.InstanceValidationFixtures;
using static KhaozEngine.Tests.ItemInstances.Crafting.CraftGuardWorld;
using static KhaozEngine.Tests.ItemInstances.Generation.GenerationWorld;

namespace KhaozEngine.Tests.ItemInstances.Crafting;

/// <summary>
/// The three closed vocabularies of spec 10.3: how a guard SET composes, one fact per guard kind, and one
/// fact per selector kind.
/// <para>
/// The worlds are authored HERE rather than shipped, because the engine ships eighteen shapes and no rows,
/// and every registry and snapshot a fact builds is its own, so nothing here writes process-global state.
/// </para>
/// </summary>
public sealed class CraftGuardTests
{
    [Fact]
    public void Guards_are_ANDed_and_there_is_no_OR_no_NOT_and_no_nesting()
    {
        CraftWorld world = Chain();
        byte[] stored = world.Payload(60, RareRarity, [Affix(1)]);

        // ANDed, so one false anywhere in the set fails the whole set wherever it sits, and the FIRST
        // false is the one reported.
        Assert.Equal(CraftGuardOutcome.Passed, Set(world, stored, [Passes, Passes], out _));
        Assert.Equal(CraftGuardOutcome.Refused, Set(world, stored, [Passes, Fails], out int second));
        Assert.Equal(1, second);
        Assert.Equal(CraftGuardOutcome.Refused, Set(world, stored, [Fails, Passes], out int first));
        Assert.Equal(0, first);

        // No nesting, and therefore no room for an operator: a guard is a kind plus two integers, so
        // nothing in the vocabulary can name another guard to OR it with or to negate.
        Assert.All(
            typeof(CraftGuard).GetProperties(BindingFlags.Public | BindingFlags.Instance),
            static property => Assert.True(
                property.PropertyType == typeof(CraftGuardKind) || property.PropertyType == typeof(int),
                property.PropertyType.Name));
    }

    [Fact]
    public void A_TARGET_guard_that_fails_refuses_the_whole_craft_and_consumes_NOTHING()
    {
        CraftWorld world = Chain();
        byte[] stored = world.Payload(60, RareRarity, [Affix(1)]);

        CraftWorkingCopy copy = world.Open(stored);
        CraftGuardOutcome outcome = CraftGuardEvaluator.EvaluateSet(
            CraftGuardScope.Target,
            [Fails],
            ref copy,
            world.Snapshot,
            out int failed);

        Assert.Equal(CraftGuardOutcome.Refused, outcome);
        Assert.Equal(0, failed);
        Assert.True(copy.IsRefused);

        // Nothing was written, so there is no half craft for the executor to undo, and every later step is
        // a no-op on a copy that already refused.
        Assert.False(copy.TryEncode(out _));
        Assert.Equal(new CraftRefusal(CraftRefusalKind.GuardFailed, (int)CraftGuardKind.RarityIs), copy.Refusal);

        // And no draw was consumed either, which is a property of the SHAPE rather than of this craft: the
        // evaluator is handed no IRandomSource anywhere in its surface, so a guard provably cannot roll.
        Assert.All(
            typeof(CraftGuardEvaluator).GetMethods(BindingFlags.Public | BindingFlags.Static),
            static method => Assert.DoesNotContain(
                method.GetParameters(),
                static parameter => parameter.ParameterType == typeof(IRandomSource)));
    }

    [Fact]
    public void A_STEP_guard_that_fails_SKIPS_that_step_and_the_craft_continues()
    {
        // Spec 10.4's worked whetstone, at quality 20. Its step 1 guard is QualityBetween(0, 19), which is
        // false, and the whetstone still repairs, because a step guard skips ONE STEP rather than refusing
        // the craft. That difference is the reason both guard scopes exist.
        CraftWorld world = Chain();
        byte[] stored = Encoded(builder => builder
            .AddScalar(InstancePropertyKind.ItemLevel, 60)
            .AddScalar(InstancePropertyKind.Quality, 20)
            .AddScalars(InstancePropertyKind.Durability, 30, 120));

        CraftWorkingCopy copy = world.Open(stored);
        CraftGuardOutcome outcome = CraftGuardEvaluator.EvaluateSet(
            CraftGuardScope.Step,
            [new CraftGuard(CraftGuardKind.QualityBetween, 0, 19)],
            ref copy,
            world.Snapshot,
            out int failed);

        Assert.Equal(CraftGuardOutcome.Skipped, outcome);
        Assert.Equal(0, failed);
        Assert.False(copy.IsRefused);

        // Step 2 runs, so the craft continued through the skip and still encodes.
        Assert.Null(CraftPrimitives.Repair(ref copy, 0));
        Assert.True(copy.TryEncode(out byte[] crafted));
        Assert.Equal(20ul, Scalar(crafted, InstancePropertyKind.Quality));
        Assert.Equal(120ul, Scalar(crafted, InstancePropertyKind.Durability));
    }

    [Fact]
    public void A_refusal_NAMES_the_guard_kind_rather_than_carrying_a_message()
    {
        CraftWorld world = Chain();
        CraftWorkingCopy copy = world.Open(world.Payload(60, RareRarity, [Affix(1)]));
        _ = CraftGuardEvaluator.EvaluateSet(CraftGuardScope.Target, [Fails], ref copy, world.Snapshot, out _);

        Assert.Equal(
            new CraftRefusal(CraftRefusalKind.GuardFailed, (int)CraftGuardKind.RarityIs),
            copy.Refusal);

        // And there is nowhere for a message to live, which is what makes a refusal a value a counter can
        // bucket and a client can localize rather than a string only its author reads.
        Assert.DoesNotContain(
            typeof(CraftRefusal).GetProperties(BindingFlags.Public | BindingFlags.Instance),
            static property => property.PropertyType == typeof(string));
    }

    [Fact]
    public void RarityIsAtMost_walks_the_upgrade_from_chain_rather_than_comparing_ids()
    {
        // The chain is magic 1, rare 2, exalted 9, mythic 3, so the ids DISAGREE with the order twice over.
        CraftWorld world = Chain();
        CraftWorkingCopy copy = world.Open(world.Payload(60, Exalted, [Affix(1)]));

        // An id comparison reads 9 is at most 3 as false. The chain walks mythic down to exalted and
        // answers true, which is the only reading that survives an author numbering a rarity late.
        Assert.True(Guard(CraftGuardKind.RarityIsAtMost, Mythic, 0, in copy));

        // And it is not simply true everywhere: rare is BELOW exalted, so an exalted item is not at most
        // rare, even though an id comparison of 9 against 2 says nothing useful either way.
        Assert.False(Guard(CraftGuardKind.RarityIsAtMost, RareRarity, 0, in copy));
        Assert.False(Guard(CraftGuardKind.RarityIsAtMost, MagicRarity, 0, in copy));
    }

    [Fact]
    public void HasMod_looks_in_kind_131_AND_kind_133()
    {
        CraftWorld world = Chain();
        CraftWorkingCopy affixed = world.Open(world.Payload(60, RareRarity, [Affix(1)]));
        CraftWorkingCopy enchanted = world.Open(world.Payload(60, RareRarity, [], null, [Affix(2)]));

        Assert.True(Guard(CraftGuardKind.HasMod, 1, 0, in affixed));
        Assert.False(Guard(CraftGuardKind.HasMod, 2, 0, in affixed));

        // The enchantment is in kind 133 and nowhere else, so a guard reading only kind 131 answers false
        // for an item that plainly carries the mod.
        Assert.True(Guard(CraftGuardKind.HasMod, 2, 0, in enchanted));
        Assert.False(Guard(CraftGuardKind.HasMod, 1, 0, in enchanted));
    }

    [Fact]
    public void HasTag_asks_the_BASE_rather_than_the_instance()
    {
        // ONE payload, two bases. The payload carries no tag anywhere, so the only thing that can move the
        // answer is the item row the working copy was opened on.
        CraftWorld world = Chain();
        byte[] stored = world.Payload(60, RareRarity, [Affix(1)]);

        CraftWorkingCopy greatsword = world.Open(stored);
        Assert.True(Guard(CraftGuardKind.HasTag, MetalTag, 0, in greatsword));
        Assert.True(Guard(CraftGuardKind.HasTag, BladeTag, 0, in greatsword));
        Assert.False(Guard(CraftGuardKind.HasTag, GemTag, 0, in greatsword));

        CraftWorkingCopy wand = (world with { DefinitionId = Wand }).Open(stored);
        Assert.True(Guard(CraftGuardKind.HasTag, GemTag, 0, in wand));
        Assert.False(Guard(CraftGuardKind.HasTag, MetalTag, 0, in wand));
    }

    [Fact]
    public void RarityIs_is_true_when_kind_130_equals_the_rarity_id()
    {
        CraftWorkingCopy copy = Chain().Open(Chain().Payload(60, RareRarity, [Affix(1)]));

        Assert.True(Guard(CraftGuardKind.RarityIs, RareRarity, 0, in copy));
        Assert.False(Guard(CraftGuardKind.RarityIs, MagicRarity, 0, in copy));
    }

    [Fact]
    public void RarityIsAtMost_is_true_for_the_rarity_itself_and_for_everything_it_upgrades_from()
    {
        CraftWorld world = Chain();
        CraftWorkingCopy copy = world.Open(world.Payload(60, RareRarity, [Affix(1)]));

        Assert.True(Guard(CraftGuardKind.RarityIsAtMost, RareRarity, 0, in copy));
        Assert.True(Guard(CraftGuardKind.RarityIsAtMost, Exalted, 0, in copy));
        Assert.False(Guard(CraftGuardKind.RarityIsAtMost, MagicRarity, 0, in copy));
    }

    [Fact]
    public void RarityIsAtMost_stops_at_a_RETIRED_rule_in_the_chain()
    {
        // Every other reader of a rarity rule gates on the LIVE row: CraftPrimitives.Resolve refuses a
        // retired rarity id outright and walks upgrade_from over LiveRows alone. This guard read the row
        // whatever its retired bit said, so a rule a later version retired still carried its link and an
        // exalted item still answered "at most mythic" through it.
        CraftWorld live = Chain();
        CraftWorkingCopy joined = live.Open(live.Payload(60, MagicRarity, [Affix(1)]));
        Assert.True(Guard(CraftGuardKind.RarityIsAtMost, Mythic, 0, in joined));

        CraftWorld retired = ChainWithRetired(RareRarity);
        CraftWorkingCopy broken = retired.Open(retired.Payload(60, MagicRarity, [Affix(1)]));
        Assert.False(Guard(CraftGuardKind.RarityIsAtMost, Mythic, 0, in broken));

        // The links ABOVE the retired one still join, so what broke is that rule rather than the walk.
        CraftWorkingCopy above = retired.Open(retired.Payload(60, Exalted, [Affix(1)]));
        Assert.True(Guard(CraftGuardKind.RarityIsAtMost, Mythic, 0, in above));
    }

    [Fact]
    public void AffixCountAtMost_counts_the_entries_of_one_mod_kind()
    {
        CraftWorld world = Chain();

        // Mods 1 and 3 are prefixes and mod 2 is a suffix, so the two counts are read apart.
        CraftWorkingCopy copy = world.Open(world.Payload(60, RareRarity, [Affix(1), Affix(2), Affix(3)]));

        Assert.True(Guard(CraftGuardKind.AffixCountAtMost, ModContentType.PrefixKind, 2, in copy));
        Assert.False(Guard(CraftGuardKind.AffixCountAtMost, ModContentType.PrefixKind, 1, in copy));
        Assert.True(Guard(CraftGuardKind.AffixCountAtMost, ModContentType.SuffixKind, 1, in copy));
    }

    [Fact]
    public void AffixCountAtLeast_counts_the_entries_of_one_mod_kind()
    {
        CraftWorld world = Chain();
        CraftWorkingCopy copy = world.Open(world.Payload(60, RareRarity, [Affix(1), Affix(2), Affix(3)]));

        Assert.True(Guard(CraftGuardKind.AffixCountAtLeast, ModContentType.PrefixKind, 2, in copy));
        Assert.False(Guard(CraftGuardKind.AffixCountAtLeast, ModContentType.PrefixKind, 3, in copy));

        // A mod kind of 0 is every kind, which is how an author asks about the list as a whole.
        Assert.True(Guard(CraftGuardKind.AffixCountAtLeast, 0, 3, in copy));
    }

    [Fact]
    public void HasMod_is_true_when_the_item_carries_that_mod_and_false_when_it_does_not()
    {
        CraftWorld world = Chain();
        CraftWorkingCopy copy = world.Open(world.Payload(60, RareRarity, [Affix(1)], null, [Affix(2)]));

        Assert.True(Guard(CraftGuardKind.HasMod, 1, 0, in copy));
        Assert.True(Guard(CraftGuardKind.HasMod, 2, 0, in copy));
        Assert.False(Guard(CraftGuardKind.HasMod, 3, 0, in copy));
        Assert.False(Guard(CraftGuardKind.HasMod, 0, 0, in copy));
    }

    [Fact]
    public void HasTag_is_true_for_each_tag_the_base_carries_and_false_for_every_other()
    {
        CraftWorld world = Chain();
        CraftWorkingCopy copy = world.Open(world.Payload(60, RareRarity, [Affix(1)]));

        Assert.True(Guard(CraftGuardKind.HasTag, MetalTag, 0, in copy));
        Assert.True(Guard(CraftGuardKind.HasTag, BladeTag, 0, in copy));
        Assert.False(Guard(CraftGuardKind.HasTag, 99, 0, in copy));

        // A tag id of 0 is no tag at all rather than every tag, so it is never carried.
        Assert.False(Guard(CraftGuardKind.HasTag, 0, 0, in copy));
    }

    [Fact]
    public void LacksMod_is_true_exactly_when_HasMod_is_not()
    {
        CraftWorld world = Chain();
        CraftWorkingCopy copy = world.Open(world.Payload(60, RareRarity, [Affix(1)], null, [Affix(2)]));

        Assert.False(Guard(CraftGuardKind.LacksMod, 1, 0, in copy));
        Assert.False(Guard(CraftGuardKind.LacksMod, 2, 0, in copy));
        Assert.True(Guard(CraftGuardKind.LacksMod, 3, 0, in copy));
    }

    [Fact]
    public void ItemLevelBetween_reads_kind_2_inclusive_at_both_ends()
    {
        CraftWorld world = Chain();
        CraftWorkingCopy copy = world.Open(world.Payload(60, RareRarity, [Affix(1)]));

        Assert.True(Guard(CraftGuardKind.ItemLevelBetween, 60, 60, in copy));
        Assert.True(Guard(CraftGuardKind.ItemLevelBetween, 1, 100, in copy));
        Assert.False(Guard(CraftGuardKind.ItemLevelBetween, 61, 100, in copy));
        Assert.False(Guard(CraftGuardKind.ItemLevelBetween, 1, 59, in copy));
    }

    [Fact]
    public void QualityBetween_reads_kind_3_and_an_item_with_no_quality_reads_as_0()
    {
        CraftWorld world = Chain();
        CraftWorkingCopy plain = world.Open(world.Payload(60, RareRarity, [Affix(1)]));
        CraftWorkingCopy polished = world.Open(world.Payload(60, RareRarity, [Affix(1)], null, null, 20));

        // The whetstone's own step guard, and the reason an absent kind 3 must read as 0: quality 0 is
        // what an item that never had quality carries, so a repair currency has to see it.
        Assert.True(Guard(CraftGuardKind.QualityBetween, 0, 19, in plain));
        Assert.False(Guard(CraftGuardKind.QualityBetween, 0, 19, in polished));
        Assert.True(Guard(CraftGuardKind.QualityBetween, 20, 20, in polished));
    }

    [Fact]
    public void SocketCountBetween_reads_kind_132s_count_inclusive()
    {
        CraftWorld world = Chain();
        CraftWorkingCopy none = world.Open(world.Payload(60, RareRarity, [Affix(1)]));
        CraftWorkingCopy two = world.Open(world.Payload(60, RareRarity, [Affix(1)], [GemSocket, GemSocket]));

        Assert.True(Guard(CraftGuardKind.SocketCountBetween, 0, 0, in none));
        Assert.False(Guard(CraftGuardKind.SocketCountBetween, 1, 4, in none));
        Assert.True(Guard(CraftGuardKind.SocketCountBetween, 2, 2, in two));
        Assert.False(Guard(CraftGuardKind.SocketCountBetween, 3, 4, in two));
    }

    [Fact]
    public void SocketEmpty_is_true_when_that_sockets_contained_definition_is_0()
    {
        CraftWorld world = Chain();
        byte[] stored = Encoded(builder => builder
            .AddScalar(InstancePropertyKind.ItemLevel, 60)
            .AddSockets([new InstanceSocket(GemSocket, 0, 0, default), new InstanceSocket(GemSocket, Wand, 77, default)]));

        CraftWorkingCopy copy = world.Open(stored);
        Assert.True(Guard(CraftGuardKind.SocketEmpty, 0, 0, in copy));
        Assert.False(Guard(CraftGuardKind.SocketEmpty, 1, 0, in copy));

        // A socket index the item does not have is not an empty socket, because there is no socket there.
        Assert.False(Guard(CraftGuardKind.SocketEmpty, 2, 0, in copy));
    }

    [Fact]
    public void IsIdentified_compares_kind_128s_state_and_an_item_with_no_kind_128_is_identified()
    {
        CraftWorld world = Chain();
        CraftWorkingCopy unidentified = world.Open(world.Payload(60, RareRarity, [Affix(1)]));
        CraftWorkingCopy plain = world.Open(Encoded(builder => builder.AddScalar(InstancePropertyKind.ItemLevel, 60)));

        Assert.True(Guard(CraftGuardKind.IsIdentified, 0, 0, in unidentified));
        Assert.False(Guard(CraftGuardKind.IsIdentified, 1, 0, in unidentified));

        // An item that never carried kind 128 was never unidentified, so the whetstone's target guard
        // IsIdentified(1) passes on it rather than refusing every ordinary item in the game.
        Assert.True(Guard(CraftGuardKind.IsIdentified, 1, 0, in plain));
        Assert.False(Guard(CraftGuardKind.IsIdentified, 0, 0, in plain));
    }

    [Fact]
    public void FlagIs_compares_one_bit_of_kind_1()
    {
        CraftWorld world = Chain();
        byte[] stored = Encoded(builder => builder
            .AddScalar(InstancePropertyKind.Flags, 1u << MirroredBit)
            .AddScalar(InstancePropertyKind.ItemLevel, 60));

        CraftWorkingCopy copy = world.Open(stored);
        Assert.True(Guard(CraftGuardKind.FlagIs, MirroredBit, 1, in copy));
        Assert.False(Guard(CraftGuardKind.FlagIs, MirroredBit, 0, in copy));
        Assert.True(Guard(CraftGuardKind.FlagIs, CorruptedBit, 0, in copy));
        Assert.False(Guard(CraftGuardKind.FlagIs, CorruptedBit, 1, in copy));
    }

    [Fact]
    public void NotLegacy_is_true_when_NO_affix_on_the_item_names_a_legacy_mod_row()
    {
        CraftWorld world = Legacy();
        CraftWorkingCopy clean = world.Open(world.Payload(60, RareRarity, [Affix(1)]));
        CraftWorkingCopy old = world.Open(world.Payload(60, RareRarity, [Affix(1), Affix(AncientMod)]));
        CraftWorkingCopy enchanted = world.Open(world.Payload(60, RareRarity, [], null, [Affix(AncientMod)]));

        Assert.True(Guard(CraftGuardKind.NotLegacy, 0, 0, in clean));
        Assert.False(Guard(CraftGuardKind.NotLegacy, 0, 0, in old));
        Assert.False(Guard(CraftGuardKind.NotLegacy, 0, 0, in enchanted));
    }

    [Fact]
    public void IsCorruptible_is_true_when_kind_1_bit_0_is_clear()
    {
        CraftWorld world = Chain();
        CraftWorkingCopy plain = world.Open(world.Payload(60, RareRarity, [Affix(1)]));
        CraftWorkingCopy corrupted = world.Open(Encoded(builder => builder
            .AddScalar(InstancePropertyKind.Flags, 1u << CorruptedBit)
            .AddScalar(InstancePropertyKind.ItemLevel, 60)));

        Assert.True(Guard(CraftGuardKind.IsCorruptible, 0, 0, in plain));
        Assert.False(Guard(CraftGuardKind.IsCorruptible, 0, 0, in corrupted));
    }

    [Fact]
    public void ByModId_refuses_when_the_mod_is_absent()
    {
        CraftWorld world = Chain();
        byte[] stored = world.Payload(60, RareRarity, [Affix(1), Affix(3)]);

        // The entry NAMING that mod, wherever it sits in the sorted list.
        Assert.Equal([1], Resolve(world, stored, new CraftSelector(CraftSelectorKind.ByModId, 3), out _));

        CraftWorkingCopy copy = world.Open(stored);
        Span<int> indexes = stackalloc int[CraftWorkingCopy.MaxAffixes];
        CraftRefusal? refusal = new CraftSelector(CraftSelectorKind.ByModId, 2).Resolve(
            ref copy,
            InstancePropertyKind.Affixes,
            new ScriptedRandomSource([]),
            indexes,
            out int count);

        Assert.Equal(new CraftRefusal(CraftRefusalKind.AffixAbsent, 2), refusal);
        Assert.Equal(0, count);
        Assert.True(copy.IsRefused);
    }

    [Fact]
    public void ByIndex_indexes_the_SORTED_list()
    {
        // Authored HIGH mod id first. The encoder sorts ascending by mod id, which is the only order the
        // payload ever holds, so index 0 is mod 1 whatever order the author handed the entries in.
        CraftWorld world = Chain();
        byte[] stored = world.Payload(60, RareRarity, [Affix(3), Affix(1)]);

        Assert.Equal([0], Resolve(world, stored, new CraftSelector(CraftSelectorKind.ByIndex, 0), out _));
        Assert.Equal([1], Resolve(world, stored, new CraftSelector(CraftSelectorKind.ByIndex, 1), out _));
        Assert.Equal([1, 3], Mods(world, stored));

        CraftWorkingCopy copy = world.Open(stored);
        Span<int> indexes = stackalloc int[CraftWorkingCopy.MaxAffixes];
        CraftRefusal? refusal = new CraftSelector(CraftSelectorKind.ByIndex, 2).Resolve(
            ref copy,
            InstancePropertyKind.Affixes,
            new ScriptedRandomSource([]),
            indexes,
            out _);

        Assert.Equal(new CraftRefusal(CraftRefusalKind.AffixAbsent, 2), refusal);
    }

    [Fact]
    public void RandomOfKind_is_the_ONLY_selector_that_draws_and_it_draws_exactly_once()
    {
        CraftWorld world = Chain();
        byte[] stored = world.Payload(60, RareRarity, [Affix(1), Affix(2), Affix(3)]);

        var drawing = new RecordingRandomSource(new ScriptedRandomSource([1]));
        int[] picked = Resolve(world, stored, new CraftSelector(CraftSelectorKind.RandomOfKind, ModContentType.PrefixKind), out _, drawing);

        // Mods 1 and 3 are the prefixes, the draw answered the second of them, and it cost the stream one
        // draw and no more.
        Assert.Equal([2], picked);
        Assert.Equal(["int:0:2"], drawing.Calls);

        // Every other selector kind draws NOTHING at all, which is what makes the draw count readable off
        // the step list.
        foreach (CraftSelector selector in Quiet())
        {
            var counted = new RecordingRandomSource(new ScriptedRandomSource([]));
            _ = Resolve(world, stored, selector, out _, counted);
            Assert.Empty(counted.Calls);
        }
    }

    [Fact]
    public void AllOfKind_selects_every_entry_of_that_kind_in_sorted_order()
    {
        CraftWorld world = Chain();
        byte[] stored = world.Payload(60, RareRarity, [Affix(1), Affix(2), Affix(3)]);

        Assert.Equal([0, 2], Resolve(world, stored, new CraftSelector(CraftSelectorKind.AllOfKind, ModContentType.PrefixKind), out _));
        Assert.Equal([1], Resolve(world, stored, new CraftSelector(CraftSelectorKind.AllOfKind, ModContentType.SuffixKind), out _));

        // A kind the item carries none of selects nothing, which is a step that does nothing rather than a
        // refusal: the currency asked for every entry of a kind and there were none.
        Assert.Empty(Resolve(
            world,
            world.Payload(60, RareRarity, [Affix(2)]),
            new CraftSelector(CraftSelectorKind.AllOfKind, ModContentType.PrefixKind),
            out _));
    }

    [Fact]
    public void LowestTier_and_HighestTier_break_a_tie_by_LOWER_mod_id()
    {
        // Both selectors read the tier ORDINAL. Mods 1 and 3 are prefixes sharing ordinal 2, so the tie is
        // the whole question, and mod 5 sits alone at ordinal 1.
        CraftWorld world = Chain();
        byte[] stored = world.Payload(60, RareRarity, [Affix(3, 2), Affix(1, 2), Affix(5, 1)]);

        Assert.Equal([1, 3, 5], Mods(world, stored));
        Assert.Equal([0], Resolve(world, stored, new CraftSelector(CraftSelectorKind.HighestTier, ModContentType.PrefixKind), out _));
        Assert.Equal([2], Resolve(world, stored, new CraftSelector(CraftSelectorKind.LowestTier, ModContentType.PrefixKind), out _));
    }

    [Fact]
    public void A_currencys_draw_count_is_a_function_of_its_STEP_LIST_not_of_the_item_it_hits()
    {
        // ONE step list over TWO items: a full prefix list and an item with no prefix at all. Both cost the
        // same draws, or a seeded craft session diverges at the first item whose selection ran dry, which
        // is the reproducibility property spec 9.3 gives the generator one level up.
        CraftWorld world = Chain();
        CraftSelector[] steps =
        [
            new CraftSelector(CraftSelectorKind.RandomOfKind, ModContentType.PrefixKind),
            new CraftSelector(CraftSelectorKind.RandomOfKind, ModContentType.SuffixKind),
        ];

        var full = new CountingSeededRandomSource(915);
        var dry = new CountingSeededRandomSource(915);
        byte[] rich = world.Payload(60, RareRarity, [Affix(1), Affix(2), Affix(3)]);
        byte[] bare = world.Payload(60, RareRarity, [Affix(2)]);

        foreach (CraftSelector step in steps)
        {
            _ = Resolve(world, rich, step, out _, full);
            _ = Resolve(world, bare, step, out _, dry);
        }

        Assert.True(full.UnderlyingDraws > 0, "The full item drew nothing, so the comparison reads nothing.");
        Assert.Equal(full.UnderlyingDraws, dry.UnderlyingDraws);
    }

    /// <summary>A guard that is true on every item these facts open, which is the AND's neutral element.</summary>
    static CraftGuard Passes => new(CraftGuardKind.RarityIs, RareRarity, 0);

    /// <summary>A guard that is false on the same items, which is what a set fails on.</summary>
    static CraftGuard Fails => new(CraftGuardKind.RarityIs, MagicRarity, 0);

    /// <summary>One guard, evaluated the way spec 10.3 writes it.</summary>
    static bool Guard(CraftGuardKind kind, int a, int b, in CraftWorkingCopy copy)
        => CraftGuardEvaluator.Evaluate(kind, a, b, in copy, copy.Snapshot);

    /// <summary>One guard set over a fresh copy, answering the outcome and which guard failed.</summary>
    static CraftGuardOutcome Set(CraftWorld world, byte[] stored, CraftGuard[] guards, out int failed)
    {
        CraftWorkingCopy copy = world.Open(stored);
        return CraftGuardEvaluator.EvaluateSet(CraftGuardScope.Target, guards, ref copy, world.Snapshot, out failed);
    }

    /// <summary>Every selector kind that must draw nothing at all, which is the other five.</summary>
    static IEnumerable<CraftSelector> Quiet()
    {
        yield return new CraftSelector(CraftSelectorKind.ByModId, 1);
        yield return new CraftSelector(CraftSelectorKind.ByIndex, 0);
        yield return new CraftSelector(CraftSelectorKind.AllOfKind, ModContentType.PrefixKind);
        yield return new CraftSelector(CraftSelectorKind.LowestTier, ModContentType.PrefixKind);
        yield return new CraftSelector(CraftSelectorKind.HighestTier, ModContentType.PrefixKind);
    }

    /// <summary>One selection over a fresh copy, as the indexes it resolved to.</summary>
    static int[] Resolve(
        CraftWorld world,
        byte[] stored,
        CraftSelector selector,
        out CraftRefusal? refusal,
        IRandomSource? random = null)
    {
        CraftWorkingCopy copy = world.Open(stored);
        Span<int> indexes = stackalloc int[CraftWorkingCopy.MaxAffixes];
        refusal = selector.Resolve(
            ref copy,
            InstancePropertyKind.Affixes,
            random ?? new ScriptedRandomSource([]),
            indexes,
            out int count);
        return indexes[..count].ToArray();
    }

    /// <summary>The mod ids of one stored payload, in the order it holds them.</summary>
    static int[] Mods(CraftWorld world, byte[] stored)
    {
        CraftWorkingCopy copy = world.Open(stored);
        var entries = new InstanceAffix[CraftWorkingCopy.MaxAffixes];
        int count = copy.ReadAffixes(InstancePropertyKind.Affixes, entries);
        var mods = new int[count];
        for (int index = 0; index < count; index++)
        {
            mods[index] = entries[index].ModId;
        }

        return mods;
    }

    /// <summary>One scalar field's value, read off the encoded bytes.</summary>
    static ulong Scalar(ReadOnlyMemory<byte> payload, ushort kind)
    {
        int offset = 0;
        Assert.True(ContentVarint.TryReadUInt64(Body(payload, kind).Span, ref offset, out ulong value, out _));
        return value;
    }
}

/// <summary>
/// The two authored worlds the guard and standing rule facts read: a rarity chain whose ids deliberately
/// disagree with its order, and the generation world plus one legacy mod.
/// <para>
/// Both are authored HERE rather than shipped, and every instance is its own, so nothing here writes
/// process-global state.
/// </para>
/// </summary>
internal static class CraftGuardWorld
{
    /// <summary>The rarity above rare, numbered LATE so an id comparison disagrees with the chain.</summary>
    public const int Exalted = 9;

    /// <summary>The rarity above exalted, numbered LOW for the same reason.</summary>
    public const int Mythic = 3;

    /// <summary>A prefix mod row carrying <c>legacy</c>, which is what the frozen entry rule reads.</summary>
    public const int AncientMod = 20;

    /// <summary>A second legacy prefix, for the trim that has nothing left it may remove.</summary>
    public const int ElderMod = 21;

    /// <summary>Kind 1 bit 0, corrupted, which is the standing refusal's own bit.</summary>
    public const int CorruptedBit = 0;

    /// <summary>Kind 1 bit 1, mirrored, which is an ordinary flag a guard reads.</summary>
    public const int MirroredBit = 1;

    /// <summary>The generation world with its rarities re-authored as one four step upgrade chain.</summary>
    public static CraftWorld Chain()
    {
        ContentTypeRegistry registry = GenerationWorld.World();
        List<ContentRow> rows = GenerationWorld.Rows(registry);
        _ = rows.RemoveAll(static row => row.Type.Value == InstanceContentTypeIds.RarityRuleTypeId);
        rows.AddRange(
        [
            RarityRule(registry, MagicRarity, "magic", minAffixes: 1, maxAffixes: 1, maxPrefixes: 1, maxSuffixes: 1),
            RarityRule(
                registry,
                RareRarity,
                "rare",
                minAffixes: 4,
                maxAffixes: 4,
                maxPrefixes: 3,
                maxSuffixes: 3,
                nameWordPositions: 2,
                upgradeFrom: MagicRarity),
            RarityRule(registry, Exalted, "exalted", minAffixes: 5, maxAffixes: 5, maxPrefixes: 3, maxSuffixes: 3, upgradeFrom: RareRarity),
            RarityRule(registry, Mythic, "mythic", minAffixes: 6, maxAffixes: 6, maxPrefixes: 3, maxSuffixes: 3, upgradeFrom: Exalted),
        ]);

        return new CraftWorld(registry, Snapshot(registry, [.. rows]), Greatsword);
    }

    /// <summary>The same four step chain with one of its rules RETIRED, which is what breaks a link.</summary>
    public static CraftWorld ChainWithRetired(int rarityId)
    {
        CraftWorld chain = Chain();
        var rows = new List<ContentRow>();
        foreach (ContentRow row in chain.Snapshot.Rows(new ContentTypeId(InstanceContentTypeIds.RarityRuleTypeId)))
        {
            rows.Add(row.Id == rarityId ? row.WithIdentity(row.Id, isRetired: true) : row);
        }

        foreach (ContentTypeId type in chain.Snapshot.Types)
        {
            if (type.Value != InstanceContentTypeIds.RarityRuleTypeId)
            {
                rows.AddRange(chain.Snapshot.Rows(type));
            }
        }

        return chain with { Snapshot = Snapshot(chain.Registry, [.. rows]) };
    }

    /// <summary>The generation world plus two legacy mods, which no table and no draw can ever reach.</summary>
    public static CraftWorld Legacy()
    {
        ContentTypeRegistry registry = GenerationWorld.World();
        List<ContentRow> rows = GenerationWorld.Rows(registry);
        rows.AddRange(
        [
            Mod(registry, AncientMod, "ancient", kind: ModContentType.PrefixKind, legacy: true),
            Mod(registry, ElderMod, "elder", kind: ModContentType.PrefixKind, legacy: true),
            ModTier(registry, 20, "ancient_t1", modId: AncientMod, ordinal: 1),
            ModTier(registry, 21, "elder_t1", modId: ElderMod, ordinal: 1),
        ]);

        return new CraftWorld(registry, Snapshot(registry, [.. rows]), Greatsword);
    }

    /// <summary>One affix entry, at the bottom of its range unless a fact says otherwise.</summary>
    public static InstanceAffix Affix(int modId, byte tier = 1, ushort position = RollPosition.Bottom)
        => new(modId, tier, position);

    /// <summary>One payload, built through the one encoder the format has.</summary>
    public static byte[] Encoded(Action<ItemInstancePayloadBuilder> author)
    {
        ArgumentNullException.ThrowIfNull(author);
        var builder = new ItemInstancePayloadBuilder();
        author(builder);
        return builder.ToArray();
    }
}
