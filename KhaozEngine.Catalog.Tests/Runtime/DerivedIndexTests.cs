using System;
using System.Collections.Generic;
using KhaozEngine.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Runtime;

/// <summary>
/// The four indexes the engine derives at load, spec 9.4: key to id per type, tag to sorted ids, family
/// membership, and the loot candidate arrays with their weights prefix summed.
/// <para>
/// Joins <c>AllocSensitive</c> because the walk budget reads
/// <c>GC.GetAllocatedBytesForCurrentThread</c>.
/// </para>
/// </summary>
[Collection("AllocSensitive")]
public class DerivedIndexTests
{
    [Fact]
    public void All_four_are_built_eagerly_at_load_and_none_is_built_lazily()
    {
        // Each is walked inside gameplay, so a lazy build inside a tick is the latency spike 9.4 refuses.
        // The runtime therefore never exists with its engine indexes missing.
        ContentRuntime runtime = CatalogLootFixtures.Runtime(out _);

        Assert.NotSame(ContentTagIndex.Empty, runtime.Indexes.Tags);
        Assert.NotSame(ContentLootIndex.Empty, runtime.Indexes.Loot);
        Assert.Same(runtime.Indexes, runtime.Indexes);
        Assert.True(runtime.Indexes.ApproximateBytes() > 0);
    }

    [Fact]
    public void Key_to_id_is_the_open_addressed_table_of_task_20_rather_than_a_second_structure()
    {
        ContentRuntime runtime = CatalogLootFixtures.Runtime(out _);
        Assert.True(runtime.TryGetTable(CatalogSnapshotFixtures.ItemType, out ContentTypeTable? items));

        // The first of the four lives on the type table it indexes, because it is keyed on a slice of that
        // table's own blob, and it is read through the runtime like everything else.
        Assert.NotEmpty(items.KeyIds);
        Assert.True(runtime.TryGetId(CatalogSnapshotFixtures.ItemType, new ContentKey("item_2"), out int id));
        Assert.Equal(2, id);
    }

    [Fact]
    public void Tag_to_ids_is_a_sorted_int_array_per_tag_id_per_content_type()
    {
        ContentRuntime runtime = CatalogRuntimeFixtures.Runtime(out _);
        ContentTagIndex tags = runtime.Indexes.Tags;

        ReadOnlySpan<int> swords = tags.Ids(CatalogSnapshotFixtures.ItemType, CatalogRuntimeFixtures.SwordTag);
        ReadOnlySpan<int> metal = tags.Ids(CatalogSnapshotFixtures.ItemType, CatalogRuntimeFixtures.MetalTag);

        Assert.Equal([1, 2, 35], swords.ToArray());
        Assert.Equal([2, 8], metal.ToArray());

        // Ascending, always, whatever order the rows or the tags were authored in.
        for (int i = 1; i < swords.Length; i++)
        {
            Assert.True(swords[i] > swords[i - 1]);
        }

        // Per TYPE, so the same tag id on another type is another list, and a type with no tagged row has
        // none at all.
        Assert.Empty(tags.Ids(CatalogSnapshotFixtures.TagType, CatalogRuntimeFixtures.SwordTag).ToArray());
        Assert.Empty(tags.Ids(new ContentTypeId(1024), CatalogRuntimeFixtures.SwordTag).ToArray());
        Assert.Empty(tags.Ids(CatalogSnapshotFixtures.ItemType, 12_345).ToArray());

        Assert.Equal(
            [CatalogRuntimeFixtures.SwordTag, CatalogRuntimeFixtures.MetalTag],
            tags.TagsOf(CatalogSnapshotFixtures.ItemType).ToArray());
        Assert.True(tags.Carries(CatalogSnapshotFixtures.ItemType, CatalogRuntimeFixtures.MetalTag, 8));
        Assert.False(tags.Carries(CatalogSnapshotFixtures.ItemType, CatalogRuntimeFixtures.MetalTag, 1));
    }

