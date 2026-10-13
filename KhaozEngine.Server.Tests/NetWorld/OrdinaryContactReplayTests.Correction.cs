using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Tests.NetWorld.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public partial class OrdinaryContactReplayTests
{
    [Fact]
    public void CorrectionClassifiesTheExactAuthorityWithoutSettlingItsPose()
    {
        using var world = new OrdinaryPlayerWorld();
        using var simulator = world.Simulator();
        var prediction = new ClientPrediction<PlayerMoveState, MoveCommand>(simulator,
            PredictionSettings.Default with { TickSeconds = 0.1f });
        var basis = world.Initial();
        prediction.Reset(basis);
        int sequence;
        using (simulator.BeginExplicitRead(basis)) sequence = prediction.Predict(new(Vector2.UnitX, false, 0));
        var correction = world.Initial(-0.5f, y: 0.76f);
        correction.Move.Grounded = false;
        correction.Move.VerticalVelocity = -0.125f;
        correction.Move.TimeSinceGrounded = 0.375f;
        correction.Move.JumpBufferRemaining = 0.05f;
        using (var read = simulator.BeginExplicitRead(correction))
        {
            Assert.True(read!.BasisValid);
            prediction.Reconcile(1, correction, sequence);
            Assert.Equal(correction, prediction.PredictedState);
            // This fixture distinguishes classification from cold placement.
            var placed = simulator.SettlePlacement(correction, read);
            Assert.Equal(MovementStepOutcome.Advanced, placed.Outcome);
            Assert.Equal(0.75f, placed.State.State.Position.Y);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClientDefersAnUnclassifiedCorrectionAndRetriesBeforePrediction(bool sharded)
    {
        using var field = new OrdinaryShoreField();
        using var heads = new OrdinaryNetworkHeads(sharded, clientField: field, spawn: new(0.75f, 0.75f, 4));
        Assert.True(heads.Client.Joined);
        var before = heads.Client.LocalPredictedState;
        field.Ready = false;
        Assert.True(heads.Client.SendInput(new(-Vector2.UnitX, false, 0)) >= 0);
        heads.Pump(1);
        var authority = heads.State;
        Assert.Equal(0.35f, authority.Position.X);
        Assert.Equal(before.Position, heads.Client.LocalPredictedState.Position);
        Assert.Equal(1, heads.Client.PendingPredictionCommands);
        field.Ready = true;
        Assert.True(heads.Client.SendInput(MoveCommand.Idle) >= 0);
        using var simulator = heads.ClientWorld.Simulator();
        var basis = new PlayerMoveState
        {
            Move = OrdinaryContactHeadTests.WirePublication(authority.Move),
            TeleportEpoch = authority.TeleportEpoch
        };
        using var read = simulator.BeginExplicitRead(basis);
        Assert.True(read!.BasisValid);
        Assert.Equal(simulator.Step(basis, MoveCommand.Idle, 0.1f), heads.Client.LocalPredictedState);
        Assert.Equal(1, heads.Client.PendingPredictionCommands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FirstUnclassifiedOrdinaryBasisDoesNotPublishAnOriginAvatar(bool sharded)
    {
        using var field = new OrdinaryShoreField { Ready = false };
        using var heads = new OrdinaryNetworkHeads(sharded, clientField: field, spawn: new(0.75f, 0.75f, 4));
        Assert.DoesNotContain(heads.Client.Snapshot(), item => item.IsLocal);
        field.Ready = true;
        heads.Pump(1);
        Assert.Contains(heads.Client.Snapshot(), item => item.IsLocal && item.Position == heads.State.Position);
        Assert.Equal(OrdinaryContactHeadTests.WirePublication(heads.State.Move), heads.Client.LocalPredictedState.Move);
    }

    [Fact]
    public void CorrectionReplayUsesOneOrdinaryReadThroughASecondCommandCommitmentLaunch()
    {
        using var serverWorld = new OrdinaryPlayerWorld();
        using var clientWorld = new OrdinaryPlayerWorld();
        using var authority = serverWorld.Simulator();
        using var replay = clientWorld.Simulator();
        var basis = serverWorld.Initial();
        basis.Move.Commitment = new(1, Vector2.UnitX, 3, 4, 20, 0.15f, 0, 3);
        var prediction = new ClientPrediction<PlayerMoveState, MoveCommand>(replay,
            PredictionSettings.Default with { TickSeconds = 0.1f });
        var wrong = basis;
        wrong.Position += new Vector3(0.125f, 0, 0);
        prediction.Reset(wrong);
        int first = -1;
        using (replay.BeginExplicitRead(wrong))
            for (int i = 0; i < 4; i++)
            {
                int sequence = prediction.Predict(MoveCommand.Idle);
                if (i == 0) first = sequence;
            }
        PlayerMoveState correction;
        using (authority.BeginExplicitRead(basis)) correction = authority.Step(basis, MoveCommand.Idle, 0.1f);
        var expected = correction;
        for (int i = 1; i < 4; i++)
        {
            using var read = authority.BeginExplicitRead(expected);
            Assert.True(read!.BasisValid);
            expected = authority.Step(expected, MoveCommand.Idle, 0.1f);
            if (i == 1) Assert.Equal(MovementCommitmentPhase.Airborne, expected.Move.Commitment.Phase);
        }
        int before = clientWorld.Physics.ReadCount;
        using (var read = replay.BeginExplicitRead(correction))
        {
            Assert.True(read!.BasisValid);
            var scope = read.Queries!.Witness.Scope;
            prediction.Reconcile(1, correction, first);
            Assert.Equal(scope, read.Queries.Witness.Scope);
            Assert.Equal(3, prediction.PendingCommandCount);
            Assert.Equal(expected, prediction.PredictedState);
        }
        Assert.Equal(1, clientWorld.Physics.ReadCount - before);
    }
}
