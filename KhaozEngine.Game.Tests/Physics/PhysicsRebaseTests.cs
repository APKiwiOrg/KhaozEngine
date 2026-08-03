using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Primitives;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Physics;

/// <summary>
/// The floating-origin physics seam: <see cref="IPhysicsWorld.Origin"/> / <see cref="IPhysicsWorld.CanRebase"/> /
/// <see cref="IPhysicsWorld.Rebase"/>, and the Bepu implementation behind them. A rebase re-expresses a live world
/// against a new origin as a bulk of direct pose writes plus broadphase refits, so it is a change of coordinate
/// space that nothing inside the simulation can observe: sleep state, contacts, velocities and constraints all
/// survive it.
/// <para>Everything here is headless and fixed-dt. The one timing case is the cost budget, and it lives in
/// <see cref="PhysicsRebaseCostTests"/> at the bottom of this file with its own noise discipline.</para>
/// </summary>
public class PhysicsRebaseTests
{
    const float Dt = 1f / 60f;

    static readonly BoxShape UnitBox = new(new Vector3(0.5f, 0.5f, 0.5f));
    static readonly BoxShape Ground = new(new Vector3(20f, 0.5f, 20f));

    static void StepMany(IPhysicsWorld world, int steps)
    {
        for (int i = 0; i < steps; i++) world.Step(Dt);
    }

    // A settled 4-box stack resting on a ground static, at the world origin, asleep or nearly so.
    static (BepuPhysicsWorld World, StaticHandle Ground, DynamicBodyHandle[] Boxes) SettledStack(int boxes = 4)
    {
        var world = new BepuPhysicsWorld();
        StaticHandle ground = world.AddStatic(Ground, Pose.At(new Vector3(0f, -0.5f, 0f)));
        var handles = new DynamicBodyHandle[boxes];
        for (int i = 0; i < boxes; i++)
            handles[i] = world.AddDynamic(UnitBox, Pose.At(new Vector3(0f, 0.5f + i * 1.02f, 0f)),
                DynamicBodyDescription.WithMass(1f));
        StepMany(world, 600);   // 10 s: fall, stack, settle
        return (world, ground, handles);
    }

    static Vector3[] Poses(IPhysicsWorld world, DynamicBodyHandle[] handles)
    {
        var poses = new Vector3[handles.Length];
        for (int i = 0; i < handles.Length; i++) poses[i] = world.GetDynamicPose(handles[i]).Position;
        return poses;
    }

    // ---------------------------------------------------------------------
    // Test 7: the round trip is bit-exact and Origin moves with the contents.
    // ---------------------------------------------------------------------

    [Fact]
    public void Rebase_RoundTrip_RestoresEveryPoseBitExactly()
    {
        (BepuPhysicsWorld world, _, DynamicBodyHandle[] boxes) = SettledStack();
        using (world)
        {
            Assert.True(world.CanRebase);
            Assert.Equal(Vector3.Zero, world.Origin);

            // Put the contents out at a grid-aligned 100 km first, which is the state a rebase exists to fix. The
            // exactness lemma needs the conversion not to GROW a coordinate's magnitude, and THIS leg grows it, so
            // it is where information is lost (the stack's sub-millimetre offsets fall off the 7.8 mm lattice) and
            // it is deliberately not asserted on. That asymmetry is why WorldFrame anchors to the NEAREST grid
            // point and only re-anchors past 96 m: a re-anchor always shrinks, so it is always exact.
            var far = new Vector3(128f * 781f, 0f, -128f * 781f);
            world.Rebase(-far);
            Assert.Equal(-far, world.Origin);
            Vector3[] atRange = Poses(world, boxes);

            // Home: magnitude shrinks, so every pose moves by exactly the delta.
            world.Rebase(Vector3.Zero);
            Assert.Equal(Vector3.Zero, world.Origin);
            for (int i = 0; i < boxes.Length; i++)
                Assert.Equal(atRange[i] - far, world.GetDynamicPose(boxes[i]).Position);

            // And back out: the sum reproduces a value that was representable, so the round trip is bit-identical
            // rather than approximately right.
            world.Rebase(-far);
            Vector3[] after = Poses(world, boxes);
            for (int i = 0; i < boxes.Length; i++)
                Assert.Equal(atRange[i], after[i]);
        }
    }

