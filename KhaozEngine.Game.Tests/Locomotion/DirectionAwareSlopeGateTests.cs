using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

// The slope gate is DIRECTION-AWARE: a destination steeper than MaxSlopeRadians blocks the move only when its
// ground is ABOVE the character's feet. Walking off a cliff used to be refused exactly like walking into one,
// because the gate read the destination normal and never compared heights, so the analytic-terrain path had no
// way to tell an ascent from a descent. Descent and level traversal now fall through to gravity, which is the
// same asymmetry the Bepu-backed collide-and-slide already applies to props, extended to the analytic terrain
// the overworld cliffs are actually made of.
//
// The rise is measured from the LOWER of the feet and the ground under the current column, which is both the
// anti-tunnel property (flying into a cliff face whose ground stands above your feet stays blocked, so the XZ can
// never be committed under terrain and left waiting for a ground clamp to pop the capsule up the cliff) and the
// close of the jump-climb exploit (#440): reading the rise from the feet ALONE let a jump pay for the ascent,
// because at the apex the face's local ground sits level with the raised feet and the climb reads as free.
public class DirectionAwareSlopeGateTests
{
    const float Dt = 1f / 30f;

    // The shipped defaults: 45 deg gate, walk 6, run 12, capsule half-height 0.9, StepHeight 0.4.
    static MoveTuning Tuning => MoveTuning.Default;

    // ~82 deg from vertical: past the gate by a wide margin, so no test here rides the slope threshold itself.
    static readonly Vector3 SteepNormal = Vector3.Normalize(new Vector3(1f, 0.14f, 0f));

    const float EdgeX = 5f;   // every fixture below is flat at Y=0 west of this line and steep east of it

    // Walk east (+X): with yaw 0 the camera-relative right axis IS +X.
    static MoveCommand East(bool run = false) => new(new Vector2(1f, 0f), run, cameraYaw: 0f, jump: false);

    static Func<float, float, Vector3> NormalSteepPastEdge =>
        (x, z) => x < EdgeX ? Vector3.UnitY : SteepNormal;

    // A terrain step at EdgeX: flat at 0 to the west, `east` to the east. Negative = a cliff to fall off,
    // positive = a face to be stopped by.
    static Func<float, float, float> HeightPastEdge(float east) => (x, z) => x < EdgeX ? 0f : east;

    // ---- Grounded: descent proceeds, ascent is still refused ----

    [Fact]
    public void Grounded_walk_off_a_steep_drop_advances_and_falls()
    {
        // The bug this closes: a cliff edge read as a wall. The ground east of the edge is 10 m DOWN, so the
        // steep destination normal must not refuse the step - the character walks off, finds no support, and falls.
        var t = Tuning;
        Func<float, float, float> ground = HeightPastEdge(-10f);
        var s = new MoveState { Position = new Vector3(EdgeX - 0.5f, t.CapsuleHalfHeight, 0f), Grounded = true };
        float startY = s.Position.Y;

        int airborneTick = -1;
        for (int i = 0; i < 12; i++)
        {
            s = CharacterMovement.Step(s, East(), Dt, ground, t, NormalSteepPastEdge);
            if (!s.Grounded && airborneTick < 0) airborneTick = i;
        }

        Assert.True(s.Position.X > EdgeX, $"the walk was refused at the cliff edge, x={s.Position.X:F3}");
        Assert.InRange(airborneTick, 0, 5);
        Assert.False(s.Grounded);
        Assert.True(s.VerticalVelocity < 0f, $"the character is not falling, vVel={s.VerticalVelocity:F3}");
        Assert.True(s.Position.Y < startY, $"the character did not descend, y={s.Position.Y:F3}");
    }

    [Fact]
    public void Grounded_walk_into_a_steep_rise_is_still_blocked()
    {
        // The preserved half of the rule. East of the edge the ground climbs at 5:1 under a steep normal, so the
        // move is refused every tick and the character neither advances onto the face nor gains height on it.
        var t = Tuning;
        Func<float, float, float> ground = (x, z) => x < EdgeX ? 0f : (x - EdgeX) * 5f;
        var s = new MoveState { Position = new Vector3(EdgeX - 0.95f, t.CapsuleHalfHeight, 0f), Grounded = true };

        for (int i = 0; i < 120; i++) s = CharacterMovement.Step(s, East(run: true), Dt, ground, t, NormalSteepPastEdge);

        Assert.True(s.Position.X < EdgeX + 0.05f, $"climbed onto the steep face, x={s.Position.X:F3}");
        Assert.True(s.Position.Y < t.CapsuleHalfHeight + 0.05f, $"gained height on a wall, y={s.Position.Y:F3}");
        Assert.True(s.Grounded);
    }

