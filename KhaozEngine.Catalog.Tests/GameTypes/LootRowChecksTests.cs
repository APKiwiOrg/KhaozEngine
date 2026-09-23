using System;
using System.Linq;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.GameTypes;
using Xunit;
using static KhaozEngine.Tests.Catalog.GameTypes.GameLootFixtures;
using static KhaozEngine.Tests.Catalog.GameTypes.GameTypeValidationFixtures;

namespace KhaozEngine.Tests.Catalog.GameTypes;

/// <summary>
/// The seven loot number rules of the cross-type sweep, <c>KGT1309</c> to <c>KGT1315</c>: numbers nobody
/// rolls against, one per half of the engine's composition rule.
/// </summary>
public class LootRowChecksTests
{
    [Fact]
    public void ALootEntryQuotingNumbersNobodyRollsAgainstIsRefusedByTheCrossTypeSweep()
    {
        ContentTypeRegistry registry = TypeRegistry();
        ContentTypeRegistration table = Registration(registry, EngineContentTypes.LootTableTypeId);
        ContentTypeRegistration entry = Registration(registry, EngineContentTypes.LootEntryTypeId);

        ContentSnapshot candidate = Snapshot(
            registry,
            LootTable(table, 1, "pouch_drops", rollCount: 1),
            LootEntry(entry, 1, "gated_pick", table: 1, weight: 1, guaranteed: false, chance: 2_500, item: 11),
            LootEntry(entry, 2, "heavy_certainty", table: 1, weight: 3, guaranteed: true, chance: 10_000, item: 11),
            LootEntry(entry, 3, "no_weight", table: 1, weight: 0, guaranteed: false, chance: 10_000, item: 11),
            LootEntry(entry, 4, "over_certain", table: 1, weight: 0, guaranteed: true, chance: 10_001, item: 11),
            LootEntry(entry, 5, "never_fires", table: 1, weight: 0, guaranteed: true, chance: 0, item: 11),
            LootEntry(entry, 6, "under_nothing", table: 1, weight: 0, guaranteed: true, chance: -1, item: 11),
            LootEntry(entry, 7, "plain_pick", table: 1, weight: 1, guaranteed: false, chance: 10_000, item: 11),
            LootEntry(entry, 8, "plain_certainty", table: 1, weight: 0, guaranteed: true, chance: 2_500, item: 11),
            LootEntry(entry, 9, "silent_pick", table: 1, weight: 1, guaranteed: false, chance: null, item: 11));

        // Each one changes a rate while the shares of the pool still add to 100 percent, and the engine
        // refuses none of them: its loot checks are KEC0023, an entry naming its draw exactly one way, and
        // KEC0024, a cycle through nested_table. The last three are the clean shapes every rule has to leave
        // alone, an ABSENT chance among them, because nothing authored is nothing to refuse.
        Assert.Equal(
            new[]
            {
                (1, GameContentFindings.SweepLootChanceNobodyRolls),
                (2, GameContentFindings.SweepLootWeightNobodyPicksBy),
                (3, GameContentFindings.SweepLootEntryNeverPicked),
                (4, GameContentFindings.SweepLootChanceOutOfRange),
                (5, GameContentFindings.SweepLootGuaranteedNeverFires),
                (6, GameContentFindings.SweepLootChanceOutOfRange),
            },
            Sweep(registry, candidate));

        // One code, two different things to tell an author. Above the scale the entry is a certainty and
        // the authored number is the part nobody rolls against. Below zero it drops nothing at all.
        string[] outOfRange = SweepFindings(registry, candidate)
            .Where(f => string.Equals(f.Code, GameContentFindings.SweepLootChanceOutOfRange, StringComparison.Ordinal))
            .Select(f => f.Message)
            .ToArray();
        Assert.Contains("is a certainty and consumes no draw", outOfRange[0], StringComparison.Ordinal);
        Assert.Contains("drops nothing and consumes no draw", outOfRange[1], StringComparison.Ordinal);
    }

