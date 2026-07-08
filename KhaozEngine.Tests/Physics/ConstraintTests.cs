using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

/// <summary>Headless, fixed-dt tests for joint constraints through the <see cref="IPhysicsWorld"/> seam: a hinge
/// pendulum swings and settles, a distance joint holds a falling body at rope length, a weld pair moves rigidly,
/// a slider stays on its axis, hinge limits clamp the swing, removing a constrained body cleans its constraints,
/// and two identical worlds with active constraints step bit-identically. Rest states are read from the body
/// pose after a fixed number of steps (no wall clock, no real device).</summary>
public class ConstraintTests
{
    const float Dt = 1f / 60f;

    static void StepMany(IPhysicsWorld world, int steps)
    {
        for (int i = 0; i < steps; i++) world.Step(Dt);
    }

    static BoxShape SmallBox => new(new Vector3(0.25f, 0.25f, 0.25f));

    // ---------------------------------------------------------------------
    // Hinge pendulum: a body hung by a hinge from a world anchor swings under gravity, confined to the hinge
    // plane, with the arm length (and so the pivot pin) held constant. A frictionless hinge conserves energy, so
    // this asserts the SWING (it reaches far below the start and passes below the pivot), not a settled rest;
    // damped settling is Task 2's motor/friction territory.
    // ---------------------------------------------------------------------

    [Fact]
    public void HingePendulum_SwingsUnderGravity_ConfinedToTheHingePlane_PinnedAtThePivot()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        // Pivot at (0,5,0). A 1 kg box hung 1.5 m to the +X side, hinge axis = Z so it swings in the X-Y plane.
        var pivot = new Vector3(0f, 5f, 0f);
        var bodyStart = pivot + new Vector3(1.5f, 0f, 0f);
        DynamicBodyHandle bob = world.AddDynamic(SmallBox, Pose.At(bodyStart),
            new DynamicBodyDescription(1f) { SleepThreshold = 0f });

        // Anchor point is on the body at local (-1.5, 0, 0) so it coincides with the pivot; hinge axis Z on both.
        var hinge = ConstraintDescription.HingeJoint(
            ConstraintAttachment.OnBody(bob),
            ConstraintAttachment.AtWorld(pivot),
            anchorA: new Vector3(-1.5f, 0f, 0f),
            anchorB: Vector3.Zero,
            axisA: Vector3.UnitZ,
            axisB: Vector3.UnitZ);
        world.AddConstraint(hinge);

        float startY = world.GetDynamicPose(bob).Position.Y;
        float lowestY = startY;
        float highestY = startY;
        float maxArmError = 0f, maxZ = 0f, maxPinError = 0f;
        for (int i = 0; i < 600; i++) // 10 s of free swinging
        {
            world.Step(Dt);
            Pose p = world.GetDynamicPose(bob);
            Vector3 arm = p.Position - pivot;
            lowestY = MathF.Min(lowestY, p.Position.Y);
            highestY = MathF.Max(highestY, p.Position.Y);
            maxArmError = MathF.Max(maxArmError, MathF.Abs(arm.Length() - 1.5f)); // arm length held (rigid link)
            maxZ = MathF.Max(maxZ, MathF.Abs(p.Position.Z));                      // confined to the X-Y hinge plane
            // The body-local (-1.5,0,0) anchor point must stay pinned to the pivot the whole time.
            Vector3 anchorWorld = p.Position + Vector3.Transform(new Vector3(-1.5f, 0f, 0f), p.Orientation);
            maxPinError = MathF.Max(maxPinError, Vector3.Distance(anchorWorld, pivot));
        }

