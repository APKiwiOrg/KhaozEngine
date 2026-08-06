using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Locomotion;

// A WALL CONTACT ON A BENDING FACE DOES NOT OSCILLATE ACROSS ITS OWN TRACTION CEILING (#502, 17.32.0).
// Eleventh round of the steep-terrain chain, and the successor to the six-round #501 arc.
//
// WHAT THE PLAYER REPORTED, verbatim from the playtest this comes from: sprinting alongside a bank gets you stuck
// in some places, and walking causes the jump/fall animation back and forth before eventually sliding past.
//
// THE MECHANISM, and #498 and #501 both wrote it down as their own residual. A walker leaning into a bank comes to
// rest exactly ON its traction ceiling, because that is where the wall contact stops it. The projected step is the
// contour of the height plane read at the DESTINATION column, so on a face that BENDS in plan it points a hair
// INSIDE the contour whatever its length, and the only endpoints available to a walker already at the ceiling are
// past it. The engine commits the longest admissible one, the support decision at the end of the SAME tick reads
// ground past the ceiling and slides the walker back down, it walks in again, and the ride is a slow oscillation.
//
// WHAT THAT COST, measured on the neighbouring WallContactTangentialTravelTests rides at 17.32.0 as reviewed:
// 543 footing flips and 5102 airborne ticks across the sixty rides of that sweep, a slide-handover speed boost
// reaching 158 percent of the commanded along-face travel, a climb creep of 2.85e-4 m per tick, and one whole ride
// (the 8 m bend at a run at 15 Hz, past the ladder's coverage ceiling) parked for 149 of its 150 ticks.
//
// ---------------------------------------------------------------------------------------------------------------
// WHAT THIS FILE ASSERTS, AND WHY IT IS ONE BAND RATHER THAN TWELVE.
//
// The neighbouring fixture pins each (bend, speed, rate) case to its own measured band, which is the right shape
// for a residual that differs case by case. The claim HERE is stronger and simpler: once the projection stops
// aiming into the face, EVERY ride in that same sweep - both bend classes, both speeds, all three rates, every
// lean - lands inside ONE narrow band, with no stall, essentially no footing flip and a creep an order under the
// slack it is made of. A per-case table could not say that, because a table with twelve rows in it can absorb one
// row going bad. A single band cannot.
//
// So this file deliberately duplicates the neighbouring fixture's geometry and ride rather than sharing it. The
// two are pinned to DIFFERENT THINGS - that one to the oscillation's measured size, this one to its absence - and
// a shared helper would make it impossible to re-pin one without the other moving under it.
public class WallContactOwnColumnTests
{
    readonly ITestOutputHelper _out;
    public WallContactOwnColumnTests(ITestOutputHelper output) => _out = output;

    static MoveTuning Tuning => MoveTuning.Default;