    // ---------------------------------------------------------------------
    // Test 7a: a SLEEPING body resting on a STATIC, with both translated in the same rebase. This is the terrain
    // case the original Bepu probe never ran (it translated a stack of dynamics), and it is the one whose failure
    // mode is a crate sinking into or being ejected from the terrain after a shift.
    // ---------------------------------------------------------------------

    [Fact]
    public void Rebase_SleepingBodyOnAStatic_StaysAsleepAndDoesNotMove()
    {
        using var world = new BepuPhysicsWorld();
        world.AddStatic(Ground, Pose.At(new Vector3(0f, -0.5f, 0f)));
        DynamicBodyHandle crate = world.AddDynamic(UnitBox, Pose.At(new Vector3(1f, 0.5f, -2f)),
            DynamicBodyDescription.WithMass(1f));

        StepMany(world, 900);   // 15 s: settle and fall asleep
        Assert.False(world.IsAwake(crate), "the crate must be asleep before the rebase or this test proves nothing");
        Vector3 before = world.GetDynamicPose(crate).Position;

        var anchor = new Vector3(-128f * 3f, 0f, 128f * 7f);
        world.Rebase(anchor);

        Assert.False(world.IsAwake(crate));                                  // the refit must not wake it
        Assert.Equal(before - anchor, world.GetDynamicPose(crate).Position); // moved by the delta, exactly

        Vector3 translated = world.GetDynamicPose(crate).Position;
        StepMany(world, 60);
        Assert.False(world.IsAwake(crate));
        Assert.Equal(translated, world.GetDynamicPose(crate).Position);      // 0.000000 m over 60 further steps

        // The pose assertions above prove the crate's TRANSFORM moved with the rebase, but Bepu keeps a separate
        // broadphase AABB per body, and a rebase that writes poses without refitting that AABB would still pass
        // every assertion so far while leaving the crate physically invisible to new collision at its new
        // coordinate. Drop a fresh dynamic box onto the sleeping crate's REBASED (x, z) from above and let it
        // settle: if the broadphase leaf moved with the pose, the box lands and rests on the crate's top face. If
        // it did not, the box falls straight through the crate (undetected) onto the static ground beneath it
        // instead, landing about a metre lower.
        DynamicBodyHandle dropped = world.AddDynamic(UnitBox,
            Pose.At(new Vector3(translated.X, translated.Y + 5f, translated.Z)), DynamicBodyDescription.WithMass(1f));
        StepMany(world, 300);   // 5 s: fall onto the crate and settle

        float restY = world.GetDynamicPose(dropped).Position.Y;
        float expectedRestY = translated.Y + 1f;   // crate top (translated.Y + 0.5) + dropped box half-height (0.5)
        Assert.True(Math.Abs(restY - expectedRestY) < 0.05f,
            $"expected the dropped box to come to rest ON the rebased crate at y~{expectedRestY:F4} (measured " +
            $"~1.4989 for a crate rebased to Y=0), got {restY:F4} - this is the broadphase-leaf-did-not-move " +
            "failure mode: the box fell through the crate to the ground instead of resting on it.");
    }

    // ---------------------------------------------------------------------
    // Test 8: contacts survive a shift into a far destination.
    // ---------------------------------------------------------------------

    [Fact]
    public void Rebase_SettledStack_KeepsItsContacts()
    {
        (BepuPhysicsWorld world, _, DynamicBodyHandle[] boxes) = SettledStack();
        using (world)
        {
            Vector3[] before = Poses(world, boxes);
            Vector3 destination = new(100_000f, 0f, 100_000f);
            world.Rebase(-destination);   // contents END at 100 km: the arithmetic runs at that magnitude

            StepMany(world, 60);
            for (int i = 0; i < boxes.Length; i++)
            {
                Vector3 expected = before[i] + destination;
                Vector3 actual = world.GetDynamicPose(boxes[i]).Position;
                Assert.True(Vector3.Distance(expected, actual) < 1e-3f,
                    $"box {i} drifted {Vector3.Distance(expected, actual) * 1000f:F3} mm after the shift");
                world.GetDynamicVelocity(boxes[i], out Vector3 linear, out _);
                Assert.True(linear.Length() < 0.05f, $"box {i} picked up {linear.Length():F4} m/s: the shift is not inert");
            }
        }
    }

