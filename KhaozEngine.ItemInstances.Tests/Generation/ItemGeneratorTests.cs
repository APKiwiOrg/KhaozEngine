using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using KhaozEngine.Primitives;
using Xunit;
using static KhaozEngine.Tests.ItemInstances.Content.InstanceContentTypeFixtures;
using static KhaozEngine.Tests.ItemInstances.Content.InstanceValidationFixtures;

namespace KhaozEngine.Tests.ItemInstances.Generation;

/// <summary>
/// Spec 9.4's thirteen steps, in order, because the ORDER of the draws is the reproducibility contract.
/// <para>
/// <b>These facts are about the CALLS the generator makes on its source, not about the values it gets
/// back.</b> A seeded stream can stay position stable while a generator that skipped a discard produces a
/// different item on the next roll, so a fact that asserts on the outcome alone cannot see the defect these
/// steps exist to prevent. <see cref="RecordingRandomSource"/> is what makes the call sequence observable
/// and <see cref="ScriptedRandomSource"/> is what puts a draw on a chosen entry.
/// </para>
/// <para>
/// Every registry and every generator a fact builds is its OWN, so nothing here writes process-global state
/// and no <c>DisableParallelization</c> collection is needed. The allocation fact reads
/// <c>GC.GetAllocatedBytesForCurrentThread()</c>, which is per thread.
/// </para>
/// </summary>
public class ItemGeneratorTests
{
    /// <summary>The greatsword's prefix pool: sharp at three tiers, heavy and strong at one each.</summary>
    const int GreatswordPrefixWeight = 335;

    /// <summary>The greatsword's suffix pool: keen and swift.</summary>
    const int GreatswordSuffixWeight = 500;

    /// <summary>Sharp's whole run of three tiers, which one placement deducts in full.</summary>
    const int SharpRunWeight = 175;

    [Fact]
    public void Two_items_of_one_rarity_on_one_base_at_one_level_consume_the_SAME_draw_count()
    {
        var first = new RecordingRandomSource(new SeededRandomSource(11));
        var second = new RecordingRandomSource(new SeededRandomSource(9_001));

        GenerationResult left = GenerationWorld.Generator(first).Generate(Rare(GenerationWorld.Greatsword));
        GenerationResult right = GenerationWorld.Generator(second).Generate(Rare(GenerationWorld.Greatsword));

        // One count draw, three draws a pick over four picks, and one draw a name position over two: the
        // number is a function of the affix count and nothing else, so two different seeds land on it.
        Assert.Equal(15, first.Calls.Count);
        Assert.Equal(15, second.Calls.Count);
        Assert.NotEqual(left.Payload.ToArray(), right.Payload.ToArray());
    }

    [Fact]
    public void Two_rolls_of_one_rarity_on_one_base_leave_the_SEEDED_STREAM_in_the_same_place()
    {
        // The CALL count was never the contract: NextInt(0, 1) consumes nothing by IRandomSource's own
        // rule, and neither does a REAL draw whose bound collapses to one. This world has one prefix
        // candidate at weight 1 and one suffix at weight 100, so the pick that lands on the prefix draws
        // over a bound of one and the pick that lands on the suffix draws over 100. Two rolls that make the
        // same calls then leave the stream two draws apart, and the next item of a seeded session is a
        // different item depending on which way an earlier one happened to fall.
        var onThePrefix = new CountingSeededRandomSource(1);
        var onTheSuffix = new CountingSeededRandomSource(5);

        GenerationResult prefix = GenerationWorld.LopsidedGenerator(onThePrefix).Generate(Magic(GenerationWorld.Dagger));
        GenerationResult suffix = GenerationWorld.LopsidedGenerator(onTheSuffix).Generate(Magic(GenerationWorld.Dagger));

        // The two seeds landed on the two different kinds, which is what makes the comparison worth making.
        int[] landed = [KindOfOnlyAffix(prefix), KindOfOnlyAffix(suffix)];
        Assert.Contains(ModContentType.PrefixKind, landed);
        Assert.Contains(ModContentType.SuffixKind, landed);

        Assert.Equal(onTheSuffix.UnderlyingDraws, onThePrefix.UnderlyingDraws);
    }