    // ---- The bank, transcribed from WallContactTangentialTravelTests, and its MIRROR ----
    //
    // Ground height as a function of the distance from a BEND CENTRE: the gate contour is a circle of the bend
    // radius and the walker's along-face travel is a lap around it. The normal is the EXACT analytic normal of
    // this height field, so nothing here measures a normal/height delegate disagreement.
    //
    // <c>Sign</c> IS THE WHOLE OF THE ROUND-TWO ADDITION AND IT COSTS THE CONVEX CASE NOTHING. +1 is the original
    // bank, rising INWARD: the outside of a spur, a headland, and the shape every fixture in the #498, #501 and
    // #502 arc is built on. -1 is its exact mirror, rising OUTWARD: a cove, a bowl, the inside of a curve. Same
    // radii, same ramp, same clamps, same gate contour, same start offset, and the walker leans up the hill
    // either way - the only thing that changes is which side of the contour the hill is on. Every arithmetic path
    // below reduces to the pre-mirror expression at Sign +1 (checked digit for digit against the sixty rides the
    // file shipped with), so the convex assertions are the same assertions they were.
    //
    // WHY A MIRROR IS WORTH SIXTY MORE RIDES. The correction this file is about removes the component of the
    // travel along the walker's own column's outward, and WHICH WAY A BEND TURNS decides whether that removes a
    // climb or adds one. Every fixture in the arc bends the same way, so a rule that is right on one sign and
    // exactly backwards on the other reads as green everywhere. It did: see the concave band below.
    readonly record struct Bank(float BendRadius, int Sign)
    {
        public const float BaseHeight = 40f;          // island scale, so feetY's rounding is the island's
        public const float GateGradient = 1.1106f;    // tan(48 deg): MoveTuning.Default's gate (45) plus its band (3)
        public const float RampPerMetre = 0.25f;
        public const float MinGradient = 0.36f;
        public const float MaxGradient = 2.5f;

        const float RampInward = (MaxGradient - GateGradient) / RampPerMetre;
        const float RampOutward = (GateGradient - MinGradient) / RampPerMetre;

        public float Radius(float x, float z) => MathF.Sqrt(x * x + z * z);

        /// <summary>How far the column is INTO the uphill side of the gate contour, which is the one quantity the
        /// mirror flips.</summary>
        float Offset(float r) => Sign > 0 ? BendRadius - r : r - BendRadius;

        public float GradientAt(float r)
        {
            float g = GateGradient + RampPerMetre * Offset(r);
            return g < MinGradient ? MinGradient : g > MaxGradient ? MaxGradient : g;
        }

        public float Height(float x, float z)
        {
            float d = Offset(Radius(x, z));
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
            // dH/dr is -g on the convex bank (uphill inward) and +g on the concave one, and the normal is the
            // upward one: (-dH/dx, 1, -dH/dz). At Sign +1 that is exactly (g x / r, 1, g z / r), as it was.
            float dhdr = Sign > 0 ? -g : g;
            return Vector3.Normalize(new Vector3(-dhdr * x / r, 1f, -dhdr * z / r));
        }

        public string Face => Sign > 0 ? "convex" : "concave";
    }

    // ---- The ride ----

    readonly record struct Ride(float Efficiency, int Ticks, int LongestStall, int Airborne, int Flips,
        float MaxClimb, string Measured);

    // Walk a held angle to the face for ten seconds, re-aiming every tick to hold the same angle, and report what
    // the walk looked like. Efficiency is the TANGENTIAL projection of each tick's displacement on the contour
    // tangent at the position it started from, signed, so a radial wobble cannot read as along-face progress.
    static Ride WalkAlong(in MoveTuning t, in Bank bank, float leanDegrees, bool run, float hz)
    {
        float dt = 1f / hz;
        int ticks = (int)MathF.Round(10f * hz);
        float speed = run ? t.RunSpeed : t.WalkSpeed;
        float lean = leanDegrees * MathF.PI / 180f;
        float cos = MathF.Cos(lean), sin = MathF.Sin(lean);
        float r0 = bank.BendRadius + bank.Sign * 0.002f;
        var s = new MoveState
        {
            Position = new Vector3(r0, bank.Height(r0, 0f) + t.CapsuleHalfHeight, 0f),
            Grounded = true,
        };

        float tangential = 0f, startFeet = s.Position.Y - t.CapsuleHalfHeight, maxClimb = 0f;
        int longestStall = 0, stall = 0, airborne = 0, flips = 0;
        bool previous = true;
        for (int i = 0; i < ticks; i++)
        {
            float x = s.Position.X, z = s.Position.Z;
            float r = MathF.Sqrt(x * x + z * z);
            float ux = x / r, uz = z / r;
            float tx = -uz, tz = ux;
            // Lean is INTO the hill on either sign: inward on the convex bank, outward on the concave one. At
            // Sign +1 this is the pre-mirror expression (tx cos - ux sin) unchanged.
            float up = bank.Sign > 0 ? -sin : sin;
            Vector2 dir = new(tx * cos + ux * up, tz * cos + uz * up);

            MoveState next = CharacterMovement.StepTowards(s, dir, run, dt, bank.Height, t, bank.NormalAt);
            float dx = next.Position.X - x, dz = next.Position.Z - z;
            tangential += dx * tx + dz * tz;
            if (MathF.Sqrt(dx * dx + dz * dz) < 1e-4f)
            {
                stall++;
                if (stall > longestStall) longestStall = stall;
            }
            else stall = 0;

            if (!next.Grounded) airborne++;
            if (next.Grounded != previous) flips++;
            previous = next.Grounded;
            maxClimb = MathF.Max(maxClimb, next.Position.Y - t.CapsuleHalfHeight - startFeet);
            s = next;
        }

        float commanded = speed * cos * dt * ticks;
        string measured = $"{bank.Face} bend {bank.BendRadius:F0} m, {(run ? "run" : "walk")} at {hz:F0} Hz, lean "
                          + $"{leanDegrees:F0} deg: along-face {tangential / commanded:P1} of commanded, longest "
                          + $"stall {longestStall}/{ticks}, airborne {airborne}, flips {flips}, climbed at most "
                          + $"{maxClimb:F5} m ({maxClimb / ticks:E2} m per tick)";
        return new Ride(tangential / commanded, ticks, longestStall, airborne, flips, maxClimb, measured);
    }