    // ---------------------------------------------------------------------
    // Test 8a: the small-frame shift has no drift term. The probe measured 0.365 mm rebasing a stack INTO a 100 km
    // destination and attributed it to the destination MAGNITUDE rather than to the shift. This measures both, so
    // that attribution stops being an inference.
    // ---------------------------------------------------------------------

    [Fact]
    public void Rebase_IntoASmallFrame_HasNoDriftTermUnlikeA100KmOne()
    {
        float far = DriftAfterShiftTo(new Vector3(100_000f, 0f, 100_000f));
        float near = DriftAfterShiftTo(new Vector3(96f, 0f, 96f));   // 136 m planar: the design target

        // The design target's shift is an order of magnitude cleaner, and in absolute terms it is micrometres.
        Assert.True(near < 3.65e-5f, $"the 136 m shift drifted {near * 1000f:F5} mm, which is not a no-op");
        Assert.True(near * 10f <= far,
            $"the 136 m shift drifted {near * 1000f:F5} mm against the 100 km shift's {far * 1000f:F5} mm: the drift " +
            "is not explained by the destination magnitude, so something about the SHIFT itself is lossy.");

        static float DriftAfterShiftTo(Vector3 destination)
        {
            (BepuPhysicsWorld world, _, DynamicBodyHandle[] boxes) = SettledStack();
            using (world)
            {
                Vector3[] before = Poses(world, boxes);
                world.Rebase(-destination);
                StepMany(world, 60);
                float worst = 0f;
                for (int i = 0; i < boxes.Length; i++)
                    worst = MathF.Max(worst, Vector3.Distance(before[i] + destination, world.GetDynamicPose(boxes[i]).Position));
                return worst;
            }
        }
    }

    // Test 8b, the cost budget, is not in this class. It lives in PhysicsRebaseCostTests at the bottom of this
    // file, which sits in the non-parallel AllocSensitive collection. That class says why.

    // ---------------------------------------------------------------------
    // Test 9: constraints survive, including a world-space anchor end (a shapeless kinematic body, which the body
    // sweep covers because it enumerates every allocated set rather than the active one).
    // ---------------------------------------------------------------------

    [Fact]
    public void Rebase_HingeAndSlider_KeepTheirJointsAndTheirWorldAnchors()
    {
        using var world = new BepuPhysicsWorld();
        var pivot = new Vector3(0f, 5f, 0f);
        DynamicBodyHandle bob = world.AddDynamic(UnitBox, Pose.At(pivot + new Vector3(1.5f, 0f, 0f)),
            new DynamicBodyDescription(1f) { SleepThreshold = 0f });
        world.AddConstraint(ConstraintDescription.HingeJoint(
            ConstraintAttachment.OnBody(bob), ConstraintAttachment.AtWorld(pivot),
            anchorA: new Vector3(-1.5f, 0f, 0f), anchorB: Vector3.Zero,
            axisA: Vector3.UnitZ, axisB: Vector3.UnitZ));

        var sliderAnchor = new Vector3(10f, 5f, 0f);
        DynamicBodyHandle slider = world.AddDynamic(UnitBox, Pose.At(sliderAnchor + new Vector3(0f, -0.5f, 0f)),
            new DynamicBodyDescription(1f) { SleepThreshold = 0f });
        world.AddConstraint(ConstraintDescription.SliderJoint(
            ConstraintAttachment.AtWorld(sliderAnchor), ConstraintAttachment.OnBody(slider),
            anchorA: Vector3.Zero, anchorB: Vector3.Zero,
            axis: Vector3.UnitY, minOffset: -2f, maxOffset: 0f));

        StepMany(world, 120);
        var anchor = new Vector3(128f * 100f, 0f, 128f * 100f);
        world.Rebase(anchor);
        StepMany(world, 300);

        // The hinge still pins the arm at its own length from the (now translated) pivot.
        Vector3 pivotLocal = pivot - anchor;
        float arm = Vector3.Distance(world.GetDynamicPose(bob).Position, pivotLocal);
        Assert.True(MathF.Abs(arm - 1.5f) < 0.05f, $"the hinge arm reads {arm:F4} m after the rebase, not 1.5 m");

        // The slider still hangs within its travel below the (now translated) world anchor, on its axis.
        Vector3 sliderLocal = sliderAnchor - anchor;
        Vector3 sliderPos = world.GetDynamicPose(slider).Position;
        Assert.True(MathF.Abs(sliderPos.X - sliderLocal.X) < 0.05f && MathF.Abs(sliderPos.Z - sliderLocal.Z) < 0.05f,
            "the slider left its axis after the rebase, so its world anchor did not move with it");
        Assert.InRange(sliderPos.Y - sliderLocal.Y, -2.1f, 0.1f);
    }

