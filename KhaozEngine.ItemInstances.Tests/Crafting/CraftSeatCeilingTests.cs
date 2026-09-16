using System;
using System.Collections.Generic;
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
/// What happens to a craft on an item carrying MORE affixes than the pack's widest LIVE rarity rule
/// allows, which is the ordinary state of an item generated under a rule a later version retired.
/// <para>
/// <b>The generator's exclusion list is sized from that widest rule</b>
/// (<see cref="GenerationTables"/>), and seating the item's present affixes is what fills it. A draw on
/// an item past the ceiling used to throw <c>InvalidOperationException</c> out of a gameplay call where
/// every other failure is a <see cref="CraftRefusal"/>, because the rule ceiling check ahead of it reads
/// <c>AffixCeiling</c>, which answers 0 for a rarity this version has no LIVE rule for and is therefore
/// skipped.
/// </para>
/// <para>
/// Every registry and snapshot a fact builds is its OWN, so nothing here writes process-global state and no
/// <c>DisableParallelization</c> collection is needed.
/// </para>
/// </summary>
public sealed class CraftSeatCeilingTests
{
    /// <summary>The first of three extra PREFIX mods, so one tag table holds more mods than it can seat.</summary>
    const int WideMod = 30;

    [Fact]
    public void AddRandomMod_on_an_item_over_the_packs_widest_rule_REFUSES_rather_than_throwing()
    {
        CraftWorld world = RetiredRuleWorld();
        byte[] stored = Stored(world);

        CraftWorkingCopy copy = world.Open(stored);
        Assert.Equal(
            new CraftRefusal(CraftRefusalKind.AffixListFull, InstancePropertyKind.Affixes),
            CraftPrimitives.AddRandomMod(ref copy, world.Generator(Source()), ModContentType.PrefixKind, 0));
        Assert.False(copy.TryEncode(out byte[] crafted));
        Assert.Empty(crafted);
    }

    [Fact]
    public void RerollMods_on_the_same_item_refuses_before_the_redraw()
    {
        CraftWorld world = RetiredRuleWorld();
        byte[] stored = Stored(world);

        // The mask keeps every prefix, so the KEPT list is what would be seated and it is the same four.
        CraftWorkingCopy copy = world.Open(stored);
        Assert.Equal(
            new CraftRefusal(CraftRefusalKind.AffixListFull, InstancePropertyKind.Affixes),
            CraftPrimitives.RerollMods(ref copy, world.Generator(Source()), 1u << (ModContentType.SuffixKind - 1)));
        Assert.False(copy.TryEncode(out _));
    }

    [Fact]
    public void The_generator_answers_rather_than_throwing_for_a_present_list_it_cannot_seat()
    {
        CraftWorld world = RetiredRuleWorld();
        ItemGenerator generator = world.Generator(Source());
        Assert.Equal(1, generator.PresentCeiling);

        InstanceAffix[] present =
        [
            new InstanceAffix(1, 1, RollPosition.Bottom),
            new InstanceAffix(WideMod, 1, RollPosition.Bottom),
            new InstanceAffix(WideMod + 1, 1, RollPosition.Bottom),
            new InstanceAffix(WideMod + 2, 1, RollPosition.Bottom),
        ];

        Assert.False(generator.TryDrawAffix(Dagger, 60, ModContentType.PrefixKind, 0, present, out InstanceAffix drawn));
        Assert.Equal(default, drawn);

        // The redraw places NOTHING and hands the kept list straight back, which is the same answer in the
        // shape a method returning a count has.
        var destination = new InstanceAffix[byte.MaxValue];
        int total = generator.RedrawAffixes(Dagger, 60, 0, ItemGenerator.AllModKinds, -1, present, destination);
        Assert.Equal(present.Length, total);
        Assert.Equal(present, destination[..total]);

        // A present list INSIDE the ceiling still draws, so what refused is the seat rather than the pool.
        Assert.True(generator.TryDrawAffix(Dagger, 60, ModContentType.PrefixKind, 0, present[..1], out _));
    }

    /// <summary>The item the three facts craft on: four distinct prefixes under a retired rarity rule.</summary>
    static byte[] Stored(CraftWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return world.Payload(
            60,
            RareRarity,
            [Affix(1), Affix(WideMod), Affix(WideMod + 1), Affix(WideMod + 2)]);
    }

    /// <summary>
    /// The generation world with the four affix rule RETIRED, so the widest live rule is magic's one affix,
    /// plus three extra prefix mods so one tag table holds more mods than the run list can hold.
    /// </summary>
    static CraftWorld RetiredRuleWorld()
    {
        ContentTypeRegistry registry = World();
        List<ContentRow> rows = Rows(registry);
        for (int index = 0; index < rows.Count; index++)
        {
            if (rows[index].Type.Value == InstanceContentTypeIds.RarityRuleTypeId && rows[index].Id == RareRarity)
            {
                rows[index] = rows[index].WithIdentity(RareRarity, isRetired: true);
            }
        }

        for (int offset = 0; offset < 3; offset++)
        {
            int modId = WideMod + offset;
            rows.Add(Mod(registry, modId, FormattableString.Invariant($"wide_{offset}"), kind: ModContentType.PrefixKind));
            rows.Add(ModTier(registry, modId, FormattableString.Invariant($"wide_{offset}_t1"), modId: modId, ordinal: 1));
            rows.Add(ModTierWeight(
                registry,
                modId,
                FormattableString.Invariant($"wide_{offset}_metal"),
                tierId: modId,
                tagId: MetalTag,
                weight: 10 + offset));
        }

        return new CraftWorld(registry, Snapshot(registry, [.. rows]), Dagger);
    }

    /// <summary>A source that answers the bottom of every range, which is enough for a fact about shape.</summary>
    static IRandomSource Source() => new ScriptedRandomSource([]);

    /// <summary>One affix entry at the bottom of its range.</summary>
    static InstanceAffix Affix(int modId) => new(modId, 1, RollPosition.Bottom);
}