    [Fact]
    public void The_tag_index_holds_a_retired_row_and_leaves_the_filtering_to_the_reader()
    {
        ContentRuntime runtime = CatalogRuntimeFixtures.Runtime(out _);

        // Item 35 is retired and carries the sword tag. An index that dropped it could not answer an admin
        // listing, and the drop path filters on the retired bit itself.
        Assert.True(runtime.IsRetired(CatalogSnapshotFixtures.ItemType, 35));
        Assert.True(runtime.Indexes.Tags.Carries(CatalogSnapshotFixtures.ItemType, CatalogRuntimeFixtures.SwordTag, 35));
    }

    [Fact]
    public void Walking_a_tag_list_allocates_nothing()
    {
        ContentRuntime runtime = CatalogRuntimeFixtures.Runtime(out _);

        long warm = 0;
        for (int i = 0; i < 64; i++)
        {
            warm += runtime.Indexes.Tags.Ids(CatalogSnapshotFixtures.ItemType, CatalogRuntimeFixtures.SwordTag).Length;
        }

        Assert.True(warm > 0);

        CatalogAllocAssert.NoPerCallAllocation("the tag index walk", () =>
        {
            long total = 0;
            for (int i = 0; i < 1000; i++)
            {
                ReadOnlySpan<int> ids = runtime.Indexes.Tags
                    .Ids(CatalogSnapshotFixtures.ItemType, CatalogRuntimeFixtures.SwordTag);
                for (int n = 0; n < ids.Length; n++)
                {
                    total += ids[n];
                }
            }

            Assert.True(total > 0);
        });
    }

    [Fact]
    public void Family_membership_is_the_two_comparison_block_test()
    {
        // (id & ~(size - 1)) == base, against each block of the family, which is contracts 5.2's whole test.
        var block = new ContentIdBlock(CatalogSnapshotFixtures.ItemType, 65_536, 64);

        Assert.True(block.IsWellFormed);
        Assert.True(block.Contains(65_536));
        Assert.True(block.Contains(65_599));
        Assert.False(block.Contains(65_535));
        Assert.False(block.Contains(65_600));
        Assert.Equal(65_600, block.Top);

        ContentFamilyIndex index = ContentFamilyIndex.FromBlocks(
        [
            new ContentIdBlock(CatalogSnapshotFixtures.ItemType, 65_600, 64),
            block,
        ]);

        // The blocks come back ordered by type then base, whatever order they went in, so a walk over a
        // family's blocks is stable.
        Assert.Equal([65_536, 65_600], new[] { index.Blocks[0].Base, index.Blocks[1].Base });
        Assert.True(index.TryGetBlock(CatalogSnapshotFixtures.ItemType, 65_601, out ContentIdBlock found));
        Assert.Equal(65_600, found.Base);
        Assert.False(index.IsInAnyBlock(CatalogSnapshotFixtures.ItemType, 65_535));
        Assert.False(index.IsInAnyBlock(CatalogSnapshotFixtures.TagType, 65_536));
    }

    [Fact]
    public void A_block_that_is_not_aligned_to_its_own_size_is_refused()
    {
        // An unaligned or non-power-of-two block would make the mask test answer for ids outside it, so it is
        // refused where it is declared rather than quietly producing wrong membership at every draw.
        Assert.Throws<ArgumentNullException>(() => ContentFamilyIndex.FromBlocks(null!));
        Assert.Throws<ArgumentException>(() => ContentFamilyIndex.FromBlocks(
            [new ContentIdBlock(CatalogSnapshotFixtures.ItemType, 1_000, 64)]));
        Assert.Throws<ArgumentException>(() => ContentFamilyIndex.FromBlocks(
            [new ContentIdBlock(CatalogSnapshotFixtures.ItemType, 1_024, 100)]));
        Assert.Throws<ArgumentException>(() => ContentFamilyIndex.FromBlocks(
            [new ContentIdBlock(CatalogSnapshotFixtures.ItemType, 8, 8)]));
        Assert.Throws<ArgumentException>(() => ContentFamilyIndex.FromBlocks(
            [new ContentIdBlock(CatalogSnapshotFixtures.ItemType, 131_072, 131_072)]));
    }

