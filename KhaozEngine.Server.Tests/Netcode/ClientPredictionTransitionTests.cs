using System.Numerics;
using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

public class ClientPredictionTransitionTests
{
    private const float Tick = 0.25f;

    private readonly record struct State(
        Vector2 Position, float Height, float Heading, uint Epoch = 0, float Step = 0f)
        : IPredictedState<State>
    {
        public Vector2 PredictionTarget => Position + new Vector2(0.5f, -0.25f);
        public float Vertical => Height;
        public bool HasYaw => true;
        public float Yaw => Heading;
        public uint TeleportEpoch => Epoch;
        public float StepDeltaY => Step;
        public State WithPosition(Vector2 position) => this with { Position = position };
        public State WithRenderState(Vector2 position, float vertical, float yaw)
            => this with { Position = position, Height = vertical, Heading = yaw };
    }

    private readonly record struct Command(Vector2 Velocity, float VerticalVelocity, float Heading, float Step = 0f);

    private sealed class Simulator : ITickSimulator<State, Command>
    {
        public State Step(in State state, in Command command, float dt) => state with
        {
            Position = state.Position + command.Velocity * dt,
            Height = state.Height + command.VerticalVelocity * dt,
            Heading = command.Heading,
            Step = command.Step,
        };
    }

    private static ClientPrediction<State, Command> New()
    {
        var settings = PredictionSettings.Default with
        {
            TickSeconds = Tick,
            CorrectionRate = 2f,
            InterpolateYaw = true,
        };
        var prediction = new ClientPrediction<State, Command>(new Simulator(), settings);
        var initial = new State(Vector2.Zero, 0f, 0f, 9);
        prediction.Reset(initial);
        prediction.Reconcile(0, initial, -1);
        return prediction;
    }

    private static void PredictOldCommands(ClientPrediction<State, Command> prediction)
    {
        Assert.Equal(0, prediction.Predict(new Command(new Vector2(8f, 4f), 8f, 0.8f, 0.3f)));
        Assert.Equal(1, prediction.Predict(new Command(new Vector2(8f, 4f), 8f, 1.6f, -0.1f)));
    }

    [Fact]
    public void TransitionPreservesTheNextCommandSequence()
    {
        var prediction = New();
        PredictOldCommands(prediction);

        prediction.ResetForTransition(new State(new Vector2(100f, -20f), 7f, -0.6f, 1));

        Assert.Equal(2, prediction.Predict(new Command(Vector2.Zero, 0f, -0.6f)));
    }

    [Fact]
    public void TransitionDiscardsOldReplayWithAMissingAcknowledgement()
    {
        var prediction = New();
        PredictOldCommands(prediction);
        var basis = new State(new Vector2(100f, -20f), 7f, -0.6f, 1);

        prediction.ResetForTransition(basis);
        Assert.Equal(0, prediction.PendingCommandCount);
        int sequence = prediction.Predict(new Command(new Vector2(12f, -8f), -4f, -0.2f, 0.25f));

        // Ack 0 leaves old command 1 unacknowledged. Only the new command may replay.
        var result = prediction.Reconcile(10, basis, lastAcknowledgedSeq: 0);
        Assert.Equal(new Vector2(103f, -22f), prediction.PredictedState.Position);
        Assert.Equal(6f, prediction.PredictedState.Height);
        Assert.Equal(-0.2f, prediction.PredictedState.Heading);
        Assert.Equal(1, prediction.PendingCommandCount);
        Assert.Equal(2, sequence);
        Assert.Equal(0.25f, prediction.StepCumulativeY);
        Assert.True(result.Teleported);

        var repeated = prediction.Reconcile(11, basis, lastAcknowledgedSeq: 0);
        Assert.Equal(new Vector2(103f, -22f), prediction.PredictedState.Position);
        Assert.Equal(6f, prediction.PredictedState.Height);
        Assert.False(repeated.Teleported);
    }

