using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Locomotion;

// A WALL CONTACT KEEPS ITS ALONG-FACE TRAVEL ON OPEN TERRAIN (#498, 17.32.0). Eighth round of the steep-terrain
// chain, and like #475 and #486 it comes from a playtest rather than an exploit sweep.
//
// WHAT WAS REPORTED. Walking SIDEWAYS along a slope on Ruinborne, pressed lightly against it, stops the character
// dead. Not a stutter: the trace is binary (full commanded speed or exactly nothing) and once it stops the geometry
// under it is unchanged, so the next tick refuses the same move for the same reason forever. Measured on the real
// island heightfield at engine 17.31.0: 32 of 63 (site, heading) pairs kept under 90 percent of their commanded
// sideways travel, the worst kept 6 percent, with ZERO footing flips and zero slide ticks. The wall contact was
// eating the along-face travel its own documentation promises to leave alone.
//
// THE MECHANISM, and it is one line. AdvanceWallSlide re-tests the PROJECTED move against the same two conditions
// and, when it still lands on past-gate ground above the reach, returns the ORIGINAL position - the whole move, not
// the into-face part of it. Since #486 the reach on a grounded walking tick is exactly 0 (a walker has no upward
// velocity, so its own resolved upward motion is nothing), so that refusal fires whenever the projected destination
// is past-gate and rises by ANY amount at all. And the projected velocity is the destination's own height-plane
// CONTOUR, which is level by construction, so what it actually asks for is float rounding plus whatever the surface
// curves through under one tick's travel: the ten measured first-blocked ticks asked for +0.000 to +0.021 m, eight
// of them at +0.000 or +0.001.
//
// THE FIXTURE IS THE MECHANISM RATHER THAN THE SITE. A face that is STRAIGHT in plan cannot show this at all: the
// projected step runs exactly along the contour, so it lands at the same steepness and the same height it left, and
// the re-test admits it. What the island has and a straight face does not is a contour that BENDS, so one tick's
// straight-line step along the tangent at the destination cuts a little inside the contour it meant to follow. That
// cut is the whole of the ask, and it is one knob: at a 400 m bend radius a walking step cuts 5e-5 m inside (the
// face is planar to well under the float noise on the feet, which is the +0.000 class), and at 8 m it cuts 2.5e-3 m
// (the curved class). Both were a permanent dead stop before this fix.
public class WallContactTangentialTravelTests
{
    // Every ride reports its own numbers whether it passes or fails: the point of this fixture is a MEASUREMENT, and
    // one that is only readable when it goes red is one nobody can compare a later build against.
    readonly ITestOutputHelper _out;
    public WallContactTangentialTravelTests(ITestOutputHelper output) => _out = output;

    static MoveTuning Tuning => MoveTuning.Default;

