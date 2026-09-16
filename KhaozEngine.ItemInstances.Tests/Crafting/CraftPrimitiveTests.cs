using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
/// One fact per primitive of spec 10.2, plus the four notes that section says an implementer would
/// otherwise guess, plus the spec sentences the plan's twelve do not reach.
/// <para>
/// The world is <c>GenerationWorld</c>'s, extended with the socket tag rules and the <c>upgrade_from</c>
/// chain the craft facts need and the generation facts do not. It is authored HERE rather than shipped,
/// because the engine ships eighteen shapes and no rows. Every registry and snapshot a fact builds is its
/// own, so nothing here writes process-global state.
/// </para>
/// </summary>
public sealed class CraftPrimitiveTests
{
    /// <summary>A socket type accepting the gem tag, which the wand carries.</summary>
    const int GemOnlySocket = 2;

    /// <summary>A socket type with a reject row that WINS over its own accept row.</summary>
    const int PickySocket = 3;

    /// <summary>A socket type with no accept row at all, which therefore accepts nothing.</summary>
    const int SealedSocket = 4;

    /// <summary>A socket type whose nested budget is four bytes.</summary>
    const int TinySocket = 5;

    /// <summary>A gem tagged base carrying the metal tag too, which the picky socket rejects.</summary>
    const int Runestone = 43;

    /// <summary>The nested budget the tiny socket authors.</summary>
    const int TinyBudget = 4;

    /// <summary>
    /// A weighted draw landing inside mod 1's third tier. Metal's prefix bucket runs sharp t1 at 100,
    /// t2 at 150, t3 at 175, heavy at 245 and strong at 335, so 160 is squarely in the third tier's slice
    /// and the ceiling is the only thing that can bring the written ordinal down.
    /// </summary>
    const int TopTierDraw = 160;

    [Fact]
    public void SetRarity_trims_from_the_END_of_the_sorted_list_so_a_replay_needs_no_draw()
    {
        var random = new RecordingRandomSource(new ScriptedRandomSource([]));
        CraftWorld world = World();
        byte[] stored = world.Payload(60, RareRarity, [Affix(1), Affix(2), Affix(3), Affix(4)]);

        CraftWorkingCopy copy = world.Open(stored);
        Assert.Null(CraftPrimitives.SetRarity(ref copy, world.Generator(random), MagicRarity, fill: false));
        Assert.True(copy.TryEncode(out byte[] crafted));

        // Magic permits one affix, so three go, and they go from the sorted END: the lowest mod id stays.
        Assert.Equal([1], Affixes(crafted).Select(static affix => affix.ModId));
        Assert.Empty(random.Calls);
    }

    [Fact]
    public void SetRarity_fills_by_repeating_steps_5_to_8_only_when_the_currency_asked_for_a_fill()
    {
        CraftWorld world = World();
        byte[] stored = world.Payload(60, MagicRarity, [Affix(1)]);

        CraftWorkingCopy held = world.Open(stored);
        Assert.Null(CraftPrimitives.SetRarity(ref held, world.Generator(Source()), RareRarity, fill: false));
        Assert.True(held.TryEncode(out byte[] unfilled));
        Assert.Single(Affixes(unfilled));

        CraftWorkingCopy filled = world.Open(stored);
        Assert.Null(CraftPrimitives.SetRarity(ref filled, world.Generator(Source()), RareRarity, fill: true));
        Assert.True(filled.TryEncode(out byte[] crafted));

        // Rare asks for four at the floor, so a fill repeats steps 5 to 8 three times and no more.
        Assert.Equal(4, Affixes(crafted).Count);
        Assert.Contains(Affixes(crafted), affix => affix.ModId == 1);
    }

    [Fact]
    public void SetRarity_with_parameter_0_walks_upgrade_from_and_has_exactly_one_answer()
    {
        CraftWorld world = World();
        byte[] stored = world.Payload(60, MagicRarity, [Affix(1)]);

        CraftWorkingCopy copy = world.Open(stored);
        Assert.Null(CraftPrimitives.SetRarity(ref copy, world.Generator(Source()), 0, fill: false));
        Assert.True(copy.TryEncode(out byte[] crafted));
        Assert.Equal(RareRarity, Rarity(crafted));

        // A second rule naming the same upgrade_from makes the answer two, which is not exactly one.
        CraftWorld ambiguous = World(registry =>
            [RarityRule(registry, 9, "rare_alt", minAffixes: 1, maxAffixes: 4, upgradeFrom: MagicRarity)]);
        CraftWorkingCopy second = ambiguous.Open(stored);
        CraftRefusal? refusal = CraftPrimitives.SetRarity(ref second, ambiguous.Generator(Source()), 0, fill: false);
        Assert.Equal(new CraftRefusal(CraftRefusalKind.RarityUpgradeAmbiguous, 2), refusal);
        Assert.False(second.TryEncode(out _));
    }

