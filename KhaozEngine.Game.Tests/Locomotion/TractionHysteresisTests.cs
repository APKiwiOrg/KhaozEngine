using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

// THE GATE BOUNDARY IS NOT A CLIFF EDGE (#475, 17.30.0). Sixth round of the steep-terrain chain, and the first that
// is not about an exploit: the previous five all asked what a character may TAKE from a face, and this one asks what
// the model does to ground sitting right ON the walkable threshold.
//
// Two failures, one boundary. MaxSlopeRadians was a bare per-tick binary, so terrain whose columns straddle it
// alternated walk ticks and slide ticks (measured on a Ruinborne bank: 43 footing flips in 330 ticks, stalled 2.73 m
// up a 7.6 m climb). And ResolveSlide committed the full fall-line projection of gravity the instant a surface
// crossed the gate, so one degree too steep behaved exactly like eighty.
//
// The fixtures here are synthetic rather than Ruinborne data, and they are built to the SHAPE the issue measured: a
// bank whose slope oscillates about two degrees either side of the gate, and uniform planar faces at chosen offsets
// past it. Every case that measures the fix also runs its own CONTROL with the knobs at zero, which is the
// pre-17.30.0 model bit for bit - so the red is in the suite permanently instead of only in a commit message.
public class TractionHysteresisTests
{
    const float Dt = 1f / 30f;

    static MoveTuning Tuning => MoveTuning.Default;

    // The pre-17.30.0 model exactly: no hysteresis band, no friction ramp. Both knobs read 0 the way a bare
    // `default(MoveTuning)` reads them, and both mechanisms are documented to degrade to the old behaviour there.
    static MoveTuning NoHysteresisNoFriction => MoveTuning.Default with
    {
        TractionHysteresisRadians = 0f,
        SlideFrictionRampRadians = 0f,
    };

    static float Deg(float degrees) => degrees * MathF.PI / 180f;

    const float EdgeX = 5f;

    // ---- The chatter bank: a slope that straddles the gate by about +-2 degrees ----
    //
    // Height rises with a gradient of 1 + WobbleAmplitude * cos(2*pi*x / WobbleLength), so the slope ANGLE runs
    // atan(0.93) = 42.9 deg to atan(1.07) = 46.9 deg against the 45 degree gate: the issue's measured bank shape,
    // whose columns ran 39.6 to 41.8 against a 40 degree gate. The wavelength is 2 m, so a walk at 6 m/s and 30 Hz
    // (0.2 m a tick) crosses the gate every five ticks - the same rate the reported footing string flips at.
    const float BankLength = 8f;             // the issue's bank was 7.6 m
    const float WobbleAmplitude = 0.07f;
    const float WobbleLength = 2f;

    static float BankGradient(float d) => 1f + WobbleAmplitude * MathF.Cos(2f * MathF.PI * d / WobbleLength);

    static float Bank(float x, float z)
    {
        float d = x - EdgeX;
        if (d <= 0f) return 0f;
        float top = BankLength + WobbleAmplitude * WobbleLength / (2f * MathF.PI) * MathF.Sin(2f * MathF.PI * BankLength / WobbleLength);
        if (d >= BankLength) return top;
        return d + WobbleAmplitude * WobbleLength / (2f * MathF.PI) * MathF.Sin(2f * MathF.PI * d / WobbleLength);
    }

    // The HONEST normal of that surface: the fixture is about the gate boundary, so its height field and its
    // classification must agree exactly (a disagreement between the two is #468's fixture, not this one).
    static Vector3 BankNormal(float x, float z)
    {
        float d = x - EdgeX;
        if (d <= 0f || d >= BankLength) return Vector3.UnitY;
        return Vector3.Normalize(new Vector3(-BankGradient(d), 1f, 0f));
    }

    static Func<float, float, float> BankGround => Bank;
    static Func<float, float, Vector3> BankNormals => BankNormal;

