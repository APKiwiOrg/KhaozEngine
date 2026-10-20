using System;
using System.Numerics;
using BepuPhysics.Collidables;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using static KhaozEngine.Tests.Physics.CertifiedGeneralSweepFixture;

namespace KhaozEngine.Tests.Physics;

[Collection("AllocSensitive")]
public sealed class CertifiedMeshSweepCoverageTests
{
    [Fact]
    public void MeshScaledRotatedBoundsCoverEveryRequiredTriangle()
    {
        foreach (Quaternion rotation in new[] { new Quaternion(0.5f, 0.5f, 0.5f, 0.5f), new Quaternion(0, 0, 0.5f, 0.8660254f) })
        {
            using var world = new BepuPhysicsWorld(Vector3.Zero);
            StaticHandle handle = world.AddStatic(CertifiedMeshSweepTests.Quad(), new Pose(new(3, 2, -4), rotation));
            SetScale(world, handle, new(2, 0.5f, 1.5f));
            Vector3 start = new Vector3(3, 2, -4) + Vector3.Transform(new Vector3(-0.5f, 0, -1.5f), rotation) + new Vector3(0, 0, -2);
            Vector3 delta = new(0, 0, 4);
            // Select a ray perpendicular to the cyclic image, or along the unchanged Z edge
            // for the rounded Z rotation. The independent installed oracle supplies the event.
            if (rotation.W != 0.5f)
            { start = new Vector3(3, 2, -4) + Vector3.Transform(new Vector3(-1, 2, -0.5f), rotation); delta = new(2, 0, 0); }
            var expected = CertifiedSweepExactOracle.TriangleEntry(world, handle, 0, start, 0.25f, 0.5f, delta);
            HitContains(Read(world, start, delta), expected.Lower, expected.Upper);
        }
        using (var closed = new BepuPhysicsWorld(Vector3.Zero))
        {
            var pose = new Pose(new(3, 2, -4), new(0.5f, 0.5f, 0.5f, 0.5f));
            StaticHandle handle = closed.AddStatic(new TriangleMeshShape(
                [new(0, -2, -2), new(0, -2, 2), new(0, 4, 0)], [0, 1, 2]), pose);
            SetScale(closed, handle, new(2, 0.5f, 1.5f));
            var entry = Static(closed, handle);
            Mutate(closed, sim => sim.Shapes.GetShape<Mesh>(entry.Shape.Index).Tree.Nodes[0].A.Min.Z = MathF.BitIncrement(-2f), handle.Value);
            ref Mesh mesh = ref Sim(closed).Shapes.GetShape<Mesh>(entry.Shape.Index);
            Assert.True(mesh.Tree.Nodes[0].A.Min.Z <= -2f);
            // The aperture's maximum X is exactly the transformed triangle's minimum X.
            // Its closed neighbor must survive both inner-bound proof and pruning.
            Vector3 centre = new(-0.25f, 4, -5), delta = new(0, -4, 0);
            var expected = CertifiedSweepExactOracle.TriangleEntry(closed, handle, 0, centre, 0.25f, 0.5f, delta);
            Assert.Equal((1.5, 1.5), expected);
            HitContains(Read(closed, centre, delta), 1.5, 1.5);
        }
        foreach (Vector3 scale in new[] { new Vector3(0, 1, 1), new(-1, 1, 1), new(float.NaN, 1, 1) })
        {
            using var world = new BepuPhysicsWorld(Vector3.Zero);
            StaticHandle handle = world.AddStatic(CertifiedMeshSweepTests.Quad(), Pose.Identity);
            SetScale(world, handle, scale);
            Refused(Read(world, new(-0.5f, 2, -0.25f), new(0, -4, 0)));
        }
    }

