using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using KhaozEngine.StatusEffects;
using KhaozEngine.StatusEffects.Netcode;
using Xunit;

namespace KhaozEngine.Tests.StatusEffects;

public class StatusEffectsHandoffTests
{
    [Fact]
    public void DeferredAdmissionKeepsLiveEffectsAndPublishesLatestStateOnlyToTheAdoptedOwner()
    {
        var registry = new ReplicationRegistry();
        registry.Register<Position>(1, (value, writer) => writer.Write(value.X),
            reader => new Position { X = reader.ReadSingle() });
        StatusEffectsReplication.Register(registry, ReplicationRegistry.FirstExtensionTypeId);
        using var host = new ShardHost(10, 0.1f, registry, 10, 0, Locate);
        Entity original = host.SpawnOwned(5, 5, 42, out CellSim source);
        source.World.Set(original, new Position { X = 11 });
        CellSim destination = host.CellFor(11, 5);
        CellSim observer = host.CellFor(25, 5);
        var gate = new DeferredAdmission();
        destination.ImportAdmission = gate;
        var book = new StatusEffectBook(new StatusEffectCatalog(new[]
        {
            new StatusEffectDefinition(new(7), 5),
            new StatusEffectDefinition(new(8), 60)
        }));
        var projection = new StatusEffectProjection(book, new ShardHostStatusEffectTarget(host));
        book.Apply(42, new(7), now: 0);
        book.Apply(42, new(8), now: 0, source: 99);
        projection.Publish();
        observer.ApplyGhostSnapshot(source.Coord, SnapshotWriter.Write(source.World, registry));
        Assert.True(observer.TryGetGhost(42, out Entity ghost));
        Assert.True(observer.World.Has<Ghost>(ghost));

        host.ProcessHandoffs();
        Assert.True(source.World.IsAlive(original));
        Assert.True(source.World.Has<Migrating>(original));
        Assert.False(host.TryGetOwner(42, out _, out _));
        Assert.Equal(1, host.PendingMigrationAdmissions);

        book.Advance(5);
        projection.Publish();

        Assert.True(book.Has(42, new(8)));
        Assert.False(book.Has(42, new(7)));
        Assert.Equal(1, book.ActorCount);
        Assert.Equal(2, source.World.Get<ActiveStatusEffects>(original).Entries.Length);
        Assert.Equal(2, observer.World.Get<ActiveStatusEffects>(ghost).Entries.Length);

        // Admission remains unavailable over another pass with no new book changes.
        host.ProcessHandoffs();
        book.Advance(6);
        projection.Publish();
        Assert.True(book.Has(42, new(8)));
        Assert.Equal(1, host.PendingMigrationAdmissions);
        Assert.Equal(2, observer.World.Get<ActiveStatusEffects>(ghost).Entries.Length);

        gate.Ready = true;
        host.ProcessHandoffs();
        Assert.True(host.TryGetOwner(42, out CellSim owner, out Entity adopted));
        Assert.Same(destination, owner);
        Assert.False(source.World.IsAlive(original));
        Assert.False(owner.World.Has<Ghost>(adopted));
        Assert.False(owner.World.Has<Migrating>(adopted));
        Assert.Equal(2, owner.World.Get<ActiveStatusEffects>(adopted).Entries.Length);

        // No changed-actor notification remains. The pending retry must read the current book.
        projection.Publish();

        Assert.Equal(new StatusEffectEntry(new(8), 99, 1, 0, 60),
            Assert.Single(owner.World.Get<ActiveStatusEffects>(adopted).Entries));
        Assert.True(book.Has(42, new(8)));
        Assert.Equal(2, observer.World.Get<ActiveStatusEffects>(ghost).Entries.Length);
        Assert.True(observer.World.Has<Ghost>(ghost));
    }

    private struct Position : IComponent { public float X; }

    private static bool Locate(World world, Entity entity, out float x, out float y)
    {
        x = world.Get<Position>(entity).X;
        y = 5;
        return true;
    }

    private sealed class DeferredAdmission : ICellImportAdmission
    {
        public bool Ready { get; set; }

        public CellAdmissionRead Acquire(World staged, IReadOnlyDictionary<long, Entity> entities,
            CellImportPurpose purpose) => new(Ready ? CellAdmissionOutcome.Accepted : CellAdmissionOutcome.Unresolved);
    }
}