    [Fact]
    public void TransitionClearsActiveCorrectionInterpolationSpeedAndStepState()
    {
        var prediction = New();
        prediction.Predict(new Command(new Vector2(8f, 4f), 8f, 0.8f, 0.3f));
        prediction.AdvancePresentation(Tick);
        var correction = new State(new Vector2(1f, 1f), 1f, 0f, 9);
        Assert.False(prediction.Reconcile(1, correction, -1).HardSnapApplied);
        Assert.Equal(new Vector2(2.5f, 0.75f), prediction.RenderedState.Position);
        Assert.Equal(2f, prediction.RenderedState.Height);
        prediction.Predict(new Command(new Vector2(8f, 4f), 8f, 1.6f, -0.1f));
        prediction.AdvancePresentation(Tick / 2f);

        // Half-tick targets are (4.5, 2.25), height 4, yaw 1.2. Both offsets are still decaying.
        Assert.NotEqual(new Vector2(4.5f, 2.25f), prediction.RenderedState.Position);
        Assert.NotEqual(4f, prediction.RenderedState.Height);
        Assert.Equal(1.2f, prediction.RenderedState.Heading, 5);
        Assert.Equal(new Vector2(1f, 0.5f), prediction.RemainingPresentationMovement);
        Assert.True(prediction.PredictedHorizontalSpeed > 0f);
        Assert.Equal(0.2f, prediction.StepCumulativeY, 5);
        var basis = new State(new Vector2(20f, -8f), 3f, -1f, 2, 0.7f);

        prediction.ResetForTransition(basis);

        Assert.Equal(basis, prediction.PredictedState);
        Assert.Equal(basis.PredictionTarget, prediction.RenderedState.Position);
        Assert.Equal(3f, prediction.RenderedState.Height);
        Assert.Equal(-1f, prediction.RenderedState.Heading);
        Assert.Equal(Vector2.Zero, prediction.RemainingPresentationMovement);
        Assert.Equal(0f, prediction.PredictedHorizontalSpeed);
        Assert.Equal(0f, prediction.StepCumulativeY);
        Assert.Equal(0, prediction.PendingCommandCount);

        prediction.AdvancePresentation(Tick / 2f);
        Assert.Equal(basis.PredictionTarget, prediction.RenderedState.Position);
        Assert.Equal(3f, prediction.RenderedState.Height);
        Assert.Equal(-1f, prediction.RenderedState.Heading);

        // A subsequent correction eases from the new placement on both axes.
        var shifted = basis with { Position = new Vector2(21f, -7f), Height = 4f };
        prediction.Reconcile(2, shifted, 0);
        prediction.AdvancePresentation(Tick / 2f);
        Assert.Equal(20.590565f, prediction.RenderedState.Position.X, 5);
        Assert.Equal(-8.159435f, prediction.RenderedState.Position.Y, 5);
        Assert.Equal(3.090565f, prediction.RenderedState.Height, 5);
        Assert.Equal(-1f, prediction.RenderedState.Heading);
    }

    [Fact]
    public void TransitionReportsOneTeleportEvenWithoutDistanceOrEpochChange()
    {
        var prediction = New();
        var basis = prediction.PredictedState;

        prediction.ResetForTransition(basis);

        var first = prediction.Reconcile(1, basis, -1);
        Assert.True(first.Teleported);
        Assert.False(first.HardSnapApplied);
        Assert.False(prediction.Reconcile(2, basis, -1).Teleported);
        prediction.ResetForTransition(basis);
        Assert.True(prediction.Reconcile(3, basis, -1).Teleported);
        Assert.False(prediction.Reconcile(4, basis, -1).Teleported);
    }

    [Fact]
    public void ConsecutiveTransitionsInstallTheLatestBasisAndCoalesceTheSignal()
    {
        var prediction = New();
        PredictOldCommands(prediction);
        var latest = new State(new Vector2(5f, -2f), 4f, 0.4f, 1);

        prediction.ResetForTransition(new State(new Vector2(90f, 10f), 20f, -1f, 3));
        prediction.ResetForTransition(latest);

        Assert.Equal(latest, prediction.PredictedState);
        Assert.Equal(latest.PredictionTarget, prediction.RenderedState.Position);
        Assert.Equal(0, prediction.PendingCommandCount);
        Assert.True(prediction.Reconcile(1, latest, -1).Teleported);
        Assert.False(prediction.Reconcile(2, latest, -1).Teleported);
        Assert.Equal(2, prediction.Predict(new Command(Vector2.Zero, 0f, 0.4f)));
    }

    [Fact]
    public void ResetStillStartsANewCommandSequenceAndClearsReplay()
    {
        var prediction = New();
        PredictOldCommands(prediction);
        var basis = new State(new Vector2(5f, -2f), 4f, 0.4f, 1);

        prediction.Reset(basis);

        Assert.Equal(0, prediction.PendingCommandCount);
        Assert.Equal(0, prediction.Predict(new Command(new Vector2(4f, 0f), 0f, 0.4f)));
        Assert.True(prediction.Reconcile(1, basis, -1).Teleported);
        Assert.Equal(new Vector2(6f, -2f), prediction.PredictedState.Position);
        Assert.False(prediction.Reconcile(2, basis, -1).Teleported);
    }

    [Theory]
    [InlineData(5f, false)]
    [InlineData(200f, true)]
    public void ReseedStillPreservesReplayAndUsesResumeDistance(float basisX, bool teleported)
    {
        var prediction = New();
        PredictOldCommands(prediction);
        prediction.AdvancePresentation(Tick);
        State renderedBefore = prediction.RenderedState;
        var basis = new State(new Vector2(basisX, 2f), 4f, -0.4f, 1);

        prediction.Reseed(basis);

        Assert.Equal(2, prediction.PendingCommandCount);
        Assert.Equal(basis, prediction.PredictedState);
        Assert.Equal(teleported ? basis.PredictionTarget : renderedBefore.Position,
            prediction.RenderedState.Position);
        Assert.Equal(0f, prediction.PredictedHorizontalSpeed);
        Assert.Equal(0f, prediction.StepCumulativeY);
        Assert.Equal(2, prediction.Predict(new Command(new Vector2(4f, -4f), -4f, -0.4f)));
        var result = prediction.Reconcile(1, basis, 0);
        Assert.Equal(new Vector2(basisX + 3f, 2f), prediction.PredictedState.Position);
        Assert.Equal(5f, prediction.PredictedState.Height);
        Assert.Equal(2, prediction.PendingCommandCount);
        Assert.Equal(teleported, result.Teleported);
        Assert.False(prediction.Reconcile(2, prediction.PredictedState, 2).Teleported);
    }
}