    // ---------------------------------------------------------------------
    // Test 10: statics move, so the same query at the translated coordinate answers identically.
    // ---------------------------------------------------------------------

    [Fact]
    public void Rebase_Statics_AnswerTheSameQueryAtTheTranslatedCoordinate()
    {
        using var world = new BepuPhysicsWorld();
        world.AddStatic(Ground, Pose.At(new Vector3(0f, -0.5f, 0f)));

        var from = new Vector3(3f, 10f, -4f);
        Assert.True(world.Raycast(from, -Vector3.UnitY, 50f, out RayHit before, QueryFilter.StaticsOnly));

        var anchor = new Vector3(128f * 781f, 0f, 128f * 781f);
        world.Rebase(anchor);

        Assert.True(world.Raycast(from - anchor, -Vector3.UnitY, 50f, out RayHit after, QueryFilter.StaticsOnly));
        Assert.Equal(before.Distance, after.Distance);   // a distance is frame-invariant, and the shift is exact
    }

    // ---------------------------------------------------------------------
    // The seam the readable Origin exists to close: everything that speaks ABSOLUTE to a rebased world. Streaming
    // does not stop when a world rebases, so a chunk that arrives afterwards has to land in the space the world is
    // in now. A sink that forgets is a sink that never read Origin, and its props sit one anchor delta away from
    // the ones already there - silently, and only for the chunks that streamed in late.
    // ---------------------------------------------------------------------

    [Fact]
    public void StreamingSinks_AddIntoARebasedWorld_AtTheReducedPose()
    {
        using var world = new BepuPhysicsWorld();
        var anchor = new Vector3(128f * 781f, 0f, 128f * 781f);   // ~100 km, grid-aligned
        world.Rebase(anchor);

        // A prop static, authored absolute.
        var propAbsolute = new Vector3(anchor.X + 12f, 4f, anchor.Z + 7f);
        var placements = new[] { new PropPlacement("crate", propAbsolute.X, propAbsolute.Y, propAbsolute.Z, 1f, 0f, 0) };
        var shapes = new Dictionary<string, PhysicsShape> { ["crate"] = UnitBox };
        var statics = new List<StaticHandle>();
        ChunkStatics.AddAll(world, shapes, placements, statics);
        Assert.Single(statics);
        Assert.True(world.Raycast(propAbsolute - anchor + new Vector3(0f, 5f, 0f), -Vector3.UnitY, 20f, out _,
            QueryFilter.StaticsOnly), "the prop must be where the world's own space puts it, not one anchor away");

        // A dynamic spawn, authored absolute.
        var spawnAbsolute = new Vector3(anchor.X - 20f, 9f, anchor.Z + 3f);
        var dynamics = new List<DynamicBodyHandle>();
        ChunkDynamics.AddAll(world, new[]
        {
            new DynamicSpawn(UnitBox, Pose.At(spawnAbsolute), DynamicBodyDescription.WithMass(1f)),
        }, dynamics);
        Assert.Equal(spawnAbsolute - anchor, world.GetDynamicPose(dynamics[0]).Position);

        // A terrain chunk, whose region origin is absolute.
        var region = new TerrainChunkRegion { OriginX = anchor.X, OriginZ = anchor.Z, Size = 60f };
        var field = new TerrainField(TerrainPresets.Clearing());
        Assert.True(ChunkTerrainCollision.Add(world, TerrainChunkBuilder.Build(field, region, lod: 1), out _));
        Assert.True(world.Raycast(new Vector3(30f, 500f, 30f), -Vector3.UnitY, 1000f, out _, QueryFilter.StaticsOnly),
            "the chunk must sit at its region origin MINUS the world origin, which for this anchor is the local 0..60 square");
    }

