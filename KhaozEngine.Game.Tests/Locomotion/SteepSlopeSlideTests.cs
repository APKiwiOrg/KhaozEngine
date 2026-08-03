using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

// STEEP GROUND IS A SURFACE YOU SLIDE ON, NOT A WALL YOU ARE DENIED AT (#442, the 17.28.0 model).
//
// These tests were the refusal-era slope-gate suite (#369, then #440). Two playtests voted the gate down: the
// 17.26.0 direction-aware gate let a repeated jump ratchet up a sheer face, and the 17.26.1 fence that closed
// that blocked sideways movement into a face while jumping, which reads as an invisible wall eating lateral air
// control. Both are the same root cause - a gate REFUSES movement, and refusal is not how terrain behaves. So
// the assertions here are rewritten to the slide semantics with each original INTENT preserved:
//
//   - no net ascent by ANY input pattern (walking, creeping, a crawling steering vector, a heavy slow, or the
//     #440 repeated-jump cycle), and, new and stronger than the old "was the move refused", the character never
//     has FOOTING on a too-steep face: Grounded is false on every tick it is seated on one,
//   - descent and walk-offs are free (unchanged: that was #369's whole point),
//   - no tunnel: never inside terrain, and never popped above the face,
//   - and the new invariants the model adds - lateral air control along a face survives, and a slide always
//     terminates on walkable ground, in water, in open air, or WHERE THE WORLD SWALLOWS ITS DESCENT (a
//     capsule the world is holding up is supported, the concave crease being the case that motivates it).
//
// What a CONTACT does to the carried velocity (into-surface dies, contour and signed fall line survive) and what
// a wedge grants is SlideContactResolveTests, beside this file. This file is the behaviour half.
//
// The two mechanisms under test are wall slide (a horizontal move whose destination ground stands above what the
// tick can reach keeps only its along-face component - a StepHeight while grounded, and the tick's own resolved
// upward motion when not, see #468 and ClampRatchetTests) and no traction (ground steeper than MaxSlopeRadians
// never grants support, so gravity decomposes against the surface and the character accelerates down the fall
// line).
public class SteepSlopeSlideTests
{
    const float Dt = 1f / 30f;

    // The shipped defaults: 45 deg gate, walk 6, run 12, capsule half-height 0.9, StepHeight 0.4.
    static MoveTuning Tuning => MoveTuning.Default;

    const float EdgeX = 5f;          // every fixture below is flat at Y=0 west of this line
    const float SteepGrade = 5f;     // 78.7 deg: past the 45 deg gate by a wide margin

    // THE CANONICAL FACE: flat at 0 west of EdgeX, rising 5:1 east of it, with the normal that actually describes
    // that surface. Its outward horizontal normal points WEST (away from the face, down the fall line), which the
    // slide and the wall projection both read - so unlike the refusal-era fixtures, the height field and the normal
    // here cannot disagree. Everything about the model is directional now, so a mismatched pair would test nothing.
    static Func<float, float, float> RisingFace => (x, z) => x < EdgeX ? 0f : (x - EdgeX) * SteepGrade;
    static readonly Vector3 RisingFaceNormal = Vector3.Normalize(new Vector3(-SteepGrade, 1f, 0f));
    static Func<float, float, Vector3> RisingFaceNormals => (x, z) => x < EdgeX ? Vector3.UnitY : RisingFaceNormal;

    // A SHEER 10 m step up east of EdgeX: a vertical wall, whose outward horizontal normal is due west and whose
    // ground stands far above any feet in these fixtures, so every tick against it is a wall contact.
    static Func<float, float, float> SheerWall => (x, z) => x < EdgeX ? 0f : 10f;
    static Func<float, float, Vector3> SheerWallNormals => (x, z) => x < EdgeX ? Vector3.UnitY : new Vector3(-1f, 0f, 0f);

    // A 10 m DROP east of EdgeX: the flat top west, the flat toe 10 m below east, and a steep east-facing band at
    // the lip itself (where a terrain sampler reports the cliff face). The descent fixture.
    static Func<float, float, float> Drop => (x, z) => x < EdgeX ? 0f : -10f;
    static readonly Vector3 DropFaceNormal = Vector3.Normalize(new Vector3(1f, 0.14f, 0f));
    static Func<float, float, Vector3> DropNormals =>
        (x, z) => x >= EdgeX && x < EdgeX + 0.5f ? DropFaceNormal : Vector3.UnitY;

    // Walk east (+X): with yaw 0 the camera-relative right axis IS +X.
    static MoveCommand East(bool run = false) => new(new Vector2(1f, 0f), run, cameraYaw: 0f, jump: false);

    // Walk or run east with the jump button HELD: the jump buffer re-fires it on every landing tick, so the character
    // runs a continuous jump-hop cycle into whatever is in front of it - exactly how the #440 exploit was played.
    static MoveCommand EastJump(bool run = false) => new(new Vector2(1f, 0f), run, cameraYaw: 0f, jump: true);

    // THE ENERGY BOUND on how far a frictionless face lets a body ride UP it, and the ceiling every fixture below
    // measures against. It is NOT the bare jump apex, which is what these fixtures used to compare with.
    //
    // A contact deletes only the into-surface component, so everything else - the run INTO the face included -
    // survives as in-plane motion, and the SIGNED fall line converts it to altitude until gravity takes it back.
    // Gravity decelerates the fall-line speed at g*h, and each metre travelled along the fall line is h metres of
    // altitude, so the two h's cancel and the rise is v^2 / (2g) whatever the face angle: the launch's whole kinetic
    // energy, cashed as height. The bound is therefore the TOTAL launch speed (the horizontal, plus the jump's
    // vertical when there is one), squared, over 2g - plus the one StepHeight a ground clamp may seat a falling body
    // onto, plus a tick of discrete-integration slack.
    //
    // At the shipped tuning a RUNNING JUMP launches at sqrt(9.798^2 + 12^2) = 15.5 m/s and is worth 4.8 m of reach
    // against a bare vertical apex of 1.92 m. That 2.4x is a real and INTENDED property of the signed fall line, not
    // a leak: a player can briefly ride a face upward on jump energy and cannot keep any of it, because there is no
    // footing up there to re-launch from and the whole rise is handed back on the way down. Measuring against
    // apex + StepHeight was measuring against a bound the model never claimed, and the 78.7 degree fixture breached
    // it the moment its input ran instead of walking (measured 2.456 m against a 2.370 m ceiling).
    static float FaceReachCeiling(in MoveTuning t, float launchSpeed, bool jumping)
        => (launchSpeed * launchSpeed + (jumping ? t.JumpSpeed * t.JumpSpeed : 0f)) / (2f * t.Gravity)
           + t.StepHeight + 0.05f;