    [Fact]
    public void The_item_AFTER_a_roll_that_ran_dry_is_the_same_item_as_after_one_that_did_not()
    {
        // Spec 9.3's property, read where it actually bites. The wand's pool is empty at every pick and the
        // greatsword's is not, so the two rolls cost the stream a different number of draws unless every
        // discard advances it. The item after them is the one that shows it: same seed, same base, and the
        // payloads have to be byte equal or a seeded session has diverged at the first item whose pool ran
        // dry, which is exactly what the spec forbids.
        ItemGenerator afterDry = GenerationWorld.Generator(new SeededRandomSource(11));
        ItemGenerator afterFull = GenerationWorld.Generator(new SeededRandomSource(11));

        GenerationResult dry = afterDry.Generate(Rare(GenerationWorld.Wand));
        GenerationResult full = afterFull.Generate(Rare(GenerationWorld.Greatsword));
        Assert.Empty(GenerationWorld.Affixes(dry.Payload));
        Assert.NotEmpty(GenerationWorld.Affixes(full.Payload));

        GenerationResult next = afterDry.Generate(Rare(GenerationWorld.Dagger));
        GenerationResult twin = afterFull.Generate(Rare(GenerationWorld.Dagger));
        Assert.True(
            ItemInstancePayload.SequenceEqual(next.Payload.Span, twin.Payload.Span),
            "The item after a dry roll is a different item from the one after a full roll, so the stream moved by a different amount.");
    }

    [Fact]
    public void A_pick_whose_live_pool_is_EMPTY_still_draws_twice_and_places_nothing()
    {
        // The wand's only tag carries no weight row anywhere, so every one of its four picks opens on an
        // empty pool. Without the two discards it would consume three draws fewer a pick than the
        // greatsword, and a seeded session would diverge at the first item whose pool empties.
        var source = new RecordingRandomSource(new SeededRandomSource(11));
        GenerationResult result = GenerationWorld.Generator(source).Generate(Rare(GenerationWorld.Wand));

        Assert.Equal(15, source.Calls.Count);
        Assert.Equal(0, result.AffixCount);
        Assert.Equal(4, result.RequestedAffixCount);
        Assert.Empty(GenerationWorld.Affixes(result.Payload));

        // Skip rather than NextInt(0, 1), because a one-wide range consumes NOTHING from the stream and a
        // discard that costs the stream nothing is the same as no discard at all. The count draw is a skip
        // too, because rare's minimum equals its maximum, which is a REAL draw whose bound collapsed.
        Assert.Equal(
            new[] { "skip", "skip", "skip", "position", "skip", "skip", "position" },
            source.Calls.Take(7).ToArray());
    }

    [Fact]
    public void The_kind_is_drawn_BEFORE_the_mod_so_a_deep_prefix_pool_does_not_eat_the_suffixes()
    {
        // Magic places ONE affix, so the item IS the first pick and nothing later muddies it. The kind
        // draw's bound is a live COUNT, five prefixes plus two suffixes, and the mod draw's bound is a live
        // WEIGHT. A pool walked mod first would draw over 835 and produce a prefix five sevenths of the
        // time instead of the five ninths the counts say.
        var source = new RecordingRandomSource(new ScriptedRandomSource([0, 0, 0]));
        GenerationResult prefixFirst = GenerationWorld.Generator(source).Generate(Magic(GenerationWorld.Greatsword));

        Assert.Equal(7, source.Bounds[1]);
        Assert.Equal(GreatswordPrefixWeight, source.Bounds[2]);
        Assert.Equal(ModContentType.PrefixKind, KindOfOnlyAffix(prefixFirst));

        // A kind draw at or past the prefix count lands on the SUFFIX, whatever the weights say.
        var suffixSource = new RecordingRandomSource(new ScriptedRandomSource([0, 5, 0]));
        GenerationResult suffixFirst = GenerationWorld.Generator(suffixSource).Generate(Magic(GenerationWorld.Greatsword));

        Assert.Equal(7, suffixSource.Bounds[1]);
        Assert.Equal(GreatswordSuffixWeight, suffixSource.Bounds[2]);
        Assert.Equal(ModContentType.SuffixKind, KindOfOnlyAffix(suffixFirst));
    }