    [Theory]
    [InlineData(1f, true)]        // 0.05 m over a 0.2 m walk step is a ~14 deg ascent: well inside the 45 deg gate
    [InlineData(0.01f, false)]    // the SAME rise over a 0.002 m crawl step is ~88 deg: refused, however slowly it is met
    public void The_ascent_gate_is_relative_to_the_tick_s_travel(float steeringLength, bool expectAdvance)
    {
        // The gate's ascent allowance is SCALE-FREE: what it permits is a gradient (rise per metre of intended
        // horizontal travel), not a fixed height. One fixture, one rise, two speeds, opposite answers - which is the
        // whole property, and which an absolute tolerance cannot express: under one it was the SPEED that decided
        // whether a face was climbable, so a slow enough mover walked up anything.
        var t = Tuning;
        Func<float, float, float> ground = HeightPastEdge(0.05f);
        var s = new MoveState { Position = new Vector3(EdgeX - 0.05f, t.CapsuleHalfHeight, 0f), Grounded = true };

        for (int i = 0; i < 600; i++)
            s = CharacterMovement.StepTowards(s, new Vector2(steeringLength, 0f), run: false, Dt, ground, t, NormalSteepPastEdge);

        Assert.Equal(expectAdvance, s.Position.X > EdgeX + 0.05f);
    }

    // ---- The gate is scale-free: a face steeper than it blocks at EVERY speed and EVERY tick rate ----

    // A 5:1 face (78.7 deg) east of the edge, with its OWN agreeing normal rather than the fixture normal above, so
    // the height field and the normal describe one surface and nothing rides on them disagreeing.
    const float SteepGrade = 5f;
    static Func<float, float, float> RisingFacePastEdge => (x, z) => x < EdgeX ? 0f : (x - EdgeX) * SteepGrade;
    static readonly Vector3 RisingFaceNormal = Vector3.Normalize(new Vector3(-SteepGrade, 1f, 0f));
    static Func<float, float, Vector3> RisingFaceNormalPastEdge => (x, z) => x < EdgeX ? Vector3.UnitY : RisingFaceNormal;

    [Theory]
    [InlineData(1f)]        // full walk speed: 0.2 m per tick
    [InlineData(0.02f)]     // 0.004 m per tick
    [InlineData(0.01f)]     // 0.002 m per tick - the rise per tick lands exactly ON the old absolute tolerance
    [InlineData(0.005f)]    // 0.001 m per tick - and under it, which is a free climb
    public void No_steering_length_creeps_up_a_steep_face(float steeringLength)
    {
        // The regression this closes: the ascent allowance used to be an absolute 0.01 m, so any mover whose per-tick
        // rise stayed under it climbed an arbitrarily steep face one sub-centimetre at a time. A 5:1 face rises five
        // times the tick's travel, so it is past the gate at every one of these steering lengths and must be refused
        // at all of them.
        var t = Tuning;
        Func<float, float, float> ground = RisingFacePastEdge;
        var s = new MoveState { Position = new Vector3(EdgeX - 0.05f, t.CapsuleHalfHeight, 0f), Grounded = true };
        float startY = s.Position.Y;

        for (int i = 0; i < 600; i++)
        {
            s = CharacterMovement.StepTowards(s, new Vector2(steeringLength, 0f), run: false, Dt, ground, t,
                RisingFaceNormalPastEdge);
            Assert.True(s.Position.X <= EdgeX + 1e-3f, $"tick {i} climbed onto the face, x={s.Position.X:F5}");
            Assert.True(s.Position.Y <= startY + 1e-3f, $"tick {i} gained height on the face, y={s.Position.Y:F5}");
        }
    }

