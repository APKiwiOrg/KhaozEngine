using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Tests.NetWorld.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class ExplicitPlayerReadTests
{
    static PlayerMoveSimulator Simulator(ExplicitPlayerEnvironment environment) => new(
        (_, _) => throw new InvalidOperationException("Explicit movement must not call the legacy terrain sampler."),
        ExplicitPlayerEnvironment.Tuning, null, null, environment.Physics, null, SamplerSpace.World,
        new ExplicitPlayerMovement(environment.Context, environment.Scope, new(WaterTraversalMode.SurfaceSwimmer, 7)));

    [Fact]
    public void ReadLeaseOutlivesStepUntilThePredictionOwnerHasStoredItsState()
    {
        using var environment = new ExplicitPlayerEnvironment();
        var simulator = Simulator(environment);
        var prediction = new ClientPrediction<PlayerMoveState, MoveCommand>(simulator,
            PredictionSettings.Default with { TickSeconds = 0.1f });
        prediction.Reset(ExplicitPlayerEnvironment.Initial);
        using (var read = simulator.BeginExplicitRead(prediction.PredictedState))
        {
            Assert.True(read!.BasisValid);
            prediction.Predict(new(Vector2.UnitX, false, 0));
            Assert.Equal(MovementStepOutcome.Advanced, simulator.LastExplicitOutcome);
            Assert.InRange(prediction.PredictedState.Position.X, 0.3999f, 0.4001f);
            Assert.Throws<InvalidOperationException>(() => environment.Physics.Step(0.1f));
        }
        Assert.Equal(1, environment.PinsDisposed);
        environment.Physics.Step(0.1f);
    }

    [Fact]
    public void CorrectionReplayRebuildsFromTheAuthoritativePoseUnderTheSameLease()
    {
        using var environment = new ExplicitPlayerEnvironment();
        var simulator = Simulator(environment);
        var prediction = new ClientPrediction<PlayerMoveState, MoveCommand>(simulator,
            PredictionSettings.Default with { TickSeconds = 0.1f });
        prediction.Reset(ExplicitPlayerEnvironment.Initial);
        int first;
        using (simulator.BeginExplicitRead(prediction.PredictedState))
        {
            first = prediction.Predict(new(Vector2.UnitX, false, 0));
            prediction.Predict(new(Vector2.UnitX, false, 0));
        }
        var basis = ExplicitPlayerEnvironment.Initial;
        basis.Position = new(-0.5f, 0.751f, 0);
        environment.RebuiltPositions.Clear();
        using (var read = simulator.BeginExplicitRead(basis))
        {
            Assert.True(read!.BasisValid);
            prediction.Reconcile(1, basis, first);
            Assert.Equal(1, prediction.PendingCommandCount);
            Assert.InRange(prediction.PredictedState.Position.X, -0.1001f, -0.0999f);
            Assert.Contains(basis.Position, environment.RebuiltPositions);
            Assert.DoesNotContain(environment.RebuiltPositions, position => position.X > 0.5f);
            Assert.Throws<InvalidOperationException>(() => environment.Physics.Step(0.1f));
        }
        environment.Physics.Step(0.1f);
    }

    [Fact]
    public void RefusedPressIsAcknowledgedWithoutLaunchingWhenTheEnvironmentReturns()
    {
        using var environment = new ExplicitPlayerEnvironment();
        var simulator = Simulator(environment);
        var prediction = new ClientPrediction<PlayerMoveState, MoveCommand>(simulator,
            PredictionSettings.Default with { TickSeconds = 0.1f });
        prediction.Reset(ExplicitPlayerEnvironment.Initial);
        environment.Availability = MovementAvailability.Unresolved;
        int sequence;
        using (var read = simulator.BeginExplicitRead(prediction.PredictedState))
        {
            Assert.False(read!.BasisValid);
            sequence = prediction.Predict(new(Vector2.Zero, false, 0, jump: true));
            Assert.Equal(MovementStepOutcome.EnvironmentUnresolved, simulator.LastExplicitOutcome);
            Assert.Equal(0, prediction.PredictedState.Move.JumpBufferRemaining);
        }
        environment.Availability = MovementAvailability.Known;
        using (var read = simulator.BeginExplicitRead(ExplicitPlayerEnvironment.Initial))
        {
            Assert.True(read!.BasisValid);
            prediction.Reconcile(1, ExplicitPlayerEnvironment.Initial, sequence);
            Assert.Equal(0, prediction.PendingCommandCount);
            prediction.Predict(MoveCommand.Idle);
            Assert.True(prediction.PredictedState.Grounded);
            Assert.Equal(0, prediction.PredictedState.VerticalVelocity);
        }
    }

    [Fact]
    public void AnExplicitSimulatorCannotFallBackWhenTheCallerOmitsItsReadScope()
    {
        using var environment = new ExplicitPlayerEnvironment();
        var simulator = Simulator(environment);
        PlayerMoveState input = ExplicitPlayerEnvironment.Initial;
        input.Move.JumpBufferRemaining = 1;
        var held = simulator.Step(input, new(Vector2.UnitX, true, 0, jump: true), 0.1f);
        Assert.Equal(input.Position, held.Position);
        Assert.Equal(input.FrameAnchor, held.FrameAnchor);
        Assert.Equal(0, held.Move.JumpBufferRemaining);
        Assert.Equal(MovementStepOutcome.EnvironmentUnresolved, simulator.LastExplicitOutcome);
        Assert.Equal(0, environment.Prepared);
    }

    [Fact]
    public void AFrameMismatchHoldsTheOriginalStampWithoutAcquiringTheWrongView()
    {
        using var environment = new ExplicitPlayerEnvironment();
        var simulator = Simulator(environment);
        var state = ExplicitPlayerEnvironment.Initial;
        state.FrameAnchor = new(512, 0);
        using var read = simulator.BeginExplicitRead(state);
        Assert.False(read!.BasisValid);
        Assert.Equal(MovementStepOutcome.FrameMismatch, read.Outcome);
        var held = simulator.Step(state, MoveCommand.Idle, 0.1f);
        Assert.Equal(state.FrameAnchor, held.FrameAnchor);
        Assert.Equal(state.Position, held.Position);
        Assert.Equal(0, environment.Prepared);
    }

    [Fact]
    public void AThrowingPublicationReleasesBothPinsAndAllowsTheNextRead()
    {
        using var environment = new ExplicitPlayerEnvironment();
        var simulator = Simulator(environment);
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var read = simulator.BeginExplicitRead(ExplicitPlayerEnvironment.Initial);
            Assert.True(read!.BasisValid);
            throw new InvalidOperationException("Publication failed.");
        }));
        environment.Physics.Step(0.1f);
        using var next = simulator.BeginExplicitRead(ExplicitPlayerEnvironment.Initial);
        Assert.True(next!.BasisValid);
    }
}
