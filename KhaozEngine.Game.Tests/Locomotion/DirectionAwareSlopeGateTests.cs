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
// The comparison is against the FEET, not the current ground height, and that is the anti-tunnel property:
// flying into a cliff face whose ground stands above your feet stays blocked, so the XZ can never be committed
// under terrain and left waiting for a ground clamp to pop the capsule up the cliff.
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
    [InlineData(0.002f, true)]    // inside the ascent tolerance: level enough to walk across
    [InlineData(0.05f, false)]    // outside it: a rise the gate must refuse
    public void The_ascent_tolerance_is_small(float rise, bool expectAdvance)
    {
        // Pins the tolerance into (0.002, 0.05) m without turning it into a knob: it exists only to absorb the
        // surface-contact skin and float noise in the height comparison, never to grant a free climb.
        var t = Tuning;
        Func<float, float, float> ground = HeightPastEdge(rise);
        var s = new MoveState { Position = new Vector3(EdgeX - 0.95f, t.CapsuleHalfHeight, 0f), Grounded = true };

        for (int i = 0; i < 60; i++) s = CharacterMovement.Step(s, East(), Dt, ground, t, NormalSteepPastEdge);

        Assert.Equal(expectAdvance, s.Position.X > EdgeX + 0.5f);
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
