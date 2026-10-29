using System.Collections.Generic;
using System.Collections.Immutable;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using KhaozEngine.StatusEffects;
using KhaozEngine.StatusEffects.Netcode;
using Xunit;

namespace KhaozEngine.Tests.StatusEffects;

public class StatusEffectProjectionTests
{
    [Fact]
    public void Projection_PublishesShownEntriesOnly()
    {
        var catalog = new StatusEffectCatalog(new[]
        {
            new StatusEffectDefinition(new(7), 60),
            new StatusEffectDefinition(new(8), 60, shown: false)
        });
        var book = new StatusEffectBook(catalog);
        var target = new RecordingTarget();
        var projection = new StatusEffectProjection(book, target);
        book.Apply(42, new(7), now: 20, source: 99);
        book.Apply(42, new(8), now: 20, source: 100);

        projection.Publish();

        var call = Assert.Single(target.Calls);
        Assert.Equal(42L, call.Actor);
        Assert.Equal(new StatusEffectEntry(new(7), 99, 1, 20, 80), Assert.Single(call.State.Entries));
        Assert.True(book.Has(42, new(8)));
    }

    [Fact]
    public void Projection_PublishesEmptyWhenLastEffectEnds()
    {
        var book = NewBook();
        var target = new RecordingTarget();
        var projection = new StatusEffectProjection(book, target);
        book.Apply(42, new(7), now: 20);
        projection.Publish();

        book.Advance(80);
        projection.Publish();
        projection.Publish();

        Assert.Equal(2, target.Calls.Count);
        Assert.Equal(42L, target.Calls[1].Actor);
        Assert.Empty(target.Calls[1].State.Entries);
        Assert.Equal(0, book.ActorCount);
    }

    [Fact]
    public void Projection_ForgetsActorWhenTargetReportsGone()
    {
        var book = NewBook();
        var target = new RecordingTarget { Result = StatusEffectPublishResult.Gone };
        var projection = new StatusEffectProjection(book, target);
        book.Apply(42, new(7), now: 20);

        projection.Publish();

        Assert.Equal(0, book.ActorCount);
        Assert.False(book.Has(42, new(7)));
        book.Advance(100);
        projection.Publish();
        Assert.Single(target.Calls);
    }

    [Fact]
    public void Projection_MergesDeferredAndChangedActorsInOrderWithCurrentEntries()
    {
        var book = NewBook();
        var target = new RecordingTarget { Result = StatusEffectPublishResult.Deferred };
        var projection = new StatusEffectProjection(book, target);
        book.Apply(30, new(7), now: 20);
        book.Apply(10, new(7), now: 20);
        projection.Publish();
        Assert.Equal(2, book.ActorCount);
        Assert.True(book.Has(10, new(7)));
        Assert.True(book.Has(30, new(7)));

        target.Calls.Clear();
        target.Result = StatusEffectPublishResult.Published;
        book.Apply(20, new(7), now: 25);
        book.Apply(30, new(7), now: 25);
        projection.Publish();

        Assert.Collection(target.Calls,
            call => Assert.Equal(10L, call.Actor),
            call => Assert.Equal(20L, call.Actor),
            call => Assert.Equal(30L, call.Actor));
        Assert.Equal(new StatusEffectEntry(new(7), 0, 1, 25, 85), Assert.Single(target.Calls[2].State.Entries));
        target.Calls.Clear();
        projection.Publish();
        Assert.Empty(target.Calls);
    }

    [Fact]
    public void Projection_ForgetsPendingActorWhenTargetReportsGone()
    {
        var book = NewBook();
        var target = new RecordingTarget { Result = StatusEffectPublishResult.Deferred };
        var projection = new StatusEffectProjection(book, target);
        book.Apply(42, new(7), now: 20);
        projection.Publish();
        Assert.Equal(1, book.ActorCount);

        target.Result = StatusEffectPublishResult.Gone;
        projection.Publish();

        Assert.Equal(0, book.ActorCount);
        Assert.False(book.Has(42, new(7)));
        Assert.Equal(2, target.Calls.Count);
        projection.Publish();
        Assert.Equal(2, target.Calls.Count);
    }

