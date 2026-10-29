using System;
using System.Collections.Generic;
using System.Reflection;
using KhaozEngine.StatusEffects;
using Xunit;

namespace KhaozEngine.Tests.StatusEffects;

public sealed class StatusEffectBookLifecycleTests
{
    [Fact]
    public void Remove_WithNullSource_RemovesEverySourcesInstance()
    {
        var book = Create(new StatusEffectDefinition(new(1), 30, scope: StatusEffectScope.PerSource), new(new(2), 30));
        book.Apply(7, new(1), 100, source: 12, magnitude: 50);
        book.Apply(7, new(2), 100);
        book.Apply(7, new(1), 101, source: 11, magnitude: 25);
        book.Advance(110);
        Events(book);

        Assert.True(book.Remove(7, new(1)));

        Assert.False(book.Has(7, new(1)));
        Assert.True(book.Has(7, new(2)));
        Assert.Equal(new[]
        {
            new StatusEffectEvent(StatusEffectEventKind.Ended, 7, new(1), 12, 1, 50, 110,
                StatusEffectEndReason.Removed),
            new StatusEffectEvent(StatusEffectEventKind.Ended, 7, new(1), 11, 1, 25, 110,
                StatusEffectEndReason.Removed)
        }, Events(book));
        Assert.False(book.Remove(7, new(1)));
        Assert.False(book.Remove(99, new(1)));
    }

    [Fact]
    public void Remove_WithSource_RemovesOnlyMatchingInstance()
    {
        var book = Create(new StatusEffectDefinition(new(1), 30, scope: StatusEffectScope.PerSource));
        book.Apply(7, new(1), 100, source: 11);
        book.Apply(7, new(1), 100, source: 12);

        Assert.False(book.Remove(7, new(1), source: 99));
        Assert.True(book.Remove(7, new(1), source: 11));

        var entries = new List<StatusEffectEntry>();
        book.CopyEntries(7, entries, shownOnly: false);
        Assert.Equal(12, Assert.Single(entries).Source);
    }

    [Fact]
    public void Clear_EndsAllWithClearedReason()
    {
        var book = Create(new StatusEffectDefinition(new(2), 30), new(new(1), 30));
        book.Apply(7, new(2), 100, magnitude: 8);
        book.Apply(7, new(1), 100, magnitude: 9);
        book.Advance(115);
        Events(book);

        Assert.Equal(2, book.Clear(7));

        Assert.Equal(new[]
        {
            new StatusEffectEvent(StatusEffectEventKind.Ended, 7, new(2), 0, 1, 8, 115,
                StatusEffectEndReason.Cleared),
            new StatusEffectEvent(StatusEffectEventKind.Ended, 7, new(1), 0, 1, 9, 115,
                StatusEffectEndReason.Cleared)
        }, Events(book));
        Assert.Equal(0, book.Clear(7));
        Assert.Equal(0, book.Clear(99));
        Assert.Equal(0UL, book.ModifierVersion(7));
    }

    [Fact]
    public void DrainChangedActors_AscendingAndDeduplicated()
    {
        var book = Create(new StatusEffectDefinition(new(1), 30));
        book.Apply(9, new(1), 100);
        book.Apply(3, new(1), 100);
        book.Apply(9, new(1), 101);
        var changed = new List<long> { 99 };

        book.DrainChangedActors(changed);

        Assert.Equal(new long[] { 99, 3, 9 }, changed);
        changed.Clear();
        book.DrainChangedActors(changed);
        Assert.Empty(changed);
    }

    [Fact]
    public void EmptyActor_IsReportedOnceThenDropped()
    {
        var book = Create(new StatusEffectDefinition(new(1), 30));
        book.Apply(7, new(1), 100);
        book.DrainChangedActors(new List<long>());
        book.Advance(130);

        Assert.Equal(1, book.ActorCount);
        Assert.Equal(0UL, book.ModifierVersion(7));
        var changed = new List<long>();
        book.DrainChangedActors(changed);

        Assert.Equal(new long[] { 7 }, changed);
        Assert.Equal(0, book.ActorCount);
        changed.Clear();
        book.DrainChangedActors(changed);
        Assert.Empty(changed);
    }

