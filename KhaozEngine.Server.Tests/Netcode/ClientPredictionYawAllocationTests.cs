using System.Numerics;
using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

/// <summary>
/// The interpolated heading is read every frame, so <c>AdvancePresentation</c> plus <c>RenderedState</c> with
/// <c>InterpolateYaw</c> set must allocate nothing. Joins <c>AllocSensitive</c> because it reads
/// <see cref="System.GC.GetAllocatedBytesForCurrentThread"/>.
/// </summary>
[Collection("AllocSensitive")]
public class ClientPredictionYawAllocationTests
{
    private const float Tick = 1f / 30f;

    // Implements every member RenderedState reads. A default interface member called on a struct through the generic
    // constraint boxes it, which would charge this fact 32 bytes per read that the heading path does not cause.
    private readonly record struct YawState(Vector2 Position, float Heading) : IPredictedState<YawState>
    {
        Vector2 IPredictedState<YawState>.PredictionTarget => Position;
        float IPredictedState<YawState>.Vertical => 0f;
        bool IPredictedState<YawState>.HasYaw => true;
        float IPredictedState<YawState>.Yaw => Heading;
        public YawState WithPosition(Vector2 position) => this with { Position = position };
        YawState IPredictedState<YawState>.WithRenderState(Vector2 position, float vertical, float yaw)
            => this with { Position = position, Heading = yaw };
    }

    private readonly record struct YawCmd(Vector2 Velocity, float Heading);

    private sealed class YawSimulator : ITickSimulator<YawState, YawCmd>
    {
        public YawState Step(in YawState state, in YawCmd command, float dt)
            => state with { Position = state.Position + command.Velocity * dt, Heading = command.Heading };
    }

    [Fact]
    public void InterpolatedHeadingAllocatesNothing()
    {
        var settings = PredictionSettings.Default with { TickSeconds = Tick, InterpolateYaw = true };
        var p = new ClientPrediction<YawState, YawCmd>(new YawSimulator(), settings);
        p.Reset(new YawState(Vector2.Zero, 0f));

        // Warm up: JIT both members on the interpolated-heading path, then start a fresh turn to measure inside.
        for (int i = 0; i < 8; i++)
        {
            p.Predict(new YawCmd(new Vector2(1f, 0f), 0.1f * i));
            p.AdvancePresentation(Tick / 2f);
            _ = p.RenderedState;
        }
        p.Predict(new YawCmd(new Vector2(1f, 0f), 1.5f));

        // Each pass stays inside the tick (frac below one), so every read takes the eased-heading branch.
        float heading = 0f;
        AllocAssert.NoPerCallAllocation("AdvancePresentation plus RenderedState with InterpolateYaw", () =>
        {
            for (int i = 0; i < 100; i++)
            {
                p.AdvancePresentation(Tick / 1000f);
                heading = p.RenderedState.Heading;
            }
        });

        Assert.InRange(heading, 0.7f, 1.5f - 1e-4f);
    }
}