    [Theory]
    [InlineData(1f / 30f)]   // the shipped server tick
    [InlineData(0.001f)]     // 1000 Hz: the per-tick rise of a 46 deg face falls under any fixed height tolerance
    public void A_face_just_past_the_gate_blocks_at_every_tick_rate(float dt)
    {
        // 46 deg is one degree past the default gate, which is the hardest case for a relative rule and the one a
        // fixed tolerance loses first: raise the tick rate and the per-tick rise shrinks without the face getting any
        // shallower. The mover may enter by at most what a gate-angle ramp would have granted it for one tick's
        // travel, and then it is fenced for good.
        var t = Tuning;
        float grade = MathF.Tan(46f * MathF.PI / 180f);
        Func<float, float, float> ground = (x, z) => x < EdgeX ? 0f : (x - EdgeX) * grade;
        Vector3 faceNormal = Vector3.Normalize(new Vector3(-grade, 1f, 0f));
        Func<float, float, Vector3> normal = (x, z) => x < EdgeX ? Vector3.UnitY : faceNormal;

        var s = new MoveState { Position = new Vector3(EdgeX - 0.05f, t.CapsuleHalfHeight, 0f), Grounded = true };
        float startY = s.Position.Y;
        float travel = t.WalkSpeed * dt;                     // one tick's intended horizontal travel
        float allowance = travel + 1e-3f;                    // tan(45 deg) is 1, so the gate's own rise over that travel

        Vector3 halfway = Vector3.Zero;
        for (int i = 0; i < 600; i++)
        {
            s = CharacterMovement.StepTowards(s, new Vector2(1f, 0f), run: false, dt, ground, t, normal);
            if (i == 299) halfway = s.Position;
            Assert.True(s.Position.X <= EdgeX + allowance, $"tick {i} walked up the face, x={s.Position.X:F5}");
            Assert.True(s.Position.Y <= startY + allowance, $"tick {i} climbed the face, y={s.Position.Y:F5}");
        }
        // The real proof that it is FENCED and not merely slow: nothing moved over the second half of the run.
        Assert.Equal(halfway.X, s.Position.X, 4);
        Assert.Equal(halfway.Y, s.Position.Y, 4);
    }

    [Theory]
    [InlineData(0.05f)]     // a heavy slow
    [InlineData(0.01f)]     // near-rooted: 0.002 m per tick, under the old absolute tolerance
    public void A_slowed_character_cannot_creep_up_a_steep_face(float speedScale)
    {
        // SpeedScale is a movement multiplier the server owns (haste/slow/root). A slow must not turn into a climbing
        // aid, which is what a fixed height tolerance made it: the smaller the scale, the smaller the per-tick rise,
        // and below the tolerance the face stopped being a wall.
        var t = Tuning;
        Func<float, float, float> ground = RisingFacePastEdge;
        var s = new MoveState
        {
            Position = new Vector3(EdgeX - 0.05f, t.CapsuleHalfHeight, 0f),
            Grounded = true,
            SpeedScale = speedScale,
        };
        float startY = s.Position.Y;

        for (int i = 0; i < 600; i++)
        {
            s = CharacterMovement.Step(s, East(), Dt, ground, t, RisingFaceNormalPastEdge);
            Assert.True(s.Position.X <= EdgeX + 1e-3f, $"tick {i} climbed onto the face, x={s.Position.X:F5}");
            Assert.True(s.Position.Y <= startY + 1e-3f, $"tick {i} gained height on the face, y={s.Position.Y:F5}");
        }
    }

    // ---- Airborne: the anti-tunnel property survives ----

    [Fact]
    public void Airborne_into_a_face_above_the_feet_is_blocked_and_never_tunnels()
    {
        // A 10 m cliff whose top the character is nowhere near. Every tick the destination ground stands above the
        // feet, so the flight is refused: the capsule must never be committed to an XZ where the terrain is over
        // its feet, which is what would leave the ground clamp to pop it up the cliff on a later tick.
        var t = Tuning;
        Func<float, float, float> ground = HeightPastEdge(10f);
        var s = new MoveState
        {
            Position = new Vector3(EdgeX - 0.5f, t.CapsuleHalfHeight + 2f, 0f),
            Grounded = false,
            TimeSinceGrounded = 1f,
        };

        for (int i = 0; i < 90; i++)
        {
            s = CharacterMovement.Step(s, East(run: true), Dt, ground, t, NormalSteepPastEdge);
            Assert.True(s.Position.X <= EdgeX,
                $"tick {i} entered the cliff face's footprint, x={s.Position.X:F3}");
            Assert.True(ground(s.Position.X, s.Position.Z) <= s.Position.Y - t.CapsuleHalfHeight + 1e-3f,
                $"tick {i} left the capsule under terrain, y={s.Position.Y:F3}");
        }
    }

