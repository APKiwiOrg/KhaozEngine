using System;
using System.Numerics;
using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

public sealed class ClientPredictionStationaryPresentationTests
{
    private const float Tick = 1f / 30f;
    private static readonly float Residual = BitConverter.Int32BitsToSingle(0x3C888883);
    private static readonly Vector2 StationaryPoint = new(195.5f, -163.5f);

    private readonly record struct State(Vector2 Position) : IPredictedState<State>
    {
        public Vector2 PredictionTarget => Position;
        public State WithPosition(Vector2 position) => this with { Position = position };
    }

    private sealed class TargetSimulator : ITickSimulator<State, Vector2>
    {
        public State Step(in State state, in Vector2 command, float dt) => state.WithPosition(command);
    }

    private static ClientPrediction<State, Vector2> New(Vector2 position)
    {
        var prediction = new ClientPrediction<State, Vector2>(new TargetSimulator(),
            PredictionSettings.Default with
            {
                TickSeconds = Tick,
                HardSnapDistance = 1024f,
                CorrectionDeadZone = 0f
            });
        prediction.Reset(new State(position));
        return prediction;
    }

    private static void PresentResidual(ClientPrediction<State, Vector2> prediction, Vector2 target)
    {
        prediction.Predict(target);
        prediction.AdvancePresentation(1f / 100f, Residual);
    }

    private static void AssertBits(float expected, float actual) =>
        Assert.Equal(BitConverter.SingleToInt32Bits(expected), BitConverter.SingleToInt32Bits(actual));

    private static void AssertBits(Vector2 expected, Vector2 actual)
    {
        AssertBits(expected.X, actual.X);
        AssertBits(expected.Y, actual.Y);
    }

    [Theory]
    [InlineData(195.5f, -163.5f)]
    [InlineData(-195.5f, 163.5f)]
    [InlineData(float.MaxValue, -float.MaxValue)]
    public void StationaryFiniteEndpointsRemainExactAtAResidualPhase(float x, float y)
    {
        var point = new Vector2(x, y);
        ClientPrediction<State, Vector2> prediction = New(point);

        PresentResidual(prediction, point);

        AssertBits(point, prediction.PredictedState.Position);
        AssertBits(point, prediction.RenderedState.Position);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(100)]
    [InlineData(144)]
    public void StationaryPresentationRemainsExactAcrossCommandCadences(int renderHz)
    {
        ClientPrediction<State, Vector2> prediction = New(StationaryPoint);
        float dt = 1f / renderHz;
        double phase = 0;

        // A finite half-second simulation, including fractional render-to-command phases.
        for (int frame = 0; frame < renderHz / 2; frame++)
        {
            phase += dt;
            while (phase >= Tick)
            {
                prediction.Predict(StationaryPoint);
                phase -= Tick;
            }
            prediction.AdvancePresentation(dt, (float)phase);

            AssertBits(StationaryPoint, prediction.RenderedState.Position);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnUnchangedCoordinateStaysExactBesideRealMovement(bool fixedX)
    {
        Vector2 start = fixedX ? new(195.5f, 10f) : new(10f, 195.5f);
        Vector2 end = fixedX ? new(195.5f, 11f) : new(11f, 195.5f);
        ClientPrediction<State, Vector2> prediction = New(start);

        PresentResidual(prediction, end);

        Vector2 actual = prediction.RenderedState.Position;
        Vector2 priorInterpolation = Vector2.Lerp(start, end, Residual / Tick);
        AssertBits(195.5f, fixedX ? actual.X : actual.Y);
        AssertBits(fixedX ? priorInterpolation.Y : priorInterpolation.X, fixedX ? actual.Y : actual.X);
        Assert.InRange(fixedX ? actual.Y : actual.X, 10.1f, 10.9f);
    }

    [Theory]
    [InlineData(195.5f, 128f)]
    [InlineData(128f, 195.5f)]
    public void ReconciliationKeepsTheStationaryVisiblePointAndItsGenuineCorrection(float initialX, float basisX)
    {
        var initial = new Vector2(initialX, -163.5f);
        var basis = new Vector2(basisX, -163.5f);
        ClientPrediction<State, Vector2> prediction = New(initial);
        PresentResidual(prediction, initial);

        ReconciliationResult result = prediction.Reconcile(1, new State(basis), 0);

        Assert.False(result.HardSnapApplied);
        AssertBits(initial, prediction.RenderedState.Position);
        Assert.NotEqual(prediction.PredictedState.Position, prediction.RenderedState.Position);
        prediction.AdvancePresentation(1f / 60f);
        float after = prediction.RenderedState.Position.X;
        Assert.True((after - initialX) * (basisX - initialX) > 0f);
        Assert.True(MathF.Abs(after - basisX) < MathF.Abs(initialX - basisX));
    }

    [Fact]
    public void QuietReseedDoesNotTurnInterpolationNoiseIntoACorrection()
    {
        ClientPrediction<State, Vector2> prediction = New(StationaryPoint);
        PresentResidual(prediction, StationaryPoint);

        prediction.Reseed(new State(StationaryPoint));

        AssertBits(StationaryPoint, prediction.RenderedState.Position);
        PresentResidual(prediction, StationaryPoint);
        AssertBits(StationaryPoint, prediction.RenderedState.Position);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1f)]
    public void UnequalEndpointsKeepTheFrameworkEndpointGuarantee(float amount)
    {
        var start = new Vector2(1e10f, -1e10f);
        var end = new Vector2(1f, -1f);
        ClientPrediction<State, Vector2> prediction = New(start);
        prediction.Predict(end);

        prediction.AdvancePresentation(1f / 100f, amount * Tick);

        AssertBits(amount == 0f ? start : end, prediction.RenderedState.Position);
    }

    [Theory]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(float.NaN)]
    public void NonfiniteCoordinatesKeepTheFrameworkInterpolationBehavior(float value)
    {
        var point = new Vector2(value, 0f);
        ClientPrediction<State, Vector2> prediction = New(point);
        prediction.Predict(point);

        prediction.AdvancePresentation(1f / 100f, 0f);

        Assert.True(float.IsNaN(prediction.RenderedState.Position.X));
        AssertBits(0f, prediction.RenderedState.Position.Y);
    }

    [Fact]
    public void NonfiniteInterpolationWeightKeepsTheFrameworkPropagation()
    {
        var prediction = new ClientPrediction<State, Vector2>(new TargetSimulator(),
            PredictionSettings.Default with { TickSeconds = float.PositiveInfinity });
        prediction.Reset(new State(StationaryPoint));

        Vector2 rendered = prediction.RenderedState.Position;

        Assert.True(float.IsNaN(rendered.X));
        Assert.True(float.IsNaN(rendered.Y));
    }
}
