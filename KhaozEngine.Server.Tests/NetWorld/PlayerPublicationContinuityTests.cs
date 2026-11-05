using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using KhaozEngine.Primitives;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using KhaozEngine.Tests.NetWorld.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class PlayerPublicationContinuityTests
{
    const float Dt = 1f / 30;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingReplayCarriesTheSamePublishedBodyAsAuthority(bool sharded)
    {
        // #1707. The correction seed and every pending step must include publication, even with zero position error.
        var tuning = OrdinaryPlayerWorld.Tuning with { AirMomentum = true, FacingTurnSpeed = 0.7f };
        using var heads = new OrdinaryNetworkHeads(sharded, tuning: tuning, tickSeconds: Dt);
        var basis = heads.State;
        basis.Position = new(0, 6, 0);
        basis.Grounded = false;
        basis.Move.HorizontalVelocity = new(3.123f, -2.456f);
        basis.Move.VerticalVelocity = -1.123f;
        basis.Move.SpeedScale = 1.1f;
        Assert.True(heads.Place(basis));
        heads.Pump(1);
        var first = new MoveCommand(new(0.4f, 0.2f), false, 0.7f);
        var pending = new MoveCommand(new(-0.2f, 0.3f), true, -0.5f);
        Assert.True(heads.Client.SendInput(first) >= 0);
        heads.Serve();
        var authoritative = heads.State;
        Assert.True(heads.Client.SendInput(pending) >= 0);
        Assert.Equal(2, heads.Client.PendingPredictionCommands);
        using var simulator = heads.ClientWorld.Simulator(tuning);
        PlayerMoveState replayed;
        using (simulator.BeginExplicitRead(authoritative)) replayed = simulator.Step(authoritative, pending, Dt);
        heads.Client.Poll();
        Assert.Equal(1, heads.Client.PendingPredictionCommands);
        ContactDropPublicationTests.AssertBodyBasis(replayed.Move, heads.Client.LocalPredictedState.Move);
        Assert.Equal(replayed.Move.FacingYaw, heads.Client.LocalPredictedState.Move.FacingYaw);
        heads.Pump(1);
        Assert.Equal(0, heads.Client.PendingPredictionCommands);
        ContactDropPublicationTests.AssertBodyBasis(replayed.Move, heads.State.Move);
        ContactDropPublicationTests.AssertBodyBasis(heads.State.Move, heads.Client.LocalPredictedState.Move);
    }

    [Fact]
    public void ActualShardHandoffKeepsEveryPublishedBodyBasisField()
    {
        using var world = new OrdinaryPlayerWorld();
        using var simulator = world.Simulator(OrdinaryPlayerWorld.Tuning with { AirMomentum = true });
        var basis = world.Initial(9.99f, 5, 6);
        basis.Grounded = false;
        basis.Move.VerticalVelocity = -1.123f;
        basis.Move.HorizontalVelocity = new(12.123f, 0.123f);
        basis.Move.TimeSinceGrounded = 0.075f;
        basis.Move.JumpBufferRemaining = 0.075f;
        basis.Move.SpeedScale = 1.1f;
        basis.Move.FacingYaw = 0.321f;
        PlayerMoveState stepped;
        using (simulator.BeginExplicitRead(basis)) stepped = simulator.Step(basis, MoveCommand.Idle, Dt);
        Assert.True(stepped.Position.X > 10);
        using var host = new ShardHost(10, Dt, MoveProtocol.CreateRegistry(), interestCellSize: 10,
            overlapMargin: 0, positionAccessor: Position);
        Entity entity = host.SpawnOwned(9.99f, 5, 7, out var source);
        source.World.Set(entity, ReplicatedPosition.FromWorld(stepped.Position, WorldFrame.Origin));
        source.World.Set(entity, MovementState.From(stepped));
        source.World.Set(entity, MovementOwnerState.From(stepped));
        host.ProcessHandoffs();
        Assert.True(host.TryGetOwner(7, out var destination, out var moved));
        Assert.NotEqual(source.Coord, destination.Coord);
        var arrived = PlayerMoveState.From(destination.World.Get<ReplicatedPosition>(moved),
            destination.World.Get<MovementState>(moved), destination.World.Get<MovementOwnerState>(moved));
        ContactDropPublicationTests.AssertBodyBasis(stepped.Move, arrived.Move);
        Assert.Equal(stepped.Move.FacingYaw, arrived.Move.FacingYaw);
        Assert.Equal(stepped.Move.ClimbRate, arrived.Move.ClimbRate);
        Assert.Equal(stepped.Move.ClimbRateEwma, arrived.Move.ClimbRateEwma);
    }

    static bool Position(World world, Entity entity, out float x, out float z)
    {
        bool found = world.TryGet(entity, out ReplicatedPosition position);
        x = position.Value.X;
        z = position.Value.Z;
        return found;
    }
}
