// Rows for a footing change inside a tick: the rest of the tick runs in the next mode, with the climb budget the
// tick has left. Expectations come from the installed geometry and the closed forms, never from re-running the cores.
using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Contacts.FootSupportScenes;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public partial class ContactControllerTests
{
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

    // A fall at 20 m/s onto a 50 degree plane rising to +X through the origin, from feet 0.5 over it. Gravity first
    // gives vy = -20 - g dt, a drop of 0.694 in four substeps of a quarter tick, and the third ends below the plane, so
    // the body meets it at the origin with a quarter of the tick left. The rest of the tick slides, without footing,
    // at the bare gate, whose friction ramp scales gravity along the plane by (50 - 45) / 8. The slide starts from the
    // vertical speed at the hand-off, vy + g dt / 4, so the tick counts gravity once: the fall-line speed
    // f = -(vy + g dt / 4) sin(50) + g sin(50) (5 / 8) dt / 4, the feet f cos(50) dt / 4 down the fall line to -X on
    // the plane, and a vertical speed of -f sin(50).
    [Theory]
    [MemberData(nameof(Grounds))]
    public void LandingOntoSteepKeepsTheRestOfTheTick(string ground)
    {
        double angle = Radians(50), h = Math.Sin(angle);
        float grade = MathF.Tan(Radians(50));
        Vector3 normal = Vector3.Normalize(new Vector3(-grade, 1, 0));
        using Ground g = Make(ground, v => Slope(v, 50), (x, _) => grade * x, (_, _) => normal);
        MoveState state = Falling(new Vector3(0, 0.5f, 0)) with { VerticalVelocity = -20 };

        MoveState result = g.Step(state, Vector2.Zero, 0, false, Tuning);

        double vy = -20 - Gravity * (double)Dt, rest = Dt / 4.0;
        Assert.Equal(4, (int)Math.Ceiling(-vy * Dt / (0.5 * Tuning.CapsuleRadius)));
        double fall = -(vy + Gravity * rest) * h + Gravity * h * 5 / 8 * rest;
        double x = -fall * Math.Cos(angle) * rest;
        Assert.False(result.Grounded, Show(result));
        AssertNear(x, Feet(result).X, HalfSkin, Show(result));
        AssertNear(grade * x, Feet(result).Y, HalfSkin, Show(result));
        AssertNear(-fall * h, result.VerticalVelocity, 1e-3, Show(result));
    }

    // A 60 degree terrain face falls to +X from the origin to an edge at x 0.15 over a pit 100 m down. A body on the
    // face at the origin slides at 20 m/s down the fall line. One tick of gravity along the plane, g sin(60) dt, takes
    // it to f1 = 20.72, whose move of f1 cos(60) dt = 0.345 runs in three substeps, each dropping at most half
    // StepHeight along the plane. The second leaves the edge for the pit, so the body flies the last third of the tick
    // from that substep's start height. It hands on the fall-line speed at the hand-off, 20 + g sin(60) 2 dt / 3, so
    // the tick counts gravity once, and the flight's gravity first gives the tick's closed-form end and vertical speed.
    [Fact]
    public void SlideOffAnEdgeHandsOnItsSpeed()
    {
        const float Edge = 0.15f;
        double angle = Radians(60), h = Math.Sin(angle), c = Math.Cos(angle), a = Gravity * h;
        float grade = MathF.Tan(Radians(60));
        Vector3 normal = Vector3.Normalize(new Vector3(grade, 1, 0));
        using var g = new Ground(null, (x, _) => x <= Edge ? -grade * x : -100, (_, _) => normal);
        MoveState state = Falling(Vector3.Zero) with
        {
            HorizontalVelocity = new Vector2((float)(20 * c), 0),
            VerticalVelocity = (float)(-20 * h),
        };

        MoveState result = g.Step(state, Vector2.Zero, 0, false, Tuning);

        double travel = (20 + a * Dt) * c * Dt, substep = 0.5 * Tuning.StepHeight * c / h;
        Assert.Equal(3, (int)Math.Ceiling(travel / substep));
        Assert.True(travel / 3 <= Edge && 2 * travel / 3 > Edge, $"the edge is not in the second substep: {travel}");
        double rest = Dt / 3.0, handed = 20 + a * (Dt - rest), vy = -handed * h - Gravity * rest;
        Assert.False(result.Grounded, Show(result));
        AssertNear(2 * travel / 3 + handed * c * rest, Feet(result).X, HalfSkin, Show(result));
        AssertNear(-grade * travel / 3 + vy * rest, Feet(result).Y, HalfSkin, Show(result));
        AssertNear(vy, result.VerticalVelocity, 1e-3, Show(result));
    }

    // A body at rest at the crease of a V-gully of 60 degree terrain faces slides onto the opposing face on its first
    // tick, stalls and wedges. As legacy, the wedge latches the fall the slide integrated, g sin(60) dt along the fall
    // line from rest with the ramp at full strength, so the tick reports the landing impact of its vertical part,
    // g sin(60)^2 dt, and holds no vertical speed while it counts as grounded.
    [Fact]
    public void WedgeLatchesTheIntegratedFall()
    {
        float grade = MathF.Tan(Radians(60));
        Vector3 left = Vector3.Normalize(new Vector3(grade, 1, 0)), right = Vector3.Normalize(new Vector3(-grade, 1, 0));
        using var g = new Ground(null, (x, _) => MathF.Abs(x) * grade, (x, _) => x <= 0 ? left : right);

        MoveState result = g.Step(Falling(Vector3.Zero), Vector2.Zero, 0, false, Tuning);

        double h = Math.Sin(Radians(60));
        Assert.True(result.Grounded && result.SupportGranted, Show(result));
        AssertNear(Gravity * h * h * Dt, result.LandingImpactSpeed, 1e-3, Show(result));
        Assert.Equal(0f, result.VerticalVelocity);
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
        Vector3 left = Vector3.Normalize(new Vector3(grade, 1, 0));
        Vector3 right = Vector3.Normalize(new Vector3(-grade, 1, 0));
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

    // The wedge row's V-gully. Once the body lies still in the crease, holding the jump launches it from every wedged
    // tick, and the jump still rises after the next tick's gravity, so that tick flies instead of sliding. The cycle
    // must manage at least one jump in two free hops of 2 JumpSpeed / g, every launch must clear the crease by half
    // the ballistic reach JumpSpeed^2 / 2g, no tick rises past that reach plus StepHeight and a 0.05 margin, and after
    // the first tenth of the held ticks the second half never peaks 20 mm above the first.
    [Theory]
    [MemberData(nameof(Grounds))]
    public void WedgeJumpClearsTheCrease(string ground)
    {
        const int Settle = 60, Ticks = 600, Settled = Ticks / 10, Half = Settled + (Ticks - Settled) / 2;
        float degrees = ground == "terrain" ? 60 : 55;
        float angle = Radians(degrees), grade = MathF.Tan(angle);
        Vector3 left = Vector3.Normalize(new Vector3(grade, 1, 0));
        Vector3 right = Vector3.Normalize(new Vector3(-grade, 1, 0));
        using Ground g = Make(ground, v => new FootSupportScene(v)
                .Slab("left", new Vector3(-MathF.Cos(angle), MathF.Sin(angle), 0), -angle, 1, 2)
                .Slab("right", new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0), angle, 1, 2),
            (x, _) => MathF.Abs(x) * grade, (x, _) => x <= 0 ? left : right);
        float startY = g.Scene is null ? 0.6f * grade : (float)g.Scene.TopHeightAt("left", -0.6, 0);
        MoveState s = Falling(new Vector3(-0.6f, startY, 0));
        for (int tick = 0; tick < Settle; tick++) s = g.Step(s, Vector2.Zero, 0, false, Tuning);
        Assert.True(s.Grounded, $"not wedged after {Settle} ticks: {Show(s)}");
        float crease = Feet(s).Y;
        double reach = (double)Tuning.JumpSpeed * Tuning.JumpSpeed / (2 * Tuning.Gravity);
        double ceiling = crease + reach + Tuning.StepHeight + 0.05;

        int jumps = 0;
        double peak = double.NegativeInfinity, firstMax = double.NegativeInfinity, secondMax = double.NegativeInfinity;
        for (int tick = 0; tick < Ticks; tick++)
        {
            s = g.Step(s, Vector2.Zero, 0, true, Tuning);
            double y = Feet(s).Y;
            if (s.VerticalVelocity == Tuning.JumpSpeed)
            {
                if (jumps > 0) Assert.True(peak >= crease + reach / 2, $"tick {tick}: the last jump peaked {peak}");
                jumps++;
                peak = double.NegativeInfinity;
            }
            peak = Math.Max(peak, y);
            Assert.True(y <= ceiling, $"tick {tick} rose past {ceiling}: {Show(s)}");
            if (tick < Settled) continue;
            if (tick < Half) firstMax = Math.Max(firstMax, y);
            else secondMax = Math.Max(secondMax, y);
        }
        double hops = Ticks * Dt * Tuning.Gravity / (2 * Tuning.JumpSpeed);
        Assert.True(jumps >= hops / 2, $"only {jumps} jumps against {hops:F1} free hops");
        Assert.True(secondMax <= firstMax + 0.02, $"the cycle ratcheted: first {firstMax}, second {secondMax}");
    }

    // Terrain falls to +X at 50 degrees to a convex break at x 1.5, then at 60 degrees. A body at rest at the origin
    // slides down and over the break. A slide descends, so its vertical speed after the tick's gravity never rises
    // and every tick stays a slide on the surface, to half a skin. Its speed only grows, except where the fall-line
    // speed moves from the 50 degree plane onto the 60 degree one, which keeps cos(10) of it. So no tick's speed falls
    // below cos(10) of the tick before. A tick that flew the input instead would keep only the vertical part.
    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public void SlideOverAConvexBreakKeepsItsSpeed(int hz)
    {
        const float Break = 1.5f, End = 4;
        float shallow = MathF.Tan(Radians(50)), steep = MathF.Tan(Radians(60));
        Vector3 upper = Vector3.Normalize(new Vector3(shallow, 1, 0));
        Vector3 lower = Vector3.Normalize(new Vector3(steep, 1, 0));
        float Height(float x, float z) => x <= Break ? -shallow * x : -shallow * Break - steep * (x - Break);
        using var g = new Ground(null, Height, (x, _) => x <= Break ? upper : lower);
        float dt = 1f / hz;
        double keep = Math.Cos(Radians(10)), speed = 0;
        MoveState s = Falling(Vector3.Zero);
        for (int tick = 1; tick <= 3 * hz && Feet(s).X < End; tick++)
        {
            s = g.Step(s, Vector2.Zero, 0, false, Tuning, dt);
            Assert.False(s.Grounded, $"tick {tick}: {Show(s)}");
            AssertNear(Height(Feet(s).X, Feet(s).Z), Feet(s).Y, HalfSkin, $"tick {tick} left the slope: {Show(s)}");
            double next = Math.Sqrt(s.HorizontalVelocity.LengthSquared() + (double)s.VerticalVelocity * s.VerticalVelocity);
            Assert.True(next >= speed * keep - 1e-3, $"tick {tick} fell from {speed} to {next}: {Show(s)}");
            speed = next;
        }
        Assert.True(Feet(s).X >= End, $"the slide never passed the break: {Show(s)}");
    }
}
