using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Locomotion;

// A WALL CONTACT'S FACE DIRECTION IS NOT A 0.4 M FACET OF THE BANK (#501, 17.32.0). Ninth round of the steep-terrain
// chain, split out of #498 before that fix closed it, and like #475, #486 and #498 it comes from a playtest.
//
// WHAT WAS REPORTED, AND WHAT MAKES IT A DIFFERENT BUG FROM #498. Walking along a bank on Ruinborne stops the
// character dead in localised sticky PLACES. The #498 dead stop was the anti-tunnel re-test refusing a projected
// step, and it left a trace full of refusals. About fifteen percent of the dead rows in that sweep had ZERO refusals:
// the projection itself was handing the ladder almost nothing to walk. Confirmed at bank (56.7, 293.0) at 50, 40 and
// 30 degrees off the face normal, 239 to 242 dead ticks of 299 with no refusal anywhere, and that bank also blocked
// at a PURELY tangential heading, which no wall geometry should do.
//
// THE MECHANISM, MEASURED RATHER THAN ARGUED. AdvanceWallSlide derived its face as
// FaceDirection(HeightPlaneNormal(nx, nz, ...)), a central difference at the capsule radius, so on a bank whose
// micro-geometry varies at metre wavelength the direction describes a 0.4 m FACET rather than the bank. The
// along-face speed a facet leaves is the commanded speed times the sine of the angle between the command and that
// facet's outward, so it vanishes where the outward is anti-parallel to the command. That point is not a coincidence
// the walk passes through, it is a STABLE FIXED POINT: the along-face drift carries the walker toward it and the
// drift rate vanishes as it arrives. A 266-row census on the real island (19 sites, walk and run) measured the
// arrival exactly - the facet outward reads 180.000 degrees off the command with 0.002 of the speed surviving - and
// found the same column keeps 0.286 read at 1 m, 0.384 at 2 m and 0.413 at 4 m. The worst censused ride made 0.028
// of its commanded travel with 248 of 300 ticks dead, zero refusals and zero footing flips.
//
// SO THE PLAYER-FACING SIGNATURE IS EXACT, AND THIS FILE ASSERTS THAT SIGNATURE RATHER THAN A SPEED. A walker that is
// fully footed and upright, holding a direction, going nowhere. No falling pose, no slide, no stutter, no refusal.
// Which is why the assertions below are an efficiency FLOOR plus a bound on the longest unbroken run of dead ticks:
// a fix that turned the park into a slow crawl would clear a floor alone, and a fix that turned it into a jitter
// would clear a stall bound alone. Both together are the report.
//
// ---------------------------------------------------------------------------------------------------------------
// THE FIXTURE IS THE MECHANISM, AND EVERY KNOB IN IT IS THERE TO BUY ONE PROPERTY.
//
// A bank rising in +z, with its gate contour running along +x, and a seeded metre-wavelength wiggle added to the
// height as a function of x alone. That last choice is what makes the fixture honest about the mechanism: adding
// W(x) to the height leaves the bank's own steepness profile in z untouched while rotating the LOCAL contour
// direction back and forth, so the facet outward sweeps through anti-parallel to any held heading steep enough to
// reach it, and nothing else about the surface changes. The wiggle is three incommensurate sinusoids rather than one
// tone, so no stencil width can null it by landing on a half wavelength.
//
// THE NORMAL DELEGATE IS THE SAME 0.4 M CENTRAL DIFFERENCE THE HEIGHT PLANE USES, which is deliberate and is what
// Ruinborne's own GroundNormal does. #468 exists because a consumer's normal delegate and height field can describe
// DIFFERENT surfaces, and this bug is not that: the census confirmed the two agree exactly at the sticky sites. So
// the fixture removes the confound rather than reproducing it, and every reading here is about the WIDTH a single
// surface is read at.
//
// THE HEADINGS ARE HELD, NOT RE-AIMED. A player pushes a stick in a world direction and holds it, and the bank's
// macro contour is straight in plan here, so a fixed heading is a fixed angle to the bank. Lean is measured from the
// contour toward the face, so lean 75 is a command 15 degrees off the face normal.
//
// WHICH LEANS SHOW IT, AND WHY THAT BAND AND NOT THE CENSUS'S OWN. The facet's outward can only reach anti-parallel
// to a command if the wiggle tilts the local contour far enough, and the tilt needed is cot(lean): the shallower the
// heading, the rougher the bank has to be. This fixture's wiggle reaches a contour gradient of 0.448, which puts the
// attractor at leans of about 66 degrees and steeper. Ruinborne's island is rougher than that relative to its banks
// and shows the same park at 30 degrees off the normal. The amplitude here is NOT simply raised to match, and that
// is a measurement rather than a preference: past about 0.7 the wiggle stops being micro-geometry and starts being
// terrain in its own right, with past-gate facets standing across the traverse, and then the walk is stopped by the
// #498 ladder refusing real walls rather than by this bug at all. Held under that, the fixture measures one thing.
//
//     lean      command off the face normal      pre-fix efficiency      post-fix
//     68 deg    22 deg                           0.83 to 0.89            0.86 to 0.99     control, no attractor
//     75 deg    15 deg                           0.11 to 0.22            0.91 to 1.05
//     79 deg    11 deg                           0.14 to 0.29            1.04 to 1.07
//
// LEAN 68 IS THE CONTROL AND IT IS LOAD-BEARING. Its wall contacts are real and frequent, its narrow face never
// reaches anti-parallel, and it must come through the fix untouched. A change that fired the wide read on healthy
// contacts would show up here first: measured at fallback thresholds of 0.15 and 0.25 this row drops to 0.08, which
// is how those thresholds were ruled out.
//
// 120 HZ IS NOT SWEPT, and the reason is a property of the fixture rather than of the rule. The wall contact pins a
// walker where its DESTINATION is past the gate, so the walker's own footing headroom is the bank's gradient ramp
// times one step. At 120 Hz a walking step is 0.05 m, that headroom is a few centimetres, and the ride spends most
// of its ticks oscillating across the traction ceiling instead of in contact - so it measures the oscillation. The
// step lengths swept here span 0.2 m to 0.8 m, a factor of four, and the face direction is not a function of step
// length at all, which is the axis this rule actually has.
public class WallFaceAttractorTests
{
    // Every ride reports its own numbers whether it passes or fails, for the same reason the neighbouring
    // WallContactTangentialTravelTests does: the point is a MEASUREMENT, and one only readable on red is one nobody
    // can compare a later build against.
    readonly ITestOutputHelper _out;
    public WallFaceAttractorTests(ITestOutputHelper output) => _out = output;

