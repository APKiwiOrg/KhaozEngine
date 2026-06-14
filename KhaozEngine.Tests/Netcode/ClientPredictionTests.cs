using KhaozEngine.Netcode;
using Microsoft.Xna.Framework;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

public class ClientPredictionTests
{
    // Command = a velocity (units/sec). Deterministic: position += command * dt.
    private readonly record struct FakeState(Vector2 Position) : IPredictedState<FakeState>
    {
        public FakeState WithPosition(Vector2 position) => this with { Position = position };
    }

    private sealed class MoveSimulator : ITickSimulator<FakeState, Vector2>
    {
        public FakeState Step(in FakeState state, in Vector2 command, float dt)
            => state.WithPosition(state.Position + command * dt);
    }

    private static ClientPrediction<FakeState, Vector2> NewPrediction(PredictionSettings? settings = null)
    {
        var p = new ClientPrediction<FakeState, Vector2>(new MoveSimulator(), settings);
        p.Reset(new FakeState(Vector2.Zero));
        return p;
    }

    [Fact]
    public void Predict_AssignsIncreasingSeq_AndAdvancesState()
    {
        var p = NewPrediction();
        int s0 = p.Predict(new Vector2(60f, 0f)); // 60 * (1/60) = 1 unit
        int s1 = p.Predict(new Vector2(60f, 0f));
        Assert.Equal(0, s0);
        Assert.Equal(1, s1);
        Assert.Equal(2f, p.PredictedState.Position.X, 3);
    }

    [Fact]
    public void Reconcile_MatchingBasis_ZeroErrorNoOffset()
    {
        var p = NewPrediction();
        p.Predict(new Vector2(60f, 0f));
        p.Predict(new Vector2(60f, 0f)); // predicted X = 2
        var r = p.Reconcile(authoritativeTick: 1, new FakeState(new Vector2(2f, 0f)), lastAcknowledgedSeq: 1);
        Assert.Equal(0f, r.PositionError, 3);
        Assert.False(r.HardSnapApplied);
        Assert.Equal(p.PredictedState.Position.X, p.RenderedState.Position.X, 3);
    }

    [Fact]
    public void Reconcile_Misprediction_SetsOffset_ThatDecaysToZero()
    {
        var p = NewPrediction();
        p.Predict(new Vector2(600f, 0f)); // seq 0, predicted X = 10
        // basis shifted +5, seq 0 still unacked -> replay puts predicted at 15, rendered stays at 10
        var r = p.Reconcile(1, new FakeState(new Vector2(5f, 0f)), lastAcknowledgedSeq: -1);
        Assert.Equal(5f, r.PositionError, 3);
        Assert.False(r.HardSnapApplied);
        Assert.Equal(15f, p.PredictedState.Position.X, 3);
        Assert.Equal(10f, p.RenderedState.Position.X, 3);
        for (int i = 0; i < 300; i++) p.AdvancePresentation(1f / 60f);
        Assert.Equal(p.PredictedState.Position.X, p.RenderedState.Position.X, 3);
    }

    [Fact]
    public void Reconcile_LargeError_HardSnaps_NoOffset()
    {
        var p = NewPrediction();
        p.Predict(new Vector2(600f, 0f)); // predicted X = 10
        var r = p.Reconcile(1, new FakeState(new Vector2(200f, 0f)), lastAcknowledgedSeq: -1);
        Assert.True(r.HardSnapApplied);
        Assert.Equal(210f, p.PredictedState.Position.X, 3);
        Assert.Equal(210f, p.RenderedState.Position.X, 3); // snapped, no smoothing
    }

    [Fact]
    public void Reconcile_AcknowledgedCommands_ArePruned()
    {
        var p = NewPrediction();
        p.Predict(new Vector2(60f, 0f)); // 0
        p.Predict(new Vector2(60f, 0f)); // 1
        p.Predict(new Vector2(60f, 0f)); // 2
        p.Reconcile(1, new FakeState(Vector2.Zero), lastAcknowledgedSeq: 2); // all acked, none replayed
        Assert.Equal(0f, p.PredictedState.Position.X, 3);
        Assert.Equal(3, p.Predict(new Vector2(60f, 0f))); // next seq continues
    }

    [Fact]
    public void MaxPendingCommands_DropsOldest()
    {
        var p = NewPrediction(PredictionSettings.Default with { MaxPendingCommands = 4 });
        for (int i = 0; i < 6; i++) p.Predict(new Vector2(60f, 0f)); // 6 predicted -> X = 6
        // nothing acked; only the last 4 commands remain to replay from origin -> X = 4
        p.Reconcile(1, new FakeState(Vector2.Zero), lastAcknowledgedSeq: -1);
        Assert.Equal(4f, p.PredictedState.Position.X, 3);
    }

    [Fact]
    public void Reconcile_ErrorAtDeadZone_IsIgnored()
    {
        var p = NewPrediction();
        p.Predict(new Vector2(60f, 0f)); // predicted X = 1
        // basis +1.5, seq 0 unacked -> replay predicted = 2.5; prevRendered = 1; |error| = 1.5 == dead-zone
        var r = p.Reconcile(1, new FakeState(new Vector2(1.5f, 0f)), lastAcknowledgedSeq: -1);
        Assert.Equal(1.5f, r.PositionError, 3);
        Assert.False(r.HardSnapApplied);
        Assert.Equal(p.PredictedState.Position.X, p.RenderedState.Position.X, 3); // offset ignored at boundary
    }

    [Fact]
    public void Reset_ClearsPendingAndSeq()
    {
        var p = NewPrediction();
        p.Predict(new Vector2(60f, 0f));
        p.Predict(new Vector2(60f, 0f));
        p.Reset(new FakeState(new Vector2(99f, 0f)));
        Assert.Equal(99f, p.PredictedState.Position.X, 3);
        Assert.Equal(0, p.Predict(new Vector2(60f, 0f))); // seq restarts at 0
        // only the one post-reset command should replay (pre-reset commands were cleared)
        p.Reconcile(1, new FakeState(Vector2.Zero), lastAcknowledgedSeq: -1);
        Assert.Equal(1f, p.PredictedState.Position.X, 3);
    }
}