    // ---- Descent and walk-offs are free (the #369 half, unchanged) ----

    [Fact]
    public void Grounded_walk_off_a_steep_drop_advances_and_falls()
    {
        // The bug #369 closed: a cliff edge read as a wall. The ground east of the edge is 10 m DOWN, so the
        // steep lip normal must not stop the step - the character walks off, finds no support, and falls.
        var t = Tuning;
        var s = new MoveState { Position = new Vector3(EdgeX - 0.5f, t.CapsuleHalfHeight, 0f), Grounded = true };
        float startY = s.Position.Y;

        int airborneTick = -1;
        for (int i = 0; i < 12; i++)
        {
            s = CharacterMovement.Step(s, East(), Dt, Drop, t, DropNormals);
            if (!s.Grounded && airborneTick < 0) airborneTick = i;
        }

        Assert.True(s.Position.X > EdgeX, $"the walk was refused at the cliff edge, x={s.Position.X:F3}");
        Assert.InRange(airborneTick, 0, 5);
        Assert.False(s.Grounded);
        Assert.True(s.VerticalVelocity < 0f, $"the character is not falling, vVel={s.VerticalVelocity:F3}");
        Assert.True(s.Position.Y < startY, $"the character did not descend, y={s.Position.Y:F3}");
    }

    [Fact]
    public void A_jump_off_a_clifftop_still_carries_out_over_the_face_and_lands_below()
    {
        // The descent half at airtime: a clifftop jump carries out over the face and lands on the low ground.
        // Blocking this would turn every cliff into flypaper.
        var t = Tuning;
        const float Top = 10f;
        Func<float, float, float> ground = (x, z) => x < EdgeX ? Top : 0f;
        Func<float, float, Vector3> normal = (x, z) => x >= EdgeX && x < EdgeX + 0.5f ? DropFaceNormal : Vector3.UnitY;
        var s = new MoveState { Position = new Vector3(EdgeX - 0.5f, Top + t.CapsuleHalfHeight, 0f), Grounded = true };

        bool clearedTheEdgeInFlight = false, descended = false;
        for (int i = 0; i < 120; i++)
        {
            // Jump on the first tick only: one clean jump-off, then the fall is gravity's.
            var cmd = new MoveCommand(new Vector2(1f, 0f), run: true, cameraYaw: 0f, jump: i == 0);
            s = CharacterMovement.Step(s, cmd, Dt, ground, t, normal);
            if (!s.Grounded && s.Position.X > EdgeX) clearedTheEdgeInFlight = true;
            if (s.Position.Y < Top) descended = true;
        }

        Assert.True(clearedTheEdgeInFlight, "the jump never carried past the cliff edge");
        Assert.True(descended, "the character never fell past the clifftop");
        Assert.True(s.Grounded, "the character never landed");
        Assert.Equal(t.CapsuleHalfHeight, s.Position.Y, 3);      // seated on the low ground, not hung on the face
        Assert.True(s.Position.X > EdgeX + 1f, $"the landing was not out past the face, x={s.Position.X:F3}");
    }

    [Fact]
    public void Airborne_momentum_out_over_a_steep_drop_keeps_flying()
    {
        // The descent half on the carried-velocity path: a flight out over a canyon meets a steep destination
        // normal whose ground is far below the feet, so the arc carries on instead of being frozen in mid-air.
        MoveTuning t = Tuning with { AirMomentum = true };
        Func<float, float, float> ground = (x, z) => x < EdgeX ? 0f : -40f;
        var s = new MoveState
        {
            Position = new Vector3(EdgeX - 0.5f, t.CapsuleHalfHeight, 0f),
            Grounded = false,
            TimeSinceGrounded = 1f,
            HorizontalVelocity = new Vector2(20f, 0f),
        };

        for (int i = 0; i < 30; i++) s = CharacterMovement.Step(s, MoveCommand.Idle, Dt, ground, t, DropNormals);

        Assert.True(s.Position.X > EdgeX + 5f, $"the arc was frozen over the canyon, x={s.Position.X:F3}");
        Assert.Equal(20f, s.HorizontalVelocity.Length(), 2);
        Assert.True(s.VerticalVelocity < 0f);
    }

    // ---- No net ascent, and no footing, by ANY input pattern ----

    [Fact]
    public void Grounded_walk_into_a_steep_face_gains_no_altitude_and_never_gets_footing()
    {
        // The preserved half of the old rule, re-stated honestly. The character may now touch the face - that is
        // the model - but a face past the gate gives it nothing: no altitude to keep, and no footing to stand on.
        var t = Tuning;
        var s = new MoveState { Position = new Vector3(EdgeX - 0.95f, t.CapsuleHalfHeight, 0f), Grounded = true };
        float baseFeet = s.Position.Y - t.CapsuleHalfHeight;

        for (int i = 0; i < 300; i++)
        {
            s = CharacterMovement.Step(s, East(run: true), Dt, RisingFace, t, RisingFaceNormals);
            AssertOnOrAboveTheSurface(s, t, RisingFace, i);
            AssertNoFootingOnTheFace(s, t, RisingFace, i);
            Assert.True(s.Position.Y - t.CapsuleHalfHeight <= baseFeet + t.StepHeight,
                $"tick {i} climbed the face, feetY={s.Position.Y - t.CapsuleHalfHeight:F5}");
        }

        Assert.True(s.Grounded, "the character never settled at the toe");
        Assert.Equal(baseFeet, s.Position.Y - t.CapsuleHalfHeight, 3);
        // And it settled WEST of the face line, not on the face: the height check alone would also pass with the
        // character parked at the very toe of the face, which is a different (and worse) resting place.
        Assert.True(s.Position.X < EdgeX, $"the run ended on the face, x={s.Position.X:F5}");
    }