    [Fact]
    public void Socket_and_Unsocket_are_the_only_primitives_that_touch_TWO_slots()
    {
        string[] twoSlotted = [.. ApplyMethods()
            .Where(static method => method.GetParameters()
                .Any(static parameter => parameter.Name is "containedDefinitionId"))
            .Select(static method => method.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)];

        Assert.Equal([nameof(CraftPrimitives.Socket), nameof(CraftPrimitives.Unsocket)], twoSlotted);
    }

    [Fact]
    public void Socket_KEEPS_the_moved_items_instance_id_rather_than_minting_one()
    {
        const ulong moved = 0x0001_0000_0000_002A;
        CraftWorld world = World();
        byte[] stored = world.Payload(60, MagicRarity, [], [GemOnlySocket]);

        CraftWorkingCopy copy = world.Open(stored);
        Assert.Null(CraftPrimitives.Socket(ref copy, 0, Wand, moved, default));
        Assert.True(copy.TryEncode(out byte[] crafted));

        (int definition, ulong instance, int nested) = Socket(crafted, 0);
        Assert.Equal(Wand, definition);
        Assert.Equal(moved, instance);
        Assert.Equal(0, nested);
    }

    [Fact]
    public void AddRandomMod_uses_the_items_OWN_item_level_from_kind_2()
    {
        CraftWorld world = BandedWorld();

        // The low mod's only tier is live to item level 20 and the high mod's from 21, so the mod a pick
        // lands on is decided by kind 2 and by nothing the caller passed in.
        Assert.Equal(LowOnlyMod, DrawnMod(world, itemLevel: 10));
        Assert.Equal(HighOnlyMod, DrawnMod(world, itemLevel: 40));

        // An item carrying no kind 2 has no item level of its OWN, so the pick refuses rather than
        // borrowing the crafter's, which is the whole reason item level is stored per instance.
        CraftWorkingCopy bare = world.Open([]);
        Assert.Equal(
            new CraftRefusal(CraftRefusalKind.FieldAbsent, InstancePropertyKind.ItemLevel),
            CraftPrimitives.AddRandomMod(ref bare, world.Generator(Source()), ModContentType.PrefixKind, 0));
    }

    [Fact]
    public void Identify_sets_state_1_and_the_mask_to_the_REGISTERED_gated_bits_only()
    {
        CraftWorld world = World();
        byte[] stored = world.Payload(60, MagicRarity, [Affix(1)]);

        CraftWorkingCopy copy = world.Open(stored);
        Assert.Null(CraftPrimitives.Identify(ref copy));
        Assert.True(copy.TryEncode(out byte[] crafted));

        (bool identified, uint mask) = Identification(crafted);
        Assert.True(identified);
        Assert.Equal(0b1111u, mask);

        // DERIVED from the registration rather than hardcoded: a fifth gated kind moves the answer, and
        // spec 12.7's "all ones" never does, because bits 4 to 31 are unassigned and stay zero in v1.
        InstancePropertyRegistry wider = InstancePropertyRegistry.CreateV1();
        wider.Register(
            InstanceKindBand.Game,
            InstancePropertyKind.FirstGameKind,
            InstancePropertyCodec.ShapeOnly,
            PropertyVisibility.Everyone,
            4,
            new InstanceFieldShape(OneVarint, InstanceCountWidth.None, default),
            ReadOnlySpan<InstanceReferenceTarget>.Empty);

        CraftWorkingCopy game = CraftWorkingCopy.Open(wider, world.Snapshot, Greatsword, stored);
        Assert.Null(CraftPrimitives.Identify(ref game));
        Assert.True(game.TryEncode(out byte[] widened));
        Assert.Equal(0b1_1111u, Identification(widened).Mask);
    }

