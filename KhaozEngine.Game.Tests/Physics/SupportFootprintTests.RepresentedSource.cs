using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using BepuPhysics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using static KhaozEngine.Tests.Physics.InstalledPoseOracle;
using StaticHandle = KhaozEngine.Physics.StaticHandle;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    [Fact]
    public void RepresentedVertexDiscKeepsClosedBoundaryAndAdjacentRadii()
    {
        Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.65f);
        M represented = InstalledPoseOracle.Represented(rotation);
        float radius = represented.X.X.AsSingle();
        Assert.Equal(0.7960838079452515, (double)radius);
        Assert.True(InstalledPoseOperator.TryCreate(new RigidPose(Vector3.Zero, rotation), out var old));
        // Evaluate the old transformed-vertex enclosure's endpoints as rationals. The coefficient box
        // reaches equality, while its affine arithmetic enclosure straddles membership independently.
        R radiusSquared = R.From(radius).Square();
        Assert.True(R.From(old.X.X.Bounds.Lower).Square() - radiusSquared < R.Zero);
        Assert.Equal(R.Zero, R.From(old.X.X.Bounds.Upper).Square() - radiusSquared);
        FeaturePoint oldVertex = old.Point(Vector3.UnitX, exactZeroProducts: true);
        Assert.True(R.From(oldVertex.X.Bounds.Lower).Square() - radiusSquared < R.Zero);
        Assert.True(R.From(oldVertex.X.Bounds.Upper).Square() - radiusSquared > R.Zero);
        using var scene = new SupportFootprintScene();
        Vector3[] local = [new(1, 0, 0), new(1.125f, 0, 0.125f), new(1.125f, 0, -0.125f)];
        scene.Install(new TriangleMeshShape(local, [0, 2, 1]), new(Vector3.Zero, rotation));
        using var lease = scene.World.AcquireQueryReadLease();
        float[] radii = [radius, MathF.BitDecrement(radius), MathF.BitIncrement(radius)];
        var membership = new GeometrySign[3];
        for (int i = 0; i < radii.Length; i++)
        {
            float r = radii[i];
            var request = Request(radius: r, upper: 1);
            SupportPolygon polygon = Assert.Single(CapturedMesh(scene, request));
            GeometrySign actual = polygon.SourcePredicates.VertexDisc(polygon, 0, FeaturePoint.Exact(Vector3.Zero), FeatureNumber.Exact(r));
            V vertex = represented.Apply(V.From(local[0]));
            R exactMembership = vertex.X.Square() + vertex.Z.Square() - R.From(r).Square();
            GeometrySign exactSign = exactMembership == R.Zero ? GeometrySign.Zero : exactMembership > R.Zero ? GeometrySign.Positive : GeometrySign.Negative;
            Assert.Equal(exactSign, actual);
            membership[i] = actual;
            var members = MemberSentinels(256);
            var joins = Enumerable.Repeat(73UL, 1024).ToArray();
            var before = (SupportFootprintMember[])members.Clone();
            var result = Capability(scene.World).QuerySupportFootprint(lease, request, members, joins);
            Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
            var member = Assert.Single(members.Take(result.Members));
            Assert.Equal(r >= radius, member.HasDiscMaximum);
            SupportFootprintMember[] beforeUnusedMembers = before.Skip(result.Members).ToArray();
            SupportFootprintMember[] afterUnusedMembers = members.Skip(result.Members).ToArray();
            ulong[] beforeUnusedJoins = Enumerable.Repeat(73UL, joins.Length - result.Members * result.JoinWordsPerRow).ToArray();
            ulong[] afterUnusedJoins = joins.Skip(result.Members * result.JoinWordsPerRow).ToArray();
            Assert.Equal(beforeUnusedMembers, afterUnusedMembers);
            Assert.Equal(beforeUnusedJoins, afterUnusedJoins);
        }
        GeometrySign boundarySign = membership[0], outsideRadiusMembership = membership[1], insideRadiusMembership = membership[2];
        Assert.Equal(GeometrySign.Zero, boundarySign);
        Assert.Equal(GeometrySign.Positive, outsideRadiusMembership);
        Assert.Equal(GeometrySign.Negative, insideRadiusMembership);
    }

    [Fact]
    public void RepresentedSourceMapRejectsDualInputAndUsesTwelveBitPatterns()
    {
        var pose = new RigidPose(Vector3.Zero, Quaternion.CreateFromYawPitchRoll(0.37f, 0.1f, -0.05f));
        Assert.True(InstalledPoseOperator.TryCreate(pose, out var dual));
        Assert.False(dual.X.IsExact && dual.Y.IsExact && dual.Z.IsExact);
        var polygon = new SupportPolygon();
        polygon.Begin(new(1), 0, 3);
        Assert.Throws<InvalidOperationException>(() => polygon.BeginSource(dual));
        Assert.False(polygon.HasSource);
        var constructor = typeof(InstalledPoseOperator).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(FeaturePoint), typeof(FeaturePoint), typeof(FeaturePoint), typeof(Vector3),
                typeof(FeaturePoint), typeof(FeaturePoint), typeof(FeaturePoint)], null)!;
        FeaturePoint x = FeaturePoint.Exact(Vector3.UnitX), y = FeaturePoint.Exact(Vector3.UnitY), z = FeaturePoint.Exact(Vector3.UnitZ);
        var mismatched = (InstalledPoseOperator)constructor.Invoke([x, y, z, Vector3.Zero, y, x, z]);
        Assert.True(mismatched.IsResolved);
        Assert.Throws<InvalidOperationException>(() => polygon.BeginSource(mismatched));
        var reversed = (InstalledPoseOperator)constructor.Invoke([FeaturePoint.Exact(-Vector3.UnitX), y, z, Vector3.Zero, x, y, z]);
        Assert.False(reversed.IsResolved);
        Assert.Throws<InvalidOperationException>(() => polygon.BeginSource(reversed));
        var singular = (InstalledPoseOperator)constructor.Invoke([FeaturePoint.Exact(Vector3.Zero), y, z, Vector3.Zero, x, y, z]);
        Assert.False(singular.IsResolved);
        Assert.Throws<InvalidOperationException>(() => polygon.BeginSource(singular));
        Assert.True(InstalledPoseOperator.TryCreateSupport(pose, out var map));
        polygon.BeginSource(map);
        Vector3[] local = [new(-1, 0, -1), new(-1, 0, 1), new(1, 0, 1)];
        for (int i = 0; i < 3; i++)
        {
            polygon.SetSource(i, local[i]);
            polygon.Set(i, map.Point(local[i]), map.Edge(local[i], local[(i + 1) % 3]));
        }
        polygon.Finish(FeaturePoint.Cross(map.Edge(local[0], local[1]), map.Edge(local[0], local[2])));
        Assert.Equal(GeometrySign.Positive, polygon.SourcePredicates.NormalComponent(polygon, 1));
        var constants = (SupportSourcePolynomial[])typeof(SupportFootprintSourcePredicates)
            .GetField("_map", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(polygon.SourcePredicates)!;
        Assert.Equal(9, constants.Length);
        Assert.All(constants, p => Assert.Equal(0, p.Degree));
        var identity = new SupportPolygonSourceMap(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, Vector3.Zero);
        var comparer = SupportSourceMapComparer.Instance;
        for (int i = 0; i < 12; i++)
        {
            float[] components = [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0];
            components[i] = components[i] == 0 ? -0f : MathF.BitIncrement(components[i]);
            var changed = new SupportPolygonSourceMap(new(components[0], components[1], components[2]),
                new(components[3], components[4], components[5]), new(components[6], components[7], components[8]),
                new(components[9], components[10], components[11]));
            Assert.False(comparer.Equals(identity, changed));
            Assert.NotEqual(comparer.GetHashCode(identity), comparer.GetHashCode(changed));
        }
        using var scene = new SupportFootprintScene();
        PhysicsShape triangle = new TriangleMeshShape([new(-1, 0, -1), new(1, 0, -1), new(0, 0, 1)], [0, 1, 2]);
        StaticHandle first = scene.Install(triangle, Pose.Identity), second = scene.Install(triangle, Pose.Identity);
        using var lease = scene.World.AcquireQueryReadLease();
        var members = new SupportFootprintMember[256];
        var joins = new ulong[1024];
        var result = Capability(scene.World).QuerySupportFootprint(lease, Request(), members, joins);
        Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
        Assert.Equal(new[] { first, second }, members.Take(result.Members).Select(m => m.Static));
    }

    [Fact]
    public void RepresentedMeshPoseMissUsesItsActualInverse()
    {
        using var counters = new WorldFormCounters();
        using var scene = new SupportFootprintScene();
        var pose = new Pose(new(0, 0.125f, 0), Quaternion.CreateFromYawPitchRoll(0.37f, 0.1f, -0.05f));
        StaticHandle owner = scene.Install(new TriangleMeshShape([new(-2, 0, -2), new(2, 0, -2), new(-2, 0, 2), new(2, 0, 2)],
            [0, 1, 2, 1, 3, 2]), pose);
        var request = Request();
        SupportQueryCounts firstCounts = Check();
        object first = MeshForm(MeshForms(scene.World), owner, 0);
        Assert.True(firstCounts.WorldFormBuilds > 0);
        SupportQueryDiagnostics.Reset();
        SupportQueryCounts hitCounts = Check();
        Assert.Equal(0, hitCounts.WorldFormBuilds);
        Assert.Equal(0, hitCounts.MeshTriangleCaptures);
        var sim = SimulationOf(scene.World);
        var handle = HandlesOf(scene.World)[owner.Value].Handle;
        sim.Statics.GetDescription(handle, out var description);
        pose = pose with { Position = new(0.03125f, 0.125f, -0.03125f), Orientation = Quaternion.CreateFromYawPitchRoll(0.31f, 0.13f, -0.07f) };
        description.Pose = new(pose.Position, pose.Orientation);
        sim.Statics.ApplyDescription(handle, description);
        scene.Capture(owner);
        SupportQueryDiagnostics.Reset();
        SupportQueryCounts missCounts = Check();
        Assert.Equal(0, missCounts.WorldFormBuilds);
        Assert.True(missCounts.MeshTriangleCaptures > 0);
        Assert.Same(first, MeshForm(MeshForms(scene.World), owner, 0));

        SupportQueryCounts Check()
        {
            using var lease = scene.World.AcquireQueryReadLease();
            var members = new SupportFootprintMember[256];
            var joins = new ulong[1024];
            var result = Capability(scene.World).QuerySupportFootprint(lease, request, members, joins);
            SupportQueryCounts captured = SupportQueryDiagnostics.Snapshot();
            CheckQueryOracle(scene, request, result, members, joins);
            Assert.Equal(new[] { 0, 1 }, members.Take(result.Members).Select(m => m.ElementId));
            string optimized = Describe(result, members, joins);
            using (SupportReferenceEvaluation.Begin())
            {
                var reference = Capability(scene.World).QuerySupportFootprint(lease, request, members, joins);
                Assert.Equal(optimized, Describe(reference, members, joins));
            }
            Assert.True(InstalledPoseOperator.TryCreateSupport(new RigidPose(pose.Position, pose.Orientation), out var map));
            M exact = InstalledPoseOracle.Represented(pose.Orientation);
            Assert.True(SupportFootprintBounds.TryCreate(request, out var bounds));
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = new((i & 1) == 0 ? bounds.Minimum.X : bounds.Maximum.X,
                    (i & 2) == 0 ? bounds.Minimum.Y : bounds.Maximum.Y, (i & 4) == 0 ? bounds.Minimum.Z : bounds.Maximum.Z);
                FeaturePoint offset = FeaturePoint.Subtract(FeaturePoint.Exact(corner), map.Translation);
                var actual = new GeometryVector(FeaturePoint.Dot(map.InverseTransposeX, offset).Bounds,
                    FeaturePoint.Dot(map.InverseTransposeY, offset).Bounds, FeaturePoint.Dot(map.InverseTransposeZ, offset).Bounds);
                V expected = exact.Inverse(V.From(corner) - V.From(pose.Position));
                InstalledSupportPoseTests.Encloses(actual, expected);
                Assert.NotEqual(expected, exact.Transpose.Apply(V.From(corner) - V.From(pose.Position)));
            }
            foreach (SupportPolygon polygon in CapturedMesh(scene, request))
            {
                V[] vertices = SupportFootprintExactOracle.Vertices(scene.Faces.Single(f => f.ElementId == polygon.ElementId));
                InstalledSupportPoseTests.Encloses(polygon.Normal.Bounds, V.Cross(vertices[1] - vertices[0], vertices[2] - vertices[0]));
            }
            return captured;
        }
        string Describe(SupportFootprintResult r, SupportFootprintMember[] m, ulong[] j) =>
            InstalledSupportPoseTests.Raw(r, scene.World, r.Lease!) + " " +
            string.Join(" ", m.Take(r.Members).Select(v => InstalledSupportPoseTests.Raw(v, scene.World, r.Lease!))) + " " +
            string.Join(" ", j.Take(r.Members * r.JoinWordsPerRow));
    }
}
