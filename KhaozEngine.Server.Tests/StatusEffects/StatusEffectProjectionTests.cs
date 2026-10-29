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
    public void Projection_ForgetsActorWhenPublishFails()
    {
        var book = NewBook();
        var target = new RecordingTarget { Accept = false };
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

        Assert.True(target.TryPublish(42, in state));
        Assert.Equal<StatusEffectEntry>(state.Entries, cell.World.Get<ActiveStatusEffects>(entity).Entries);
        Assert.False(target.TryPublish(43, in state));
        cell.World.Despawn(entity);
        Assert.False(target.TryPublish(42, in state));
    }

    private static StatusEffectBook NewBook() => new(new StatusEffectCatalog(new[]
    {
        new StatusEffectDefinition(new(7), 60)
    }));

    private sealed class RecordingTarget : IStatusEffectProjectionTarget
    {
        public bool Accept { get; init; } = true;
        public List<(long Actor, ActiveStatusEffects State)> Calls { get; } = new();

        public bool TryPublish(long actor, in ActiveStatusEffects state)
        {
            Calls.Add((actor, state));
            return Accept;
        }
    }
}
