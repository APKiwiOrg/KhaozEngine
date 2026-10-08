using System;
using System.Numerics;
using KhaozEngine.Locomotion;
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
