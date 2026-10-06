using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

// The command-phase overload of ClientPrediction.AdvancePresentation (#1313). A forward Predict that lands partway
// through a render frame has to render only the time past it, so the inter-tick clock is placed on the caller's
// command-clock residual rather than accumulating the whole frame.
public sealed class ClientPredictionPresentationPhaseTests
{
    private const float Tick = 1f / 30f;
    private const float Speed = 5f;
    // One predicted tick moves the state this far along X.
    private const float TickStep = Speed * Tick;
    private static readonly Vector2 Move = new(Speed, 0f);

    private readonly record struct State(Vector2 Position) : IPredictedState<State>
    {
        public State WithPosition(Vector2 position) => this with { Position = position };
    }

    private sealed class Simulator : ITickSimulator<State, Vector2>
    {
        public State Step(in State state, in Vector2 command, float dt) =>
            state.WithPosition(state.Position + command * dt);
    }

    // A heading-carrying state. A non-hard reconcile moves only the heading's target, so the inter-tick clock is
    // observable through the rendered heading even after a cut collapsed the positional segment.
    private readonly record struct YawState(Vector2 Position, float Heading) : IPredictedState<YawState>
    {
        bool IPredictedState<YawState>.HasYaw => true;
        float IPredictedState<YawState>.Yaw => Heading;
        public YawState WithPosition(Vector2 position) => this with { Position = position };
        YawState IPredictedState<YawState>.WithRenderState(Vector2 position, float vertical, float yaw)
            => this with { Position = position, Heading = yaw };
    }

    private sealed class YawSimulator : ITickSimulator<YawState, Vector2>
    {
        public YawState Step(in YawState state, in Vector2 command, float dt) =>
            state.WithPosition(state.Position + command * dt);
    }

    public enum Cut { None, Reset, ResetForTransition, Reseed, HardSnap }

    private static ClientPrediction<State, Vector2> New()
    {
        var prediction = new ClientPrediction<State, Vector2>(new Simulator(),
            PredictionSettings.Default with { TickSeconds = Tick });
        prediction.Reset(new State(Vector2.Zero));
        return prediction;
    }

    // A prediction carrying a live reconciliation offset, with a fresh forward prediction pending presentation.
    private static ClientPrediction<State, Vector2> NewWithCorrectionPending()
    {
        ClientPrediction<State, Vector2> prediction = New();
        prediction.Predict(Move);
        prediction.Predict(Move);
        // Acknowledges seq 0 and replays seq 1 on a basis 0.3 ahead: a glide well under HardSnapDistance.
        ReconciliationResult result = prediction.Reconcile(1, new State(new Vector2(TickStep + 0.3f, 0f)), 0);
        Assert.False(result.HardSnapApplied);
        prediction.Predict(Move);
        return prediction;
    }

    private static float X(ClientPrediction<State, Vector2> prediction) => prediction.RenderedState.Position.X;

    private static int Bits(float value) => BitConverter.SingleToInt32Bits(value);

    private static void AssertSameRender(
        ClientPrediction<State, Vector2> expected, ClientPrediction<State, Vector2> actual)
    {
        Assert.Equal(Bits(expected.RenderedState.Position.X), Bits(actual.RenderedState.Position.X));
        Assert.Equal(Bits(expected.RenderedState.Position.Y), Bits(actual.RenderedState.Position.Y));
    }

    [Theory]
    [InlineData(60)]
    [InlineData(100)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(165)]
    public void SteadyMotionHasUniformRenderedStepsAtFractionalCadences(int frameRate)
    {
        ClientPrediction<State, Vector2> prediction = New();
        float dt = 1f / frameRate;
        double phase = 0;
        float previous = 0;

        // One second of startup, then one second of steady movement. This advances a finite simulated clock.
        for (int frame = 0; frame < frameRate * 2; frame++)
        {
            phase += dt;
            while (phase >= Tick)
            {
                phase -= Tick;
                prediction.Predict(Move);
            }
            prediction.AdvancePresentation(dt, (float)phase);
            float current = X(prediction);
            if (frame >= frameRate)
                Assert.InRange(current - previous, Speed * dt - 0.0001f, Speed * dt + 0.0001f);
            previous = current;
        }
    }

    [Fact]
    public void SingleArgumentOverloadStillAccumulatesTheWholeFrame()
    {
        ClientPrediction<State, Vector2> prediction = New();
        prediction.Predict(Move);

        // The tick landed at some unknown point in the frame, so the whole 10 ms renders past it: 0.3 of a tick.
        prediction.AdvancePresentation(0.01f);
        Assert.Equal(0.3f * TickStep, X(prediction), 5);
        prediction.AdvancePresentation(0.01f);
        Assert.Equal(0.6f * TickStep, X(prediction), 5);
        // Clamped at one tick, so a stalled tick stream holds on the target.
        prediction.AdvancePresentation(0.02f);
        Assert.Equal(TickStep, X(prediction), 5);
    }

