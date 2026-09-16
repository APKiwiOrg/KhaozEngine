using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using Xunit;
using static KhaozEngine.Tests.ItemInstances.Content.InstanceContentTypeFixtures;
using static KhaozEngine.Tests.ItemInstances.Content.InstanceValidationFixtures;

namespace KhaozEngine.Tests.ItemInstances.Generation;

/// <summary>
/// The candidate tables of spec 9.2: the bands, the tag-kind-band buckets, the precomputed overlap, the
/// group index and the load index that builds all of it at boot.
/// <para>
/// <b>These facts pin the SHAPE rather than a number, because a wrong overlap list is a wrong WEIGHT
/// rather than a crash.</b> A roll that reads a bucket whose suppression list is one entry short still
/// answers, still looks plausible, and is wrong forever after. So the self check that compares the merged
/// count and weight against what the scalars imply is a red test here rather than a note in a log, and the
/// reference merge it is checked against is written separately in this file rather than shared with the
/// implementation.
/// </para>
/// <para>
/// Every registry a fact builds is its OWN and nothing here writes process-global state, so no
/// <c>DisableParallelization</c> collection is needed.
/// </para>
/// </summary>
public class ModCandidateTablesTests
{
    [Fact]
    public void The_bands_are_the_intervals_between_every_distinct_min_and_max_plus_one()
    {
        ModCandidateTables tables = BuildWorld();

        // The gates are 1 to 10, 5 to 20 and four at 1 to 65535, so the distinct mins are 1 and 5 and the
        // distinct max plus ones are 11, 21 and 65536. The count is the authored level curve and nothing
        // here picks it.
        Assert.Equal(new[] { 1, 5, 11, 21, 65536 }, tables.BandBoundaries.ToArray());
        Assert.Equal(4, tables.BandCount);
    }

    [Fact]
    public void A_band_resolves_by_BINARY_SEARCH_over_the_boundaries_and_nothing_else()
    {
        ModCandidateTables tables = BuildWorld();

        Assert.Equal(0, tables.BandOf(1));
        Assert.Equal(0, tables.BandOf(4));
        Assert.Equal(1, tables.BandOf(5));
        Assert.Equal(1, tables.BandOf(10));
        Assert.Equal(2, tables.BandOf(11));
        Assert.Equal(2, tables.BandOf(20));
        Assert.Equal(3, tables.BandOf(21));
        Assert.Equal(3, tables.BandOf(ModTierContentType.MaxItemLevel));

        // A level outside the legal range clamps into the end band rather than indexing off the array,
        // because an item level arrives from a caller rather than from a validated row.
        Assert.Equal(0, tables.BandOf(0));
        Assert.Equal(0, tables.BandOf(-5));
        Assert.Equal(3, tables.BandOf(70_000));
    }

    [Fact]
    public void Within_one_band_no_tiers_gate_changes_so_the_live_tier_set_is_constant()
    {
        ModCandidateTables tables = BuildWorld();
        (int Tier, int Min, int Max)[] gates =
        [
            (1, 1, 10), (2, 5, 20), (3, 1, 65535), (4, 1, 65535), (5, 1, 65535), (6, 1, 65535),
        ];

        var byBand = new string?[tables.BandCount];
        foreach (int level in Levels())
        {
            string live = LiveTiers(gates, level);
            int band = tables.BandOf(level);
            byBand[band] ??= live;
            Assert.Equal(byBand[band], live);
        }

        // Every boundary came from a gate, so crossing one always changes the live set. That is what makes
        // the band count the authored curve rather than a resolution this design chose.
        for (int band = 1; band < byBand.Length; band++)
        {
            Assert.NotNull(byBand[band]);
            Assert.NotEqual(byBand[band - 1], byBand[band]);
        }
    }