    static MoveTuning Tuning => MoveTuning.Default;

    // ---- The bank ----

    static class Bank
    {
        public const float BaseHeight = 40f;          // island scale, so feetY's float rounding is the island's
        public const float GateGradient = 1.1106f;    // tan(48 deg): MoveTuning.Default's gate (45) plus its band (3)
        public const float RampPerMetre = 1f;         // gradient gained per metre travelled inward

        // The ramp is clamped at both ends so the surface stays finite. The lower clamp is 1.01 m outside the gate
        // contour, which is where the flat apron the rides start on begins, and the upper is 1.89 m inside it. Every
        // ride settles between 0.07 m inside the contour and 0.37 m outside, so the contact itself never happens in
        // a clamped region and the ramp under it is exactly linear.
        public const float MinGradient = 0.10f;
        public const float MaxGradient = 3.0f;

        // THE WIGGLE. Three incommensurate metre-scale wavelengths with fixed phases, so it is deterministic seeded
        // noise rather than a tone: no stencil half-width can null all three at once, which a single sinusoid would
        // let a badly chosen wide read do by accident. Amplitudes are in metres of height, and the largest contour
        // gradient the trio produces at the 0.4 m stencil is 0.448 (see the lean band in the file header).
        public static readonly float[] Amplitude = { 0.080f, 0.065f, 0.045f };
        public static readonly float[] Wavelength = { 1.3f, 1.7f, 2.3f };
        public static readonly float[] Phase = { 0.7f, 2.9f, 5.1f };

        public static float Wiggle(float x)
        {
            float w = 0f;
            for (int i = 0; i < Amplitude.Length; i++)
                w += Amplitude[i] * MathF.Sin(2f * MathF.PI * x / Wavelength[i] + Phase[i]);
            return w;
        }