    [Fact]
    public void MeshOperationalEnclosureDoesNotChangePinnedRawTrianglePruning()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle handle = world.AddStatic(CertifiedMeshSweepTests.Quad(), Pose.Identity);
        var entry = Static(world, handle);
        ref Mesh mesh = ref Sim(world).Shapes.GetShape<Mesh>(entry.Shape.Index);
        var link = mesh.Tree.Leaves[0];
        var child = link.ChildIndex == 0 ? mesh.Tree.Nodes[link.NodeIndex].A : mesh.Tree.Nodes[link.NodeIndex].B;
        Assert.Equal(2f, child.Max.X);
        var geometry = ((System.Collections.Generic.Dictionary<int, InstalledSweepGeometry?>)Field(world, "_installedSweepStatics"))[handle.Value]!;
        InstalledSweepGeometry native = geometry.Mesh!.Triangle(0).SweepCompanion!;
        double continuation = (double)native.LeafPose.Position.X + native.Vertex(1).X;
        Assert.True(continuation > 2);
        Assert.True(geometry.Mesh.Bounds(link.NodeIndex, link.ChildIndex).Max.X >= continuation);
        HitContains(Read(world, new(-0.5f, 2, -0.25f), new(0, -4, 0)), 1.25, 1.25);
    }

    [Fact]
    public void FarMeshCannotPoisonClearButInwardInnerBoundsCannotHideGeometry()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle far = world.AddStatic(CertifiedMeshSweepTests.Quad(), Pose.At(new(100, 0, 0)));
        Clear(Read(world, new(-0.5f, 2, -0.25f), new(0, -4, 0)), 4);
        StaticHandle near = world.AddStatic(CertifiedMeshSweepTests.Quad(), Pose.Identity);
        var root = Static(world, near).Shape;
        Mutate(world, sim =>
        {
            ref Mesh mesh = ref sim.Shapes.GetShape<Mesh>(root.Index);
            mesh.Tree.Nodes[0].A.Min.X = MathF.BitIncrement(mesh.Tree.Nodes[0].A.Min.X);
        }, near.Value);
        HitContains(Read(world, new(-0.5f, 2, -0.25f), new(0, -4, 0)), 1.25, 1.25);
        Mutate(world, sim => sim.Shapes.GetShape<Mesh>(root.Index).Tree.Nodes[0].A.Min.X = float.NaN, near.Value);
        Refused(Read(world, new(-0.5f, 2, -0.25f), new(0, -4, 0), exclusions: [near, far]));
    }

    [Fact]
    public void MeshInnerTraversalExhaustionAndLateFailurePublishNoPrefix()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var vertices = new Vector3[4097 * 3];
        var indices = new int[vertices.Length];
        for (int i = 0; i < 4097; i++)
        {
            vertices[i * 3] = new(-2, 0, -2); vertices[i * 3 + 1] = new(2, 0, -2); vertices[i * 3 + 2] = new(-2, 0, 2);
            for (int j = 0; j < 3; j++) indices[i * 3 + j] = i * 3 + j;
        }
        world.AddStatic(new TriangleMeshShape(vertices, indices), Pose.Identity);
        Assert.Equal(default(CapsuleSweepResult), Read(world, new(-0.5f, 0.75f, -0.25f), Vector3.Zero));
        using var bounded = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle boundedHandle = bounded.AddStatic(new TriangleMeshShape(
            [new(-2, 0, -2), new(2, 0, -2), new(-2, 0, 2)], [0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2]), Pose.Identity);
        using (IPhysicsQueryLease read = bounded.AcquireQueryReadLease())
        {
            var native = Static(bounded, boundedHandle);
            Sim(bounded).Statics.GetDescription(native.Handle, out var description);
            var geometry = ((System.Collections.Generic.Dictionary<int, InstalledSweepGeometry?>)Field(bounded, "_installedSweepStatics"))[boundedHandle.Value]!;
            var scratch = new CapsuleSweepScratch();
            var dimensions = new CapsuleQueryDimensions(0.25f, 1);
            Vector3 start = new(-0.5f, 0.75f, -0.25f);
            var complete = new CapsuleSweepWork();
            HitContains(CapsuleMeshSweep.SweepBounded(Sim(bounded).Shapes, native.Shape, description.Pose,
                geometry, dimensions, start, Vector3.Zero, default, ref complete, scratch, 3, 4, 2), 0, 0);
            Assert.Equal(4, complete.Descendants);
            // Valid tree topology, smaller injected ceilings. No malformed-node shortcut.
            foreach (var limits in new[] { (1, 4, 2), (3, 1, 2), (3, 4, 1) })
            {
                var work = new CapsuleSweepWork();
                Assert.Equal(default(CapsuleSweepResult), CapsuleMeshSweep.SweepBounded(Sim(bounded).Shapes,
                    native.Shape, description.Pose, geometry, dimensions, start, Vector3.Zero, default,
                    ref work, scratch, limits.Item1, limits.Item2, limits.Item3));
                Assert.True(work.Exhausted);
            }
            // One primitive remains in the shared 4096-descendant account.
            var shared = new CapsuleSweepWork { Descendants = 4095 };
            Refused(CapsuleMeshSweep.Sweep(Sim(bounded).Shapes, native.Shape, description.Pose, geometry,
                dimensions, start, Vector3.Zero, default, ref shared, scratch));
            Assert.Equal(4096, shared.Descendants);
            Assert.True(shared.Exhausted);
            Assert.False(shared.Primitive());
            Assert.Equal(4096, shared.Descendants);
            read.AssertCurrent();
        }
        using var late = new BepuPhysicsWorld(Vector3.Zero);
        Vector3 contact = new(-0.5f, 0.75f, -0.25f);
        StaticHandle early = late.AddStatic(new BoxShape(new(0.5f)), Pose.At(contact));
        StaticHandle meshHandle = late.AddStatic(CertifiedMeshSweepTests.Quad(), Pose.Identity);
        var shape = Static(late, meshHandle).Shape;
        using IPhysicsQueryLease lease = late.AcquireQueryReadLease();
        HitContains(late.SweepCapsuleCertified(new CapsuleQueryDimensions(0.25f, 1), Pose.At(contact), Vector3.UnitX), 0, 0);
        var candidates = ConcurrentReadGateTests.Gate(late).ScratchPool.Constructed()[0].SweepCandidates.Idle!;
        Assert.Equal(Static(late, early).Handle.Value, candidates[0].StaticHandle.Value);
        ref Mesh mesh = ref Sim(late).Shapes.GetShape<Mesh>(shape.Index);
        BepuPhysics.Collidables.Triangle saved = mesh.Triangles[1];
        mesh.Triangles[1].C = mesh.Triangles[1].B;
        Assert.Equal(default(CapsuleSweepResult), late.SweepCapsuleCertified(new CapsuleQueryDimensions(0.25f, 1), Pose.At(contact), Vector3.UnitX));
        mesh.Triangles[1] = saved;
        HitContains(late.SweepCapsuleCertified(new CapsuleQueryDimensions(0.25f, 1), Pose.At(contact), Vector3.UnitX), 0, 0);
        lease.AssertCurrent();
    }

    [Theory]
    [InlineData(8193, true)]
    [InlineData(8194, false)]
    public void MeshProvedSparseTreeHonoursThe8192ExaminedNodeCeiling(int triangles, bool completes)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle handle = world.AddStatic(RepeatedTriangles(triangles, sparse: true), Pose.Identity);
        var native = Static(world, handle);
        Mutate(world, sim =>
        {
            ref var tree = ref sim.Shapes.GetShape<Mesh>(native.Shape.Index).Tree;
            Assert.Equal(triangles - 1, tree.NodeCount);
            // Deliberately loose, valid inner bounds visit every node. Only one triangle
            // intersects the aperture, proving this is independent of the leaf ceiling.
            for (int i = 0; i < tree.NodeCount; i++)
            {
                if (tree.Nodes[i].A.Index >= 0) tree.Nodes[i].A.Min = Vector3.Min(tree.Nodes[i].A.Min, new(-2, 0, -2));
                if (tree.Nodes[i].B.Index >= 0) tree.Nodes[i].B.Min = Vector3.Min(tree.Nodes[i].B.Min, new(-2, 0, -2));
            }
        }, handle.Value);
        CapsuleSweepResult result = Read(world, new(-0.5f, 0.75f, -0.25f), Vector3.Zero);
        if (completes) HitContains(result, 0, 0);
        else Assert.Equal(default(CapsuleSweepResult), result);
    }

    [Theory]
    [InlineData(129, true)]
    [InlineData(130, false)]
    public void MeshProvedCombTreeHonoursThe128PendingCeiling(int triangles, bool completes)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle handle = world.AddStatic(RepeatedTriangles(triangles), Pose.Identity);
        var native = Static(world, handle);
        Mutate(world, sim =>
        {
            ref var tree = ref sim.Shapes.GetShape<Mesh>(native.Shape.Index).Tree;
            for (int i = 0; i < tree.NodeCount; i++)
            {
                bool last = i == tree.NodeCount - 1;
                int bLeaf = last ? triangles - 1 : i;
                int aIndex = last ? BepuPhysics.Trees.Tree.Encode(triangles - 2) : i + 1;
                tree.Nodes[i].A = new BepuPhysics.Trees.NodeChild
                { Min = new(-2, 0, -2), Max = new(2, 0, 2), Index = aIndex, LeafCount = triangles - i - 1 };
                tree.Nodes[i].B = new BepuPhysics.Trees.NodeChild
                { Min = new(-2, 0, -2), Max = new(2, 0, 2), Index = BepuPhysics.Trees.Tree.Encode(bLeaf), LeafCount = 1 };
                tree.Metanodes[i].Parent = i - 1;
                tree.Metanodes[i].IndexInParent = i == 0 ? -1 : 0;
                tree.Leaves[bLeaf] = new BepuPhysics.Trees.Leaf(i, 1);
                if (last) tree.Leaves[triangles - 2] = new BepuPhysics.Trees.Leaf(i, 0);
            }
        }, handle.Value);
        CapsuleSweepResult result = Read(world, new(-0.5f, 0.75f, -0.25f), Vector3.Zero);
        if (completes) HitContains(result, 0, 0);
        else Assert.Equal(default(CapsuleSweepResult), result);
    }

    [Fact]
    public void MeshExactly4096RelevantTrianglesComplete()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(RepeatedTriangles(4096), Pose.Identity);
        HitContains(Read(world, new(-0.5f, 0.75f, -0.25f), Vector3.Zero), 0, 0);
    }

    static TriangleMeshShape RepeatedTriangles(int count, bool sparse = false)
    {
        var vertices = new Vector3[count * 3]; var indices = new int[count * 3];
        for (int i = 0; i < count; i++)
        {
            float x = sparse && i != 0 ? 100 : 0;
            vertices[i * 3] = new(x - 2, 0, -2); vertices[i * 3 + 1] = new(x + 2, 0, -2); vertices[i * 3 + 2] = new(x - 2, 0, 2);
            for (int j = 0; j < 3; j++) indices[i * 3 + j] = i * 3 + j;
        }
        return new(vertices, indices);
    }

    [Fact]
    public void MeshRecenteredOperationalOffsetBoundCoversCancellation()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var shape = new TriangleMeshShape([new(1022, 0, -2), new(1026, 0, -2), new(1022, 0, 2)], [0, 1, 2]);
        var pose = new Pose(new(768, 256, -256), new(0, 0, 0.5f, 0.8660254f));
        StaticHandle handle = world.AddStatic(shape, pose);
        using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
        var geometry = ((System.Collections.Generic.Dictionary<int, InstalledSweepGeometry?>)Field(world, "_installedSweepStatics"))[handle.Value]!;
        InstalledSweepGeometry native = geometry.Mesh!.Triangle(0).SweepCompanion!;
        var root = new BepuPhysics.RigidPose(pose.Position, pose.Orientation);
        Assert.True(native.TryPose(root, out var map));
        Vector3 centre = native.LeafPose.Position + new Vector3(0.03125f);
        var leaf = new InstalledSweepLeaf(native, native.LeafPose, map);
        Assert.True(InstalledSweepBounds.TryOperationalError(leaf, centre, Vector3.Zero, 0.5f, out double error));
        var q = new BepuUtilities.QuaternionWide
        { X = new(pose.Orientation.X), Y = new(pose.Orientation.Y), Z = new(pose.Orientation.Z), W = new(pose.Orientation.W) };
        BepuUtilities.Vector3Wide.Broadcast(native.LocalPose.Position, out var offset);
        BepuUtilities.QuaternionWide.TransformWithoutOverlap(offset, q, out var rotated);
        Vector3 sourceOffset = (root.Position - centre) + new Vector3(rotated.X[0], rotated.Y[0], rotated.Z[0]);
        BepuUtilities.Vector3Wide.Broadcast(-sourceOffset, out var relative);
        BepuUtilities.QuaternionWide.Conjugate(q, out var inverse);
        BepuUtilities.QuaternionWide.TransformWithoutOverlap(relative, inverse, out var actual);
        double Model(FeaturePoint row) => ((double)centre.X - native.LeafPose.Position.X) * row.X.Value +
            ((double)centre.Y - native.LeafPose.Position.Y) * row.Y.Value + ((double)centre.Z - native.LeafPose.Position.Z) * row.Z.Value;
        double departure = Math.Max(Math.Abs(Model(map.RepresentedX) - actual.X[0]),
            Math.Max(Math.Abs(Model(map.RepresentedY) - actual.Y[0]), Math.Abs(Model(map.RepresentedZ) - actual.Z[0])));
        Assert.True(departure > 0.00001, $"The fixture must exercise intermediate cancellation. departure={departure}");
        Assert.True(departure <= error, $"source departure={departure} proved bound={error}");
        lease.AssertCurrent();
    }

    internal static void SetScale(BepuPhysicsWorld world, StaticHandle handle, Vector3 scale)
    {
        var native = Static(world, handle);
        Mutate(world, sim =>
        {
            sim.Shapes.GetShape<Mesh>(native.Shape.Index).Scale = scale;
            CapsuleSweepGeometry.TryCapture(sim.Shapes, native.Shape, out var captured);
            ((System.Collections.Generic.Dictionary<int, InstalledSweepGeometry?>)Field(world, "_installedSweepStatics"))[handle.Value] = captured;
        }, handle.Value);
    }
}