    static MoveCommand East(bool run = false) => new(new Vector2(1f, 0f), run, cameraYaw: 0f, jump: false);

    // Walk east up the bank from the flat and report what the climb looked like: how many times footing flipped after
    // the character first set foot on the bank, how far up it got, and the footing string itself for the failure
    // message (the same shape the issue reported its measurement in).
    static (int flips, float topFeet, float endX, string footing) ClimbTheBank(in MoveTuning t, int ticks)
    {
        var s = new MoveState
        {
            Position = new Vector3(EdgeX - 0.5f, Bank(EdgeX - 0.5f, 0f) + t.CapsuleHalfHeight, 0f),
            Grounded = true,
        };
        var chars = new char[ticks];
        int flips = 0;
        bool onset = false;
        bool previous = true;
        float topFeet = 0f;
        for (int i = 0; i < ticks; i++)
        {
            s = CharacterMovement.Step(s, East(), Dt, BankGround, t, BankNormals);
            chars[i] = s.Grounded ? 'F' : '.';
            topFeet = MathF.Max(topFeet, s.Position.Y - t.CapsuleHalfHeight);
            // Onset is the first tick spent ON the bank, so the flat approach cannot pad the count either way.
            if (!onset && s.Position.X > EdgeX) { onset = true; previous = s.Grounded; continue; }
            if (onset && s.Grounded != previous) flips++;
            previous = s.Grounded;
        }
        return (flips, topFeet, s.Position.X, new string(chars));
    }

    [Fact]
    public void A_bank_straddling_the_gate_chatters_without_hysteresis()
    {
        // THE RED, kept in the suite. This is the reported bug reproduced on a synthetic bank: with the band at zero
        // the walkability decision is re-taken from scratch every tick, so the character alternates walk ticks and
        // full-gravity slide ticks and gains and loses the same ground for as long as you hold the key.
        var t = NoHysteresisNoFriction;
        (int flips, float topFeet, float endX, string footing) = ClimbTheBank(t, 600);

        string measured = $"flips {flips}, top {topFeet:F3} m, end x {endX:F3}";
        Assert.True(flips > 20, $"the control did not reproduce the reported chatter: {measured}\n{footing}");
        // And it never gets up the bank: 600 ticks is 120 m of commanded travel against an 8 m climb.
        Assert.True(topFeet < BankLength - 1f, $"the control climbed the bank after all: {measured}");
    }

    [Fact]
    public void A_bank_straddling_the_gate_holds_one_continuous_footing_decision()
    {
        // THE GREEN. Same bank, same input, hysteresis at its shipped default: the band covers the +-2 degree
        // straddle, so the character keeps its feet for the whole climb - one decision, not a strobe - and walks to
        // the top at walking pace instead of stalling part-way up.
        var t = Tuning;
        (int flips, float topFeet, float endX, string footing) = ClimbTheBank(t, 600);

        string measured = $"flips {flips}, top {topFeet:F3} m, end x {endX:F3}";
        Assert.Equal(0, flips);
        Assert.True(topFeet >= BankLength - 1e-2f, $"the walk stalled on the bank: {measured}\n{footing}");
        Assert.True(endX > EdgeX + BankLength, $"the walk never crossed the bank: {measured}");
    }

    [Fact]
    public void The_climb_is_stall_free_at_walking_pace()
    {
        // STALL-FREE means the progress is the walk, not merely that it eventually arrives. The bank is 8 m of
        // horizontal at ~45 degrees, so a 6 m/s walk covers it in 8 / 6 = 1.33 s, and anything materially slower is
        // ground being handed back. Measured against the control in the same shape below.
        var t = Tuning;
        var s = new MoveState
        {
            Position = new Vector3(EdgeX - 0.5f, 0f + t.CapsuleHalfHeight, 0f),
            Grounded = true,
        };
        int ticksToTop = -1;
        for (int i = 0; i < 600 && ticksToTop < 0; i++)
        {
            s = CharacterMovement.Step(s, East(), Dt, BankGround, t, BankNormals);
            if (s.Position.X >= EdgeX + BankLength) ticksToTop = i + 1;
        }

        // 0.5 m of flat approach plus 8 m of bank at 6 m/s is 1.417 s, or 43 ticks at 30 Hz. The bound is twice that,
        // so it fails on any real stall while staying well clear of the per-tick rounding of the arrival tick.
        Assert.InRange(ticksToTop, 1, 86);
    }

