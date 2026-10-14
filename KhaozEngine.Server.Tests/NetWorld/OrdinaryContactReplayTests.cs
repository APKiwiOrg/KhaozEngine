using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.Tests.NetWorld.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public partial class OrdinaryContactReplayTests
{
    [Fact]
    public void OrdinaryReplayKeepsOneResidentScopeIncludingCommitmentLaunch()
    {
        using var server = new OrdinaryPlayerWorld();
        using var client = new OrdinaryPlayerWorld();
        using var authority = server.Simulator();
        using var replay = client.Simulator();
        var basis = server.Initial();
        basis.Move.Commitment = new MovementCommitment(1, Vector2.UnitX, 3, 4, 20, 0.15f, 0, 3);
        var commands = new[] { MoveCommand.Idle, MoveCommand.Idle, MoveCommand.Idle, MoveCommand.Idle };
        var states = new List<MoveState>();
        var state = basis;
        foreach (var command in commands)
        {
            using var read = authority.BeginExplicitRead(state);
            Assert.True(read!.BasisValid);
            state = authority.Step(state, command, OrdinaryPlayerWorld.Dt);
            states.Add(state.Move);
        }
        var replayStates = new List<MoveState>();
        int readsBefore = client.Physics.ReadCount;
        using (var read = replay.BeginExplicitRead(basis))
        {
            Assert.True(read!.BasisValid);
            var scope = read.Queries!.Witness.Scope;
            state = basis;
            foreach (var command in commands)
            {
                state = replay.Step(state, command, OrdinaryPlayerWorld.Dt);
                replayStates.Add(state.Move);
                Assert.Equal(scope, read.Queries.Witness.Scope);
            }
            Assert.Throws<System.InvalidOperationException>(() => client.Physics.Step(0.1f));
        }
        Assert.Equal(1, client.Physics.ReadCount - readsBefore);
        Assert.Equal(states, replayStates);
        Assert.True(replayStates[1].Commitment.IsActive);
        Assert.Equal(MovementCommitmentPhase.Airborne, replayStates[1].Commitment.Phase);
    }

    [Fact]
    public void ClientResidentIslandHoldsWhileLargerServerIslandAdvances()
    {
        using var server = new OrdinaryPlayerWorld();
        using var client = new OrdinaryPlayerWorld(island: 8);
        using var authority = server.Simulator();
        using var replay = client.Simulator();
        var basis = server.Initial(7.6f);
        var command = new MoveCommand(Vector2.UnitX, false, 0);
        using (authority.BeginExplicitRead(basis)) Assert.True(authority.Step(basis, command, 1f).Position.X > basis.Position.X);
        using (replay.BeginExplicitRead(basis))
        {
            Assert.Equal(basis.Position, replay.Step(basis, command, 1f).Position);
            Assert.Equal(MovementStepOutcome.EnvironmentUnresolved, replay.LastExplicitOutcome);
        }
    }
}
