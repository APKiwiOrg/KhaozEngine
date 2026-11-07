using System;
using System.Collections.Generic;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using Xunit;
using static KhaozEngine.Tests.ItemInstances.Content.InstanceContentTypeFixtures;
using static KhaozEngine.Tests.ItemInstances.Content.InstanceValidationFixtures;

namespace KhaozEngine.Tests.ItemInstances.Generation;

/// <summary>Boot admission for candidate tables, with each refusal beside its repaired boundary control.</summary>
public class ModCandidateTablesRefusalTests
{
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

        GenerationTableException failure = Assert.Throws<GenerationTableException>(
            () => ModCandidateTables.Build(candidate));
        Assert.Equal(GenerationTableRefusal.BucketEntryCountExceeded, failure.Reason);
        Assert.Equal(5, failure.TagId);
        Assert.Equal(1, failure.ModKind);
        Assert.Equal(0, failure.Band);
        Assert.Equal(65_550L, failure.ActualCount);
        Assert.Equal(65_535, failure.MaximumCount);

        rows.RemoveAll(row => row.Type.Value == InstanceContentTypeIds.ModTierWeightTypeId && row.Id > 65_535);
        ModCandidateTables atLimit = ModCandidateTables.Build(Snapshot(registry, [.. rows]));
        Assert.Equal(65_535L, atLimit.TableEntries);
        rows.RemoveAll(row => row.Type.Value == InstanceContentTypeIds.ModTierWeightTypeId && row.Id == 65_535);
        ModCandidateTables belowLimit = ModCandidateTables.Build(Snapshot(registry, [.. rows]));
        Assert.Equal(65_534L, belowLimit.TableEntries);
    }

    [Fact]
    public void A_signatures_UNION_weight_past_int_MaxValue_THROWS_at_build_because_a_draw_is_bounded_by_an_int()
    {
        ContentTypeRegistry registry = Registry();

        // KEC0113 bounds the rows sharing one TAG, and this is the union over a base's tag positions, which
        // is a superset of any one of them. Two tags each holding 1.5 billion sit comfortably under the
        // per-tag ceiling and sum to 3 billion, which is what a roll would have to draw over.
        const int half = 1_500_000_000;
        ContentRow[] Rows(int secondWeight) =>
        [
            Tag(registry, 5, "metal"),
            Tag(registry, 6, "blade"),
            Item(registry, 40, "greatsword", [5, 6]),
            Mod(registry, 1, "sharp"),
            Mod(registry, 2, "keen"),
            ModTier(registry, 1, "sharp_t1", modId: 1, ordinal: 1),
            ModTier(registry, 2, "keen_t1", modId: 2, ordinal: 1),
            ModTierWeight(registry, 1, "sharp_t1_metal", tierId: 1, tagId: 5, weight: half),
            ModTierWeight(registry, 2, "keen_t1_blade", tierId: 2, tagId: 6, weight: secondWeight),
        ];

        GenerationTableException failure = Assert.Throws<GenerationTableException>(
            () => ModCandidateTables.Build(Snapshot(registry, Rows(half))));
        Assert.Equal(GenerationTableRefusal.PoolWeightExceeded, failure.Reason);
        Assert.Equal(0, failure.Signature);
        Assert.Equal(1, failure.ModKind);
        Assert.Equal(0, failure.Band);
        Assert.Equal(3_000_000_000L, failure.ActualWeight);
        Assert.Equal(2_147_483_647, failure.MaximumWeight);

        // Each bucket still fits. Repair only the union, at the inclusive limit and one below it.
        ModCandidateTables atLimit = ModCandidateTables.Build(Snapshot(registry, Rows(int.MaxValue - half)));
        Assert.True(atLimit.Signatures.TryGetSignature(40, out int signature));
        Assert.True(atLimit.TryGetKindPosition(ModContentType.PrefixKind, out int kind));
        Assert.Equal(2_147_483_647L, atLimit.LiveWeight(signature, kind, 0));
        ModCandidateTables belowLimit = ModCandidateTables.Build(Snapshot(registry, Rows(int.MaxValue - half - 1)));
        Assert.Equal(2_147_483_646L, belowLimit.LiveWeight(signature, kind, 0));
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

        GenerationTableException failure = Assert.Throws<GenerationTableException>(
            () => ModCandidateTables.Build(Snapshot(registry, Rows(16))));
        Assert.IsAssignableFrom<InvalidOperationException>(failure);
        Assert.Equal(GenerationTableRefusal.TierOrdinalExceeded, failure.Reason);
        Assert.Equal(1, failure.TierId);
        Assert.Equal(16L, failure.ActualOrdinal);
        Assert.Equal(15, failure.MaximumOrdinal);
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
        GenerationTableException failure = Assert.Throws<GenerationTableException>(
            () => ModCandidateTables.Build(Snapshot(registry, rows.ToArray())));
        Assert.Equal(GenerationTableRefusal.BaseTagCountExceeded, failure.Reason);
        Assert.Equal(40, failure.BaseId);
        Assert.Equal(9L, failure.ActualCount);
        Assert.Equal(8, failure.MaximumCount);

        // At the ceiling it builds, and the header space is the positions the signature actually has.
        rows[^1] = Item(registry, 40, "greatsword", tags.GetRange(0, ModCandidateTables.MaxGenerationTagPositions));
        ModCandidateTables tables = ModCandidateTables.Build(Snapshot(registry, rows.ToArray()));
        Assert.Equal(8, tables.TagPositionCount);
    }

    [Fact]
    public void A_bucket_weight_past_the_int_ceiling_is_refused_before_overlap()
    {
        ContentTypeRegistry registry = Registry();
        ContentRow[] Rows(int firstWeight) =>
        [
            Tag(registry, 5, "metal"),
            Tag(registry, 6, "wood"),
            Item(registry, 40, "greatsword", [6]),
            Mod(registry, 1, "sharp"),
            Mod(registry, 2, "keen"),
            ModTier(registry, 1, "sharp_t", modId: 1, ordinal: 1),
            ModTier(registry, 2, "keen_t", modId: 2, ordinal: 1),
            ModTierWeight(registry, 1, "sharp_weight", tierId: 1, tagId: 5, weight: firstWeight),
            ModTierWeight(registry, 2, "keen_weight", tierId: 2, tagId: 5, weight: 1),
        ];

        GenerationTableException failure = Assert.Throws<GenerationTableException>(
            () => ModCandidateTables.Build(Snapshot(registry, Rows(int.MaxValue))));
        Assert.Equal(GenerationTableRefusal.BucketWeightExceeded, failure.Reason);
        Assert.Equal(5, failure.TagId);
        Assert.Equal(1, failure.ModKind);
        Assert.Equal(0, failure.Band);
        Assert.Equal(2_147_483_648L, failure.ActualWeight);
        Assert.Equal(2_147_483_647, failure.MaximumWeight);

        ModCandidateTables atLimit = ModCandidateTables.Build(Snapshot(registry, Rows(int.MaxValue - 1)));
        Assert.True(atLimit.TryGetKindPosition(ModContentType.PrefixKind, out int kind));
        Assert.Equal(2_147_483_647, atLimit.BucketTotal(atLimit.BucketOf(5, kind, 0)));
        ModCandidateTables belowLimit = ModCandidateTables.Build(Snapshot(registry, Rows(int.MaxValue - 2)));
        Assert.Equal(2_147_483_646, belowLimit.BucketTotal(belowLimit.BucketOf(5, kind, 0)));
    }

    [Fact]
    public void A_mod_id_past_the_packed_key_ceiling_is_refused()
    {
        ContentTypeRegistry registry = Registry();
        ContentRow[] Rows(int modId) =>
        [
            Tag(registry, 5, "metal"),
            Item(registry, 40, "greatsword", [5]),
            Mod(registry, modId, "sharp"),
            ModTier(registry, 1, "sharp_t", modId: modId, ordinal: 1),
            ModTierWeight(registry, 1, "weight", tierId: 1, tagId: 5, weight: 3),
        ];

        GenerationTableException failure = Assert.Throws<GenerationTableException>(
            () => ModCandidateTables.Build(Snapshot(registry, Rows(134_217_728))));
        Assert.Equal(GenerationTableRefusal.PackedModIdExceeded, failure.Reason);
        Assert.Equal(134_217_728, failure.ModId);
        Assert.Equal(134_217_727, failure.MaximumModId);

        ModCandidateTables atLimit = ModCandidateTables.Build(Snapshot(registry, Rows(ModCandidateTables.MaxModId)));
        Assert.True(atLimit.TryGetKindPosition(ModContentType.PrefixKind, out int kind));
        int packed = Assert.Single(atLimit.BucketPacked(atLimit.BucketOf(5, kind, 0)).ToArray());
        Assert.Equal(134_217_727, ModCandidateTables.ModIdOf(packed));
    }
}