    // ---- Hysteresis is ASYMMETRIC: keeping footing is not the same as gaining it ----

    // A uniform planar face east of EdgeX at a chosen angle, with the normal that describes exactly that surface.
    static (Func<float, float, float> ground, Func<float, float, Vector3> normals) Face(float degrees)
    {
        float grade = MathF.Tan(Deg(degrees));
        Vector3 n = Vector3.Normalize(new Vector3(-grade, 1f, 0f));
        return ((x, z) => x < EdgeX ? 0f : (x - EdgeX) * grade,
                (x, z) => x < EdgeX ? Vector3.UnitY : n);
    }

    const float ProbeX = EdgeX + 1.5f;   // the column both paths below are compared on

    [Fact]
    public void A_walker_keeps_footing_on_gate_plus_two_ground_where_a_lander_gets_none()
    {
        // THE ASYMMETRY, on ONE column. 47 degrees is two past the 45 degree gate and inside the 3 degree band, so
        // whether the ground grants footing depends entirely on whether the body arrived with any. A walk up from the
        // adjacent flat keeps it. A drop out of the air onto the identical column gets nothing and slides, which is
        // what keeps the band from being a wider gate: it can only ever HOLD footing, never hand it out.
        var t = Tuning;
        (Func<float, float, float> ground, Func<float, float, Vector3> normals) = Face(47f);

        // (a) THE WALKER: on the flat, walking east onto the face.
        var walker = new MoveState
        {
            Position = new Vector3(EdgeX - 0.2f, t.CapsuleHalfHeight, 0f),
            Grounded = true,
        };
        bool walkerLostFooting = false;
        int walkerTicksPastProbe = 0;
        for (int i = 0; i < 120; i++)
        {
            walker = CharacterMovement.Step(walker, East(), Dt, ground, t, normals);
            if (walker.Position.X > EdgeX && !walker.Grounded) walkerLostFooting = true;
            if (walker.Position.X >= ProbeX) walkerTicksPastProbe++;
        }
        Assert.False(walkerLostFooting, $"the walker lost footing inside the band at x={walker.Position.X:F3}");
        Assert.True(walkerTicksPastProbe > 0, "the walker never reached the probe column");
        Assert.True(walker.Grounded, "the walker did not end the climb standing");

        // (b) THE LANDER: dropped onto the SAME column from 3 m up, with the coyote window long expired. Driven IDLE
        // rather than east, because the two paths must differ only in how the body arrived - a lander that is also
        // holding a movement key becomes a walker the moment it reaches the flat, and would then legitimately climb
        // back onto the face with the footing the flat gave it.
        var lander = new MoveState
        {
            Position = new Vector3(ProbeX, ground(ProbeX, 0f) + t.CapsuleHalfHeight + 3f, 0f),
            Grounded = false,
            TimeSinceGrounded = 1f,
        };
        var idle = new MoveCommand(Vector2.Zero, run: false, cameraYaw: 0f, jump: false);
        int landerGrantsOnTheFace = 0;
        float landerStartFeet = lander.Position.Y - t.CapsuleHalfHeight;
        for (int i = 0; i < 120; i++)
        {
            lander = CharacterMovement.Step(lander, idle, Dt, ground, t, normals);
            if (lander.Position.X > EdgeX && (lander.Grounded || lander.SupportGranted)) landerGrantsOnTheFace++;
        }

        Assert.Equal(0, landerGrantsOnTheFace);
        // And it did not merely hang there: no footing means it slid down the fall line, which on this face means
        // west and down. Friction makes that gentle, not absent.
        Assert.True(lander.Position.Y - t.CapsuleHalfHeight < landerStartFeet - 1f,
            $"the lander never slid: feet {lander.Position.Y - t.CapsuleHalfHeight:F3} from {landerStartFeet:F3}");
        Assert.True(lander.Position.X < ProbeX, $"the lander slid uphill, x={lander.Position.X:F3}");
    }

