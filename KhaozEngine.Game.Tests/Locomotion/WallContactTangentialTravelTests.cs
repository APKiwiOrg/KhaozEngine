using System;
using System.Collections.Generic;
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
//
// ---------------------------------------------------------------------------------------------------------------
// WHAT THE LADDER COVERS, STATED AS TERRAIN, BECAUSE THE STEP LENGTH IS NOT A CONSTANT OF THE ENGINE.
//
// The ask a bend makes of one tick is G * L^2 / (2R): G the gradient at the gate contour, L = speed * dt the step
// length in metres, R the bend radius in plan. It is QUADRATIC in the step, so it is a different rule at run speed
// than at walk speed and a different rule again at 15 Hz than at 120 - which is exactly what a fixture that swept
// neither could not see. Shortening the step to a fraction k does NOT divide the ask by k, because the step line is
// tangent at the FULL destination rather than at the shortened one: it leaves k(2-k) of it, so the sixty-fourth rung
// still asks about a thirty-second (0.031) of the full ask rather than a sixty-fourth of it.
//
// So the smallest bend a step of length L holds its full travel through is the R that puts 0.031 * G * L^2 / (2R)
// under the 1 mm slack, which at MoveTuning.Default's 48 degree grounded gate is R > 17.2 * L^2 metres:
//
//     step L        walk 6 m/s          run 12 m/s           smallest bend held
//     0.05 m        120 Hz                                   0.04 m
//     0.10 m                            120 Hz               0.17 m
//     0.20 m        30 Hz                                    0.7 m
//     0.40 m        15 Hz               30 Hz                2.8 m
//     0.80 m                            15 Hz                11.0 m
//
// APPROACHING THE CEILING DEGRADES IN HALVINGS, AND PAST IT THE WALK PARKS. Between the rungs the tick commits
// L/2^n for the first rung that clears, so the walk shortens rather than stopping: measured on the 8 m bend, 98 to
// 102 percent of the commanded along-face travel at walk and 30 Hz, and 86 to 94 percent once the step doubles.
// PAST the floor rung every rung is refused, the tick commits nothing, and whether that reads as a crawl or a dead
// stop is decided by the support decision rather than by the ladder. On this fixture it is a dead stop: the walker
// comes to rest a hair OUTSIDE the gate contour, where the ground is still inside its own traction ceiling, so
// nothing takes its footing, nothing slides it back, the geometry under it never changes, and the next tick refuses
// the same move for the same reason forever. THE 8 M BEND AT RUN SPEED AND 15 HZ IS THE ONE CASE IN THIS FIXTURE
// ON THAT SIDE OF THE LINE (L = 0.80 m needs an 11 m bend), it is swept on purpose so the ceiling is MEASURED
// rather than asserted, and it is pinned to the park it produces - 149 of 150 ticks stalled, 12 to 23 mm of travel
// in ten seconds - so that a change which lifts it out of the park reds this file and sends the next author back to
// this table. 15 Hz is under any tick rate the fleet ships, and at 30 Hz the same run needs only a 2.8 m bend, so the
// shipped configurations are comfortably inside the ceiling. Moving it further out is #502's job rather than a
// deeper ladder's.
//
// A genuine REFUSAL - every rung on ground that both stands past the gate and rises past the allowance - needs an
// ask the shortest rung cannot get under, and on a smooth face that means a bend radius of about a centimetre,
// which is a crease rather than a bend. That is the shape A_rising_gully_crease_is_refused builds, and it is the
// only geometry in this file where the last line of AdvanceWallSlide's ladder executes at all.
//
// The three rungs this ladder shipped with covered R > 130 * L^2 (a 5.2 m bend at walk and 30 Hz, but a 21 m bend
// at run), which is ordinary gate-contour terrain, and that is why the floor is a sixty-fourth and not an eighth.
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
        /// FULL step's destination, so on a bend it is a chord aimed slightly inside the contour.
        /// <para>WRITTEN AS s^2 / (rd + r) RATHER THAN r - r^2 / rd, WHICH IS THE SAME NUMBER AND LOSES IT. At a
        /// 400 m radius the direct form subtracts two floats that agree to seven digits, so what survives is a
        /// couple of ULPs of 400 (3e-5 m) and the printed ask quantises to multiples of that: it read 6.1e-5 m for
        /// a 0.20 m step and 3.1e-5 m for a 0.17 m one, up to 37 percent off the true value, which is not a
        /// quantity anybody can compare a later build against.</para></summary>
        public (float cut, float rise) ProjectedStepAsk(float tangentialStep)
        {
            float r = BendRadius;
            float s = tangentialStep;
            float rd = MathF.Sqrt(r * r + s * s);
            float inside = r * (s * s / (rd + r)) / rd;   // = r - r^2 / rd, without the cancellation
            return (inside, GradientAt(r) * inside);
        }
    }

    // ---- The ride ----

    // Efficiency is the TANGENTIAL projection of the ride, never its path length. A walker leaning on a bank
    // oscillates across the contour (see the residual note below), and a path length sums |delta p| over that
    // oscillation, so it reads the radial wobble as along-face progress: measured 0.8 to 2.4 points of overstatement
    // across these rides, all of it in the walker's favour. Projecting each tick's displacement on the contour
    // tangent at the position it STARTED from counts only what the ride actually made along the face, and it is
    // signed, so a tick that is pushed backwards subtracts. Both numbers are printed, so the wobble stays visible.
    readonly record struct Ride(float Efficiency, int Ticks, int LongestStall, int Airborne, int Flips,
        float MaxClimb, string Measured);

    // Walk a held angle to the face for a fixed number of seconds and report what the walk looked like. The command
    // is RE-AIMED every tick to hold the same angle to the face, which is what a stick held sideways along a bank
    // does and what keeps a heading meaningful on a face that bends. It is still a pure function of position, so a
    // reconcile replay of any tick reaches the same command.
    //
    // The ride STARTS a couple of millimetres outside the gate contour, which is where a walker that has been
    // leaning on the bank for a moment already is: the approach is not what is under test, the contact is, and
    // starting a metre out spends the whole ride walking there at a rate the lean angle happens to set.
    static Ride WalkAlong(in MoveTuning t, in Bank bank, float leanDegrees, bool run, float hz, float seconds,
        float startOutside)
    {
        float dt = 1f / hz;
        int ticks = (int)MathF.Round(seconds * hz);
        float speed = run ? t.RunSpeed : t.WalkSpeed;
        float lean = leanDegrees * MathF.PI / 180f;
        float cos = MathF.Cos(lean), sin = MathF.Sin(lean);
        float r0 = bank.BendRadius + startOutside;
        var s = new MoveState
        {
            Position = new Vector3(r0, bank.Height(r0, 0f) + t.CapsuleHalfHeight, 0f),
            Grounded = true,
        };

        float tangential = 0f, path = 0f, startFeet = s.Position.Y - t.CapsuleHalfHeight, maxClimb = 0f;
        int longestStall = 0, stall = 0, stallTicks = 0, airborne = 0, flips = 0;
        bool previous = true;
        for (int i = 0; i < ticks; i++)
        {
            float x = s.Position.X, z = s.Position.Z;
            float r = MathF.Sqrt(x * x + z * z);
            // Outward radial (away from the face, down its fall line) and the contour tangent beside it.
            float ux = x / r, uz = z / r;
            float tx = -uz, tz = ux;
            Vector2 dir = new(tx * cos - ux * sin, tz * cos - uz * sin);

            MoveState next = CharacterMovement.StepTowards(s, dir, run, dt, bank.Height, t, bank.NormalAt);
            float dx = next.Position.X - x, dz = next.Position.Z - z;
            tangential += dx * tx + dz * tz;
            float step = MathF.Sqrt(dx * dx + dz * dz);
            path += step;
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
        float commanded = speed * cos * dt * ticks;
        float endOffset = MathF.Sqrt(s.Position.X * s.Position.X + s.Position.Z * s.Position.Z) - bank.BendRadius;
        (float cut, float rise) = bank.ProjectedStepAsk(speed * cos * dt);
        string measured = $"bend {bank.BendRadius:F0} m, {(run ? "run" : "walk")} at {hz:F0} Hz, lean "
                          + $"{leanDegrees:F0} deg: along-face {tangential:F3} of a commanded {commanded:F3} "
                          + $"({tangential / commanded:P1}), path {path:F3}, longest stall {longestStall}/{ticks} "
                          + $"ticks, stalled {stallTicks}, airborne {airborne}, flips {flips}, ended "
                          + $"{endOffset:F4} m outside the contour, climbed at most {maxClimb:F4} m above its "
                          + $"start. One projected step asks for {cut:F6} m inward = {rise:F6} m of rise";
        return new Ride(tangential / commanded, ticks, longestStall, airborne, flips, maxClimb, measured);
    }

    // ---- The sweep: lean x speed x tick rate, on both bend classes ----
    //
    // The step length L = speed * dt is the knob the ask is quadratic in, so a sweep that held it fixed (which is
    // what walk-only at 30 Hz did) measured one point of a curve and reported it as the rule. These six (speed,
    // rate) pairs span L from 0.05 m to 0.80 m, a factor of sixteen in step length and so a factor of 256 in the ask.
    public static IEnumerable<object[]> Rides()
    {
        foreach (float lean in new[] { 0f, 5f, 10f, 20f, 30f })
            foreach (bool run in new[] { false, true })
                foreach (float hz in new[] { 15f, 30f, 120f })
                    yield return new object[] { lean, run, hz };
    }

    // WHAT EACH CASE IS PINNED TO, AND WHY THE EFFICIENCY BAND HAS AN UPPER SIDE.
    //
    // The lower side is the bug: the wall contact must not eat the along-face travel. The UPPER side is a residual
    // this fix introduced and #502 will remove. A walker that gets slid back down the bank and walks in again spends
    // part of each cycle on a SLIDE tick, whose carry is the fall line plus the contour, and the contour part is
    // handed over on top of the walk - so a bank-hug can travel FASTER along the face than the stick asked for.
    // Measured at up to 127 percent on the near-planar bend at 120 Hz, and it did not exist before this change (the
    // pre-fix ride never got a slide tick because it never moved). That is a real handover of speed the player did
    // not ask for, it is bounded today, and an unbounded assertion would let it grow to any size at all without a
    // test noticing. So every case carries a two-sided band pinned from its own measurement with modest headroom,
    // and a ride at two hundred percent reds this file.
    //
    // MaxStall / MaxFlips / MaxAirborne are pinned the same way and for the same reason: the flips and the airborne
    // ticks are the visible face of that same oscillation (see the residual note under ClimbBound), and a fixture
    // that only PRINTED them could not tell a 6-flip ride from a 60-flip one.
    readonly record struct Bounds(float LoEfficiency, float HiEfficiency, int MaxStall, int MaxFlips, int MaxAirborne);

    // Lean 0 is the control on both faces and it is pinned far harder than any contact case can be: a purely
    // tangential step on a face that bends AWAY from the walker lands slightly OUTSIDE the contour, so it never
    // meets the wall at all and must come through untouched - full travel, no stall, no flip, no airborne tick.
    static Bounds Control => new(0.99f, 1.01f, 0, 0, 0);

    // PINNED FROM THE MEASUREMENTS OF THE RUN THAT TURNED THIS FILE GREEN, one row per (bend class, speed, rate).
    // The efficiency band is the measured spread over leans 5 to 30 with about 5 points of headroom either side,
    // except that the upper bound never sits below 1.05: recovering travel must never red this file, only handing
    // over MORE than the residual measured here may. The counters are the measured worst case over those leans with
    // headroom, and the flip bounds are deliberately tight enough that a 50-flip ride reds every row.
    //
    //   bend    speed   rate     measured efficiency   flips   airborne   worst climb
    //   400 m   walk    15 Hz    103.5 - 109.9 %         6        15       0.0024 m
    //   400 m   walk    30 Hz    104.0 - 112.9 %         6        39       0.0088 m
    //   400 m   walk   120 Hz    104.1 - 109.4 %         4       114       0.0345 m
    //   400 m   run     15 Hz    104.6 - 158.4 %        25       100       0.0270 m
    //   400 m   run     30 Hz    104.6 - 125.8 %        12        78       0.0855 m
    //   400 m   run    120 Hz    104.6 - 115.0 %         6       204       0.1527 m
    //     8 m   walk    15 Hz     85.8 -  93.6 %        20        40       0.0029 m
    //     8 m   walk    30 Hz     98.3 - 102.3 %        20       100       0.0031 m
    //     8 m   walk   120 Hz    120.6 - 128.1 %        20       390       0.0253 m
    //     8 m   run     15 Hz      0.0 -   0.0 %         0         0       0.0022 m   past the ceiling, it parks
    //     8 m   run     30 Hz     87.8 -  92.6 %        31       127       0.0029 m
    //     8 m   run    120 Hz    122.8 - 133.8 %        42       493       0.0574 m
    //
    // An unpinned combination THROWS rather than passing: adding a rate to Rides() without measuring what it does
    // is how a sweep grows cases nobody ever looked at.
    static Bounds Expected(float bendRadius, float lean, bool run, float hz)
    {
        if (lean == 0f) return Control;
        return (bendRadius < 100f, run, hz) switch
        {
            (false, false, 15f) => new Bounds(0.95f, 1.15f, 5, 10, 30),
            (false, false, 30f) => new Bounds(0.95f, 1.18f, 5, 10, 60),
            (false, false, 120f) => new Bounds(0.95f, 1.15f, 5, 8, 200),
            (false, true, 15f) => new Bounds(0.95f, 1.65f, 5, 32, 150),
            (false, true, 30f) => new Bounds(0.95f, 1.31f, 5, 18, 120),
            (false, true, 120f) => new Bounds(0.95f, 1.20f, 5, 10, 320),
            (true, false, 15f) => new Bounds(0.80f, 1.05f, 5, 28, 70),
            (true, false, 30f) => new Bounds(0.93f, 1.08f, 5, 28, 150),
            (true, false, 120f) => new Bounds(0.95f, 1.34f, 5, 28, 550),
            // Past the ladder's coverage ceiling: an 8 m bend needs an 11 m one at a 0.80 m step. Every rung is
            // refused and the walker never leaves the spot it stopped on, which is the #498 dead stop itself at a
            // bend an order of magnitude tighter. Pinned to the park rather than left open, so the day it moves,
            // this file says so.
            (true, true, 15f) => new Bounds(0f, 0.05f, 150, 4, 5),
            (true, true, 30f) => new Bounds(0.82f, 1.05f, 5, 40, 180),
            (true, true, 120f) => new Bounds(0.95f, 1.39f, 5, 48, 650),
            _ => throw new ArgumentOutOfRangeException(nameof(hz), hz,
                "no band is pinned for this (bend class, speed, tick rate) - measure it before sweeping it"),
        };
    }

    // ---- The near-planar face: the +0.000 class, which is eight of the ten measured blocks ----

    [Theory]
    [MemberData(nameof(Rides))]
    public void A_near_planar_bank_keeps_its_along_face_travel(float lean, bool run, float hz)
    {
        // A 400 m bend radius: one walking step's projected endpoint lands 5e-5 m inside the contour it meant to
        // follow, which is a rise of 5.6e-5 m. That is a face no measurement a player can make calls curved, and it
        // is the class the island's blocks overwhelmingly fell in. Ten seconds, exactly the reported ride.
        //
        // Lean 0 is the control: on a face that bends AWAY from the walker a purely tangential step lands slightly
        // OUTSIDE the contour, so it never meets the wall at all and must be untouched by any of this.
        var bank = new Bank(400f);
        Ride r = WalkAlong(Tuning, bank, lean, run, hz, seconds: 10f, startOutside: 0.002f);
        _out.WriteLine(r.Measured);
        Check(r, Expected(bank.BendRadius, lean, run, hz));
    }

    // ---- The curved face: the class the slack alone cannot cover ----

    [Theory]
    [MemberData(nameof(Rides))]
    public void A_curved_bank_never_parks_the_walker(float lean, bool run, float hz)
    {
        // An 8 m bend radius: one walking step's projected endpoint lands 2.5e-3 m inside the contour, a rise of
        // 2.8e-3 m, which is well past any float tolerance this rule could honestly carry, so the slack alone does
        // not reach it and the shortening ladder is what keeps the walk moving. At run speed and 15 Hz the step is
        // long enough that even the sixty-fourth rung cannot get under the slack - see Expected and the ceiling
        // table in the file header.
        var bank = new Bank(8f);
        Ride r = WalkAlong(Tuning, bank, lean, run, hz, seconds: 10f, startOutside: 0.002f);
        _out.WriteLine(r.Measured);
        Check(r, Expected(bank.BendRadius, lean, run, hz));
    }

    static void Check(in Ride r, in Bounds b)
    {
        Assert.True(r.Efficiency >= b.LoEfficiency, $"the wall contact ate the along-face travel: {r.Measured}");
        Assert.True(r.Efficiency <= b.HiEfficiency, $"the bank handed the walker free along-face speed: {r.Measured}");
        Assert.True(r.LongestStall <= b.MaxStall, $"the walk parked against the face: {r.Measured}");
        Assert.True(r.Flips <= b.MaxFlips, $"the ride flickered its footing: {r.Measured}");
        Assert.True(r.Airborne <= b.MaxAirborne, $"the ride parked the walker in a slide: {r.Measured}");
        Assert.True(r.MaxClimb < ClimbPerTick * r.Ticks, $"the wall contact handed over altitude: {r.Measured}");
    }

    // WHAT THE FIX DOES NOT BUY, MEASURED RATHER THAN ARGUED, AND WHY THESE RIDES ALLOW FOOTING FLIPS AT ALL.
    //
    // A walker leaning into a bank comes to rest exactly ON its traction ceiling, because that is where the wall
    // contact stops it: the ground it stands on is the steepest it can stand on. On a face that BENDS, the projected
    // step is aimed at the contour through the FULL step's destination rather than through the walker's own column,
    // so it points a hair inside the contour whatever its length - and the only endpoints available at the ceiling
    // are therefore past it. The pre-fix answer was to refuse all of them, which is the dead stop #498 reports. The
    // answer now is to commit the longest one inside the allowance, so the walker travels, and the support decision
    // at the end of that tick reads ground past its ceiling and slides it a few centimetres back down. It walks in
    // again, and the ride is a slow oscillation across the contour instead of a wall. The along-face speed that
    // oscillation hands over is the upper half of the efficiency band above.
    //
    // THE CLIMB BOUND IS A RATE PER TICK, NOT A DISTANCE PER RIDE, AND THAT IS A CORRECTION. This file first pinned
    // a flat 0.02 m across a ride, which is true of a walk at 30 Hz and measurably false of a run at 120 Hz: the
    // same rides climb 0.085 m and 0.153 m. The reason is that the thing being bounded is a per-tick allowance (the
    // 1 mm ProjectedRiseSlack), so its worst case scales with the tick COUNT exactly as the slack's own comment
    // says, and a flat metre-bound silently tightens as the rate rises until it is measuring the clock. Bounded as a
    // rate, every ride in the sweep creeps at most 2.9e-4 m a tick, which is under a third of one slack, and this
    // bound is 4e-4. For scale, a genuine ratchet running at the slack's full rate would be 0.3 m over a 300-tick
    // ride, and #486's 0.4 m StepHeight seat bought that in a single tick.
    //
    // Removing the oscillation needs the projection to read its contour at the walker's column instead of at the
    // destination, which is a change to what the wall contact resolves against rather than to what it admits, and
    // is #502. The creep that survives inside this bound is tracked there with it.
    const float ClimbPerTick = 4e-4f;

    // ---- What must still be refused ----

    // TWO CREASES RIDE HERE, AND THEY ARE NOT THE SAME TEST.
    //
    // The symmetric one below is a CONTAINMENT test and nothing more, which is worth saying because this file used
    // to claim it covered the refusal. It does not, and that was checked by deleting the refusal line: the walk
    // drives straight into the corner, the destination's height plane is the exact bisector, so the projection
    // removes the WHOLE velocity and every rung lands on the walker's own column - which is flat ground it is
    // standing on. Rung 0 leaves through the not-steep branch, and the last line of the ladder is never reached.
    // The containment is real and worth pinning. The mechanism is the projection collapsing to zero, not the ladder.
    //
    // The RISING GULLY after it is the refusal test. Getting the last line to execute needs a projection that is
    // non-zero AND lands on ground that is past the gate and rises past the allowance at every rung down to a
    // sixty-fourth of the step - which on a smooth face means a bend of about a centimetre, so the shape has to be
    // an actual crease with the along-face direction running into it.
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
    public void A_symmetric_concave_crease_contains_the_walker()
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

        string measured = $"symmetric crease: max ({maxX:F4}, {maxZ:F4}), airborne {airborne}/300";
        _out.WriteLine(measured);
        Assert.True(maxX <= CreaseX + 1e-3f, $"the crease admitted a climb in x: {measured}");
        Assert.True(maxZ <= CreaseZ + 1e-3f, $"the crease admitted a climb in z: {measured}");
        Assert.True(airborne == 0, $"the crease put the character in the air: {measured}");
    }

    // THE RISING GULLY: THE ONE GEOMETRY IN THIS FILE WHERE THE LAST LINE OF THE LADDER RUNS.
    //
    // A V-gully whose floor climbs at a WALKABLE grade (17 degrees, so the walker keeps its footing on the axis and
    // the case does not decay into a slide after one tick) and whose walls are far past the gate (76 degrees). The
    // walker stands on the axis and walks up the gully leaning three degrees into one wall.
    //
    // WHY EVERY RUNG IS REFUSED, from the geometry rather than from the engine. The destination is on the near wall,
    // so it is past the gate and stands 9 cm above the feet: a wall contact. The height plane there is read over a
    // capsule-radius stencil that STRADDLES the axis, so its z-gradient is small and its face direction is mostly
    // down-gully - which means the projection removes almost all of the up-gully velocity and what survives points
    // ACROSS the axis onto the FAR wall. Every rung therefore lands on 76 degree ground that rises, and the shortest
    // of them still rises 3.4 mm, which is three and a half times the slack. There is no shortening that gets under
    // it, because the wall it is running into is not a bend the step can undercut - it is the crease itself.
    //
    // WHAT THE ASSERTIONS PIN, and each of them fails against a build whose last rung commits unconditionally: the
    // walker never gains altitude (it cannot, since it never moves), never advances up the gully, and never loses
    // its footing. That mutant commits a 0.3 mm step onto ground 3.4 mm up the far wall, the clamp seats the capsule
    // there, and the support decision then takes its footing - which is the exact #468 shape this refusal exists to
    // forbid, three orders of magnitude smaller than the sea cliff it was found on.
    const float GullyFloorGrade = 0.30f;    // ~17 degrees along the axis: walkable, so the walker keeps its footing
    const float GullyWallGrade = 4.0f;      // ~76 degrees away from the axis: far past the gate, either side

    static float Gully(float x, float z)
        => Bank.BaseHeight + GullyFloorGrade * x + GullyWallGrade * MathF.Abs(z);

    static Vector3 GullyNormal(float x, float z)
        => Vector3.Normalize(new Vector3(-GullyFloorGrade, 1f,
            z > 0f ? -GullyWallGrade : z < 0f ? GullyWallGrade : 0f));

    [Fact]
    public void A_rising_gully_crease_is_refused()
    {
        var t = Tuning;
        float dt = 1f / 30f;
        const float startX = 4f;
        var s = new MoveState
        {
            Position = new Vector3(startX, Gully(startX, 0f) + t.CapsuleHalfHeight, 0f),
            Grounded = true,
        };
        float lean = 3f * MathF.PI / 180f;
        var dir = new Vector2(MathF.Cos(lean), MathF.Sin(lean));

        float startFeet = s.Position.Y - t.CapsuleHalfHeight, maxClimb = 0f, maxX = s.Position.X;
        int airborne = 0;
        for (int i = 0; i < 300; i++)
        {
            s = CharacterMovement.StepTowards(s, dir, run: false, dt, Gully, t, GullyNormal);
            maxX = MathF.Max(maxX, s.Position.X);
            maxClimb = MathF.Max(maxClimb, s.Position.Y - t.CapsuleHalfHeight - startFeet);
            if (!s.Grounded) airborne++;
        }

        string measured = $"rising gully: advanced {maxX - startX:F6} m up the gully, climbed at most "
                          + $"{maxClimb:F6} m, airborne {airborne}/300, ended at "
                          + $"({s.Position.X:F4}, {s.Position.Z:F4})";
        _out.WriteLine(measured);
        Assert.True(maxClimb < 1e-3f, $"the gully crease handed over altitude: {measured}");
        Assert.True(maxX - startX < 1e-4f, $"the gully crease admitted a climb up the axis: {measured}");
        Assert.True(airborne == 0, $"the gully crease put the character in the air: {measured}");
    }
}
