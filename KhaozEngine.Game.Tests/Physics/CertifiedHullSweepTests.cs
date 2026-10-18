using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using BepuPhysics.Collidables;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using static KhaozEngine.Tests.Physics.CertifiedGeneralSweepFixture;

namespace KhaozEngine.Tests.Physics;

[Collection("AllocSensitive")]
public sealed class CertifiedHullSweepTests
{
    internal static ConvexHullShape Tetrahedron() => new([Vector3.Zero, new(2, 0, 0), new(0, 2, 0), new(0, 0, 2)]);

    [Fact]
    public void HullTetrahedronEmptyAabbCornerIsClear()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(Tetrahedron(), Pose.Identity);
        Clear(Read(world, new(1.5f), new(0.125f, 0, 0), length: 0), 0.125f);
    }

    [Fact]
    public void HullFaceEdgeVertexAndInteriorUseInstalledTopology()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle handle = world.AddStatic(Tetrahedron(), Pose.Identity);
        Vector3 centre = new(-2, 0.75f, 0.25f), delta = new(4, 0, 0);
        var expected = CertifiedSweepExactOracle.HullEntry(world, handle, centre, 0.25f, 0.5f, delta);
        Assert.InRange(expected.Lower, 1.749999999999, 1.75);
        Assert.InRange(expected.Upper, 1.75, 1.750000000001);
        CapsuleSweepResult face = Read(world, centre, delta);
        HitContains(face, 1.75, 1.75);
        foreach (Vector3 point in new[] { new Vector3(1, -0.25f, 0), new(-0.25f, 0, 0), new(0.25f) })
            HitContains(Read(world, point, Vector3.Zero, length: 0), 0, 0);
    }

    [Fact]
    public void HullIsolatedVertexTangencyAtOneThirdCannotBeSampledAway()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var hull = new ConvexHullShape([Vector3.Zero, new(2, 2, 0), new(2, 0, 2), new(2, 2, 2)]);
        StaticHandle handle = world.AddStatic(hull, Pose.Identity);
        Vector3 centre = new(-1, -0.25f, 0), delta = new(3, 0, 0);
        var expected = CertifiedSweepExactOracle.HullEntry(world, handle, centre, 0.25f, 0, delta);
        Assert.InRange(expected.Lower, 0.999999999999, 1);
        Assert.InRange(expected.Upper, 1, 1.000000000001);
        CapsuleSweepResult isolatedTangent = Read(world, centre, delta, length: 0);
        HitContains(isolatedTangent, 1, 1);
    }

    [Fact]
    public void HullWrapperAndReusedHandleKeepInstalledVertexIdentity()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        Vector3 translation = new(3, 2, -4);
        StaticHandle first = world.AddStatic(Tetrahedron(), Pose.At(translation));
        var native = Static(world, first);
        ref Compound wrapper = ref Sim(world).Shapes.GetShape<Compound>(native.Shape.Index);
        Assert.Equal(1, wrapper.Children.Length);
        Assert.Equal(new Vector3(0.5f), wrapper.Children[0].LocalPose.Position);
        Assert.Equal(Quaternion.Identity, wrapper.Children[0].LocalPose.Orientation);
        Assert.Equal(default(ConvexHull).TypeId, wrapper.Children[0].ShapeIndex.Type);
        int[] expectedInstalledVertexBits = InstalledVertexBits(world, first);
        var captures = (Dictionary<int, InstalledSweepGeometry?>)Field(world, "_installedSweepStatics");
        Assert.NotNull(captures[first.Value]);
        InstalledSweepGeometry previous = captures[first.Value]!;
        HitContains(Read(world, translation + new Vector3(-2, 0.75f, 0.25f), new(4, 0, 0)), 1.75, 1.75);
        int[] observedInstalledVertexBits = CapturedVertexBits(captures[first.Value]!);
        Assert.Equal(expectedInstalledVertexBits, observedInstalledVertexBits);
        world.RemoveStatic(first);
        var replacementShape = new ConvexHullShape([new(0.5f, 0, 0), new(3.5f, 0, 0), new(0.5f, 2, 0), new(0.5f, 0, 2)]);
        StaticHandle replacement = world.AddStatic(replacementShape, Pose.At(translation));
        var reused = Static(world, replacement);
        Assert.Equal(native.Handle.Value, reused.Handle.Value);
        Assert.Equal(native.Shape.Index, reused.Shape.Index);
        Assert.False(captures.ContainsKey(first.Value));
        Assert.NotEqual(expectedInstalledVertexBits, InstalledVertexBits(world, replacement));
        Assert.Equal(InstalledVertexBits(world, replacement), CapturedVertexBits(captures[replacement.Value]!));
        Vector3 start = translation + new Vector3(-2, 0.75f, 0.25f), delta = new(4, 0, 0);
        var expected = CertifiedSweepExactOracle.HullEntry(world, replacement, start, 0.25f, 0.5f, delta);
        HitContains(Read(world, start, delta), expected.Lower, expected.Upper);
        HitContains(Read(world, start, delta), 2.25, 2.25);
        InstalledSweepGeometry current = captures[replacement.Value]!;
        captures[replacement.Value] = previous;
        Refused(Read(world, start, delta));
        captures[replacement.Value] = current;
    }

    [Fact]
    public void FarHullAndMobilityExclusionsNoLongerPoisonClear()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle far = world.AddStatic(Tetrahedron(), Pose.At(new(100, 0, 0)));
        Vector3 start = new(-2, 0.75f, 0.25f), delta = new(4, 0, 0);
        Clear(Read(world, start, delta), 4);
        world.AddDynamic(Tetrahedron(), Pose.Identity, new DynamicBodyDescription(1));
        HitContains(Read(world, start, delta, filter: QueryFilter.DynamicsOnly), 1.75, 1.75);
        Clear(Read(world, start, delta, filter: QueryFilter.StaticsOnly), 4);
        StaticHandle near = world.AddStatic(Tetrahedron(), Pose.Identity);
        using var excluded = world.CreateQueryViewExcludingStatics([near, far]);
        using var included = world.CreateQueryViewExcludingStatics([]);
        using IPhysicsQueryLease lease = ((IPhysicsQueryLeaseSource)excluded).AcquireQueryReadLease();
        var dimensions = new CapsuleQueryDimensions(0.25f, 1);
        Clear(((IPhysicsCapsuleGeometryQueries)excluded).SweepCapsuleCertified(dimensions, Pose.At(start), delta, QueryFilter.StaticsOnly), 4);
        HitContains(((IPhysicsCapsuleGeometryQueries)included).SweepCapsuleCertified(dimensions, Pose.At(start), delta, QueryFilter.StaticsOnly), 1.75, 1.75);
        lease.AssertCurrent();
    }

    [Fact]
    public void HullUncertainTopologyAndLateUnresolvedLeafRefuseAtomically()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle early = world.AddStatic(new BoxShape(new(0.5f)), Pose.Identity);
        StaticHandle handle = world.AddStatic(Tetrahedron(), Pose.Identity);
        using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
        CapsuleSweepResult Query() => world.SweepCapsuleCertified(new CapsuleQueryDimensions(0.25f, 1), Pose.Identity, Vector3.UnitX);
        HitContains(Query(), 0, 0);
        var candidates = ConcurrentReadGateTests.Gate(world).ScratchPool.Constructed()[0].SweepCandidates.Idle!;
        Assert.Equal(2, candidates.Count);
        Assert.Equal(Static(world, early).Handle.Value, candidates[0].StaticHandle.Value);
        var native = Static(world, handle);
        ref Compound compound = ref Sim(world).Shapes.GetShape<Compound>(native.Shape.Index);
        ref ConvexHull hull = ref Sim(world).Shapes.GetShape<ConvexHull>(compound.Children[0].ShapeIndex.Index);
        HullVertexIndex saved = hull.FaceVertexIndices[0];
        hull.FaceVertexIndices[0] = new HullVertexIndex { BundleIndex = ushort.MaxValue, InnerIndex = ushort.MaxValue };
        CapsuleSweepResult malformed = Query();
        Assert.Equal(default(CapsuleSweepResult), malformed);
        hull.FaceVertexIndices[0] = saved;
        HitContains(Query(), 0, 0);
        var captures = (Dictionary<int, InstalledSweepGeometry?>)Field(world, "_installedSweepStatics");
        InstalledSweepGeometry geometry = captures[handle.Value]!;
        FieldInfo mapField = typeof(InstalledSweepGeometry).GetField("_operator", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object savedMap = mapField.GetValue(geometry)!;
        object uncertain = mapField.GetValue(geometry)!;
        typeof(InstalledPoseOperator).GetField("<X>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(uncertain, new FeaturePoint(FeatureNumber.Enclosed(GeometryInterval.Enclose(-1, 1)), FeatureNumber.Exact(0), FeatureNumber.Exact(0)));
        mapField.SetValue(geometry, uncertain);
        Assert.Equal(default(CapsuleSweepResult), Query());
        object overflowing = mapField.GetValue(geometry)!;
        typeof(InstalledPoseOperator).GetField("<X>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(overflowing, new FeaturePoint(FeatureNumber.Exact(double.Epsilon), FeatureNumber.Exact(0), FeatureNumber.Exact(0)));
        mapField.SetValue(geometry, overflowing);
        Assert.Equal(default(CapsuleSweepResult), Query());
        SweepExactWorkspace failed = ConcurrentReadGateTests.Gate(world).ScratchPool.Constructed()[0].GeneralSweeps.Idle!.Exact;
        Assert.False(failed.IsResolved);
        Assert.InRange(failed.Checkpoint, 1, SweepExactWorkspace.MaximumNumbers - 1);
        mapField.SetValue(geometry, savedMap);
        HitContains(Query(), 0, 0);
        lease.AssertCurrent();
    }

    [Fact]
    public void HullBareLeafAndCentroidRootShareTheInstalledSolid()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle handle = world.AddStatic(Tetrahedron(), Pose.Identity);
        using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
        var native = Static(world, handle);
        var shapes = Sim(world).Shapes;
        ref Compound wrapper = ref shapes.GetShape<Compound>(native.Shape.Index);
        var child = wrapper.Children[0];
        Assert.True(CapsuleSweepGeometry.TryCaptureHull(shapes, child.ShapeIndex, out var bare));
        Assert.True(InstalledPoseOperator.TryCreate(child.LocalPose, out var map));
        bare!.PublishPose(child.LocalPose, child.LocalPose, map);
        Assert.True(CapsuleSweepGeometry.TryReadLeaf(shapes, child.ShapeIndex, child.LocalPose, bare, 0, out var leaf));
        var work = new CapsuleSweepWork();
        HitContains(CapsuleConvexSweep.Sweep(leaf, new(-2, 0.75f, 0.25f), 0.25f, 0.5f,
            new(4, 0, 0), 0.001f, ref work, new CapsuleSweepScratch()), 1.75, 1.75);
        Assert.InRange(work.Cells, 1, 256);
        Assert.InRange(work.Features, 1, 4096);
        lease.AssertCurrent();
    }

    [Fact]
    public void HullRootBoundsCoverTheCentroidPoseAndRepairInwardNeighbours()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var asymmetric = new ConvexHullShape([Vector3.Zero, new(4, 0, 0), new(0, 2, 0), new(0, 0, 1)]);
        var pose = new Pose(new(3, 2, -4), new(0.5f, 0.5f, 0.5f, 0.5f));
        StaticHandle handle = world.AddStatic(asymmetric, pose);
        var native = Static(world, handle);
        var geometry = ((Dictionary<int, InstalledSweepGeometry?>)Field(world, "_installedSweepStatics"))[handle.Value]!;
        Sim(world).Statics.GetDescription(native.Handle, out var installed);
        Assert.True(InstalledSweepBounds.TryCreate(geometry, installed.Pose, out Vector3 requiredMin, out Vector3 requiredMax));
        Mutate(world, simulation =>
        {
            int index = simulation.Statics[simulation.Statics.HandleToIndex[native.Handle.Value]].BroadPhaseIndex;
            simulation.BroadPhase.UpdateStaticBounds(index, new(MathF.BitIncrement(requiredMin.X), requiredMin.Y, requiredMin.Z), requiredMax);
        }, handle.Value);
        int broad = Sim(world).Statics[Sim(world).Statics.HandleToIndex[native.Handle.Value]].BroadPhaseIndex;
        var tree = Sim(world).BroadPhase.StaticTree;
        var stored = tree.Leaves[broad];
        var node = tree.Nodes[stored.NodeIndex];
        var bound = stored.ChildIndex == 0 ? node.A : node.B;
        Assert.True(bound.Min.X <= requiredMin.X);
        Vector3 centre = pose.Position + new Vector3(-2, 0.75f, 0.25f), delta = new(4, 0, 0);
        var expected = CertifiedSweepExactOracle.HullEntry(world, handle, centre, 0.25f, 0, delta);
        HitContains(Read(world, centre, delta, length: 0), expected.Lower, expected.Upper);
        Clear(Read(world, new(-10), Vector3.UnitX, length: 0), 1);
    }

    [Fact]
    public void HullMalformedCaptureAndGeneralCompoundsRemainRefused()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle handle = world.AddStatic(Tetrahedron(), Pose.Identity);
        var native = Static(world, handle);
        ref Compound wrapper = ref Sim(world).Shapes.GetShape<Compound>(native.Shape.Index);
        ref ConvexHull hull = ref Sim(world).Shapes.GetShape<ConvexHull>(wrapper.Children[0].ShapeIndex.Index);
        int saved = hull.FaceToVertexIndicesStart[0];
        hull.FaceToVertexIndicesStart[0] = -1;
        Assert.False(CapsuleSweepGeometry.TryCaptureHull(Sim(world).Shapes, native.Shape, out _));
        hull.FaceToVertexIndicesStart[0] = saved;
        HullVertexIndex savedIndex = hull.FaceVertexIndices[0];
        hull.FaceVertexIndices[0] = hull.FaceVertexIndices[1];
        Assert.False(CapsuleSweepGeometry.TryCaptureHull(Sim(world).Shapes, native.Shape, out _));
        hull.FaceVertexIndices[0] = savedIndex;
        Assert.True(CapsuleSweepGeometry.TryCaptureHull(Sim(world).Shapes, native.Shape, out _));
        world.AddStatic(new CompoundShape([new(Tetrahedron(), Pose.Identity), new(new BoxShape(new(0.5f)), Pose.At(new(3, 0, 0)))]), Pose.At(new(100, 0, 0)));
        Refused(Read(world, new(-2, 0.75f, 0.25f), new(4, 0, 0)));
    }

    [Fact]
    public void HullPolygonFacesAndNonRigidPoseKeepFiniteFeatureDistance()
    {
        var points = new Vector3[8];
        for (int i = 0; i < points.Length; i++)
            points[i] = new((i & 1) == 0 ? -0.5f : 0.5f, (i & 2) == 0 ? -0.25f : 0.25f, (i & 4) == 0 ? -0.125f : 0.125f);
        using var identity = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle handle = identity.AddStatic(new ConvexHullShape(points), Pose.Identity);
        var geometry = ((Dictionary<int, InstalledSweepGeometry?>)Field(identity, "_installedSweepStatics"))[handle.Value]!;
        Assert.Equal(6, geometry.FaceCount);
        Assert.Equal(8, geometry.VertexCount);
        Assert.Equal(12, geometry.EdgeCount);
        Assert.Equal(6, geometry.SurfaceCount);
        HitContains(Read(identity, new(-2, 0, 0), new(4, 0, 0)), 1.25, 1.25);
        Clear(Read(identity, new(0.6875f, 0.4375f, 0), new(0, 0, 0.0625f), length: 0), 0.0625f);
        using var tilted = new BepuPhysicsWorld(Vector3.Zero);
        var pose = new Pose(new(0.28660256f, 0.2f, 0), new(0, 0, 0.5f, 0.8660254f));
        StaticHandle rotated = tilted.AddStatic(new ConvexHullShape(points), pose);
        foreach (float length in new[] { 0f, 0.6f })
        {
            Vector3 centre = pose.Position + Vector3.Transform(new Vector3(0.0625f, 0.03125f, -1), pose.Orientation);
            Vector3 delta = new(0, 0, 2);
            var expected = CertifiedSweepExactOracle.HullEntry(tilted, rotated, centre, 0.0625f, length * 0.5f, delta);
            using (IPhysicsQueryLease lease = tilted.AcquireQueryReadLease())
            {
                var installed = Static(tilted, rotated);
                Sim(tilted).Statics.GetDescription(installed.Handle, out var description);
                var captured = ((Dictionary<int, InstalledSweepGeometry?>)Field(tilted, "_installedSweepStatics"))[rotated.Value]!;
                Assert.True(CapsuleSweepGeometry.TryReadLeaf(Sim(tilted).Shapes, installed.Shape, description.Pose, captured, 0, out var leaf));
                var work = new CapsuleSweepWork();
                var scratch = new CapsuleSweepScratch();
                var kernel = CapsuleConvexSweep.Sweep(leaf, centre, 0.0625f, length * 0.5f, delta, 0.001f, ref work, scratch);
                Assert.True(kernel.IsComplete, $"length={length} features={work.Features} cells={work.Cells} exhausted={work.Exhausted} exact={scratch.Exact.IsResolved} numbers={scratch.Exact.Checkpoint}");
            }
            HitContains(Read(tilted, centre, delta, 0.0625f, length), expected.Lower, expected.Upper);
        }
    }

    static int[] CapturedVertexBits(InstalledSweepGeometry geometry)
    {
        var bits = new List<int>();
        for (int face = 0; face < geometry.FaceCount; face++)
            for (int i = 0; i < geometry.InteriorFaceSize(face); i++)
            {
                Vector3 point = geometry.Vertex(geometry.InteriorFaceVertex(face, i));
                bits.Add(BitConverter.SingleToInt32Bits(point.X));
                bits.Add(BitConverter.SingleToInt32Bits(point.Y));
                bits.Add(BitConverter.SingleToInt32Bits(point.Z));
            }
        return bits.ToArray();
    }

    [Fact]
    public void HullClosedPathAndTangentialMotionUseSharedContract()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(Tetrahedron(), Pose.Identity);
        foreach (Vector3 delta in new[] { Vector3.Zero, new Vector3(0, 0.125f, 0), new(-0.125f, 0, 0) })
        {
            HitContains(Read(world, new(-0.25f, 0.5f, 0.5f), delta, length: 0), 0, 0);
            HitContains(Read(world, new(-0.25f, 0.5f, 0.5f), delta, length: 0, filter: QueryFilter.All with { CullBackFaces = true }), 0, 0);
        }
        Clear(Read(world, new(MathF.BitDecrement(-0.25f), 0.5f, 0.5f), new(0, 0.125f, 0), length: 0), 0.125f);
        HitContains(Read(world, new(-1.25f, 0.5f, 0.5f), Vector3.UnitX, length: 0), 1, 1);
    }

    internal static int[] InstalledVertexBits(BepuPhysicsWorld world, StaticHandle handle)
    {
        var native = Static(world, handle);
        ref Compound compound = ref Sim(world).Shapes.GetShape<Compound>(native.Shape.Index);
        ref ConvexHull hull = ref Sim(world).Shapes.GetShape<ConvexHull>(compound.Children[0].ShapeIndex.Index);
        var bits = new List<int>();
        for (int i = 0; i < hull.FaceVertexIndices.Length; i++)
        {
            hull.GetPoint(hull.FaceVertexIndices[i], out Vector3 point);
            bits.Add(BitConverter.SingleToInt32Bits(point.X));
            bits.Add(BitConverter.SingleToInt32Bits(point.Y));
            bits.Add(BitConverter.SingleToInt32Bits(point.Z));
        }
        return bits.ToArray();
    }
}