    [Fact]
    public void Ground_past_the_band_refuses_footing_to_a_walker_too()
    {
        // THE BAND'S OWN CEILING, and the reason hysteresis is not simply a wider gate. 49 degrees is one degree past
        // gate plus band, so even a character that walks onto it with perfect footing is refused and slides straight
        // back off. The steepest ground a body can stand on is exactly gate + band, by any route.
        //
        // THE HEIGHT BOUND IS THE LAUNCH ENERGY, not a StepHeight, for the reason #442's own fixtures record: a
        // contact deletes only the into-surface component, so the run INTO the face survives as in-plane motion and
        // the signed fall line cashes it as altitude, worth v^2 / (2 * Gravity) at any face angle. At run speed that
        // is 2.88 m, plus the one StepHeight a grounded tick may legitimately step onto and a tick of slack. What the
        // run may NOT do is find footing up there, which is the assertion this case is actually for.
        var t = Tuning;
        (Func<float, float, float> ground, Func<float, float, Vector3> normals) = Face(49f);

        var s = new MoveState
        {
            Position = new Vector3(EdgeX - 0.2f, t.CapsuleHalfHeight, 0f),
            Grounded = true,
        };
        float ceiling = t.RunSpeed * t.RunSpeed / (2f * t.Gravity) + t.StepHeight + 0.05f;
        for (int i = 0; i < 300; i++)
        {
            s = CharacterMovement.Step(s, East(run: true), Dt, ground, t, normals);
            if (s.Position.X > EdgeX)
                Assert.False(s.Grounded, $"tick {i} found footing past the band at x={s.Position.X:F3}");
            Assert.True(s.Position.Y - t.CapsuleHalfHeight <= ceiling,
                $"tick {i} climbed past the launch energy, feetY={s.Position.Y - t.CapsuleHalfHeight:F5} " +
                $"against a ceiling {ceiling:F5}");
        }
        Assert.True(s.Position.X < EdgeX, $"the run ended on the face, x={s.Position.X:F5}");
    }

    // ---- The friction ramp ----

    // Seed a character AT REST in slide contact with a uniform face at `degrees` and step it, reporting the vertical
    // velocity after `ticks`. At rest the fall-line speed starts at zero, so the whole of the resulting motion is the
    // ramp under test.
    static (float vVel, float dropped) SlideFromRest(in MoveTuning t, float degrees, int ticks, float startOffset = 6f)
    {
        (Func<float, float, float> ground, Func<float, float, Vector3> normals) = Face(degrees);
        float StartX = EdgeX + startOffset;
        var s = new MoveState
        {
            Position = new Vector3(StartX, ground(StartX, 0f) + t.CapsuleHalfHeight, 0f),
            Grounded = false,
            TimeSinceGrounded = 1f,
        };
        float startFeet = s.Position.Y - t.CapsuleHalfHeight;
        var idle = new MoveCommand(Vector2.Zero, run: false, cameraYaw: 0f, jump: false);
        for (int i = 0; i < ticks; i++) s = CharacterMovement.Step(s, idle, Dt, ground, t, normals);
        return (s.VerticalVelocity, startFeet - (s.Position.Y - t.CapsuleHalfHeight));
    }