    [Fact]
    public void A_bucket_is_ascending_by_packed_key_BY_CONSTRUCTION_with_no_sort_pass()
    {
        ContentTypeRegistry registry = Registry();

        // The tier ROW ids run the opposite way to the packed order, so a build that walked the rows in id
        // order would hand back a bucket in that order instead.
        ContentSnapshot candidate = Snapshot(
            registry,
            Tag(registry, 5, "metal"),
            Item(registry, 40, "greatsword", [5]),
            Mod(registry, 1, "sharp"),
            Mod(registry, 2, "keen"),
            Mod(registry, 3, "brutal"),
            ModTier(registry, 1, "brutal_t2", modId: 3, ordinal: 2),
            ModTier(registry, 2, "brutal_t1", modId: 3, ordinal: 1),
            ModTier(registry, 3, "sharp_t1", modId: 1, ordinal: 1),
            ModTier(registry, 4, "keen_t1", modId: 2, ordinal: 1),
            ModTierWeight(registry, 1, "w1", tierId: 1, tagId: 5, weight: 7),
            ModTierWeight(registry, 2, "w2", tierId: 2, tagId: 5, weight: 11),
            ModTierWeight(registry, 3, "w3", tierId: 3, tagId: 5, weight: 13),
            ModTierWeight(registry, 4, "w4", tierId: 4, tagId: 5, weight: 17));

        ModCandidateTables tables = ModCandidateTables.Build(candidate);
        int bucket = tables.BucketOf(5, KindPosition(tables, ModContentType.PrefixKind), 0);

        Assert.Equal(
            new[]
            {
                ModCandidateTables.Pack(1, 1),
                ModCandidateTables.Pack(2, 1),
                ModCandidateTables.Pack(3, 1),
                ModCandidateTables.Pack(3, 2),
            },
            tables.BucketPacked(bucket).ToArray());

        // The column beside it is the RUNNING weight through the entry, so a pick is one binary search
        // rather than a walk and the last entry IS the bucket total.
        Assert.Equal(new[] { 13, 30, 41, 48 }, tables.BucketCumulative(bucket).ToArray());
        Assert.Equal(48, tables.BucketTotal(bucket));
    }

    [Fact]
    public void A_LEGACY_mod_never_enters_a_table_at_all()
    {
        ModCandidateTables tables = BuildWorld();
        int legacy = ModCandidateTables.Pack(3, 1);
        long total = 0;

        for (int bucket = 0; bucket < tables.BucketCount; bucket++)
        {
            Assert.DoesNotContain(legacy, tables.BucketPacked(bucket).ToArray());
            total += tables.BucketTotal(bucket);
        }

        // Its 999 weight is in no total either, which is the point: the flag is folded in at BUILD rather
        // than tested per candidate, so the generator needs no rule for it.
        Assert.Equal(2800L, total);

        // It is out of the group index for the same reason, that it is in no table to exclude from.
        Assert.Equal(new[] { 1 }, tables.GroupMembers(1).ToArray());
    }

    [Fact]
    public void A_tier_with_no_weight_row_can_never_spawn_and_that_is_legal()
    {
        ModCandidateTables tables = BuildWorld();
        int unweighted = ModCandidateTables.Pack(2, 2);

        for (int bucket = 0; bucket < tables.BucketCount; bucket++)
        {
            Assert.DoesNotContain(unweighted, tables.BucketPacked(bucket).ToArray());
        }

        // Legal rather than a finding: it is how a tier reachable only through crafting is authored.
        Assert.Equal(0, tables.ConsistencyFailures);
    }

    [Fact]
    public void A_base_weight_is_the_FIRST_matching_tag_in_authored_order_never_a_sum()
    {
        ModCandidateTables tables = BuildWorld();
        int prefix = KindPosition(tables, ModContentType.PrefixKind);
        int greatsword = SignatureOf(tables, 40);
        int wand = SignatureOf(tables, 41);

        // The greatsword is tagged metal then blade, so tier 1 of the sharp mod is worth its METAL weight
        // of 100 and its blade weight of 10 is discarded. The wand is tagged gem then metal, so the same
        // tier is worth 500 at the same item level. Neither is 110 and neither is 600.
        Assert.Equal(
            110,
            tables.BucketTotal(tables.BucketOf(5, prefix, 0)) + tables.BucketTotal(tables.BucketOf(7, prefix, 0)));
        Assert.Equal(100L, tables.LiveWeight(greatsword, prefix, 0));
        Assert.Equal(500L, tables.LiveWeight(wand, prefix, 0));
        Assert.Equal(1L, tables.LiveCount(greatsword, prefix, 0));
        Assert.Equal(1L, tables.LiveCount(wand, prefix, 0));
    }