        /// <summary>Gradient magnitude of the bank profile at an inward offset: the traction ceiling exactly at z = 0,
        /// steeper inward, gentler outward, flat past either clamp.</summary>
        public static float GradientAt(float z)
        {
            float g = GateGradient + RampPerMetre * z;
            return g < MinGradient ? MinGradient : g > MaxGradient ? MaxGradient : g;
        }

        /// <summary>The integral of that gradient from z = 0, so the surface is continuous in its slope.</summary>
        public static float Profile(float z)
        {
            const float zMax = (MaxGradient - GateGradient) / RampPerMetre;
            const float zMin = -(GateGradient - MinGradient) / RampPerMetre;
            if (z > zMax) return Quad(zMax) + MaxGradient * (z - zMax);
            if (z < zMin) return Quad(zMin) + MinGradient * (z - zMin);
            return Quad(z);
        }

        static float Quad(float z) => GateGradient * z + RampPerMetre * z * z * 0.5f;

        public static float Height(float x, float z) => BaseHeight + Profile(z) + Wiggle(x);

        /// <summary>A central difference of the height at a given half-width, which is what both delegates below and
        /// the stencil profile in the file header are all reading.</summary>
        public static (float gx, float gz) Gradient(float x, float z, float r)
        {
            float inv = 0.5f / r;
            return ((Height(x + r, z) - Height(x - r, z)) * inv, (Height(x, z + r) - Height(x, z - r)) * inv);
        }

        /// <summary>The ground normal, as the SAME capsule-radius central difference the engine's height plane reads
        /// (and as Ruinborne's own GroundNormal computes it). See the file header: this is not a #468 mismatched-pair
        /// fixture, and making the two delegates agree exactly is what keeps it from becoming one.</summary>
        public static Vector3 NormalAt(float x, float z)
        {
            (float gx, float gz) = Gradient(x, z, Tuning.CapsuleRadius);
            float inv = 1f / MathF.Sqrt(gx * gx + gz * gz + 1f);
            return new Vector3(-gx * inv, inv, -gz * inv);
        }
    }

    // ---- The ride ----

    // Along-face travel is the +x component of the ride and nothing else, because the bank's macro contour IS +x here
    // by construction. It is signed, so a tick pushed backwards subtracts, which matters: the attractor's near side
    // and far side push opposite ways and a path length would read both as progress.
    readonly record struct Ride(float Efficiency, int Ticks, int LongestStall, int Airborne, int Flips,
        float DeepestInside, string Measured);

    // Hold a heading for a fixed number of seconds and report what the walk looked like.
    //
    // THE RIDE STARTS IN THE APRON AND WALKS IN. The neighbouring fixture starts a couple of millimetres outside its
    // contour because its bank ramps gently enough that the walker's resting place is knowable up front. This one
    // cannot: the wall contact pins a walker where its DESTINATION is past the gate, so its resting offset is the
    // gradient ramp times one step and therefore different for every (speed, rate) pair swept. Starting out in the
    // apron lets the contact itself choose, which costs the ride its first handful of ticks at full commanded speed
    // and no more.
    static Ride WalkAlong(in MoveTuning t, float leanDegrees, bool run, float hz, float seconds)
    {
        float dt = 1f / hz;
        int ticks = (int)MathF.Round(seconds * hz);
        float speed = run ? t.RunSpeed : t.WalkSpeed;
        float lean = leanDegrees * MathF.PI / 180f;
        var dir = new Vector2(MathF.Cos(lean), MathF.Sin(lean));
        const float startX = 4f, startZ = -1.2f;
        var s = new MoveState
        {
            Position = new Vector3(startX, Bank.Height(startX, startZ) + t.CapsuleHalfHeight, startZ),
            Grounded = true,
        };

        float travel = 0f, startFeet = s.Position.Y - t.CapsuleHalfHeight, maxClimb = 0f, deepest = startZ;
        int longestStall = 0, stall = 0, stalled = 0, airborne = 0, flips = 0;
        bool previous = true;
        for (int i = 0; i < ticks; i++)
        {
            float x = s.Position.X, z = s.Position.Z;
            MoveState next = CharacterMovement.StepTowards(s, dir, run, dt, Bank.Height, t, Bank.NormalAt);
            float dx = next.Position.X - x, dz = next.Position.Z - z;
            travel += dx;
            float step = MathF.Sqrt(dx * dx + dz * dz);
            if (step < 1e-4f)
            {
                stalled++;
                stall++;
                if (stall > longestStall) longestStall = stall;
            }
            else stall = 0;

            if (!next.Grounded) airborne++;
            if (next.Grounded != previous) flips++;
            previous = next.Grounded;
            maxClimb = MathF.Max(maxClimb, next.Position.Y - t.CapsuleHalfHeight - startFeet);
            deepest = MathF.Max(deepest, next.Position.Z);
            s = next;
        }

        float commanded = speed * MathF.Cos(lean) * dt * ticks;
        string measured = $"lean {leanDegrees:F0} deg, {(run ? "run" : "walk")} at {hz:F0} Hz: along-face "
                          + $"{travel:F3} of a commanded {commanded:F3} ({travel / commanded:P1}), longest stall "
                          + $"{longestStall}/{ticks} ticks, stalled {stalled}, airborne {airborne}, flips {flips}, "
                          + $"ended ({s.Position.X:F3}, {s.Position.Z:F3}), climbed {maxClimb:F4} m above its start, "
                          + $"reached {deepest:F4} m inward of the gate contour";
        return new Ride(travel / commanded, ticks, longestStall, airborne, flips, deepest, measured);
    }

