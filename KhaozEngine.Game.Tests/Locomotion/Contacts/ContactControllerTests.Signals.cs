using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Contacts.FootSupportScenes;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public partial class ContactControllerTests
{
    public static IEnumerable<object[]> SignalRows()
    {
        foreach (SceneVariant variant in new[] { SceneVariant.Box, SceneVariant.Mesh })
            foreach (int hz in new[] { 30, 60 })
                foreach (bool run in new[] { false, true }) yield return [variant, hz, run];
    }

    static void AssertNoSignals(in MoveState state)
    {
        Assert.Equal(0f, state.StepDeltaY);
        AssertNoClimb(state);
    }

    static void AssertNoClimb(in MoveState state)
    {
        Assert.Equal(0f, state.ClimbRate);
        Assert.Equal(0f, state.ClimbRateEwma);
    }

    static MoveState WithSignals(in MoveState state) => state with
    {
        StepDeltaY = 0.18f,
        ClimbRate = 1,
        ClimbRateEwma = 2,
    };

    // The authored riser is 0.18 m. Pacing can split its event over at most ceil(height / budget) + 1 ticks.
    public static IEnumerable<object[]> DoorstepSignalRows()
    {
        foreach (object[] row in SignalRows())
            foreach (int direction in new[] { 1, -1 }) yield return [row[0], row[1], row[2], direction];
    }

    [Theory]
    [MemberData(nameof(DoorstepSignalRows))]
    public void DoorstepReportsItsHeightAsAStep(SceneVariant variant, int hz, bool run, int direction)
    {
        using var g = new Ground(Doorstep(variant, 0.18f), Abyss);
        float dt = 1f / hz;
        MoveState s = Standing(new Vector3(direction > 0 ? -0.5f : 0.5f, direction > 0 ? 0 : 0.18f, 0));
        double sum = 0;
        int events = 0;
        for (int tick = 0; tick < 10; tick++)
        {
            bool moving = direction * Feet(s).X < 1;
            s = g.Step(s, moving ? direction * Vector2.UnitX : Vector2.Zero, moving ? 1 : 0,
                false, Tuning, dt, run);
            Assert.True(s.Grounded, Show(s));
            AssertNoClimb(s);
            Assert.True(direction * s.StepDeltaY >= 0, Show(s));
            if (s.StepDeltaY != 0) events++;
            sum += s.StepDeltaY;
        }
        AssertNear(direction * 0.18f, sum, Skin, $"direction {direction}, {Show(s)}");
        AssertNear(direction > 0 ? 0.18f : 0, Feet(s).Y, Skin, Show(s));
        int maxTicks = direction > 0 ? (int)Math.Ceiling(0.18f / ((double)Tuning.MaxStepClimbSpeed * dt)) + 1 : 1;
        Assert.InRange(events, 1, maxTicks);
    }

    [Theory]
    [InlineData(30, 0.18f, 0f)]
    [InlineData(60, 0.18f, 0f)]
    [InlineData(30, 0f, 1f)]
    [InlineData(60, 0f, 1f)]
    public void CarriedStepLagReportsItsAppliedRise(int hz, float step, float climb)
    {
        using var g = new Ground(null, (_, _) => 0.18f);
        MoveState state = Standing(new Vector3(0, 0.03f, 0)) with { StepDeltaY = step, ClimbRate = climb };
        float dt = 1f / hz;

        MoveState result = g.Step(state, Vector2.Zero, 0, false, Tuning, dt);

        AssertNear((double)Tuning.MaxStepClimbSpeed * dt, result.StepDeltaY, Skin, Show(result));
        AssertNoClimb(result);
    }

    public static IEnumerable<object[]> SmoothSignalRows()
    {
        foreach (object[] row in SignalRows())
            foreach (string ground in new[] { "floor", "ramp", "plane", "hill" })
                yield return [row[0], row[1], row[2], ground];
    }

    [Theory]
    [MemberData(nameof(SmoothSignalRows))]
    public void SmoothGroundReportsNoSignals(SceneVariant variant, int hz, bool run, string ground)
    {
        float grade = MathF.Tan(Radians(20));
        Vector3 planeNormal = Vector3.Normalize(new Vector3(-grade, 1, 0));
        static float Hill(float x, float z) => 0.2f * MathF.Sin(x) * MathF.Cos(z);
        static Vector3 HillNormal(float x, float z) => Vector3.Normalize(new Vector3(
            -0.2f * MathF.Cos(x) * MathF.Cos(z), 1, 0.2f * MathF.Sin(x) * MathF.Sin(z)));
        FootSupportScene? scene = ground switch
        {
            "floor" => new FootSupportScene(variant).Flat("floor", -20, 20, -5, 5, 0),
            "ramp" => new FootSupportScene(variant).Slab("ramp", Vector3.Zero, Radians(20), 20, 5),
            _ => null,
        };
        Func<float, float, float> height = ground switch
        {
            "plane" => (x, _) => grade * x,
            "hill" => Hill,
            _ => Abyss,
        };
        Func<float, float, Vector3>? normal = ground switch
        {
            "plane" => (_, _) => planeNormal,
            "hill" => HillNormal,
            _ => null,
        };
        using var g = new Ground(scene, height, normal);
        var feet = new Vector3(0.3f, 0, 0.4f);
        feet.Y = scene is null ? height(feet.X, feet.Z) : (float)scene.TopHeightAt(ground, feet.X, feet.Z);
        foreach (bool pacedLag in ground == "hill" ? new[] { false, true } : new[] { false })
        {
            MoveTuning tuning = pacedLag ? Tuning with { MaxStepClimbSpeed = 0.001f } : Tuning;
            MoveState s = WithSignals(Standing(feet));
            for (int tick = 0; tick < hz; tick++)
            {
                s = g.Step(s, Vector2.UnitX, 1, false, tuning, 1f / hz, run);
                Assert.True(s.Grounded, $"{ground}, paced lag {pacedLag}, tick {tick}: {Show(s)}");
                AssertNoSignals(s);
            }
        }
    }

    [Theory]
    [MemberData(nameof(SignalRows))]
    public void AirAndSlidesReportNoSignals(SceneVariant variant, int hz, bool run)
    {
        float dt = 1f / hz;
        using (var stairs = new Ground(Staircase(variant, 4, 0.18f, 0.3f), Abyss))
        {
            MoveState s = stairs.Step(Standing(new Vector3(-0.5f, 0, 0)), Vector2.Zero, 0, true, Tuning, dt);
            AssertNoSignals(s);
            bool airborne = false, landed = false;
            for (int tick = 0; tick < 2 * hz && !landed; tick++)
            {
                s = stairs.Step(s, Feet(s).X < 1.2f ? Vector2.UnitX : Vector2.Zero,
                    Feet(s).X < 1.2f ? 1 : 0, false, Tuning, dt, run);
                AssertNoSignals(s);
                airborne |= !s.Grounded;
                landed = s.Grounded;
            }
            Assert.True(airborne && landed, Show(s));
            AssertNear(4 * 0.18f, Feet(s).Y, Skin, Show(s));
        }
        using (var doorstep = new Ground(Doorstep(variant, 0.18f), Abyss))
        {
            MoveState s = WithSignals(Falling(new Vector3(0.5f, 1.18f, 0)));
            for (int tick = 0; tick < hz && !s.Grounded; tick++)
            {
                s = doorstep.Step(s, Vector2.Zero, 0, false, Tuning, dt, run);
                AssertNoSignals(s);
            }
            Assert.True(s.Grounded, Show(s));
            Assert.True(s.SupportGranted, Show(s));
            Assert.True(s.LandingImpactSpeed > 0, Show(s));
            AssertNear(0.18f, Feet(s).Y, Skin, Show(s));
        }
        float grade = MathF.Tan(Radians(60));
        Vector3 normal = Vector3.Normalize(new Vector3(grade, 1, 0));
        using var slide = new Ground(null, (x, _) => -grade * x, (_, _) => normal);
        MoveState sliding = WithSignals(Falling(Vector3.Zero));
        for (int tick = 0; tick < hz; tick++)
        {
            sliding = slide.Step(sliding, Vector2.Zero, 0, false, Tuning, dt, run);
            Assert.False(sliding.Grounded, Show(sliding));
            AssertNoSignals(sliding);
        }
        Assert.True(Feet(sliding).X > 0 && Feet(sliding).Y < 0, Show(sliding));
    }

    // At x -0.25 the radius-0.2 foot is still below the riser. The next walk mounts it before the jump is consumed.
    [Theory]
    [MemberData(nameof(SignalRows))]
    public void AJumpTickKeepsItsGroundStep(SceneVariant variant, int hz, bool run)
    {
        using var g = new Ground(Doorstep(variant, 0.18f), Abyss);
        float dt = 1f / hz;
        MoveState s = g.Step(Standing(new Vector3(-0.25f, 0, 0)), Vector2.UnitX, 1, true, Tuning, dt, run);

        Assert.False(s.Grounded, Show(s));
        Assert.True(s.SupportGranted, Show(s));
        Assert.Equal(Tuning.JumpSpeed, s.VerticalVelocity);
        AssertNear(Math.Min(0.18f, (double)Tuning.MaxStepClimbSpeed * dt), s.StepDeltaY, Skin, Show(s));
        AssertNoClimb(s);
        MoveState airborne = g.Step(s, Vector2.Zero, 0, false, Tuning, dt);
        Assert.False(airborne.Grounded, Show(airborne));
        AssertNoSignals(airborne);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public void HeldAndFallbackReportZeros(int hz)
    {
        float dt = 1f / hz;
        MoveState state = WithSignals(Standing(Vector3.Zero));
        using (var held = new Ground(OverCapacityFan(), Abyss))
        {
            MoveState result = held.Step(state, Vector2.UnitX, 1, false, Tuning, dt);
            Assert.Equal(state.Position, result.Position);
            Assert.True(result.Grounded, Show(result));
            AssertNoSignals(result);
        }
        using var flat = new Ground(null, Flat);
        MoveState fallback = flat.Step(state, Vector2.UnitX, 1, false, Tuning, dt,
            clampXz: (_, _) => new Vector2(float.NaN, float.NaN));
        Assert.Equal(state.Position, fallback.Position);
        AssertNoSignals(fallback);
    }

    [Theory]
    [MemberData(nameof(SignalRows))]
    public void PacingOffReportsTheDoorstepInOneTick(SceneVariant variant, int hz, bool run)
    {
        using var g = new Ground(Doorstep(variant, 0.18f), Abyss);
        MoveTuning tuning = Tuning with { MaxStepClimbSpeed = 0 };
        MoveState s = Standing(new Vector3(-0.5f, 0, 0));
        var events = new List<float>();
        for (int tick = 0; tick < 10; tick++)
        {
            bool moving = Feet(s).X < 1;
            s = g.Step(s, moving ? Vector2.UnitX : Vector2.Zero, moving ? 1 : 0, false, tuning, 1f / hz, run);
            Assert.True(s.Grounded, Show(s));
            AssertNoClimb(s);
            if (s.StepDeltaY != 0) events.Add(s.StepDeltaY);
        }
        AssertNear(0.18f, Assert.Single(events), Skin, Show(s));
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public void HeldKeepsTheGrant(int hz)
    {
        using var g = new Ground(OverCapacityFan(), Abyss);
        MoveState s = g.Step(Standing(Vector3.Zero), Vector2.UnitX, 1, false, Tuning, 1f / hz);

        Assert.Equal(Standing(Vector3.Zero).Position, s.Position);
        Assert.True(s.Grounded, Show(s));
        Assert.True(s.SupportGranted, Show(s));
        AssertNoSignals(s);
    }
}