    [Fact]
    public void SetFlag_refuses_a_bit_above_2_in_v1()
    {
        CraftWorld world = World();
        byte[] stored = world.Payload(60, MagicRarity, [Affix(1)]);

        for (int bit = 0; bit <= 2; bit++)
        {
            CraftWorkingCopy legal = world.Open(stored);
            Assert.Null(CraftPrimitives.SetFlag(ref legal, bit, value: true));
            Assert.True(legal.TryEncode(out byte[] crafted));
            Assert.Equal(1u << bit, Scalar(crafted, InstancePropertyKind.Flags));
        }

        CraftWorkingCopy reserved = world.Open(stored);
        Assert.Equal(
            new CraftRefusal(CraftRefusalKind.FlagBitReserved, 3),
            CraftPrimitives.SetFlag(ref reserved, 3, value: true));
        Assert.False(reserved.TryEncode(out _));
    }

    [Fact]
    public void Repair_with_amount_0_raises_kind_5_current_to_its_maximum_and_no_further()
    {
        CraftWorld world = World();
        byte[] stored = world.Durable(current: 30, maximum: 120);

        CraftWorkingCopy full = world.Open(stored);
        Assert.Null(CraftPrimitives.Repair(ref full, 0));
        Assert.True(full.TryEncode(out byte[] repaired));
        Assert.Equal((120ul, 120ul), Pair(repaired, InstancePropertyKind.Durability));

        // An amount walks toward the maximum and stops there, so no craft can raise the ceiling.
        CraftWorkingCopy partial = world.Open(stored);
        Assert.Null(CraftPrimitives.Repair(ref partial, 1000));
        Assert.True(partial.TryEncode(out byte[] capped));
        Assert.Equal((120ul, 120ul), Pair(capped, InstancePropertyKind.Durability));

        CraftWorkingCopy none = world.Open(world.Payload(60, MagicRarity, [Affix(1)]));
        Assert.Equal(
            new CraftRefusal(CraftRefusalKind.FieldAbsent, InstancePropertyKind.Durability),
            CraftPrimitives.Repair(ref none, 0));
    }

    [Fact]
    public void A_socket_primitive_refuses_a_contained_item_the_socket_types_tag_rules_reject()
    {
        CraftWorld world = World();

        // The picky socket accepts gem and rejects metal, and the runestone carries both. Reject is
        // checked FIRST and wins, so an item carrying an accepted tag is still refused.
        CraftWorkingCopy picky = world.Open(world.Payload(60, MagicRarity, [], [PickySocket]));
        Assert.Equal(
            new CraftRefusal(CraftRefusalKind.SocketTagRejected, PickySocket),
            CraftPrimitives.Socket(ref picky, 0, Runestone, 0, default));

        // The wand carries gem alone, so the same socket takes it.
        CraftWorkingCopy accepted = world.Open(world.Payload(60, MagicRarity, [], [PickySocket]));
        Assert.Null(CraftPrimitives.Socket(ref accepted, 0, Wand, 0, default));
    }

    [Fact]
    public void A_socket_type_with_NO_accept_row_accepts_NOTHING()
    {
        CraftWorld world = World();

        CraftWorkingCopy copy = world.Open(world.Payload(60, MagicRarity, [], [SealedSocket]));
        Assert.Equal(
            new CraftRefusal(CraftRefusalKind.SocketTagRejected, SealedSocket),
            CraftPrimitives.Socket(ref copy, 0, Wand, 0, default));

        // A socket type of 0 is NO RESTRICTION, which is a different statement and the only one that
        // reads an empty rule set as permissive.
        CraftWorkingCopy free = world.Open(world.Payload(60, MagicRarity, [], [0]));
        Assert.Null(CraftPrimitives.Socket(ref free, 0, Wand, 0, default));
    }

    [Fact]
    public void A_socket_primitive_refuses_a_nested_payload_above_socket_type_max_nested_bytes()
    {
        CraftWorld world = World();
        byte[] nested = Nested(builder => builder
            .AddScalar(InstancePropertyKind.ItemLevel, 60)
            .AddScalar(InstancePropertyKind.Quality, 20));
        Assert.True(nested.Length > TinyBudget);

        CraftWorkingCopy tiny = world.Open(world.Payload(60, MagicRarity, [], [TinySocket]));
        Assert.Equal(
            new CraftRefusal(CraftRefusalKind.NestedPayloadTooLong, TinyBudget),
            CraftPrimitives.Socket(ref tiny, 0, Wand, 0, nested));

        // A budget of 0 in content MEANS the whole payload cap, never no nesting at all.
        CraftWorkingCopy whole = world.Open(world.Payload(60, MagicRarity, [], [GemOnlySocket]));
        Assert.Null(CraftPrimitives.Socket(ref whole, 0, Wand, 0, nested));
    }