    [Fact]
    public void ALootTableWhoseRollCountAndPoolDisagreeIsRefusedByTheCrossTypeSweep()
    {
        ContentTypeRegistry registry = TypeRegistry();
        ContentTypeRegistration table = Registration(registry, EngineContentTypes.LootTableTypeId);
        ContentTypeRegistration entry = Registration(registry, EngineContentTypes.LootEntryTypeId);

        ContentSnapshot candidate = Snapshot(
            registry,
            LootTable(table, 1, "picks_from_nothing", rollCount: 2),
            LootTable(table, 2, "picks_nobody_takes", rollCount: 0),
            LootTable(table, 3, "nothing", rollCount: 0),
            LootTable(table, 4, "rare", rollCount: 1),
            LootEntry(entry, 1, "only_certainty", table: 1, weight: 0, guaranteed: true, chance: 10_000, item: 11),
            LootEntry(entry, 2, "unrolled_pick", table: 2, weight: 1, guaranteed: false, chance: 10_000, item: 11),
            LootEntry(entry, 3, "rare_pick", table: 4, weight: 1, guaranteed: false, chance: 10_000, item: 11));

        // A guaranteed entry contributes no weight, so the first table's two picks draw over an empty pool.
        // The second holds a pick nothing ever takes. The 'nothing' table is the legal shape of the pair: no
        // picks AND no entries, which is what an entry drawing it uses to mean nothing.
        Assert.Equal(
            new[]
            {
                (1, GameContentFindings.SweepLootTableNothingToPick),
                (2, GameContentFindings.SweepLootTableRollsNobodyTakes),
            },
            Sweep(registry, candidate));
    }

    /// <summary>
    /// One weighted pick over four entries, every one at the certainty a weighted entry carries, which is the
    /// ordinary drop table. A rule that refused this would be refusing the content a world ships.
    /// </summary>
    [Fact]
    public void AnOrdinaryWeightedLootShapeSweepsClean()
    {
        ContentTypeRegistry registry = TypeRegistry();
        ContentTypeRegistration table = Registration(registry, EngineContentTypes.LootTableTypeId);
        ContentTypeRegistration entry = Registration(registry, EngineContentTypes.LootEntryTypeId);

        ContentSnapshot candidate = Snapshot(
            registry,
            LootTable(table, 1, "pouch_drops", rollCount: 1),
            LootEntry(entry, 1, "pouch_bread", table: 1, weight: 45, guaranteed: false, chance: 10_000, item: 11),
            LootEntry(entry, 2, "pouch_coins_2", table: 1, weight: 66, guaranteed: false, chance: 10_000, item: 12),
            LootEntry(entry, 3, "pouch_coins_4", table: 1, weight: 65, guaranteed: false, chance: 10_000, item: 12),
            LootEntry(entry, 8, "pouch_belt", table: 1, weight: 4, guaranteed: false, chance: 10_000, item: 13));

        Assert.Empty(Sweep(registry, candidate));
    }

    /// <summary>
    /// The loot rules read no knob, so they run under <see cref="GameContentSweepOptions.None"/> too.
    /// </summary>
    [Fact]
    public void TheLootNumberRulesRunWithNoKnobNamed()
    {
        ContentTypeRegistry registry = TypeRegistry();
        ContentTypeRegistration table = Registration(registry, EngineContentTypes.LootTableTypeId);
        ContentTypeRegistration entry = Registration(registry, EngineContentTypes.LootEntryTypeId);

        ContentSnapshot candidate = Snapshot(
            registry,
            LootTable(table, 1, "pouch_drops", rollCount: 1),
            LootEntry(entry, 1, "gated_pick", table: 1, weight: 1, guaranteed: false, chance: 2_500, item: 11));

        Assert.Equal(
            new[] { (1, GameContentFindings.SweepLootChanceNobodyRolls) },
            Sweep(registry, candidate, GameContentSweepOptions.None));
    }
}
