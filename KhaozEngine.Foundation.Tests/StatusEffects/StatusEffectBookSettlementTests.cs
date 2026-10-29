using System.Collections.Generic;
using KhaozEngine.StatusEffects;
using Xunit;

namespace KhaozEngine.Tests.StatusEffects;

public sealed class StatusEffectBookSettlementTests
{
    [Fact]
    public void Apply_AfterInactiveGap_ExpiresOldWindowAndStartsFreshCadence()
    {
        var book = Create();
        book.Apply(7, new(1), 0);
        Events(book);
        book.Advance(4);
        Assert.Equal(new[] { Event(StatusEffectEventKind.Periodic, 2),
            Event(StatusEffectEventKind.Periodic, 4) }, Events(book));

        Assert.Equal(StatusEffectApplyResult.Applied, book.Apply(7, new(1), 10));

        Assert.Equal(new StatusEffectEntry(new(1), 0, 1, 10, 15), Assert.Single(Entries(book)));
        Assert.Equal(new[]
        {
            Event(StatusEffectEventKind.Ended, 5, StatusEffectEndReason.Expired),
            Event(StatusEffectEventKind.Applied, 10)
        }, Events(book));
        book.Advance(10);
        Assert.Empty(Events(book));
        book.Advance(12);
        Assert.Equal(Event(StatusEffectEventKind.Periodic, 12), Assert.Single(Events(book)));
    }

    [Fact]
    public void Apply_AtEndBoundary_RaisesFinalPeriodicThenExpiryBeforeFreshApplication()
    {
        var book = new StatusEffectBook(new StatusEffectCatalog(new[]
        {
            new StatusEffectDefinition(new(1), 4, maxStacks: 3, periodTicks: 2)
        }));
        book.Apply(7, new(1), 0);
        book.Advance(2);
        Events(book);

        Assert.Equal(StatusEffectApplyResult.Applied, book.Apply(7, new(1), 4));

        Assert.Equal(new StatusEffectEntry(new(1), 0, 1, 4, 8), Assert.Single(Entries(book)));
        Assert.Equal(new[]
        {
            Event(StatusEffectEventKind.Periodic, 4),
            Event(StatusEffectEventKind.Ended, 4, StatusEffectEndReason.Expired),
            Event(StatusEffectEventKind.Applied, 4)
        }, Events(book));
        book.Advance(4);
        Assert.Empty(Events(book));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Apply_AtDueTick_RaisesPeriodicOnceWithPreApplicationState(bool advanceFirst)
    {
        var book = Create();
        book.Apply(7, new(1), 0, source: 11, magnitude: 3);
        Events(book);

        if (advanceFirst)
            book.Advance(2);
        Assert.Equal(StatusEffectApplyResult.Stacked,
            book.Apply(7, new(1), 2, source: 12, magnitude: 9));
        book.Advance(2);

        Assert.Equal(new[]
        {
            new StatusEffectEvent(StatusEffectEventKind.Periodic, 7, new(1), 11, 1, 3, 2,
                StatusEffectEndReason.None),
            new StatusEffectEvent(StatusEffectEventKind.Stacked, 7, new(1), 12, 2, 9, 2,
                StatusEffectEndReason.None)
        }, Events(book));
        Assert.Equal(new StatusEffectEntry(new(1), 12, 2, 2, 7), Assert.Single(Entries(book)));
    }

    [Fact]
    public void Apply_ExpiredOtherEffect_ReleasesCapacityBeforeAdmission()
    {
        var book = new StatusEffectBook(new StatusEffectCatalog(new[]
        {
            new StatusEffectDefinition(new(1), 5),
            new StatusEffectDefinition(new(2), 5)
        }), maxEffectsPerActor: 1);
        book.Apply(7, new(1), 0);
        Events(book);

        Assert.Equal(StatusEffectApplyResult.Applied, book.Apply(7, new(2), 10));

        Assert.Equal(new StatusEffectEntry(new(2), 0, 1, 10, 15), Assert.Single(Entries(book)));
        Assert.Equal(new[]
        {
            Event(StatusEffectEventKind.Ended, 5, StatusEffectEndReason.Expired),
            new StatusEffectEvent(StatusEffectEventKind.Applied, 7, new(2), 0, 1, 0, 10,
                StatusEffectEndReason.None)
        }, Events(book));
        book.Advance(10);
        Assert.Empty(Events(book));
    }

    [Fact]
    public void ApplyBeforeAdvance_AndAdvanceBeforeApply_SettleAllTargetInstancesInApplicationOrder()
    {
        var definitions = new[]
        {
            new StatusEffectDefinition(new(1), 5, maxStacks: 3, periodTicks: 2),
            new StatusEffectDefinition(new(2), 3, periodTicks: 3)
        };
        var applyFirst = new StatusEffectBook(new StatusEffectCatalog(definitions));
        var advanceFirst = new StatusEffectBook(new StatusEffectCatalog(definitions));
        foreach (var book in new[] { applyFirst, advanceFirst })
        {
            book.Apply(7, new(1), 0);
            book.Apply(7, new(2), 0);
            Events(book);
        }

        applyFirst.Apply(7, new(1), 10);
        applyFirst.Advance(10);
        advanceFirst.Advance(10);
        advanceFirst.Apply(7, new(1), 10);

        var expectedEvents = new[]
        {
            Event(StatusEffectEventKind.Periodic, 2),
            Event(StatusEffectEventKind.Periodic, 4),
            Event(StatusEffectEventKind.Ended, 5, StatusEffectEndReason.Expired),
            new StatusEffectEvent(StatusEffectEventKind.Periodic, 7, new(2), 0, 1, 0, 3,
                StatusEffectEndReason.None),
            new StatusEffectEvent(StatusEffectEventKind.Ended, 7, new(2), 0, 1, 0, 3,
                StatusEffectEndReason.Expired),
            Event(StatusEffectEventKind.Applied, 10)
        };
        Assert.Equal(expectedEvents, Events(applyFirst));
        Assert.Equal(expectedEvents, Events(advanceFirst));
        var expectedEntries = new[] { new StatusEffectEntry(new(1), 0, 1, 10, 15) };
        Assert.Equal(expectedEntries, Entries(applyFirst));
        Assert.Equal(expectedEntries, Entries(advanceFirst));
        Assert.Equal(applyFirst.ModifierVersion(7), advanceFirst.ModifierVersion(7));
        var changedApplyFirst = new List<long>();
        var changedAdvanceFirst = new List<long>();
        applyFirst.DrainChangedActors(changedApplyFirst);
        advanceFirst.DrainChangedActors(changedAdvanceFirst);
        Assert.Equal(new long[] { 7 }, changedApplyFirst);
        Assert.Equal(changedApplyFirst, changedAdvanceFirst);
    }

    private static StatusEffectBook Create() => new(new StatusEffectCatalog(new[]
    {
        new StatusEffectDefinition(new(1), 5, maxStacks: 3, periodTicks: 2)
    }));

    private static StatusEffectEvent Event(StatusEffectEventKind kind, long tick,
        StatusEffectEndReason reason = StatusEffectEndReason.None) => new(kind, 7, new(1), 0, 1, 0, tick, reason);

    private static List<StatusEffectEntry> Entries(StatusEffectBook book)
    {
        var entries = new List<StatusEffectEntry>();
        book.CopyEntries(7, entries, shownOnly: false);
        return entries;
    }

    private static List<StatusEffectEvent> Events(StatusEffectBook book)
    {
        var events = new List<StatusEffectEvent>();
        book.DrainEvents(events);
        return events;
    }
}
