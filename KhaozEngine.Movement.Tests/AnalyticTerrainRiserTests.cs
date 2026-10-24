using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Movement;

public class AnalyticTerrainRiserTests(ITestOutputHelper output)
{
    static MoveTuning Tuning => OrdinaryContactScene.DefaultTuning with
    {
        StepHeight = 0.6f,
        RunSpeed = 6f,
        MaxStepClimbSpeed = 3.5f,
        CapsuleHalfHeight = 0.75f,
        CapsuleRadius = 0.25f,
        MaxSlopeRadians = 20f * MathF.PI / 180f,
    };

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public void HalfMetreAnalyticRiserCrossesAndSettlesPaced(int hz)
    {
        using var scene = Riser(0.5f);
        MoveState final = RunOneSecond(scene, hz);

        Assert.True(final.Position.X > 2f, $"Final centre: {final.Position}");
        Assert.InRange(final.Position.Y - 0.75f,
            0.5f - ShellMotion.ContactSkin, 0.5f + ShellMotion.ContactSkin);
        Assert.True(final.Grounded);
    }

    [Theory]
    [InlineData(0.6f, 30)]
    [InlineData(0.6f, 60)]
    [InlineData(0.61f, 30)]
    [InlineData(0.61f, 60)]
    public void AnalyticRiserAtStepHeightIsAdmittedAndAboveIsBlocked(float rise, int hz)
    {
        using var scene = Riser(rise);
        MoveState final = RunOneSecond(scene, hz);

        if (rise == 0.6f)
        {
            Assert.True(final.Position.X > 2f, $"Final centre: {final.Position}");
            Assert.InRange(final.Position.Y - 0.75f,
                0.6f - ShellMotion.ContactSkin, 0.6f + ShellMotion.ContactSkin);
        }
        else
        {
            Assert.InRange(final.Position.X, 0.5f, MathF.BitDecrement(1f));
            Assert.InRange(final.Position.Y - 0.75f, -ShellMotion.ContactSkin, ShellMotion.ContactSkin);
        }
        Assert.True(final.Grounded);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public void SteepContinuousTerrainRemainsUnclimbable(int hz)
    {
        float grade = MathF.Tan(60f * MathF.PI / 180f);
        Vector3 normal = Vector3.Normalize(new Vector3(-grade, 1, 0));
        using var scene = new OrdinaryContactScene(tuning: Tuning,
            height: (x, _) => grade * x, normal: (_, _) => normal,
            extent: new(new(-20, -40, -20), new(20, 40, 20), -35, 35),
            boundary: MovementBoundary.Rectangle(-20, -20, 20, 20));

        RunOneSecond(scene, hz, steep: true);
    }

    static OrdinaryContactScene Riser(float rise) => new(tuning: Tuning,
        height: (x, _) => x < 1 ? 0 : rise, normal: (_, _) => Vector3.UnitY,
        extent: new(new(-20, -4, -20), new(20, 4, 20), 0, rise),
        boundary: MovementBoundary.Rectangle(-20, -20, 20, 20));

    MoveState RunOneSecond(OrdinaryContactScene scene, int hz, bool steep = false)
    {
        float dt = 1f / hz;
        MoveState direct = new() { Position = new(0, 0.75f, 0), Grounded = true };
        FramedMovementState ordinary = scene.State(new(0, 0.75f, 0));
        for (int tick = 0; tick < hz; tick++)
        {
            MoveState previous = direct;
            direct = CharacterMovement.StepTowards(direct, Vector2.UnitX, true, dt, scene.Height,
                scene.Tuning, scene.Normal, scene.Physics,
                (x, z) => new(Math.Clamp(x, -20f, 20f), Math.Clamp(z, -20f, 20f)));
            using var lease = scene.Acquire();
            // Each fresh null-hint lease must authorize its canonical selection before selected queries.
            Assert.Equal(MovementAvailability.Known, lease.RebuildSelection(ordinary, out var selection));
            ordinary = new(ordinary.State, ordinary.Frame, selection);
            MovementStepResult result = scene.Binding.StepTowards(ordinary, Vector2.UnitX, true, dt, lease);
            ordinary = result.State;
            output.WriteLine($"{hz} Hz tick {tick + 1}: {previous.Position} -> {direct.Position}, " +
                $"grounded {direct.Grounded}, vertical velocity {direct.VerticalVelocity:R}, " +
                $"step rise {direct.StepDeltaY:R}, ordinary {ordinary.State.Position}, outcome {result.Outcome}");

            Assert.True(result.Outcome is MovementStepOutcome.Advanced or MovementStepOutcome.Blocked,
                $"Tick {tick + 1} did not produce an accepted state: {result.Outcome}.");
            Assert.Equal(direct, ordinary.State);
            float positiveFeetRise = MathF.Max(0, direct.Position.Y - previous.Position.Y);
            Assert.True(positiveFeetRise <= 3.5f * dt + ShellMotion.ContactSkin,
                $"Tick {tick + 1} rose {positiveFeetRise:R} at {hz} Hz.");
            if (steep)
            {
                Assert.True(direct.Position.X <= ShellMotion.ContactSkin,
                    $"Tick {tick + 1} advanced uphill to {direct.Position}.");
                Assert.True(direct.Position.Y - 0.75f <= ShellMotion.ContactSkin,
                    $"Tick {tick + 1} climbed to {direct.Position}.");
            }
            else Assert.True(direct.Grounded, $"Tick {tick + 1} lost ground at {direct.Position}.");
        }
        return direct;
    }
}
