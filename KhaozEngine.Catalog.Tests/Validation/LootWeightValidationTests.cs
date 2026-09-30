using KhaozEngine.Catalog;
using Xunit;
using static KhaozEngine.Tests.Catalog.Validation.ContentValidationFixtures;

namespace KhaozEngine.Tests.Catalog.Validation;

/// <summary>The publish-time bounds that keep a loot index's prefix sums exact.</summary>
public class LootWeightValidationTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(2147483648L)]
    public void KEC0043_rejects_a_weight_outside_the_runtime_int_range(long weight)
    {
        ContentTypeRegistry registry = EngineRegistry();
        ContentSnapshot candidate = Snapshot(
            registry,
            Item(7, "sword"),
            LootTable(100, "goblin"),
            LootEntry(500, "goblin_sword", table: 100, item: 7, weight: weight));

        ContentValidationReport report = Validate(candidate, registry);

        ContentFinding finding = Single(report, "KEC0043");
        Assert.Equal(LootEntryType, finding.Type);
        Assert.Equal(500, finding.Id);
    }

    [Fact]
    public void KEC0043_rejects_a_weighted_pool_over_int_max()
    {
        ContentTypeRegistry registry = EngineRegistry();
        ContentSnapshot candidate = Snapshot(
            registry,
            Item(7, "sword"),
            LootTable(100, "goblin"),
            LootEntry(500, "goblin_sword", table: 100, item: 7, weight: int.MaxValue),
            LootEntry(501, "goblin_sword_bonus", table: 100, item: 7, weight: 1));

        ContentValidationReport report = Validate(candidate, registry);

        ContentFinding finding = Single(report, "KEC0043");
        Assert.Equal(LootTableType, finding.Type);
        Assert.Equal(100, finding.Id);
    }

    [Fact]
    public void A_weighted_pool_at_int_max_is_valid()
    {
        ContentTypeRegistry registry = EngineRegistry();
        ContentSnapshot candidate = Snapshot(
            registry,
            Item(7, "sword"),
            LootTable(100, "goblin"),
            LootEntry(500, "goblin_sword", table: 100, item: 7, weight: int.MaxValue));

        ContentValidationReport report = Validate(candidate, registry);

        AssertNone(report, "KEC0043");
        Assert.True(report.IsValid, Describe(report));
    }
}