    // ---- The sweep: the neighbouring fixture's own sixty rides, under ONE band ----

    public static IEnumerable<object[]> Rides()
    {
        foreach (float bend in new[] { 400f, 8f })
            foreach (float lean in new[] { 0f, 5f, 10f, 20f, 30f })
                foreach (bool run in new[] { false, true })
                    foreach (float hz in new[] { 15f, 30f, 120f })
                        yield return new object[] { bend, lean, run, hz };
    }

    // THE BAND, AND WHERE EVERY NUMBER IN IT COMES FROM. Measured over all sixty rides of the sweep on the build
    // this file shipped with, then given headroom, and each of the five is a different failure the oscillation
    // showed:
    //
    //     quantity                    measured over the sixty rides      pinned at
    //     efficiency                  0.999 to 1.047                     0.95 to 1.05
    //     longest stall               0 ticks on every ride              0
    //     footing flips               0 on 59 rides, 2 on one            4
    //     airborne ticks              0 on 59 rides, 57 on one           70
    //     climb creep                 at most 1.44e-5 m per tick         3e-5
    //
    // THE ONE RIDE THAT IS NOT CLEAN IS NAMED RATHER THAN ROUNDED AWAY: the near-planar 400 m bend at a WALK at
    // 120 Hz and a five degree lean, whose 0.05 m step is short enough that the walker settles inside the float
    // noise on its own resting offset and spends 57 of its 1200 ticks re-seating. It is 19 percent of one ride's
    // worth of airborne against the 5102 the reviewed build spends across the sweep, and the bounds are set to
    // admit it and nothing worse.
    //
    // THE EFFICIENCY BAND IS TWO-SIDED FOR THE REASON THE NEIGHBOURING FIXTURE GIVES, and the upper side is the
    // headline. A walker that gets slid back down a bank and walks in again spends part of each cycle on a SLIDE
    // tick, whose carry is the fall line PLUS the contour, and the contour part arrives on top of the walk - so
    // the reviewed build hands over up to 158 percent of the commanded along-face travel. That speed the player
    // never asked for is the same oscillation seen from the other side, and 1.05 is what kills it.
    //
    // THE CREEP IS A PER-TICK QUANTITY, never a per-ride one: it is made of the 1 mm ProjectedRiseSlack the
    // re-test spends at most once a tick, so its worst case scales with the tick COUNT and a flat metre bound
    // would silently tighten as the rate rises until it was measuring the clock.
    const float LoEfficiency = 0.95f;
    const float HiEfficiency = 1.05f;
    const int MaxStall = 0;
    const int MaxFlips = 4;
    const int MaxAirborne = 70;
    const float MaxCreepPerTick = 3e-5f;

