using System;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Tests.NetWorld.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public partial class OrdinaryContactConfigurationTests
{
    [Fact]
    public void ReconnectClientRefusesIncompleteContactConfigurationBeforeCreatingTransport()
    {
        using var world = new OrdinaryPlayerWorld();
        var (st, ct) = LoopbackTransport.CreatePair();
        using (st) using (ct)
        {
            int connections = 0;
            Assert.Throws<ArgumentException>(() => new WorldClient(() => { connections++; return ct; },
                world.Ground, OrdinaryPlayerWorld.Tuning, new() { FrameAnchoring = false }, physics: world.Physics));
            Assert.Equal(0, connections);
        }
    }

    [Fact]
    public void FailedHeadsAndDisposedAdaptersLeaveCallerExplicitViewsUsable()
    {
        using var environment = new ExplicitPlayerEnvironment();
        var settings = new ExplicitPlayerMovement(environment.Context, environment.Scope,
            new(WaterTraversalMode.SurfaceSwimmer, 7));
        var (st, ct) = LoopbackTransport.CreatePair();
        using (st) using (ct)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new WorldServer(st, new()
            { FrameAnchoring = false, MaxPlayers = 0, ExplicitMovement = settings },
                (_, _) => 0, ExplicitPlayerEnvironment.Tuning, physics: environment.Physics));
            Assert.Throws<ArgumentException>(() => new WorldClient(ct, (_, _) => 0,
                ExplicitPlayerEnvironment.Tuning, new()
                { FrameAnchoring = false, ExplicitMovement = settings, ReceiveServerTick = true,
                    RequestUnreliableDeltaReplication = true }, physics: environment.Physics));
        }
        var first = new PlayerMoveSimulator((_, _) => 0, ExplicitPlayerEnvironment.Tuning, null, null,
            environment.Physics, null, SamplerSpace.World, settings);
        first.Dispose();
        using var second = new PlayerMoveSimulator((_, _) => 0, ExplicitPlayerEnvironment.Tuning, null, null,
            environment.Physics, null, SamplerSpace.World, settings);
        using var read = second.BeginExplicitRead(ExplicitPlayerEnvironment.Initial);
        Assert.True(read!.BasisValid);
        var state = second.Step(ExplicitPlayerEnvironment.Initial, new(System.Numerics.Vector2.UnitX, false, 0), 0.1f);
        Assert.Equal(0.4f, state.Position.X);
        Assert.Equal(MovementStepOutcome.Advanced, second.LastExplicitOutcome);
    }

    [Fact]
    public void SingleHeadConstructorFailuresReleaseCreatedMovementViews()
    {
        using var world = new OrdinaryPlayerWorld();
        var (st, ct) = LoopbackTransport.CreatePair();
        using (st) using (ct)
        {
            int before = world.Physics.ViewDisposals;
            Assert.Throws<ArgumentOutOfRangeException>(() => new WorldServer(st, new()
            {
                FrameAnchoring = false, MaxPlayers = 0,
                MovementRegistration = world.Registration, MovementBoundary = world.Boundary
            }, world.Ground, OrdinaryPlayerWorld.Tuning, physics: world.Physics));
            Assert.Equal(before + 1, world.Physics.ViewDisposals);
            Assert.Throws<ArgumentException>(() => new WorldClient(ct, world.Ground, OrdinaryPlayerWorld.Tuning, new()
            {
                FrameAnchoring = false, ReceiveServerTick = true, RequestUnreliableDeltaReplication = true,
                MovementRegistration = world.Registration, MovementBoundary = world.Boundary
            }, physics: world.Physics));
            Assert.Equal(before + 2, world.Physics.ViewDisposals);
            Assert.Equal(0, world.Physics.SourceDisposals);
        }
        using var read = world.Physics.AcquireQueryReadLease();
        read.AssertCurrent();
    }
}