    [Fact]
    public void Two_weight_rows_of_one_tier_and_one_tag_are_ONE_candidate_at_the_FIRST_rows_weight()
    {
        ContentTypeRegistry registry = Registry();
        ContentSnapshot candidate = Snapshot(
            registry,
            Tag(registry, 5, "metal"),
            Item(registry, 40, "greatsword", [5]),
            Mod(registry, 1, "sharp"),
            ModTier(registry, 1, "sharp_t1", modId: 1, ordinal: 1),
            ModTier(registry, 2, "sharp_t2", modId: 1, ordinal: 2),
            ModTierWeight(registry, 1, "sharp_t1_metal", tierId: 1, tagId: 5, weight: 100),
            ModTierWeight(registry, 2, "sharp_t1_metal_again", tierId: 1, tagId: 5, weight: 900),
            ModTierWeight(registry, 3, "sharp_t2_metal", tierId: 2, tagId: 5, weight: 200));

        ModCandidateTables tables = ModCandidateTables.Build(candidate);
        int prefix = KindPosition(tables, ModContentType.PrefixKind);
        int greatsword = SignatureOf(tables, 40);

        // Three ROWS make three entries, because the build never merges by key, and the two that share a
        // key sit adjacent because the bucket is ascending by packed key. The overlap then treats the
        // second as a repeat of a key already spent, exactly as it treats a later TAG repeating an earlier
        // tag's entry, and 900 of authored weight is suppressed rather than summed.
        Assert.Equal(3, tables.BucketLength(tables.BucketOf(5, prefix, 0)));
        Assert.Equal(1_200, tables.BucketTotal(tables.BucketOf(5, prefix, 0)));
        Assert.Equal(2L, tables.LiveCount(greatsword, prefix, 0));
        Assert.Equal(300L, tables.LiveWeight(greatsword, prefix, 0));

        int header = tables.HeaderOf(greatsword, prefix, 0, 0);
        Assert.Equal(1, tables.SuppressedCountAt(header));
        Assert.Equal(900, tables.SuppressedWeightAt(header));
        Assert.Equal(new ushort[] { 1 }, tables.SuppressedIndexesAt(header).ToArray());

        // The build is self consistent about it, which is what makes this a behaviour to pin rather than a
        // defect to fix HERE: the tables agree with themselves and the publish is what refuses the rows.
        // KEC0117 is that refusal.
        Assert.Equal(0, tables.ConsistencyFailures);
    }

    [Fact]
    public void A_base_listing_ONE_TAG_TWICE_keeps_both_positions_with_the_repeat_suppressed()
    {
        ContentTypeRegistry registry = Registry();
        ContentSnapshot candidate = Snapshot(
            registry,
            Tag(registry, 5, "metal"),
            Item(registry, 40, "greatsword", [5, 5]),
            Item(registry, 41, "dagger", [5]),
            Mod(registry, 1, "sharp"),
            ModTier(registry, 1, "sharp_t1", modId: 1, ordinal: 1),
            ModTier(registry, 2, "sharp_t2", modId: 1, ordinal: 2),
            ModTierWeight(registry, 1, "sharp_t1_metal", tierId: 1, tagId: 5, weight: 100),
            ModTierWeight(registry, 2, "sharp_t2_metal", tierId: 2, tagId: 5, weight: 200));

        ModCandidateTables tables = ModCandidateTables.Build(candidate);
        int prefix = KindPosition(tables, ModContentType.PrefixKind);
        int twice = SignatureOf(tables, 40);
        int once = SignatureOf(tables, 41);

        // A repeated tag is an authoring mistake nothing refuses, and what matters is that it costs the
        // roll NOTHING: the second position's entries are all keys the first already spent, so the whole
        // position is suppressed and the base rolls exactly as the base that listed the tag once does.
        Assert.Equal(tables.LiveWeight(once, prefix, 0), tables.LiveWeight(twice, prefix, 0));
        Assert.Equal(tables.LiveCount(once, prefix, 0), tables.LiveCount(twice, prefix, 0));
        Assert.Equal(300L, tables.LiveWeight(twice, prefix, 0));
        Assert.Equal(2L, tables.LiveCount(twice, prefix, 0));

        // The second position is where the whole bucket went, rather than the first losing anything.
        Assert.Equal(0, tables.SuppressedCountAt(tables.HeaderOf(twice, prefix, 0, 0)));
        Assert.Equal(2, tables.SuppressedCountAt(tables.HeaderOf(twice, prefix, 0, 1)));
        Assert.Equal(300, tables.SuppressedWeightAt(tables.HeaderOf(twice, prefix, 0, 1)));
        Assert.Equal(0, tables.ConsistencyFailures);
    }

