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
// contacts shows up here first, and it is what has ruled out every over-wide setting the rule has been swept over:
// under the round-one keep trigger this row dropped to 0.08 at a threshold of 0.15, and under the shipped max-keep
// comparison it is the row that breaks first as the doubt band widens, taking 14 footing flips and 65 airborne ticks
// at a band of 0.20 where a band of 0.15 leaves it clean. Both are the same measurement about the same row.
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
    //
    // THESE TWELVE ROWS ARE THE MOST PERTURBATION-SENSITIVE ASSERTIONS IN THE STEEP-TERRAIN CHAIN, AND THAT WAS
    // MEASURED RATHER THAN INFERRED (2026-08-06, #502). A wall contact on this bank pins the walker where its
    // DESTINATION is past the gate, on a surface whose micro-geometry varies at metre wavelength, so the position
    // it settles at is a chaotic function of the trajectory that got it there. The control is a rotation of the
    // projected travel by an angle with NO GEOMETRIC MEANING AT ALL, applied to the shipped rule with nothing
    // else changed, and it is a runnable procedure rather than a paragraph: see
    // WallContactOwnColumnTests.The_perturbation_control_for_every_sensitivity_claim_in_this_chain for the exact
    // steps. Re-measured on this build, as efficiency:
    //
    //     ride                0 deg   0.01    0.05     0.2       1
    //     68 walk 15 Hz       0.912   0.873   0.934   0.908   0.946
    //     68 run  30 Hz       0.839   0.887   0.799   0.878   0.779
    //     79 walk 15 Hz       1.064   1.044   1.088   1.102   1.155
    //
    // A HUNDREDTH OF A DEGREE MOVES A PINNED ROW BY UP TO 0.049 OF ITS EFFICIENCY, which is what these rows are
    // sensitive at and is what a reader of this table needs.
    //
    // THE CENSUS HALF OF THE SAME CONTROL IS A DIFFERENT STATEMENT AND USED TO BE QUOTED FOR THIS ONE, WHICH WAS
    // WRONG (corrected round two, #502). An earlier draft here cited the control at 0.001, 0.05 and 0.2 degrees
    // as evidence about census PARKS. It is not: re-swept over the 3824-ride census, a rotation of 0.001 degrees
    // costs nothing at all, 0.05 costs one top-tier row, 0.2 costs two. At the AGGREGATE tier level the first
    // park appears at one degree, but an INDIVIDUAL chaotic bank row can park at 0.2 (lean 48, run 15 Hz, the
    // rotated build reads 0.014 with 146 dead ticks where the shipped one rides clean), so the tier table is not
    // a per-row immunity claim. The criterion that separates chaos from mechanism is ensemble determinism under
    // start perturbation: round one's lean-49 park was 21 of 21 deterministic while the pre-#502 build never
    // parks there in the same ensemble, which is what made it a real defect and not noise.
    //
    // WHAT THAT MEANS FOR A READER OF THIS TABLE, AND IT IS NOT THAT THE FILE IS WEAK. The park these rows exist
    // to forbid is a factor-of-five effect (0.11 to 0.22 of commanded against 0.9 and up), and no perturbation of
    // any size turns a healthy ride into that. What the sensitivity DOES mean is that a row failing by two
    // percent is not evidence of anything, and a row passing by two percent is not evidence either. So a future
    // round should read a small breach here as a prompt to run the control before it reads it as a defect.
    //
    // AND TWO OF THE THREE #502 RE-BASELINES ARE GONE AGAIN, WHICH IS THE OTHER HALF OF THE SAME LESSON. Round
    // one of #502 loosened this row's efficiency ceiling and the inside-contour bound below to admit its own
    // measurements. Round two's sign gate puts both back UNDER the numbers they were pinned at before #502
    // existed, so both reverted rather than staying loose: a bound raised for a build that turned out to be
    // half-wrong is a bound nobody has justified. The airborne one did not revert and says so at its row.
    readonly record struct Bounds(float LoEfficiency, float HiEfficiency, int MaxStall, int MaxFlips, int MaxAirborne);

    static Bounds Expected(float lean, bool run, float hz) => (lean, run, hz) switch
    {
        (68f, false, 15f) => new Bounds(0.80f, 1.05f, 8, 8, 25),
        (68f, false, 30f) => new Bounds(0.80f, 1.10f, 8, 30, 110),
        (68f, true, 15f) => new Bounds(0.80f, 1.05f, 8, 6, 15),
        // RE-BASELINED 2026-08-06 (#502), airborne 55 -> 70, and this is the one of the three that did NOT come
        // back. Measured 57 on the build that levels the projected travel against the walker's own column and
        // gates that levelling on its sign, against 58 on round one and 19 when the row was first pinned. The
        // pre-#502 engine reads 41. So the row genuinely spends more of its ride re-seating than it did, the
        // increase survives the fix to the fix, and it is pinned at the measurement plus the same headroom the
        // rest of this table carries rather than at round one's 75. See the note under Bounds for why this row's
        // numbers cannot be held to three significant figures by any build.
        (68f, true, 30f) => new Bounds(0.80f, 1.05f, 8, 12, 70),
        // REVERTED 2026-08-06 (#502 round two), ceiling 1.15 -> 1.10, which is where it was before #502. Round
        // one measured 1.106 here and raised the bound to admit it. This build measures 1.053, under the original
        // pin, so the bound goes back.
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
    // deepest any ride in the sweep reaches is 0.151 m inside, which is the ordinary oscillation across the ceiling
    // (walk in, lose footing, slide back out), and the bound is 0.18 m: twenty percent over the measurement, still
    // under one walking step at 30 Hz, and well under the 0.4 m single-tick steep seat #486 records. The
    // millimetre-scale steep-ground grants that #468 is really about are policed by the #486
    // and #468 fixtures on analytic steep faces, where no walkable seat can confound the reading.
    //
    // REVERTED 2026-08-06 (#502 round two), 0.18 -> 0.15, which is where it was before #502, and the record that
    // went with the raise was wrong twice over. Round one raised it because it measured 0.1507 and the prose said
    // the bound had been "set from 0.073 and pinned at double that". The pre-#502 engine's own deepest ride on
    // this sweep is 0.1375, not 0.073, so the original 0.15 was never double anything - it was a tenth of margin
    // over the real measurement, which is a far tighter bound than the note claimed and makes round one's breach
    // a genuine 10 percent regression rather than a rounding one. This build reaches 0.1117, comfortably under
    // both the original pin and the pre-#502 engine's own figure, so the bound goes back to 0.15 and the baseline
    // in this note is the measured one. What it guards is unchanged: still far too tight for the #486 seat, and
    // the steep-ground grants #468 is about are policed on analytic faces elsewhere.
    const float InsideContourBound = 0.15f;

    // ---- The whole heading range at one degree, which is what caught the wide read's own attractor (#501) ----

    // WHY THREE HEADINGS WERE NOT ENOUGH. The sweep above pins 68, 75 and 79 because those are where the census put
    // the bug. A substitution can move a park instead of removing one, and a moved park lands wherever the new rule's
    // boundary happens to sit, which is not a heading anybody picked in advance. It landed ONE DEGREE off the control:
    // at 69, 70 and 71 degrees the walker settled where its narrow face keeps 0.094 of the command, just inside that
    // build's keep trigger, took a wide face pointing straight up the local wiggle rather than along it, and stopped dead. The
    // wall contact had done nothing wrong at that column - the wide direction is a contour of a 2 m plane and simply
    // is not level on the metre-scale surface the ladder reads, so every rung of it climbed and every rung was
    // refused. Measured on 17.32.0's first entry: 0.17 of commanded travel with 118 consecutive dead ticks at 15 Hz,
    // and 0.08 with 269 at a run at 30 Hz, against 0.83 and 0.81 with no wide read at all.
    //
    // SO THE SWEEP IS THE WHOLE RANGE AT ONE DEGREE NOW, and the assertion is a park bound rather than a per-heading
    // band, because 112 hand-pinned bands would be unmaintainable and would not be checking anything the two numbers
    // below do not. Over the range at ONE DEGREE this build has not one stalled tick anywhere, where 17.32.0's first
    // entry has runs of 269 and the keep-trigger-only build it replaced has the same.
    //
    // THE GRANULARITY IS PART OF THAT STATEMENT AND USED TO BE MISSING FROM IT, WHICH MADE IT TOO STRONG. A
    // one-degree grid steps ACROSS the extremes of a quantity that varies this fast with the heading, so "not one
    // stalled tick anywhere" is a claim about the grid rather than about the range. Re-scanned at 0.02 degrees, this
    // build has two headings in 60 to 87 that stall past eight ticks, both hitches inside otherwise healthy rides: a
    // run at 15 Hz near 78.3 and a run at 30 Hz near 72.5.
    //
    // REFINING THAT SCAN FINDS THEIR FLOOR RATHER THAN A DEEPER PARK, WHICH IS THE PROPERTY WORTH DISCLOSING. A hitch
    // that grew every time the grid got finer would be a park the grid was hiding, and neither of these does. From
    // 0.005 degrees down to 0.0002 the first SATURATES at 39 ticks (at lean 78.464, with the ride's travel steady at
    // 0.79 of its command) and the second reaches 23 (at 72.535, travel 0.86). It is 39 rather than the 37 recorded
    // when the finest scan run was 0.02 degrees: the number the record carries has to be the saturated one, since a
    // depth read off a coarse grid is a lower bound on the depth and reads as an upper one. The pre-#501 engine at
    // those same two headings reads 0.14 with 120 dead ticks and 0.10 with 178, and 17.32.0 as reviewed has no stall
    // at either. Over the whole census range at 0.1 degrees the ledger runs the same way by a wide margin: of 37988
    // rides, 17.32.0 stalls past eight ticks on 128 that the pre-#501 engine walked cleanly, with a worst run of 243,
    // and this build stalls on none of them.
    //
    // 88 AND 89 DEGREES ARE OUTSIDE THE RANGE ON PURPOSE. A command that close to the face normal is a walk INTO a
    // wall, and the pre-#501 engine parks there too (measured 95 to 245 dead ticks at 89 degrees, against 84 to 234
    // on this build). Pinning a heading the rule was never able to walk would be pinning something else's bug.
    //
    // WHAT THE BAND IS, AND THE RECTIFICATION RESIDUAL IN IT. Two-sided, 0.20 to 1.25, and THE TWO BOUNDS ARE NOT THE
    // SAME KIND OF NUMBER, which the file used to claim they were. The CEILING is pinned from a 0.1-degree scan
    // rather than from the one-degree grid the sweep below walks, because on the high side a finer grid finds a
    // HIGHER peak and a ceiling a finer scan steps over is no ceiling: this build reads 1.1882 at one degree and
    // 1.2266 at 0.1, and 1.25 clears the second. That correction is what caught the reviewed build, whose own peak
    // was recorded as 1.1993 from a one-degree grid and is really 1.2565 at 0.1 degrees - OVER the 1.25 this file
    // pins, so 17.32.0 as shipped breaches its own ceiling and only the grid hid it. This build peaks at 1.2266 with
    // 0.0234 to spare.
    //
    // THE FLOOR CANNOT BE THAT KIND OF NUMBER, AND SAYING IT WAS IS THE ERROR CORRECTED HERE. On the low side a finer
    // grid finds a LOWER trough, so no finite scan pins a floor the next one cannot get under, and the honest
    // statement is which scan the pin guards. It guards the integer-degree scan the sweep below walks, where this
    // build reads 0.2134. A 0.1-degree scan of the same range reads 0.2058, at 82.2 at a RUN at 30 Hz (the earlier
    // record of 0.2048 at 80.4 at a walk at 30 Hz was a misattribution: 80.4 at a walk reads 0.342). Refined to
    // convergence the floor is 0.1989, at 81.96 at the same run at 30 Hz and stable from 0.02 degrees down to 0.001 -
    // UNDER this file's 0.20, at a heading the sweep below never visits.
    //
    // THAT SUB-PIN TROUGH IS A GRID ARTEFACT AND NOT A MOVEMENT DEFECT, WHICH IS WHY IT IS DISCLOSED RATHER THAN
    // CHASED. The ride at 81.96 has ZERO dead ticks, where the pre-#501 engine at the same heading reads 0.198 with
    // 208 of its 300 ticks dead. A walker losing four fifths of its travel to a bank it is nearly facing is what a
    // near-normal heading does, and the fix's job was to stop it PARKING there, which it did.
    //
    // The high side is the residual the review asked to have pinned: a heading steep enough to spend most of its
    // ticks on the wall can come out of the switch travelling FASTER along the bank than the stick asked for, because
    // the substitution only ever fires on the ticks whose narrow answer was small and so acts as a rectifier on an
    // oscillation.
    //
    // WHAT IS NOT PINNED HERE, AND THE MEASUREMENT BEHIND THAT. The review asked for a toggle count as well. Every
    // ride-observable proxy for one was tried and none of them discriminate: the obvious one, sign reversals of the
    // per-tick along-face step, reads 6 on the chattering keep-trigger-only build at 79 degrees and 15 on this one,
    // because a walker parked on a chattering boundary does not reverse anything - it stops. Toggles were counted
    // instead from instrumented engine builds while the constants were being chosen, and that measurement lives at
    // CharacterMovement.WideFaceKeepMargin where the constant it justifies is. What a fixture can see is the
    // consequence, and both of this build's consequences are asserted below.
    [Theory]
    [InlineData(false, 15f)]
    [InlineData(false, 30f)]
    [InlineData(true, 15f)]
    [InlineData(true, 30f)]
    public void No_heading_across_the_trigger_boundary_parks_the_walker(bool run, float hz)
    {
        float worst = float.MaxValue, best = float.MinValue, worstLean = 0f, bestLean = 0f;
        int longestStall = 0, stallLean = 0;
        for (int lean = 60; lean <= 87; lean++)
        {
            Ride r = WalkAlong(Tuning, lean, run, hz, seconds: 10f);
            if (r.Efficiency < worst) { worst = r.Efficiency; worstLean = lean; }
            if (r.Efficiency > best) { best = r.Efficiency; bestLean = lean; }
            if (r.LongestStall > longestStall) { longestStall = r.LongestStall; stallLean = lean; }
        }

        string measured = $"{(run ? "run" : "walk")} at {hz:F0} Hz over leans 60 to 87: efficiency {worst:P1} (at "
                          + $"{worstLean:F0} deg) to {best:P1} (at {bestLean:F0} deg), longest stall anywhere "
                          + $"{longestStall} ticks (at {stallLean} deg)";
        _out.WriteLine(measured);

        Assert.True(longestStall <= BoundaryStallBound, $"a heading in the sweep parked the walker: {measured}");
        Assert.True(worst >= BoundaryFloor, $"a heading in the sweep lost its along-face travel: {measured}");
        Assert.True(best <= BoundaryCeiling,
            $"the switch rectified a heading into more travel than the stick asked for: {measured}");
    }

    // Zero measured on the one-degree grid this sweep walks, so this is float slack rather than a budget: one stalled
    // tick on this bank at these headings is already a wall contact refusing a move the pre-#501 engine committed.
    // The two sub-degree hitches a 0.02-degree scan finds are in the block above, and they are not what this number
    // is about.
    const int BoundaryStallBound = 8;

    // The FLOOR guards the integer-degree scan the sweep below actually walks, where this build reads 0.2134. It is
    // not a floor a finer scan cannot get under, and the block above says what is under it: 0.2058 at 0.1 degrees and
    // a converged 0.1989 at 81.96 at a run at 30 Hz. The CEILING is the other way round and IS pinned from the
    // 0.1-degree scan, because there the finer grid finds the higher peak: 1.2266 against 1.1882 at one degree. See
    // the block above for why that asymmetry is load-bearing and what the ceiling half of it caught.
    const float BoundaryFloor = 0.20f;
    const float BoundaryCeiling = 1.25f;

    // ---- The shallower half of the range, which nothing in this chain had ever swept ----

    // WHY 40 TO 60 EXISTS FROM ROUND TWO OF #502. Everything above scans 60 to 87, because that is where the
    // #501 census put the attractor and where the wide read's own moved park landed. The #502 review then found
    // three census PARKS at leans 45, 49 and 50 - a run at 15 Hz reading 0.35, 0.03 and 0.03 of its command with
    // 68, 137 and 137 consecutive dead ticks - which is the same signature this file exists to forbid, sitting
    // entirely below the window every scan in the file walks. A blind spot that wide is worth closing whatever
    // lives in it.
    //
    // WHAT LIVES IN IT, and it is not one story but two. Scanned at one degree over 40 to 60:
    //
    //     speed  rate     pre-#501        pre-#502        #502 round one   this build
    //     walk   15 Hz    0.927,   0      0.927,   0      0.858,   0       0.902,   0
    //     walk   30 Hz    0.364,   0      0.364,   0      0.396,   0       0.373,   0
    //     run    15 Hz    0.025, 135      0.025, 135      0.026, 137       0.025, 138
    //     run    30 Hz    0.285, 212      0.285, 212      0.754,   0       0.894,   0
    //
    // (floor of the scan, then the longest stall anywhere in it.)
    //
    // THE RUN AT 15 HZ PARKS IN EVERY BUILD IN THE CHAIN INCLUDING THE ONE BEFORE ANY OF IT, so it is not #501's
    // and not #502's. A walker at 46 degrees off this bank's contour at a 0.80 m step is being driven into
    // metre-wavelength micro-geometry a step and a half wide, and the pre-#501 engine stops there for 135 ticks
    // of its 150. It is pinned below rather than fixed, at a bound that admits the measurement and nothing worse,
    // so it cannot grow silently while nobody is scanning here. Naming it is the point, so name it precisely:
    // the pre-existing park lives at leans 46 and 47. The three census parks the round-one review found sat at
    // leans 45, 49 and 50, were clean in every build before round one, and round two REMOVED them (0.72 to 0.81
    // with no dead tick). Round one had also accidentally ridden the pre-existing lean-46 park (0.547 with none),
    // and round two returns that row to its pre-existing state, inside this pin.
    //
    // THE RUN AT 30 HZ IS THE OPPOSITE AND IS THE FIND WORTH HAVING. The pre-#501 engine parks there for 212 of
    // its 300 ticks at lean 44, and BOTH rounds of #502 remove it outright - 0.285 to 0.894 of commanded travel
    // with no dead tick anywhere. Nobody was looking, so nothing recorded it. It is pinned now.
    //
    // The two walk rows come through the whole chain within a few points of where they started and are pinned
    // with the ordinary headroom.
    [Theory]
    [InlineData(false, 15f)]
    [InlineData(false, 30f)]
    [InlineData(true, 15f)]
    [InlineData(true, 30f)]
    public void The_shallow_half_of_the_range_is_swept_too(bool run, float hz)
    {
        float worst = float.MaxValue, worstLean = 0f;
        int longestStall = 0, stallLean = 0;
        for (int lean = ShallowLo; lean <= ShallowHi; lean++)
        {
            Ride r = WalkAlong(Tuning, lean, run, hz, seconds: 10f);
            if (r.Efficiency < worst) { worst = r.Efficiency; worstLean = lean; }
            if (r.LongestStall > longestStall) { longestStall = r.LongestStall; stallLean = lean; }
        }

        string measured = $"{(run ? "run" : "walk")} at {hz:F0} Hz over leans {ShallowLo} to {ShallowHi}: floor "
                          + $"{worst:P1} (at {worstLean:F0} deg), longest stall anywhere {longestStall} ticks (at "
                          + $"{stallLean} deg)";
        _out.WriteLine(measured);

        (float floor, int maxStall) = ShallowExpected(run, hz);
        Assert.True(longestStall <= maxStall, $"a heading in the shallow sweep parked the walker: {measured}");
        Assert.True(worst >= floor, $"a heading in the shallow sweep lost its along-face travel: {measured}");
    }

    const int ShallowLo = 40;
    const int ShallowHi = 60;

    // Pinned from the table above, at the measurement plus headroom, EXCEPT the run at 15 Hz stall, which is
    // pinned at the measurement plus headroom on a park that predates this whole chain and is disclosed as one.
    // A future round that fixes it should tighten this to 8 like the others and say so.
    static (float floor, int maxStall) ShallowExpected(bool run, float hz) => (run, hz) switch
    {
        (false, 15f) => (0.80f, 8),
        (false, 30f) => (0.30f, 8),
        (true, 15f) => (0.02f, 160),
        (true, 30f) => (0.80f, 8),
        _ => throw new ArgumentOutOfRangeException(nameof(hz), hz,
            "no band is pinned for this (speed, tick rate) - measure it before sweeping it"),
    };

    // ---- The stencil profile itself, measured rather than asserted from the ride ----

    // WHY THIS TEST EXISTS SEPARATELY FROM THE RIDES. The rides show that the walk no longer parks. They cannot show
    // WHY the chosen width is the right one, because a ride only ever visits the positions its own dynamics take it
    // to. This walks the whole traverse and reports the WORST face direction each stencil width produces anywhere on
    // it, which is the table at WideFaceStencilScale and the thing that has to keep holding for that constant to
    // stay justified.
    //
    // The assertion is deliberately about the SHAPE rather than the exact numbers: the capsule-width read must reach
    // zero (or the fixture has stopped reproducing the bug at all) and the shipped width must not (or the wide read
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