    [Fact]
    public void A_nested_payload_is_ONE_LEVEL_and_a_socket_inside_a_socket_is_refused()
    {
        CraftWorld world = World();
        byte[] nested = Nested(builder => builder.AddSockets([new InstanceSocket(GemOnlySocket, 0, 0, default)]));

        CraftWorkingCopy copy = world.Open(world.Payload(60, MagicRarity, [], [GemOnlySocket]));
        Assert.Equal(
            new CraftRefusal(CraftRefusalKind.NestedPayloadNests, InstancePropertyKind.Sockets),
            CraftPrimitives.Socket(ref copy, 0, Wand, 0, nested));
        Assert.False(copy.TryEncode(out _));
    }

    [Fact]
    public void Every_one_of_the_fourteen_primitives_has_exactly_one_static_apply_method()
    {
        string[] named = [.. Enum.GetNames<CraftPrimitive>().OrderBy(static name => name, StringComparer.Ordinal)];
        string[] applied = [.. ApplyMethods()
            .Select(static method => method.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)];

        Assert.Equal(14, named.Length);
        Assert.Equal(named, applied);
    }

    [Fact]
    public void AddRandomMod_with_a_tier_ceiling_never_writes_a_tier_above_it()
    {
        CraftWorld world = World();

        // Mod 1 carries three tiers and the draw is scripted onto its top one, so the ceiling is the only
        // thing that can bring the written ordinal down.
        CraftWorkingCopy free = world.Open(world.Payload(60, MagicRarity, []));
        Assert.Null(CraftPrimitives.AddRandomMod(ref free, world.Generator(Draw(TopTierDraw)), ModContentType.PrefixKind, 0));
        Assert.True(free.TryEncode(out byte[] uncapped));
        Assert.Equal(3, Affixes(uncapped)[0].Tier);

        CraftWorkingCopy capped = world.Open(world.Payload(60, MagicRarity, []));
        Assert.Null(CraftPrimitives.AddRandomMod(ref capped, world.Generator(Draw(TopTierDraw)), ModContentType.PrefixKind, 1));
        Assert.True(capped.TryEncode(out byte[] crafted));
        Assert.Equal(1, Affixes(crafted)[0].Tier);
    }

    [Fact]
    public void RerollValues_rewrites_the_position_and_leaves_the_mod_and_the_tier_alone()
    {
        CraftWorld world = World();
        byte[] stored = world.Payload(60, RareRarity, [Affix(1, tier: 2), Affix(2), Affix(4)]);
        var random = new RecordingRandomSource(new ScriptedRandomSource([], [1234, 4321]));

        CraftWorkingCopy copy = world.Open(stored);
        Assert.Null(CraftPrimitives.RerollValues(ref copy, random, [0, 2]));
        Assert.True(copy.TryEncode(out byte[] crafted));

        List<InstanceAffix> affixes = Affixes(crafted);
        Assert.Equal([1, 2, 4], affixes.Select(static affix => affix.ModId));
        Assert.Equal([2, 1, 1], affixes.Select(static affix => (int)affix.Tier));
        Assert.Equal([1234, RollPosition.Bottom, 4321], affixes.Select(static affix => (int)affix.Position));
        Assert.Equal(["position", "position"], random.Calls);
    }

    [Fact]
    public void RemoveMod_and_RemoveEnchant_take_their_entries_from_the_SORTED_list()
    {
        CraftWorld world = World();
        byte[] stored = world.Payload(60, RareRarity, [Affix(4), Affix(1), Affix(2)], [], [Affix(3), Affix(5)]);

        CraftWorkingCopy copy = world.Open(stored);
        Assert.Null(CraftPrimitives.RemoveMod(ref copy, [1]));
        Assert.Null(CraftPrimitives.RemoveEnchant(ref copy, [0]));
        Assert.True(copy.TryEncode(out byte[] crafted));

        Assert.Equal([1, 4], Affixes(crafted).Select(static affix => affix.ModId));
        Assert.Equal([5], Enchantments(crafted).Select(static affix => affix.ModId));

        CraftWorkingCopy past = world.Open(stored);
        Assert.Equal(
            new CraftRefusal(CraftRefusalKind.AffixAbsent, 9),
            CraftPrimitives.RemoveMod(ref past, [9]));
    }