    [Fact]
    public void A_version_loaded_from_a_pack_declares_no_family_at_all()
    {
        // A family and its blocks are AUTHORING rows and nothing in the four pack formats carries them, so
        // the read side has no source and the index does not invent one out of the id clustering.
        // https://github.com/APKiwiOrg/KhaozEngine/issues/934
        ContentRuntime runtime = CatalogRuntimeFixtures.Runtime(out _);

        Assert.Same(ContentFamilyIndex.Empty, runtime.Indexes.Families);
        Assert.Empty(runtime.Indexes.Families.Blocks);
        Assert.False(runtime.Indexes.Families.IsInAnyBlock(CatalogSnapshotFixtures.ItemType, 2));
    }

    [Fact]
    public void Loot_candidates_are_per_table_with_the_weights_prefix_summed()
    {
        ContentRuntime runtime = CatalogLootFixtures.Runtime(out _);
        ContentLootIndex loot = runtime.Indexes.Loot;

        Assert.Equal(2, loot.TableCount);
        Assert.Equal(3, loot.EntryCount(CatalogLootFixtures.GoblinTable));
        Assert.Equal(2, loot.RollCount(CatalogLootFixtures.GoblinTable));
        Assert.False(loot.IsGuaranteed(CatalogLootFixtures.GoblinTable));

        // Weights 10, 30 and 60 in sort order become the running totals 10, 40 and 100, so a draw is
        // NextInt(0, 100) plus one binary search with no summation.
        Assert.Equal([10, 40, 100], loot.PrefixWeights(CatalogLootFixtures.GoblinTable).ToArray());
        Assert.Equal(100, loot.TotalWeight(CatalogLootFixtures.GoblinTable));

        ReadOnlySpan<int> weights = loot.PrefixWeights(CatalogLootFixtures.GoblinTable);
        for (int i = 1; i < weights.Length; i++)
        {
            Assert.True(weights[i] >= weights[i - 1]);
        }

        // One binary search, which is what monotonic running totals are for.
        Assert.Equal(1, IndexForRoll(weights, 10));
        Assert.Equal(0, IndexForRoll(weights, 9));
        Assert.Equal(2, IndexForRoll(weights, 99));
    }

    [Fact]
    public void Loot_entries_are_ordered_by_sort_then_by_id_and_carry_what_a_draw_reads()
    {
        ContentRuntime runtime = CatalogLootFixtures.Runtime(out _);
        ReadOnlySpan<ContentLootEntry> entries = runtime.Indexes.Loot.Entries(CatalogLootFixtures.GoblinTable);

        Assert.Equal([0, 1, 2], new[] { entries[0].Sort, entries[1].Sort, entries[2].Sort });
        Assert.Equal([202, 201, 203], new[] { entries[0].EntryId, entries[1].EntryId, entries[2].EntryId });

        // The first entry is a plain item draw, the second recurses into a table, and nothing else is set on
        // either, which is KEC0023's exactly-one rule seen from the read side.
        Assert.Equal(2, entries[0].ItemId);
        Assert.Equal(0, entries[0].NestedTableId);
        Assert.Equal(5_000, entries[0].ChanceBasisPoints);
        Assert.Equal(1, entries[0].MinCount);
        Assert.Equal(3, entries[0].MaxCount);
        Assert.Equal(CatalogLootFixtures.RareTable, entries[1].NestedTableId);
        Assert.Equal(0, entries[1].ItemId);
    }

    [Fact]
    public void A_tag_filtered_entry_resolves_to_live_items_carrying_every_listed_tag()
    {
        ContentRuntime runtime = CatalogLootFixtures.Runtime(out _);
        ContentLootIndex loot = runtime.Indexes.Loot;

        // The third entry filters on the sword tag. Items 1, 2 and 35 carry it and 35 is retired, so a drop
        // that offered it would offer something out of play.
        ReadOnlySpan<int> candidates = loot.Candidates(CatalogLootFixtures.GoblinTable, 2);

        Assert.Equal([1, 2], candidates.ToArray());
        Assert.Empty(loot.Candidates(CatalogLootFixtures.GoblinTable, 0).ToArray());
        Assert.Empty(loot.Candidates(CatalogLootFixtures.GoblinTable, 1).ToArray());

        // The rare table's entry filters on BOTH tags, which is an intersection rather than a union.
        Assert.Equal([2], loot.Candidates(CatalogLootFixtures.RareTable, 0).ToArray());
    }

