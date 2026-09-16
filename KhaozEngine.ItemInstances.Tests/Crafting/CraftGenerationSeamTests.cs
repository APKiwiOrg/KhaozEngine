using System;
using System.Collections.Generic;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.ItemInstances.Generation;
using Xunit;
using static KhaozEngine.Tests.ItemInstances.Content.InstanceContentTypeFixtures;
using static KhaozEngine.Tests.ItemInstances.Content.InstanceValidationFixtures;
using static KhaozEngine.Tests.ItemInstances.Generation.GenerationWorld;

namespace KhaozEngine.Tests.ItemInstances.Crafting;

/// <summary>
/// The seam between the craft primitives and the generator they draw through, which is where a property of
/// one half is only true because of the other.
/// <para>
/// Two things live here. The draw COUNT of spec 9.3 is the generator's property and a craft inherits it
/// through the same helper, so a craft whose pool ran dry has to leave the stream where a craft whose pool
/// did not leaves it. And the rarity rule's <c>max_affixes</c> is CONTENT the generator sizes its scratch
/// from, so a craft that would walk past one item's own rule has to be refused at the ask rather than
/// dropped at the write.
/// </para>
/// <para>
/// Every registry and snapshot a fact builds is its own, so nothing here writes process-global state.
/// </para>
/// </summary>
public sealed class CraftGenerationSeamTests
{
    [Fact]
    public void Two_RerollMods_on_one_seed_leave_the_stream_in_the_same_place_dry_pool_or_not()
    {
        // RerollMods runs spec 9.4 steps 4 to 9 through the generator, so it inherits the draw count rule:
        // the number of draws is a function of the pick count and of nothing else. The greatsword's pool is
        // full at every pick and the wand's is empty at all four, and the two crafts have to cost the
        // stream the same, or a seeded replay of a craft session diverges at the first craft that ran dry.
        var full = new CountingSeededRandomSource(915);
        var dry = new CountingSeededRandomSource(915);

        CraftWorld world = World();
        int placed = Reroll(world with { DefinitionId = Greatsword }, full);
        int empty = Reroll(world with { DefinitionId = Wand }, dry);

        Assert.True(placed > 0, "The greatsword craft placed nothing, so the comparison reads nothing.");
        Assert.Equal(0, empty);
        Assert.Equal(full.UnderlyingDraws, dry.UnderlyingDraws);
    }

    [Fact]
    public void AddRandomMod_past_the_raritys_max_affixes_is_REFUSED_rather_than_dropped()
    {
        // Magic permits ONE affix and the generator's affix scratch is sized from the widest rule in the
        // pack, which is rare's four. So a second pick on a magic item has room in the buffer and no right
        // to it: dropped where the scratch is narrow, written where it is not, and an item outside its own
        // rule either way with nothing reported.
        CraftWorld world = World();
        CraftWorkingCopy full = world.Open(world.Payload(60, MagicRarity, [new InstanceAffix(1, 1, RollPosition.Bottom)]));

        Assert.Equal(
            new CraftRefusal(CraftRefusalKind.AffixListFull, InstancePropertyKind.Affixes),
            CraftPrimitives.AddRandomMod(ref full, world.Generator(new SeededRandomSource(11)), ModContentType.SuffixKind, 0));
        Assert.True(full.IsRefused);

        // The same craft on the same item with the list UNDER the ceiling still applies, so what is refused
        // is the ceiling rather than the primitive.
        CraftWorkingCopy room = world.Open(world.Payload(60, MagicRarity, []));
        Assert.Null(CraftPrimitives.AddRandomMod(ref room, world.Generator(new SeededRandomSource(11)), ModContentType.SuffixKind, 0));
        Assert.True(room.TryEncode(out byte[] crafted));
        Assert.Single(GenerationWorld.Affixes(crafted));

        // And an item whose rarity this version carries no rule for keeps kind 131's byte count as its only
        // ceiling, because there is no rule to read one off.
        CraftWorkingCopy unruled = world.Open(world.Payload(60, 0, [new InstanceAffix(1, 1, RollPosition.Bottom)]));
        Assert.Null(CraftPrimitives.AddRandomMod(ref unruled, world.Generator(new SeededRandomSource(11)), ModContentType.SuffixKind, 0));
    }

    /// <summary>One <c>RerollMods</c> over every kind, answering how many affixes it left on the item.</summary>
    static int Reroll(CraftWorld world, IRandomSource random)
    {
        CraftWorkingCopy copy = world.Open(world.Payload(60, RareRarity, []));
        Assert.Null(CraftPrimitives.RerollMods(ref copy, world.Generator(random), ItemGenerator.AllModKinds));
        Assert.True(copy.TryEncode(out byte[] crafted));
        return GenerationWorld.Affixes(crafted).Count;
    }

    /// <summary>The generation world, which already carries a full pool base and an empty pool one.</summary>
    static CraftWorld World()
    {
        ContentTypeRegistry registry = GenerationWorld.World();
        List<ContentRow> rows = GenerationWorld.Rows(registry);
        return new CraftWorld(registry, Snapshot(registry, [.. rows]), Greatsword);
    }
}
