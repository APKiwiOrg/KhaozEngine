using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Sharding;
using KhaozEngine.Tests.NetWorld.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class ExplicitShardedPlacementTests
{
    [Fact]
    public void UnresolvedFarDestinationLeavesTheSourcePoseEpochAndOwnerUntouched()
    {
        using var pair = new Pair();
        Assert.True(pair.Server.TryGetPlayerState(pair.Slot, out var before));
        Assert.True(pair.Server.TryGetPlayerNetId(pair.Slot, out long id));
        Assert.True(pair.Server.Host.TryGetOwner(id, out var source, out _));
        var destination = pair.Server.Host.CellFor(65540, 4);
        pair.Environments[destination.Coord].Availability = MovementAvailability.Unresolved;
        var target = before;
        target.Position = new(65540, 0.7f, 4);
        pair.Server.SetPlayerState(pair.Slot, target, teleport: true);
        Assert.True(pair.Server.TryGetPlayerState(pair.Slot, out var after));
        Assert.Equal(before.Position, after.Position);
        Assert.Equal(before.TeleportEpoch, after.TeleportEpoch);
        Assert.True(pair.Server.Host.TryGetOwner(id, out var owner, out _));
        Assert.Same(source, owner);
    }

    [Fact]
    public void FarTeleportPublishesInTheDestinationFrameUnderItsReadAndClassifiesWater()
    {
        using var pair = new Pair();
        Assert.True(pair.Server.TryGetPlayerState(pair.Slot, out var before));
        Assert.True(pair.Server.TryGetPlayerNetId(pair.Slot, out long id));
        var destination = pair.Server.Host.CellFor(65540, 4);
        var environment = pair.Environments[destination.Coord];
        int published = 0;
        environment.OnPinDispose = () =>
        {
            Assert.True(pair.Server.Host.TryGetOwner(id, out var owner, out var entity));
            Assert.Same(destination, owner);
            var position = owner.World.Get<ReplicatedPosition>(entity);
            Assert.Equal(destination.Frame, position.Frame);
            Assert.Equal(4, position.Local.X);
            Assert.Equal(WaterExcursionState.Surface, owner.World.Get<MovementState>(entity).WaterExcursion);
            Assert.Equal(1, pair.Server.Host.OwnerCount(id));
            Assert.Throws<InvalidOperationException>(() => environment.Physics.Step(0.1f));
            published++;
        };
        var target = before;
        target.Position = new(65540, 0.7f, 4);
        target.Move.WaterExcursion = WaterExcursionState.AirborneFromWater;
        target.Move.VerticalVelocity = 7;
        pair.Server.SetPlayerState(pair.Slot, target, teleport: true);
        Assert.True(published > 0);
        environment.OnPinDispose = null;
        Assert.True(pair.Server.TryGetPlayerState(pair.Slot, out var after));
        Assert.True(after.Swimming);
        Assert.False(after.Grounded);
        Assert.Equal(0, after.VerticalVelocity);
        Assert.Equal(before.TeleportEpoch + 1, after.TeleportEpoch);
        Assert.Equal(target.Position, after.Position);
    }

    [Fact]
    public void SameCellTeleportCannotBypassUnavailableEnvironmentData()
    {
        using var pair = new Pair();
        Assert.True(pair.Server.TryGetPlayerState(pair.Slot, out var before));
        var cell = pair.Server.Host.CellFor(4, 4);
        pair.Environments[cell.Coord].Availability = MovementAvailability.Unresolved;
        var target = before;
        target.Position = new(5, 0.751f, 4);
        pair.Server.SetPlayerState(pair.Slot, target, teleport: true);
        Assert.True(pair.Server.TryGetPlayerState(pair.Slot, out var after));
        Assert.Equal(before.Position, after.Position);
        Assert.Equal(before.TeleportEpoch, after.TeleportEpoch);
    }

    sealed class Pair : IDisposable
    {
        public ShardedWorldServer Server { get; }
        public Dictionary<CellCoord, ExplicitPlayerEnvironment> Environments { get; } = new();
        readonly LoopbackTransport st;
        readonly LoopbackTransport ct;
        readonly NetClient client;
        public int Slot => client.Slot;
        public Pair()
        {
            (st, ct) = LoopbackTransport.CreatePair();
            ShardedWorldServer? created = null;
            Server = created = new(st, new()
            {
                TickSeconds = 0.1f,
                CellSize = 128,
                OverlapMargin = 4,
                InterestRadius = 2,
                MaxPlayers = 2,
                SpawnPosition = _ => new(4, 0.751f, 4),
                PhysicsWorldFactory = coord =>
                {
                    var environment = new ExplicitPlayerEnvironment(created!.FrameFor(coord).Anchor,
                        surfaceY: coord.X == 512 ? 1 : null);
                    Environments.Add(coord, environment);
                    return environment.Physics;
                },
                ExplicitMovementFactory = cell =>
                {
                    var environment = Environments[cell.Coord];
                    return new(environment.Context, environment.Scope, new(WaterTraversalMode.SurfaceSwimmer, 7));
                }
            }, (_, _) => throw new InvalidOperationException("Legacy sampler called."), ExplicitPlayerEnvironment.Tuning);
            client = new(ct, TestHandshake.Wire(Encoding.UTF8.GetBytes("hero")));
            for (int tick = 0; tick < 6; tick++) { client.Poll(); Server.Poll(); Server.Tick(0.1f); }
            Assert.True(Server.TryGetPlayerState(Slot, out _));
        }
        public void Dispose()
        {
            foreach (var environment in Environments.Values) environment.OnPinDispose = null;
            Server.Dispose();
            foreach (var environment in Environments.Values) environment.Dispose();
            st.Dispose(); ct.Dispose();
        }
    }
}