        // It swings: the bob passes well below the pivot (a stuck / rigid joint would stay near the start height).
        Assert.True(lowestY < pivot.Y - 1.3f, $"pendulum must swing down past the pivot (lowest y {lowestY:F3}, pivot {pivot.Y})");
        // Energy is conserved, NOT gained: the bob starts level with the pivot at rest, so it must never rise above
        // its start height across the whole 10 s run (a small band for solver compliance). A slow solver energy gain
        // would let the swing climb past the horizontal launch on later cycles - this catches it.
        Assert.True(highestY < startY + 0.05f, $"pendulum must not gain energy and rise above its start height (highest y {highestY:F3}, start {startY:F3})");
        // The pivot pin holds and the arm stays rigid (a hinge is a point pin + axis, not a stretchy spring).
        Assert.True(maxArmError < 0.05f, $"hinge arm length must stay ~1.5 m (rigid link), worst error {maxArmError:F4}");
        Assert.True(maxPinError < 0.1f, $"hinge anchor must stay pinned to the pivot, worst off by {maxPinError:F4}");
        // The Z hinge axis keeps the swing in the X-Y plane.
        Assert.True(maxZ < 0.05f, $"hinge axis Z must confine the swing to the X-Y plane, worst Z {maxZ:F4}");
    }

    // ---------------------------------------------------------------------
    // Distance joint: a body hangs at rope length below a world anchor, not falling through.
    // ---------------------------------------------------------------------

    [Fact]
    public void DistanceJoint_HoldsAFallingBodyAtRopeLength()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        var anchor = new Vector3(0f, 10f, 0f);
        const float ropeLength = 3f;
        // A 2 kg body starting just below the anchor. Rope: min 0, max 3. It falls until the rope goes taut at 3 m.
        DynamicBodyHandle bob = world.AddDynamic(SmallBox, Pose.At(anchor + new Vector3(0f, -0.5f, 0f)),
            new DynamicBodyDescription(2f) { SleepThreshold = 0f });

        var rope = ConstraintDescription.DistanceJoint(
            ConstraintAttachment.AtWorld(anchor),
            ConstraintAttachment.OnBody(bob),
            anchorA: Vector3.Zero,
            anchorB: Vector3.Zero,
            minDistance: 0f,
            maxDistance: ropeLength);
        world.AddConstraint(rope);

        StepMany(world, 600); // 10 s: fall and hang taut

        float dist = Vector3.Distance(world.GetDynamicPose(bob).Position, anchor);
        // The body hangs at ~rope length: it fell (dist grew past 0.5) but the rope caught it (dist ~ 3, in a band).
        Assert.True(dist > 2.7f && dist < 3.3f, $"body must hang at ~rope length ({ropeLength} m), distance was {dist:F3}");
        // It hangs essentially straight down.
        Assert.True(world.GetDynamicPose(bob).Position.Y < anchor.Y - 2.5f, "body must have fallen to the end of the rope");
    }

    [Fact]
    public void DistanceJoint_MinEqualsMax_IsARigidRod()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        var anchor = new Vector3(0f, 10f, 0f);
        const float rod = 2f;
        DynamicBodyHandle bob = world.AddDynamic(SmallBox, Pose.At(anchor + new Vector3(0f, -rod, 0f)),
            new DynamicBodyDescription(1f) { SleepThreshold = 0f });
        world.AddConstraint(ConstraintDescription.DistanceJoint(
            ConstraintAttachment.AtWorld(anchor), ConstraintAttachment.OnBody(bob),
            Vector3.Zero, Vector3.Zero, rod, rod));

        StepMany(world, 300);
        float dist = Vector3.Distance(world.GetDynamicPose(bob).Position, anchor);
        Assert.True(MathF.Abs(dist - rod) < 0.15f, $"rigid rod (min==max) must hold the body at exactly {rod} m, was {dist:F3}");
    }

    // ---------------------------------------------------------------------
    // Weld: two bodies move rigidly - their relative pose stays constant over many steps.
    // ---------------------------------------------------------------------

    [Fact]
    public void WeldJoint_KeepsTheRelativePoseConstant_OverManySteps()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        // Two boxes side by side in free fall (no ground), welded. Their relative offset must stay ~constant as
        // they fall together and tumble (a small initial spin makes orientation evolve).
        var a = world.AddDynamic(SmallBox, Pose.At(new Vector3(0f, 5f, 0f)),
            new DynamicBodyDescription(1f) { AngularVelocity = new Vector3(0.5f, 0.3f, 0f), SleepThreshold = 0f });
        var b = world.AddDynamic(SmallBox, Pose.At(new Vector3(0.6f, 5f, 0f)),
            new DynamicBodyDescription(1f) { SleepThreshold = 0f });
        world.AddConstraint(ConstraintDescription.WeldJoint(
            ConstraintAttachment.OnBody(a), ConstraintAttachment.OnBody(b), Vector3.Zero));

        // Relative offset of B in A's frame at the start (should stay constant).
        static Vector3 RelOffset(IPhysicsWorld w, DynamicBodyHandle a, DynamicBodyHandle b)
        {
            Pose pa = w.GetDynamicPose(a), pb = w.GetDynamicPose(b);
            return Vector3.Transform(pb.Position - pa.Position, Quaternion.Conjugate(pa.Orientation));
        }

        StepMany(world, 5); // let the weld settle its initial error
        Vector3 rel0 = RelOffset(world, a, b);
        for (int i = 0; i < 200; i++)
        {
            world.Step(Dt);
            Vector3 rel = RelOffset(world, a, b);
            Assert.True(Vector3.Distance(rel, rel0) < 0.05f,
                $"welded bodies must hold a constant relative pose (drift {Vector3.Distance(rel, rel0):F4} at step {i})");
        }
    }

    // ---------------------------------------------------------------------
    // Slider: a body pushed sideways stays on its axis; travel is clamped by the limits.
    // ---------------------------------------------------------------------

    [Fact]
    public void SliderJoint_StaysOnItsAxis_UnderLateralPush_AndClampsTravel()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld(Vector3.Zero); // no gravity: isolate the slider behaviour
        var anchor = new Vector3(0f, 0f, 0f);
        // Slider along X, travel clamped to [-1, 1] m. The body starts at the anchor and is shoved along +Y and +Z
        // (off-axis) and +X (past the limit). It must stay on the X line (Y,Z ~ 0) and stop at the +X limit.
        // Start the body at a NON-identity orientation (0.6 rad about an oblique axis): the angular servo must lock
        // the relative rotation captured at add time, i.e. THIS starting rotation, not identity. If the servo
        // wrongly captured identity (or reset the target), the body would rotate toward identity and the hold-
        // orientation assertion below would fail - a plain identity start could not tell the two apart.
        Quaternion startOrient = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(
            Vector3.Normalize(new Vector3(0.3f, 1f, 0.5f)), 0.6f));
        DynamicBodyHandle slider = world.AddDynamic(SmallBox, new Pose(anchor, startOrient),
            new DynamicBodyDescription(1f) { SleepThreshold = 0f });
        world.AddConstraint(ConstraintDescription.SliderJoint(
            ConstraintAttachment.AtWorld(anchor), ConstraintAttachment.OnBody(slider),
            anchorA: Vector3.Zero, anchorB: Vector3.Zero, axis: Vector3.UnitX,
            minOffset: -1f, maxOffset: 1f));

        // Shove hard off-axis and along the axis past the limit, AND apply a spin so the angular lock has real work
        // to do. Track the extremes over the run: the axis has no damping so a body driven into the +1 stop rebounds
        // and oscillates, but it must never leave the axis, never travel past the limit, and never rotate away from
        // its captured start orientation.
        world.SetDynamicVelocity(slider, new Vector3(5f, 4f, 3f), new Vector3(3f, 2f, 4f));
        float maxX = 0f, maxOffAxis = 0f, worstOrientDot = 1f;
        for (int i = 0; i < 300; i++) // 5 s
        {
            world.Step(Dt);
            Pose sp = world.GetDynamicPose(slider);
            maxX = MathF.Max(maxX, sp.Position.X);
            maxOffAxis = MathF.Max(maxOffAxis, MathF.Sqrt(sp.Position.Y * sp.Position.Y + sp.Position.Z * sp.Position.Z));
            // Closeness of the current orientation to the captured start orientation: |dot| = 1 when identical,
            // falls off as it rotates away. (|dot| handles the q/-q double cover.)
            worstOrientDot = MathF.Min(worstOrientDot, MathF.Abs(Quaternion.Dot(sp.Orientation, startOrient)));
        }

        // Stayed on the X axis: the off-axis push (Y,Z) never moved it off the line.
        Assert.True(maxOffAxis < 0.05f, $"slider must stay on its axis, worst off-axis distance was {maxOffAxis:F4}");
        // Travel clamped: X reached the +1 m limit (proving it slid freely to there) but not far past it (5 m/s for
        // 5 s unclamped would be +25 m).
        Assert.True(maxX > 0.9f, $"slider must slide to its +1 m limit, max X reached was {maxX:F3}");
        Assert.True(maxX < 1.15f, $"slider travel must clamp at the +1 m limit, max X was {maxX:F3}");
        // Rotation locked the whole time to the NON-identity captured start pose (a wrong captured target would let
        // it drift toward identity and drop this dot). |dot| stays ~1 = held at the start orientation.
        Assert.True(worstOrientDot > 0.99f, $"slider must lock rotation to its captured start pose, worst orientation dot was {worstOrientDot:F4}");
    }

    // ---------------------------------------------------------------------
    // Hinge limits clamp the swing: a driven hinge never exceeds its angular limits.
    // ---------------------------------------------------------------------

    [Fact]
    public void HingeWithLimits_NeverExceedsThemAcrossTheSwing()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        var pivot = new Vector3(0f, 5f, 0f);
        var bodyStart = pivot + new Vector3(1.5f, 0f, 0f); // horizontal, this is the 0-angle rest pose
        DynamicBodyHandle bob = world.AddDynamic(SmallBox, Pose.At(bodyStart),
            new DynamicBodyDescription(1f) { SleepThreshold = 0f });

        // Hinge about Z with a limit of [-0.5, +0.5] rad. Gravity pulls the bob DOWN (which would swing it far past
        // -0.5 rad without the limit), so the swing must stop at ~ -0.5 rad: the anchor arm never drops below the
        // angle limit. Measure the arm angle from +X in the X-Y plane.
        var hinge = ConstraintDescription.HingeJoint(
            ConstraintAttachment.OnBody(bob), ConstraintAttachment.AtWorld(pivot),
            anchorA: new Vector3(-1.5f, 0f, 0f), anchorB: Vector3.Zero,
            axisA: Vector3.UnitZ, axisB: Vector3.UnitZ)
            .WithAngularLimit(-0.5f, 0.5f);
        world.AddConstraint(hinge);

        float worstAngle = 0f;
        for (int i = 0; i < 600; i++)
        {
            world.Step(Dt);
            Vector3 arm = world.GetDynamicPose(bob).Position - pivot; // pivot -> bob, ~1.5 m long
            float angle = MathF.Atan2(arm.Y, arm.X); // 0 = horizontal +X, negative = swung down
            if (angle < worstAngle) worstAngle = angle;
            // Never past the -0.5 rad limit (a small band for spring compliance).
            Assert.True(angle > -0.5f - 0.15f, $"hinge must not swing past its -0.5 rad limit, angle was {angle:F3} at step {i}");
        }
        // And it actually reached the limit (the limit is doing work, not vacuously true because it never swung).
        Assert.True(worstAngle < -0.3f, $"hinge should have swung down to near its limit, worst angle was {worstAngle:F3}");
    }

    // ---------------------------------------------------------------------
    // Lifecycle: removing a constrained body cleans its constraints (no crash, no dangling solver ref).
    // ---------------------------------------------------------------------

    [Fact]
    public void RemovingAConstrainedBody_CleansItsConstraints_AndSteppingStaysSafe()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        var a = world.AddDynamic(SmallBox, Pose.At(new Vector3(0f, 5f, 0f)), DynamicBodyDescription.WithMass(1f));
        var b = world.AddDynamic(SmallBox, Pose.At(new Vector3(0.6f, 5f, 0f)), DynamicBodyDescription.WithMass(1f));
        ConstraintHandle weld = world.AddConstraint(ConstraintDescription.WeldJoint(
            ConstraintAttachment.OnBody(a), ConstraintAttachment.OnBody(b), Vector3.Zero));

        StepMany(world, 30);
        // Remove body A: its weld to B must be torn down automatically (Bepu would corrupt if the body were removed
        // with a live constraint). Stepping afterwards must not throw.
        world.RemoveDynamic(a);
        StepMany(world, 60);

        // B survives and keeps falling.
        Assert.True(world.GetDynamicPose(b).Position.Y < 5f, "surviving body must keep simulating after its partner was removed");
        // The constraint handle is now stale; removing it is a safe no-op (the body removal already cleaned it).
        world.RemoveConstraint(weld);
        world.RemoveConstraint(weld); // double-remove: still a no-op
    }

    [Fact]
    public void RemoveConstraint_MidSim_IsSafe_AndDoubleRemoveIsANoOp()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        var anchor = new Vector3(0f, 10f, 0f);
        DynamicBodyHandle bob = world.AddDynamic(SmallBox, Pose.At(anchor + new Vector3(0f, -2f, 0f)),
            new DynamicBodyDescription(1f) { SleepThreshold = 0f });
        ConstraintHandle rope = world.AddConstraint(ConstraintDescription.DistanceJoint(
            ConstraintAttachment.AtWorld(anchor), ConstraintAttachment.OnBody(bob),
            Vector3.Zero, Vector3.Zero, 0f, 2f));

        StepMany(world, 120);
        float heldY = world.GetDynamicPose(bob).Position.Y;
        Assert.True(heldY > anchor.Y - 2.5f, "body should be held by the rope before removal");

        // Remove the constraint mid-sim: the body must now fall freely (the rope no longer holds it).
        world.RemoveConstraint(rope);
        world.RemoveConstraint(rope); // double-remove no-op
        StepMany(world, 120);
        Assert.True(world.GetDynamicPose(bob).Position.Y < heldY - 1f,
            "after the rope is removed the body must fall past the old rope length");
    }

    [Fact]
    public void AddConstraint_WithStaleBodyHandle_Throws()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        var a = world.AddDynamic(SmallBox, Pose.At(new Vector3(0f, 5f, 0f)), DynamicBodyDescription.WithMass(1f));
        world.RemoveDynamic(a); // a is now stale
        Assert.Throws<ArgumentException>(() => world.AddConstraint(ConstraintDescription.DistanceJoint(
            ConstraintAttachment.OnBody(a), ConstraintAttachment.AtWorld(Vector3.Zero),
            Vector3.Zero, Vector3.Zero, 0f, 1f)));
    }

    [Fact]
    public void AddConstraint_BothEndsWorldAnchors_Throws()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        Assert.Throws<ArgumentException>(() => world.AddConstraint(ConstraintDescription.DistanceJoint(
            ConstraintAttachment.AtWorld(Vector3.Zero), ConstraintAttachment.AtWorld(new Vector3(1f, 0f, 0f)),
            Vector3.Zero, Vector3.Zero, 0f, 1f)));
    }

    // ---------------------------------------------------------------------
    // Determinism: two identical worlds with active constraints step bit-identically.
    // ---------------------------------------------------------------------

    [Fact]
    public void TwoIdenticalWorlds_WithActiveConstraints_ProduceIdenticalPoses()
    {
        static (Vector3 pos, Quaternion orient) Run()
        {
            using IPhysicsWorld world = new BepuPhysicsWorld();
            var pivot = new Vector3(0f, 5f, 0f);
            // A hinge pendulum plus a distance-jointed second body: two live constraints and a kinematic anchor
            // each, exercising the full constraint solve path in the determinism fingerprint.
            var bob = world.AddDynamic(SmallBox, Pose.At(pivot + new Vector3(1.5f, 0f, 0.2f)),
                new DynamicBodyDescription(1f) { AngularVelocity = new Vector3(0.3f, 0f, 0.4f), SleepThreshold = 0f });
            world.AddConstraint(ConstraintDescription.HingeJoint(
                ConstraintAttachment.OnBody(bob), ConstraintAttachment.AtWorld(pivot),
                new Vector3(-1.5f, 0f, 0f), Vector3.Zero, Vector3.UnitZ, Vector3.UnitZ));

            var hung = world.AddDynamic(SmallBox, Pose.At(new Vector3(3f, 8f, 0f)),
                new DynamicBodyDescription(2f) { SleepThreshold = 0f });
            world.AddConstraint(ConstraintDescription.DistanceJoint(
                ConstraintAttachment.AtWorld(new Vector3(3f, 10f, 0f)), ConstraintAttachment.OnBody(hung),
                Vector3.Zero, Vector3.Zero, 0f, 2f));

            StepMany(world, 200);
            return (world.GetDynamicPose(bob).Position, world.GetDynamicPose(bob).Orientation);
        }

        var a = Run();
        var b = Run();
        Assert.Equal(a.pos.X, b.pos.X);
        Assert.Equal(a.pos.Y, b.pos.Y);
        Assert.Equal(a.pos.Z, b.pos.Z);
        Assert.Equal(a.orient.X, b.orient.X);
        Assert.Equal(a.orient.Y, b.orient.Y);
        Assert.Equal(a.orient.Z, b.orient.Z);
        Assert.Equal(a.orient.W, b.orient.W);
    }

    // ---------------------------------------------------------------------
    // Dynamic-to-dynamic ball socket: two free bodies pinned at a shared point stay pinned.
    // ---------------------------------------------------------------------

    [Fact]
    public void BallSocket_TwoDynamicBodies_StayPinnedAtTheSharedPoint()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld(Vector3.Zero); // no gravity: isolate the pin
        var a = world.AddDynamic(SmallBox, Pose.At(new Vector3(0f, 0f, 0f)),
            new DynamicBodyDescription(1f) { SleepThreshold = 0f });
        var b = world.AddDynamic(SmallBox, Pose.At(new Vector3(1f, 0f, 0f)),
            new DynamicBodyDescription(1f) { SleepThreshold = 0f });
        // Pin A's +0.5X face point to B's -0.5X face point: the two anchor points should coincide.
        world.AddConstraint(ConstraintDescription.BallSocketJoint(
            ConstraintAttachment.OnBody(a), ConstraintAttachment.OnBody(b),
            anchorA: new Vector3(0.5f, 0f, 0f), anchorB: new Vector3(-0.5f, 0f, 0f)));

        // Yank B apart; the ball socket must reel the anchors back together.
        world.SetDynamicVelocity(b, new Vector3(3f, 2f, 0f), Vector3.Zero);
        StepMany(world, 300);

        Pose pa = world.GetDynamicPose(a), pb = world.GetDynamicPose(b);
        Vector3 anchorAWorld = pa.Position + Vector3.Transform(new Vector3(0.5f, 0f, 0f), pa.Orientation);
        Vector3 anchorBWorld = pb.Position + Vector3.Transform(new Vector3(-0.5f, 0f, 0f), pb.Orientation);
        Assert.True(Vector3.Distance(anchorAWorld, anchorBWorld) < 0.1f,
            $"ball-socket anchors must stay coincident, off by {Vector3.Distance(anchorAWorld, anchorBWorld):F3}");
    }

    // ---------------------------------------------------------------------
    // World-anchor visibility: the shapeless kinematic body a world anchor is realised as must be invisible to
    // every query (ray, sweep, dynamics-only ray), regardless of QueryMobility, so a character walking through a
    // world-anchored hinge/rope pivot never collides with an invisible sphere at the pivot. A shape-bearing
    // anchor would be a QueryMobility.All / Dynamics hit (kinematic counts as non-static) and stall the character.
    // ---------------------------------------------------------------------

    // Builds a world with a single world-anchored hinge whose pivot is at `pivot`, plus the dynamic bob it holds.
    // The anchor is the shapeless kinematic body created for the AtWorld end; the queries below must not see it.
    static (IPhysicsWorld world, DynamicBodyHandle bob) HingeAnchoredAt(Vector3 pivot)
    {
        IPhysicsWorld world = new BepuPhysicsWorld();
        DynamicBodyHandle bob = world.AddDynamic(SmallBox, Pose.At(pivot + new Vector3(1.5f, 0f, 0f)),
            new DynamicBodyDescription(1f) { SleepThreshold = 0f });
        world.AddConstraint(ConstraintDescription.HingeJoint(
            ConstraintAttachment.OnBody(bob), ConstraintAttachment.AtWorld(pivot),
            anchorA: new Vector3(-1.5f, 0f, 0f), anchorB: Vector3.Zero,
            axisA: Vector3.UnitZ, axisB: Vector3.UnitZ));
        return (world, bob);
    }

    [Fact]
    public void Raycast_ThroughAWorldAnchorPivot_Misses()
    {
        var pivot = new Vector3(0f, 5f, 0f);
        (IPhysicsWorld world, _) = HingeAnchoredAt(pivot);
        using (world)
        {
            // Ray straight through the pivot point along +X (default filter = QueryMobility.All). The bob sits at
            // x=+1.5 (a SmallBox 0.25 half-extent, so its near face is ~1.25); aim the ray to arrive AT the pivot
            // and stop before the bob, so any hit within 1.0 m can only be the anchor.
            bool hit = world.Raycast(new Vector3(-5f, 5f, 0f), Vector3.UnitX, 6f, out RayHit rh);
            // A hit at the pivot (distance ~5) would be the invisible anchor; the bob is farther (~6.25).
            Assert.False(hit && rh.Distance < 5.5f,
                $"default raycast must not hit the shapeless world-anchor pivot (hit={hit}, dist={(hit ? rh.Distance : -1f):F3})");
        }
    }

    [Fact]
    public void CapsuleSweep_ThroughAWorldAnchorPivot_Misses()
    {
        var pivot = new Vector3(0f, 5f, 0f);
        (IPhysicsWorld world, _) = HingeAnchoredAt(pivot);
        using (world)
        {
            // Sweep a small capsule straight through the pivot along +X (default filter). It must reach the pivot
            // region (distance ~5) with no hit; the bob is farther out at x~+1.5.
            var capsule = new CapsuleShape(0.2f, 0.4f);
            bool hit = world.SweepCapsule(capsule, Pose.At(new Vector3(-5f, 5f, 0f)), Vector3.UnitX, 5.4f, out SweepHit sh);
            Assert.False(hit && sh.Distance < 5.5f,
                $"default capsule sweep must not hit the shapeless world-anchor pivot (hit={hit}, dist={(hit ? sh.Distance : -1f):F3})");
        }
    }

    [Fact]
    public void DynamicsOnlyRaycast_ThroughAWorldAnchorPivot_Misses()
    {
        var pivot = new Vector3(0f, 5f, 0f);
        (IPhysicsWorld world, _) = HingeAnchoredAt(pivot);
        using (world)
        {
            // The anchor is a KINEMATIC body, so a QueryMobility.Dynamics ray (which accepts non-statics) is the
            // exact filter that a shape-bearing anchor would be caught by. Shapeless keeps it out of the broadphase,
            // so even this filter misses it at the pivot.
            var dynamicsOnly = new QueryFilter(QueryMobility.Dynamics);
            bool hit = world.Raycast(new Vector3(-5f, 5f, 0f), Vector3.UnitX, 5.4f, out RayHit rh, dynamicsOnly);
            Assert.False(hit && rh.Distance < 5.5f,
                $"dynamics-only raycast must not hit the shapeless world-anchor pivot (hit={hit}, dist={(hit ? rh.Distance : -1f):F3})");
        }
    }

    [Fact]
    public void CharacterMovement_WalksThroughAWorldAnchoredHingePivot_WithoutStalling()
    {
        // A world-anchored hinge pivot at ground level, directly in the character's path. With a shape-bearing
        // anchor the character's collide-and-slide sweep (default filter) would snag on the invisible sphere at the
        // pivot and stall; with a shapeless anchor the character walks straight through it. Flat ground at y=0.
        // The pivot sits at x=3, well ahead of where the swinging bob (a real dynamic body, ~x=4.5) can reach, so
        // reaching past the pivot proves the invisible anchor did not stop the character (the bob legitimately
        // blocks farther on and is not part of this assertion).
        const float halfH = 0.9f;
        var pivot = new Vector3(3f, halfH, 0f); // on the character's line of travel, at capsule-centre height
        (IPhysicsWorld world, _) = HingeAnchoredAt(pivot);
        using (world)
        {
            var tuning = MoveTuning.Default;
            float GroundHeight(float x, float z) => 0f;

            // Start behind the pivot, walk in +X straight through it. CameraYaw 0 => forward is -Z, right is +X, so
            // Move.X = 1 drives +X.
            var state = new MoveState { Position = new Vector3(0f, halfH, 0f), Grounded = true };
            var cmd = new MoveCommand(new Vector2(1f, 0f), run: false, cameraYaw: 0f);

            float lastX = state.Position.X;
            bool crossedPivot = false;
            for (int i = 0; i < 300; i++) // 5 s at walk speed 3 m/s
            {
                MoveState next = CharacterMovement.Step(state, cmd, Dt, GroundHeight, tuning, world: world);
                // Position must advance monotonically in X (never stall or reverse at the pivot).
                Assert.True(next.Position.X >= lastX - 1e-4f,
                    $"character must not stall/reverse at the world-anchor pivot (x {next.Position.X:F3} < prev {lastX:F3} at step {i})");
                lastX = next.Position.X;
                if (next.Position.X > pivot.X + 0.5f) crossedPivot = true;
                state = next;
            }
            Assert.True(crossedPivot, $"character must walk past the pivot at x={pivot.X}, reached x={lastX:F3}");
        }
    }

    // =====================================================================
    // Task 2: motors and servos. A hinge servo reaches and holds a target angle without oscillation blowup; a hinge
    // velocity motor spins until an angular limit clamps it; a slider position servo lifts a platform and holds it
    // against gravity; a distance winch reels a hanging body up as its target length shrinks; and two identical
    // worlds with an active servo step bit-identically. (The allocation-free retarget test lives in its own
    // non-parallel collection below.)
    // =====================================================================

    // ---------------------------------------------------------------------
    // Hinge angle servo: drives the hinge to a target angle and holds it, no oscillation blowup.
    // ---------------------------------------------------------------------

    [Fact]
    public void HingeServo_ReachesAndHoldsTheTargetAngle_WithoutOscillationBlowup()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld(Vector3.Zero); // no gravity: isolate the servo hold
        var pivot = new Vector3(0f, 5f, 0f);
        // A 1 kg arm hinged at the pivot, extending +X, hinge axis Z (swings in the X-Y plane). The servo drives
        // the hinge to +0.8 rad and must reach and hold it (measured as the arm angle from +X), no wild spin.
        var bodyStart = pivot + new Vector3(1.5f, 0f, 0f);
        DynamicBodyHandle arm = world.AddDynamic(SmallBox, Pose.At(bodyStart),
            new DynamicBodyDescription(1f) { SleepThreshold = 0f });

        const float targetAngle = 0.8f;
        var hinge = ConstraintDescription.HingeJoint(
                ConstraintAttachment.AtWorld(pivot), ConstraintAttachment.OnBody(arm),
                anchorA: Vector3.Zero, anchorB: new Vector3(-1.5f, 0f, 0f),
                axisA: Vector3.UnitZ, axisB: Vector3.UnitZ)
            .WithHingeServo(targetAngle, maxSpeed: 3f);
        world.AddConstraint(hinge);

        float ArmAngle()
        {
            Vector3 armVec = world.GetDynamicPose(arm).Position - pivot;
            return MathF.Atan2(armVec.Y, armVec.X); // 0 = +X, positive = swung up
        }

        StepMany(world, 300); // 5 s to converge
        float settled = ArmAngle();
        // Reached the target angle (a small band for spring compliance). The sign of the reached angle tracks the
        // hinge/servo A-B convention; assert the magnitude landed on the commanded 0.8 rad.
        Assert.True(MathF.Abs(MathF.Abs(settled) - targetAngle) < 0.1f,
            $"hinge servo must reach the {targetAngle} rad target, settled at {settled:F3}");

        // Hold without oscillation blowup: over the next 5 s the angle must stay within a tight band of where it
        // settled (a diverging/ringing servo would widen this).
        float minA = settled, maxA = settled;
        for (int i = 0; i < 300; i++)
        {
            world.Step(Dt);
            float a = ArmAngle();
            minA = MathF.Min(minA, a);
            maxA = MathF.Max(maxA, a);
        }
        Assert.True(maxA - minA < 0.05f,
            $"hinge servo must hold steady (no oscillation blowup), angle band was {maxA - minA:F4} rad");
    }

    // ---------------------------------------------------------------------
    // Hinge velocity motor: spins the hinge at a target angular velocity until an angular limit clamps it.
    // ---------------------------------------------------------------------

    [Fact]
    public void HingeMotor_SpinsAtTargetVelocity_UntilLimitsClampIt()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld(Vector3.Zero); // no gravity: isolate the motor
        var pivot = new Vector3(0f, 5f, 0f);
        var bodyStart = pivot + new Vector3(1.5f, 0f, 0f); // 0-angle rest pose (horizontal +X)
        DynamicBodyHandle arm = world.AddDynamic(SmallBox, Pose.At(bodyStart),
            new DynamicBodyDescription(1f) { SleepThreshold = 0f });

        // Motor drives the hinge at 2 rad/s about Z; an angular limit of [-1, 0] rad clamps it at one end. The motor
        // target's sign is right-handed about the hinge axis with end A as the reference (here A is the world anchor,
        // axis +Z), so a positive target swings the arm DOWN toward -Z-handed angles; the -1 rad limit stops it. The
        // test cares that the motor spins the joint and the limit clamps it, not which sign is "up".
        var hinge = ConstraintDescription.HingeJoint(
                ConstraintAttachment.AtWorld(pivot), ConstraintAttachment.OnBody(arm),
                anchorA: Vector3.Zero, anchorB: new Vector3(-1.5f, 0f, 0f),
                axisA: Vector3.UnitZ, axisB: Vector3.UnitZ)
            .WithAngularLimit(-1f, 0f)
            .WithHingeMotor(2f, maxTorque: 200f); // a capped motor eases into the end-stop instead of slamming through it
        world.AddConstraint(hinge);

        float ArmAngle()
        {
            Vector3 armVec = world.GetDynamicPose(arm).Position - pivot;
            return MathF.Atan2(armVec.Y, armVec.X);
        }

        // Early on the motor is spinning it (the arm has moved off 0 within the first second).
        StepMany(world, 30);
        Assert.True(MathF.Abs(ArmAngle()) > 0.1f, "hinge motor must start spinning the arm off its rest angle");

        // After long enough the limit clamps it: the angle never exceeds |1| rad by much and it holds near the stop.
        float worstMag = 0f;
        for (int i = 0; i < 400; i++)
        {
            world.Step(Dt);
            worstMag = MathF.Max(worstMag, MathF.Abs(ArmAngle()));
        }
        Assert.True(worstMag > 0.85f, $"motor must drive the hinge to its 1 rad limit, worst reached {worstMag:F3}");
        Assert.True(worstMag < 1.2f, $"the angular limit must clamp the motor at ~1 rad, worst was {worstMag:F3}");
    }

    // ---------------------------------------------------------------------
    // Slider position servo: lifts a platform to a target offset and holds it against gravity.
    // ---------------------------------------------------------------------

    [Fact]
    public void SliderServo_MovesAPlatformToTarget_AndHoldsAgainstGravity()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld(); // gravity ON: the servo must hold the platform up
        var anchor = new Vector3(0f, 0f, 0f);
        // A 2 kg platform on a vertical (Y) slider from a world anchor at the origin. The servo commands offset +2 m
        // (up) and must lift it there against gravity and hold, force cap high enough to carry the weight.
        DynamicBodyHandle platform = world.AddDynamic(SmallBox, Pose.At(anchor),
            new DynamicBodyDescription(2f) { SleepThreshold = 0f });

        const float targetOffset = 2f;
        var slider = ConstraintDescription.SliderJoint(
                ConstraintAttachment.AtWorld(anchor), ConstraintAttachment.OnBody(platform),
                anchorA: Vector3.Zero, anchorB: Vector3.Zero, axis: Vector3.UnitY,
                minOffset: 0f, maxOffset: 3f)
            .WithSliderServo(targetOffset, maxSpeed: 2f, maxForce: 200f);
        world.AddConstraint(slider);

        StepMany(world, 400); // ~6.7 s: rise and settle
        float settledY = world.GetDynamicPose(platform).Position.Y;
        Assert.True(MathF.Abs(settledY - targetOffset) < 0.1f,
            $"slider servo must lift the platform to the {targetOffset} m target, settled at Y={settledY:F3}");

        // Hold against gravity: over the next few seconds it must not sag away from the target.
        float minY = settledY, maxY = settledY;
        for (int i = 0; i < 300; i++)
        {
            world.Step(Dt);
            float y = world.GetDynamicPose(platform).Position.Y;
            minY = MathF.Min(minY, y);
            maxY = MathF.Max(maxY, y);
        }
        Assert.True(maxY - minY < 0.05f, $"slider servo must hold the platform steady against gravity, band {maxY - minY:F4} m");
        Assert.True(minY > targetOffset - 0.1f, $"slider servo must not sag under gravity, lowest Y {minY:F3}");
    }

    // ---------------------------------------------------------------------
    // Distance winch: a hanging body reels UP as the servo's target length shrinks over time.
    // ---------------------------------------------------------------------

    [Fact]
    public void Winch_ReelsAHangingBodyUpward_OverTime()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld(); // gravity ON: the body hangs, the winch pulls it up
        var anchor = new Vector3(0f, 10f, 0f);
        // A 1 kg body hanging 4 m below the anchor on a distance servo (winch). Shrinking the target length reels it
        // up toward the anchor. Start the servo at the current 4 m so it holds, then reel in to 1 m over the run.
        DynamicBodyHandle load = world.AddDynamic(SmallBox, Pose.At(anchor + new Vector3(0f, -4f, 0f)),
            new DynamicBodyDescription(1f) { SleepThreshold = 0f });

        ConstraintHandle winch = world.AddConstraint(ConstraintDescription.DistanceJoint(
                ConstraintAttachment.AtWorld(anchor), ConstraintAttachment.OnBody(load),
                anchorA: Vector3.Zero, anchorB: Vector3.Zero, minDistance: 0f, maxDistance: 4f)
            .WithWinch(4f, maxSpeed: 2f, maxForce: 200f));

        float startY = world.GetDynamicPose(load).Position.Y;
        // Reel the target length from 4 m down to 1 m over 300 steps (5 s), one cheap allocation-free retarget/frame.
        float length = 4f;
        for (int i = 0; i < 300; i++)
        {
            length = MathF.Max(1f, length - 0.02f);
            world.SetConstraintTarget(winch, length);
            world.Step(Dt);
        }
        float endY = world.GetDynamicPose(load).Position.Y;
        float endDist = Vector3.Distance(world.GetDynamicPose(load).Position, anchor);

        Assert.True(endY > startY + 2f, $"winch must reel the body upward (startY {startY:F3} -> endY {endY:F3})");
        Assert.True(MathF.Abs(endDist - 1f) < 0.3f, $"winch must reel to ~1 m from the anchor, ended at {endDist:F3} m");
    }

    [Fact]
    public void SetConstraintTarget_OnAStaleOrMotorlessConstraint_Throws()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        // A passive distance joint (no winch): retargeting it has no motor to drive, so it throws.
        DynamicBodyHandle bob = world.AddDynamic(SmallBox, Pose.At(new Vector3(0f, 8f, 0f)),
            new DynamicBodyDescription(1f) { SleepThreshold = 0f });
        ConstraintHandle passive = world.AddConstraint(ConstraintDescription.DistanceJoint(
            ConstraintAttachment.AtWorld(new Vector3(0f, 10f, 0f)), ConstraintAttachment.OnBody(bob),
            Vector3.Zero, Vector3.Zero, 0f, 2f));
        Assert.Throws<ArgumentException>(() => world.SetConstraintTarget(passive, 1f));

        // A powered winch whose body is then removed: the handle is stale, so retargeting throws.
        ConstraintHandle winch = world.AddConstraint(ConstraintDescription.DistanceJoint(
                ConstraintAttachment.AtWorld(new Vector3(5f, 10f, 0f)), ConstraintAttachment.OnBody(
                    world.AddDynamic(SmallBox, Pose.At(new Vector3(5f, 8f, 0f)), DynamicBodyDescription.WithMass(1f))),
                Vector3.Zero, Vector3.Zero, 0f, 2f)
            .WithWinch(2f));
        // Recreate a body handle to remove: pull it back via a fresh add/remove is awkward; instead remove by
        // stepping then removing the constraint, which makes the handle stale for a mutation.
        world.RemoveConstraint(winch);
        Assert.Throws<ArgumentException>(() => world.SetConstraintTarget(winch, 1f));
    }

    // ---------------------------------------------------------------------
    // A hinge drive on the wrong joint kind is rejected at add time.
    // ---------------------------------------------------------------------

    [Fact]
    public void AddConstraint_WithAMotorForTheWrongJointKind_Throws()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        DynamicBodyHandle body = world.AddDynamic(SmallBox, Pose.At(new Vector3(0f, 5f, 0f)),
            DynamicBodyDescription.WithMass(1f));
        // A distance joint carrying a HINGE servo: the drive does not match the kind, so the add throws.
        var bad = ConstraintDescription.DistanceJoint(
                ConstraintAttachment.AtWorld(new Vector3(0f, 7f, 0f)), ConstraintAttachment.OnBody(body),
                Vector3.Zero, Vector3.Zero, 0f, 2f)
            with
        { Motor = ConstraintMotor.HingeAngle, MotorTarget = 0.5f };
        Assert.Throws<ArgumentException>(() => world.AddConstraint(bad));
    }

    // ---------------------------------------------------------------------
    // Determinism with an active servo: two identical worlds each driving a hinge servo step bit-identically.
    // ---------------------------------------------------------------------

    [Fact]
    public void TwoIdenticalWorlds_WithAnActiveServo_ProduceIdenticalPoses()
    {
        static (Vector3 pos, Quaternion orient) Run()
        {
            using IPhysicsWorld world = new BepuPhysicsWorld();
            var pivot = new Vector3(0f, 5f, 0f);
            var arm = world.AddDynamic(SmallBox, Pose.At(pivot + new Vector3(1.5f, 0f, 0f)),
                new DynamicBodyDescription(1f) { SleepThreshold = 0f });
            ConstraintHandle hinge = world.AddConstraint(ConstraintDescription.HingeJoint(
                    ConstraintAttachment.AtWorld(pivot), ConstraintAttachment.OnBody(arm),
                    Vector3.Zero, new Vector3(-1.5f, 0f, 0f), Vector3.UnitZ, Vector3.UnitZ)
                .WithHingeServo(1.0f, maxSpeed: 2f));

            // Retarget every frame (a moving servo goal) to exercise the ApplyDescription path in the fingerprint.
            for (int i = 0; i < 200; i++)
            {
                world.SetConstraintTarget(hinge, 0.5f + 0.5f * MathF.Sin(i * 0.05f));
                world.Step(Dt);
            }
            return (world.GetDynamicPose(arm).Position, world.GetDynamicPose(arm).Orientation);
        }

        var a = Run();
        var b = Run();
        Assert.Equal(a.pos.X, b.pos.X);
        Assert.Equal(a.pos.Y, b.pos.Y);
        Assert.Equal(a.pos.Z, b.pos.Z);
        Assert.Equal(a.orient.X, b.orient.X);
        Assert.Equal(a.orient.Y, b.orient.Y);
        Assert.Equal(a.orient.Z, b.orient.Z);
        Assert.Equal(a.orient.W, b.orient.W);
    }
}

