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
//     terminates on walkable ground, in water, in open air, or WEDGED between opposing faces (a crease
//     is supported, because a pinched capsule is held up by the two faces).
//
// What a CONTACT does to the carried velocity (into-surface dies, contour and signed fall line survive) and what
// a wedge grants is SlideContactResolveTests, beside this file. This file is the behaviour half.
//
// The two mechanisms under test are wall slide (a horizontal move whose destination ground stands more than
// StepHeight above the feet keeps only its along-face component) and no traction (ground steeper than
// MaxSlopeRadians never grants support, so gravity decomposes against the surface and the character accelerates
// down the fall line).
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

    // Walk east with the jump button HELD: the jump buffer re-fires it on every landing tick, so the character runs a
    // continuous jump-hop cycle into whatever is in front of it - which is exactly how the #440 exploit was played.
    static MoveCommand EastJump() => new(new Vector2(1f, 0f), run: false, cameraYaw: 0f, jump: true);

    // A jump's own apex above its launch height, discrete-integration slack included.
    static float JumpApex(in MoveTuning t) => t.JumpSpeed * t.JumpSpeed / (2f * t.Gravity) + 0.05f;

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
    [InlineData(1f / 30f)]   // the shipped server tick
    [InlineData(0.001f)]     // 1000 Hz: the per-tick rise of a 46 deg face falls under any fixed height tolerance
    public void A_face_just_past_the_gate_gives_no_net_ascent_at_any_tick_rate(float dt)
    {
        // 46 deg is one degree past the default gate: the hardest case, and the one a fixed tolerance loses first.
        // The bound is a bound rather than a fence, and it has two terms. A tick MAY step onto the toe of the face
        // (anything higher is a wall contact), which is the StepHeight. And a walk that steps on carries its walk
        // speed onto the surface, where the SIGNED fall line converts it to up-slope motion until gravity takes it
        // back - a frictionless face turning run into altitude, capped by the energy that arrived, so at most
        // WalkSpeed^2 / 2g. There is no footing up there to re-launch from, so this is a one-shot conversion and
        // not a ratchet, and the second half of the run may still not sit above the first.
        var t = Tuning;
        float grade = MathF.Tan(46f * MathF.PI / 180f);
        Func<float, float, float> ground = (x, z) => x < EdgeX ? 0f : (x - EdgeX) * grade;
        Vector3 faceNormal = Vector3.Normalize(new Vector3(-grade, 1f, 0f));
        Func<float, float, Vector3> normal = (x, z) => x < EdgeX ? Vector3.UnitY : faceNormal;

        var s = new MoveState { Position = new Vector3(EdgeX - 0.05f, t.CapsuleHalfHeight, 0f), Grounded = true };
        float baseFeet = s.Position.Y - t.CapsuleHalfHeight;

        float ceiling = baseFeet + t.StepHeight + t.WalkSpeed * t.WalkSpeed / (2f * t.Gravity);
        float firstHalfMax = 0f, secondHalfMax = 0f;
        for (int i = 0; i < 600; i++)
        {
            s = CharacterMovement.StepTowards(s, new Vector2(1f, 0f), run: false, dt, ground, t, normal);
            float feet = s.Position.Y - t.CapsuleHalfHeight;
            AssertOnOrAboveTheSurface(s, t, ground, i);
            AssertNoFootingOnTheFace(s, t, ground, i);
            Assert.True(feet <= ceiling, $"tick {i} climbed the face, feetY={feet:F5} against a ceiling {ceiling:F5}");
            if (i < 300) firstHalfMax = MathF.Max(firstHalfMax, feet);
            else secondHalfMax = MathF.Max(secondHalfMax, feet);
        }
        // THE RATCHET TEST PROPER, and the tolerance is the measured PHASE swing of the enter-and-slide-back cycle,
        // not a fudge. Over 4000 ticks the per-octile maximum here oscillates inside 5 mm at 30 Hz and 2 mm at
        // 1 kHz with no trend in either, so two halves can land on opposite phases and differ by that much while
        // accumulating exactly nothing. 20 mm covers it with margin and is still three orders below a real
        // ratchet: a face that kept even one tick's ramp rise per cycle would be metres up by the 600th tick.
        Assert.True(secondHalfMax <= firstHalfMax + 2e-2f,
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
    [InlineData(false)]
    [InlineData(true)]     // the carried-velocity path slides the same, so the exploit closes on both
    public void Repeated_jumping_into_a_steep_face_gains_no_altitude(bool airMomentum)
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
        // motion, and a frictionless face can turn that into altitude. So a cycle transiently reaches slightly
        // HIGHER than a bare vertical jump would (measured peak 2.22 m above the base against a bare apex of 1.92),
        // which is correct physics rather than a leak, and it is still inside the one-arc ceiling below because the
        // arc is what pays for it. The invariants that matter are unchanged: never grounded on the face, and no net
        // gain across cycles.
        MoveTuning t = Tuning with { AirMomentum = airMomentum };
        var s = new MoveState { Position = new Vector3(EdgeX - 0.05f, t.CapsuleHalfHeight, 0f), Grounded = true };
        float baseFeetY = s.Position.Y - t.CapsuleHalfHeight;
        float ceiling = baseFeetY + JumpApex(t) + t.StepHeight;

        // ~24 ticks per arc at 30 Hz (2 * 9.798 / 25 = 0.784 s), so 400 ticks is 16 full jump cycles.
        int jumps = 0;
        float firstHalfMax = baseFeetY, secondHalfMax = baseFeetY;
        for (int i = 0; i < 400; i++)
        {
            s = CharacterMovement.Step(s, EastJump(), Dt, RisingFace, t, RisingFaceNormals);
            if (s.VerticalVelocity == t.JumpSpeed) jumps++;   // the launch tick stamps the speed exactly
            AssertOnOrAboveTheSurface(s, t, RisingFace, i);
            AssertNoFootingOnTheFace(s, t, RisingFace, i);
            float feet = s.Position.Y - t.CapsuleHalfHeight;
            Assert.True(feet <= ceiling, $"tick {i} rose past one arc above the base, feetY={feet:F5}");
            if (i < 200) firstHalfMax = MathF.Max(firstHalfMax, feet);
            else secondHalfMax = MathF.Max(secondHalfMax, feet);
        }
        Assert.True(jumps >= 10, $"the fixture never ran 10 jump cycles, jumps={jumps}");
        // The ratchet test proper: 8 more jump cycles bought no more height than the first 8 did. The tolerance is
        // the measured PHASE swing of the cycle, not a fudge. Over 4000 ticks the per-octile maximum oscillates
        // inside 10 mm (2.2147 to 2.2245) with no trend at all, and the halves of THIS 400-tick window differ by
        // 21 mm because the first half also carries the approach ticks before the cycle settles. 30 mm covers both
        // and is still two orders below one riser of a real ratchet, which is what the #440 playtest climbed a
        // whole sea cliff with.
        Assert.True(secondHalfMax <= firstHalfMax + 3e-2f,
            $"the jump cycle ratcheted: first half {firstHalfMax:F5}, second half {secondHalfMax:F5}");

        // Release the button and let the last arc settle, so the final altitude is a landed one and not mid-flight.
        for (int i = 0; i < 60; i++) s = CharacterMovement.Step(s, East(), Dt, RisingFace, t, RisingFaceNormals);
        Assert.True(s.Grounded, "the character never settled");
        Assert.InRange(s.Position.Y - t.CapsuleHalfHeight, baseFeetY - 1e-3f, baseFeetY + t.StepHeight);
        // And it settled west of the face line: sixteen jump cycles bought no ground either, which the height
        // check alone does not say (the toe of the face is at the base height too).
        Assert.True(s.Position.X < EdgeX, $"the run ended on the face, x={s.Position.X:F5}");
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
