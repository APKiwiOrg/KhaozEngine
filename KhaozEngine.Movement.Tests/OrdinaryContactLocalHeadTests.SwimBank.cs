using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public partial class OrdinaryContactLocalHeadTests
{
    const float BankSurface = -0.02f;
    const float BankDt = 1f / 30;

    static float BankFloor(float x) => Math.Clamp(-2f + 0.5f * x, -2f, 0f);

    static Vector3 BankNormal(float x) =>
        x > 0 && x < 4 ? Vector3.Normalize(new Vector3(-0.5f, 1, 0)) : Vector3.UnitY;

    static MoveTuning BankTuning => OrdinaryContactScene.DefaultTuning with
    {
        CapsuleRadius = 0.3f,
        CapsuleHalfHeight = 0.75f,
        MaxSlopeRadians = MathF.PI / 4,
        StepHeight = 0.4f,
        FootRadiusFraction = 0.5f,
        SwimEnterDepthFraction = 0.65f,
        SwimExitDepthFraction = 0.55f,
        SwimSurfaceSubmersionFraction = 0.6f,
        SwimBuoyancyStiffness = 8,
        SwimSpeed = 3,
        Water = new(WaterTraversalMode.SurfaceSwimmer),
    };

    // Rest is approached from below at ulps, from above at ulps, from 1 mm above, and from 0.3 m below with a
    // buoyant rise that lasts the whole approach. Footing must not depend on the sign buoyancy settles with.
    [Theory]
    [InlineData(-2e-7f, 6e-7f)]
    [InlineData(2e-7f, -6e-7f)]
    [InlineData(1e-3f, 0f)]
    [InlineData(-0.3f, 0f)]
    public void SurfaceSwimmerLandsOnARisingBankWhateverItsBuoyantApproach(float offset, float vertical)
    {
        MoveTuning tuning = BankTuning;
        using var scene = new OrdinaryContactScene(tuning, (x, _) => BankFloor(x), (x, _) => BankNormal(x),
            new(new(-20, -4, -20), new(20, 4, 20), -2, 0), MovementWaterField.Level("world", BankSurface, -4));
        double rest = BankSurface + (double)tuning.CapsuleHalfHeight * (1 - 2d * tuning.SwimSurfaceSubmersionFraction);
        var state = new FramedMovementState(new MoveState
        {
            Position = new(0, (float)rest + offset, 0),
            VerticalVelocity = vertical,
            Swimming = true,
            WaterExcursion = WaterExcursionState.Surface,
        }, scene.Frame, null);
        // Footing returns where the bed reaches the swim exit line, at X 2.31. Swimming at SwimSpeed from X 0 covers
        // 0.1 m a tick, so the 24th tick is the first whose end axis passes the line, and no tick ends within a
        // rounding of it. The bed there lies within StepHeight above the feet of every approach tested, so the
        // landing is due by that tick.
        float exitLine = BankSurface - 2 * tuning.CapsuleHalfHeight * tuning.SwimExitDepthFraction;
        float exitAxis = (exitLine + 2) / 0.5f;
        int bound = (int)MathF.Ceiling(exitAxis / (tuning.SwimSpeed * BankDt));
        Assert.Equal(24, bound);
        using var lease = scene.Acquire();
        int tick = 0;
        while (!state.State.Grounded && tick < bound)
        {
            MovementStepResult result = scene.Binding.StepTowards(state, Vector2.UnitX, false, BankDt, lease);
            Assert.True(result.Outcome == MovementStepOutcome.Advanced,
                $"tick {tick}, pose {state.State.Position}, outcome {result.Outcome}");
            state = result.State;
            tick++;
        }
        MoveState body = state.State;
        Assert.True(body.Grounded, $"after {tick} ticks, pose {body.Position}, swimming {body.Swimming}");
        Assert.False(body.Swimming);
        Assert.Equal(WaterExcursionState.None, body.WaterExcursion);
        Assert.NotNull(state.Selection!.Value.Support);
        Assert.InRange(body.Position.X, exitAxis - tuning.FootRadiusFraction * tuning.CapsuleRadius, 4);
        float feet = body.Position.Y - tuning.CapsuleHalfHeight;
        // A landing paces its rise by MaxStepClimbSpeed, so its lag stays under StepHeight less the tick's budget.
        float lag = tuning.StepHeight - tuning.MaxStepClimbSpeed * BankDt;
        Assert.InRange(feet, BankFloor(body.Position.X) - lag, BankFloor(body.Position.X) + 0.01f);
    }
}