    [Fact]
    public void Projection_RetriesEmptyStateAfterTheLastDeferredEffectExpires()
    {
        var book = NewBook();
        var target = new RecordingTarget { Result = StatusEffectPublishResult.Deferred };
        var projection = new StatusEffectProjection(book, target);
        book.Apply(42, new(7), now: 20);
        projection.Publish();
        book.Advance(80);
        projection.Publish();
        Assert.Empty(target.Calls[1].State.Entries);

        target.Result = StatusEffectPublishResult.Published;
        projection.Publish();

        Assert.Equal(3, target.Calls.Count);
        Assert.Equal(42L, target.Calls[2].Actor);
        Assert.Empty(target.Calls[2].State.Entries);
        projection.Publish();
        Assert.Equal(3, target.Calls.Count);
    }

    [Fact]
    public void Projection_PublishesOnlyChangedActorsWithIndependentSnapshots()
    {
        var book = NewBook();
        var target = new RecordingTarget();
        var projection = new StatusEffectProjection(book, target);
        book.Apply(42, new(7), now: 20);
        book.Apply(43, new(7), now: 21);
        projection.Publish();
        book.Advance(22);
        projection.Publish();
        Assert.Equal(2, target.Calls.Count);

        book.Apply(42, new(7), now: 25);
        projection.Publish();

        Assert.Equal(3, target.Calls.Count);
        Assert.Equal(42L, target.Calls[0].Actor);
        Assert.Equal(43L, target.Calls[1].Actor);
        Assert.Equal(new StatusEffectEntry(new(7), 0, 1, 20, 80), Assert.Single(target.Calls[0].State.Entries));
        Assert.Equal(new StatusEffectEntry(new(7), 0, 1, 21, 81), Assert.Single(target.Calls[1].State.Entries));
        Assert.Equal(new StatusEffectEntry(new(7), 0, 1, 25, 85), Assert.Single(target.Calls[2].State.Entries));
    }

    [Fact]
    public void Projection_PublishesTheMaximumActorCapacity()
    {
        var definitions = new List<StatusEffectDefinition>();
        for (int i = 1; i <= 255; i++)
            definitions.Add(new(new(i), 60));
        var book = new StatusEffectBook(new StatusEffectCatalog(definitions), maxEffectsPerActor: 255);
        for (int i = 1; i <= 255; i++)
            Assert.Equal(StatusEffectApplyResult.Applied, book.Apply(42, new(i), now: 20));
        var target = new RecordingTarget();

        new StatusEffectProjection(book, target).Publish();

        Assert.Equal(255, Assert.Single(target.Calls).State.Entries.Length);
    }

    [Fact]
    public void ShardHostTarget_PublishesToTheOwnedEntityAndRefusesMissingActors()
    {
        using var host = new ShardHost(100, 1f / 30f, new ReplicationRegistry());
        Entity entity = host.SpawnOwned(0, 0, 42, out CellSim cell);
        var target = new ShardHostStatusEffectTarget(host);
        var state = new ActiveStatusEffects
        {
            Entries = ImmutableArray.Create(new StatusEffectEntry(new(7), 99, 1, 20, 80))
        };

        Assert.Equal(StatusEffectPublishResult.Published, target.Publish(42, in state));
        Assert.Equal<StatusEffectEntry>(state.Entries, cell.World.Get<ActiveStatusEffects>(entity).Entries);
        Assert.Equal(StatusEffectPublishResult.Gone, target.Publish(43, in state));
        cell.World.Despawn(entity);
        Assert.Equal(StatusEffectPublishResult.Gone, target.Publish(42, in state));
    }

    private static StatusEffectBook NewBook() => new(new StatusEffectCatalog(new[]
    {
        new StatusEffectDefinition(new(7), 60)
    }));

    private sealed class RecordingTarget : IStatusEffectProjectionTarget
    {
        public StatusEffectPublishResult Result { get; set; } = StatusEffectPublishResult.Published;
        public List<(long Actor, ActiveStatusEffects State)> Calls { get; } = new();

        public StatusEffectPublishResult Publish(long actor, in ActiveStatusEffects state)
        {
            Calls.Add((actor, state));
            return Result;
        }
    }
}
