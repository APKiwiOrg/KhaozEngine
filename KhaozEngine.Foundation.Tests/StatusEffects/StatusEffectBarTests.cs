using System;
using System.Collections.Generic;
using KhaozEngine.StatusEffects;
using Xunit;

namespace KhaozEngine.Tests.StatusEffects;

public sealed class StatusEffectBarTests
{
    [Theory]
    [InlineData(100L, 130L, 2, 115d, 15d, 0.5f)]
    [InlineData(100L, 130L, 1, 90d, 40d, 1f)]
    [InlineData(130L, 130L, 1, 115d, 15d, 0f)]
    [InlineData(100L, 130L, 1, 122.5d, 7.5d, 0.25f)]
    public void Build_ComputesRemainingAndClampedFraction_ForPresentationWindows(
        long start, long end, int stacks, double now, double expectedRemaining, float expectedFraction)
    {
        var catalog = Create(new StatusEffectDefinition(new(1), 30));
        var rows = new List<StatusEffectBarItem>();

        StatusEffectBar.Build(new[] { new StatusEffectEntry(new(1), 7, stacks, start, end) }, catalog, now, rows);

        var row = Assert.Single(rows);
        Assert.Equal(expectedRemaining, row.RemainingTicks);
        Assert.Equal(expectedFraction, row.Fraction);
    }

    [Fact]
    public void Build_SkipsExpiredAndUnknown()
    {
        var catalog = Create(new StatusEffectDefinition(new(1), 30));
        StatusEffectEntry[] entries =
        [
            new(new(1), 1, 1, 80, 110),
            new(new(1), 2, 1, 85, 115),
            new(new(99), 3, 1, 100, 130),
            new(new(1), 4, 1, 100, 130)
        ];
        var rows = new List<StatusEffectBarItem>();

        StatusEffectBar.Build(entries, catalog, 115, rows);

        Assert.Equal(4, Assert.Single(rows).Source);
    }

    [Fact]
    public void Build_KeepsEntryOrderAndCopiesPresentation()
    {
        var catalog = Create(
            new StatusEffectDefinition(new(1), 30, polarity: StatusEffectPolarity.Harmful, iconKey: "icon-a"),
            new(new(2), 30, shown: false, iconKey: "icon-b"));
        StatusEffectEntry[] entries =
        [
            new(new(2), 12, 3, 100, 130),
            new(new(1), 11, 2, 100, 120),
            new(new(2), 13, 1, 110, 130)
        ];
        var rows = new List<StatusEffectBarItem>();

        StatusEffectBar.Build(entries, catalog, 115, rows);

        Assert.Equal(new[]
        {
            new StatusEffectBarItem(new(2), "icon-b", StatusEffectPolarity.Beneficial, 12, 3, 15, 0.5f),
            new StatusEffectBarItem(new(1), "icon-a", StatusEffectPolarity.Harmful, 11, 2, 5, 0.25f),
            new StatusEffectBarItem(new(2), "icon-b", StatusEffectPolarity.Beneficial, 13, 1, 15, 0.75f)
        }, rows);
    }

    [Fact]
    public void Build_AppendsWithoutClearingExistingRows()
    {
        var catalog = Create(new StatusEffectDefinition(new(1), 30));
        var sentinel = new StatusEffectBarItem(new(9), "existing", StatusEffectPolarity.Harmful, 99, 1, 8, 1);
        var rows = new List<StatusEffectBarItem> { sentinel };

        StatusEffectBar.Build(new[] { new StatusEffectEntry(new(1), 7, 1, 100, 130) }, catalog, 115, rows);
        StatusEffectBar.Build(ReadOnlySpan<StatusEffectEntry>.Empty, catalog, 115, rows);

        Assert.Equal(2, rows.Count);
        Assert.Equal(sentinel, rows[0]);
        Assert.Equal(new StatusEffectId(1), rows[1].Effect);
    }

    [Fact]
    public void Build_DurationBeyondLongRangeDoesNotOverflow()
    {
        var catalog = Create(new StatusEffectDefinition(new(1), 30));
        var rows = new List<StatusEffectBarItem>();

        StatusEffectBar.Build(new[] { new StatusEffectEntry(new(1), 7, 1, long.MinValue, long.MaxValue) },
            catalog, 0, rows);

        var row = Assert.Single(rows);
        Assert.Equal((double)long.MaxValue, row.RemainingTicks);
        Assert.Equal(0.5f, row.Fraction);
    }

    [Fact]
    public void Build_RejectsNullCatalog()
    {
        var rows = new List<StatusEffectBarItem>();

        Assert.Throws<ArgumentNullException>(() =>
            StatusEffectBar.Build(ReadOnlySpan<StatusEffectEntry>.Empty, null!, 0, rows));
    }

    [Fact]
    public void Build_RejectsNullDestination()
    {
        var catalog = Create(new StatusEffectDefinition(new(1), 30));

        Assert.Throws<ArgumentNullException>(() =>
            StatusEffectBar.Build(ReadOnlySpan<StatusEffectEntry>.Empty, catalog, 0, null!));
    }

    private static StatusEffectCatalog Create(params StatusEffectDefinition[] definitions) => new(definitions);
}
