using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using KhaozEngine.Tests.NetWorld.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public partial class OrdinaryContactConfigurationTests
{
    [Theory]
    [InlineData("registration", "ensure")]
    [InlineData("registration", "restore")]
    [InlineData("registration", "relocate")]
    [InlineData("boundary", "ensure")]
    [InlineData("boundary", "restore")]
    [InlineData("boundary", "relocate")]
    [InlineData("view", "ensure")]
    [InlineData("view", "restore")]
    [InlineData("view", "relocate")]
    public void FailedCellConfigurationCannotBypassAdmissionOnRetry(string failure, string door)
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        using (st) using (ct)
        {
            CellCoord destination = new(0, 0);
            bool refusing = true;
            int configurations = 0, notifications = 0;
            var failedSources = new List<ObservedPhysicsWorld>();
            using var server = new ShardedWorldServer(st, new()
            {
                FrameAnchoring = false,
                PhysicsWorldFactory = coord =>
                {
                    var source = new ObservedPhysicsWorld { CertifiedSweep = coord != destination || !refusing || failure != "view" };
                    if (coord == destination && refusing) failedSources.Add(source);
                    return source;
                },
                MovementRegistrationFactory = cell =>
                {
                    if (cell.Coord == destination) configurations++;
                    return cell.Coord == destination && refusing && failure == "registration"
                        ? null! : OrdinaryContactLifecycleTests.Register(cell.Physics!);
                },
                MovementBoundaryFactory = cell => cell.Coord == destination
                    ? refusing && failure == "boundary" ? null! : MovementBoundary.Rectangle(-2, -2, 2, 2)
                    : MovementBoundary.Rectangle(-20, -20, 20, 20)
            }, (_, _) => 0, OrdinaryPlayerWorld.Tuning);
            server.CellCreated += coord => { if (coord == destination) notifications++; };
            var sourceCell = server.Host.EnsureCell(new(-1, 0));
            var entity = sourceCell.World.Spawn();
            sourceCell.World.Set(entity, new NetId(99));
            var initial = new PlayerMoveState { Move = new() { Position = new(-4, 0.75f, 0), Grounded = true } };
            sourceCell.World.Set(entity, ReplicatedPosition.FromWorld(initial.Position, default));
            MovementComponents.Set(sourceCell.World, entity, initial);
            sourceCell.RegisterOwned(99, entity);
            void Refuses(Action action)
            {
                if (failure == "view") Assert.Throws<NotSupportedException>(action);
                else Assert.Throws<InvalidOperationException>(action);
            }
            Refuses(() => server.EnsureCell(destination));
            var forbidden = initial;
            forbidden.Position = new(4, 0.75f, 0);
            void Configure(World world, Entity candidate)
            {
                world.Set(candidate, ReplicatedPosition.FromWorld(forbidden.Position, default));
                MovementComponents.Set(world, candidate, forbidden);
            }
            switch (door)
            {
                case "ensure": Refuses(() => server.EnsureCell(destination)); break;
                case "restore": Assert.Empty(server.TryRestoreCell(destination, Snapshot(forbidden)).NetIds); break;
                default: Refuses(() => server.Host.TryRelocateOwned(99, destination, Configure, out _)); break;
            }
            Assert.False(server.Host.TryGetCell(destination, out _));
            Assert.Equal(0, notifications);
            Assert.True(server.Host.TryGetOwner(99, out var owner, out var owned));
            Assert.Same(sourceCell, owner);
            Assert.Equal(entity, owned);
            Assert.Equal(initial.Position, owner.World.Get<ReplicatedPosition>(owned).Value);
            foreach (var failed in failedSources)
            {
                Assert.Equal(1, failed.SourceDisposals);
                Assert.Equal(failure == "view" ? new[] { "movement-view", "physics" } : new[] { "physics" }, failed.DisposalOrder);
            }
            int attempts = configurations;
            refusing = false;
            server.EnsureCell(destination);
            server.EnsureCell(destination);
            Assert.Equal(attempts + 1, configurations);
            Assert.Equal(1, notifications);
            Assert.True(server.Host.TryGetCell(destination, out var recovered));
            Assert.NotNull(recovered.ImportAdmission);
            Assert.False(server.TryRestoreCell(destination, Snapshot(forbidden)).Ok);
            Assert.False(server.Host.TryRelocateOwned(99, destination, Configure, out _));
            Assert.True(server.Host.TryGetOwner(99, out owner, out owned));
            Assert.Same(sourceCell, owner);
            Assert.Equal(initial.Position, owner.World.Get<ReplicatedPosition>(owned).Value);
            forbidden.Position = new(1, 0.75f, 0);
            Assert.Contains(100L, server.TryRestoreCell(destination, Snapshot(forbidden)).NetIds);
            Assert.True(server.Host.TryRelocateOwned(99, destination, Configure, out _));
            Assert.True(server.Host.TryGetOwner(99, out owner, out _));
            Assert.Same(recovered, owner);
            Assert.Equal(attempts + 1, configurations);
            Assert.Equal(1, notifications);

            byte[] Snapshot(PlayerMoveState state)
            {
                var world = new World();
                var candidate = world.Spawn();
                world.Set(candidate, new NetId(100));
                world.Set(candidate, ReplicatedPosition.FromWorld(state.Position, default));
                MovementComponents.Set(world, candidate, state);
                return SnapshotWriter.WriteFiltered(world, server.Registry, new HashSet<long> { 100 }, ReplicationChannels.Persist);
            }
        }
    }

    [Fact]
    public void LocomotionInternalsAreNotExposedToProductionNetworkHeads() =>
        Assert.DoesNotContain(typeof(EffectiveMovementBinding).Assembly.GetCustomAttributes(false),
            attribute => attribute is InternalsVisibleToAttribute friend && friend.AssemblyName == "KhaozEngine.NetWorld");
}