    [Theory]
    [MemberData(nameof(Rides))]
    public void A_bank_ride_holds_its_ceiling_without_oscillating(float bend, float lean, bool run, float hz)
    {
        Ride r = WalkAlong(Tuning, new Bank(bend, 1), lean, run, hz);
        _out.WriteLine(r.Measured);

        Assert.True(r.Efficiency >= LoEfficiency, $"the wall contact ate the along-face travel: {r.Measured}");
        Assert.True(r.Efficiency <= HiEfficiency,
            $"the oscillation handed the walker free along-face speed: {r.Measured}");
        Assert.True(r.LongestStall <= MaxStall, $"the walk parked against the face: {r.Measured}");
        Assert.True(r.Flips <= MaxFlips, $"the ride flickered its footing across the ceiling: {r.Measured}");
        Assert.True(r.Airborne <= MaxAirborne, $"the ride slid back down the bank: {r.Measured}");
        Assert.True(r.MaxClimb <= MaxCreepPerTick * r.Ticks,
            $"the wall contact crept up the face: {r.Measured}");
    }

    // ---- The MIRROR: the same sixty rides on a bend that turns the other way ----

    // WHAT THIS FAMILY IS FOR, AND WHY IT EXISTS ONLY FROM ROUND TWO. The correction above removes the travel's
    // component along the walker's own column's outward. On a CONVEX bend that removes a climb, which is the whole
    // of #502. On a CONCAVE one - a cove, a bowl, the inside of a curve - the destination anchor is the one that
    // already DESCENDS by the sagitta and the walker's own column is the one that climbs, so the identical
    // arithmetic replaces the right answer with the wrong one, by the same size, in the opposite direction. Every
    // bank fixture in the #498, #501 and #502 arc bends the same way, so round one shipped fully green with the
    // rule exactly backwards on half of all terrain.
    //
    // WHAT THE MIRROR MEASURED, on the same sixty (bend, lean, speed, rate) rides as the sweep above:
    //
    //     build                         efficiency         flips   airborne   longest stall   creep per tick
    //     pre-#502 (the reference)      0.948 to 1.049         6        171               0         1.51e-5
    //     #502 round one                0.827 to 2.044       471      13225               0         3.47e-4
    //     this build                    0.948 to 1.049         6        171               0         1.51e-5
    //
    // THE WORST SINGLE ROW IS THE 8 M CONCAVE BEND AT A WALK AT 120 HZ AND A LEAN OF ZERO, which is a walker
    // holding a purely tangential heading on a bowl: round one spends 1187 of its 1200 ticks AIRBORNE and covers
    // 2.044 times the travel the stick asked for, because it is being slid down the face rather than walked along
    // it. The pre-#502 engine reads 1.000 with no airborne tick at all. That row is why the band below is
    // two-sided and why airborne is asserted: an efficiency floor alone would pass a build sliding a player
    // downhill at double speed.
    //
    // THE FIX IS A SIGN GATE ON THE CORRECTION, so on this family the levelling is arithmetically absent and the
    // rides come back to the pre-#502 engine's own numbers TO EVERY DIGIT on all sixty rows - not merely inside
    // the band. Checked digit for digit outside the fixture, and the band here is what a fixture can hold. The
    // convex sixty above are likewise unchanged from round one to every digit, so the gate costs the shape #502
    // was reported on nothing at all.
    //
    // A WIDER FAMILY WAS MEASURED AND IS NOT SHIPPED. Bends 400, 50, 20 and 8 at leans 0 to 45 (192 rides) read
    // the same way: pre-#502 0.929 to 1.049 with 6 flips and 171 airborne, round one 0.750 to 2.044 with 1565
    // flips and 40162 airborne, this build identical to pre-#502 on every row. The sixty here are the mirror of
    // the sweep above rather than a family of their own, deliberately, so the two sweeps stay comparable.
    public static IEnumerable<object[]> ConcaveRides() => Rides();