    [Fact]
    public void The_suppression_scalars_reproduce_the_merged_count_and_weight_on_every_key()
    {
        ModCandidateTables tables = BuildWorld();

        Assert.Equal(0, tables.ConsistencyFailures);
        Assert.Equal(4L, tables.SuppressedEntries);

        // The self check inside the build is one half. This is the other: an independent merge, written
        // here rather than shared with the implementation, over every (signature, kind, band) key.
        for (int signature = 0; signature < tables.Signatures.Count; signature++)
        {
            for (int kind = 0; kind < tables.KindCount; kind++)
            {
                for (int band = 0; band < tables.BandCount; band++)
                {
                    (long Count, long Weight) merged = ReferenceMerge(tables, signature, kind, band);
                    Assert.Equal(merged.Count, tables.LiveCount(signature, kind, band));
                    Assert.Equal(merged.Weight, tables.LiveWeight(signature, kind, band));
                }
            }
        }

        // And the list itself, hand checked on the one key the fixture was authored to overlap on: the wand
        // carries gem then metal, so the metal table's only entry is the one the gem table already won.
        int header = tables.HeaderOf(SignatureOf(tables, 41), KindPosition(tables, ModContentType.PrefixKind), 0, 1);
        Assert.Equal(1, tables.SuppressedCountAt(header));
        Assert.Equal(100, tables.SuppressedWeightAt(header));
        Assert.Equal(new ushort[] { 0 }, tables.SuppressedIndexesAt(header).ToArray());
    }

