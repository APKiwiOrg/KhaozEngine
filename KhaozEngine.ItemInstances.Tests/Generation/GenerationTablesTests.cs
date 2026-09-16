using System;
using System.Collections.Generic;
using System.Globalization;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using Xunit;
using static KhaozEngine.Tests.ItemInstances.Content.InstanceContentTypeFixtures;
using static KhaozEngine.Tests.ItemInstances.Content.InstanceValidationFixtures;

namespace KhaozEngine.Tests.ItemInstances.Generation;

/// <summary>
/// The refusals <see cref="GenerationTables"/> makes at BUILD, which are the ones a roll cannot make for
/// itself without failing open.
/// <para>
/// Each mirrors a publish finding at the fold, for the version that reached a boot without a publish. A
/// publish is the right place for an authoring error and a boot is the last place it can be caught, so what
/// these two refuse are the two shapes whose runtime failure is SILENT: a run list that fills leaves a mod
/// live, and an uncoverable name position shifts every later word down one place in kind 134.
/// </para>
/// <para>
/// Every registry a fact builds is its own, so nothing here writes process-global state.
/// </para>
/// </summary>
public class GenerationTablesTests
{
    [Fact]
    public void A_worst_case_run_count_past_the_ceiling_is_REFUSED_rather_than_clamped()
    {
        // The worst case is the widest rarity's affix count times the widest mod group, plus one run each,
        // bounded by the widest bucket. A clamp down to the ceiling would put the failure at the seat,
        // where the only thing left to do is drop a run and leave a mod LIVE.
        // The ceiling is 4,096 runs a tag table, and 255 affixes times 16 group members plus 255 is 4,335,
        // so a bucket of 4,160 distinct mods is the cheapest shape past it.
        const int groupMembers = 16;
        const int mods = 4_160;
        ContentTypeRegistry registry = Registry();
        var rows = new List<ContentRow>
        {
            Tag(registry, 5, "metal"),
            Item(registry, 40, "greatsword", [5]),
            ModGroup(registry, 1, "crowded", maxPerItem: 1),
            RarityRule(
                registry,
                1,
                "grand",
                minAffixes: byte.MaxValue,
                maxAffixes: byte.MaxValue,
                maxPrefixes: byte.MaxValue,
                maxSuffixes: byte.MaxValue),
        };

        for (int mod = 1; mod <= mods; mod++)
        {
            string name = string.Create(CultureInfo.InvariantCulture, $"m{mod}");
            rows.Add(mod <= groupMembers
                ? Mod(registry, mod, name, kind: ModContentType.PrefixKind, group: 1)
                : Mod(registry, mod, name, kind: ModContentType.PrefixKind));
            rows.Add(ModTier(registry, mod, string.Create(CultureInfo.InvariantCulture, $"t{mod}"), modId: mod, ordinal: 1));
            rows.Add(ModTierWeight(registry, mod, string.Create(CultureInfo.InvariantCulture, $"w{mod}"), tierId: mod, tagId: 5, weight: 1));
        }

        ContentSnapshot candidate = Snapshot(registry, [.. rows]);
        ModCandidateTables tables = ModCandidateTables.Build(candidate);

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => GenerationTables.Build(tables, candidate));
        Assert.Contains("4096", failure.Message, StringComparison.Ordinal);
        Assert.Contains("LIVE", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reachable_rarity_with_an_uncoverable_name_position_is_REFUSED_at_the_fold()
    {
        // KEC0109's second clause refuses this at publish, so the hole is only reachable on a snapshot that
        // bypassed one. What it protects against is not a MISSING name: step 10 writes the words it rolled
        // in order, so a skipped position shifts every later word down one place in kind 134 and the item
        // is named by a misaligned list nothing in the payload flags.
        ContentTypeRegistry registry = GenerationWorld.World();
        List<ContentRow> rows = GenerationWorld.Rows(registry);
        rows.RemoveAll(row => row.Type.Value == InstanceContentTypeIds.RareNameWordWeightTypeId && row.Id == 1);

        ContentSnapshot candidate = Snapshot(registry, [.. rows]);
        ModCandidateTables tables = ModCandidateTables.Build(candidate);

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => GenerationTables.Build(tables, candidate));
        Assert.Contains(InstanceContentFindings.RareNameCoverage, failure.Message, StringComparison.Ordinal);

        // And the whole world, with both positions covered, still builds. What is refused is the UNCOVERED
        // position rather than a rarity that rolls a name at all.
        ContentSnapshot whole = GenerationWorld.Candidate(registry);
        GenerationTables built = GenerationTables.Build(ModCandidateTables.Build(whole), whole);
        Assert.Equal(whole.VersionNumber, built.ContentVersion);
    }

    [Fact]
    public void A_rarity_reachable_at_NO_signature_is_not_asked_to_cover_anything()
    {
        // The refusal is per reachable (rarity, signature) pair, which is what keeps it the weaker half of
        // KEC0109 rather than a stricter rule of its own. A rarity no base's tags can roll has no name to
        // fail to compose, so dropping its weight row makes an otherwise uncoverable position legal.
        ContentTypeRegistry registry = GenerationWorld.World();
        List<ContentRow> rows = GenerationWorld.Rows(registry);
        rows.RemoveAll(row => row.Type.Value == InstanceContentTypeIds.RareNameWordWeightTypeId && row.Id == 1);
        rows.RemoveAll(row => row.Type.Value == InstanceContentTypeIds.RarityWeightTypeId && row.Id == 2);

        ContentSnapshot candidate = Snapshot(registry, [.. rows]);
        GenerationTables built = GenerationTables.Build(ModCandidateTables.Build(candidate), candidate);
        Assert.Equal(candidate.VersionNumber, built.ContentVersion);
    }
}