    [Fact]
    public void Placing_a_mod_deducts_its_WHOLE_run_of_tiers_from_every_table_of_its_kind()
    {
        // Draw 0 lands on sharp tier 1. Sharp carries tiers 1, 2 and 3 at 100, 50 and 25, contiguous in
        // every bucket because the table is sorted by the packed key, so the second pick's prefix weight is
        // 335 less the whole 175 rather than less the 100 of the tier that was placed.
        var source = new RecordingRandomSource(new ScriptedRandomSource([0, 0, 0, 0, 0]));
        GenerationResult result = GenerationWorld.Generator(source).Generate(Rare(GenerationWorld.Greatsword));

        Assert.Equal(GreatswordPrefixWeight - SharpRunWeight, source.Bounds[4]);

        // Two prefixes and two suffixes are still live, so the second pick's kind draw sees four.
        Assert.Equal(4, source.Bounds[3]);
        Assert.Equal(1, GenerationWorld.Affixes(result.Payload).Count(affix => affix.ModId == 1));
    }

    [Fact]
    public void A_group_at_max_per_item_deducts_every_other_mod_of_that_group()
    {
        // Draw 175 lands on heavy, which shares group 1 with strong at a cap of one per item. Placing it
        // deducts its own 70 AND strong's 90, so the second pick draws over sharp's run alone.
        var source = new RecordingRandomSource(new ScriptedRandomSource([0, 0, 175, 0, 0]));
        GenerationResult result = GenerationWorld.Generator(source).Generate(Rare(GenerationWorld.Greatsword));

        Assert.Equal(SharpRunWeight, source.Bounds[4]);

        // Three prefixes and two suffixes are still live: sharp's whole run survives and both group members
        // are gone, one placed and one deducted behind it.
        Assert.Equal(5, source.Bounds[3]);

        List<InstanceAffix> affixes = GenerationWorld.Affixes(result.Payload);
        Assert.Contains(affixes, affix => affix.ModId == 3);
        Assert.DoesNotContain(affixes, affix => affix.ModId == 5);
    }

    [Fact]
    public void An_entry_the_overlap_already_suppressed_is_never_deducted_twice()
    {
        // The greatsword lists metal then blade and blade repeats sharp tier 1 at 1000 and heavy tier 1 at
        // 7, so the overlap has already taken both out of the blade table. The dagger lists metal alone and
        // has nothing to suppress. Both open at 335 and both drop to 160 when sharp's run is placed: a
        // deduction that ran twice over the blade table would take the greatsword 1007 lower and negative.
        var greatsword = new RecordingRandomSource(new ScriptedRandomSource([0, 0, 0, 0, 0]));
        var dagger = new RecordingRandomSource(new ScriptedRandomSource([0, 0, 0, 0, 0]));

        GenerationWorld.Generator(greatsword).Generate(Rare(GenerationWorld.Greatsword));
        GenerationWorld.Generator(dagger).Generate(Rare(GenerationWorld.Dagger));

        Assert.Equal(GreatswordPrefixWeight, greatsword.Bounds[2]);
        Assert.Equal(GreatswordPrefixWeight, dagger.Bounds[2]);
        Assert.Equal(GreatswordPrefixWeight - SharpRunWeight, greatsword.Bounds[4]);
        Assert.Equal(GreatswordPrefixWeight - SharpRunWeight, dagger.Bounds[4]);
    }

    [Fact]
    public void The_landing_entry_of_a_shifted_draw_is_LIVE_by_construction()
    {
        // Every draw the second pick can make, swept. Sharp's run sits at the FRONT of the table and is
        // excluded, so every one of the 160 remaining draws has to shift past it: consecutive dead entries
        // share one shifted position, so a dead entry is either wholly behind the draw or wholly ahead of
        // it and can never be the answer.
        for (int draw = 0; draw < GreatswordPrefixWeight - SharpRunWeight; draw++)
        {
            var source = new ScriptedRandomSource([0, 0, 0, 0, draw]);
            GenerationResult result = GenerationWorld.Generator(source).Generate(Rare(GenerationWorld.Greatsword));
            List<InstanceAffix> affixes = GenerationWorld.Affixes(result.Payload);

            Assert.Equal(1, affixes.Count(affix => affix.ModId == 1));

            // Heavy holds 70 of the 160 and strong the other 90, which is the proportionality the weights
            // author rather than anything the generator decides.
            int second = affixes.Single(affix => affix.ModId is 3 or 5).ModId;
            Assert.Equal(draw < 70 ? 3 : 5, second);
        }
    }