    [Fact]
    public void RerollMods_re_runs_steps_4_to_9_for_the_masked_kinds_and_leaves_the_others()
    {
        CraftWorld world = World();
        byte[] stored = world.Payload(60, RareRarity, [Affix(1), Affix(2), Affix(3), Affix(4)]);

        CraftWorkingCopy copy = world.Open(stored);
        Assert.Null(CraftPrimitives.RerollMods(ref copy, world.Generator(Source()), 1u << (ModContentType.PrefixKind - 1)));
        Assert.True(copy.TryEncode(out byte[] crafted));

        // Mods 2 and 4 are suffixes, so they are untouched. Mods 1 and 3 are prefixes and were discarded
        // before the redraw, so the list is theirs to lose.
        List<int> ids = [.. Affixes(crafted).Select(static affix => affix.ModId)];
        Assert.Contains(2, ids);
        Assert.Contains(4, ids);
        foreach (int id in ids)
        {
            Assert.Contains(Kind(world, id), new[] { ModContentType.PrefixKind, ModContentType.SuffixKind });
        }
    }

    [Fact]
    public void ApplyEnchant_writes_kind_133_at_the_BOTTOM_of_the_range_because_it_draws_nothing()
    {
        CraftWorld world = World();
        byte[] stored = world.Payload(60, MagicRarity, [Affix(1)]);

        CraftWorkingCopy copy = world.Open(stored);
        Assert.Null(CraftPrimitives.ApplyEnchant(ref copy, 2, 1));
        Assert.True(copy.TryEncode(out byte[] crafted));

        InstanceAffix written = Assert.Single(Enchantments(crafted));
        Assert.Equal(new InstanceAffix(2, 1, RollPosition.Bottom), written);

        // A mod this version has no live row for is a content refusal rather than a payload the validator
        // would quarantine later.
        CraftWorkingCopy missing = world.Open(stored);
        Assert.Equal(
            new CraftRefusal(CraftRefusalKind.ContentRowMissing, 99),
            CraftPrimitives.ApplyEnchant(ref missing, 99, 1));
    }

    [Fact]
    public void AddSocket_appends_an_EMPTY_socket_and_keeps_the_AUTHORED_order()
    {
        CraftWorld world = World();
        byte[] stored = world.Payload(60, MagicRarity, [], [GemOnlySocket, PickySocket]);

        CraftWorkingCopy copy = world.Open(stored);
        Assert.Null(CraftPrimitives.AddSocket(ref copy, SealedSocket));
        Assert.True(copy.TryEncode(out byte[] crafted));

        Assert.Equal([GemOnlySocket, PickySocket, SealedSocket], SocketTypes(crafted));
        Assert.Equal((0, 0ul, 0), Socket(crafted, 2));

        CraftWorkingCopy missing = world.Open(stored);
        Assert.Equal(
            new CraftRefusal(CraftRefusalKind.ContentRowMissing, 99),
            CraftPrimitives.AddSocket(ref missing, 99));
    }

    [Fact]
    public void Unsocket_hands_the_contained_item_back_with_its_instance_id()
    {
        const ulong moved = 77;
        CraftWorld world = World();
        byte[] nested = Nested(builder => builder.AddScalar(InstancePropertyKind.ItemLevel, 12));

        CraftWorkingCopy copy = world.Open(world.Payload(60, MagicRarity, [], [GemOnlySocket]));
        Assert.Null(CraftPrimitives.Socket(ref copy, 0, Wand, moved, nested));
        Assert.Null(CraftPrimitives.Unsocket(ref copy, 0, out int definition, out ulong instance, out byte[] payload));
        Assert.True(copy.TryEncode(out byte[] crafted));

        Assert.Equal(Wand, definition);
        Assert.Equal(moved, instance);
        Assert.Equal(nested, payload);
        Assert.Equal((0, 0ul, 0), Socket(crafted, 0));

        CraftWorkingCopy empty = world.Open(world.Payload(60, MagicRarity, [], [GemOnlySocket]));
        Assert.Equal(
            new CraftRefusal(CraftRefusalKind.SocketEmpty, 0),
            CraftPrimitives.Unsocket(ref empty, 0, out _, out _, out _));
    }