    [Fact]
    public void Airborne_momentum_into_a_face_above_the_feet_is_blocked()
    {
        // Same rule on the carried-velocity path, and the refused move must not survive into the carry either.
        MoveTuning t = Tuning with { AirMomentum = true };
        Func<float, float, float> ground = HeightPastEdge(10f);
        var s = new MoveState
        {
            Position = new Vector3(EdgeX - 0.5f, t.CapsuleHalfHeight + 2f, 0f),
            Grounded = false,
            TimeSinceGrounded = 1f,
            HorizontalVelocity = new Vector2(20f, 0f),
        };

        for (int i = 0; i < 30; i++)
        {
            s = CharacterMovement.Step(s, MoveCommand.Idle, Dt, ground, t, NormalSteepPastEdge);
            Assert.True(s.Position.X <= EdgeX, $"tick {i} tunnelled the face, x={s.Position.X:F3}");
            Assert.True(ground(s.Position.X, s.Position.Z) <= s.Position.Y - t.CapsuleHalfHeight + 1e-3f,
                $"tick {i} left the capsule under terrain, y={s.Position.Y:F3}");
        }
        Assert.Equal(Vector2.Zero, s.HorizontalVelocity);
    }

    [Fact]
    public void Airborne_momentum_out_over_a_steep_drop_keeps_flying()
    {
        // The descent half on the momentum path: a flight out over a canyon meets a steep destination normal whose
        // ground is far below the feet, so the arc carries on instead of being frozen in mid-air.
        MoveTuning t = Tuning with { AirMomentum = true };
        Func<float, float, float> ground = HeightPastEdge(-40f);
        var s = new MoveState
        {
            Position = new Vector3(EdgeX - 0.5f, t.CapsuleHalfHeight, 0f),
            Grounded = false,
            TimeSinceGrounded = 1f,
            HorizontalVelocity = new Vector2(20f, 0f),
        };

        for (int i = 0; i < 30; i++) s = CharacterMovement.Step(s, MoveCommand.Idle, Dt, ground, t, NormalSteepPastEdge);

        Assert.True(s.Position.X > EdgeX + 5f, $"the arc was frozen over the canyon, x={s.Position.X:F3}");
        Assert.Equal(20f, s.HorizontalVelocity.Length(), 2);
        Assert.True(s.VerticalVelocity < 0f);
    }

    // ---- #440: airtime must never buy admission onto a too-steep face ----

    // Walk east with the jump button HELD: the jump buffer re-fires it on every landing tick, so the character runs a
    // continuous jump-hop cycle into whatever is in front of it - which is exactly how the exploit was played.
    static MoveCommand EastJump(bool run = false) => new(new Vector2(1f, 0f), run, cameraYaw: 0f, jump: true);