    // ---- The fixture ----
    //
    // Ground height is a function of the distance r from a BEND CENTRE, rising inward: a gentle apron far out, a
    // slope ramping through the traction ceiling exactly at the bend radius, and past-gate ground inside it. So the
    // gate contour is a circle of that radius and the walker's along-face travel is a lap around it.
    //
    // Everything but the bend radius is held. The base height is island scale on purpose (the float rounding on
    // feetY is proportional to the world height, and this rule is decided at that scale). The slope ramps at a
    // quarter of a gradient unit per metre, which is a real bank rather than a step function, so nothing here is
    // measuring a discontinuity. And the normal is the EXACT analytic normal of this height field, so nothing here
    // is measuring a normal/height delegate disagreement either.
    readonly record struct Bank(float BendRadius)
    {
        public const float BaseHeight = 40f;          // island scale, so feetY's rounding is the island's
        public const float GateGradient = 1.1106f;    // tan(48 deg): MoveTuning.Default's gate (45) plus its band (3)
        public const float RampPerMetre = 0.25f;      // gradient gained per metre travelled inward
        public const float MinGradient = 0.36f;       // ~20 degrees, the apron the walk starts on
        public const float MaxGradient = 2.5f;        // ~68 degrees, far past anything the band holds

        const float RampInward = (MaxGradient - GateGradient) / RampPerMetre;
        const float RampOutward = (GateGradient - MinGradient) / RampPerMetre;

        public float CentreX => 0f;
        public float CentreZ => 0f;

        public float Radius(float x, float z) => MathF.Sqrt(x * x + z * z);

        /// <summary>Gradient magnitude at a radius: the traction ceiling exactly at the bend radius, steeper inward,
        /// gentler outward, flat either side of the ramp.</summary>
        public float GradientAt(float r)
        {
            float g = GateGradient + RampPerMetre * (BendRadius - r);
            return g < MinGradient ? MinGradient : g > MaxGradient ? MaxGradient : g;
        }

        /// <summary>The integral of that gradient inward from the bend radius, so the surface is continuous in its
        /// slope and the normal below is exactly its own.</summary>
        public float Height(float x, float z)
        {
            float d = BendRadius - Radius(x, z);       // metres inward of the gate contour
            if (d > RampInward) return BaseHeight + Ramp(RampInward) + MaxGradient * (d - RampInward);
            if (d < -RampOutward) return BaseHeight + Ramp(-RampOutward) + MinGradient * (d + RampOutward);
            return BaseHeight + Ramp(d);
        }

        static float Ramp(float d) => GateGradient * d + RampPerMetre * d * d * 0.5f;

        public Vector3 NormalAt(float x, float z)
        {
            float r = Radius(x, z);
            if (r < 1e-6f) return Vector3.UnitY;
            float g = GradientAt(r);
            // Height rises INWARD, so dh/dx is -g * x / r and the normal (-dh/dx, 1, -dh/dz) tilts outward.
            return Vector3.Normalize(new Vector3(g * x / r, 1f, g * z / r));
        }

        /// <summary>How far inside the gate contour one tick's PROJECTED step lands, and what that costs in height:
        /// the fixture's own statement of the ask this rule is being asked to admit, computed from the geometry and
        /// not from the engine. The projected step leaves the walker's position perpendicular to the radius at the
        /// FULL step's destination, so on a bend it is a chord aimed slightly inside the contour.</summary>
        public (float cut, float rise) ProjectedStepAsk(float tangentialStep)
        {
            float r = BendRadius;
            float rd = MathF.Sqrt(r * r + tangentialStep * tangentialStep);
            float inside = r - r * r / rd;            // the endpoint's radius is r^2 / rd
            return (inside, GradientAt(r) * inside);
        }
    }

    // ---- The ride ----

    readonly record struct Ride(float Efficiency, float Travel, int LongestStall, int StallTicks, int Airborne,
        int Flips, float EndOffset, float MaxClimb, string Measured);

    // Walk a held angle to the face for a fixed number of ticks and report what the walk looked like. The command is
    // RE-AIMED every tick to hold the same angle to the face, which is what a stick held sideways along a bank does
    // and what keeps a heading meaningful on a face that bends. It is still a pure function of position, so a
    // reconcile replay of any tick reaches the same command.
    //
    // The ride STARTS a couple of millimetres outside the gate contour, which is where a walker that has been
    // leaning on the bank for a moment already is: the approach is not what is under test, the contact is, and
    // starting a metre out spends the whole ride walking there at a rate the lean angle happens to set.
    static Ride WalkAlong(in MoveTuning t, in Bank bank, float leanDegrees, float startOutside, float dt, int ticks)
    {
        float lean = leanDegrees * MathF.PI / 180f;
        float cos = MathF.Cos(lean), sin = MathF.Sin(lean);
        float r0 = bank.BendRadius + startOutside;
        var s = new MoveState
        {
            Position = new Vector3(r0, bank.Height(r0, 0f) + t.CapsuleHalfHeight, 0f),
            Grounded = true,
        };

        float travel = 0f, startFeet = s.Position.Y - t.CapsuleHalfHeight, maxClimb = 0f;
        int longestStall = 0, stall = 0, stallTicks = 0, airborne = 0, flips = 0;
        bool previous = true;
        for (int i = 0; i < ticks; i++)
        {
            float x = s.Position.X, z = s.Position.Z;
            float r = MathF.Sqrt(x * x + z * z);
            // Outward radial (away from the face, down its fall line) and the contour tangent beside it.
            float ux = x / r, uz = z / r;
            Vector2 dir = new(-uz * cos - ux * sin, ux * cos - uz * sin);

            MoveState next = CharacterMovement.StepTowards(s, dir, run: false, dt, bank.Height, t, bank.NormalAt);
            float step = MathF.Sqrt((next.Position.X - x) * (next.Position.X - x)
                                    + (next.Position.Z - z) * (next.Position.Z - z));
            travel += step;
            if (step < 1e-4f)
            {
                stallTicks++;
                stall++;
                if (stall > longestStall) longestStall = stall;
            }
            else stall = 0;

            if (!next.Grounded) airborne++;
            if (next.Grounded != previous) flips++;
            previous = next.Grounded;
            float climb = next.Position.Y - t.CapsuleHalfHeight - startFeet;
            if (climb > maxClimb) maxClimb = climb;
            s = next;
        }

        // The commanded along-face travel: the tangential component of the held command, for the whole ride. That is
        // what the wall contact promises to leave untouched.
        float commanded = t.WalkSpeed * cos * dt * ticks;
        float endOffset = MathF.Sqrt(s.Position.X * s.Position.X + s.Position.Z * s.Position.Z) - bank.BendRadius;
        (float cut, float rise) = bank.ProjectedStepAsk(t.WalkSpeed * cos * dt);
        string measured = $"bend {bank.BendRadius:F0} m, lean {leanDegrees:F0} deg: travel {travel:F3} of a commanded "
                          + $"{commanded:F3} ({travel / commanded:P1}), longest stall {longestStall}/{ticks} ticks, "
                          + $"stalled {stallTicks}, airborne {airborne}, flips {flips}, ended {endOffset:F4} m outside "
                          + $"the contour, climbed at most {maxClimb:F4} m above its start; one projected step asks "
                          + $"for {cut:F6} m inward = {rise:F6} m of rise";
        return new Ride(travel / commanded, travel, longestStall, stallTicks, airborne, flips, endOffset, maxClimb,
            measured);
    }

