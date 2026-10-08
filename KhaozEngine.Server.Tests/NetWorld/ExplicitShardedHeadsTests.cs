using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Ecs;
using KhaozEngine.Primitives;
using KhaozEngine.Replication;
using System.Collections.Generic;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Tests.NetWorld.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class ExplicitShardedHeadsTests
{
    [Fact]
    public void ShardedJoinAndTickUseTheCellEnvironmentAndPublishBeforeRelease()
    {
        using var pair = new Pair();
        pair.Pump(6);
        Assert.True(pair.Client.Joined);
        Assert.Equal(1, pair.Factories);
        int published = 0;
        pair.Environment.OnPinDispose = () =>
        {
            Assert.True(pair.Server.TryGetPlayerState(0, out var state));
            Assert.InRange(state.Position.X, 4.3999f, 4.4001f);
            Assert.Throws<InvalidOperationException>(() => pair.Environment.Physics.Step(0.1f));
            published++;
        };
        pair.Client.SendInput(new(Vector2.UnitX, false, 0));
        pair.Pump(1);
        Assert.True(published > 0);
        Assert.Equal(0, pair.LegacyCalls);
        pair.Environment.OnPinDispose = null;
        pair.Environment.Physics.Step(0.1f);
    }

    [Fact]
    public void AnUnresolvedShardedSpawnIsNotPublished()
    {
        using var pair = new Pair(ready: false);
        int joins = 0;
        pair.Server.PlayerJoined += (_, _) => joins++;
        pair.Pump(6);
        Assert.False(pair.Server.TryGetPlayerState(0, out _));
        Assert.False(pair.Client.Joined);
        Assert.Equal(0, joins);
        Assert.Equal(0, pair.LegacyCalls);
    }

    [Fact]
    public void UnresolvedShardTickAcknowledgesAndConsumesTheJump()
    {
        using var pair = new Pair();
        pair.Pump(6);
        pair.Environment.Availability = MovementAvailability.Unresolved;
        pair.Client.SendInput(new(Vector2.Zero, false, 0, jump: true));
        pair.Pump(1);
        Assert.True(pair.Server.TryGetPlayerState(0, out var held));
        Assert.True(held.Grounded);
        Assert.Equal(0, held.Move.JumpBufferRemaining);
        Assert.Equal(0, pair.Client.PendingPredictionCommands);
        pair.Environment.Availability = MovementAvailability.Known;
        pair.Client.SendInput(MoveCommand.Idle);
        pair.Pump(1);
        Assert.True(pair.Server.TryGetPlayerState(0, out var restored));
        Assert.True(restored.Grounded);
        Assert.Equal(0, restored.VerticalVelocity);
        Assert.Equal(0, pair.LegacyCalls);
    }


    [Fact]
    public void ShardedSurfaceJumpReplicatesExcursionAndReturnsToSwimming()
    {
        using var pair = new Pair(wet: true);
        pair.Pump(8);
        Assert.True(pair.Server.TryGetPlayerState(0, out var swimming));
        Assert.True(swimming.Swimming);
        Assert.Equal(WaterExcursionState.Surface, swimming.Move.WaterExcursion);
        pair.Client.SendInput(new(Vector2.UnitX, true, 0, jump: true));
        pair.Pump(1);
        Assert.True(pair.Server.TryGetPlayerState(0, out var airborne));
        Assert.False(airborne.Swimming);
        Assert.Equal(WaterExcursionState.AirborneFromWater, airborne.Move.WaterExcursion);
        Assert.InRange(airborne.Position.X - swimming.Position.X, 0.2999f, 0.3001f);
        Assert.Equal(WaterExcursionState.AirborneFromWater, pair.Client.LocalPredictedState.Move.WaterExcursion);
        pair.Client.SendInput(MoveCommand.Idle);
        pair.Pump(12);
        Assert.True(pair.Server.TryGetPlayerState(0, out var landed));
        Assert.True(landed.Swimming);
        Assert.False(landed.Grounded);
        Assert.Equal(WaterExcursionState.Surface, landed.Move.WaterExcursion);
        Assert.Equal(0, pair.LegacyCalls);
    }


    [Fact]
    public void UnclassifiedRestoredCellStateDoesNotPublishAnyEntity()
    {
        using var pair = new Pair();
        pair.Pump(6);
        var cell = pair.Server.Host.CellFor(4, 4);
        pair.Environment.Availability = MovementAvailability.Unresolved;
        var result = pair.Server.TryRestoreCell(cell.Coord, RestoreBytes(pair.Server.Registry, 0.751f));
        Assert.False(result.Ok);
        Assert.False(pair.Server.TryGetEntity(99, out _, out _));
        Assert.True(pair.Server.TryGetPlayerState(0, out _));
    }

    [Fact]
    public void ColdWaterRestoreDerivesSwimmingAndPublishesUnderTheDestinationRead()
    {
        using var pair = new Pair(wet: true);
        pair.Pump(6);
        var cell = pair.Server.Host.CellFor(4, 4);
        int published = 0;
        pair.Environment.OnPinDispose = () =>
        {
            Assert.True(pair.Server.TryGetEntity(99, out var world, out var entity));
            Assert.True(world.Get<MovementState>(entity).Swimming);
            Assert.Equal(WaterExcursionState.Surface, world.Get<MovementState>(entity).WaterExcursion);
            Assert.Throws<InvalidOperationException>(() => pair.Environment.Physics.Step(0.1f));
            published++;
        };
        var result = pair.Server.TryRestoreCell(cell.Coord, RestoreBytes(pair.Server.Registry, 0.7f));
        Assert.True(result.Ok);
        Assert.True(published > 0);
        pair.Environment.OnPinDispose = null;
    }


    [Fact]
    public void ASecondUnresolvedRestoredActorDiscardsTheEntireStagedBatch()
    {
        using var pair = new Pair();
        pair.Pump(6);
        var cell = pair.Server.Host.CellFor(4, 4);
        pair.Environment.ReconstructionAvailability = p => p.X > 4.5f ? MovementAvailability.Unresolved : MovementAvailability.Known;
        int releases = 0;
        pair.Environment.OnPinDispose = () =>
        {
            Assert.False(pair.Server.TryGetEntity(99, out _, out _));
            Assert.False(pair.Server.TryGetEntity(100, out _, out _));
            releases++;
        };
        var result = pair.Server.TryRestoreCell(cell.Coord, RestoreBytes(pair.Server.Registry, 0.751f, second: true));
        Assert.False(result.Ok);
        Assert.True(result.NeedsAdmission);
        Assert.Equal(KhaozEngine.Sharding.CellAdmissionOutcome.Unresolved, result.Admission);
        Assert.True(releases > 0);
        pair.Environment.OnPinDispose = null;
    }

    static byte[] RestoreBytes(ReplicationRegistry registry, float y, bool second = false)
    {
        var world = new World();
        Entity entity = world.Spawn();
        world.Set(entity, new NetId(99));
        world.Set(entity, ReplicatedPosition.FromWorld(new(4, y, 4), WorldFrame.Origin));
        world.Set(entity, new MovementState());
        world.Set(entity, new MovementOwnerState());
        if (second)
        {
            Entity another = world.Spawn();
            world.Set(another, new NetId(100));
            world.Set(another, ReplicatedPosition.FromWorld(new(5, y, 4), WorldFrame.Origin));
            world.Set(another, new MovementState());
            world.Set(another, new MovementOwnerState());
        }
        return SnapshotWriter.WriteFiltered(world, registry, new HashSet<long> { 99, 100 }, ReplicationChannels.Persist);
    }

    sealed class Pair : IDisposable
    {
        public ExplicitPlayerEnvironment Environment { get; }
        public ExplicitPlayerEnvironment ClientEnvironment { get; }
        public ShardedWorldServer Server { get; }
        public WorldClient Client { get; }
        public int Factories;
        public int LegacyCalls;
        readonly INetTransport serverTransport;
        public Pair(bool ready = true, bool wet = false)
        {
            Environment = new(surfaceY: wet ? 1 : null);
            ClientEnvironment = new(surfaceY: wet ? 1 : null);
            Environment.Availability = ready ? MovementAvailability.Known : MovementAvailability.Unresolved;
            var (st, ct) = LoopbackTransport.CreatePair();
            serverTransport = st;
            var config = new ShardedWorldServerConfig
            {
                TickSeconds = 0.1f,
                MaxPlayers = 2,
                CellSize = 64,
                OverlapMargin = 4,
                InterestRadius = 2,
                FrameAnchoring = false,
                SpawnPosition = _ => new(4, wet ? 0.7f : 0.751f, 4),
                PhysicsWorldFactory = _ => Environment.Physics,
                ExplicitMovementFactory = cell =>
                {
                    Factories++;
                    Assert.Same(Environment.Physics, cell.Physics);
                    return new(Environment.Context, Environment.Scope, new(WaterTraversalMode.SurfaceSwimmer, 7));
                }
            };
            Server = new(st, config, (_, _) => { LegacyCalls++; return 0; }, ExplicitPlayerEnvironment.Tuning);
            Client = new(ct, (_, _) => 0, ExplicitPlayerEnvironment.Tuning,
                new WorldClientConfig
                {
                    TickSeconds = 0.1f,
                    FrameAnchoring = false,
                    ExplicitMovement = new(ClientEnvironment.Context, ClientEnvironment.Scope, new(WaterTraversalMode.SurfaceSwimmer, 7))
                }, physics: ClientEnvironment.Physics);
        }
        public void Pump(int ticks)
        {
            for (int tick = 0; tick < ticks; tick++)
            {
                Client.Poll(); Server.Poll(); Server.Tick(0.1f); Client.Poll();
            }
        }
        public void Dispose()
        {
            Environment.OnPinDispose = null;
            Client.Dispose(); Server.Dispose(); Environment.Dispose(); ClientEnvironment.Dispose(); serverTransport.Dispose();
        }
    }
}
