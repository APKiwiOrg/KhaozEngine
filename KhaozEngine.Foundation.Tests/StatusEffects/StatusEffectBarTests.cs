using System;
using System.Collections.Generic;
using KhaozEngine.StatusEffects;
using Xunit;

namespace KhaozEngine.Tests.StatusEffects;

public sealed class StatusEffectBarTests
{
    [Fact]
    public void Build_ComputesRemainingAndFraction()
    {
        var catalog = Create(new StatusEffectDefinition(new(1), 30));
        var rows = new List<StatusEffectBarItem>();

        StatusEffectBar.Build(new[] { new StatusEffectEntry(new(1), 7, 2, 100, 130) }, catalog, 115, rows);

        var row = Assert.Single(rows);
        Assert.Equal(15d, row.RemainingTicks);
        Assert.Equal(0.5f, row.Fraction);
    }

    [Fact]
    public void Build_ClampsFractionBeforeStart()
    {
        var catalog = Create(new StatusEffectDefinition(new(1), 30));
        var rows = new List<StatusEffectBarItem>();

        StatusEffectBar.Build(new[] { new StatusEffectEntry(new(1), 7, 1, 100, 130) }, catalog, 90, rows);

        var row = Assert.Single(rows);
        Assert.Equal(40d, row.RemainingTicks);
        Assert.Equal(1f, row.Fraction);
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
    public void Build_ZeroLengthWindowHasZeroFraction()
    {
        var catalog = Create(new StatusEffectDefinition(new(1), 30));
        var rows = new List<StatusEffectBarItem>();

        StatusEffectBar.Build(new[] { new StatusEffectEntry(new(1), 7, 1, 130, 130) }, catalog, 115, rows);

        var row = Assert.Single(rows);
        Assert.Equal(15d, row.RemainingTicks);
        Assert.Equal(0f, row.Fraction);
    }

    [Fact]
    public void Build_PreservesFractionalTicks()
    {
        var catalog = Create(new StatusEffectDefinition(new(1), 30));
        var rows = new List<StatusEffectBarItem>();

        StatusEffectBar.Build(new[] { new StatusEffectEntry(new(1), 7, 1, 100, 130) }, catalog, 122.5, rows);

        var row = Assert.Single(rows);
        Assert.Equal(7.5, row.RemainingTicks);
        Assert.Equal(0.25f, row.Fraction);
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