    // Measured over all sixty mirror rides on this build, then given headroom, in the same shape as the convex
    // band above. The two efficiency edges differ from the convex ones because the ride does: a bowl's contour
    // curves away from the walker rather than toward it, so the pre-#502 engine already loses a little travel on
    // the tightest bend at the slowest rate (0.948 at the 8 m bend at a run at 15 Hz) and nothing about this fix
    // gives that back - it was never the bug.
    //
    //     quantity                    measured over the sixty rides      pinned at
    //     efficiency                  0.948 to 1.049                     0.90 to 1.06
    //     longest stall               0 ticks on every ride              0
    //     footing flips               0 on 57 rides, 2 on three          4
    //     airborne ticks              0 on 57 rides, 55 to 59 on three   70
    //     climb creep                 at most 1.51e-5 m per tick         3e-5
    //
    // The three rides that are not clean are the 400 m bend at 120 Hz at leans 0 and 5, and they are the same
    // float-noise re-seating the convex band names: a 0.05 m step is short enough that the walker settles inside
    // the noise on its own resting offset. They are the pre-#502 engine's own three rides, unchanged.
    //
    // Against round one this band is red on 44 of its 60 rows.
    const float LoConcaveEfficiency = 0.90f;
    const float HiConcaveEfficiency = 1.06f;

    [Theory]
    [MemberData(nameof(ConcaveRides))]
    public void A_concave_bank_ride_is_left_exactly_as_it_was(float bend, float lean, bool run, float hz)
    {
        Ride r = WalkAlong(Tuning, new Bank(bend, -1), lean, run, hz);
        _out.WriteLine(r.Measured);

        Assert.True(r.Efficiency >= LoConcaveEfficiency, $"the wall contact ate the along-face travel: {r.Measured}");
        Assert.True(r.Efficiency <= HiConcaveEfficiency,
            $"the levelling ran backwards and slid the walker down the bowl: {r.Measured}");
        Assert.True(r.LongestStall <= MaxStall, $"the walk parked against the face: {r.Measured}");
        Assert.True(r.Flips <= MaxFlips, $"the ride flickered its footing across the ceiling: {r.Measured}");
        Assert.True(r.Airborne <= MaxAirborne, $"the ride slid back down the bank: {r.Measured}");
        Assert.True(r.MaxClimb <= MaxCreepPerTick * r.Ticks, $"the wall contact crept up the face: {r.Measured}");
    }

    // ---- The asymmetric gully: the refusal a levelling of the wrong sign turns into a seat ----

    // THE SHAPE. The rising gully of WallContactTangentialTravelTests with its two walls made DIFFERENT - 4.0 and
    // 1.2, so the capsule-wide stencil straddling the axis no longer cancels them - and the walker started 5 cm
    // off the axis rather than on it, leaning 3 and 10 degrees each way, on both sides. Both walls are past the
    // gate (1.2 is 50 degrees against a 48 degree gate), so every one of the thirty-two rides is a wall contact
    // that the anti-tunnel rule is supposed to refuse outright.
    //
    // WHAT IT CAUGHT. The pre-#502 engine refuses EXACTLY, gaining at most 1.78 mm of altitude anywhere in the
    // family, and that 1.78 mm is the ProjectedRiseSlack the re-test spends at most once a tick rather than a
    // seat. Round one turns the refusal into a creep: up to 48.7 mm on the 5 cm start at a 10 degree lean at a
    // walk at 30 Hz, five rides over 3 mm, and rides that gain the altitude with every one of their 300 ticks
    // AIRBORNE - which is the #468 shape exactly, a step admitted onto ground the support decision then refuses.
    // It is the same defect the concave family above measures, seen on a crease instead of a bend: the walker's
    // own column here is the SHALLOW wall, its outward points across the gully, and levelling against it adds the
    // rise the destination's contour was shedding.
    //
    // With the sign gate the whole family returns to 1.78 mm, the pre-#502 number, on every row.
    //
    // WHY THE ASSERTION IS ALTITUDE AND NOT TRAVEL. The rides differ a lot in how far they advance, because a
    // 5 cm start off a crease axis is a chaotic initial condition and the engine is entitled to slide the walker
    // out of it. What it is NEVER entitled to do is hand the walker altitude on ground past its own gate, and
    // that quantity is clean: three of the four builds in the arc read the same 1.78 mm worst case and round one
    // reads 27 times it.
    const float GullyFloorGrade = 0.30f;
    const float GullySteepWall = 4.0f;      // ~76 degrees: far past the gate
    const float GullyShallowWall = 1.2f;    // ~50 degrees: past the gate, but only just, and NOT the other wall

