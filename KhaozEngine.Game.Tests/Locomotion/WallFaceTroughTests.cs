using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Locomotion;

// A WIDE FACE MUST NOT OVERRULE A NARROW ONE THAT IS STILL TELLING THE TRUTH (#501 round two, 17.32.0). Tenth
// round of the steep-terrain chain, and the first that comes from a REVIEW of the round before it rather than from
// a playtest.
//
// WHAT #501 SHIPPED AND WHAT IT MISSED. #501 gave the wall contact a second, wider face direction for the case where
// the capsule-width read has drifted anti-parallel to the command, and two triggers for taking it: the narrow face
// keeps almost none of the command, or the two candidate travels oppose by more than a right angle. The second
// trigger was ungated, and an ungated opposition test asks the wrong question, because two faces disagreeing does not
// say WHICH of them is wrong. On the shapes #501 was measured against the narrow face was the wrong one whenever they
// disagreed. On the shape below it is the RIGHT one, and the fix walked straight into it.
//
// THE SHAPE. A trough narrower than the capsule, with UNEQUAL flanks. The capsule-radius stencil straddles the floor
// line, so the narrow read is a real local plane tilted toward whichever flank the walker is leaning on, and it is
// honest: it keeps most of the command and points along the trough. The 2 m read spans the floor and BOTH flanks and
// averages them, so on unequal flanks it reports an uphill that leans the other way, and its contour points across
// the trough. The two are then more than a right angle apart with the narrow one perfectly healthy, the ungated
// opposition test fires, the walker is handed a travel aimed into the flank it is already leaning on, and the
// anti-tunnel ladder refuses every rung of it. Measured on the V below at 20 degrees off the trough axis: the narrow
// face keeps 0.83 of the command, the wide face points 114 degrees away from it, and the walk goes from 0.908 of its
// commanded travel with no wide read at all to 0.017 with 291 of 300 ticks dead.
//
// SO THIS FILE IS PINNED TO THE PRE-#501 ENGINE, which is the only build in the chain that never had a wide face at
// all. Every band below is that build's own measured ride widened by five points either way. That is deliberate: the
// wide read exists to remove a park, so on ground where the narrow face was never in trouble it owes the walk exactly
// what the walk already had, and a fixture that pinned the FIXED build's numbers instead would have no way of saying
// so. Sixteen rides, and at 17.32.0's first entry fifteen of them are red.
//
// THE BITE IS NOT MIRROR-SYMMETRIC EVEN THOUGH THE RULE IS. Which lean direction gets eaten, and at what angle, is a
// question about where the wide read's averaged uphill lands relative to the narrow one, so it moves with the flank
// ratio: the 1.67 V is eaten leaning into its SHALLOW flank from 15 degrees out, the 1.30 valley keeps that direction
// clean until 35 and is eaten leaning into its STEEP flank from 55. Both directions are swept below for that reason,
// and the rows were chosen from a 744-ride census rather than by picking an angle and hoping.
//
// WHAT IS DELIBERATELY NOT HERE. Past about 70 degrees off the trough axis, and at a run at 15 Hz (a 0.80 m step)
// from about 40, the PRE-#501 engine parks on these troughs too - narrow keep under a tenth, every rung refused. That
// is a real limitation and it is the ladder's rather than the face's, so it is not this file's business and no row
// below sits in it. It is #530.
public class WallFaceTroughTests
{
    readonly ITestOutputHelper _out;
    public WallFaceTroughTests(ITestOutputHelper output) => _out = output;

    static MoveTuning Tuning => MoveTuning.Default;

    // ---- The troughs ----