    [Fact]
    public void PhaseOverloadPlacesTheClockOnTheResidualThenAccumulates()
    {
        ClientPrediction<State, Vector2> prediction = New();
        prediction.Predict(Move);

        prediction.AdvancePresentation(0.01f, 0.005f);
        Assert.Equal(0.15f * TickStep, X(prediction), 5);
        // No prediction since, so the next frame accumulates from the residual whatever phase is supplied.
        prediction.AdvancePresentation(0.01f, 0.03f);
        Assert.Equal(0.45f * TickStep, X(prediction), 5);
    }

    [Fact]
    public void PhaseBoundsAreInclusive()
    {
        ClientPrediction<State, Vector2> atStart = New();
        atStart.Predict(Move);
        atStart.AdvancePresentation(0.01f, 0f);
        Assert.Equal(Bits(0f), Bits(X(atStart)));

        ClientPrediction<State, Vector2> atEnd = New();
        atEnd.Predict(Move);
        atEnd.AdvancePresentation(0.01f, Tick);
        Assert.Equal(Bits(atEnd.PredictedState.Position.X), Bits(X(atEnd)));
    }

    [Fact]
    public void NonFinitePhaseIsRejectedIndependentlyOfTheConfiguredTick()
    {
        var prediction = new ClientPrediction<State, Vector2>(new Simulator(),
            PredictionSettings.Default with { TickSeconds = float.PositiveInfinity });

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            prediction.AdvancePresentation(0.01f, float.PositiveInfinity));
    }

    public static IEnumerable<object[]> InvalidPhases() => new[]
    {
        new object[] { float.NaN },
        new object[] { float.PositiveInfinity },
        new object[] { float.NegativeInfinity },
        new object[] { MathF.BitDecrement(0f) },
        new object[] { -0.001f },
        new object[] { MathF.BitIncrement(Tick) },
        new object[] { 1f },
    };

    [Theory]
    [MemberData(nameof(InvalidPhases))]
    public void InvalidPhaseThrowsBeforeChangingAnything(float phase)
    {
        ClientPrediction<State, Vector2> refused = NewWithCorrectionPending();
        ClientPrediction<State, Vector2> control = NewWithCorrectionPending();
        float before = X(refused);

        var thrown = Assert.Throws<ArgumentOutOfRangeException>(() => refused.AdvancePresentation(0.01f, phase));
        Assert.Equal("commandPhaseSeconds", thrown.ParamName);
        Assert.Throws<ArgumentOutOfRangeException>(() => refused.AdvancePresentation(0f, phase));
        Assert.Throws<ArgumentOutOfRangeException>(() => refused.AdvancePresentation(float.NaN, phase));
        Assert.Equal(Bits(before), Bits(X(refused)));

        // The clock, the pending prediction and the decay carriers are all untouched: from here the refused
        // prediction tracks a control that never saw the bad calls, frame for frame.
        for (int frame = 0; frame < 6; frame++)
        {
            refused.AdvancePresentation(0.01f, 0.004f);
            control.AdvancePresentation(0.01f, 0.004f);
            AssertSameRender(control, refused);
        }
    }

    [Fact]
    public void SingleArgumentFrameConsumesThePendingPrediction()
    {
        ClientPrediction<State, Vector2> prediction = New();
        prediction.Predict(Move);
        prediction.AdvancePresentation(0.01f);
        // The phase belongs to a tick this segment already advanced past, so it is ignored.
        prediction.AdvancePresentation(0.01f, 0f);
        Assert.Equal(0.6f * TickStep, X(prediction), 5);
    }

    [Fact]
    public void SingleArgumentFrameAlwaysAccumulatesAfterAPhaseFrame()
    {
        ClientPrediction<State, Vector2> prediction = New();
        prediction.Predict(Move);
        prediction.AdvancePresentation(0.01f, 0.002f);
        Assert.Equal(0.06f * TickStep, X(prediction), 5);
        prediction.AdvancePresentation(0.01f);
        Assert.Equal(0.36f * TickStep, X(prediction), 5);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-0.01f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void InvalidFrameTimeKeepsThePredictionPendingAndDecaysLikeTheSingleArgumentOverload(float dt)
    {
        ClientPrediction<State, Vector2> phased = NewWithCorrectionPending();
        ClientPrediction<State, Vector2> legacy = NewWithCorrectionPending();

        // Neither overload advances, applies a phase or consumes the pending prediction on an invalid frame, and the
        // correction decay sees the same zero step either way.
        phased.AdvancePresentation(dt, 0.02f);
        legacy.AdvancePresentation(dt);
        AssertSameRender(legacy, phased);

        // The next valid frame still places the clock on its own residual, for both.
        phased.AdvancePresentation(0.01f, 0.005f);
        legacy.AdvancePresentation(0.01f, 0.005f);
        AssertSameRender(legacy, phased);
        for (int frame = 0; frame < 6; frame++)
        {
            phased.AdvancePresentation(0.01f, 0f);
            legacy.AdvancePresentation(0.01f);
            AssertSameRender(legacy, phased);
        }
    }

    [Fact]
    public void InvalidFrameTimeLeavesThePhaseForTheNextValidFrame()
    {
        ClientPrediction<State, Vector2> prediction = New();
        prediction.Predict(Move);
        prediction.AdvancePresentation(0f, 0.02f);
        Assert.Equal(Bits(0f), Bits(X(prediction)));
        prediction.AdvancePresentation(0.01f, 0.005f);
        Assert.Equal(0.15f * TickStep, X(prediction), 5);
    }

    [Fact]
    public void StalledFramesIgnoreTheSuppliedPhaseAndNeverStepBack()
    {
        ClientPrediction<State, Vector2> prediction = New();
        prediction.Predict(Move);
        prediction.AdvancePresentation(0.01f, 0.02f);
        Assert.Equal(0.6f * TickStep, X(prediction), 5);

        // No tick fires. A phase of zero would roll the render back to the segment start if it were applied.
        prediction.AdvancePresentation(0.01f, 0f);
        Assert.Equal(0.9f * TickStep, X(prediction), 5);
        prediction.AdvancePresentation(0.01f, 0f);
        Assert.Equal(Bits(prediction.PredictedState.Position.X), Bits(X(prediction)));
        prediction.AdvancePresentation(0.01f, 0f);
        Assert.Equal(Bits(prediction.PredictedState.Position.X), Bits(X(prediction)));
    }

    [Fact]
    public void CatchUpPredictionsPresentTheLatestSegmentAtTheResidual()
    {
        ClientPrediction<State, Vector2> prediction = New();
        prediction.Predict(Move);
        prediction.Predict(Move);
        prediction.Predict(Move);
        prediction.AdvancePresentation(0.1f, 0.01f);
        Assert.Equal(2.3f * TickStep, X(prediction), 5);
    }

    [Fact]
    public void NonHardReconcileKeepsThePendingPredictionAndTheSameDecay()
    {
        ClientPrediction<State, Vector2> phased = NewWithCorrectionPending();
        ClientPrediction<State, Vector2> legacy = NewWithCorrectionPending();
        // A second glide between the prediction and its presentation. Nothing is acknowledged, so both pending
        // commands replay onto the moved basis.
        Assert.False(phased.Reconcile(2, new State(new Vector2(TickStep + 0.25f, 0f)), 0).HardSnapApplied);
        Assert.False(legacy.Reconcile(2, new State(new Vector2(TickStep + 0.25f, 0f)), 0).HardSnapApplied);

        phased.AdvancePresentation(0.01f, 0.004f);
        legacy.AdvancePresentation(0.01f);

        // Same frame time, so the two decays match exactly and only the inter-tick fraction differs: 0.12 against
        // 0.3 of the latest segment.
        Assert.Equal(-0.18f * TickStep, X(phased) - X(legacy), 5);
        Assert.Equal(0.88f * TickStep, phased.RemainingPresentationMovement.X, 5);
        Assert.Equal(0.7f * TickStep, legacy.RemainingPresentationMovement.X, 5);
    }

    [Theory]
    [InlineData(Cut.None, 0.25f)]
    [InlineData(Cut.Reset, 1f)]
    [InlineData(Cut.ResetForTransition, 1f)]
    [InlineData(Cut.Reseed, 1f)]
    [InlineData(Cut.HardSnap, 1f)]
    public void CutsClearThePendingPrediction(Cut cut, float expectedHeading)
    {
        var prediction = new ClientPrediction<YawState, Vector2>(new YawSimulator(),
            PredictionSettings.Default with { TickSeconds = Tick, InterpolateYaw = true });
        prediction.Reset(new YawState(Vector2.Zero, 0f));
        prediction.Predict(Move);

        Vector2 basis = new(TickStep, 0f);
        switch (cut)
        {
            case Cut.Reset: prediction.Reset(new YawState(basis, 0f)); break;
            case Cut.ResetForTransition: prediction.ResetForTransition(new YawState(basis, 0f)); break;
            case Cut.Reseed: prediction.Reseed(new YawState(basis, 0f)); break;
            case Cut.HardSnap:
                basis = new Vector2(500f, 0f);
                Assert.True(prediction.Reconcile(1, new YawState(basis, 0f), 0).HardSnapApplied);
                break;
        }

        // A non-hard reconcile turns only the heading target, which leaves the heading's interpolation start behind.
        // The rendered heading then reads the inter-tick clock directly.
        Assert.False(prediction.Reconcile(2, new YawState(basis, 1f), 0).HardSnapApplied);
        prediction.AdvancePresentation(0.01f, Tick / 4f);

        // A pending prediction places the clock a quarter tick in. After a cut nothing is pending, so the frame
        // accumulates onto a clock that already sits at the tick and renders the target heading.
        Assert.Equal(expectedHeading, prediction.RenderedState.Heading, 5);
    }
}