    static float AsymGully(float x, float z)
        => Bank.BaseHeight + GullyFloorGrade * x + (z >= 0f ? GullySteepWall * z : GullyShallowWall * -z);

    static Vector3 AsymGullyNormal(float x, float z)
        => Vector3.Normalize(new Vector3(-GullyFloorGrade, 1f,
            z > 0f ? -GullySteepWall : z < 0f ? GullyShallowWall : 0f));

    public static IEnumerable<object[]> GullyRides()
    {
        foreach (float startZ in new[] { 0.05f, -0.05f })
            foreach (float lean in new[] { 3f, 10f, -3f, -10f })
                foreach (bool run in new[] { false, true })
                    foreach (float hz in new[] { 15f, 30f })
                        yield return new object[] { startZ, lean, run, hz };
    }

    // 1.78 mm measured on this build and on the pre-#502 engine, pinned at 3 mm. Round one reaches 48.7 mm and is
    // red on five of the thirty-two rows.
    const float MaxGullyClimb = 3e-3f;

    [Theory]
    [MemberData(nameof(GullyRides))]
    public void An_asymmetric_gully_still_refuses_rather_than_seating(float startZ, float lean, bool run, float hz)
    {
        MoveTuning t = Tuning;
        float dt = 1f / hz;
        int ticks = (int)MathF.Round(10f * hz);
        float radians = lean * MathF.PI / 180f;
        var dir = new Vector2(MathF.Cos(radians), MathF.Sin(radians));
        const float startX = 4f;
        var s = new MoveState
        {
            Position = new Vector3(startX, AsymGully(startX, startZ) + t.CapsuleHalfHeight, startZ),
            Grounded = true,
        };

        float startFeet = s.Position.Y - t.CapsuleHalfHeight, maxClimb = 0f;
        int airborne = 0;
        for (int i = 0; i < ticks; i++)
        {
            s = CharacterMovement.StepTowards(s, dir, run, dt, AsymGully, t, AsymGullyNormal);
            maxClimb = MathF.Max(maxClimb, s.Position.Y - t.CapsuleHalfHeight - startFeet);
            if (!s.Grounded) airborne++;
        }

        string measured = $"asymmetric gully from z {startZ:F2}, lean {lean:F0} deg, {(run ? "run" : "walk")} at "
                          + $"{hz:F0} Hz: climbed at most {maxClimb:F6} m, airborne {airborne}/{ticks}, ended "
                          + $"({s.Position.X:F4}, {s.Position.Z:F4})";
        _out.WriteLine(measured);
        Assert.True(maxClimb < MaxGullyClimb, $"the gully handed over altitude past the gate: {measured}");
    }

    // ---- The ladder's coverage ceiling, which this fix is what retires ----

    // THE 8 M BEND AT A RUN AT 15 HZ IS THE ONE RIDE IN THE SWEEP THE #498 LADDER CANNOT REACH. Its 0.80 m step
    // needs an 11 m bend for the sixty-fourth rung to get under the slack, so every rung is refused, the tick
    // commits nothing, and the walker parks: WallContactTangentialTravelTests pins that park at 149 of 150 ticks
    // stalled and 12 to 23 mm of travel in ten seconds, and its file header says in as many words that moving the
    // ceiling further out is #502's job rather than a deeper ladder's.
    //
    // It is asserted separately from the band above even though the band already covers it, because a ride that
    // goes from a dead stop to full travel is the single clearest statement this fix makes and a reader looking
    // for it should not have to find it inside a sixty-row theory.
    [Theory]
    [InlineData(5f)]
    [InlineData(10f)]
    [InlineData(20f)]
    [InlineData(30f)]
    public void The_ladder_coverage_ceiling_no_longer_parks_the_walker(float lean)
    {
        Ride r = WalkAlong(Tuning, new Bank(8f, 1), lean, run: true, hz: 15f);
        _out.WriteLine(r.Measured);
        Assert.True(r.LongestStall == 0, $"the 8 m bend at a run at 15 Hz still parks: {r.Measured}");
        Assert.True(r.Efficiency >= LoEfficiency, $"the 8 m bend at a run at 15 Hz still crawls: {r.Measured}");
    }

