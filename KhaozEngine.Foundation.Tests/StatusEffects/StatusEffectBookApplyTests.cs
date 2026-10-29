using System;
using System.Collections.Generic;
using KhaozEngine.StatusEffects;
using Xunit;

namespace KhaozEngine.Tests.StatusEffects;

public sealed class StatusEffectBookApplyTests
{
    [Fact]
    public void Apply_NewInstance_OpensWindowAndRaisesApplied()
    {
        var book = Create(new StatusEffectDefinition(new(1), 30));

        Assert.Equal(StatusEffectApplyResult.Applied, book.Apply(7, new(1), 100));

        var entries = new List<StatusEffectEntry>();
        Assert.Equal(1, book.CopyEntries(7, entries, shownOnly: false));
        Assert.Equal(new StatusEffectEntry(new(1), 0, 1, 100, 130), Assert.Single(entries));
        Assert.Equal(new StatusEffectEvent(StatusEffectEventKind.Applied, 7, new(1), 0, 1, 0, 100,
            StatusEffectEndReason.None), Assert.Single(Events(book)));
        Assert.True(book.Has(7, new(1)));
        Assert.Equal(1, book.ActorCount);
    }

    [Fact]
    public void Reapply_RefreshNeverShortens()
    {
        var book = Create(new StatusEffectDefinition(new(1), 30));
        book.Apply(7, new(1), 100);

        Assert.Equal(StatusEffectApplyResult.Refreshed, book.Apply(7, new(1), 110, durationTicks: 5));

        var entries = new List<StatusEffectEntry>();
        book.CopyEntries(7, entries, shownOnly: false);
        Assert.Equal(new StatusEffectEntry(new(1), 0, 1, 110, 130), Assert.Single(entries));
        Assert.Equal(StatusEffectEventKind.Refreshed, Events(book)[1].Kind);
    }

    [Fact]
    public void Reapply_StacksUpToCap()
    {
        var book = Create(new StatusEffectDefinition(new(1), 30, maxStacks: 3));

        Assert.Equal(StatusEffectApplyResult.Applied, book.Apply(7, new(1), 100));
        Assert.Equal(StatusEffectApplyResult.Stacked, book.Apply(7, new(1), 101));
        Assert.Equal(StatusEffectApplyResult.Stacked, book.Apply(7, new(1), 102));
        Assert.Equal(StatusEffectApplyResult.Refreshed, book.Apply(7, new(1), 103));

        var entries = new List<StatusEffectEntry>();
        book.CopyEntries(7, entries, shownOnly: false);
        Assert.Equal(new StatusEffectEntry(new(1), 0, 3, 103, 133), Assert.Single(entries));
        Assert.Equal(new[] { StatusEffectEventKind.Applied, StatusEffectEventKind.Stacked,
            StatusEffectEventKind.Stacked, StatusEffectEventKind.Refreshed }, Events(book).ConvertAll(e => e.Kind));
    }

    [Fact]
    public void PerSource_KeepsOneInstancePerSource()
    {
        var book = Create(new StatusEffectDefinition(new(1), 30, scope: StatusEffectScope.PerSource));
        book.Apply(7, new(1), 100, source: 11);
        book.Apply(7, new(1), 101, source: 12);

        Assert.Equal(StatusEffectApplyResult.Refreshed, book.Apply(7, new(1), 110, source: 11));

        var entries = new List<StatusEffectEntry>();
        Assert.Equal(2, book.CopyEntries(7, entries, shownOnly: false));
        Assert.Equal(new[] { new StatusEffectEntry(new(1), 11, 1, 110, 140),
            new StatusEffectEntry(new(1), 12, 1, 101, 131) }, entries);
    }

    [Fact]
    public void PerActor_ReplacesSourceAndMagnitude()
    {
        var book = Create(new StatusEffectDefinition(new(1), 30, periodTicks: 10));
        book.Apply(7, new(1), 100, source: 11, magnitude: 25);
        book.Apply(7, new(1), 105, source: 12, magnitude: 60);
        book.Advance(110);

        var events = Events(book);
        Assert.Equal(new StatusEffectEvent(StatusEffectEventKind.Refreshed, 7, new(1), 12, 1, 60, 105,
            StatusEffectEndReason.None), events[1]);
        Assert.Equal(new StatusEffectEvent(StatusEffectEventKind.Periodic, 7, new(1), 12, 1, 60, 110,
            StatusEffectEndReason.None), events[2]);
        var entries = new List<StatusEffectEntry>();
        book.CopyEntries(7, entries, shownOnly: false);
        Assert.Equal(12, Assert.Single(entries).Source);
    }