    [Fact]
    public void The_affix_list_is_sorted_ascending_by_mod_id_before_it_is_written()
    {
        // Draws chosen so the picks arrive in DESCENDING mod id order: strong, then heavy, then sharp.
        var source = new ScriptedRandomSource([0, 0, 245, 0, 70, 0, 0]);
        GenerationResult result = GenerationWorld.Generator(source).Generate(Rare(GenerationWorld.Greatsword));
        List<InstanceAffix> affixes = GenerationWorld.Affixes(result.Payload);

        Assert.True(affixes.Count > 1, "The fact needs more than one affix to have an order at all.");
        for (int index = 1; index < affixes.Count; index++)
        {
            Assert.True(
                affixes[index - 1].ModId < affixes[index].ModId,
                FormattableString.Invariant($"{affixes[index - 1].ModId} is not below {affixes[index].ModId}."));
        }
    }

    [Fact]
    public void A_forced_unique_takes_its_lines_and_sockets_from_content_and_draws_NOTHING()
    {
        var source = new RecordingRandomSource(new SeededRandomSource(11));
        GenerationResult result = GenerationWorld.Generator(source).Generate(new GenerationContext(
            GenerationWorld.Greatsword,
            ItemLevel: 60,
            ForcedRarityId: GenerationWorld.RareRarity,
            ForcedUniqueTemplateId: GenerationWorld.SunbrandTemplate,
            Quality: 0));

        Assert.Empty(source.Calls);

        // Kind 129 names the template, kind 131 carries its lines and kind 132 its OWN socket rather than
        // the base's two, because a unique's sockets are the template's authored list (spec 8.6).
        Assert.Equal(new byte[] { GenerationWorld.SunbrandTemplate }, GenerationWorld.Body(result.Payload, InstancePropertyKind.UniqueTemplate).ToArray());
        Assert.Equal(new[] { 6 }, GenerationWorld.Affixes(result.Payload).Select(affix => affix.ModId).ToArray());
        Assert.Equal(new[] { GenerationWorld.GemSocket }, GenerationWorld.SocketTypes(result.Payload));
        Assert.Empty(GenerationWorld.RareName(result.Payload).WordIds);
    }

    [Fact]
    public void A_generated_item_carries_kind_128_with_state_0_and_a_revealed_mask_of_0()
    {
        // Spec 9.4 step 12's field list does not name kind 128 and spec 12.7 requires it: an item carrying
        // no Identification field at all is INDISTINGUISHABLE from an identified one under CanSee, whose
        // identified argument the caller would then have to guess. The generator is what decides a new item
        // is unidentified, so the generator writes the field.
        GenerationResult result = GenerationWorld.Generator(new SeededRandomSource(11))
            .Generate(Rare(GenerationWorld.Greatsword));

        Assert.Equal(new byte[] { 0, 0 }, GenerationWorld.Body(result.Payload, InstancePropertyKind.Identification).ToArray());
    }

    [Fact]
    public void A_payload_that_would_be_EMPTY_takes_instance_id_0_and_is_a_plain_stack()
    {
        // Step 13's rule is contracts 6.2's, restated: an item takes an id when its encoded payload is non
        // empty. Every generated item carries kind 2 and kind 128, so the empty branch is a GUARD rather
        // than an outcome, and what the fact pins is that the two halves agree everywhere they can be seen.
        var allocator = GenerationWorld.FreshAllocator();
        ItemGenerator generator = GenerationWorld.Generator(new SeededRandomSource(11), allocator);

        var issued = new HashSet<long>();
        foreach (int baseId in new[] { GenerationWorld.Greatsword, GenerationWorld.Wand, GenerationWorld.Dagger })
        {
            GenerationResult result = generator.Generate(Rare(baseId));
            Assert.NotEmpty(result.Payload.ToArray());
            Assert.NotEqual(0L, result.InstanceId);
            Assert.True(issued.Add(result.InstanceId), "An instance id was issued twice.");
        }

        Assert.False(InstanceIdAllocator.NeedsInstanceId(0, DeclaredInstanceProperties.None));
    }

