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
/// <para>Everything here is headless and fixed-dt. The one timing case (the cost budget) states its own noise
/// discipline.</para>
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

    // ---------------------------------------------------------------------
    // Test 8b: the cost budget, as an acceptance condition rather than an estimate. One rebase must cost less than
    // one physics step on the same world. Timing is noisy, so both sides are the BEST of several runs (the best
    // sample is the one least polluted by scheduling), and the world is warmed first.
    // ---------------------------------------------------------------------

    [Fact]
    public void Rebase_CostsLessThanOneStep_AtAResidentStreamingScale()
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
        world.Rebase(new Vector3(128f, 0f, 0f));
        world.Rebase(Vector3.Zero);

        double step = BestOf(5, () => world.Step(Dt));
        double rebase = BestOf(5, () =>
        {
            world.Rebase(new Vector3(128f, 0f, 128f));
            world.Rebase(Vector3.Zero);
        }) / 2d;                            // two rebases per sample so the world ends where it started

        Assert.True(rebase < step,
            $"one rebase cost {rebase * 1000d:F3} ms against a step's {step * 1000d:F3} ms. The budget is one " +
            "rebase per physics step: bound the streaming ring, or the refit needs amortizing across ticks.");

        static double BestOf(int samples, Action action)
        {
            double best = double.MaxValue;
            for (int i = 0; i < samples; i++)
            {
                long start = Stopwatch.GetTimestamp();
                action();
                best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalSeconds);
            }
            return best;
        }
    }

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