/// <summary>The allocation-sensitive constraint test lives in the non-parallel <c>AllocSensitive</c> collection so
/// the per-thread <c>GC.GetAllocatedBytesForCurrentThread()</c> measurement is not perturbed by other tests churning
/// the GC on parallel threads.</summary>
[Collection("AllocSensitive")]
public class ConstraintMotorAllocTests
{
    const float Dt = 1f / 60f;
    static BoxShape SmallBox => new(new Vector3(0.25f, 0.25f, 0.25f));

    // ---------------------------------------------------------------------
    // Steady-state per-frame retargeting is allocation-free: SetConstraintTarget re-describes the live Bepu
    // servo via a stack-built description + Solver.ApplyDescription, which allocates nothing (verified in a
    // Bepu reflection probe: 1000 ApplyDescription calls = 0 bytes). This locks that the seam adds no per-frame
    // garbage, so a game can drive a servo goal every tick without GC pressure.
    // ---------------------------------------------------------------------

    [Fact]
    public void SetConstraintTarget_SteadyState_AllocatesNothing()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        var anchor = new Vector3(0f, 10f, 0f);
        DynamicBodyHandle load = world.AddDynamic(SmallBox, Pose.At(anchor + new Vector3(0f, -3f, 0f)),
            new DynamicBodyDescription(1f) { SleepThreshold = 0f });
        ConstraintHandle winch = world.AddConstraint(ConstraintDescription.DistanceJoint(
                ConstraintAttachment.AtWorld(anchor), ConstraintAttachment.OnBody(load),
                Vector3.Zero, Vector3.Zero, 0f, 3f)
            .WithWinch(3f, maxSpeed: 2f));

        // Warm: run the retarget path (and step once) to trigger first-call JIT before measuring.
        for (int warm = 0; warm < 8; warm++)
        {
            world.SetConstraintTarget(winch, 2.5f);
            world.Step(Dt);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
            world.SetConstraintTarget(winch, 2.0f + 0.5f * (i % 2));
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before); // per-frame target updates allocate nothing at steady state
    }
}