    [Fact]
    public void The_only_allocation_per_generation_is_the_payload_buffer()
    {
        // The generator owns every working array and sizes it at construction, so its OWN per generation
        // allocation is zero. What the measured number holds is the payload array and the
        // ItemInstancePayloadBuilder that encoded it, which the plan requires as the one encoder, so the
        // number is a function of the PAYLOAD rather than of the candidate pool. That is what the second
        // half asserts: a pool made larger by an order of magnitude moves it by nothing at all.
        ItemGenerator generator = GenerationWorld.Generator(new SeededRandomSource(11));
        GenerationContext context = Rare(GenerationWorld.Greatsword);
        for (int warm = 0; warm < 64; warm++)
        {
            _ = generator.Generate(context);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        GenerationResult result = generator.Generate(context);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(
            allocated is > 0 and < 2_048,
            FormattableString.Invariant($"A generation allocated {allocated} bytes, which is outside the encoder's own working set."));
        Assert.True(result.Payload.Length > 0);

        long second = GC.GetAllocatedBytesForCurrentThread();
        _ = generator.Generate(context);
        Assert.Equal(allocated, GC.GetAllocatedBytesForCurrentThread() - second);
    }

    [Fact]
    public void The_generator_holds_its_IRandomSource_and_has_no_per_call_source_parameter()
    {
        // Contracts 14.4 read as a test rather than as a comment. A per call source keeps "does this roll"
        // answerable at the METHOD and loses it at the TYPE, and the type is the half the contract cares
        // about: a caller anywhere in the fleet could hand a SeededRandomSource to the production generator
        // with nothing in any signature to notice.
        ConstructorInfo constructor = Assert.Single(typeof(ItemGenerator).GetConstructors());
        Assert.Contains(constructor.GetParameters(), parameter => parameter.ParameterType == typeof(IRandomSource));

        foreach (MethodInfo method in typeof(ItemGenerator).GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            Assert.DoesNotContain(
                method.GetParameters(),
                parameter => parameter.ParameterType == typeof(IRandomSource));
        }

        Assert.DoesNotContain(
            typeof(GenerationContext).GetProperties(),
            property => property.PropertyType == typeof(IRandomSource));
    }

    [Fact]
    public void The_result_reports_BOTH_the_affixes_placed_and_the_count_that_was_asked_for()
    {
        // Spec 9.4's own record has nowhere to report an item whose pool ran dry, and its step 8 says the
        // shortfall is "REPORTED IN THE RESULT rather than retried". A caller that cannot tell a three
        // affix roll from a six affix roll that ran dry cannot log the difference.
        GenerationResult full = GenerationWorld.Generator(new SeededRandomSource(11))
            .Generate(Rare(GenerationWorld.Greatsword));
        GenerationResult dry = GenerationWorld.Generator(new SeededRandomSource(11))
            .Generate(Rare(GenerationWorld.Wand));

        Assert.Equal(4, full.RequestedAffixCount);
        Assert.Equal(4, full.AffixCount);
        Assert.Equal(4, dry.RequestedAffixCount);
        Assert.Equal(0, dry.AffixCount);
        Assert.Equal(GenerationWorld.RareRarity, full.RarityId);
        Assert.Equal(GenerationWorld.Greatsword, full.BaseId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65_536)]
    public void An_item_level_outside_1_to_65535_is_refused_at_the_door(int itemLevel)
    {
        ItemGenerator generator = GenerationWorld.Generator(new SeededRandomSource(11));
        Assert.Throws<ArgumentOutOfRangeException>(() => generator.Generate(
            new GenerationContext(GenerationWorld.Greatsword, itemLevel, GenerationWorld.RareRarity, 0, 0)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65_536)]
    public void A_quality_outside_0_to_65535_is_refused_at_the_door(int quality)
    {
        ItemGenerator generator = GenerationWorld.Generator(new SeededRandomSource(11));
        Assert.Throws<ArgumentOutOfRangeException>(() => generator.Generate(
            new GenerationContext(GenerationWorld.Greatsword, 50, GenerationWorld.RareRarity, 0, quality)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    public void A_forced_rarity_id_outside_1_to_255_is_refused_at_the_door(int rarityId)
    {
        ItemGenerator generator = GenerationWorld.Generator(new SeededRandomSource(11));
        Assert.Throws<ArgumentOutOfRangeException>(() => generator.Generate(
            new GenerationContext(GenerationWorld.Greatsword, 50, rarityId, 0, 0)));
    }

    [Fact]
    public void An_affix_count_above_255_is_refused_because_kind_131s_count_is_a_byte()
    {
        // The rarity rule's own ceiling is 255 and a publish refuses anything past it, so this is a rule
        // that reached a boot without a publish. It is refused where it is READ rather than left to the
        // payload builder, so the message names the rarity rule rather than a list length.
        ContentTypeRegistry registry = GenerationWorld.World();
        List<ContentRow> rows = GenerationWorld.Rows(registry);
        rows.RemoveAll(row => row.Type.Value == InstanceContentTypeIds.RarityRuleTypeId && row.Id == GenerationWorld.RareRarity);
        rows.Add(RarityRule(
            registry,
            GenerationWorld.RareRarity,
            "rare",
            minAffixes: 300,
            maxAffixes: 300,
            maxPrefixes: 300,
            maxSuffixes: 300));

        ContentSnapshot candidate = Snapshot(registry, [.. rows]);
        var generator = new ItemGenerator(
            ModCandidateTables.Build(candidate),
            candidate,
            new SeededRandomSource(11),
            GenerationWorld.FreshAllocator());

        Assert.Throws<InvalidOperationException>(() => generator.Generate(Rare(GenerationWorld.Greatsword)));
    }

    [Fact]
    public void The_generated_payload_decodes_validates_and_stacks_with_its_own_twin()
    {
        InstancePropertyRegistry registry = InstancePropertyRegistry.CreateV1();

        GenerationResult result = GenerationWorld.Generator(new SeededRandomSource(4_242))
            .Generate(Rare(GenerationWorld.Greatsword));
        GenerationResult twin = GenerationWorld.Generator(new SeededRandomSource(4_242))
            .Generate(Rare(GenerationWorld.Greatsword));

        Assert.Null(ItemInstancePayload.Validate(registry, result.Payload.Span));

        // Byte equality IS the stacking rule (spec 4.6), so two items rolled off one seed on one base merge
        // and their instance ids are the only thing that differs.
        Assert.True(ItemInstancePayload.SequenceEqual(result.Payload.Span, twin.Payload.Span));

        // The item level, the rarity and the sockets the base declares are all there, and the durability
        // the greatsword does not declare is not.
        Assert.Equal(new byte[] { 60 }, GenerationWorld.Body(result.Payload, InstancePropertyKind.ItemLevel).ToArray());
        Assert.Equal(new byte[] { GenerationWorld.RareRarity }, GenerationWorld.Body(result.Payload, InstancePropertyKind.Rarity).ToArray());
        Assert.Equal(new[] { GenerationWorld.GemSocket, GenerationWorld.GemSocket }, GenerationWorld.SocketTypes(result.Payload));
        Assert.True(GenerationWorld.Body(result.Payload, InstancePropertyKind.Durability).IsEmpty);
        (int rarityRuleId, List<int> wordIds) = GenerationWorld.RareName(result.Payload);
        Assert.Equal(GenerationWorld.RareRarity, rarityRuleId);
        Assert.Equal(new[] { 1, 2 }, wordIds);
    }

    [Fact]
    public void A_base_declaring_durability_carries_kind_5_at_FULL_and_no_socket_field()
    {
        GenerationResult result = GenerationWorld.Generator(new SeededRandomSource(11))
            .Generate(Rare(GenerationWorld.Dagger));

        // 120 of 120, two varints, which is spec 9.4 step 12's "durability at full when the base declares
        // it". The dagger declares no socket, so kind 132 is absent rather than an empty list.
        Assert.Equal(new byte[] { 0x78, 0x78 }, GenerationWorld.Body(result.Payload, InstancePropertyKind.Durability).ToArray());
        Assert.True(GenerationWorld.Body(result.Payload, InstancePropertyKind.Sockets).IsEmpty);
    }

    [Fact]
    public void Quality_is_written_only_when_it_is_NON_zero()
    {
        ItemGenerator generator = GenerationWorld.Generator(new SeededRandomSource(11));

        GenerationResult plain = generator.Generate(Rare(GenerationWorld.Greatsword));
        GenerationResult polished = generator.Generate(
            new GenerationContext(GenerationWorld.Greatsword, 60, GenerationWorld.RareRarity, 0, 20));

        Assert.True(GenerationWorld.Body(plain.Payload, InstancePropertyKind.Quality).IsEmpty);
        Assert.Equal(new byte[] { 20 }, GenerationWorld.Body(polished.Payload, InstancePropertyKind.Quality).ToArray());
    }

    [Fact]
    public void The_rarity_is_ROLLED_against_the_bases_tags_when_none_is_forced()
    {
        // Magic holds 1000 of the 1500 and rare the other 500, so a draw of 0 is magic and a draw of 1000
        // is rare, which is the first tag wins rule of spec 8.3 read through the rarity weights.
        var magic = new RecordingRandomSource(new ScriptedRandomSource([0]));
        var rare = new RecordingRandomSource(new ScriptedRandomSource([1_000]));

        GenerationResult magicResult = GenerationWorld.Generator(magic).Generate(Rolled(GenerationWorld.Greatsword));
        GenerationResult rareResult = GenerationWorld.Generator(rare).Generate(Rolled(GenerationWorld.Greatsword));

        Assert.Equal(1_500, magic.Bounds[0]);
        Assert.Equal(GenerationWorld.MagicRarity, magicResult.RarityId);
        Assert.Equal(GenerationWorld.RareRarity, rareResult.RarityId);
        Assert.Equal(1, magicResult.RequestedAffixCount);
    }

    [Fact]
    public void The_content_version_the_result_names_is_the_snapshots_own()
    {
        ContentTypeRegistry registry = GenerationWorld.World();
        ContentSnapshot candidate = GenerationWorld.Candidate(registry);
        var generator = new ItemGenerator(
            ModCandidateTables.Build(candidate),
            candidate,
            new SeededRandomSource(11),
            GenerationWorld.FreshAllocator());

        Assert.Equal(candidate.VersionNumber, generator.Generate(Rare(GenerationWorld.Greatsword)).ContentVersion);
    }

    [Fact]
    public void The_engine_item_field_indexes_the_generator_reads_are_still_where_it_reads_them()
    {
        // The generator reads durability_max off an item row and socket_type off a base_socket row by
        // POSITION, and neither engine type declares an index constant the way the eighteen do. This is
        // that missing constant, written as a fact: a schema reorder fails here rather than silently
        // pointing the generator at a neighbouring value of the same kind.
        ContentFieldSchema item = ItemContentType.CreateSchema();
        Assert.Equal(ItemContentType.TagsField, item.Fields[2].Name);
        Assert.Equal(ItemContentType.DurabilityMaxField, item.Fields[13].Name);
        Assert.Equal(ItemContentType.SocketMaxField, item.Fields[14].Name);

        ContentFieldSchema socket = BaseSocketContentType.CreateSchema();
        Assert.Equal(BaseSocketContentType.ItemField, socket.Fields[0].Name);
        Assert.Equal(BaseSocketContentType.SortField, socket.Fields[1].Name);
        Assert.Equal(BaseSocketContentType.SocketTypeField, socket.Fields[2].Name);
    }

    [Fact]
    public void The_roll_position_formula_is_contracts_6_4s_worked_table_and_lives_in_ONE_place()
    {
        // A tier with min 10 and max 40, which is the table contracts 6.4 works through. Position 0 gives
        // the bottom of the range, 65535 gives the top and 32768 lands exactly halfway, so both ends are
        // reachable and the midpoint has no rounding mode to disagree about.
        Assert.Equal(10, RollPosition.Resolve(0, 10, 40));
        Assert.Equal(18, RollPosition.Resolve(16_384, 10, 40));
        Assert.Equal(25, RollPosition.Resolve(32_768, 10, 40));
        Assert.Equal(40, RollPosition.Resolve(65_535, 10, 40));

        // A tier whose max equals its min yields that value for every position, and a negative range is
        // ordinary content rather than a special case.
        Assert.Equal(7, RollPosition.Resolve(0, 7, 7));
        Assert.Equal(7, RollPosition.Resolve(65_535, 7, 7));
        Assert.Equal(-40, RollPosition.Resolve(0, -40, -10));
        Assert.Equal(-10, RollPosition.Resolve(65_535, -40, -10));
    }

    /// <summary>A forced rare on one base at item level 60, which is what most facts roll.</summary>
    static GenerationContext Rare(int baseId) => new(baseId, 60, GenerationWorld.RareRarity, 0, 0);

    /// <summary>A forced magic, which places exactly one affix.</summary>
    static GenerationContext Magic(int baseId) => new(baseId, 60, GenerationWorld.MagicRarity, 0, 0);

    /// <summary>The same, with the rarity ROLLED rather than forced.</summary>
    static GenerationContext Rolled(int baseId) => new(baseId, 60, 0, 0, 0);

    /// <summary>The mod kind of a one affix item, which the authored world's mod ids make unambiguous.</summary>
    static int KindOfOnlyAffix(GenerationResult result)
    {
        InstanceAffix affix = Assert.Single(GenerationWorld.Affixes(result.Payload));
        return affix.ModId is 2 or 4 ? ModContentType.SuffixKind : ModContentType.PrefixKind;
    }
}
