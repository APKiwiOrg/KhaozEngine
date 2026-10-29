using System;
using System.Collections.Generic;
using KhaozEngine.StatusEffects;
using Xunit;

namespace KhaozEngine.Tests.StatusEffects;

public sealed class StatusEffectBookAdvanceTests
{
    [Fact]
    public void Advance_ExpiresAtEndTick()
    {
        var book = Create(new StatusEffectDefinition(new(1), 30));
        book.Apply(7, new(1), 100);
        Events(book);

        book.Advance(129);
        Assert.True(book.Has(7, new(1)));
        Assert.Empty(Events(book));
        book.Advance(130);

        Assert.False(book.Has(7, new(1)));
        Assert.Equal(new StatusEffectEvent(StatusEffectEventKind.Ended, 7, new(1), 0, 1, 0, 130,
            StatusEffectEndReason.Expired), Assert.Single(Events(book)));
    }

    [Fact]
    public void Periodic_FiresOnCadenceAndAtEndBeforeExpiry()
    {
        var book = Create(new StatusEffectDefinition(new(1), 30, periodTicks: 10));
        book.Apply(7, new(1), 100);
        Events(book);

        for (long tick = 100; tick <= 130; tick++)
            book.Advance(tick);

        Assert.Equal(new[]
        {
            Event(StatusEffectEventKind.Periodic, 110),
            Event(StatusEffectEventKind.Periodic, 120),
            Event(StatusEffectEventKind.Periodic, 130),
            Event(StatusEffectEventKind.Ended, 130, StatusEffectEndReason.Expired)
        }, Events(book));
    }

    [Fact]
    public void Periodic_CatchUpAfterGap()
    {
        var book = Create(new StatusEffectDefinition(new(1), 30, periodTicks: 10));
        book.Apply(7, new(1), 100);
        Events(book);

        book.Advance(200);
        book.Advance(200);
        book.Advance(210);

        Assert.Equal(new[]
        {
            Event(StatusEffectEventKind.Periodic, 110),
            Event(StatusEffectEventKind.Periodic, 120),
            Event(StatusEffectEventKind.Periodic, 130),
            Event(StatusEffectEventKind.Ended, 130, StatusEffectEndReason.Expired)
        }, Events(book));
    }

    [Fact]
    public void Periodic_RefreshKeepsAnchor()
    {
        var book = Create(new StatusEffectDefinition(new(1), 30, periodTicks: 10));
        book.Apply(7, new(1), 100);
        book.Advance(115);
        book.Apply(7, new(1), 115);
        Events(book);

        book.Advance(130);

        Assert.Equal(new[] { Event(StatusEffectEventKind.Periodic, 120),
            Event(StatusEffectEventKind.Periodic, 130) }, Events(book));

        book.Advance(145);

        Assert.Equal(new[] { Event(StatusEffectEventKind.Periodic, 140),
            Event(StatusEffectEventKind.Ended, 145, StatusEffectEndReason.Expired) }, Events(book));
    }

    [Fact]
    public void Periodic_PeriodLongerThanDurationNeverFires()
    {
        var book = Create(new StatusEffectDefinition(new(1), 5, periodTicks: 10));
        book.Apply(7, new(1), 100);
        Events(book);

        book.Advance(110);

        Assert.Equal(Event(StatusEffectEventKind.Ended, 105, StatusEffectEndReason.Expired),
            Assert.Single(Events(book)));
    }

    [Fact]
    public void Periodic_UsesCurrentStacksAndMagnitude()
    {
        var book = Create(new StatusEffectDefinition(new(1), 30, maxStacks: 3, periodTicks: 10));
        book.Apply(7, new(1), 100, magnitude: 4);
        book.Apply(7, new(1), 105, magnitude: 9);
        Events(book);

        book.Advance(110);

        Assert.Equal(new StatusEffectEvent(StatusEffectEventKind.Periodic, 7, new(1), 0, 2, 9, 110,
            StatusEffectEndReason.None), Assert.Single(Events(book)));
    }

