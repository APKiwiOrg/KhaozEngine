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

    // ---- The bank, transcribed from WallContactTangentialTravelTests ----
    //
    // Ground height as a function of the distance from a BEND CENTRE, rising inward: the gate contour is a circle
    // of the bend radius and the walker's along-face travel is a lap around it. The normal is the EXACT analytic
    // normal of this height field, so nothing here measures a normal/height delegate disagreement.
    readonly record struct Bank(float BendRadius)
    {
        public const float BaseHeight = 40f;          // island scale, so feetY's rounding is the island's
        public const float GateGradient = 1.1106f;    // tan(48 deg): MoveTuning.Default's gate (45) plus its band (3)
        public const float RampPerMetre = 0.25f;
        public const float MinGradient = 0.36f;
        public const float MaxGradient = 2.5f;

        const float RampInward = (MaxGradient - GateGradient) / RampPerMetre;
        const float RampOutward = (GateGradient - MinGradient) / RampPerMetre;

        public float Radius(float x, float z) => MathF.Sqrt(x * x + z * z);

        public float GradientAt(float r)
        {
            float g = GateGradient + RampPerMetre * (BendRadius - r);
            return g < MinGradient ? MinGradient : g > MaxGradient ? MaxGradient : g;
        }

        public float Height(float x, float z)
        {
            float d = BendRadius - Radius(x, z);
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
            return Vector3.Normalize(new Vector3(g * x / r, 1f, g * z / r));
        }
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
        float r0 = bank.BendRadius + 0.002f;
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
            Vector2 dir = new(tx * cos - ux * sin, tz * cos - uz * sin);

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
        string measured = $"bend {bank.BendRadius:F0} m, {(run ? "run" : "walk")} at {hz:F0} Hz, lean "
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
        Ride r = WalkAlong(Tuning, new Bank(bend), lean, run, hz);
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
        Ride r = WalkAlong(Tuning, new Bank(8f), lean, run: true, hz: 15f);
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
        var bank = new Bank(bend);
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