    /// <summary>A straight trough running along +x whose floor climbs at a WALKABLE grade and whose two flanks are
    /// both past the gate at DIFFERENT steepnesses. <paramref name="Smoothing"/> 0 is the hard V (the asymmetric
    /// sibling of the rising gully in WallContactTangentialTravelTests, which has equal flanks). Above 0 the corner
    /// is a tanh of that half-width, so the surface is C-infinity and no reading anywhere on it depends on a
    /// derivative that does not exist.</summary>
    readonly record struct Trough(string Name, float PositiveFlank, float NegativeFlank, float Smoothing)
    {
        public const float FloorGrade = 0.30f;      // ~16.7 degrees along the axis: walkable, so the walk keeps footing
        public const float BaseHeight = 40f;        // island scale, so feetY's float rounding is the island's

        public float Height(float x, float z)
        {
            float across;
            if (Smoothing <= 0f) across = z >= 0f ? PositiveFlank * z : NegativeFlank * -z;
            else
            {
                // The integral of  mean * tanh(z / s) + half-difference,  which is the smoothed |z| of an asymmetric
                // V: its slope is exactly +PositiveFlank far up one side and -NegativeFlank far down the other.
                float mean = (PositiveFlank + NegativeFlank) * 0.5f;
                float halfDifference = (PositiveFlank - NegativeFlank) * 0.5f;
                across = mean * Smoothing * LogCosh(z / Smoothing) + halfDifference * z;
            }
            return BaseHeight + FloorGrade * x + across;
        }

        // log(cosh(u)) without the overflow: cosh saturates a float at about u = 89 and this fixture reads out to 2 m
        // at a smoothing of 0.10, which is u = 20 and already past where exp(2u) is comfortable.
        static float LogCosh(float u)
        {
            float a = MathF.Abs(u);
            return a + MathF.Log(1f + MathF.Exp(-2f * a)) - 0.6931472f;
        }

        /// <summary>The ground normal, as the SAME capsule-radius central difference the engine's height plane reads
        /// (and as Ruinborne's own GroundNormal computes it). Like the neighbouring attractor fixture this is
        /// deliberately NOT a #468 mismatched-pair case: the two delegates describe one surface, and the whole
        /// question here is what WIDTH that one surface is read at.</summary>
        public Vector3 NormalAt(float x, float z)
        {
            float r = Tuning.CapsuleRadius, inv = 0.5f / r;
            float gx = (Height(x + r, z) - Height(x - r, z)) * inv;
            float gz = (Height(x, z + r) - Height(x, z - r)) * inv;
            float n = 1f / MathF.Sqrt(gx * gx + gz * gz + 1f);
            return new Vector3(-gx * n, n, -gz * n);
        }
    }

    // The 1.67 hard V is the shape the review measured the blocker on. The two tanh valleys carry the ends of the
    // flank-ratio range and the two smoothings, so a build that only satisfies the corner case reds here.
    static Trough V167 => new("V 2.0/1.2", 2.0f, 1.2f, 0f);
    static Trough Smooth10 => new("tanh 0.10 m, 2.4/1.2", 2.4f, 1.2f, 0.10f);
    static Trough Smooth25 => new("tanh 0.25 m, 1.56/1.2", 1.56f, 1.2f, 0.25f);

    // ---- The ride ----

    // Along-face travel is the +x component and nothing else, because the trough axis IS +x by construction. Signed,
    // so a tick pushed backwards subtracts: the wide face aims ACROSS the trough here, and a path length would read
    // that as progress.
    readonly record struct Ride(float Efficiency, int LongestStall, int Airborne, int Flips, string Measured);

    // Hold a heading from the floor line and report what the walk looked like. Lean is measured from the trough axis:
    // negative leans into the shallower flank, positive into the steeper one.
    static Ride WalkAlong(in Trough trough, float leanDegrees, bool run, float hz, float seconds)
    {
        MoveTuning t = Tuning;
        float dt = 1f / hz;
        int ticks = (int)MathF.Round(seconds * hz);
        float speed = run ? t.RunSpeed : t.WalkSpeed;
        float lean = leanDegrees * MathF.PI / 180f;
        var dir = new Vector2(MathF.Cos(lean), MathF.Sin(lean));
        var s = new MoveState
        {
            Position = new Vector3(0f, trough.Height(0f, 0f) + t.CapsuleHalfHeight, 0f),
            Grounded = true,
        };

        float travel = 0f;
        int longestStall = 0, stall = 0, stalled = 0, airborne = 0, flips = 0;
        bool previous = true;
        for (int i = 0; i < ticks; i++)
        {
            float x = s.Position.X, z = s.Position.Z;
            MoveState next = CharacterMovement.StepTowards(s, dir, run, dt, trough.Height, t, trough.NormalAt);
            float dx = next.Position.X - x, dz = next.Position.Z - z;
            travel += dx;
            if (MathF.Sqrt(dx * dx + dz * dz) < 1e-4f)
            {
                stalled++;
                stall++;
                if (stall > longestStall) longestStall = stall;
            }
            else stall = 0;

            if (!next.Grounded) airborne++;
            if (next.Grounded != previous) flips++;
            previous = next.Grounded;
            s = next;
        }

        float commanded = speed * MathF.Cos(lean) * dt * ticks;
        string measured = $"{trough.Name}, lean {leanDegrees:F0} deg, {(run ? "run" : "walk")} at {hz:F0} Hz: "
                          + $"along-axis {travel:F3} of a commanded {commanded:F3} ({travel / commanded:P1}), longest "
                          + $"stall {longestStall}/{ticks} ticks, stalled {stalled}, airborne {airborne}, flips "
                          + $"{flips}, ended ({s.Position.X:F3}, {s.Position.Z:F3})";
        return new Ride(travel / commanded, longestStall, airborne, flips, measured);
    }