    [Theory]
    [InlineData(46f, 0.125f)]    // gate + 1: an eighth of the ramp
    [InlineData(49f, 0.5f)]      // gate + half the ramp
    [InlineData(53f, 1f)]        // gate + the whole ramp: full gravity from here up
    [InlineData(75f, 1f)]        // far past it, unchanged from every release before friction
    public void The_fall_line_acceleration_follows_the_friction_ramp(float degrees, float expectedScale)
    {
        // THE RAMP, MEASURED RATHER THAN READ OFF THE SOURCE. One tick from rest on a planar face: the fall-line
        // speed becomes Gravity * sin(slope) * scale * dt, and its vertical component is that times sin(slope) again
        // (the down-slope tangent's Y is -sin(slope)). So the whole formula is observable in MoveState alone.
        //
        // THE VERDICT IS A RATIO against the same tick with friction off, not an absolute against the formula, and
        // that is deliberate. The slide re-seats the capsule onto the height field within the contact skin, so the
        // committed vertical carries the rounding of a difference of WORLD heights - about 6e-5 m/s on this fixture at
        // 75 degrees, where the face stands 22 m up. That noise is common to both tunings and divides straight out,
        // which leaves the ratio measuring the one thing under test. The absolute check below keeps the formula
        // honest at a tolerance sized to that noise rather than to the answer.
        var t = Tuning;
        float h = MathF.Sin(Deg(degrees));
        (float vVel, _) = SlideFromRest(t, degrees, 1);
        (float unscaled, _) = SlideFromRest(NoHysteresisNoFriction, degrees, 1);

        // Four places rather than exactness, because the ramp divides an ANGLE by the band: the plane's own float
        // noise (a few microradians, read off a height field 7 to 22 m up) lands on the scale multiplied by
        // 1 / SlideFrictionRampRadians, so the interior rows carry about 4e-5 of it. The clamped rows are exact.
        Assert.Equal(expectedScale, vVel / unscaled, 4);
        float expected = -t.Gravity * h * h * Dt;
        Assert.True(MathF.Abs(unscaled - expected) <= 2e-4f * MathF.Abs(expected),
            $"the unscaled tick is not gravity's own fall-line projection: {unscaled:F7} against {expected:F7}");
    }

    [Fact]
    public void A_face_one_degree_past_the_gate_slides_gently()
    {
        // WHAT THE RAMP IS FOR, stated as the feel it buys. One second of sliding from rest on a 46 degree face: the
        // pre-friction model drops the character over six metres, which is a cliff. With the ramp it is under a
        // metre, which is losing your footing on a steep bank. The face is still unstandable and the character still
        // never gets footing on it - it just stops being punished as though it had walked off a precipice.
        // The seed sits 12 m along the face so the CONTROL is still on it after a second: at 6 m it reaches the toe
        // inside the window and the comparison would be measuring the fixture's length instead of the ramp.
        var t = Tuning;
        (float vVel, float dropped) = SlideFromRest(t, 46f, 30, startOffset: 12f);
        (float unscaledV, float unscaledDrop) = SlideFromRest(NoHysteresisNoFriction, 46f, 30, startOffset: 12f);

        string measured = $"friction: {dropped:F3} m at {-vVel:F3} m/s, none: {unscaledDrop:F3} m at {-unscaledV:F3} m/s";
        Assert.True(dropped < 1f, $"the gentle slide was not gentle: {measured}");
        Assert.True(unscaledDrop > 6f, $"the control was not the full-gravity slide: {measured}");
        Assert.True(unscaledDrop > 6f * dropped, $"the ramp bought less than the eighth it promises: {measured}");
    }