    [Fact]
    public void ActorFull_RefusesNewInstanceButRefreshesExisting()
    {
        var book = new StatusEffectBook(new StatusEffectCatalog(new[]
        {
            new StatusEffectDefinition(new(1), 30, scope: StatusEffectScope.PerSource),
            new StatusEffectDefinition(new(2), 30)
        }), maxEffectsPerActor: 2);
        book.Apply(7, new(1), 100, source: 11);
        book.Apply(7, new(1), 100, source: 12);
        ulong version = book.ModifierVersion(7);

        Assert.Equal(StatusEffectApplyResult.ActorFull, book.Apply(7, new(1), 100, source: 13));
        Assert.Equal(StatusEffectApplyResult.ActorFull, book.Apply(7, new(2), 100));
        Assert.Equal(version, book.ModifierVersion(7));
        Assert.Equal(StatusEffectApplyResult.Refreshed, book.Apply(7, new(1), 101, source: 11));
        Assert.Equal(3, Events(book).Count);
    }

    [Fact]
    public void ActorCapacity_AcceptsTheByteLimit()
    {
        var definitions = new StatusEffectDefinition[256];
        for (int i = 0; i < definitions.Length; i++)
            definitions[i] = new(new(i + 1), 30);
        var book = new StatusEffectBook(new StatusEffectCatalog(definitions), maxEffectsPerActor: 255);

        for (int id = 1; id <= 255; id++)
            Assert.Equal(StatusEffectApplyResult.Applied, book.Apply(7, new(id), 100));

        Assert.Equal(StatusEffectApplyResult.ActorFull, book.Apply(7, new(256), 100));
        var entries = new List<StatusEffectEntry>();
        Assert.Equal(255, book.CopyEntries(7, entries, shownOnly: false));
    }

    [Fact]
    public void Apply_UnknownDefinitionDoesNotCreateActorOrChanges()
    {
        var book = Create(new StatusEffectDefinition(new(1), 30));

        Assert.Equal(StatusEffectApplyResult.UnknownDefinition, book.Apply(7, new(2), 100));
        Assert.Equal(0, book.ActorCount);
        Assert.Equal(0UL, book.ModifierVersion(7));
        Assert.Empty(Events(book));
        var changed = new List<long>();
        book.DrainChangedActors(changed);
        Assert.Empty(changed);
    }

    [Fact]
    public void Apply_RejectsInvalidActorAndDuration()
    {
        var book = Create(new StatusEffectDefinition(new(1), 30));

        Assert.Throws<ArgumentOutOfRangeException>(() => book.Apply(0, new(1), 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => book.Apply(7, new(1), 100, durationTicks: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => book.Apply(7, new(1), 100, durationTicks: -1));
        Assert.Equal(0, book.ActorCount);
        Assert.Empty(Events(book));
    }

    [Fact]
    public void Apply_RejectsUnrepresentableEndTickWithoutChangingState()
    {
        var book = Create(new StatusEffectDefinition(new(1), 30));

        Assert.Throws<ArgumentOutOfRangeException>(() => book.Apply(7, new(1), long.MaxValue - 10));

        Assert.Equal(0, book.ActorCount);
        Assert.Empty(Events(book));
    }

    [Fact]
    public void CopyEntries_AppendsInApplicationOrderAndFiltersHiddenEffects()
    {
        var book = Create(new StatusEffectDefinition(new(2), 30, shown: false), new(new(1), 30));
        book.Apply(7, new(2), 100);
        book.Apply(7, new(1), 101);
        book.Apply(7, new(2), 102);
        var entries = new List<StatusEffectEntry> { new(new(99), 0, 1, 0, 1) };

        Assert.Equal(1, book.CopyEntries(7, entries, shownOnly: true));
        Assert.Equal(new StatusEffectEntry(new(1), 0, 1, 101, 131), entries[1]);
        Assert.Equal(2, book.CopyEntries(7, entries, shownOnly: false));
        Assert.Equal(new StatusEffectEntry(new(2), 0, 1, 102, 132), entries[2]);
        Assert.Equal(new StatusEffectEntry(new(1), 0, 1, 101, 131), entries[3]);
        Assert.Equal(0, book.CopyEntries(99, entries, shownOnly: false));
        Assert.Equal(4, entries.Count);
    }

    private static StatusEffectBook Create(params StatusEffectDefinition[] definitions) =>
        new(new StatusEffectCatalog(definitions));

    private static List<StatusEffectEvent> Events(StatusEffectBook book)
    {
        var events = new List<StatusEffectEvent>();
        book.DrainEvents(events);
        return events;
    }
}