    // ---------------------------------------------------------------------
    // Test 11: the seam's default-interface contract. A backend that does not implement Rebase reports so and
    // throws rather than silently doing nothing, which is what makes CanRebase worth checking.
    // ---------------------------------------------------------------------

    [Fact]
    public void SeamDefault_CannotRebase_AndThrowsIfAsked()
    {
        IPhysicsWorld seam = new UnrebasableWorld();
        Assert.False(seam.CanRebase);
        Assert.Equal(Vector3.Zero, seam.Origin);
        Assert.Throws<NotSupportedException>(() => seam.Rebase(new Vector3(128f, 0f, 0f)));
    }

    // A backend written before the rebase API existed: it implements only the members that were there, so it picks
    // up all three defaults. Exactly the shape of a consumer's test double.
    sealed class UnrebasableWorld : IPhysicsWorld
    {
        public StaticHandle AddStatic(PhysicsShape shape, Pose pose, PhysicsMaterial? material = null) => default;
        public void RemoveStatic(StaticHandle handle) { }
        public DynamicBodyHandle AddDynamic(PhysicsShape shape, Pose pose, DynamicBodyDescription body, PhysicsMaterial? material = null) => default;
        public void RemoveDynamic(DynamicBodyHandle handle) { }
        public Pose GetDynamicPose(DynamicBodyHandle handle) => Pose.Identity;
        public void GetDynamicVelocity(DynamicBodyHandle handle, out Vector3 linear, out Vector3 angular) { linear = default; angular = default; }
        public void SetDynamicVelocity(DynamicBodyHandle handle, Vector3 linear, Vector3 angular) { }
        public bool IsAwake(DynamicBodyHandle handle) => false;
        public ConstraintHandle AddConstraint(in ConstraintDescription description) => default;
        public void RemoveConstraint(ConstraintHandle handle) { }
        public void SetConstraintTarget(ConstraintHandle handle, float target) { }
        public void Step(float dt) { }
        public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit, QueryFilter filter = default) { hit = default; return false; }
        public bool SweepCapsule(CapsuleShape capsule, Pose pose, Vector3 direction, float maxDistance, out SweepHit hit, QueryFilter filter = default) { hit = default; return false; }
        public bool ComputePenetration(CapsuleShape capsule, Pose pose, out Vector3 mtv) { mtv = default; return false; }
        public void Dispose() { }
    }
}


/// <summary>
/// The rebase COST budget, in a class of its own because it belongs in the non-parallel <c>AllocSensitive</c>
/// collection, exactly like <c>ConstraintMotorAllocTests</c> next door. Two reasons, and both are the reason this
/// test was reworked at all. Its zero-allocation assertion reads <c>GC.GetAllocatedBytesForCurrentThread()</c>,
/// which other tests churning the GC on parallel threads perturb through gen-0 reconciliation. And its timing
/// half wants a machine the rest of this assembly is not competing for, which is precisely the condition issue
/// 466 recorded: the old bound failed only inside the runner's own full-suite execution, and passed when these
/// same tests were run alone on that same machine.
/// </summary>
[Collection("AllocSensitive")]
public class PhysicsRebaseCostTests
{
    const float Dt = 1f / 60f;

    static readonly BoxShape UnitBox = new(new Vector3(0.5f, 0.5f, 0.5f));

    static void StepMany(IPhysicsWorld world, int steps)
    {
        for (int i = 0; i < steps; i++) world.Step(Dt);
    }

    // ---------------------------------------------------------------------
    // Test 8b: the cost budget, as an acceptance condition rather than an estimate. Its noise discipline and the
    // arithmetic behind its bound are on the method.
    // ---------------------------------------------------------------------