    [Fact]
    public void SetQuality_writes_kind_3_as_a_delta_or_as_an_absolute()
    {
        CraftWorld world = World();
        byte[] stored = world.Payload(60, MagicRarity, [Affix(1)], [], [], quality: 12);

        CraftWorkingCopy delta = world.Open(stored);
        Assert.Null(CraftPrimitives.SetQuality(ref delta, 8, absolute: false));
        Assert.True(delta.TryEncode(out byte[] raised));
        Assert.Equal(20ul, Scalar(raised, InstancePropertyKind.Quality));

        CraftWorkingCopy exact = world.Open(stored);
        Assert.Null(CraftPrimitives.SetQuality(ref exact, 5, absolute: true));
        Assert.True(exact.TryEncode(out byte[] set));
        Assert.Equal(5ul, Scalar(set, InstancePropertyKind.Quality));

        // A delta walking off the bottom clamps, and a quality of 0 drops the field, so a crafted item
        // stacks with one that never had quality at all.
        CraftWorkingCopy stripped = world.Open(stored);
        Assert.Null(CraftPrimitives.SetQuality(ref stripped, -100, absolute: false));
        Assert.True(stripped.TryEncode(out byte[] bare));
        Assert.DoesNotContain(InstancePropertyKind.Quality, Fields(bare).Select(static field => field.Kind));
    }

    /// <summary>The fourteen static apply methods, which is the surface the closed enum promises.</summary>
    static IEnumerable<MethodInfo> ApplyMethods()
        => typeof(CraftPrimitives).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(static method => method.GetParameters() is [{ ParameterType.IsByRef: true }, ..]);

    /// <summary>A source that answers the bottom of every range, which is enough for a fact about shape.</summary>
    static IRandomSource Source() => new ScriptedRandomSource([]);

    /// <summary>A source whose one weighted draw lands on a chosen entry of the pool.</summary>
    static IRandomSource Draw(int value) => new ScriptedRandomSource([value]);

    /// <summary>One affix on a mod, at the bottom of its range unless a fact says otherwise.</summary>
    static InstanceAffix Affix(int modId, byte tier = 1, ushort position = RollPosition.Bottom)
        => new(modId, tier, position);

    /// <summary>One mod row's authored kind, read the way the primitives read it.</summary>
    static int Kind(CraftWorld world, int modId)
    {
        Assert.True(world.Snapshot.TryGetRow(new ContentTypeId(InstanceContentTypeIds.ModTypeId), modId, out ContentRow? row));
        return (int)(row.Fields[ModContentType.KindIndex].Number);
    }

    /// <summary>The mod whose only tier is live below item level 21.</summary>
    const int LowOnlyMod = 10;

    /// <summary>The mod whose only tier is live from item level 21 up.</summary>
    const int HighOnlyMod = 11;

    /// <summary>The mod one pick placed on an item of that level, which is the fact's whole question.</summary>
    static int DrawnMod(CraftWorld world, int itemLevel)
    {
        CraftWorkingCopy copy = world.Open(world.Payload(itemLevel, MagicRarity, []));
        Assert.Null(CraftPrimitives.AddRandomMod(ref copy, world.Generator(Source()), ModContentType.PrefixKind, 0));
        Assert.True(copy.TryEncode(out byte[] crafted));
        return Assert.Single(Affixes(crafted)).ModId;
    }

    /// <summary>The generation world plus the socket rules and the upgrade chain a craft needs.</summary>
    static CraftWorld World(Func<ContentTypeRegistry, IEnumerable<ContentRow>>? extra = null)
    {
        ContentTypeRegistry registry = GenerationWorld.World();
        List<ContentRow> rows = GenerationWorld.Rows(registry);
        _ = rows.RemoveAll(row =>
            row.Type == new ContentTypeId(InstanceContentTypeIds.RarityRuleTypeId) && row.Id == RareRarity);
        rows.AddRange(
        [
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
            Base(registry, Runestone, "runestone", [GemTag, MetalTag], durabilityMax: 0, socketMax: 0),
            SocketType(registry, GemOnlySocket, "gem_only_socket"),
            SocketTagRule(registry, 10, "gem_only_accepts_gem", socketTypeId: GemOnlySocket, tagId: GemTag),
            SocketType(registry, PickySocket, "picky_socket"),
            SocketTagRule(registry, 11, "picky_accepts_gem", socketTypeId: PickySocket, tagId: GemTag),
            SocketTagRule(
                registry,
                12,
                "picky_rejects_metal",
                socketTypeId: PickySocket,
                tagId: MetalTag,
                rule: SocketTagRuleContentType.RuleReject),
            SocketType(registry, SealedSocket, "sealed_socket"),
            SocketType(registry, TinySocket, "tiny_socket", maxNestedBytes: TinyBudget),
            SocketTagRule(registry, 13, "tiny_accepts_gem", socketTypeId: TinySocket, tagId: GemTag),
        ]);

        if (extra is not null)
        {
            rows.AddRange(extra(registry));
        }

        return new CraftWorld(registry, Snapshot(registry, [.. rows]), Greatsword);
    }