    // ---- Why the walker's own column is the anchor, measured from the surface rather than from the engine ----

    // WHAT THIS TEST IS FOR. The rides show the oscillation is gone. They cannot show WHY reading the contour at
    // the walker's own column is the thing that removes it, because a ride only reports where it ended up. This
    // walks the geometry directly and reports what one tick's projected step ASKS FOR at each of the three
    // candidate anchors, which is the whole design argument in one table.
    //
    // A walker resting on the gate contour of a circular bank of radius R, stepping a fraction k of its length L
    // along the contour of the height plane read at anchor m (0 = its own column, 0.5 = the midpoint of the step,
    // 1 = the destination). What the surface then does to it:
    //
    //     anchor    k=1        k=1/2      k=1/8      k=1/64      shape of the ask
    //     0.0       -G L^2/2R  -k^2 ...   -k^2 ...   -k^2 ...    always DOWNHILL, and quadratic in the rung
    //     0.5       0          +          +          +           zero at full length, WORSE as it shortens
    //     1.0       +G L^2/2R  0.75 of it 0.23 of it 0.031 of it always UPHILL, k(2-k) in the rung
    //
    // THE DESTINATION ANCHOR IS THE ONLY ONE THAT CLIMBS AT FULL LENGTH, which is #502. The MIDPOINT cancels the
    // climb at full length and is still rejected, because its ask gets WORSE as the ladder shortens (the step line
    // stays aimed at the full step's midpoint, so a half rung asks for a quarter of the full-length figure where
    // the full rung asked for nothing) - a ladder whose rungs are not monotone is a ladder that cannot be reasoned
    // about. The walker's own column descends by the sagitta at every rung and is quadratic in the rung, so the
    // ladder underneath it is both unnecessary and, where a shape does make the ask positive, 128 times more
    // effective than it is against the destination anchor.
    //
    // Computed from the height field rather than from CharacterMovement, so it is a statement about the surface
    // and not a re-derivation of the rule under test.
    [Theory]
    [InlineData(400f, 0.20f)]
    [InlineData(400f, 0.80f)]
    [InlineData(8f, 0.20f)]
    [InlineData(8f, 0.80f)]
    public void The_own_column_anchor_is_the_only_one_whose_step_never_climbs(float bend, float stepLength)
    {
        var bank = new Bank(bend, 1);
        var row = new System.Text.StringBuilder($"bend {bend:F0} m, step {stepLength:F2} m:");
        float ownWorst = float.MinValue, midWorst = float.MinValue;
        foreach (float m in new[] { 0f, 0.5f, 1f })
        {
            row.Append($"  anchor {m:F1}");
            foreach (float k in new[] { 1f, 0.5f, 0.125f, 1f / 64f })
            {
                float ask = Ask(bank, stepLength, m, k);
                row.Append($" {ask:E2}");
                if (m == 0f) ownWorst = MathF.Max(ownWorst, ask);
                if (m == 0.5f && k < 1f) midWorst = MathF.Max(midWorst, ask);
            }
        }
        _out.WriteLine(row.ToString());

        // THE SHORT RUNGS AT THE 400 M BEND READ EXACTLY ZERO AND THAT IS FLOAT RESOLUTION RATHER THAN
        // GEOMETRY: the ask there is 3e-5 m of rise on a 40 m base height, which is about one ulp, so a
        // sixty-fourth of it has nowhere to land. The full-length rung is the one the sign claims are made
        // about, and the shortening columns are printed for the shape rather than asserted on.
        Assert.True(ownWorst <= 0f,
            $"the own-column anchor's step climbs at some rung, so this fixture has stopped describing the "
            + $"geometry #502 is about: {row}");
        Assert.True(Ask(bank, stepLength, 1f, 1f) > 0f,
            $"the destination anchor's full-length step no longer climbs, so this fixture has stopped "
            + $"reproducing #502: {row}");
        Assert.True(Ask(bank, stepLength, 0f, 1f) < 0f,
            $"the own-column anchor's full-length step no longer descends, so the fix has no margin here: {row}");
        Assert.True(midWorst > Ask(bank, stepLength, 0.5f, 1f),
            $"the midpoint anchor's ask no longer gets worse as the ladder shortens, so the reason it was "
            + $"declined has gone: {row}");
    }

