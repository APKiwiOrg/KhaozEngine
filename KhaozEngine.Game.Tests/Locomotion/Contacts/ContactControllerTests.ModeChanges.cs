// Rows for a footing change inside a tick: the rest of the tick runs in the next mode, with the climb budget the
// tick has left. Expectations come from the installed geometry, the closed forms and the cores themselves.
using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Contacts.FootSupportScenes;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public partial class ContactControllerTests
{
    // The rest of a tick whose ground move changed footing: no support flies, steep support slides, and a slide that
    // loses its support flies what is left. None of the parity rows lands again within the tick.
    static (Vector3 Feet, float VerticalVelocity) Continued(Ground g, in GroundStepResult core, Vector2 velocity,
        in MoveTuning tuning)
    {
        (Vector3 feet, Vector2 horizontal, float vertical, float time, double budget) =
            (core.Feet, velocity, 0f, core.RemainingTime, core.ClimbBudget);
        if (core.Footing == GroundFooting.Steep)
        {
            SlideStepResult slide = SlideCore.Step(feet, velocity, 0, velocity, time, tuning, Settings, Banded,
                core.Support, g.Height, g.Normal, g.World, g.Lease, budget);
            if (slide.Outcome != SlideOutcome.Airborne || !(slide.RemainingTime > 0))
            {
                Assert.True(slide.Outcome == SlideOutcome.Sliding, $"{slide}");
                return (slide.Feet, slide.VerticalVelocity);
            }
            (feet, horizontal, vertical, time, budget) =
                (slide.Feet, slide.HorizontalVelocity, slide.VerticalVelocity, slide.RemainingTime, slide.ClimbBudget);
        }
        AirStepResult air = AirPass.Step(feet, horizontal, vertical, time, tuning.Gravity, tuning, Settings, Banded,
            g.Height, g.Normal, g.World, g.Lease, budget);
        Assert.True(air.Outcome == AirOutcome.Airborne, $"{air}");
        return (air.Feet, air.VerticalVelocity);
    }

    // A body at the apex of its arc, feet 0.3 below the 0.6 ledge top and 0.3 short of its edge, runs at 12 m/s, so
    // its tick plans 0.4 in three substeps and lands on the ledge before the last one. The rise of 0.3 is more than
    // two budgets of MaxStepClimbSpeed dt = 0.117. The landing tick climbs at most one budget above its start feet,
    // the following ticks at most one budget each, and the body stands on the top after ceil(0.3 / budget) ticks.
    [Theory]
    [MemberData(nameof(Physical))]
    public void LandingTickClimbsWithinOneBudget(string ground)
    {
        using Ground g = Make(ground, Ledge, Flat);
        double budget = (double)Tuning.MaxStepClimbSpeed * Dt;
        const float StartY = LedgeTop - 0.3f;
        MoveState s = Falling(new Vector3(0.7f, StartY, 0));

        s = g.Step(s, Vector2.UnitX, 1, false, Tuning, run: true);
        Assert.True(s.Grounded, Show(s));
        Assert.True(Feet(s).X > 1, Show(s));
        Assert.True(Feet(s).Y <= StartY + budget + HalfSkin, $"climbed past one budget: {Show(s)}");

        int ticks = 1;
        while (Feet(s).Y < LedgeTop - HalfSkin && ticks < 10)
        {
            MoveState next = g.Step(s, Vector2.Zero, 0, false, Tuning);
            Assert.True(next.Grounded, Show(next));
            Assert.True(Feet(next).Y - Feet(s).Y <= budget + HalfSkin, $"climbed past one budget: {Show(next)}");
            s = next;
            ticks++;
        }
        AssertNear(LedgeTop, Feet(s).Y, HalfSkin, Show(s));
        Assert.Equal((int)Math.Ceiling(0.3 / budget), ticks);
    }

    // Flat terrain with a 5 m cliff over x > 1 for z < 0 only, so its face is a wall to the axis and ends at z 0. A
    // run at 12 m/s along (1, 1) from (0.95, -0.1) with feet 0.07 falling at 3 m/s plans (0.283, -0.128, 0.283) in
    // three substeps. The first meets the cliff and keeps only its Z part. The second reaches z 0.089, past the
    // cliff's end, with feet at -0.015, and lands with a third of the tick left. The ground walks that third with the
    // velocity the contact left, so it never regains the X part the cliff removed: X stays 0.95 and Z covers the
    // whole tick's 0.283.
    [Fact]
    public void GrazeThenLandDoesNotWalkBackIntoTheWall()
    {
        static float Height(float x, float z) => x > 1 && z < 0 ? 5 : 0;
        static Vector3 Normal(float x, float z) => x > 1 && z < 0 ? -Vector3.UnitX : Vector3.UnitY;
        using var g = new Ground(null, Height, Normal);
        Vector2 direction = Vector2.Normalize(Vector2.One);
        MoveState s = Falling(new Vector3(0.95f, 0.07f, -0.1f)) with { VerticalVelocity = -3 };

        MoveState landed = g.Step(s, direction, 1, false, Tuning, run: true);

        Assert.True(landed.Grounded, Show(landed));
        AssertNear(0.95, Feet(landed).X, HalfSkin, Show(landed));
        AssertNear(-0.1 + Tuning.RunSpeed * direction.Y * Dt, Feet(landed).Z, HalfSkin, Show(landed));
        AssertNear(0, Feet(landed).Y, HalfSkin, Show(landed));
        AssertNear(0, landed.HorizontalVelocity.X, 1e-4, Show(landed));
    }

    // The coyote row's ledges. Walking from x -0.45 at 6 m/s, whose tick move rounds just past 0.2 and so takes two
    // substeps of 0.1, the footprint leaves a prop ledge on the first substep from x 0.15, and the axis leaves the
    // terrain ledge on the first substep from x -0.05. The other half of the tick flies at the walking speed, so the
    // leaving tick still covers 0.2 and carries 6 m/s, with momentum on or off.
    [Theory]
    [MemberData(nameof(Grounds))]
    public void WalkOffKeepsTheTickSpeed(string ground)
    {
        const float Top = 2;
        using Ground g = Make(ground, v => FootSupportScenes.Floor(v).Flat("ledge", -4, 0, -2, 2, Top),
            (x, _) => x <= 0 ? Top : 0);
        foreach (bool momentum in new[] { true, false })
        {
            MoveTuning tuning = Tuning with { AirMomentum = momentum };
            MoveState s = Standing(new Vector3(-0.45f, Top, 0)), previous = s;
            for (int tick = 0; tick < 10 && s.Grounded; tick++)
            {
                previous = s;
                s = g.Step(s, Vector2.UnitX, 1, false, tuning);
            }
            Assert.False(s.Grounded, Show(s));
            AssertNear(Tuning.WalkSpeed * Dt, Feet(s).X - Feet(previous).X, HalfSkin, $"momentum {momentum}: {Show(s)}");
            AssertNear(Tuning.WalkSpeed, s.HorizontalVelocity.X, 1e-4, $"momentum {momentum}: {Show(s)}");
            Assert.True(s.VerticalVelocity < 0, $"momentum {momentum}: {Show(s)}");
        }
    }

    // A fall at 20 m/s onto a 50 degree plane rising to +X meets it within the tick, with time left. The rest of the
    // tick is the slide core's from the landing, with the air pass's velocity and budget, so the body slides down the
    // fall line, -X, past where it met the face.
    [Theory]
    [MemberData(nameof(Grounds))]
    public void LandingOntoSteepKeepsTheRestOfTheTick(string ground)
    {
        float grade = MathF.Tan(Radians(50));
        Vector3 normal = Vector3.Normalize(new Vector3(-grade, 1, 0));
        using Ground g = Make(ground, v => Slope(v, 50), (x, _) => grade * x, (_, _) => normal);
        MoveState state = Falling(new Vector3(0, 0.5f, 0)) with { VerticalVelocity = -20 };

        MoveState result = g.Step(state, Vector2.Zero, 0, false, Tuning);

        var start = new Vector3(state.Position.X, state.Position.Y - HalfHeight, state.Position.Z);
        float gate = Tuning.MaxSlopeRadians;
        AirStepResult air = AirPass.Step(start, Vector2.Zero, state.VerticalVelocity, Dt, Gravity, Tuning, Settings,
            gate, g.Height, g.Normal, g.World, g.Lease);
        Assert.True(air.Outcome == AirOutcome.Sliding && air.RemainingTime > 0, $"{air}");
        SlideStepResult slide = SlideCore.Step(air.Feet, air.HorizontalVelocity, air.VerticalVelocity, Vector2.Zero,
            air.RemainingTime, Tuning, Settings, gate, air.Support, g.Height, g.Normal, g.World, g.Lease,
            air.ClimbBudget);
        Assert.True(slide.Outcome == SlideOutcome.Sliding, $"{slide}");

        var expected = new Vector3(slide.Feet.X, slide.Feet.Y + HalfHeight, slide.Feet.Z);
        Assert.True(BitsEqual(expected, result.Position), $"slide {slide}, controller {Show(result)}");
        Assert.False(result.Grounded);
        Assert.True(Feet(result).X < air.Feet.X - HalfSkin, $"air {air}, controller {Show(result)}");
        Assert.True(Feet(result).Y < air.Feet.Y - HalfSkin, $"air {air}, controller {Show(result)}");
    }

    // The slide core's V-gully: 60 degree terrain faces, or 55 degree slabs. A body sliding in from x -0.6 stalls
    // short of the crease while its substeps are long, creeps the rest from rest, and then lies still. Once it lies
    // still, every tick starts grounded on steep support, slides at once and wedges again, so it stays grounded and
    // granted on every tick instead of alternating.
    [Theory]
    [MemberData(nameof(Grounds))]
    public void WedgeStaysGroundedEveryTick(string ground)
    {
        const int Ticks = 60;
        float degrees = ground == "terrain" ? 60 : 55;
        float angle = Radians(degrees), grade = MathF.Tan(angle);
        Vector3 left = Vector3.Normalize(new Vector3(grade, 1, 0)), right = Vector3.Normalize(new Vector3(-grade, 1, 0));
        using Ground g = Make(ground, v => new FootSupportScene(v)
                .Slab("left", new Vector3(-MathF.Cos(angle), MathF.Sin(angle), 0), -angle, 1, 2)
                .Slab("right", new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0), angle, 1, 2),
            (x, _) => MathF.Abs(x) * grade, (x, _) => x <= 0 ? left : right);
        float startY = g.Scene is null ? 0.6f * grade : (float)g.Scene.TopHeightAt("left", -0.6, 0);
        var states = new MoveState[Ticks + 1];
        states[0] = Falling(new Vector3(-0.6f, startY, 0));
        for (int tick = 1; tick <= Ticks; tick++)
            states[tick] = g.Step(states[tick - 1], Vector2.Zero, 0, false, Tuning);

        // It lies still from the first tick from which the feet never move more than half a skin.
        static bool Moved(in MoveState a, in MoveState b) => Vector3.Distance(Feet(a), Feet(b)) > HalfSkin;
        Assert.False(Moved(states[Ticks], states[Ticks - 1]), Show(states[Ticks]));
        int still = Ticks;
        while (still > 1 && !Moved(states[still - 1], states[still - 2])) still--;
        Assert.True(still <= Ticks / 2, $"still moving at tick {still}: {Show(states[still])}");
        for (int tick = still; tick <= Ticks; tick++)
        {
            MoveState s = states[tick];
            Assert.True(s.Grounded && s.SupportGranted, $"tick {tick}, still from {still}: {Show(s)}");
            Assert.True(Math.Abs(Feet(s).X) <= 0.5 * Tuning.CapsuleRadius + HalfSkin, Show(s));
        }
    }
}