    /// <summary>
    /// THE COST BUDGET, as an acceptance condition rather than an estimate. One rebase at a resident streaming
    /// scale must stay within a small multiple of one physics step on the same world, because the regression this
    /// guards is an unbounded refit, and that costs a rebase MULTIPLES of a step rather than a few percent.
    /// <para>
    /// WHY IT IS NOT <c>rebase &lt; step</c> ANY MORE (issue 466). That form asserted at the true value of the
    /// quantity it measured: a rebase at this scale costs about what a step costs, so the verdict sat on the
    /// boundary and any noise at all decided it. It reddened the self-hosted macOS leg three times, and that leg
    /// is the primary dev Mac, shared with interactive work. Run 30737392817 failed and then failed AGAIN on an
    /// idle rerun. Run 30795099275 measured a 54.211 ms rebase against a 13.456 ms step, a 4.03x flip, on a
    /// machine concurrently running three test suites and a build. Run 30800593925 measured 3.758 ms against
    /// 2.848 ms, a 1.32x flip in which BOTH numbers are healthy and only the ORDER was wrong.
    /// </para>
    /// <para>
    /// THE MEASUREMENT. The samples are INTERLEAVED, one step and one rebase pair per iteration, so a scheduler
    /// burst lands inside the same iteration as the step it is compared against rather than wholly inside one of
    /// two sequential measurement blocks. Block separation is what produced both flips above: on 30795099275 the
    /// rebase block came out 54x its idle cost while the step block was barely 4x, and on 30800593925 the rebase
    /// block was inflated where the step block was not. And the verdict is the MEDIAN of the five per-iteration
    /// ratios rather than one number divided by another, so a preempted sample cannot decide it. Three of the
    /// five have to go the same way first, and every ratio carries its own correction for machine load, because
    /// both of its halves met that machine within a few milliseconds of each other.
    /// </para>
    /// <para>
    /// WHERE 3x COMES FROM, measured on the same Mac that hosts the self-hosted leg. Unloaded, the ratio is
    /// about 0.32x, a rebase being CHEAPER than a step at this scale (roughly 1.0 ms against 3.1 ms). Under a
    /// continuous solution rebuild holding load average between 42 and 52 on 12 cores, which is heavier than any
    /// of the three incidents, five consecutive runs put the median ratio at 0.34, 0.45, 0.35, 0.57 and 0.33,
    /// and the worst SINGLE iteration out of those 25 reached 0.92x. So the bound sits 5.3x above the worst
    /// median a saturated machine produced and 3.3x above its worst individual sample, and it clears the worst
    /// residual noise the OLD formulation recorded on healthy numbers, 30800593925's 1.32x, by 2.3x. It
    /// deliberately does not try to absorb 30795099275's 4.03x, because interleaving removes that shape instead
    /// of tolerating it, and a bound wide enough to swallow it would assert nothing at all. What 3x still fails
    /// is the regression this test exists for: an unbounded refit, or the <c>Statics.ApplyDescription</c> form
    /// that wakes the whole sleeping population, costs a rebase an ORDER of magnitude and not a third.
    /// </para>
    /// <para>
    /// AND A STRUCTURAL PIN NO CLOCK CAN FLAKE, in the shape the drain test took for the same reason (commit
    /// 4a075e9d). A rebase is a straight-line pass over Bepu's unmanaged buffers, so it allocates NOTHING
    /// managed, identically on every machine at every load. That catches the per-object managed work an
    /// unbounded refit would introduce. It does not catch a regression that stays inside Bepu's own unmanaged
    /// tree, which is what the timing bound above is still here for. Both assertions share this one fixture
    /// instead of splitting into two facts, because building it is most of the test's runtime and a split pays
    /// that twice.
    /// </para>
    /// </summary>
    [Fact]
    public void Rebase_StaysWithinASmallMultipleOfOneStep_AtAResidentStreamingScale()
    {
        // Ruinborne's resident shape: a gameplay ring of terrain statics, a few thousand props, a few hundred
        // dynamics. Terrain statics are triangle meshes, which is the expensive collidable to refit.
        using var world = new BepuPhysicsWorld();
        for (int i = 0; i < 25; i++)
        {
            float x = (i % 5) * 60f, z = (i / 5) * 60f;
            world.AddStatic(FlatTriangleMesh(60f, 8), Pose.At(new Vector3(x, 0f, z)));
        }
        var rng = new XorRng(12345);
        for (int i = 0; i < 2000; i++)
            world.AddStatic(UnitBox, Pose.At(new Vector3(rng.NextFloat() * 300f, 0.5f, rng.NextFloat() * 300f)));
        for (int i = 0; i < 200; i++)
            world.AddDynamic(UnitBox, Pose.At(new Vector3(rng.NextFloat() * 300f, 4f + i * 0.01f, rng.NextFloat() * 300f)),
                DynamicBodyDescription.WithMass(1f));

        StepMany(world, 120);              // settle, and warm every code path the measurement touches
        var shift = new Vector3(128f, 0f, 128f);
        world.Rebase(shift);
        world.Rebase(Vector3.Zero);

        // The structural half, taken on the warmed world before any clock is involved.
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        world.Rebase(shift);
        world.Rebase(Vector3.Zero);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Assert.True(allocated == 0L,
            $"two rebases of this world allocated {allocated} managed bytes. A rebase is a pose write plus an " +
            "UpdateBounds per static and per body, straight over Bepu's unmanaged buffers, so it allocates " +
            "nothing: a non-zero count means the pass now does per-object managed work, which is the shape an " +
            "unbounded refit takes. This assertion is machine-independent, so read it as a real regression and " +
            "never as noise.");

        // The timed half. One step and one rebase pair per iteration, so both halves of every ratio meet the same
        // machine. See the method's note for why the verdict is the median ratio and why the bound is 3x.
        const int Samples = 5;             // odd, so the median is a measured sample rather than an average
        const double Headroom = 3d;
        var steps = new double[Samples];
        var rebases = new double[Samples];
        var ratios = new double[Samples];
        for (int i = 0; i < Samples; i++)
        {
            long beforeStep = Stopwatch.GetTimestamp();
            world.Step(Dt);
            long afterStep = Stopwatch.GetTimestamp();
            world.Rebase(shift);
            world.Rebase(Vector3.Zero);    // two rebases per sample so the world ends where it started
            long afterRebase = Stopwatch.GetTimestamp();

            steps[i] = Stopwatch.GetElapsedTime(beforeStep, afterStep).TotalSeconds;
            rebases[i] = Stopwatch.GetElapsedTime(afterStep, afterRebase).TotalSeconds / 2d;
            ratios[i] = rebases[i] / steps[i];
        }

        double ratio = Median(ratios);

        Assert.True(ratio < Headroom,
            $"one rebase cost {Median(rebases) * 1000d:F3} ms against a step's {Median(steps) * 1000d:F3} ms, a " +
            $"median ratio of {ratio:F2}x over {Samples} interleaved samples against a bound of {Headroom:F0}x. " +
            "The budget is a small multiple of one physics step: bound the streaming ring, or the refit needs " +
            $"amortizing across ticks. Per-iteration step/rebase in ms: {Pairs(steps, rebases)}.");

        static double Median(double[] samples)
        {
            var sorted = (double[])samples.Clone();
            Array.Sort(sorted);
            return sorted[sorted.Length / 2];
        }

        // Unsorted and paired, because which step a rebase was measured against is the whole diagnosis.
        static string Pairs(double[] steps, double[] rebases)
        {
            var parts = new string[steps.Length];
            for (int i = 0; i < steps.Length; i++)
                parts[i] = $"{steps[i] * 1000d:F3}/{rebases[i] * 1000d:F3}";
            return string.Join(", ", parts);
        }
    }

    // A flat res x res triangle grid over [0, size]^2 at y = 0, wound so Bepu's front face points up (the same
    // reversal TerrainChunkCollision applies). Stands in for a terrain chunk's collision surface.
    static TriangleMeshShape FlatTriangleMesh(float size, int res)
    {
        int cols = res + 1;
        var verts = new Vector3[cols * cols];
        for (int iz = 0; iz <= res; iz++)
        for (int ix = 0; ix <= res; ix++)
            verts[iz * cols + ix] = new Vector3((float)ix / res * size, 0f, (float)iz / res * size);

        var inds = new List<int>(res * res * 6);
        for (int iz = 0; iz < res; iz++)
        for (int ix = 0; ix < res; ix++)
        {
            int i0 = iz * cols + ix, i1 = i0 + 1, i2 = (iz + 1) * cols + ix, i3 = i2 + 1;
            inds.Add(i0); inds.Add(i3); inds.Add(i2);
            inds.Add(i0); inds.Add(i1); inds.Add(i3);
        }
        return new TriangleMeshShape(verts, inds.ToArray());
    }
}