    [Theory]
    [InlineData(1f)]        // full walk speed: 0.2 m per tick
    [InlineData(0.02f)]     // 0.004 m per tick
    [InlineData(0.01f)]     // 0.002 m per tick - the rise per tick lands exactly ON the retired absolute tolerance
    [InlineData(0.005f)]    // 0.001 m per tick - and under it, which the first cut of the gate made a free climb
    public void No_steering_length_creeps_up_a_steep_face(float steeringLength)
    {
        // SCALE-FREENESS, which the gate needed a gradient rule to get and the slide gets for free: input has NO
        // authority along the fall line at all, so what a tick's travel happens to be cannot buy any of it. This is
        // also the NPC path (StepTowards), so an enemy creeps up nothing either.
        var t = Tuning;
        var s = new MoveState { Position = new Vector3(EdgeX - 0.05f, t.CapsuleHalfHeight, 0f), Grounded = true };
        float baseFeet = s.Position.Y - t.CapsuleHalfHeight;

        for (int i = 0; i < 600; i++)
        {
            s = CharacterMovement.StepTowards(s, new Vector2(steeringLength, 0f), run: false, Dt, RisingFace, t,
                RisingFaceNormals);
            AssertOnOrAboveTheSurface(s, t, RisingFace, i);
            AssertNoFootingOnTheFace(s, t, RisingFace, i);
            Assert.True(s.Position.Y - t.CapsuleHalfHeight <= baseFeet + t.StepHeight,
                $"tick {i} gained height on the face, feetY={s.Position.Y - t.CapsuleHalfHeight:F5}");
        }
    }

    [Theory]
    [InlineData(1f / 30f, false)]   // the shipped server tick
    [InlineData(1f / 30f, true)]    // and at RUN speed, which carries four times the energy onto the face
    [InlineData(0.001f, false)]     // 1000 Hz: the per-tick rise of a marginal face falls under any fixed height tolerance
    [InlineData(0.001f, true)]
    public void A_face_just_past_the_gate_gives_no_net_ascent_at_any_tick_rate(float dt, bool run)
    {
        // 49 deg is one degree past the traction budget - the 45 deg gate PLUS the 3 deg hysteresis band a standing
        // character keeps its footing over - so it is the hardest case, and the one a fixed tolerance loses first.
        // The bound is a bound rather than a fence: a tick MAY step onto the toe of the face (anything higher is a
        // wall contact), and the speed that steps on is converted to altitude by the signed fall line until gravity
        // takes it back. That is FaceReachCeiling, read with this row's own launch speed and no jump term, so the
        // run rows are measured against the energy they actually arrive with (3.33 m) rather than the walk's (1.17).
        // There is no footing up there to re-launch from, so the conversion is one-shot and not a ratchet, and the
        // second half of the run may still not sit above the first.
        //
        // THIS FIXTURE RAN AT 46 DEG UNTIL #475, and the move is a recalibration rather than a weakening. 46 deg is
        // now inside the band, so a character that walks onto it from the adjacent flat KEEPS its footing and walks up
        // it, deliberately - the whole point of hysteresis is that ground a degree past the gate is ground a standing
        // body holds. What this case is for is ground the model grants nothing on, and that boundary moved by the
        // width of the band, so the fixture moves with it. The behaviour at 46 deg is under test in
        // TractionHysteresisTests, on both sides of the asymmetry.
        var t = Tuning;
        float grade = MathF.Tan(49f * MathF.PI / 180f);
        Func<float, float, float> ground = (x, z) => x < EdgeX ? 0f : (x - EdgeX) * grade;
        Vector3 faceNormal = Vector3.Normalize(new Vector3(-grade, 1f, 0f));
        Func<float, float, Vector3> normal = (x, z) => x < EdgeX ? Vector3.UnitY : faceNormal;

        var s = new MoveState { Position = new Vector3(EdgeX - 0.05f, t.CapsuleHalfHeight, 0f), Grounded = true };
        float baseFeet = s.Position.Y - t.CapsuleHalfHeight;

        float ceiling = baseFeet + FaceReachCeiling(t, run ? t.RunSpeed : t.WalkSpeed, jumping: false);
        // The window is TIME, not ticks, so the 1 kHz rows cover the same 40 seconds of contact the 30 Hz ones do
        // rather than 0.6 s of it. The first 8 seconds are the APPROACH and are excluded from the comparison: the
        // charge into the face is the run's largest single excursion (measured 1.467 m at run speed, against a
        // steady state that settles at the toe), so a first half containing it swamps any creep a second half could
        // show, which is exactly how a contaminated window hides a ratchet instead of catching one.
        int ticks = (int)(40f / dt), settled = (int)(8f / dt), halfPoint = settled + (ticks - settled) / 2;
        float firstHalfMax = float.MinValue, secondHalfMax = float.MinValue;
        for (int i = 0; i < ticks; i++)
        {
            s = CharacterMovement.StepTowards(s, new Vector2(1f, 0f), run, dt, ground, t, normal);
            float feet = s.Position.Y - t.CapsuleHalfHeight;
            AssertOnOrAboveTheSurface(s, t, ground, i);
            AssertNoFootingOnTheFace(s, t, ground, i);
            Assert.True(feet <= ceiling, $"tick {i} climbed the face, feetY={feet:F5} against a ceiling {ceiling:F5}");
            if (i < settled) continue;
            if (i < halfPoint) firstHalfMax = MathF.Max(firstHalfMax, feet);
            else secondHalfMax = MathF.Max(secondHalfMax, feet);
        }
        // THE RATCHET TEST PROPER, and the tolerance is the measured swing of the settled enter-and-slide-back
        // cycle, not a fudge. Over the compared 32 seconds the four rows land at -2.16, +0.05, -1.37 and -0.07 mm,
        // all of them at or below zero, and the per-octile maximum inside the window swings by at most 28 mm at
        // 30 Hz and 1.2 mm at 1 kHz with no upward trend in either. Each half takes its maximum over 16 seconds,
        // so it averages that swing out. 10 mm is about five times the worst measured half difference and is the
        // DETECTION FLOOR: it catches any creep past 0.63 mm per second of continuous contact.
        Assert.True(secondHalfMax <= firstHalfMax + 1e-2f,
            $"the face accumulated altitude: first half {firstHalfMax:F5}, second half {secondHalfMax:F5}");
    }