    // ---- The near-planar face: the +0.000 class, which is eight of the ten measured blocks ----

    [Theory]
    [InlineData(0f)]
    [InlineData(5f)]
    [InlineData(10f)]
    [InlineData(20f)]
    [InlineData(30f)]
    public void A_near_planar_bank_keeps_its_along_face_travel(float lean)
    {
        // A 400 m bend radius: one walking step's projected endpoint lands 5e-5 m inside the contour it meant to
        // follow, which is a rise of 5.6e-5 m. That is a face no measurement a player can make calls curved, and it
        // is the class the island's blocks overwhelmingly fell in. Ten seconds at 30 Hz, exactly the reported ride.
        //
        // Lean 0 is the control: on a face that bends AWAY from the walker a purely tangential step lands slightly
        // OUTSIDE the contour, so it never meets the wall at all and must be untouched by any of this.
        var bank = new Bank(400f);
        Ride r = WalkAlong(Tuning, bank, lean, startOutside: 0.002f, dt: 1f / 30f, ticks: 300);
        _out.WriteLine(r.Measured);

        Assert.True(r.Efficiency > 0.95f, $"the wall contact ate the along-face travel: {r.Measured}");
        Assert.True(r.LongestStall < 5, $"the walk parked against the face: {r.Measured}");
        Assert.True(r.MaxClimb < ClimbBound, $"the wall contact handed over altitude: {r.Measured}");
    }

    // ---- The curved face: the class the slack alone cannot cover ----

    [Theory]
    [InlineData(5f)]
    [InlineData(10f)]
    [InlineData(20f)]
    [InlineData(30f)]
    public void A_curved_bank_never_parks_the_walker(float lean)
    {
        // An 8 m bend radius: one walking step's projected endpoint lands 2.5e-3 m inside the contour, a rise of
        // 2.8e-3 m, which is well past any float tolerance this rule could honestly carry, so the slack alone does
        // not reach it and the shortening ladder is what keeps the walk moving.
        var bank = new Bank(8f);
        Ride r = WalkAlong(Tuning, bank, lean, startOutside: 0.002f, dt: 1f / 30f, ticks: 300);
        _out.WriteLine(r.Measured);

        Assert.True(r.LongestStall < 5, $"the walk parked against the curved face: {r.Measured}");
        Assert.True(r.Efficiency > 0.95f, $"the curved face ate the along-face travel: {r.Measured}");
        Assert.True(r.MaxClimb < ClimbBound, $"the curved face handed over altitude: {r.Measured}");
        Assert.True(r.Airborne < 150, $"the curved face parked the walker in a slide: {r.Measured}");
    }