    [Fact]
    public void An_entry_naming_a_table_the_version_does_not_carry_is_dropped_rather_than_failing_the_load()
    {
        ContentRuntime runtime = CatalogLootFixtures.Runtime(out _);

        // The fixture carries one orphan entry. It is KEC0006's finding, and an index that threw on it would
        // fail the boot before the validator ever got to say so.
        Assert.Equal(3, runtime.Indexes.Loot.EntryCount(CatalogLootFixtures.GoblinTable));
        Assert.Equal(1, runtime.Indexes.Loot.EntryCount(CatalogLootFixtures.RareTable));
        Assert.Equal(0, runtime.Indexes.Loot.EntryCount(CatalogLootFixtures.MissingTable));
        Assert.Empty(runtime.Indexes.Loot.PrefixWeights(CatalogLootFixtures.MissingTable).ToArray());
    }

    [Fact]
    public void A_negative_weight_is_clamped_so_the_prefix_array_stays_searchable()
    {
        ContentRuntime runtime = CatalogLootFixtures.RuntimeWithWeights([-5, 20, 0], out _);
        ReadOnlySpan<int> weights = runtime.Indexes.Loot.PrefixWeights(CatalogLootFixtures.GoblinTable);

        Assert.Equal([0, 20, 20], weights.ToArray());
        for (int i = 1; i < weights.Length; i++)
        {
            Assert.True(weights[i] >= weights[i - 1]);
        }
    }

    [Fact]
    public void A_weight_total_that_would_overflow_saturates_rather_than_wrapping_negative()
    {
        ContentRuntime runtime = CatalogLootFixtures.RuntimeWithWeights(
            [int.MaxValue, int.MaxValue, 1], out _);
        ReadOnlySpan<int> weights = runtime.Indexes.Loot.PrefixWeights(CatalogLootFixtures.GoblinTable);

        Assert.Equal([int.MaxValue, int.MaxValue, int.MaxValue], weights.ToArray());
        Assert.Equal(int.MaxValue, runtime.Indexes.Loot.TotalWeight(CatalogLootFixtures.GoblinTable));
    }

    [Fact]
    public void A_version_with_no_loot_table_indexes_nothing_and_answers_empty()
    {
        ContentRuntime runtime = CatalogRuntimeFixtures.Runtime(out _);

        Assert.Same(ContentLootIndex.Empty, runtime.Indexes.Loot);
        Assert.Equal(0, runtime.Indexes.Loot.TableCount);
        Assert.Equal(0, runtime.Indexes.Loot.EntryCount(1));
        Assert.Empty(runtime.Indexes.Loot.Entries(1).ToArray());
        Assert.Empty(runtime.Indexes.Loot.Candidates(1, 0).ToArray());
        Assert.Equal(0, runtime.Indexes.Loot.TotalWeight(1));
        Assert.False(runtime.Indexes.Loot.IsGuaranteed(1));
    }

    [Fact]
    public void A_guaranteed_table_says_so_and_carries_its_roll_count()
    {
        ContentRuntime runtime = CatalogLootFixtures.Runtime(out _);

        Assert.True(runtime.Indexes.Loot.IsGuaranteed(CatalogLootFixtures.RareTable));
        Assert.Equal(1, runtime.Indexes.Loot.RollCount(CatalogLootFixtures.RareTable));
    }

    /// <summary>The draw a prefix-summed array is for: one binary search, no summation, no allocation.</summary>
    static int IndexForRoll(ReadOnlySpan<int> prefixWeights, int roll)
    {
        int found = prefixWeights.BinarySearch(roll);
        return found >= 0 ? found + 1 : ~found;
    }
}