    [Fact]
    public void The_union_is_never_materialised_at_boot_or_at_a_roll()
    {
        ModCandidateTables tables = BuildWorld();

        long union = 0;
        for (int signature = 0; signature < tables.Signatures.Count; signature++)
        {
            for (int kind = 0; kind < tables.KindCount; kind++)
            {
                for (int band = 0; band < tables.BandCount; band++)
                {
                    union += ReferenceMerge(tables, signature, kind, band).Count;
                }
            }
        }

        // What a boot holds is the tables plus what the first-tag-wins rule DISCARDS, which is strictly less
        // than the unions those tables imply. The 148 MB row of spec 9.2's measured table is what the other
        // reading costs.
        Assert.Equal(24L, union);
        Assert.Equal(16L, tables.TableEntries);
        Assert.True(
            tables.TableEntries + tables.SuppressedEntries < union,
            FormattableString.Invariant($"{tables.TableEntries} plus {tables.SuppressedEntries} is not below {union}."));

        // Nothing is allocated, memoized or evicted at a roll, so there is no cache, no hit rate and no
        // pathological pack that degrades to a merge per roll.
        Sweep(tables);
        long before = GC.GetAllocatedBytesForCurrentThread();
        long live = Sweep(tables);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0L, after - before);
        Assert.True(live > 0, "The sweep read nothing, so the zero allocation above proves nothing.");
    }

    [Fact]
    public void A_bucket_above_65535_entries_THROWS_at_build_rather_than_wrapping_a_ushort()
    {
        ContentTypeRegistry registry = Registry();
        var rows = new List<ContentRow>
        {
            Tag(registry, 5, "metal"),
            Item(registry, 40, "greatsword", [5]),
        };

        // Fifteen tiers a mod is the packed key's whole ceiling, so 4,370 mods is the cheapest way past a
        // 16 bit entry index in one (tag, kind, band) bucket.
        const int mods = 4370;
        int tier = 0;
        for (int mod = 1; mod <= mods; mod++)
        {
            rows.Add(Mod(registry, mod, FormattableString.Invariant($"m{mod}")));
            for (int ordinal = 1; ordinal <= ModCandidateTables.MaxTierOrdinal; ordinal++)
            {
                tier++;
                rows.Add(ModTier(registry, tier, FormattableString.Invariant($"t{tier}"), modId: mod, ordinal: ordinal));
                rows.Add(ModTierWeight(registry, tier, FormattableString.Invariant($"w{tier}"), tierId: tier, tagId: 5, weight: 1));
            }
        }

        Assert.Equal(65_550, tier);
        ContentSnapshot candidate = Snapshot(registry, rows.ToArray());

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => ModCandidateTables.Build(candidate));
        Assert.Contains("65535", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_signatures_UNION_weight_past_int_MaxValue_THROWS_at_build_because_a_draw_is_bounded_by_an_int()
    {
        ContentTypeRegistry registry = Registry();

        // KEC0113 bounds the rows sharing one TAG, and this is the union over a base's tag positions, which
        // is a superset of any one of them. Two tags each holding 1.5 billion sit comfortably under the
        // per-tag ceiling and sum to 3 billion, which is what a roll would have to draw over.
        const int half = 1_500_000_000;
        ContentRow[] Rows(int weight) =>
        [
            Tag(registry, 5, "metal"),
            Tag(registry, 6, "blade"),
            Item(registry, 40, "greatsword", [5, 6]),
            Mod(registry, 1, "sharp"),
            Mod(registry, 2, "keen"),
            ModTier(registry, 1, "sharp_t1", modId: 1, ordinal: 1),
            ModTier(registry, 2, "keen_t1", modId: 2, ordinal: 1),
            ModTierWeight(registry, 1, "sharp_t1_metal", tierId: 1, tagId: 5, weight: weight),
            ModTierWeight(registry, 2, "keen_t1_blade", tierId: 2, tagId: 6, weight: weight),
        ];

        // A union just under the ceiling builds, so the refusal is the CEILING rather than a pair of big
        // numbers. The two mods differ, so nothing here is suppressed and both weights are live.
        ModCandidateTables under = ModCandidateTables.Build(Snapshot(registry, Rows(1_000_000_000)));
        Assert.Equal(2_000_000_000L, under.LiveWeight(SignatureOf(under, 40), KindPosition(under, ModContentType.PrefixKind), 0));

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => ModCandidateTables.Build(Snapshot(registry, Rows(half))));
        Assert.Contains("3000000000", failure.Message, StringComparison.Ordinal);
        Assert.Contains("2147483647", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tier_ordinal_above_15_THROWS_at_build_because_the_packed_key_carries_four_bits()
    {
        ContentTypeRegistry registry = Registry();

        ContentRow[] Rows(int ordinal) =>
        [
            Tag(registry, 5, "metal"),
            Item(registry, 40, "greatsword", [5]),
            Mod(registry, 1, "sharp"),
            ModTier(registry, 1, "sharp_t", modId: 1, ordinal: ordinal),
            ModTierWeight(registry, 1, "w", tierId: 1, tagId: 5, weight: 3),
        ];

        // Fifteen is the ceiling and it builds. Sixteen would alias onto tier 0 of the next mod id, which is
        // the silent failure the packed key's four bits have to refuse, so a publish reports KEC0102 and a
        // hand built snapshot that reached a boot without one is refused here.
        ModCandidateTables fifteen = ModCandidateTables.Build(Snapshot(registry, Rows(15)));
        Assert.Equal(1L, fifteen.TableEntries);

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => ModCandidateTables.Build(Snapshot(registry, Rows(16))));
        Assert.Contains("16", failure.Message, StringComparison.Ordinal);
        Assert.Contains(InstanceContentFindings.TierOrdinal, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_base_tag_list_past_the_generation_ceiling_THROWS_at_build()
    {
        ContentTypeRegistry registry = Registry();
        var tags = new List<int>();
        var rows = new List<ContentRow>();
        for (int tag = 1; tag <= ModCandidateTables.MaxGenerationTagPositions + 1; tag++)
        {
            tags.Add(tag);
            rows.Add(Tag(registry, tag, FormattableString.Invariant($"tag{tag}")));
        }

        rows.Add(Item(registry, 40, "greatsword", tags));

        // The suppression header count is signatures times bands times kinds times POSITIONS, so an
        // unbounded position count makes the table build unbounded. A publish reports KEC0114 and the build
        // refuses it for the same reason the ordinal ceiling does.
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => ModCandidateTables.Build(Snapshot(registry, rows.ToArray())));
        Assert.Contains("8", failure.Message, StringComparison.Ordinal);
        Assert.Contains(InstanceContentFindings.GenerationTagPositions, failure.Message, StringComparison.Ordinal);

        // At the ceiling it builds, and the header space is the positions the signature actually has.
        rows[^1] = Item(registry, 40, "greatsword", tags.GetRange(0, ModCandidateTables.MaxGenerationTagPositions));
        ModCandidateTables tables = ModCandidateTables.Build(Snapshot(registry, rows.ToArray()));
        Assert.Equal(ModCandidateTables.MaxGenerationTagPositions, tables.TagPositionCount);
    }

    [Fact]
    public void A_snapshot_with_NO_mod_tier_weight_rows_builds_empty_tables_which_is_a_CLIENT()
    {
        ContentTypeRegistry registry = Registry();

        // The weight type is ServerOnly as a WHOLE type, so a client's pack carries no weight row and the
        // tables it builds are empty BY CONSTRUCTION rather than behind a flag. That is what keeps the
        // generator off a client with no "is this a server" test anywhere.
        ContentSnapshot candidate = Snapshot(
            registry,
            Tag(registry, 5, "metal"),
            Item(registry, 40, "greatsword", [5]),
            Mod(registry, 1, "sharp"),
            ModTier(registry, 1, "sharp_t1", modId: 1, ordinal: 1, itemLevelMin: 1, itemLevelMax: 10));

        ModCandidateTables tables = ModCandidateTables.Build(candidate);

        Assert.Equal(0L, tables.TableEntries);
        Assert.Equal(0L, tables.SuppressedEntries);
        Assert.Equal(0, tables.ConsistencyFailures);
        Assert.Equal(0, tables.TagCount);
        Assert.Equal(0, tables.BucketCount);
        Assert.Equal(2, tables.BandCount);
        Assert.Equal(0L, tables.LiveWeight(SignatureOf(tables, 40), 0, 0));
    }

    [Fact]
    public void The_kind_set_is_the_AUTHORED_kinds_ascending_rather_than_prefix_and_suffix_only()
    {
        ModCandidateTables tables = BuildWorld();

        // Spec 8.2 gives the engine kinds 1 and 2 and leaves 3 to 255 to the game, and a bucket is indexed
        // by the kind's POSITION, so an implicit, a corruption line or a material line is a counted pool of
        // its own rather than a row the tables silently drop.
        Assert.Equal(new[] { 1, 2, 3 }, tables.Kinds.ToArray());
        Assert.Equal(3, tables.KindCount);
        Assert.True(tables.TryGetKindPosition(3, out int position));
        Assert.Equal(2, position);
        Assert.False(tables.TryGetKindPosition(9, out _));
        Assert.Equal(70, tables.BucketTotal(tables.BucketOf(5, position, 0)));
    }

    [Fact]
    public void The_group_index_answers_every_other_mod_of_a_group_as_a_lookup()
    {
        ModCandidateTables tables = BuildWorld();

        Assert.Equal(new[] { 1 }, tables.GroupMembers(1).ToArray());
        Assert.Equal(1, tables.GroupOf(1));
        Assert.Equal(0, tables.GroupOf(2));
        Assert.Equal(2, tables.MaxPerItem(1));
        Assert.Equal(ModContentType.PrefixKind, tables.KindOf(1));
        Assert.Equal(3, tables.KindOf(4));

        // A group nobody authored answers empty rather than throwing, because the generator asks before it
        // knows whether the placed mod carries one.
        Assert.True(tables.GroupMembers(0).IsEmpty);
        Assert.True(tables.GroupMembers(99).IsEmpty);
        Assert.Equal(0, tables.MaxPerItem(99));
    }

    [Fact]
    public void ResidentBytes_is_the_self_reported_size_of_what_a_boot_holds_for_good()
    {
        ContentTypeRegistry registry = Registry();
        ModCandidateTables tables = ModCandidateTables.Build(Snapshot(registry, World(registry)));
        ModCandidateTables empty = ModCandidateTables.Build(Snapshot(registry, Tag(registry, 5, "metal")));

        // The packed key and its cumulative weight are four bytes each and a suppression index is two, so
        // the number can never be below what the entries alone cost.
        Assert.True(
            tables.ResidentBytes >= (tables.TableEntries * 8) + (tables.SuppressedEntries * 2),
            FormattableString.Invariant($"{tables.ResidentBytes} is below the entry cost."));
        Assert.True(tables.ResidentBytes > empty.ResidentBytes);
        Assert.True(tables.ResidentBytes < 1024 * 1024, FormattableString.Invariant($"{tables.ResidentBytes}"));
    }

    [Fact]
    public void The_index_reads_another_types_ROWS_and_never_another_INDEX()
    {
        ContentTypeRegistry registry = Registry();
        ContentSnapshot candidate = Snapshot(registry, World(registry));
        var recording = new RecordingSnapshot(candidate);
        var index = new ModCandidateTablesIndex();

        index.Build(recording);

        Assert.Equal(InstanceContentTypeIds.ModTypeId, index.Type.Value);
        Assert.Equal(16L, index.Tables.TableEntries);

        // The three row sets spec 9.2 names, plus the bases the signature intern needs and the groups the
        // group index needs, plus everything ELSE a roll reads, because the index folds the whole of it
        // ONCE per snapshot rather than once per generator. The list is short of the full set because this
        // world authors no rarity rule, no rare name word and no unique, and each empty family short
        // circuits its own children. Still no read of anything that is not a ROW: IContentSnapshot carries
        // no index at all, which is what makes the rule a refusal rather than a comment.
        Assert.Equal(
            new ushort[]
            {
                EngineContentTypes.ItemTypeId,
                EngineContentTypes.BaseSocketTypeId,
                InstanceContentTypeIds.ModTypeId,
                InstanceContentTypeIds.ModGroupTypeId,
                InstanceContentTypeIds.RarityRuleTypeId,
                InstanceContentTypeIds.UniqueTemplateTypeId,
                InstanceContentTypeIds.ModTierTypeId,
                InstanceContentTypeIds.ModTierWeightTypeId,
                InstanceContentTypeIds.RarityKindLimitTypeId,
            },
            recording.TypesRead());
    }

    [Fact]
    public void A_build_failure_THROWS_and_fails_the_boot_closed()
    {
        var index = new ModCandidateTablesIndex();
        ContentTypeRegistry registry = Registry(index);
        ContentSnapshot candidate = Snapshot(
            registry,
            Tag(registry, 5, "metal"),
            Item(registry, 40, "greatsword", [5]),
            Mod(registry, 1, "sharp"),
            ModTier(registry, 1, "sharp_t", modId: 1, ordinal: 16),
            ModTierWeight(registry, 1, "w", tierId: 1, tagId: 5, weight: 3));

        ContentRuntime runtime = ContentRuntime.FromSnapshot(candidate, registry);

        ContentLoadIndexException failure = Assert.Throws<ContentLoadIndexException>(runtime.BuildLoadIndexes);
        Assert.Equal(InstanceContentTypeIds.ModTypeId, failure.Type.Value);
        Assert.IsType<InvalidOperationException>(failure.InnerException);
        Assert.False(runtime.LoadIndexesBuilt);

        // A partial index is never left behind, and the tables a failed build would have handed out are not
        // reachable at all.
        Assert.Throws<InvalidOperationException>(() => _ = index.Tables);
    }

    [Fact]
    public void A_BOOT_builds_the_tables_once_and_they_are_immutable_for_the_process()
    {
        var index = new ModCandidateTablesIndex();
        ContentTypeRegistry registry = Registry(index);
        ContentSnapshot candidate = Snapshot(registry, World(registry));
        ContentRuntime runtime = ContentRuntime.FromSnapshot(candidate, registry);

        runtime.BuildLoadIndexes();

        Assert.True(runtime.TryGetLoadIndex<ModCandidateTablesIndex>(
            new ContentTypeId(InstanceContentTypeIds.ModTypeId),
            out ModCandidateTablesIndex? found));
        Assert.Same(index, found);
        Assert.NotNull(found);
        Assert.Equal(16L, found.Tables.TableEntries);

        // There is exactly ONE table set in a process and no roll can see two: a new content version becomes
        // active at server RESTART, so there is no swap to build.
        Assert.Same(found.Tables, index.Tables);
        Assert.Throws<InvalidOperationException>(() => index.Build(candidate));
    }

    /// <summary>Every item level these facts walk, which covers all four bands of the authored world.</summary>
    static IEnumerable<int> Levels()
    {
        for (int level = 1; level <= 30; level++)
        {
            yield return level;
        }

        yield return 1_000;
        yield return ModTierContentType.MaxItemLevel;
    }

    /// <summary>The kind's bucket POSITION, which is what a bucket is indexed by rather than the kind id.</summary>
    static int KindPosition(ModCandidateTables tables, int kind)
    {
        Assert.True(tables.TryGetKindPosition(kind, out int position));
        return position;
    }

    /// <summary>The tag signature one authored base carries.</summary>
    static int SignatureOf(ModCandidateTables tables, int baseId)
    {
        Assert.True(tables.Signatures.TryGetSignature(baseId, out int signature));
        return signature;
    }

    /// <summary>Every tier live at one item level, as the authored gates give it.</summary>
    static string LiveTiers((int Tier, int Min, int Max)[] gates, int level)
    {
        var live = new List<string>();
        foreach ((int tier, int min, int max) in gates)
        {
            if (level >= min && level <= max)
            {
                live.Add(tier.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return string.Join(",", live);
    }

    /// <summary>
    /// The k-way merge the roll does NOT run, written independently here so the suppression lists have
    /// something to be wrong against. The first tag in authored order wins a repeated key, which is spec
    /// 8.3's rule and the only reading the scalars can be checked against.
    /// </summary>
    static (long Count, long Weight) ReferenceMerge(
        ModCandidateTables tables,
        int signature,
        int kindPosition,
        int band)
    {
        var seen = new HashSet<int>();
        long count = 0;
        long weight = 0;
        ReadOnlySpan<int> tags = tables.Signatures.TagsOf(signature);
        for (int position = 0; position < tags.Length; position++)
        {
            int bucket = tables.BucketOf(tags[position], kindPosition, band);
            ReadOnlySpan<int> packed = tables.BucketPacked(bucket);
            for (int entry = 0; entry < packed.Length; entry++)
            {
                if (!seen.Add(packed[entry]))
                {
                    continue;
                }

                count++;
                weight += tables.WeightAt(bucket, entry);
            }
        }

        return (count, weight);
    }

    /// <summary>Every key's live scalars, which is what opening a pool reads and what must allocate nothing.</summary>
    static long Sweep(ModCandidateTables tables)
    {
        long total = 0;
        for (int signature = 0; signature < tables.Signatures.Count; signature++)
        {
            for (int kind = 0; kind < tables.KindCount; kind++)
            {
                for (int band = 0; band < tables.BandCount; band++)
                {
                    total += tables.LiveWeight(signature, kind, band) + tables.LiveCount(signature, kind, band);
                }
            }
        }

        return total;
    }

    static ModCandidateTables BuildWorld()
    {
        ContentTypeRegistry registry = Registry();
        return ModCandidateTables.Build(Snapshot(registry, World(registry)));
    }

    /// <summary>
    /// One small authored world, sized so every number in these facts is hand checkable: three tags, four
    /// bases over three tag signatures, four mods across three kinds with one of them legacy, six tiers and
    /// seven weight rows. It is authored HERE rather than shipped, because the engine ships shapes and no
    /// rows.
    /// </summary>
    static ContentRow[] World(ContentTypeRegistry registry) =>
    [
        Tag(registry, 5, "metal"),
        Tag(registry, 6, "gem"),
        Tag(registry, 7, "blade"),

        Item(registry, 40, "greatsword", [5, 7]),
        Item(registry, 41, "wand", [6, 5]),
        Item(registry, 42, "sabre", [5, 7]),
        Item(registry, 43, "plain"),

        ModGroup(registry, 1, "added_attack", maxPerItem: 2),

        Mod(registry, 1, "sharp", kind: ModContentType.PrefixKind, group: 1),
        Mod(registry, 2, "keen", kind: ModContentType.SuffixKind),
        Mod(registry, 3, "ancient", kind: ModContentType.PrefixKind, group: 1, legacy: true),
        Mod(registry, 4, "runed", kind: 3),

        ModTier(registry, 1, "sharp_t1", modId: 1, ordinal: 1, itemLevelMin: 1, itemLevelMax: 10),
        ModTier(registry, 2, "sharp_t2", modId: 1, ordinal: 2, itemLevelMin: 5, itemLevelMax: 20),
        ModTier(registry, 3, "keen_t1", modId: 2, ordinal: 1),
        ModTier(registry, 4, "keen_t2", modId: 2, ordinal: 2),
        ModTier(registry, 5, "ancient_t1", modId: 3, ordinal: 1),
        ModTier(registry, 6, "runed_t1", modId: 4, ordinal: 1),

        ModTierWeight(registry, 1, "sharp_t1_metal", tierId: 1, tagId: 5, weight: 100),
        ModTierWeight(registry, 2, "sharp_t1_gem", tierId: 1, tagId: 6, weight: 500),
        ModTierWeight(registry, 3, "sharp_t2_metal", tierId: 2, tagId: 5, weight: 50),
        ModTierWeight(registry, 4, "keen_t1_metal", tierId: 3, tagId: 5, weight: 300),
        ModTierWeight(registry, 5, "ancient_t1_metal", tierId: 5, tagId: 5, weight: 999),
        ModTierWeight(registry, 6, "runed_t1_metal", tierId: 6, tagId: 5, weight: 70),
        ModTierWeight(registry, 7, "sharp_t1_blade", tierId: 1, tagId: 7, weight: 10),
    ];

    /// <summary>
    /// A snapshot that records which TYPE a read named, so the load index rule can be checked rather than
    /// asserted. It forwards everything and answers nothing of its own.
    /// </summary>
    sealed class RecordingSnapshot(IContentSnapshot inner) : IContentSnapshot
    {
        readonly SortedSet<ushort> _types = [];

        public int VersionNumber => inner.VersionNumber;

        public ContentVersionIdentity Identity => inner.Identity;

        public IReadOnlyList<RemapRule> Rules => inner.Rules;

        public ushort[] TypesRead()
        {
            var read = new ushort[_types.Count];
            _types.CopyTo(read);
            return read;
        }

        public bool TryGetRow(ContentTypeId type, int id, [MaybeNullWhen(false)] out ContentRow row)
        {
            _types.Add(type.Value);
            return inner.TryGetRow(type, id, out row);
        }

        public bool TryGetId(ContentTypeId type, ContentKey key, out int id)
        {
            _types.Add(type.Value);
            return inner.TryGetId(type, key, out id);
        }

        public IReadOnlyList<ContentRow> Rows(ContentTypeId type)
        {
            _types.Add(type.Value);
            return inner.Rows(type);
        }

        public bool IsRetired(ContentTypeId type, int id)
        {
            _types.Add(type.Value);
            return inner.IsRetired(type, id);
        }
    }
}