    [Theory]
    [InlineData(0.05f)]     // a heavy slow
    [InlineData(0.01f)]     // near-rooted: 0.002 m per tick
    public void A_slowed_character_cannot_creep_up_a_steep_face(float speedScale)
    {
        // SpeedScale is a movement multiplier the server owns (haste/slow/root). A slow must not turn into a
        // climbing aid, which is what a fixed height tolerance made it in the gate's first cut.
        var t = Tuning;
        var s = new MoveState
        {
            Position = new Vector3(EdgeX - 0.05f, t.CapsuleHalfHeight, 0f),
            Grounded = true,
            SpeedScale = speedScale,
        };
        float baseFeet = s.Position.Y - t.CapsuleHalfHeight;

        for (int i = 0; i < 600; i++)
        {
            s = CharacterMovement.Step(s, East(), Dt, RisingFace, t, RisingFaceNormals);
            AssertOnOrAboveTheSurface(s, t, RisingFace, i);
            AssertNoFootingOnTheFace(s, t, RisingFace, i);
            Assert.True(s.Position.Y - t.CapsuleHalfHeight <= baseFeet + t.StepHeight,
                $"tick {i} gained height on the face, feetY={s.Position.Y - t.CapsuleHalfHeight:F5}");
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]     // the carried-velocity path slides the same, so the exploit closes on both
    [InlineData(false, true)]     // and at RUN speed, which is how the #440 cliff was actually climbed
    [InlineData(true, true)]
    public void Repeated_jumping_into_a_steep_face_gains_no_altitude(bool airMomentum, bool run)
    {
        // THE PLAYTESTED #440 EXPLOIT, on a 78.7 deg sea cliff. The gate closed it by refusing the drift onto the
        // face. The slide closes it by making the face worthless once reached - landing on it lands in a SLIDE, so
        // the cycle cannot ratchet. The character may never be grounded while it is over the face, and that is the
        // whole of it: no footing means no second jump from up there, so whatever one arc reaches is handed back
        // on the way down. Altitude is bounded by ONE arc: an apex, plus the single StepHeight a ground clamp may
        // seat the falling body onto.
        //
        // NOTE ON THE CEILING under the signed fall line. A contact deletes only the into-surface component, so an
        // arc that meets the face converts the REST of its speed - the run into the face included - into along-face
        // motion, and a frictionless face can turn that into altitude. So a cycle transiently reaches HIGHER than a
        // bare vertical jump would (measured peak 2.224 m above the base at walk speed and 2.456 m at run speed,
        // against a bare apex of 1.920), which is correct physics rather than a leak. That is why the ceiling is
        // FaceReachCeiling and not apex + StepHeight: the bound is the energy the launch arrived with, and the run
        // rows breach the old bound honestly. The invariants that matter are unchanged: never grounded on the face,
        // and no net gain across cycles.
        MoveTuning t = Tuning with { AirMomentum = airMomentum };
        var s = new MoveState { Position = new Vector3(EdgeX - 0.05f, t.CapsuleHalfHeight, 0f), Grounded = true };
        float baseFeetY = s.Position.Y - t.CapsuleHalfHeight;
        float ceiling = baseFeetY + FaceReachCeiling(t, run ? t.RunSpeed : t.WalkSpeed, jumping: true);

        // ~26 ticks per arc at 30 Hz, so 6000 ticks is over 200 full jump cycles. The window is long BECAUSE the
        // detector is a half-against-half comparison: the creep it can resolve is the tolerance divided by the
        // cycles in the second half, so cycles are what buy sensitivity. The first 200 ticks are the APPROACH and
        // are excluded from the comparison - the character crosses to the face and settles into the cycle there,
        // and a first half carrying that transient reads 21-26 mm high all by itself, which is most of the old
        // 30 mm tolerance spent on hiding the very thing the assertion is for.
        const int Ticks = 6000, Settled = 200;
        const int HalfPoint = Settled + (Ticks - Settled) / 2;
        int jumps = 0;
        float firstHalfMax = baseFeetY, secondHalfMax = baseFeetY;
        for (int i = 0; i < Ticks; i++)
        {
            s = CharacterMovement.Step(s, EastJump(run), Dt, RisingFace, t, RisingFaceNormals);
            if (s.VerticalVelocity == t.JumpSpeed) jumps++;   // the launch tick stamps the speed exactly
            AssertOnOrAboveTheSurface(s, t, RisingFace, i);
            AssertNoFootingOnTheFace(s, t, RisingFace, i);
            float feet = s.Position.Y - t.CapsuleHalfHeight;
            Assert.True(feet <= ceiling, $"tick {i} rose past the launch energy above the base, feetY={feet:F5} " +
                                         $"against a ceiling {ceiling:F5}");
            if (i < Settled) continue;
            if (i < HalfPoint) firstHalfMax = MathF.Max(firstHalfMax, feet);
            else secondHalfMax = MathF.Max(secondHalfMax, feet);
        }
        Assert.True(jumps >= 100, $"the fixture never ran 100 jump cycles, jumps={jumps}");
        // THE RATCHET TEST PROPER: a hundred-odd more jump cycles bought no more height than the first hundred did.
        // The tolerance is the measured swing of the settled cycle, not a fudge. Over the compared window the four
        // rows land at +0.36, +0.36, -3.92 and -3.92 mm, and the per-octile maximum inside it swings by at most
        // 10.7 mm with no trend. 20 mm is about twice that worst swing, and it is the DETECTION FLOOR: the sparsest
        // row runs ~107 cycles per half, so it catches any creep past 19 mm per 100 cycles here, and past 35 mm per
        // 100 cycles on the sparsest fixture in this file (the near-gate running jump below, ~57 cycles per half).
        // Either way it is well inside the 5 cm per 100 cycles a creep would have to stay under to hide - and the
        // #440 playtest that climbed a whole sea cliff was orders above that.
        Assert.True(secondHalfMax <= firstHalfMax + 2e-2f,
            $"the jump cycle ratcheted: first half {firstHalfMax:F5}, second half {secondHalfMax:F5}");

        // Release the button and let the last arc settle, so the final altitude is a landed one and not mid-flight.
        for (int i = 0; i < 60; i++) s = CharacterMovement.Step(s, East(), Dt, RisingFace, t, RisingFaceNormals);
        Assert.True(s.Grounded, "the character never settled");
        Assert.InRange(s.Position.Y - t.CapsuleHalfHeight, baseFeetY - 1e-3f, baseFeetY + t.StepHeight);
        // And it settled west of the face line: sixteen jump cycles bought no ground either, which the height
        // check alone does not say (the toe of the face is at the base height too).
        Assert.True(s.Position.X < EdgeX, $"the run ended on the face, x={s.Position.X:F5}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_running_jump_up_a_near_gate_face_rides_far_past_a_bare_apex_and_keeps_none_of_it(bool airMomentum)
    {
        // THE 2.4x REACH, pinned rather than left as prose, because it is the fact that made the old ceilings
        // wrong. A near-gate face is the best converter there is: at 46 degrees almost all of a horizontal run
        // survives the contact as in-plane motion, so a running jump into one rides it far higher than any bare
        // vertical jump reaches. Measured here: 4.913 m above the base, against a bare apex of 1.920 m - a factor
        // of 2.6 - and inside the 5.25 m the launch energy pays for. That is the signed fall line working as
        // designed, and it is exactly what a frictionless surface does with speed.
        //
        // What it does NOT do is keep any of it. There is no footing on the face to re-launch from, so every metre
        // of the ride is handed back on the way down, which the half-against-half comparison is what proves.
        MoveTuning t = Tuning with { AirMomentum = airMomentum };
        float grade = MathF.Tan(46f * MathF.PI / 180f);
        Func<float, float, float> ground = (x, z) => x < EdgeX ? 0f : (x - EdgeX) * grade;
        Vector3 faceNormal = Vector3.Normalize(new Vector3(-grade, 1f, 0f));
        Func<float, float, Vector3> normal = (x, z) => x < EdgeX ? Vector3.UnitY : faceNormal;

        var s = new MoveState { Position = new Vector3(EdgeX - 0.05f, t.CapsuleHalfHeight, 0f), Grounded = true };
        float baseFeetY = s.Position.Y - t.CapsuleHalfHeight;
        float bareApex = t.JumpSpeed * t.JumpSpeed / (2f * t.Gravity);
        float ceiling = baseFeetY + FaceReachCeiling(t, t.RunSpeed, jumping: true);

        // THE APPROACH TAKES LONGER TO SETTLE SINCE #468, so the excluded window is longer and the run is longer to
        // keep the compared halves fat. The cycle used to reach its final orbit within 200 ticks. Now it holds one
        // orbit (peak 4.247 m) for about 5300 ticks, steps once to a second (4.495 m), and stays there. Measured
        // over 48000 ticks - 1086 launches, eight times this window - it steps exactly once and never again, and
        // the run's global maximum stays the 4.913 m of the very first approach. That is a discrete stepper settling
        // between two periodic orbits, not a creep, so the fix is to compare halves that sit on the SAME orbit
        // rather than to widen the tolerance: 20 mm is the detector, and weakening it to swallow a 247 mm orbit step
        // would blind the test to the ratchet it exists for.
        const int Ticks = 20000, Settled = 6000;
        const int HalfPoint = Settled + (Ticks - Settled) / 2;
        int jumps = 0;
        float peak = baseFeetY, firstHalfMax = baseFeetY, secondHalfMax = baseFeetY;
        for (int i = 0; i < Ticks; i++)
        {
            s = CharacterMovement.Step(s, EastJump(run: true), Dt, ground, t, normal);
            if (s.VerticalVelocity == t.JumpSpeed) jumps++;
            AssertOnOrAboveTheSurface(s, t, ground, i);
            AssertNoFootingOnTheFace(s, t, ground, i);
            float feet = s.Position.Y - t.CapsuleHalfHeight;
            peak = MathF.Max(peak, feet);
            Assert.True(feet <= ceiling, $"tick {i} rose past the launch energy, feetY={feet:F5} against a " +
                                         $"ceiling {ceiling:F5}");
            if (i < Settled) continue;
            if (i < HalfPoint) firstHalfMax = MathF.Max(firstHalfMax, feet);
            else secondHalfMax = MathF.Max(secondHalfMax, feet);
        }

        Assert.True(jumps >= 50, $"the fixture never ran 50 jump cycles, jumps={jumps}");
        Assert.True(peak > 2f * bareApex,
            $"the face did not convert the run into altitude, peak {peak:F4} against a bare apex {bareApex:F4}");
        // Same detector, same measured basis as the 78.7 fixture above: the compared halves land at +0.88 mm and
        // the tolerance is 20 mm. This is the sparsest cycle in the file (~119 launches over 6000 ticks, so ~57
        // per half), which makes it the fixture that sets the file's detection floor at 35 mm per 100 cycles.
        Assert.True(secondHalfMax <= firstHalfMax + 2e-2f,
            $"the near-gate cycle ratcheted: first half {firstHalfMax:F5}, second half {secondHalfMax:F5}");
    }

    // ---- Anti-tunnel: never inside terrain, never popped above the face ----

    [Fact]
    public void Airborne_into_a_sheer_wall_keeps_no_into_face_motion_and_never_tunnels()
    {
        // A 10 m sheer wall whose top the character is nowhere near. The into-wall component of every tick dies, so
        // the capsule is never committed to an XZ where the terrain stands over its feet - which is what would have
        // left the ground clamp to pop it up the wall on a later tick.
        var t = Tuning;
        var s = new MoveState
        {
            Position = new Vector3(EdgeX - 0.5f, t.CapsuleHalfHeight + 2f, 0f),
            Grounded = false,
            TimeSinceGrounded = 1f,
        };

        for (int i = 0; i < 90; i++)
        {
            s = CharacterMovement.Step(s, East(run: true), Dt, SheerWall, t, SheerWallNormals);
            Assert.True(s.Position.X <= EdgeX, $"tick {i} entered the wall's footprint, x={s.Position.X:F3}");
            Assert.True(SheerWall(s.Position.X, s.Position.Z) <= s.Position.Y - t.CapsuleHalfHeight + 1e-3f,
                $"tick {i} left the capsule under terrain, y={s.Position.Y:F3}");
        }
    }

    [Fact]
    public void Airborne_momentum_into_a_sheer_wall_sheds_the_into_wall_carry()
    {
        // Same rule on the carried-velocity path, and the denied move must not survive into the carry either.
        MoveTuning t = Tuning with { AirMomentum = true };
        var s = new MoveState
        {
            Position = new Vector3(EdgeX - 0.5f, t.CapsuleHalfHeight + 2f, 0f),
            Grounded = false,
            TimeSinceGrounded = 1f,
            HorizontalVelocity = new Vector2(20f, 0f),
        };

        for (int i = 0; i < 30; i++)
        {
            s = CharacterMovement.Step(s, MoveCommand.Idle, Dt, SheerWall, t, SheerWallNormals);
            Assert.True(s.Position.X <= EdgeX, $"tick {i} tunnelled the wall, x={s.Position.X:F3}");
            Assert.True(SheerWall(s.Position.X, s.Position.Z) <= s.Position.Y - t.CapsuleHalfHeight + 1e-3f,
                $"tick {i} left the capsule under terrain, y={s.Position.Y:F3}");
        }
        Assert.Equal(Vector2.Zero, s.HorizontalVelocity);
    }

    [Fact]
    public void Falling_alongside_a_steep_face_while_steering_into_it_never_seats_mid_face()
    {
        // The #440 exploit's passive twin: a character falling past a face while holding "into" it used to be seated
        // by the ground clamp partway up (from 20 m this landed ~14 m up a 5:1 face). It may touch the face now, but
        // it has no footing there, so every grounded tick it ever reports is at the toe.
        var t = Tuning;
        var s = new MoveState
        {
            Position = new Vector3(EdgeX - 0.5f, t.CapsuleHalfHeight + 20f, 0f),
            Grounded = false,
            TimeSinceGrounded = 1f,
        };

        for (int i = 0; i < 300; i++)
        {
            s = CharacterMovement.Step(s, East(run: true), Dt, RisingFace, t, RisingFaceNormals);
            AssertOnOrAboveTheSurface(s, t, RisingFace, i);
            AssertNoFootingOnTheFace(s, t, RisingFace, i);
            AssertWestOfTheFaceLine(s, t, i);
        }
        Assert.True(s.Grounded, "the fall never landed");
        Assert.Equal(t.CapsuleHalfHeight, s.Position.Y, 3);   // the toe: the only ground there is to land on
        Assert.True(s.Position.X < EdgeX, $"the landing was on the face, x={s.Position.X:F5}");
    }

    // ---- The reported feel bug: lateral air control along a face must survive ----

    [Fact]
    public void Jumping_alongside_a_face_while_holding_into_it_keeps_the_along_face_component()
    {
        // THE USER'S EXACT COMPLAINT with the 17.26.1 fence: strafing along a cliff mid-jump felt like hitting an
        // invisible wall, because the gate refused the WHOLE move the moment any of it pointed at the face. The wall
        // slide removes only the into-face component, so the along-face travel is the same as it would be with no
        // face there at all. The command is 45 degrees into-and-along the wall, so the along-face component is
        // speed/sqrt(2) in both worlds and the two Z tracks must land on each other.
        var t = Tuning;
        Func<float, float, float> flat = (x, z) => 0f;
        Func<float, float, Vector3> flatNormals = (x, z) => Vector3.UnitY;

        var alongWall = new MoveState { Position = new Vector3(EdgeX - 0.1f, t.CapsuleHalfHeight, 0f), Grounded = true };
        var control = alongWall;
        for (int i = 0; i < 20; i++)
        {
            // Jump on the first tick only, then hold the direction through the arc.
            var c = new MoveCommand(new Vector2(1f, 1f), run: false, cameraYaw: 0f, jump: i == 0);
            alongWall = CharacterMovement.Step(alongWall, c, Dt, SheerWall, t, SheerWallNormals);
            control = CharacterMovement.Step(control, c, Dt, flat, t, flatNormals);
        }

        float wallZ = MathF.Abs(alongWall.Position.Z), freeZ = MathF.Abs(control.Position.Z);
        Assert.True(freeZ > 0.5f, $"the control jump made no lateral progress, |z|={freeZ:F3}");
        Assert.True(wallZ >= 0.99f * freeZ,
            $"the wall ate the lateral air control: |z|={wallZ:F3} against a free {freeZ:F3}");
        Assert.True(alongWall.Position.X <= EdgeX + 1e-3f, $"the wall was entered, x={alongWall.Position.X:F3}");
    }

    // ---- The slide itself ----

    [Fact]
    public void A_character_on_a_steep_face_slides_to_the_toe_lands_and_reports_one_impact()
    {
        // The core of the model. Seated 10 m up a 5:1 face with no input at all: altitude falls every single tick
        // (gravity's tangential component is the only thing acting), the character is never grounded on the way
        // down, and the walkable toe is where the slide terminates - one landing, one LandingImpactSpeed, from the
        // speed the slide accumulated.
        var t = Tuning;
        const float StartX = EdgeX + 2f;
        var s = new MoveState
        {
            Position = new Vector3(StartX, RisingFace(StartX, 0f) + t.CapsuleHalfHeight, 0f),
            Grounded = false,
            TimeSinceGrounded = 1f,
        };

        int impacts = 0, landedAt = -1;
        float impactSpeed = 0f;
        for (int i = 0; i < 200; i++)
        {
            float prevY = s.Position.Y;
            s = CharacterMovement.Step(s, MoveCommand.Idle, Dt, RisingFace, t, RisingFaceNormals);
            AssertOnOrAboveTheSurface(s, t, RisingFace, i);
            if (s.LandingImpactSpeed != 0f) { impacts++; impactSpeed = s.LandingImpactSpeed; }
            if (s.Grounded)
            {
                if (landedAt < 0) landedAt = i;
            }
            else
            {
                Assert.True(landedAt < 0, $"tick {i} left the ground again after landing at {landedAt}");
                Assert.True(s.Position.Y < prevY - 1e-6f,
                    $"tick {i} did not descend while sliding, y={s.Position.Y:F5} was {prevY:F5}");
            }
        }

        Assert.True(landedAt > 0, "the slide never reached the toe");
        Assert.True(s.Grounded);
        Assert.Equal(t.CapsuleHalfHeight, s.Position.Y, 3);     // the flat toe, west of the edge
        Assert.True(s.Position.X < EdgeX, $"the slide ended on the face, x={s.Position.X:F3}");
        Assert.Equal(1, impacts);
        Assert.True(impactSpeed > 5f, $"the landing reported no accumulated fall, impact={impactSpeed:F3} m/s");
    }

    [Fact]
    public void No_jump_while_sliding()
    {
        // No footing means no jump: a face is not a launch pad. The jump bit is held for the whole slide and the
        // coyote window is long expired at the start, so a launch would have to come from footing on the face. The
        // per-tick DESCENT assertion below is what catches one: a fired jump stamps a large positive vertical and
        // the very next committed position rises. That is strictly stronger than a float equality against
        // JumpSpeed on one tick's velocity, which is why that check is not here.
        var t = Tuning;
        const float StartX = EdgeX + 4f;   // 20 m up, so the whole window below is spent on the face
        var s = new MoveState
        {
            Position = new Vector3(StartX, RisingFace(StartX, 0f) + t.CapsuleHalfHeight, 0f),
            Grounded = false,
            TimeSinceGrounded = 1f,
        };

        var jump = new MoveCommand(Vector2.Zero, run: false, cameraYaw: 0f, jump: true);
        for (int i = 0; i < 30; i++)
        {
            float prevY = s.Position.Y;
            s = CharacterMovement.Step(s, jump, Dt, RisingFace, t, RisingFaceNormals);
            Assert.True(s.Position.X > EdgeX, $"tick {i} left the face, so the window is no longer a slide");
            Assert.False(s.Grounded, $"tick {i} found footing on the face");
            Assert.True(s.Position.Y < prevY, $"tick {i} rose on the face, y={s.Position.Y:F5}");
        }
    }

    [Fact]
    public void A_slide_into_deep_water_flips_to_swimming()
    {
        // The medium hand-off, one of the three legal ends of a slide (walkable ground, water, open air). The face
        // runs down into a flooded basin, and the existing submersion hysteresis flips the character to Swimming on the
        // way down without the slide needing to know water exists.
        var t = Tuning;
        Func<float, float, float> ground = (x, z) => x < EdgeX ? -5f : (x - EdgeX) * SteepGrade;
        Func<float, float, Vector3> normals = (x, z) => x < EdgeX ? Vector3.UnitY : RisingFaceNormal;
        const float SurfaceY = 3f;
        Func<float, float, float, MovementMedium> medium = (x, z, feetY) => new MovementMedium(SurfaceY, inWater: true);

        const float StartX = EdgeX + 2f;
        var s = new MoveState
        {
            Position = new Vector3(StartX, ground(StartX, 0f) + t.CapsuleHalfHeight, 0f),
            Grounded = false,
            TimeSinceGrounded = 1f,
        };

        bool swam = false;
        for (int i = 0; i < 200 && !swam; i++)
        {
            s = CharacterMovement.Step(s, MoveCommand.Idle, Dt, ground, t, normals, world: null, clampXz: null,
                medium: medium);
            swam = s.Swimming;
        }

        Assert.True(swam, $"the slide never handed off to the swim, y={s.Position.Y:F3} x={s.Position.X:F3}");
        Assert.False(s.Grounded);
    }

    [Fact]
    public void StepTowards_slides_identically_to_the_player_path()
    {
        // The NPC path is the same core, so an enemy on a face slides exactly as a player does - bit-for-bit, not
        // approximately. A world-space steering direction of unit length resolves to the same (direction, fraction)
        // pair a full-tilt camera-relative command does, so any divergence here is the two paths having drifted.
        var t = Tuning;
        const float StartX = EdgeX + 2f;
        var seed = new MoveState
        {
            Position = new Vector3(StartX, RisingFace(StartX, 0f) + t.CapsuleHalfHeight, 0f),
            Grounded = false,
            TimeSinceGrounded = 1f,
        };
        MoveState player = seed, npc = seed;

        for (int i = 0; i < 120; i++)
        {
            player = CharacterMovement.Step(player, East(), Dt, RisingFace, t, RisingFaceNormals);
            npc = CharacterMovement.StepTowards(npc, new Vector2(1f, 0f), run: false, Dt, RisingFace, t,
                RisingFaceNormals);
            Assert.Equal(player.Position, npc.Position);
            Assert.Equal(player.Grounded, npc.Grounded);
            Assert.Equal(player.VerticalVelocity, npc.VerticalVelocity);
            Assert.Equal(player.HorizontalVelocity, npc.HorizontalVelocity);
        }
        Assert.True(player.Grounded, "the fixture never completed the slide");
    }

    // ---- Regression guard: the step-up path is untouched ----

    [Fact]
    public void A_legal_riser_within_StepHeight_still_mounts()
    {
        // Nothing here may reach the prop step-up. A 0.3 m riser on flat analytic terrain (the way the demos feed a
        // ground normal) is inside StepHeight, so the swept step-up mounts it exactly as before.
        var t = Tuning;
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(Riser(0.3f), Pose.At(Vector3.Zero));
        world.Step(Dt);

        float halfH = t.CapsuleHalfHeight;
        var s = new MoveState { Position = new Vector3(0f, halfH, 1f), Grounded = true };
        var cmd = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: 0f, jump: false);   // forward = -Z
        float Ground(float x, float z) => 0f;
        Func<float, float, Vector3> flatNormal = (x, z) => Vector3.UnitY;

        for (int i = 0; i < 180; i++) s = CharacterMovement.Step(s, cmd, Dt, Ground, t, flatNormal, world);

        Assert.True(s.Position.Y > 0.3f + halfH - 0.05f, $"the riser was not mounted, y={s.Position.Y:F3}");
        Assert.True(s.Grounded);
    }

    // ---- Shared invariants ----

    // NEVER INSIDE TERRAIN. The ground clamp forbids penetration on the analytic path, so the feet are at or above
    // the surface under the capsule on every tick, sliding or not.
    static void AssertOnOrAboveTheSurface(in MoveState s, in MoveTuning t, Func<float, float, float> ground, int tick)
        => Assert.True(s.Position.Y - t.CapsuleHalfHeight >= ground(s.Position.X, s.Position.Z) - 1e-3f,
            $"tick {tick} left the capsule under terrain: feetY={s.Position.Y - t.CapsuleHalfHeight:F5}, " +
            $"ground={ground(s.Position.X, s.Position.Z):F5}");

    // WEST OF THE FACE LINE, per tick, on the axis the drift actually happens on. The refusal-era suite bounded X
    // at EdgeX flat, which the slide model cannot honour and should not: a character 20 m up has NO wall in front
    // of it (the face's ground there is far below its feet), so it flies out over the face's footprint exactly as
    // it would over open ground, and the fixture measures it 3.5 m past EdgeX at the top of the fall. What it may
    // never do is be at an X where the face stands OVER its feet, which is the same anti-tunnel fact the surface
    // invariant carries, restated on the axis that names the CAUSE ("it drifted into the footprint") rather than
    // the symptom ("it is inside terrain"). The bound is exact - the face line at this tick's own feet - so a drift
    // of one tick's travel into the footprint fails it, seating or no seating.
    static void AssertWestOfTheFaceLine(in MoveState s, in MoveTuning t, int tick)
    {
        float feet = s.Position.Y - t.CapsuleHalfHeight;
        float faceLine = EdgeX + feet / SteepGrade;
        Assert.True(s.Position.X <= faceLine + 1e-3f,
            $"tick {tick} drifted {s.Position.X - faceLine:F5} m into the face footprint: x={s.Position.X:F5}, " +
            $"the face line at feet {feet:F5} is {faceLine:F5}");
    }

    // NO FOOTING ON A TOO-STEEP FACE. This is the assertion that replaces the refusal-era "the move was blocked":
    // the character may be seated on the face, but while it is, it is not grounded - so it cannot jump from there,
    // cannot refresh coyote there, and does not latch a landing there.
    static void AssertNoFootingOnTheFace(in MoveState s, in MoveTuning t, Func<float, float, float> ground, int tick)
    {
        if (s.Position.X <= EdgeX) return;                     // west of the edge is the walkable flat
        Assert.False(s.Grounded,
            $"tick {tick} found footing on the face at x={s.Position.X:F5}, y={s.Position.Y:F5} " +
            $"(ground {ground(s.Position.X, s.Position.Z):F5})");
    }

    // A one-sided step: a +Z-facing riser quad (Y 0..height at Z=0) and a deep +Y-facing tread behind it. One-sided
    // on purpose - it is the shape the building/curb proxies use and the one the mount was hardened against.
    static TriangleMeshShape Riser(float height, float treadDepth = 40f, float halfX = 20f)
    {
        var v = new List<Vector3>();
        var idx = new List<int>();
        void Tri(int a, int b, int c) { idx.Add(a); idx.Add(b); idx.Add(c); }

        int b0 = v.Count;
        v.Add(new Vector3(-halfX, 0f, 0f));
        v.Add(new Vector3(halfX, 0f, 0f));
        v.Add(new Vector3(halfX, height, 0f));
        v.Add(new Vector3(-halfX, height, 0f));
        Tri(b0 + 0, b0 + 2, b0 + 1); Tri(b0 + 0, b0 + 3, b0 + 2);

        b0 = v.Count;
        v.Add(new Vector3(-halfX, height, 0f));
        v.Add(new Vector3(halfX, height, 0f));
        v.Add(new Vector3(halfX, height, -treadDepth));
        v.Add(new Vector3(-halfX, height, -treadDepth));
        Tri(b0 + 0, b0 + 1, b0 + 2); Tri(b0 + 0, b0 + 2, b0 + 3);

        return new TriangleMeshShape(v.ToArray(), idx.ToArray());
    }
}