    // ---- The sweep ----

    public static IEnumerable<object[]> Rides()
    {
        foreach (string trough in new[] { "V167", "Smooth10", "Smooth25", "Smooth25Steep" })
            foreach (bool run in new[] { false, true })
                foreach (float hz in new[] { 15f, 30f })
                    yield return new object[] { trough, run, hz };
    }

    static (Trough trough, float lean) Case(string name) => name switch
    {
        // Leaning into the SHALLOW flank, across the flank-ratio range. The lean is per shape because the bite window
        // moves with the ratio (see the file header), and each was read off the census rather than guessed.
        "V167" => (V167, -20f),
        "Smooth10" => (Smooth10, -30f),
        "Smooth25" => (Smooth25, -40f),
        // The other direction, on the shape whose ratio is small enough that the shallow side comes through clean.
        "Smooth25Steep" => (Smooth25, 55f),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "no trough case by that name"),
    };

    // WHAT EACH ROW IS PINNED TO: THE PRE-#501 ENGINE'S OWN RIDE, plus or minus five points. The upper side matters as
    // much as the lower here, for the reason the neighbouring fixture gives: a bank-hug spends part of its cycle on a
    // slide tick whose carry is the fall line plus the contour, so a build can buy along-axis travel by turning the
    // walk into a slide, and an open-topped band would not notice.
    //
    //   case            speed  rate    pre-#501   17.32.0 as first shipped        this build
    //   V167            walk   15 Hz     0.909    0.014, 144/150 ticks dead         0.909
    //   V167            walk   30 Hz     0.908    0.017, 291/300 ticks dead         0.908
    //   V167            run    15 Hz     0.909    0.290, no stall                   0.909
    //   V167            run    30 Hz     0.908    0.007, 294/300 ticks dead         0.908
    //   Smooth10        walk   15 Hz     0.852    0.013, 147/150 ticks dead         0.852
    //   Smooth10        walk   30 Hz     0.849    0.849, no stall                   0.849
    //   Smooth10        run    15 Hz     0.823    0.007, 146/150 ticks dead         0.823
    //   Smooth10        run    30 Hz     0.852    0.007, 297/300 ticks dead         0.852
    //   Smooth25        walk   15 Hz     0.781    0.035,  50/150 ticks dead         0.781
    //   Smooth25        walk   30 Hz     0.776    0.021,  68/300 ticks dead         0.776
    //   Smooth25        run    15 Hz     0.785    0.241,  18/150 ticks dead         0.785
    //   Smooth25        run    30 Hz     0.780    0.024,  50/300 ticks dead         0.780
    //   Smooth25Steep   walk   15 Hz     0.678    0.004,  32/150 ticks dead         0.678
    //   Smooth25Steep   walk   30 Hz     0.653    0.005,  76/300 ticks dead         0.653
    //   Smooth25Steep   run    15 Hz     0.700    0.229,  12/150 ticks dead         0.700
    //   Smooth25Steep   run    30 Hz     0.678    0.006,  32/300 ticks dead         0.678
    //
    // The last column is not a coincidence and is the strongest statement this file makes: on every one of these rows
    // the shipped build reproduces the pre-#501 ride to every digit printed, because the doubt band puts all sixteen
    // of them outside the wide read entirely. The one row 17.32.0 got right (Smooth10 walk 30 Hz, whose settled
    // column happens to sit where the two faces agree) is pinned the same way as the other fifteen, and the three
    // that read as a crawl rather than a park - the run at 15 Hz rows at 0.290, 0.241 and 0.229 - fail the floor
    // exactly as the parks do, because a walker crawling at a quarter speed along an open trough is also the bug.
    readonly record struct Bounds(float LoEfficiency, float HiEfficiency);

    static Bounds Expected(string name, bool run, float hz) => (name, run, hz) switch
    {
        ("V167", false, 15f) => new Bounds(0.86f, 0.96f),
        ("V167", false, 30f) => new Bounds(0.86f, 0.96f),
        ("V167", true, 15f) => new Bounds(0.86f, 0.96f),
        ("V167", true, 30f) => new Bounds(0.86f, 0.96f),
        ("Smooth10", false, 15f) => new Bounds(0.80f, 0.90f),
        ("Smooth10", false, 30f) => new Bounds(0.80f, 0.90f),
        ("Smooth10", true, 15f) => new Bounds(0.77f, 0.87f),
        ("Smooth10", true, 30f) => new Bounds(0.80f, 0.90f),
        ("Smooth25", false, 15f) => new Bounds(0.73f, 0.83f),
        ("Smooth25", false, 30f) => new Bounds(0.73f, 0.83f),
        ("Smooth25", true, 15f) => new Bounds(0.73f, 0.84f),
        ("Smooth25", true, 30f) => new Bounds(0.73f, 0.83f),
        ("Smooth25Steep", false, 15f) => new Bounds(0.63f, 0.73f),
        ("Smooth25Steep", false, 30f) => new Bounds(0.60f, 0.70f),
        ("Smooth25Steep", true, 15f) => new Bounds(0.65f, 0.75f),
        ("Smooth25Steep", true, 30f) => new Bounds(0.63f, 0.73f),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name,
            "no band is pinned for this (trough, speed, tick rate) - measure it before sweeping it"),
    };

    [Theory]
    [MemberData(nameof(Rides))]
    public void An_asymmetric_trough_keeps_the_narrow_face_that_is_still_telling_the_truth(string name, bool run,
        float hz)
    {
        (Trough trough, float lean) = Case(name);
        Ride r = WalkAlong(trough, lean, run, hz, seconds: 10f);
        _out.WriteLine(r.Measured);
        Bounds b = Expected(name, run, hz);

        // The park is the report, so both halves of it are asserted: a build that crawled would clear a floor alone
        // and a build that jittered in place would clear a stall bound alone.
        Assert.True(r.Efficiency >= b.LoEfficiency,
            $"a wide face overruled a healthy narrow one and ate the along-axis travel: {r.Measured}");
        Assert.True(r.LongestStall <= MaxStall, $"the walk parked in the trough: {r.Measured}");

        // And the shape of the ride, so a build cannot buy the travel back by sliding down a flank.
        Assert.True(r.Efficiency <= b.HiEfficiency,
            $"the trough handed the walker free along-axis speed: {r.Measured}");
        Assert.True(r.Airborne == 0, $"the trough put the walker in the air: {r.Measured}");
        Assert.True(r.Flips == 0, $"the ride flickered its footing: {r.Measured}");
    }

    // Every row here is footed for its whole ride on the pre-#501 engine, which is what makes a hard zero the right
    // bound for the flips and the airborne count and lets the stall bound be this tight. The floor of the trough is
    // walkable by construction and the walk never leaves it, so a single stalled tick is already a wall contact that
    // refused a move the pre-#501 engine committed. Eight is float slack, not a budget.
    const int MaxStall = 8;

    // ---- Why the wide read is wrong here, measured from the surface rather than from the engine ----

    // WHAT THIS TEST IS FOR. The rides show the walk is not parked. They cannot show WHY the narrow face deserved to
    // keep its job, because a ride only reports where it ended up. This walks the geometry directly and reports what
    // each read says at the column a leaning walker settles on, which is the whole argument for gating the opposition
    // test: the narrow face is keeping most of the command, so it is not the anti-parallel facet #501 is about, and
    // the two faces nonetheless disagree by more than a right angle.
    //
    // Deliberately computed from the height field rather than from CharacterMovement, so it is a statement about the
    // surface and not a re-derivation of the rule under test.
    [Theory]
    [InlineData("V167")]
    [InlineData("Smooth10")]
    [InlineData("Smooth25")]
    [InlineData("Smooth25Steep")]
    public void The_narrow_face_keeps_most_of_the_command_while_disagreeing_with_the_wide_one(string name)
    {
        (Trough trough, float lean) = Case(name);
        float rad = lean * MathF.PI / 180f;
        var v = new Vector2(MathF.Cos(rad) * Tuning.WalkSpeed, MathF.Sin(rad) * Tuning.WalkSpeed);
        float speed = v.Length();

        // The columns a wall contact on this flank can actually READ. A walker is stopped at the gate contour and
        // aims one step past it, so the band is the contour to one walking step beyond, and nothing further out is
        // reachable at the speeds this file sweeps. Reported at the WORST disagreement inside that band, because one
        // such column is all the ungated trigger needs: the walk drifts along the flank and the first one it reaches
        // is where it stops.
        float gateZ = 0f;
        for (int i = 0; i <= 2000 && !PastGate(trough, gateZ); i++) gateZ = MathF.Sign(lean) * i * 0.001f;
        float reach = Tuning.WalkSpeed / 30f;
        float worstBetween = 0f, atZ = 0f, atNarrow = 0f, atWide = 0f;
        for (int i = 0; i <= 200; i++)
        {
            float z = gateZ + MathF.Sign(lean) * i * 0.001f;
            if (MathF.Abs(z - gateZ) > reach) break;
            if (!PastGate(trough, z)) continue;
            (float nx, float nz) = Contour(trough, z, Tuning.CapsuleRadius, v);
            (float wx, float wz) = Contour(trough, z, Tuning.CapsuleRadius * ShippedWideScale, v);
            float narrow = MathF.Sqrt(nx * nx + nz * nz) / speed;
            if (narrow <= ShippedDoubtFraction) continue;     // inside the band, where the trigger may legitimately fire
            float between = MathF.Abs(MathF.Atan2(nz, nx) - MathF.Atan2(wz, wx)) * 180f / MathF.PI;
            if (between > 180f) between = 360f - between;
            if (between <= worstBetween) continue;
            worstBetween = between;
            atZ = z;
            atNarrow = narrow;
            atWide = MathF.Sqrt(wx * wx + wz * wz) / speed;
        }

        string measured = $"{trough.Name}, lean {lean:F0} deg: the worst past-gate column is z {atZ:F4}, where the "
                          + $"narrow face keeps {atNarrow:F4} of the command, the wide face keeps {atWide:F4}, and "
                          + $"the two travels are {worstBetween:F1} degrees apart";
        _out.WriteLine(measured);

        // Both halves are the bug. A healthy narrow face is what makes the override wrong, and a disagreement past a
        // right angle is what makes the ungated trigger fire on it. Either one alone is an ordinary contact.
        Assert.True(atNarrow > ShippedDoubtFraction,
            $"no past-gate column here has a healthy narrow face, so this fixture has stopped reproducing #501: "
            + measured);
        Assert.True(worstBetween > 90f,
            $"the two faces no longer disagree past a right angle where the narrow one is healthy, so the ungated "
            + $"trigger would not have fired and this fixture has stopped reproducing #501: {measured}");
    }

    // Mirrors CharacterMovement's own constants, which are private. Restated on purpose rather than read through a
    // seam: a test that silently tracked the engine could not catch either number moving.
    const float ShippedWideScale = 5f;
    const float ShippedDoubtFraction = 0.15f;

    static bool PastGate(in Trough trough, float z)
    {
        Vector3 n = trough.NormalAt(0.2f, z);
        return MathF.Acos(Math.Clamp(n.Y, -1f, 1f))
               > Tuning.MaxSlopeRadians + Tuning.TractionHysteresisRadians;
    }

    /// <summary>What a projection onto the face read at half-width <paramref name="r"/> would leave of
    /// <paramref name="v"/>, as the engine computes it: the height plane's outward is the normalized downhill of the
    /// central difference, and the projection removes the component along it.</summary>
    static (float x, float z) Contour(in Trough trough, float z, float r, Vector2 v)
    {
        float inv = 0.5f / r;
        float gx = (trough.Height(0.2f + r, z) - trough.Height(0.2f - r, z)) * inv;
        float gz = (trough.Height(0.2f, z + r) - trough.Height(0.2f, z - r)) * inv;
        float m = MathF.Sqrt(gx * gx + gz * gz);
        if (m < 1e-9f) return (v.X, v.Y);
        float fx = -gx / m, fz = -gz / m;
        float into = v.X * fx + v.Y * fz;
        return (v.X - into * fx, v.Y - into * fz);
    }
}