    // ---- The sweep ----

    public static IEnumerable<object[]> Rides()
    {
        foreach (float lean in new[] { 68f, 75f, 79f })
            foreach (bool run in new[] { false, true })
                foreach (float hz in new[] { 15f, 30f })
                    yield return new object[] { lean, run, hz };
    }

    // WHAT EACH CASE IS PINNED TO.
    //
    // The lower efficiency side is the bug. The UPPER side is the same residual the neighbouring fixture carries and
    // for the same reason: a walker leaning on a bank spends part of each cycle on a SLIDE tick, whose carry is the
    // fall line plus the contour, and the contour part arrives on top of the walk. So a bank-hug can travel slightly
    // faster along the face than the stick asked for. It is bounded today (measured up to 1.07) and an unbounded
    // assertion would let it grow to any size without a test noticing.
    //
    // PINNED FROM THE MEASUREMENTS OF THE RUN THAT TURNED THIS FILE GREEN, one row per (lean, speed, rate), with the
    // lower side at the measured low minus about 5 points and the upper at the measured high plus about 5:
    //
    //   lean    speed   rate     pre-fix   post-fix   longest stall (pre)   flips   airborne   deepest inside
    //   68 deg  walk    15 Hz     0.850     0.857        0/150                 2         5       +0.0515 m
    //   68 deg  walk    30 Hz     0.854     0.985        0/300                24        72       +0.0546 m
    //   68 deg  run     15 Hz     0.889     0.889        0/150                 0         0       -0.1522 m
    //   68 deg  run     30 Hz     0.833     0.861        0/300                 6        19       -0.0215 m
    //   75 deg  walk    15 Hz     0.220     0.999       98/150                 6        10       +0.0206 m
    //   75 deg  walk    30 Hz     0.220     1.045      213/300                10        32       +0.0099 m
    //   75 deg  run     15 Hz     0.109     0.938      114/150                 4         6       -0.0058 m
    //   75 deg  run     30 Hz     0.110     0.914      238/300                10        41       +0.0730 m
    //   79 deg  walk    15 Hz     0.292     1.070       91/150                 8        10       -0.0036 m
    //   79 deg  walk    30 Hz     0.294     1.035      179/300                10        27       -0.0071 m
    //   79 deg  run     15 Hz     0.144     1.059      119/150                 6        11       +0.0525 m
    //   79 deg  run     30 Hz     0.146     1.051      236/300                14        48       +0.0709 m
    //
    // The pre-fix stall column is the bug itself: on every attractor row the walk spends between two thirds and four
    // fifths of the ride not moving at all, with at most two footing flips and at most eight airborne ticks in the
    // whole of it. Fully footed, holding a direction, going nowhere.
    //
    // An unpinned combination THROWS rather than passing, so a heading added to Rides() without measuring what it
    // does cannot quietly join the sweep.
    readonly record struct Bounds(float LoEfficiency, float HiEfficiency, int MaxStall, int MaxFlips, int MaxAirborne);

