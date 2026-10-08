using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using Xunit;

namespace KhaozEngine.Tests.Sharding;

public class CellImportAdmissionTests
{
    struct Position : IComponent { public float X; }
    static ReplicationRegistry Registry()
    {
        var registry = new ReplicationRegistry();
        registry.Register<Position>(1, (v, w) => w.Write(v.X), r => new() { X = r.ReadSingle() });
        return registry;
    }
    static bool Locate(World world, Entity entity, out float x, out float y)
    {
        x = world.Get<Position>(entity).X;
        y = 5;
        return true;
    }

    [Fact]
    public void HandoffRetainsTheFrozenSourceUntilDestinationAdmissionCanRetry()
    {
        using var host = new ShardHost(10, 0.1f, Registry(), 10, 0, Locate);
        Entity entity = host.SpawnOwned(5, 5, 1, out CellSim source);
        source.World.Set(entity, new Position { X = 11 });
        var destination = host.CellFor(11, 5);
        var gate = new Gate { Ready = false };
        destination.ImportAdmission = gate;
        host.ProcessHandoffs();
        Assert.True(source.World.IsAlive(entity));
        Assert.True(source.World.Has<Migrating>(entity));
        Assert.False(destination.TryGetOwned(1, out _));
        Assert.Equal(1, gate.Attempts);
        Assert.Equal(1, host.PendingMigrationAdmissions);
        Assert.False(host.CanRemoveCell(source.Coord));
        Assert.False(host.CanRemoveCell(destination.Coord));
        gate.Ready = true;
        host.ProcessHandoffs();
        Assert.Equal(2, gate.Attempts);
        Assert.True(destination.TryGetOwned(1, out var adopted));
        Assert.Equal(11, destination.World.Get<Position>(adopted).X);
        Assert.False(source.World.IsAlive(entity));
        Assert.Equal(1, host.OwnerCount(1));
        Assert.Equal(0, host.PendingMigrationAdmissions);
    }

    [Fact]
    public void HandoffPublishesTheTransientMarkBeforeReleasingAdmission()
    {
        using var host = new ShardHost(10, 0.1f, Registry(), 10, 0, Locate);
        Entity entity = host.SpawnOwned(5, 5, 1, out CellSim source);
        source.World.Set(entity, new Position { X = 11 });
        source.World.Set(entity, new Transient { Scope = TransientScope.DurableOnly });
        var destination = host.CellFor(11, 5);
        int released = 0;
        destination.ImportAdmission = new Gate
        {
            OnDispose = () =>
            {
                Assert.True(destination.TryGetOwned(1, out var adopted));
                Assert.True(destination.World.Has<Transient>(adopted));
                Assert.Equal(TransientScope.DurableOnly, destination.World.Get<Transient>(adopted).Scope);
                Assert.Equal(1, host.OwnerCount(1));
                released++;
            }
        };
        host.ProcessHandoffs();
        Assert.Equal(1, released);
    }


    [Fact]
    public void StagedPublicationReadsTheWireOnceAndCopiesAdmittedValues()
    {
        int reads = 0;
        var registry = new ReplicationRegistry();
        registry.Register<Position>(1, (value, writer) => writer.Write(value.X), reader =>
        {
            reads++;
            return new() { X = reader.ReadSingle() };
        });
        var source = new World();
        Entity entity = source.Spawn();
        source.Set(entity, new NetId(1));
        source.Set(entity, new Position { X = 4 });
        byte[] bytes = SnapshotWriter.WriteFiltered(source, registry, new HashSet<long> { 1 });
        Assert.True(SnapshotStaging.TryDecode(registry, bytes, out var staging, out _));
        Assert.Equal(1, reads);
        staging!.World.Set(staging.Entities[1], new Position { X = 7 });
        var destination = new World();
        Entity copied = staging.CopyTo(1, destination);
        Assert.Equal(1, reads);
        Assert.Equal(7, destination.Get<Position>(copied).X);
        Assert.Equal(4, source.Get<Position>(entity).X);
    }


    struct MigratedValue : IComponent { public float Value; }

    [Fact]
    public void LocalRelocationStagesTypedValuesAndPreservesSourceUntilAdmissionSucceeds()
    {
        var registry = Registry();
        registry.Register<MigratedValue>(16, (_, _) => throw new InvalidOperationException("Unexpected wire write."),
            _ => throw new InvalidOperationException("Unexpected wire read."), channels: ReplicationChannels.Migrate);
        using var host = new ShardHost(10, 0.1f, registry, 10, 0, Locate);
        Entity entity = host.SpawnOwned(5, 5, 1, out var source);
        source.World.Set(entity, new Position { X = 5 });
        source.World.Set(entity, new MigratedValue { Value = 0.1234567f });
        source.World.Set(entity, new Transient { Scope = TransientScope.DurableOnly });
        var destination = host.CellFor(11, 5);
        var gate = new Gate { Ready = false };
        destination.ImportAdmission = gate;
        Assert.False(host.TryRelocateOwned(1, destination.Coord,
            (world, candidate) => world.Set(candidate, new Position { X = 11 }), out var refusal));
        Assert.True(refusal.NeedsAdmission);
        Assert.True(source.TryGetOwned(1, out var original));
        Assert.Equal(entity, original);
        Assert.Equal(5, source.World.Get<Position>(entity).X);
        Assert.False(source.World.Has<Migrating>(entity));
        gate.Ready = true;
        gate.OnDispose = () =>
        {
            Assert.False(source.World.IsAlive(entity));
            Assert.True(destination.TryGetOwned(1, out var adopted));
            Assert.Equal(0.1234567f, destination.World.Get<MigratedValue>(adopted).Value);
            Assert.Equal(TransientScope.DurableOnly, destination.World.Get<Transient>(adopted).Scope);
            Assert.Equal(1, host.OwnerCount(1));
        };
        Assert.True(host.TryRelocateOwned(1, destination.Coord,
            (world, candidate) => world.Set(candidate, new Position { X = 11 }), out var accepted));
        Assert.True(accepted.Ok);
    }

    sealed class Gate : ICellImportAdmission
    {
        public bool Ready = true;
        public int Attempts;
        public Action? OnDispose;
        public CellAdmissionRead Acquire(World staged, IReadOnlyDictionary<long, Entity> entities)
        {
            Attempts++;
            return new(Ready ? CellAdmissionOutcome.Accepted : CellAdmissionOutcome.Unresolved,
                Ready ? new Release(OnDispose) : null);
        }
    }
    sealed class Release(Action? action) : IDisposable { public void Dispose() => action?.Invoke(); }
}