    // ---- The perturbation control, as a runnable recipe rather than a paragraph ----

    // WHY THIS IS HERE. Several blocks in this chain defend a small census or fixture movement by saying it is
    // inside the band a MEANINGLESS perturbation produces. That defence is only worth anything if the next reader
    // can re-run it, and until round two it existed as prose only, with numbers nobody could reproduce and, as it
    // turned out, numbers that were wrong (see the block under Bounds in WallFaceAttractorTests). So the control
    // ships as an executable procedure with its exact steps, skipped by default because it is a MEASUREMENT and
    // not an assertion: it has no pass condition, it prints a table, and a table with a pass condition bolted to
    // it on a shape this chaotic is a flaky test.
    //
    // THE PROCEDURE, exactly.
    //
    //   1. In CharacterMovement.WallContact.cs, immediately after the line
    //          (sx, sz) = LevelOnOwnColumn(sx, sz, ox, oz);
    //      insert a rotation of the surviving travel by an angle with no geometric meaning at all:
    //          float a = <degrees> * MathF.PI / 180f, ca = MathF.Cos(a), sa = MathF.Sin(a);
    //          (sx, sz) = (sx * ca - sz * sa, sx * sa + sz * ca);
    //   2. Delete the Skip argument below and run this one test.
    //   3. Repeat at 0, 0.01, 0.05, 0.2 and 1 degrees, and REVERT step 1.
    //
    // WHAT IT READS ON THIS BUILD, for the twelve WallFaceAttractorTests rides, as efficiency:
    //
    //     ride                0 deg   0.01    0.05     0.2       1
    //     68 walk 15 Hz       0.912   0.873   0.934   0.908   0.946
    //     68 run  30 Hz       0.839   0.887   0.799   0.878   0.779
    //     79 walk 15 Hz       1.064   1.044   1.088   1.102   1.155
    //
    // A HUNDREDTH OF A DEGREE MOVES A PINNED ROW BY UP TO 0.049 OF ITS EFFICIENCY. That is the number the
    // sensitivity claims in this chain rest on, and it is why a row failing or passing by two percent is not
    // evidence of anything on that fixture. The census half of the same control is in CHANGELOG.md's #502 entry
    // and is a DIFFERENT statement with a different threshold: there the control costs nothing measurable below
    // one degree, so the two must not be quoted for each other.
    //
    // The bank rides in THIS file are not part of that caveat and the control says so: they are analytic, smooth,
    // and their sixty-row sweep does not move at all until the rotation reaches a degree.
    [Fact(Skip = "measurement, not an assertion: see the procedure above, it needs an engine edit to mean anything")]
    public void The_perturbation_control_for_every_sensitivity_claim_in_this_chain()
    {
        foreach (float bend in new[] { 400f, 8f })
            foreach (bool run in new[] { false, true })
            {
                Ride r = WalkAlong(Tuning, new Bank(bend, 1), 10f, run, 30f);
                _out.WriteLine("convex  " + r.Measured);
                Ride m = WalkAlong(Tuning, new Bank(bend, -1), 10f, run, 30f);
                _out.WriteLine("concave " + m.Measured);
            }
    }

    /// <summary>What one tick's projected step asks the surface for, in metres of rise: the height at the endpoint
    /// of a step of length <paramref name="k"/> times <paramref name="stepLength"/> taken from the gate contour
    /// along the contour direction of the height plane read at anchor <paramref name="m"/> along the full step,
    /// minus the height it started at. Positive is a climb.</summary>
    static float Ask(in Bank bank, float stepLength, float m, float k)
    {
        float r0 = bank.BendRadius;
        float dTheta = m * stepLength / r0;
        float tx = -MathF.Sin(dTheta), tz = MathF.Cos(dTheta);
        return bank.Height(r0 + k * stepLength * tx, k * stepLength * tz) - bank.Height(r0, 0f);
    }
}
