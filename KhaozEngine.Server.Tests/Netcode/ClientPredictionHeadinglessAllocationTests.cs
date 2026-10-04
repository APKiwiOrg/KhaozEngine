using System.Numerics;
using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

/// <summary>
/// A state that keeps the default heading members must not pay for them. Reading a default interface member on a
/// struct through the generic constraint boxes it, so with <c>InterpolateYaw</c> off neither <c>Predict</c>, a steady
/// <c>Reconcile</c> nor <c>RenderedState</c> may read <c>Yaw</c>. Joins <c>AllocSensitive</c> because it reads
/// <see cref="System.GC.GetAllocatedBytesForCurrentThread"/>.
/// </summary>
[Collection("AllocSensitive")]
public class ClientPredictionHeadinglessAllocationTests
{
    private const float Tick = 1f / 30f;

    // Implements every member Predict, AdvancePresentation, a non-snap Reconcile and RenderedState read, and none of
    // the heading members.
    private readonly record struct PlanarState(Vector2 Position) : IPredictedState<PlanarState>
    {
        Vector2 IPredictedState<PlanarState>.PredictionTarget => Position;
        float IPredictedState<PlanarState>.Vertical => 0f;
        float IPredictedState<PlanarState>.StepDeltaY => 0f;
        Vector2 IPredictedState<PlanarState>.FrameAnchor => Vector2.Zero;
        uint IPredictedState<PlanarState>.TeleportEpoch => 0;
        public PlanarState WithPosition(Vector2 position) => this with { Position = position };
        PlanarState IPredictedState<PlanarState>.WithRenderState(Vector2 position, float vertical)
            => this with { Position = position };
    }

    private readonly record struct PlanarCmd(Vector2 Velocity);

    private sealed class PlanarSimulator : ITickSimulator<PlanarState, PlanarCmd>
    {
        public PlanarState Step(in PlanarState state, in PlanarCmd command, float dt)
            => state with { Position = state.Position + command.Velocity * dt };
    }

    [Fact]
    public void PredictReconcileAndRenderAllocateNothingWithoutHeadingInterpolation()
    {
        // A small pending bound so the warm-up fills the command list to its steady capacity.
        var settings = PredictionSettings.Default with { TickSeconds = Tick, MaxPendingCommands = 4 };
        var p = new ClientPrediction<PlanarState, PlanarCmd>(new PlanarSimulator(), settings);
        p.Reset(new PlanarState(Vector2.Zero));

        var command = new PlanarCmd(new Vector2(1f, 0f));
        int tick = 0;
        for (int i = 0; i < 16; i++)
        {
            p.Predict(command);
            p.AdvancePresentation(Tick / 2f);
            _ = p.RenderedState;
        }

        // A basis equal to the prediction that acknowledges every predicted command takes the steady non-snap branch.
        bool snapped = false;
        float x = 0f;
        AllocAssert.NoPerCallAllocation("Predict, Reconcile, AdvancePresentation and RenderedState without InterpolateYaw", () =>
        {
            for (int i = 0; i < 100; i++)
            {
                int seq = p.Predict(command);
                p.AdvancePresentation(Tick / 2f);
                snapped |= p.Reconcile(++tick, p.PredictedState, seq).HardSnapApplied;
                x = p.RenderedState.Position.X;
            }
        });

        Assert.False(snapped);
        Assert.True(x > 0f);
    }
}