    // WHAT THE FIX DOES NOT BUY, MEASURED RATHER THAN ARGUED, AND WHY THESE RIDES DO NOT ASSERT ZERO FOOTING FLIPS.
    //
    // A walker leaning into a bank comes to rest exactly ON its traction ceiling, because that is where the wall
    // contact stops it: the ground it stands on is the steepest it can stand on. On a face that BENDS, the projected
    // step is aimed at the contour through the FULL step's destination rather than through the walker's own column,
    // so it points a hair inside the contour whatever its length - and the only endpoints available at the ceiling
    // are therefore past it. The pre-fix answer was to refuse all of them, which is the dead stop #498 reports. The
    // answer now is to commit the longest one inside the allowance, so the walker travels, and the support decision
    // at the end of that tick reads ground past its ceiling and slides it a few centimetres back down. It walks in
    // again, and the ride is a slow oscillation across the contour instead of a wall.
    //
    // Measured over these rides at 30 Hz for 10 s: 2 to 6 flips on the near-planar face, 12 to 20 on the 8 m bend,
    // with the walker keeping ALL of its commanded along-face travel. Against #486's record on the same clock - 112
    // flips and 539 airborne ticks out of 600, from a 0.4 m StepHeight seat - this is a different order of thing,
    // and the reason is the number below: the whole altitude the wall contact ever hands over across a 300-tick ride
    // is under a centimetre, where a genuine ratchet at the slack's own worst case would be 0.3 m. THAT is the
    // invariant these rides pin, and it is the one #468 and #486 are about. The flips are a consequence of standing
    // on the ceiling, not of buying height off it.
    //
    // Removing them needs the projection to read its contour at the walker's column instead of at the destination,
    // which is a change to what the wall contact resolves against rather than to what it admits, and is #502.
    const float ClimbBound = 0.02f;

    // ---- What must still be refused ----

    // A CONCAVE CREASE IS STILL A REFUSAL, and it is the case the anti-tunnel re-test was written for. Two steep
    // faces meeting at an inside corner: sliding along one runs into the other, there is genuinely nowhere to go,
    // and no shortening of the step changes that because every point along it is inside both faces.
    const float CreaseX = 5f;
    const float CreaseZ = 5f;
    static readonly float CreaseGradient = MathF.Tan(70f * MathF.PI / 180f);

    static float Crease(float x, float z)
        => Bank.BaseHeight
           + (x > CreaseX ? (x - CreaseX) * CreaseGradient : 0f)
           + (z > CreaseZ ? (z - CreaseZ) * CreaseGradient : 0f);

    static Vector3 CreaseNormal(float x, float z)
        => Vector3.Normalize(new Vector3(x > CreaseX ? -CreaseGradient : 0f, 1f, z > CreaseZ ? -CreaseGradient : 0f));

    [Fact]
    public void A_concave_crease_is_still_refused()
    {
        var t = Tuning;
        float dt = 1f / 30f;
        var s = new MoveState
        {
            Position = new Vector3(4f, Crease(4f, 4f) + t.CapsuleHalfHeight, 4f),
            Grounded = true,
        };
        var dir = Vector2.Normalize(new Vector2(1f, 1f));
        float maxX = s.Position.X, maxZ = s.Position.Z;
        int airborne = 0;
        for (int i = 0; i < 300; i++)
        {
            s = CharacterMovement.StepTowards(s, dir, run: false, dt, Crease, t, CreaseNormal);
            maxX = MathF.Max(maxX, s.Position.X);
            maxZ = MathF.Max(maxZ, s.Position.Z);
            if (!s.Grounded) airborne++;
        }

        string measured = $"concave crease: max ({maxX:F4}, {maxZ:F4}), airborne {airborne}/300";
        _out.WriteLine(measured);
        Assert.True(maxX <= CreaseX + 1e-3f, $"the crease admitted a climb in x: {measured}");
        Assert.True(maxZ <= CreaseZ + 1e-3f, $"the crease admitted a climb in z: {measured}");
        Assert.True(airborne == 0, $"the crease put the character in the air: {measured}");
    }
}