    /// <summary>A world whose two mods split at item level 21, so a pick's answer names the level it read.</summary>
    static CraftWorld BandedWorld()
    {
        ContentTypeRegistry registry = GenerationWorld.World();
        List<ContentRow> rows =
        [
            Tag(registry, MetalTag, "metal"),
            Stat(registry, 3, "attack"),
            Base(registry, Dagger, "dagger", [MetalTag], durabilityMax: 0, socketMax: 0),
            Mod(registry, LowOnlyMod, "low_only", kind: ModContentType.PrefixKind),
            Mod(registry, HighOnlyMod, "high_only", kind: ModContentType.PrefixKind),
            ModTier(registry, 1, "low_only_t1", modId: LowOnlyMod, ordinal: 1, itemLevelMin: 1, itemLevelMax: 20),
            ModTier(registry, 2, "high_only_t1", modId: HighOnlyMod, ordinal: 1, itemLevelMin: 21),
            ModTierWeight(registry, 1, "low_only_metal", tierId: 1, tagId: MetalTag, weight: 100),
            ModTierWeight(registry, 2, "high_only_metal", tierId: 2, tagId: MetalTag, weight: 100),
            RarityRule(registry, MagicRarity, "magic", minAffixes: 1, maxAffixes: 1, maxPrefixes: 1, maxSuffixes: 1),
            RarityWeight(registry, 1, "magic_metal", rarityId: MagicRarity, tagId: MetalTag, weight: 1000),
        ];

        return new CraftWorld(registry, Snapshot(registry, [.. rows]), Dagger);
    }

    /// <summary>Kind 130's rarity ordinal.</summary>
    static int Rarity(ReadOnlyMemory<byte> payload) => Body(payload, InstancePropertyKind.Rarity).Span[0];

    /// <summary>One scalar field's value.</summary>
    static ulong Scalar(ReadOnlyMemory<byte> payload, ushort kind)
    {
        int offset = 0;
        Assert.True(ContentVarint.TryReadUInt64(Body(payload, kind).Span, ref offset, out ulong value, out _));
        return value;
    }

    /// <summary>One two varint field's values.</summary>
    static (ulong First, ulong Second) Pair(ReadOnlyMemory<byte> payload, ushort kind)
    {
        ReadOnlySpan<byte> body = Body(payload, kind).Span;
        int offset = 0;
        Assert.True(ContentVarint.TryReadUInt64(body, ref offset, out ulong first, out _));
        Assert.True(ContentVarint.TryReadUInt64(body, ref offset, out ulong second, out _));
        return (first, second);
    }

    /// <summary>Kind 128's state and mask.</summary>
    static (bool Identified, uint Mask) Identification(ReadOnlyMemory<byte> payload)
    {
        ReadOnlySpan<byte> body = Body(payload, InstancePropertyKind.Identification).Span;
        int offset = 1;
        Assert.True(ContentVarint.TryRead(body, ref offset, out uint mask, out _));
        return (body[0] == 1, mask);
    }

    /// <summary>One socket's contained definition, instance id and nested length.</summary>
    static (int Definition, ulong Instance, int Nested) Socket(ReadOnlyMemory<byte> payload, int index)
    {
        ReadOnlySpan<byte> body = Body(payload, InstancePropertyKind.Sockets).Span;
        int offset = 0;
        Assert.True(ContentVarint.TryRead(body, ref offset, out uint count, out _));
        Assert.True(index < count);
        for (int entry = 0; entry <= index; entry++)
        {
            Assert.True(ContentVarint.TryRead(body, ref offset, out _, out _));
            Assert.True(ContentVarint.TryRead(body, ref offset, out uint definition, out _));
            Assert.True(ContentVarint.TryReadUInt64(body, ref offset, out ulong instance, out _));
            Assert.True(ContentVarint.TryRead(body, ref offset, out uint nested, out _));
            if (entry == index)
            {
                return ((int)definition, instance, (int)nested);
            }

            offset += (int)nested;
        }

        throw new InvalidOperationException();
    }

