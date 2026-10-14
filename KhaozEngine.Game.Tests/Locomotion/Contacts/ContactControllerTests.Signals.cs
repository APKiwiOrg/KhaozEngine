using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.NetWorld;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Contacts.FootSupportScenes;

namespace KhaozEngine.Tests.Locomotion.Contacts;

[Collection("AllocSensitive")]
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

    // A stationary body that still owes a step's climb reports the paid part as a step event.
    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public void CarriedStepLagReportsItsAppliedRise(int hz)
    {
        using var g = new Ground(null, (_, _) => 0.18f);
        MoveState state = Standing(new Vector3(0, 0.03f, 0)) with { StepDeltaY = 0.18f };
        float dt = 1f / hz;

        MoveState result = g.Step(state, Vector2.Zero, 0, false, Tuning, dt);

        AssertNear((double)Tuning.MaxStepClimbSpeed * dt, result.StepDeltaY, Skin, Show(result));
        AssertNoClimb(result);
    }

    // The same lag under a run in progress continues the ascent, so it reports a climb rate seeded from the paid
    // rise (0.7 of MaxStepClimbSpeed with no carried EWMA) and no step event.
    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public void CarriedRunPaysItsLagAsAClimb(int hz)
    {
        using var g = new Ground(null, (_, _) => 0.18f);
        MoveState state = Standing(new Vector3(0, 0.03f, 0)) with { ClimbRate = 1f };
        float dt = 1f / hz;

        MoveState result = g.Step(state, Vector2.Zero, 0, false, Tuning, dt);

        Assert.Equal(0f, result.StepDeltaY);
        AssertNear(GroundSignals.SeedFraction * Tuning.MaxStepClimbSpeed, result.ClimbRateEwma, 1e-3f, Show(result));
        Assert.Equal(result.ClimbRateEwma, result.ClimbRate);
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

    [Theory]
    [MemberData(nameof(SignalRows))]
    public void StairAscentExportsAContinuousClimbRate(SceneVariant variant, int hz, bool run)
    {
        using var g = new Ground(Staircase(variant, 16, 0.18f, 0.28f), Abyss);
        float dt = 1f / hz;
        MoveState s = Standing(new Vector3(-0.5f, 0, 0));
        bool started = false, finished = false;
        var climb = new List<(float Rate, double Rise)>();
        for (int tick = 0; tick < 2 * hz && !finished; tick++)
        {
            MoveState input = s;
            s = g.Step(s, Vector2.UnitX, 1, false, Tuning, dt, run);
            Assert.True(s.Grounded, Show(s));
            started |= Feet(s).Y > Skin;
            if (!started) continue;
            if (!(s.ClimbRate > 0))
            {
                float speed = run ? Tuning.RunSpeed : Tuning.WalkSpeed;
                GroundStepResult core = GroundCore.Step(Feet(input), speed * dt * Vector2.UnitX, dt, Tuning, Settings,
                    DryQueries.For(g.Height, g.Normal, g.World, g.Lease, Tuning), Banded,
                    stepInProgress: input.StepDeltaY > 0 || input.ClimbRate > 0);
                Assert.True(s.ClimbRate > 0,
                    $"tick {tick}, input rate {input.ClimbRate:R}, actual travel {Feet(s) - Feet(input)}, " +
                    $"core travel {core.Achieved}, rise {core.StepRise:R}, support {core.Support.Height:R}: {Show(s)}");
            }
            Assert.InRange(s.ClimbRate, 0f, Tuning.MaxStepClimbSpeed);
            Assert.Equal(0f, s.StepDeltaY);
            climb.Add((s.ClimbRate, (double)Feet(s).Y - Feet(input).Y));
            finished = Feet(s).Y >= 16 * 0.18f - Skin;
        }
        Assert.True(started && finished, Show(s));
        double sum = 0, rise = 0;
        int first = climb.Count / 2;
        for (int i = first; i < climb.Count; i++)
        {
            sum += climb[i].Rate;
            rise += climb[i].Rise;
        }
        int ticks = climb.Count - first;
        AssertNear(rise / (ticks * (double)dt), sum / ticks, 0.25, Show(s));
    }

    [Theory]
    [MemberData(nameof(SignalRows))]
    public void StairDescentExportsANegativeClimbRate(SceneVariant variant, int hz, bool run)
    {
        using var g = new Ground(Staircase(variant, 8, 0.18f, 0.28f), Abyss);
        float dt = 1f / hz;
        MoveState s = Standing(new Vector3(2.5f, 8 * 0.18f, 0));
        bool started = false, finished = false;
        for (int tick = 0; tick < 2 * hz && !finished; tick++)
        {
            s = g.Step(s, -Vector2.UnitX, 1, false, Tuning, dt, run);
            Assert.True(s.Grounded, Show(s));
            started |= Feet(s).Y < 8 * 0.18f - Skin;
            if (!started) continue;
            Assert.True(s.ClimbRate < 0, $"tick {tick}, rate {s.ClimbRate:R}: {Show(s)}");
            Assert.InRange(s.ClimbRate, -6f, 0f);
            Assert.Equal(0f, s.StepDeltaY);
            Assert.Equal(0f, s.ClimbRateEwma);
            finished = Feet(s).Y <= Skin;
        }
        Assert.True(started && finished, Show(s));
    }

    [Theory]
    [MemberData(nameof(SignalRows))]
    public void DiagonalAscentStillReadsTheRun(SceneVariant variant, int hz, bool run)
    {
        using var g = new Ground(Stairs(variant, 0.28f, 0.18f, 8, halfWidth: 3), Abyss);
        float dt = 1f / hz;
        var direction = new Vector2(MathF.Cos(Radians(30)), MathF.Sin(Radians(30)));
        MoveState s = Standing(new Vector3(-0.5f, 0, 0));
        bool found = false;
        for (int tick = 0; tick < hz && Feet(s).Y < 4 * 0.18f; tick++)
        {
            s = g.Step(s, direction, 1, false, Tuning, dt, run);
            if (Feet(s).Y <= Skin) continue;
            if (s.ClimbRate <= 0) continue;
            Assert.Equal(0f, s.StepDeltaY);
            found = true;
        }
        Assert.True(found, Show(s));
    }

    [Theory]
    [MemberData(nameof(SignalRows))]
    public void StairsEndingAtAWallExportNothingOnceStopped(SceneVariant variant, int hz, bool run)
    {
        using var g = new Ground(Staircase(variant, 8, 0.18f, 0.28f).Wall("wall", 2.6f, 0.2f, 4), Abyss);
        MoveState s = Standing(new Vector3(-0.5f, 0, 0));
        bool climbed = false, stopped = false;
        for (int tick = 0; tick < 2 * hz; tick++)
        {
            MoveState input = s;
            Vector3 previous = Feet(s);
            s = g.Step(s, Vector2.UnitX, 1, false, Tuning, 1f / hz, run);
            climbed |= s.ClimbRate > 0;
            if (previous.X < 2.6f - Tuning.CapsuleRadius - 2 * Skin) continue;
            if (Feet(s).X != previous.X || Feet(s).Z != previous.Z) continue;
            if (Math.Abs(previous.Y - 8 * 0.18f) > Skin) continue;
            Assert.True(s.ClimbRate == 0f && s.ClimbRateEwma == 0f && s.StepDeltaY == 0f,
                $"tick {tick}, travel {Feet(s) - previous}, step {s.StepDeltaY:R}, " +
                $"input rate {input.ClimbRate:R}, input step {input.StepDeltaY:R}: {Show(s)}");
            Assert.True(float.IsFinite(s.ClimbRate) && float.IsFinite(s.ClimbRateEwma) &&
                float.IsFinite(s.StepDeltaY), Show(s));
            stopped = true;
        }
        Assert.True(climbed && stopped, Show(s));
    }

    [Theory]
    [InlineData(30, 1.6f, true)]
    [InlineData(60, 1.6f, true)]
    [InlineData(30, 1.2f, false)]
    [InlineData(60, 1.2f, false)]
    public void SamplesOnSteepSupportAreSkipped(int hz, float steepFrom, bool enoughWalkable)
    {
        float steepGrade = MathF.Tan(Radians(70));
        Vector3 steepNormal = Vector3.Normalize(new Vector3(-steepGrade, 1, 0));
        float Height(float x, float z) => x < steepFrom
            ? 0.18f * (MathF.Floor(x / 0.28f) + 1)
            : 0.18f * (MathF.Floor(steepFrom / 0.28f) + 1) + steepGrade * (x - steepFrom);
        Vector3 Normal(float x, float z) => x < steepFrom ? Vector3.UnitY : steepNormal;
        using var g = new Ground(null, Height, Normal);
        MoveState input = Standing(new Vector3(0.5f, 0.36f, 0));

        MoveState result = g.Step(input, Vector2.UnitX, 1, false, Tuning, 1f / hz);

        if (enoughWalkable)
        {
            Assert.True(result.ClimbRate > 0, Show(result));
            double rate = (Feet(result).Y - Feet(input).Y) * hz;
            double expected = 0.7 * rate;
            AssertNear(expected, result.ClimbRate, 1e-5, Show(result));
            Assert.Equal(0f, result.StepDeltaY);
        }
        else AssertNoClimb(result);
    }

    [Theory]
    [MemberData(nameof(SignalRows))]
    public void DeniedDescentExportsNothing(SceneVariant variant, int hz, bool run)
    {
        using var g = new Ground(Staircase(variant, 8, 0.18f, 0.28f).Wall("wall", 2.2f, 0.2f, 4), Abyss);
        float x = 2.2f + (variant == SceneVariant.Box ? 0.2f : 0f) + Tuning.CapsuleRadius + Skin;
        MoveState s = Standing(new Vector3(x, 8 * 0.18f, 0)) with { ClimbRate = -3f };
        bool stopped = false;
        for (int tick = 0; tick < 4; tick++)
        {
            MoveState input = s;
            Vector3 previous = Feet(s);
            s = g.Step(s, -Vector2.UnitX, 1, false, Tuning, 1f / hz, run);
            if (s.ClimbRate != 0f)
            {
                float speed = run ? Tuning.RunSpeed : Tuning.WalkSpeed;
                GroundStepResult core = GroundCore.Step(previous, -speed / hz * Vector2.UnitX, 1f / hz, Tuning, Settings,
                    DryQueries.For(g.Height, g.Normal, g.World, g.Lease, Tuning), Banded,
                    stepInProgress: input.StepDeltaY > 0 || input.ClimbRate > 0);
                Assert.True(s.ClimbRate == 0f,
                    $"tick {tick}, rate {s.ClimbRate:R}, actual travel {Feet(s) - previous}, " +
                    $"core travel {core.Achieved}, rise {core.StepRise:R}: {Show(s)}");
            }
            AssertNoSignals(s);
            stopped |= Feet(s).X == previous.X;
        }
        Assert.True(stopped, Show(s));
    }

    [Theory]
    [MemberData(nameof(SignalRows))]
    public void ReplayFromAQuantizedClimbRateIsIdentical(SceneVariant variant, int hz, bool run)
    {
        using var g = new Ground(Staircase(variant, 8, 0.18f, 0.28f), Abyss);
        // Mid stair a rate between two quanta. On the seventh tread, where the window already sees the landing, a
        // rate under half a quantum that only continuation could carry.
        foreach ((Vector3 feet, float rate, bool climbs) in new[]
                 {
                     (new Vector3(0.5f, 0.36f, 0), 1.73f, true),
                     (new Vector3(1.78f, 1.26f, 0), 0.02f, false),
                 })
        {
            MoveState live = Standing(feet) with { ClimbRate = rate, ClimbRateEwma = rate };
            bool climbed = false;
            for (int tick = 0; tick < hz; tick++)
            {
                // The replay starts each tick from the live state with its rate rebuilt from the wire.
                MoveState basis = live with
                {
                    ClimbRate = MovementState.DecodeClimbRate(MovementState.QuantizeClimbRate(live.ClimbRate)),
                };
                MoveState replay = g.Step(basis, Vector2.UnitX, 1, false, Tuning, 1f / hz, run);
                live = g.Step(live, Vector2.UnitX, 1, false, Tuning, 1f / hz, run);
                AssertSameBits(live, replay, tick);
                climbed |= live.ClimbRate > 0;
            }
            Assert.True(climbed == climbs, $"start {feet}, rate {rate}: {Show(live)}");
        }
    }

    [Theory]
    [MemberData(nameof(SignalRows))]
    public void ClimbRateDoesNotDependOnTheUnreplicatedStepEvent(SceneVariant variant, int hz, bool run)
    {
        float dt = 1f / hz;
        // An even stair starts its run on the first riser. A 1 m first tread makes that riser a lone step whose paced
        // lag is still being paid when the window reaches the stair.
        foreach (float? firstTread in new float?[] { null, 1f })
        {
            using var g = new Ground(Stairs(variant, 0.28f, 0.18f, 16, firstTread: firstTread), Abyss);
            // The single-World head passes each output on whole. The sharded head rebuilds it without the step event.
            MoveState carried = Standing(new Vector3(-0.5f, 0, 0)), rebuilt = carried;
            bool top = false, bottom = false, ascended = false, descended = false;
            for (int tick = 0; tick < 8 * hz && !bottom; tick++)
            {
                Vector2 direction = top ? -Vector2.UnitX : Vector2.UnitX;
                carried = g.Step(carried, direction, 1, false, Tuning, dt, run);
                rebuilt = g.Step(rebuilt with { StepDeltaY = 0f }, direction, 1, false, Tuning, dt, run);
                string context = $"first tread {firstTread}, tick {tick}: {Show(carried)} against {Show(rebuilt)}";
                Assert.True(Same(carried.ClimbRate, rebuilt.ClimbRate) &&
                    Same(carried.ClimbRateEwma, rebuilt.ClimbRateEwma), context);
                Assert.True(Same(carried.Position.X, rebuilt.Position.X) &&
                    Same(carried.Position.Y, rebuilt.Position.Y) && Same(carried.Position.Z, rebuilt.Position.Z),
                    context);
                ascended |= carried.ClimbRate > 0;
                descended |= carried.ClimbRate < 0;
                top |= Feet(carried).Y >= 16 * 0.18f - Skin;
                bottom = top && Feet(carried).Y <= Skin;
            }
            Assert.True(bottom && ascended && descended, $"first tread {firstTread}: {Show(carried)}");
        }

        static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
    }

    // A run is in progress only when the wire keeps its rate nonzero. QuantizeClimbRate rounds exact halves to even,
    // so half a quantum is not a run on any head.
    [Fact]
    public void RunInProgressMatchesTheWireQuantum()
    {
        float half = MoveState.ClimbRateQuantum / 2;
        foreach (float rate in new[] { half, MathF.BitDecrement(half), MathF.BitIncrement(half), 3 * half,
            MoveState.ClimbRateQuantum, 0f })
        {
            Assert.Equal(MovementState.QuantizeClimbRate(rate) > 0, GroundSignals.Ascending(rate));
            Assert.Equal(MovementState.QuantizeClimbRate(-rate) < 0, GroundSignals.Descending(-rate));
        }
    }

    // A slow climb whose smoothed rate falls below half a quantum ends its run and its EWMA, so no head carries a run
    // that a basis rebuilt from the wire (rate decoded, EWMA seeded from it, no step event) would not. The rebuilt EWMA
    // is the quantized rate, so the two can still straddle the half-quantum threshold, by at most one quantum, the
    // continuation gap the sharded handoff already accepts.
    [Theory]
    [InlineData(SceneVariant.Box, 60)]
    [InlineData(SceneVariant.Mesh, 60)]
    [InlineData(SceneVariant.Box, 30)]
    [InlineData(SceneVariant.Mesh, 30)]
    public void SlowStairClimbCarriesNoRunTheWireCannotShow(SceneVariant variant, int hz)
    {
        using var g = new Ground(Staircase(variant, 16, 0.18f, 0.28f), Abyss);
        float dt = 1f / hz, half = MoveState.ClimbRateQuantum / 2;
        MoveState live = Standing(new Vector3(-0.5f, 0, 0));
        int worst = 0;
        for (int tick = 0; tick < 20 * hz; tick++)
        {
            float decoded = MovementState.DecodeClimbRate(MovementState.QuantizeClimbRate(live.ClimbRate));
            MoveState basis = live with { ClimbRate = decoded, ClimbRateEwma = MathF.Max(0f, decoded), StepDeltaY = 0f };
            MoveState next = g.Step(live, Vector2.UnitX, 0.05f, false, Tuning, dt);
            MoveState fromWire = g.Step(basis, Vector2.UnitX, 0.05f, false, Tuning, dt);
            Assert.True(next.ClimbRateEwma == 0f || next.ClimbRateEwma > half, $"tick {tick}: {Show(next)}");
            worst = Math.Max(worst, Math.Abs(MovementState.QuantizeClimbRate(next.ClimbRate) -
                MovementState.QuantizeClimbRate(fromWire.ClimbRate)));
            live = next;
        }
        Assert.True(worst <= 1, $"the live and wire-rebuilt climb rates differ by {worst} quanta");
    }

    [Theory]
    [InlineData(SceneVariant.Box, 0f)]
    [InlineData(SceneVariant.Mesh, 0f)]
    [InlineData(SceneVariant.Box, HalfSkin)]
    [InlineData(SceneVariant.Mesh, HalfSkin)]
    public void SubSkinGroundTravelKeepsAppliedStepsWithoutStartingARun(SceneVariant variant, float travel)
    {
        using var g = new Ground(Staircase(variant, 8, 0.18f, 0.28f), Abyss);
        MoveState initial = Standing(new Vector3(0.5f, 0.36f, 0));
        SupportSample support = FootSupport.Find(g.Height, g.Normal, g.World, g.Lease,
            new FootSupportQuery(new Vector2(0.5f, 0), 0.36f, 0.2f, Tuning.StepHeight, Tuning.StepHeight,
                MathF.Cos(Banded)));
        foreach (float rise in new[] { 0.1f, -0.1f })
        {
            var result = GroundSignals.Resolve(initial, rise, rise, rise > 0, rise < 0, 0f, travel * Vector2.UnitX,
                Feet(initial), support, true, Dt, Tuning, 0.2f, MathF.Cos(Banded),
                DryQueries.For(g.Height, g.Normal, g.World, g.Lease, Tuning));

            Assert.Equal((0f, 0f, rise), result);
        }
    }

    [Theory]
    [InlineData(SceneVariant.Box, 0f)]
    [InlineData(SceneVariant.Mesh, 0f)]
    [InlineData(SceneVariant.Box, HalfSkin)]
    [InlineData(SceneVariant.Mesh, HalfSkin)]
    public void SubSkinGroundTravelContinuesAnAppliedAscent(SceneVariant variant, float travel)
    {
        using var g = new Ground(Staircase(variant, 8, 0.18f, 0.28f), Abyss);
        MoveState input = Standing(new Vector3(0.5f, 0.36f, 0)) with
        {
            ClimbRate = 1.75f,
            ClimbRateEwma = 1.75f,
        };
        SupportSample support = FootSupport.Find(g.Height, g.Normal, g.World, g.Lease,
            new FootSupportQuery(new Vector2(0.5f, 0), 0.36f, 0.2f, Tuning.StepHeight, Tuning.StepHeight,
                MathF.Cos(Banded)));

        var result = GroundSignals.Resolve(input, 0.1f, 0.1f, true, false, 0f, travel * Vector2.UnitX, Feet(input), support,
            true, Dt, Tuning, 0.2f, MathF.Cos(Banded), DryQueries.For(g.Height, g.Normal, g.World, g.Lease, Tuning));

        double expected = 1.75 + (1 - Math.Exp(-5.0 * Dt)) * (0.1 / Dt - 1.75);
        AssertNear(expected, result.ClimbRate, 1e-5, result);
        Assert.Equal(result.ClimbRate, result.Ewma);
        Assert.Equal(0f, result.StepDeltaY);
    }

    [Theory]
    [InlineData(SceneVariant.Box, 1)]
    [InlineData(SceneVariant.Mesh, 1)]
    [InlineData(SceneVariant.Box, -1)]
    [InlineData(SceneVariant.Mesh, -1)]
    public void GroundSignalBoundaryEvidence(SceneVariant variant, int direction)
    {
        using var g = new Ground(Staircase(variant, 8, 0.18f, 0.28f), Abyss);
        var feet = direction > 0 ? new Vector3(1.5f, 1.05f, 0) : new Vector3(0.1f, 0.36f, 0);
        MoveState input = Standing(feet) with { ClimbRate = direction * 3f, ClimbRateEwma = direction > 0 ? 3f : 0f };
        float rise = direction > 0 ? 3.5f * Dt : -0.18f;
        Vector2 travel = direction * 0.2f * Vector2.UnitX;
        float cosGate = MathF.Cos(Banded);
        Span<float> heights = stackalloc float[4];
        SupportSample axisSupport = default;
        for (int k = 0; k < 4; k++)
        {
            float distance = k * Tuning.CapsuleRadius;
            float reach = Tuning.StepHeight + 2 * distance;
            var axis = new Vector2(feet.X + direction * distance, feet.Z);
            var support = FootSupport.Find(g.Height, g.Normal, g.World, g.Lease,
                new FootSupportQuery(axis, feet.Y, 0.2f, reach, reach, cosGate));
            Assert.Equal(SupportStatus.Walkable, support.Status);
            heights[k] = support.Height;
            if (k == 0) axisSupport = support;
        }
        // Equal sample spacing gives these closed-form least-squares weights.
        double grade = (-3.0 * heights[0] - heights[1] + heights[2] + 3.0 * heights[3]) /
            (10 * (double)Tuning.CapsuleRadius);
        var result = GroundSignals.Resolve(input, rise, rise, rise > 0, rise < 0, 0f, travel, feet, axisSupport, true, Dt,
            Tuning, 0.2f, cosGate, DryQueries.For(g.Height, g.Normal, g.World, g.Lease, Tuning));

        Assert.True(direction * result.ClimbRate > 0,
            $"feet {feet}, heights [{heights[0]:R}, {heights[1]:R}, {heights[2]:R}, {heights[3]:R}], " +
            $"grade {grade:R}, rate {result.ClimbRate:R}, event {result.StepDeltaY:R}");
    }

    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void WarmSignalResolveAllocatesNothing(SceneVariant variant)
    {
        using var g = new Ground(Staircase(variant, 8, 0.18f, 0.28f), Abyss);
        var feet = new Vector3(0.5f, 0.36f, 0);
        MoveState input = Standing(feet) with { ClimbRate = 1.75f, ClimbRateEwma = 1.75f };
        Vector2 travel = 0.2f * Vector2.UnitX;
        float cosGate = MathF.Cos(Banded);
        SupportSample axisSupport = FootSupport.Find(g.Height, g.Normal, g.World, g.Lease,
            new FootSupportQuery(new Vector2(feet.X, feet.Z), feet.Y, 0.2f, Tuning.StepHeight, Tuning.StepHeight,
                cosGate));
        GroundSignals.Resolve(input, 0, 0, false, false, 0f, travel, feet, axisSupport, true, Dt, Tuning, 0.2f, cosGate,
            DryQueries.For(g.Height, g.Normal, g.World, g.Lease, Tuning));
        GroundSignals.Resolve(input, 0, 0, false, false, 0f, travel, feet, axisSupport, true, Dt, Tuning, 0.2f, cosGate,
            DryQueries.For(g.Height, g.Normal, g.World, g.Lease, Tuning));

        using var allocationRegion = AllocationRegion.Start(nameof(WarmSignalResolveAllocatesNothing));
        long before = GC.GetAllocatedBytesForCurrentThread();
        var result = GroundSignals.Resolve(input, 0, 0, false, false, 0f, travel, feet, axisSupport, true, Dt, Tuning, 0.2f,
            cosGate, DryQueries.For(g.Height, g.Normal, g.World, g.Lease, Tuning));
        long after = GC.GetAllocatedBytesForCurrentThread();

        allocationRegion.Dispose();
        Assert.Equal(before, after);
        Assert.True(result.ClimbRate > 0);
    }
}