    [Fact]
    public void ModifierTotal_ScalesByStacks()
    {
        var book = Create(new StatusEffectDefinition(new(1), 30, maxStacks: 3,
            modifiers: new[] { new StatusEffectModifier(2, 500) }));
        book.Apply(7, new(1), 100);
        ulong first = book.ModifierVersion(7);
        book.Apply(7, new(1), 101);
        ulong second = book.ModifierVersion(7);
        book.Apply(7, new(1), 102);

        Assert.Equal(1500, book.ModifierTotal(7, 2));
        Assert.Equal(0, book.ModifierTotal(7, 3));
        Assert.Equal(0, book.ModifierTotal(99, 2));
        Assert.True(first > 0);
        Assert.True(second > first);
        Assert.True(book.ModifierVersion(7) > second);
        book.Advance(132);
        Assert.Equal(0, book.ModifierTotal(7, 2));
        Assert.Equal(0UL, book.ModifierVersion(7));
    }

    [Fact]
    public void ModifierTotal_IncludesHiddenEffectsAndRepeatedChannelLines()
    {
        var book = Create(new StatusEffectDefinition(new(1), 30, modifiers: new[] { new StatusEffectModifier(2, 500), new(2, -100) }),
            new(new(2), 30, shown: false, modifiers: new[] { new StatusEffectModifier(2, -50), new(3, 7) }));
        book.Apply(7, new(1), 100);
        book.Apply(7, new(2), 100);

        Assert.Equal(350, book.ModifierTotal(7, 2));
        Assert.Equal(7, book.ModifierTotal(7, 3));
        book.Remove(7, new(2));
        Assert.Equal(400, book.ModifierTotal(7, 2));
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void ModifierTotal_RejectsOutOfRangeTotals(int value)
    {
        var book = Create(new StatusEffectDefinition(new(1), 30, maxStacks: 2,
            modifiers: new[] { new StatusEffectModifier(2, value) }));
        book.Apply(7, new(1), 100);
        book.Apply(7, new(1), 101);

        Assert.Throws<OverflowException>(() => book.ModifierTotal(7, 2));
    }

    [Fact]
    public void ModifierTotal_AllowsLargeContributionsThatCancel()
    {
        var book = Create(new StatusEffectDefinition(new(1), 30, maxStacks: 2,
                modifiers: new[] { new StatusEffectModifier(2, int.MaxValue) }),
            new(new(2), 30, maxStacks: 2, modifiers: new[] { new StatusEffectModifier(2, -int.MaxValue) }));
        book.Apply(7, new(1), 100);
        book.Apply(7, new(1), 101);
        book.Apply(7, new(2), 100);
        book.Apply(7, new(2), 101);

        Assert.Equal(0, book.ModifierTotal(7, 2));
    }

    [Fact]
    public void ModifierVersion_ContinuesBeyondUIntRange()
    {
        var book = Create(new StatusEffectDefinition(new(1), 30));
        // Seed the boundary directly instead of generating billions of changes.
        var stamp = typeof(StatusEffectBook).GetField("_modifierStamp", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(stamp);
        stamp.SetValue(book, Convert.ChangeType(4294967294UL, stamp.FieldType));

        book.Apply(7, new(1), 100);
        Assert.Equal(4294967295UL, book.ModifierVersion(7));
        book.Apply(9, new(1), 100);
        Assert.Equal(4294967296UL, book.ModifierVersion(9));
        book.Apply(7, new(1), 101);
        Assert.Equal(4294967297UL, book.ModifierVersion(7));
    }

    [Fact]
    public void ModifierVersion_NeverRepeatsAfterDrop()
    {
        var book = Create(new StatusEffectDefinition(new(1), 30));
        book.Apply(7, new(1), 100);
        ulong first = book.ModifierVersion(7);
        book.Advance(130);
        Assert.Equal(0UL, book.ModifierVersion(7));
        book.DrainChangedActors(new List<long>());

        book.Apply(9, new(1), 130);
        ulong otherActor = book.ModifierVersion(9);
        book.Apply(7, new(1), 130);

        Assert.True(otherActor > first);
        Assert.True(book.ModifierVersion(7) > otherActor);
    }

    [Fact]
    public void Expiry_ChangesVersionWhileOtherEffectsRemain()
    {
        var book = Create(new StatusEffectDefinition(new(1), 10), new(new(2), 30));
        book.Apply(7, new(1), 100);
        book.Apply(7, new(2), 100);
        ulong previous = book.ModifierVersion(7);
        book.DrainChangedActors(new List<long>());

        book.Advance(110);

        Assert.True(book.ModifierVersion(7) > previous);
        Assert.False(book.Has(7, new(1)));
        Assert.True(book.Has(7, new(2)));
        var changed = new List<long>();
        book.DrainChangedActors(changed);
        Assert.Equal(new long[] { 7 }, changed);
    }

    [Fact]
    public void ClearAndReapplyBeforeDrain_PreservesNewInstance()
    {
        var book = Create(new StatusEffectDefinition(new(1), 30));
        book.Apply(7, new(1), 100);
        book.Clear(7);
        book.Apply(7, new(1), 101);

        var changed = new List<long>();
        book.DrainChangedActors(changed);

        Assert.Equal(new long[] { 7 }, changed);
        Assert.Equal(1, book.ActorCount);
        Assert.True(book.Has(7, new(1)));
        Assert.NotEqual(0UL, book.ModifierVersion(7));
    }

    [Fact]
    public void ModifierVersion_ChangesOnRefreshAndRemovalButNotPeriodic()
    {
        var book = Create(new StatusEffectDefinition(new(1), 30, periodTicks: 10), new(new(2), 30));
        book.Apply(7, new(1), 100);
        book.Apply(7, new(2), 100);
        ulong initial = book.ModifierVersion(7);
        book.Advance(110);
        Assert.Equal(initial, book.ModifierVersion(7));
        book.DrainChangedActors(new List<long>());
        book.Advance(120);
        var changed = new List<long>();
        book.DrainChangedActors(changed);
        Assert.Empty(changed);

        book.Apply(7, new(1), 120);
        ulong refreshed = book.ModifierVersion(7);
        Assert.True(refreshed > initial);
        book.Remove(7, new(1));
        Assert.True(book.ModifierVersion(7) > refreshed);
        book.DrainChangedActors(changed);
        Assert.Equal(new long[] { 7 }, changed);
    }

    [Fact]
    public void Forget_DropsActorAndPendingChangeWithoutRaisingEvents()
    {
        var book = Create(new StatusEffectDefinition(new(1), 30));
        book.Apply(7, new(1), 100);
        ulong first = book.ModifierVersion(7);
        Events(book);

        book.Forget(7);
        book.Forget(7);

        Assert.Equal(0, book.ActorCount);
        Assert.False(book.Has(7, new(1)));
        Assert.Equal(0UL, book.ModifierVersion(7));
        Assert.Empty(Events(book));
        var changed = new List<long>();
        book.DrainChangedActors(changed);
        Assert.Empty(changed);
        book.Apply(7, new(1), 101);
        Assert.True(book.ModifierVersion(7) > first);
    }

    [Fact]
    public void DrainEvents_AppendsAndConsumesPendingEvents()
    {
        var book = Create(new StatusEffectDefinition(new(1), 30));
        book.Apply(7, new(1), 100);
        var sentinel = new StatusEffectEvent(StatusEffectEventKind.Applied, 99, new(9), 0, 1, 0, 1,
            StatusEffectEndReason.None);
        var events = new List<StatusEffectEvent> { sentinel };

        book.DrainEvents(events);
        book.DrainEvents(events);

        Assert.Equal(2, events.Count);
        Assert.Equal(sentinel, events[0]);
        Assert.Equal(7, events[1].Actor);
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