    /// <summary>Kind 133's entries, which share kind 131's layout.</summary>
    static List<InstanceAffix> Enchantments(ReadOnlyMemory<byte> payload)
    {
        var registry = InstancePropertyRegistry.CreateV1();
        CraftWorkingCopy copy = CraftWorkingCopy.Open(registry, EmptySnapshot(), Greatsword, payload.Span);
        var entries = new InstanceAffix[CraftWorkingCopy.MaxAffixes];
        int count = copy.ReadAffixes(InstancePropertyKind.Enchantments, entries);
        return [.. entries.AsSpan(0, count).ToArray()];
    }

    /// <summary>A snapshot carrying nothing, for a reader that only needs the payload codec.</summary>
    static ContentSnapshot EmptySnapshot() => Snapshot(GenerationWorld.World());

    /// <summary>One nested payload, built through the one encoder the format has.</summary>
    static byte[] Nested(Action<ItemInstancePayloadBuilder> author)
    {
        var builder = new ItemInstancePayloadBuilder();
        author(builder);
        return builder.ToArray();
    }

    /// <summary>The one varint header shape the widened registration needs.</summary>
    static readonly InstanceSlotKind[] OneVarint = [InstanceSlotKind.Varint];
}

/// <summary>
/// One authored world and the target it crafts on, so a fact reads as its primitive rather than as five
/// lines of construction. It holds no process state and every instance is its own.
/// </summary>
/// <param name="Registry">The content types the rows were authored against.</param>
/// <param name="Snapshot">The published version the craft reads.</param>
/// <param name="DefinitionId">The base the target is made from.</param>
internal sealed record CraftWorld(ContentTypeRegistry Registry, ContentSnapshot Snapshot, int DefinitionId)
{
    /// <summary>A working copy over one stored payload, on the v1 property kinds.</summary>
    public CraftWorkingCopy Open(ReadOnlySpan<byte> payload)
        => CraftWorkingCopy.Open(InstancePropertyRegistry.CreateV1(), Snapshot, DefinitionId, payload);

    /// <summary>A generator over this world's tables, on the source the fact hands in.</summary>
    public ItemGenerator Generator(IRandomSource random)
        => new(
            ModCandidateTables.Build(Snapshot),
            Snapshot,
            random,
            GenerationWorld.FreshAllocator());

    /// <summary>One target payload, through the one encoder the format has.</summary>
    public byte[] Payload(
        int itemLevel,
        int rarityId,
        IReadOnlyList<InstanceAffix> affixes,
        IReadOnlyList<int>? socketTypes = null,
        IReadOnlyList<InstanceAffix>? enchantments = null,
        int quality = 0)
    {
        var builder = new ItemInstancePayloadBuilder();
        _ = builder.AddScalar(InstancePropertyKind.ItemLevel, (uint)itemLevel);
        if (quality > 0)
        {
            _ = builder.AddScalar(InstancePropertyKind.Quality, (uint)quality);
        }

        _ = builder.AddIdentification(identified: false, revealedMask: 0);
        _ = builder.AddByte(InstancePropertyKind.Rarity, (byte)rarityId);
        if (affixes.Count > 0)
        {
            _ = builder.AddAffixes(InstancePropertyKind.Affixes, [.. affixes]);
        }

        if (socketTypes is { Count: > 0 })
        {
            var sockets = new InstanceSocket[socketTypes.Count];
            for (int index = 0; index < sockets.Length; index++)
            {
                sockets[index] = new InstanceSocket(socketTypes[index], 0, 0, default);
            }

            _ = builder.AddSockets(sockets);
        }

        if (enchantments is { Count: > 0 })
        {
            _ = builder.AddAffixes(InstancePropertyKind.Enchantments, [.. enchantments]);
        }

        return builder.ToArray();
    }

    /// <summary>One target carrying durability and nothing else a repair reads.</summary>
    public byte[] Durable(int current, int maximum)
    {
        var builder = new ItemInstancePayloadBuilder();
        _ = builder.AddScalar(InstancePropertyKind.ItemLevel, 60);
        _ = builder.AddScalars(InstancePropertyKind.Durability, (uint)current, (uint)maximum);
        return builder.ToArray();
    }
}