    [Fact]
    public void Friction_never_lengthens_a_rising_ride_up_a_face()
    {
        // THE ONE RULE THAT KEEPS FRICTION FROM BEING AN EXPLOIT. A ramp applied to the whole of gravity would scale
        // the DECELERATION of a rising graze too, and a body that decelerates at an eighth of gravity rides eight
        // times as high - unbounded as the scale approaches zero at the gate. So the up half keeps full-strength
        // gravity, and the reach of a running jump onto a marginal face is exactly what it was before friction
        // existed. Measured on the 46 degree face the #442 fixtures use as their best converter.
        var t = Tuning;
        (Func<float, float, float> ground, Func<float, float, Vector3> normals) = Face(46f);

        float PeakOf(in MoveTuning tune)
        {
            var s = new MoveState
            {
                Position = new Vector3(EdgeX - 0.05f, tune.CapsuleHalfHeight, 0f),
                Grounded = true,
            };
            var jump = new MoveCommand(new Vector2(1f, 0f), run: true, cameraYaw: 0f, jump: true);
            float peak = 0f;
            for (int i = 0; i < 300; i++)
            {
                s = CharacterMovement.Step(s, jump, Dt, ground, tune, normals);
                peak = MathF.Max(peak, s.Position.Y - tune.CapsuleHalfHeight);
            }
            return peak;
        }

        float withFriction = PeakOf(t);
        float without = PeakOf(NoHysteresisNoFriction);
        string measured = $"with friction {withFriction:F4} m, without {without:F4} m";
        // The energy bound is the same number in both cases, and the measured peaks agree to well inside a
        // millimetre, because the rising half of the ride is byte-for-byte the same arithmetic.
        Assert.True(withFriction <= without + 1e-3f, $"friction lengthened the ride up the face: {measured}");
    }

    [Fact]
    public void Both_knobs_at_zero_reproduce_the_previous_model_on_a_steep_face()
    {
        // THE COMPATIBILITY CLAIM, pinned. A game that sets neither knob (or reads a bare `default(MoveTuning)`, which
        // gets 0 for both) must have the 17.29.0 model back exactly. The face is 78.7 degrees, past every band and
        // ramp, so the ONLY thing that could differ between the two tunings on it is the arithmetic itself.
        var t = NoHysteresisNoFriction;
        (Func<float, float, float> ground, Func<float, float, Vector3> normals) = Face(78.7f);
        var shipped = Tuning;

        const float StartX = EdgeX + 4f;
        MoveState a = new()
        {
            Position = new Vector3(StartX, ground(StartX, 0f) + t.CapsuleHalfHeight, 0f),
            Grounded = false,
            TimeSinceGrounded = 1f,
        };
        MoveState b = a;
        for (int i = 0; i < 200; i++)
        {
            a = CharacterMovement.Step(a, East(), Dt, ground, t, normals);
            b = CharacterMovement.Step(b, East(), Dt, ground, shipped, normals);
            Assert.Equal(a.Position.X, b.Position.X);
            Assert.Equal(a.Position.Y, b.Position.Y);
            Assert.Equal(a.VerticalVelocity, b.VerticalVelocity);
            Assert.Equal(a.Grounded, b.Grounded);
        }
    }

    [Fact]
    public void Walkable_ground_well_under_the_gate_is_untouched_by_either_knob()
    {
        // The byte-identity that matters most, because it is the ground every game actually spends its time on. A
        // 20 degree hill is 25 degrees under the gate and 28 under the band, so neither mechanism can have an opinion
        // about it, and the walk across it must be identical to the tick with both knobs at zero.
        var t = Tuning;
        var off = NoHysteresisNoFriction;
        float grade = MathF.Tan(Deg(20f));
        Func<float, float, float> ground = (x, z) => x * grade;
        Vector3 n = Vector3.Normalize(new Vector3(-grade, 1f, 0f));
        Func<float, float, Vector3> normals = (x, z) => n;

        MoveState a = new() { Position = new Vector3(0f, t.CapsuleHalfHeight, 0f), Grounded = true };
        MoveState b = a;
        for (int i = 0; i < 200; i++)
        {
            a = CharacterMovement.Step(a, East(run: true), Dt, ground, t, normals);
            b = CharacterMovement.Step(b, East(run: true), Dt, ground, off, normals);
            Assert.Equal(a.Position.X, b.Position.X);
            Assert.Equal(a.Position.Y, b.Position.Y);
            Assert.True(a.Grounded, $"tick {i} lost footing on a 20 degree hill");
        }
    }
}