    [Fact]
    public void Periodic_AtLargestTickDoesNotOverflow()
    {
        var book = Create(new StatusEffectDefinition(new(1), 30, periodTicks: 10));
        book.Apply(7, new(1), long.MaxValue - 30);
        Events(book);

        book.Advance(long.MaxValue);

        Assert.Equal(new[]
        {
            Event(StatusEffectEventKind.Periodic, long.MaxValue - 20),
            Event(StatusEffectEventKind.Periodic, long.MaxValue - 10),
            Event(StatusEffectEventKind.Periodic, long.MaxValue),
            Event(StatusEffectEventKind.Ended, long.MaxValue, StatusEffectEndReason.Expired)
        }, Events(book));
    }

    [Fact]
    public void Advance_OrdersActorsThenInstancesByApplication()
    {
        var book = Create(new StatusEffectDefinition(new(2), 10, periodTicks: 10), new(new(1), 10, periodTicks: 10));
        book.Apply(9, new(1), 100);
        book.Apply(3, new(2), 100);
        book.Apply(3, new(1), 100);
        Events(book);

        book.Advance(110);

        var events = Events(book);
        Assert.Equal(new long[] { 3, 3, 3, 3, 9, 9 }, events.ConvertAll(e => e.Actor));
        Assert.Equal(new[] { new StatusEffectId(2), new(2), new(1), new(1), new(1), new(1) },
            events.ConvertAll(e => e.Effect));
        Assert.Equal(new[] { StatusEffectEventKind.Periodic, StatusEffectEventKind.Ended,
            StatusEffectEventKind.Periodic, StatusEffectEventKind.Ended,
            StatusEffectEventKind.Periodic, StatusEffectEventKind.Ended }, events.ConvertAll(e => e.Kind));
    }

    [Fact]
    public void NowGoingBackwards_Throws()
    {
        var book = Create(new StatusEffectDefinition(new(1), 30));
        book.Advance(100);

        Assert.Throws<ArgumentOutOfRangeException>(() => book.Advance(99));
        Assert.Throws<ArgumentOutOfRangeException>(() => book.Apply(7, new(1), 99));
        Assert.Equal(StatusEffectApplyResult.Applied, book.Apply(7, new(1), 100));
        book.Advance(100);
    }

    [Fact]
    public void SameCallSequence_ProducesIdenticalEvents()
    {
        var first = Create(new StatusEffectDefinition(new(1), 30, maxStacks: 3, periodTicks: 10),
            new(new(2), 15, scope: StatusEffectScope.PerSource, periodTicks: 5));
        var second = Create(new StatusEffectDefinition(new(1), 30, maxStacks: 3, periodTicks: 10),
            new(new(2), 15, scope: StatusEffectScope.PerSource, periodTicks: 5));

        foreach (var book in new[] { first, second })
        {
            book.Apply(9, new(1), 100);
            book.Apply(3, new(2), 100, source: 11, magnitude: 5);
            book.Apply(3, new(2), 101, source: 12, magnitude: 7);
            book.Advance(112);
            book.Apply(9, new(1), 115);
            book.Remove(3, new(2), source: 11);
            book.Advance(150);
        }

        Assert.Equal(Events(first), Events(second));
    }

    [Fact]
    public void EventsDrainedAfterAdvance_AllowClearDuringHandling()
    {
        var book = Create(new StatusEffectDefinition(new(1), 30, periodTicks: 10), new(new(2), 20));
        book.Apply(7, new(1), 100);
        Events(book);
        book.Advance(110);

        foreach (var item in Events(book))
        {
            Assert.Equal(StatusEffectEventKind.Periodic, item.Kind);
            Assert.Equal(1, book.Clear(item.Actor));
            Assert.Equal(StatusEffectApplyResult.Applied, book.Apply(item.Actor, new(2), 110));
        }

        var events = Events(book);
        Assert.Equal(StatusEffectEndReason.Cleared, events[0].EndReason);
        Assert.Equal(StatusEffectEventKind.Applied, events[1].Kind);
        Assert.False(book.Has(7, new(1)));
        Assert.True(book.Has(7, new(2)));
    }

    private static StatusEffectBook Create(params StatusEffectDefinition[] definitions) =>
        new(new StatusEffectCatalog(definitions));

    private static StatusEffectEvent Event(StatusEffectEventKind kind, long tick,
        StatusEffectEndReason reason = StatusEffectEndReason.None) => new(kind, 7, new(1), 0, 1, 0, tick, reason);

    private static List<StatusEffectEvent> Events(StatusEffectBook book)
    {
        var events = new List<StatusEffectEvent>();
        book.DrainEvents(events);
        return events;
    }
}
