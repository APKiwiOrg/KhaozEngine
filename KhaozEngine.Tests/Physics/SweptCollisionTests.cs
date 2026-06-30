using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public class SweptCollisionTests
{
    private static readonly MoveTuning Tuning = new(
        WalkSpeed: 3f, RunSpeed: 6f, CapsuleHalfHeight: 0.9f, MaxSlopeRadians: 0.9f);
    private static float Flat(float x, float z) => 0f;

    // One-sided thin quad wall in the XY plane at z=2 (front face normal -Z, toward the approaching capsule),
    // spanning x[-10,10] (wide enough to block for a full sliding test), y[0,3]. A single quad => two triangles,
    // ~0.0 m thick: the classic tunnel trap.
    private static TriangleMeshShape ThinWallAtZ2()
    {
        var v = new[]
        {
            new Vector3(-10f, 0f, 2f), new Vector3(10f, 0f, 2f),
            new Vector3(10f, 3f, 2f), new Vector3(-10f, 3f, 2f),
        };
        // Wound so the front face normal points -Z (toward the capsule coming from z<2).
        var idx = new[] { 0, 2, 1, 0, 3, 2 };
        return new TriangleMeshShape(v, idx);
    }

    [Fact]
    public void FastMove_DoesNotTunnelThroughThinOneSidedWall()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(ThinWallAtZ2(), Pose.At(Vector3.Zero));
        world.Step(1f / 60f);

        // Drive straight toward +Z (Move.Y=-1 at yaw=0 => +Z). A LARGE dt (0.1 s) at run speed makes one tick's
        // displacement ~0.6 m, well over the 0.4 m capsule radius - the regime where the old teleport-then-
        // depenetrate resolver tunnels through the one-sided quad (low-frame-rate clients hit exactly this).
        const float BigDt = 0.1f;
        var state = new MoveState { Position = new Vector3(0f, 0.9f, 0f), Grounded = true };
        var run = new MoveCommand(new Vector2(0f, -1f), run: true, cameraYaw: 0f, jump: false);
        for (int i = 0; i < 60; i++)
            state = CharacterMovement.Step(state, run, BigDt, Flat, Tuning, groundNormal: null, world: world);

        // The capsule centre must stay on the near side of the wall (z < 2 - radius + skin), never past it.
        Assert.True(state.Position.Z < 1.65f, $"tunneled through the thin wall, z={state.Position.Z}");
    }

    [Fact]
    public void Diagonal_SlidesAlongWall_NoPenetration()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(ThinWallAtZ2(), Pose.At(Vector3.Zero));
        world.Step(1f / 60f);

        // Move diagonally into the wall (toward +Z and +X): expect blocked in Z, sliding in +X.
        var state = new MoveState { Position = new Vector3(0f, 0.9f, 0f), Grounded = true };
        var diag = new MoveCommand(Vector2.Normalize(new Vector2(1f, -1f)), run: false, cameraYaw: 0f, jump: false);
        for (int i = 0; i < 120; i++)
            state = CharacterMovement.Step(state, diag, 1f / 60f, Flat, Tuning, groundNormal: null, world: world);

        Assert.True(state.Position.Z < 1.65f, $"penetrated/over-advanced into wall, z={state.Position.Z}");
        Assert.True(state.Position.X > 1.0f, $"did not slide along the wall, x={state.Position.X}");
    }

    [Fact]
    public void InnerCorner_StopsWithoutPenetration()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(ThinWallAtZ2(), Pose.At(Vector3.Zero));            // wall at z=2 (faces -Z)
        // Side wall at x=2 facing -X: quad in the ZY plane.
        var sv = new[]
        {
            new Vector3(2f, 0f, -3f), new Vector3(2f, 0f, 3f),
            new Vector3(2f, 3f, 3f), new Vector3(2f, 3f, -3f),
        };
        world.AddStatic(new TriangleMeshShape(sv, new[] { 0, 1, 2, 0, 2, 3 }), Pose.At(Vector3.Zero));
        world.Step(1f / 60f);

        var state = new MoveState { Position = new Vector3(0f, 0.9f, 0f), Grounded = true };
        var diag = new MoveCommand(Vector2.Normalize(new Vector2(1f, -1f)), run: false, cameraYaw: 0f, jump: false);
        for (int i = 0; i < 240; i++)
            state = CharacterMovement.Step(state, diag, 1f / 60f, Flat, Tuning, groundNormal: null, world: world);

        // Wedged into the corner: stopped short of both faces (centre within radius+skin of each).
        Assert.True(state.Position.Z < 1.65f && state.Position.X < 1.65f,
            $"corner not respected: pos={state.Position}");
        // And stable (no NaN / fling).
        Assert.True(float.IsFinite(state.Position.X) && float.IsFinite(state.Position.Z));
    }

    [Fact]
    public void Stairs_WithRisersUnderStepHeight_AreWalkable()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        // Three solid box steps, each riser 0.25 m (< StepHeight 0.4). Step s spans z[2+0.4s, 2+0.4s+0.4] and
        // y[0, 0.25*(s+1)]. One-sided-mesh richness/trap behavior is covered by the sibling thin-wall / closed-
        // shell tests; this fixture isolates the step-up probe (shape-agnostic: it sweeps).
        for (int s = 0; s < 3; s++)
        {
            float topY = 0.25f * (s + 1);
            float zCentre = 2f + 0.4f * s + 0.2f;
            world.AddStatic(new BoxShape(new Vector3(3f, topY * 0.5f, 0.2f)),
                Pose.At(new Vector3(0f, topY * 0.5f, zCentre)));
        }
        world.Step(1f / 60f);

        var state = new MoveState { Position = new Vector3(0f, 0.9f, 0f), Grounded = true };
        var fwd = new MoveCommand(new Vector2(0f, -1f), run: false, cameraYaw: 0f, jump: false);   // toward +Z
        // Walk forward and track the peak elevation/advance: the step-up probe mounts the capsule onto each tread,
        // so it climbs the 3-step staircase, then walks off the far end back to terrain (the staircase is only
        // ~1.2 m deep, far shorter than 300 ticks of travel). Asserting on the peak (not the final resting state)
        // isolates "did it climb the stairs", independent of how far past the finite staircase it walks.
        float peakY = state.Position.Y, peakZ = state.Position.Z;
        for (int i = 0; i < 300; i++)
        {
            state = CharacterMovement.Step(state, fwd, 1f / 60f, Flat, Tuning, groundNormal: null, world: world);
            if (state.Position.Y > peakY) peakY = state.Position.Y;
            if (state.Position.Z > peakZ) peakZ = state.Position.Z;
        }

        // Climbed at least the first step: capsule centre rose from 0.9 (terrain rest) onto a tread
        // (>= 0.25 + halfHeight), and advanced onto the stairs.
        Assert.True(peakY > 0.9f + 0.2f, $"did not climb the stairs, peak y={peakY}");
        Assert.True(peakZ > 2.2f, $"did not advance onto the stairs, peak z={peakZ}");
    }

    // A CLOSED one-sided box mesh (6 outward-wound quads = 12 triangles), centred at `c`, half-extent `h`. This is
    // the trap-risk shape: a hollow building shell whose INNER faces generate no contacts, so if the capsule ever
    // got inside it could never eject. The swept resolver must keep it OUTSIDE at all times.
    private static TriangleMeshShape ClosedOneSidedBox(Vector3 c, float h)
    {
        var v = new System.Collections.Generic.List<Vector3>();
        var idx = new System.Collections.Generic.List<int>();
        void Quad(Vector3 a, Vector3 b, Vector3 cc, Vector3 d)
        {
            int b0 = v.Count; v.Add(a + c); v.Add(b + c); v.Add(cc + c); v.Add(d + c);
            idx.Add(b0); idx.Add(b0 + 1); idx.Add(b0 + 2);
            idx.Add(b0); idx.Add(b0 + 2); idx.Add(b0 + 3);
        }
        // Outward-wound faces (front normal = cross(v1-v0, v2-v0) points AWAY from the centre).
        Quad(new(h, -h, -h), new(h, h, -h), new(h, h, h), new(h, -h, h));       // +X
        Quad(new(-h, -h, h), new(-h, h, h), new(-h, h, -h), new(-h, -h, -h));   // -X
        Quad(new(-h, h, -h), new(-h, h, h), new(h, h, h), new(h, h, -h));       // +Y
        Quad(new(-h, -h, -h), new(h, -h, -h), new(h, -h, h), new(-h, -h, h));   // -Y
        Quad(new(-h, -h, h), new(h, -h, h), new(h, h, h), new(-h, h, h));       // +Z
        Quad(new(-h, -h, -h), new(-h, h, -h), new(h, h, -h), new(h, -h, -h));   // -Z
        return new TriangleMeshShape(v.ToArray(), idx.ToArray());
    }

    [Fact]
    public void FastMove_NeverEntersClosedOneSidedShell()
    {
        // Closed hollow box (building shell) half-extent 1.5 centred at (0,0,4): AABB x,y in [-1.5,1.5], z in [2.5,5.5].
        // One-sided faces => the inside cannot eject. Drive hard (BigDt=0.1 ~0.6 m/tick at run = >radius, the tunnel
        // regime) at the near face AND at a corner; the capsule centre must NEVER be inside the shell.
        var box = ClosedOneSidedBox(new Vector3(0f, 0f, 4f), 1.5f);
        const float BigDt = 0.1f;
        static bool Inside(Vector3 p) =>
            p.X > -1.5f && p.X < 1.5f && p.Y > -1.5f && p.Y < 1.5f && p.Z > 2.5f && p.Z < 5.5f;

        using (IPhysicsWorld world = new BepuPhysicsWorld())
        {
            world.AddStatic(box, Pose.At(Vector3.Zero)); world.Step(1f / 60f);
            var state = new MoveState { Position = new Vector3(0f, 0.9f, 0f), Grounded = true };
            var run = new MoveCommand(new Vector2(0f, -1f), run: true, cameraYaw: 0f, jump: false);   // toward +Z
            for (int i = 0; i < 300; i++)
            {
                state = CharacterMovement.Step(state, run, BigDt, Flat, Tuning, groundNormal: null, world: world);
                Assert.False(Inside(state.Position), $"tick {i}: capsule entered the closed shell at {state.Position}");
            }
        }

        using (IPhysicsWorld world = new BepuPhysicsWorld())
        {
            world.AddStatic(box, Pose.At(Vector3.Zero)); world.Step(1f / 60f);
            var state = new MoveState { Position = new Vector3(-3f, 0.9f, 0f), Grounded = true };
            var diag = new MoveCommand(Vector2.Normalize(new Vector2(1f, -1f)), run: true, cameraYaw: 0f, jump: false); // +X +Z, at a corner
            for (int i = 0; i < 300; i++)
            {
                state = CharacterMovement.Step(state, diag, BigDt, Flat, Tuning, groundNormal: null, world: world);
                Assert.False(Inside(state.Position), $"tick {i}: capsule entered the closed shell (corner) at {state.Position}");
            }
        }
    }

    // A tall solid prop (tree trunk / rock pillar): a convex-hull cylinder, radius `r`, spanning y[0, height],
    // axis at `c`. Vertical sides (not walkable) + a flat top - the shape a player should be stopped at the base
    // of, never hauled up the side of.
    private static ConvexHullShape TrunkHull(Vector3 c, float r = 0.5f, float height = 3f)
    {
        var pts = new System.Collections.Generic.List<Vector3>();
        for (int i = 0; i < 16; i++)
        {
            float a = i * MathF.PI * 2f / 16f;
            var o = new Vector3(MathF.Cos(a) * r, 0f, MathF.Sin(a) * r);
            pts.Add(c + o);
            pts.Add(c + o + new Vector3(0f, height, 0f));
        }
        return new ConvexHullShape(pts.ToArray());
    }

    [Fact]
    public void BesideAProp_CapsuleIsNotLiftedUpItsSide()
    {
        // Regression (the "float up trees/rocks/walls" bug): the downward support-floor sweep latched onto a prop
        // the capsule is pressed BESIDE (not on) - it grazes the prop's top from the side, reports it as the floor,
        // and the pos.Y = groundY snap HAULS the capsule up onto the prop top. It triggers once the capsule is even
        // slightly above the terrain (airborne / a jump / uneven ground / a shove). A capsule beside a tall trunk
        // must fall back to the ground, never be lifted onto the ~3 m trunk top.
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(TrunkHull(Vector3.Zero), Pose.At(Vector3.Zero));   // trunk axis at origin, top at y=3
        world.Step(1f / 60f);

        // Capsule tangent to the trunk's -Z side (centre z = -(0.5 + 0.4) = -0.9), airborne at y=1.5 so the support
        // probe (which starts 1.8 m above the head) reaches above the trunk top and grazes it. Press toward +Z.
        var state = new MoveState { Position = new Vector3(0f, 1.5f, -0.9f), Grounded = false, VerticalVelocity = 0f };
        var intoTrunk = new MoveCommand(new Vector2(0f, -1f), run: false, cameraYaw: 0f, jump: false);

        float peakY = state.Position.Y;
        for (int i = 0; i < 120; i++)
        {
            state = CharacterMovement.Step(state, intoTrunk, 1f / 60f, Flat, Tuning, groundNormal: null, world: world);
            if (state.Position.Y > peakY) peakY = state.Position.Y;
        }

        Assert.True(peakY < 1.6f, $"capsule was hauled up the trunk side (peak y={peakY}); it must not rise above its start");
        Assert.True(state.Position.Y < 1.1f, $"capsule did not fall back to the ground beside the trunk (y={state.Position.Y})");
    }

    [Fact]
    public void BesideAWall_AirborneCapsuleFallsInsteadOfHanging()
    {
        // The building case: a capsule airborne near a one-sided wall (the shape town buildings bake to) while
        // pressing into it must FALL back down, not hang pinned partway up it (grounded=no, stuck) or get hauled up
        // - the playtest "stuck up the building wall". Starts CLEAR of the wall (the swept move always keeps the
        // capsule a SkinWidth off a one-sided mesh in steady state; an exact-tangent start can't be depenetrated
        // since ComputePenetration does not report one-sided-mesh overlap, and never arises in play).
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(ThinWallAtZ2(), Pose.At(Vector3.Zero));   // wall at z=2, faces -Z, spans y[0,3]
        world.Step(1f / 60f);

        // Airborne at y=1.5, clear of the wall (z=1.0, front edge z=1.4), pressing +Z into it while falling.
        var state = new MoveState { Position = new Vector3(0f, 1.5f, 1.0f), Grounded = false, VerticalVelocity = 0f };
        var intoWall = new MoveCommand(new Vector2(0f, -1f), run: false, cameraYaw: 0f, jump: false);

        float peakY = state.Position.Y;
        for (int i = 0; i < 120; i++)
        {
            state = CharacterMovement.Step(state, intoWall, 1f / 60f, Flat, Tuning, groundNormal: null, world: world);
            if (state.Position.Y > peakY) peakY = state.Position.Y;
        }

        Assert.True(peakY < 1.6f, $"capsule rose up the wall (peak y={peakY})");
        Assert.True(state.Position.Y < 1.1f, $"capsule hung on the wall instead of falling (y={state.Position.Y})");
        Assert.True(state.Position.Z < 1.65f, $"capsule tunneled into the wall (z={state.Position.Z})");
    }

    [Fact]
    public void GroundedWalkIntoWall_StopsAndStrafes_NeverStuck()
    {
        // The building case of the tester's "stuck just walking into it": a grounded capsule walking into a
        // one-sided wall (a town building) must STOP at the wall, then STRAFE freely along it (not freeze), and walk
        // back away. Mirrors the trunk test with the one-sided mesh town buildings bake to.
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(ThinWallAtZ2(), Pose.At(Vector3.Zero));   // wall at z=2, faces -Z
        world.Step(1f / 60f);

        var state = new MoveState { Position = new Vector3(0f, 0.9f, 0f), Grounded = true };
        var into = new MoveCommand(new Vector2(0f, -1f), run: false, cameraYaw: 0f, jump: false);   // +Z into wall
        for (int i = 0; i < 200; i++)
            state = CharacterMovement.Step(state, into, 1f / 60f, Flat, Tuning, groundNormal: null, world: world);
        Assert.True(state.Position.Z < 1.65f && state.Position.Z > 1.5f, $"did not stop at the wall (z={state.Position.Z})");
        Assert.True(state.Position.Y < 1.0f, $"rose up the wall (y={state.Position.Y})");

        float xBefore = state.Position.X;
        var strafe = new MoveCommand(new Vector2(1f, 0f), run: false, cameraYaw: 0f, jump: false);   // +X along the wall
        for (int i = 0; i < 90; i++)
            state = CharacterMovement.Step(state, strafe, 1f / 60f, Flat, Tuning, groundNormal: null, world: world);
        Assert.True(state.Position.X > xBefore + 0.5f, $"frozen against the wall - could not strafe (x {xBefore:F2} -> {state.Position.X:F2})");
    }

    [Fact]
    public void RunJumpIntoWall_ArrivingAirborne_FallsBackToGround_NeverPins()
    {
        // The tester report (the screenshot): run up to a town building and jump into it, and get PINNED partway up
        // the wall (mid-jump pose, grounded=no, NOT falling - the position frozen while vertical velocity rails to
        // terminal). The case the grounded-walk-into (horizontal) and airborne-fall-beside-clear (starts off the
        // wall) tests miss: the capsule arrives at the one-sided wall WHILE AIRBORNE and pressing in, landing exactly
        // TANGENT to it via a graze the swept move does not register as a hit (pos += delta lands it flush on the
        // face). From then on every sweep starts touching, so SweepCapsule returns t=0 / zero-normal and the
        // resolver's degenerate branch makes no progress - up OR down. ComputePenetration cannot reopen a gap (a
        // one-sided face reports no overlap), so depenetrate-to-clearance is a no-op for buildings. The capsule must
        // fall back to the ground and end grounded, never freeze pinned against the wall.
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(ThinWallAtZ2(), Pose.At(Vector3.Zero));   // wall at z=2, faces -Z, spans y[0,3]
        world.Step(1f / 60f);

        // Grounded a short run-up back from the wall; jump on the first tick and keep running +Z into it. The
        // capsule clears the ground, rises, and reaches the wall on the way down - arriving airborne and tangent.
        var state = new MoveState { Position = new Vector3(0f, 0.9f, -1.5f), Grounded = true };
        float restY = state.Position.Y;
        var runIntoJump = new MoveCommand(new Vector2(0f, -1f), run: true, cameraYaw: 0f, jump: true);
        var runInto = new MoveCommand(new Vector2(0f, -1f), run: true, cameraYaw: 0f, jump: false);

        float peakY = state.Position.Y;
        for (int i = 0; i < 240; i++)
        {
            MoveCommand cmd = i == 0 ? runIntoJump : runInto;   // one jump press, then hold into the wall
            state = CharacterMovement.Step(state, cmd, 1f / 60f, Flat, Tuning, groundNormal: null, world: world);
            if (state.Position.Y > peakY) peakY = state.Position.Y;
        }

        // It actually jumped (left the ground during the arc).
        Assert.True(peakY > restY + 0.5f, $"did not jump (rest y={restY:F2}, peak y={peakY:F2})");
        // And came all the way back down: grounded on the terrain again, never pinned partway up the wall.
        Assert.True(state.Grounded, $"pinned mid-jump against the wall (grounded={state.Grounded}, pos={state.Position})");
        Assert.True(state.Position.Y < restY + 0.05f, $"hung partway up the wall instead of falling back (y={state.Position.Y:F2}, rest={restY:F2})");
        // And did not tunnel through it.
        Assert.True(state.Position.Z < 1.65f, $"tunneled into the wall (z={state.Position.Z:F2})");
    }

    [Fact]
    public void RunJumpIntoTallClosedShell_NeverEnters_AndLands()
    {
        // The realistic building case for the jump fix: a CLOSED one-sided shell (a hollow building - the trap-risk
        // shape, inner faces generate no contacts) taller than the jump, driven into at a run WHILE jumping. The
        // capsule must never get inside the shell (the swept resolver's trap-proofing must survive the degenerate-
        // contact recovery the jump fix adds), and must end grounded back on the terrain - not flung up and over.
        // Near face at z=1.5 (faces -Z), shell spans x[-2.5,2.5], y[-1.5,3.5], z[1.5,6.5]; jump apex (~head y 3.0)
        // stays below the y=3.5 roof, so the capsule presses a tall wall, never clearing it.
        var box = ClosedOneSidedBox(new Vector3(0f, 1f, 4f), 2.5f);
        static bool Inside(Vector3 p) =>
            p.X > -2.5f && p.X < 2.5f && p.Y > -1.5f && p.Y < 3.5f && p.Z > 1.5f && p.Z < 6.5f;

        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(box, Pose.At(Vector3.Zero));
        world.Step(1f / 60f);

        var state = new MoveState { Position = new Vector3(0f, 0.9f, -1f), Grounded = true };
        var runJump = new MoveCommand(new Vector2(0f, -1f), run: true, cameraYaw: 0f, jump: true);   // +Z into the shell
        var run = new MoveCommand(new Vector2(0f, -1f), run: true, cameraYaw: 0f, jump: false);
        for (int i = 0; i < 360; i++)
        {
            MoveCommand cmd = (i % 60 == 0) ? runJump : run;   // jump repeatedly while held against the wall
            state = CharacterMovement.Step(state, cmd, 1f / 60f, Flat, Tuning, groundNormal: null, world: world);
            Assert.False(Inside(state.Position), $"tick {i}: capsule entered the closed shell at {state.Position}");
        }
        Assert.True(state.Grounded, $"did not settle grounded after jumping at the wall (pos={state.Position})");
        Assert.True(state.Position.Y < 1.0f, $"flung up / left on the roof instead of on the ground (y={state.Position.Y:F2})");
    }

    [Fact]
    public void JumpStraightUpAtWall_SlidesUpAndLands()
    {
        // The settled case (regression guard, passes pre-fix): a capsule grounded flush against a one-sided wall,
        // jumping straight up while pressed in, slides UP the wall (up is parallel to the face) and lands back down.
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(ThinWallAtZ2(), Pose.At(Vector3.Zero));   // wall at z=2, faces -Z, spans y[0,3]
        world.Step(1f / 60f);

        var state = new MoveState { Position = new Vector3(0f, 0.9f, 0f), Grounded = true };
        var into = new MoveCommand(new Vector2(0f, -1f), run: false, cameraYaw: 0f, jump: false);   // +Z into wall
        for (int i = 0; i < 90; i++)
            state = CharacterMovement.Step(state, into, 1f / 60f, Flat, Tuning, groundNormal: null, world: world);
        Assert.True(state.Position.Z > 1.5f && state.Grounded, $"setup: did not settle grounded at the wall (pos={state.Position}, grounded={state.Grounded})");
        float restY = state.Position.Y;

        var jumpInto = new MoveCommand(new Vector2(0f, -1f), run: false, cameraYaw: 0f, jump: true);
        float peakY = state.Position.Y;
        for (int i = 0; i < 240; i++)
        {
            MoveCommand cmd = i == 0 ? jumpInto : into;
            state = CharacterMovement.Step(state, cmd, 1f / 60f, Flat, Tuning, groundNormal: null, world: world);
            if (state.Position.Y > peakY) peakY = state.Position.Y;
        }

        Assert.True(peakY > restY + 0.5f, $"did not rise up the wall on the jump (rest y={restY:F2}, peak y={peakY:F2})");
        Assert.True(state.Grounded, $"pinned mid-jump against the wall (grounded={state.Grounded}, pos={state.Position})");
        Assert.True(state.Position.Y < restY + 0.05f, $"hung partway up the wall instead of falling back (y={state.Position.Y:F2}, rest={restY:F2})");
        Assert.True(state.Position.Z < 1.65f, $"tunneled into the wall (z={state.Position.Z:F2})");
    }

    // One-sided side wall at x=2 facing -X (ZY plane); with ThinWallAtZ2 it forms an inner corner at (x=2, z=2).
    private static TriangleMeshShape SideWallAtX2()
    {
        var sv = new[]
        {
            new Vector3(2f, 0f, -10f), new Vector3(2f, 0f, 10f),
            new Vector3(2f, 3f, 10f), new Vector3(2f, 3f, -10f),
        };
        return new TriangleMeshShape(sv, new[] { 0, 1, 2, 0, 2, 3 });
    }

    [Fact]
    public void RunJumpIntoInnerCorner_ArrivingAirborne_FallsBackToGround()
    {
        // The corner variant of the jump pin (the screenshot shows a player pinned at a building CORNER): two
        // one-sided walls meeting at an inner corner, run-jumped into so the capsule arrives airborne and tangent to
        // BOTH faces at once. Each degenerate contact must recover independently so the capsule slides down the
        // corner and lands, never freezing wedged mid-corner, and never tunnels either wall.
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(ThinWallAtZ2(), Pose.At(Vector3.Zero));    // z=2, faces -Z
        world.AddStatic(SideWallAtX2(), Pose.At(Vector3.Zero));    // x=2, faces -X
        world.Step(1f / 60f);

        var state = new MoveState { Position = new Vector3(0f, 0.9f, 0f), Grounded = true };
        float restY = state.Position.Y;
        var diagJump = new MoveCommand(Vector2.Normalize(new Vector2(1f, -1f)), run: true, cameraYaw: 0f, jump: true);   // +X +Z into the corner
        var diag = new MoveCommand(Vector2.Normalize(new Vector2(1f, -1f)), run: true, cameraYaw: 0f, jump: false);

        float peakY = state.Position.Y;
        for (int i = 0; i < 240; i++)
        {
            MoveCommand cmd = i == 0 ? diagJump : diag;
            state = CharacterMovement.Step(state, cmd, 1f / 60f, Flat, Tuning, groundNormal: null, world: world);
            if (state.Position.Y > peakY) peakY = state.Position.Y;
        }

        Assert.True(peakY > restY + 0.5f, $"did not jump (rest y={restY:F2}, peak y={peakY:F2})");
        Assert.True(state.Grounded, $"pinned mid-jump in the corner (grounded={state.Grounded}, pos={state.Position})");
        Assert.True(state.Position.Y < restY + 0.05f, $"hung in the corner instead of falling back (y={state.Position.Y:F2})");
        Assert.True(state.Position.Z < 1.65f && state.Position.X < 1.65f, $"tunneled into a corner wall (pos={state.Position})");
    }

    [Fact]
    public void GroundedWalkIntoTrunk_DoesNotStickOrFloat()
    {
        // The tester report: "stuck just walking into a tree". A GROUNDED capsule walking horizontally into a trunk
        // (the common case, vs the airborne float-up tests above) must not float up, not tunnel through, must still
        // be able to STRAFE around it (not freeze in place), and must be able to walk back away.
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(TrunkHull(new Vector3(0f, 0f, 2f)), Pose.At(Vector3.Zero));   // trunk axis at z=2, top y=3
        world.Step(1f / 60f);

        var state = new MoveState { Position = new Vector3(0f, 0.9f, 0f), Grounded = true };
        var into = new MoveCommand(new Vector2(0f, -1f), run: false, cameraYaw: 0f, jump: false);   // +Z into trunk
        float peakY = state.Position.Y;
        for (int i = 0; i < 120; i++)
        {
            state = CharacterMovement.Step(state, into, 1f / 60f, Flat, Tuning, groundNormal: null, world: world);
            if (state.Position.Y > peakY) peakY = state.Position.Y;
        }
        Assert.True(peakY < 1.1f, $"floated up while walking into the trunk (peak y={peakY})");
        Assert.True(state.Position.Z < 1.7f, $"walked through the trunk (z={state.Position.Z})");

        // Pressed against the trunk -> strafe +X: must SLIDE around it, not be frozen.
        float xBefore = state.Position.X;
        var strafe = new MoveCommand(new Vector2(1f, 0f), run: false, cameraYaw: 0f, jump: false);   // +X
        for (int i = 0; i < 90; i++)
            state = CharacterMovement.Step(state, strafe, 1f / 60f, Flat, Tuning, groundNormal: null, world: world);
        Assert.True(state.Position.X > xBefore + 0.5f, $"frozen against the trunk - could not strafe (x {xBefore:F2} -> {state.Position.X:F2})");

        // And can walk back away (-Z).
        float zBefore = state.Position.Z;
        var away = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: 0f, jump: false);   // -Z
        for (int i = 0; i < 60; i++)
            state = CharacterMovement.Step(state, away, 1f / 60f, Flat, Tuning, groundNormal: null, world: world);
        Assert.True(state.Position.Z < zBefore - 0.5f, $"could not walk away from the trunk (z {zBefore:F2} -> {state.Position.Z:F2})");
    }

    [Fact]
    public void FastPath_IsDeterministic_AcrossTwoWorlds()
    {
        static MoveState RunOnce()
        {
            IPhysicsWorld world = new BepuPhysicsWorld();
            world.AddStatic(ThinWallAtZ2(), Pose.At(Vector3.Zero));
            world.Step(1f / 60f);
            var s = new MoveState { Position = new Vector3(0.13f, 0.9f, 0f), Grounded = true };
            var cmd = new MoveCommand(new Vector2(0.3f, -1f), run: true, cameraYaw: 0.2f, jump: false);
            for (int i = 0; i < 200; i++)
                s = CharacterMovement.Step(s, cmd, 1f / 60f, Flat, Tuning, groundNormal: null, world: world);
            world.Dispose();
            return s;
        }

        MoveState a = RunOnce(), b = RunOnce();
        Assert.Equal(BitConverter.SingleToInt32Bits(a.Position.X), BitConverter.SingleToInt32Bits(b.Position.X));
        Assert.Equal(BitConverter.SingleToInt32Bits(a.Position.Y), BitConverter.SingleToInt32Bits(b.Position.Y));
        Assert.Equal(BitConverter.SingleToInt32Bits(a.Position.Z), BitConverter.SingleToInt32Bits(b.Position.Z));
    }
}