    static Bounds Expected(float lean, bool run, float hz) => (lean, run, hz) switch
    {
        (68f, false, 15f) => new Bounds(0.80f, 1.05f, 8, 8, 25),
        (68f, false, 30f) => new Bounds(0.80f, 1.10f, 8, 30, 110),
        (68f, true, 15f) => new Bounds(0.80f, 1.05f, 8, 6, 15),
        (68f, true, 30f) => new Bounds(0.80f, 1.05f, 8, 12, 55),
        (75f, false, 15f) => new Bounds(0.90f, 1.10f, 8, 12, 25),
        (75f, false, 30f) => new Bounds(0.90f, 1.15f, 8, 18, 70),
        (75f, true, 15f) => new Bounds(0.85f, 1.10f, 8, 10, 20),
        (75f, true, 30f) => new Bounds(0.85f, 1.10f, 8, 18, 80),
        (79f, false, 15f) => new Bounds(0.90f, 1.20f, 8, 14, 25),
        (79f, false, 30f) => new Bounds(0.90f, 1.20f, 8, 18, 65),
        (79f, true, 15f) => new Bounds(0.90f, 1.20f, 8, 12, 25),
        (79f, true, 30f) => new Bounds(0.90f, 1.20f, 8, 22, 90),
        _ => throw new ArgumentOutOfRangeException(nameof(lean), lean,
            "no band is pinned for this (lean, speed, tick rate) - measure it before sweeping it"),
    };

    [Theory]
    [MemberData(nameof(Rides))]
    public void A_noisy_bank_traverse_is_never_parked_by_its_own_micro_geometry(float lean, bool run, float hz)
    {
        Ride r = WalkAlong(Tuning, lean, run, hz, seconds: 10f);
        _out.WriteLine(r.Measured);
        Bounds b = Expected(lean, run, hz);

        // THE TWO ASSERTIONS THAT TOGETHER ARE THE SIGNATURE. A floor alone would pass a build that crawled, and a
        // stall bound alone would pass one that jittered in place. The park this file exists to forbid fails both.
        Assert.True(r.Efficiency >= b.LoEfficiency,
            $"the wall contact's face direction ate the along-face travel: {r.Measured}");
        Assert.True(r.LongestStall <= b.MaxStall, $"the walk parked against the bank: {r.Measured}");

        // The rest is the shape of the ride, pinned so a fix that bought its travel by turning the walk into a slide
        // reds here rather than passing.
        Assert.True(r.Efficiency <= b.HiEfficiency,
            $"the bank handed the walker free along-face speed: {r.Measured}");
        Assert.True(r.Flips <= b.MaxFlips, $"the ride flickered its footing: {r.Measured}");
        Assert.True(r.Airborne <= b.MaxAirborne, $"the ride parked the walker in a slide: {r.Measured}");
        Assert.True(r.DeepestInside < InsideContourBound,
            $"the traverse carried the walker inside the gate contour: {r.Measured}");
    }

    // WHAT BOUNDS THE ALTITUDE HERE, AND WHY IT IS NOT THE NEIGHBOURING FIXTURE'S CREEP RATE. There the walker starts
    // two millimetres outside its contour and stays there, so its net height above its start IS a creep rate and can
    // be bounded as one. This ride deliberately begins in the apron and walks up a real bank to reach the contact,
    // which is half a metre of entirely legitimate climb on walkable ground, and its per-tick record advances are a
    // full uphill walking step (up to 0.35 m at a run at 15 Hz). Both quantities are dominated by the approach here,
    // so bounding either would be measuring the walk-in.
    //
    // What is NOT confounded is how far inside the gate contour the walker ever gets. The wall contact's entire job
    // on this bank is to stop it walking past that contour, so a build that bought its along-face travel by admitting
    // steps into past-gate ground shows up as a positive z and nothing else has to be disentangled from it. The
    // deepest any ride in the sweep reaches is 0.073 m inside, which is the ordinary oscillation across the ceiling
    // (walk in, lose footing, slide back out), and the bound is 0.15 m: double the measurement, still under one
    // walking step at 30 Hz, and well under the 0.4 m single-tick steep seat #486 records. The millimetre-scale steep-ground grants that #468 is really about are policed by the #486
    // and #468 fixtures on analytic steep faces, where no walkable seat can confound the reading.
    const float InsideContourBound = 0.15f;