    // A jump's own apex above its launch height, discrete-integration slack included. Nothing in these fixtures may
    // ever be higher than this above the base: exceeding it means the face itself gave the character altitude.
    static float JumpApex(in MoveTuning t) => t.JumpSpeed * t.JumpSpeed / (2f * t.Gravity) + 0.05f;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]     // the carried-velocity path takes the same gate, so the exploit closes on both
    public void Repeated_jumping_into_a_steep_face_gains_no_altitude(bool airMomentum)
    {
        // THE PLAYTESTED EXPLOIT (#440), reproduced on a 78.7 deg sea cliff. Jumping raises the feet, and the gate used
        // to measure its ascent from the feet alone: at the apex the face's local ground was level with them, the rise
        // read as ~0, the sideways drift onto the face was admitted, the ground clamp seated the character on the face,
        // and the next jump repeated - about a jump height of free climb per cycle, up a face no walk can enter.
        MoveTuning t = Tuning with { AirMomentum = airMomentum };
        Func<float, float, float> ground = RisingFacePastEdge;
        var s = new MoveState { Position = new Vector3(EdgeX - 0.05f, t.CapsuleHalfHeight, 0f), Grounded = true };
        float baseFeetY = s.Position.Y - t.CapsuleHalfHeight;
        float apex = JumpApex(t);

        // ~24 ticks per arc at 30 Hz (2 * 9.798 / 25 = 0.784 s), so 400 ticks is 16 full jump cycles.
        int jumps = 0;
        for (int i = 0; i < 400; i++)
        {
            s = CharacterMovement.Step(s, EastJump(), Dt, ground, t, RisingFaceNormalPastEdge);
            if (s.VerticalVelocity == t.JumpSpeed) jumps++;   // the launch tick stamps the speed exactly
            Assert.True(s.Position.X <= EdgeX + 1e-3f, $"tick {i} entered the face, x={s.Position.X:F5}");
            Assert.True(s.Position.Y - t.CapsuleHalfHeight <= baseFeetY + apex,
                $"tick {i} rose higher than one jump above the base, feetY={s.Position.Y - t.CapsuleHalfHeight:F5}");
        }
        Assert.True(jumps >= 10, $"the fixture never ran 10 jump cycles, jumps={jumps}");

        // Release the button and let the last arc settle, so the final altitude is a landed one and not mid-flight.
        for (int i = 0; i < 60; i++) s = CharacterMovement.Step(s, East(), Dt, ground, t, RisingFaceNormalPastEdge);
        Assert.True(s.Grounded, "the character never settled");
        Assert.InRange(s.Position.Y - t.CapsuleHalfHeight, baseFeetY - 1e-3f, baseFeetY + t.StepHeight);
        Assert.True(s.Position.X <= EdgeX + 1e-3f, $"the run ended on the face, x={s.Position.X:F5}");
    }

    [Fact]
    public void A_jump_off_a_clifftop_still_carries_out_over_the_face_and_lands_below()
    {
        // The descent half, at airtime: a destination column BELOW the current one is below BOTH terms of the new
        // reference, so a clifftop jump reads its 10 m drop as the descent it is on every airborne tick and the arc
        // carries out over the face exactly as before. Blocking this would turn every cliff into flypaper.
        var t = Tuning;
        const float Top = 10f;
        Func<float, float, float> ground = (x, z) => x < EdgeX ? Top : 0f;
        var s = new MoveState { Position = new Vector3(EdgeX - 0.5f, Top + t.CapsuleHalfHeight, 0f), Grounded = true };

        bool clearedTheEdgeInFlight = false, descended = false;
        for (int i = 0; i < 120; i++)
        {
            // Jump on the first tick only: one clean jump-off, then the fall is gravity's.
            var cmd = new MoveCommand(new Vector2(1f, 0f), run: true, cameraYaw: 0f, jump: i == 0);
            s = CharacterMovement.Step(s, cmd, Dt, ground, t, NormalSteepPastEdge);
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
    public void Falling_alongside_a_steep_face_while_steering_into_it_never_seats_mid_face()
    {
        // The exploit's passive twin, and the reason the fix is not a jump special case: ANY airtime used to discount
        // the face, so a character falling past one while holding "into" it drifted onto its footprint and was seated
        // by the ground clamp partway up (from 20 m this landed ~14 m up a 5:1 face). The toe is the only altitude a
        // fall beside this face may end at.
        var t = Tuning;
        Func<float, float, float> ground = RisingFacePastEdge;
        var s = new MoveState
        {
            Position = new Vector3(EdgeX - 0.5f, t.CapsuleHalfHeight + 20f, 0f),
            Grounded = false,
            TimeSinceGrounded = 1f,
        };

        for (int i = 0; i < 180; i++)
        {
            s = CharacterMovement.Step(s, East(run: true), Dt, ground, t, RisingFaceNormalPastEdge);
            Assert.True(s.Position.X <= EdgeX + 1e-3f, $"tick {i} drifted onto the face, x={s.Position.X:F5}");
            if (s.Grounded)
                Assert.Equal(t.CapsuleHalfHeight, s.Position.Y, 3);   // the toe: the only ground there is to land on
        }
        Assert.True(s.Grounded, "the fall never landed");
    }

    // ---- Regression guard: the step-up path is untouched ----

    [Fact]
    public void A_legal_riser_within_StepHeight_still_mounts()
    {
        // The height comparison must not reach the prop step-up. A 0.3 m riser on flat analytic terrain (the way
        // the demos feed a ground normal) is inside StepHeight, so the swept step-up mounts it exactly as before.
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