    // ---- The stencil profile itself, measured rather than asserted from the ride ----

    // WHY THIS TEST EXISTS SEPARATELY FROM THE RIDES. The rides show that the walk no longer parks. They cannot show
    // WHY the chosen width is the right one, because a ride only ever visits the positions its own dynamics take it
    // to. This walks the whole traverse and reports the WORST face direction each stencil width produces anywhere on
    // it, which is the table at WideFaceStencilScale and the thing that has to keep holding for that constant to
    // stay justified.
    //
    // The assertion is deliberately about the SHAPE rather than the exact numbers: the capsule-width read must reach
    // zero (or the fixture has stopped reproducing the bug at all) and the shipped width must not (or the fallback
    // has a fixed point of its own and the census's warning applies to it).
    [Theory]
    [InlineData(68f)]
    [InlineData(75f)]
    [InlineData(79f)]
    public void The_capsule_width_face_has_a_zero_along_the_traverse_and_the_shipped_width_does_not(float leanDegrees)
    {
        float narrowWorst = float.MaxValue, wideWorst = float.MaxValue;
        var row = new System.Text.StringBuilder($"lean {leanDegrees:F0} deg, worst keep over 40 m of traverse:");
        foreach (float r in new[] { 0.4f, 0.8f, 1.2f, 1.6f, 2.0f, 2.4f, 3.2f, 4.0f })
        {
            float worst = float.MaxValue;
            for (float x = 0f; x <= 40f; x += 0.02f) worst = MathF.Min(worst, Keep(x, leanDegrees, r));
            row.Append($"  {r:F1} m {worst:F4}");
            if (r == Tuning.CapsuleRadius) narrowWorst = worst;
            if (r == Tuning.CapsuleRadius * ShippedWideScale) wideWorst = worst;
        }
        _out.WriteLine(row.ToString());

        Assert.True(narrowWorst < 0.01f,
            $"the capsule-width face no longer reaches anti-parallel, so this fixture has stopped reproducing #501: "
            + row);
        Assert.True(wideWorst > 0.10f, $"the shipped wide read has a near-zero of its own along the traverse: {row}");
    }

    // Mirrors CharacterMovement.WideFaceStencilScale, which is private. A test that silently tracked the engine could
    // not catch the constant moving, so it is restated here on purpose and the sweep above is what pins the choice.
    const float ShippedWideScale = 5f;

    /// <summary>The fraction of the commanded speed a wall contact at the bank's gate contour would keep, projecting
    /// onto the face read at half-width <paramref name="r"/>. Computed from the geometry rather than from the engine,
    /// so it is a statement about the surface and not a re-derivation of the rule under test.</summary>
    static float Keep(float x, float leanDegrees, float r)
    {
        // The z where the capsule-width read puts this column exactly on the traction ceiling, which is where a wall
        // contact leaves a walker: the wiggle's x gradient is fixed at this column, so the bank gradient has to make
        // up the rest of the gate.
        (float wiggleGx, _) = Bank.Gradient(x, 0f, Tuning.CapsuleRadius);
        float remaining = Bank.GateGradient * Bank.GateGradient - wiggleGx * wiggleGx;
        if (remaining < 0.01f) remaining = 0.01f;
        float z = (MathF.Sqrt(remaining) - Bank.GateGradient) / Bank.RampPerMetre;

        (float gx, float gz) = Bank.Gradient(x, z, r);
        float m = MathF.Sqrt(gx * gx + gz * gz);
        if (m < 1e-9f) return 1f;
        float lean = leanDegrees * MathF.PI / 180f;
        float vx = MathF.Cos(lean), vz = MathF.Sin(lean);
        // The face outward is -(gx, gz)/m, and what a projection keeps is the magnitude of the 2D cross product of
        // the unit command with it.
        return MathF.Abs(vx * (-gz / m) - vz * (-gx / m));
    }
}
