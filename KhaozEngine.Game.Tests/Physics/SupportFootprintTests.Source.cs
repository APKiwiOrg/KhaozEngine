using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BepuPhysics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Locomotion.Contacts;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    [Fact]
    public void SourceExpansionKeepsSubnormalDegreeEightTerms()
    {
        var value = new SupportSourceCoefficient();
        value.Reset();
        value.AddScalar(float.Epsilon);
        var square = new SupportSourceCoefficient();
        square.Reset();
        square.AddProduct(value, value);
        value = square;
        square = new SupportSourceCoefficient();
        square.Reset();
        square.AddProduct(value, value);
        value = square;
        square = new SupportSourceCoefficient();
        square.Reset();
        square.AddProduct(value, value);
        value = square;
        Assert.Equal(GeometrySign.Positive, value.Sign);
        var mixed = new SupportSourceCoefficient();
        mixed.Reset();
        mixed.AddScalar(1);
        mixed.Add(value, 1);
        mixed.AddScalar(-1);
        Assert.Equal(GeometrySign.Positive, mixed.Sign);
        mixed.Add(value, -1);
        Assert.Equal(GeometrySign.Zero, mixed.Sign);
    }

    [Theory]
    [InlineData(FootprintPartition.Boxes)]
    [InlineData(FootprintPartition.Hulls)]
    public void RotatedPolyhedronFootprintMatchesExactOracle(FootprintPartition partition)
    {
        using var diagnostics = new SourceDiagnosticScope();
        using var scene = new SupportFootprintScene();
        Vector3 half = new(2, 0.125f, 2);
        PhysicsShape shape = partition == FootprintPartition.Boxes ? new BoxShape(half) :
            new ConvexHullShape([new(-2, -0.125f, -2), new(2, -0.125f, -2), new(-2, 0.125f, -2), new(2, 0.125f, -2),
                new(-2, -0.125f, 2), new(2, -0.125f, 2), new(-2, 0.125f, 2), new(2, 0.125f, 2)]);
        scene.Install(shape, new(new(0, 0.125f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.65f)));
        using var lease = scene.World.AcquireQueryReadLease();
        var members = new SupportFootprintMember[256];
        var joins = new ulong[1024];
        var request = Request();
        foreach (SupportPolygon polygon in CapturedPolygons(scene, request))
        {
            double y = scene.Faces.Single(f => f.ElementId == polygon.ElementId).Normal.Y;
            Assert.Equal(y > 0 ? GeometrySign.Positive : y < 0 ? GeometrySign.Negative : GeometrySign.Zero,
                polygon.SourcePredicates.NormalComponent(polygon, 1));
        }
        CheckQueryOracle(scene, request, Capability(scene.World).QuerySupportFootprint(lease, request, members, joins), members, joins);
        AssertSourceFallbacks();
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.05f)]
    public void RotatedObliqueEdgeCrossesDiscRim(float x)
    {
        using var diagnostics = new SourceDiagnosticScope();
        using var scene = new SupportFootprintScene();
        scene.Install(new TriangleMeshShape([new(-0.375f, 0, -0.25f), new(0.375f, 0, 0.25f), new(0.375f, 0, -0.25f)],
            [0, 2, 1]), new(new(x, 0.125f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.65f)));
        var request = Request(x: x);
        foreach (SupportPolygon polygon in CapturedMesh(scene, request))
        {
            OracleFace face = scene.Faces.Single(f => f.ElementId == polygon.ElementId);
            CheckMaximum(Measure(polygon, request), SupportFootprintOracle.Polygon(face, request)!);
        }
        SupportQueryDiagnostics.Reset();
        using var lease = scene.World.AcquireQueryReadLease();
        var members = new SupportFootprintMember[256];
        var joins = new ulong[1024];
        CheckQueryOracle(scene, request, Capability(scene.World).QuerySupportFootprint(lease, request, members, joins), members, joins);
        AssertSourceFallbacks();
    }

    [Fact]
    public void RotatedUphillPointOnObliqueEdgeMatchesExactOracle()
    {
        using var diagnostics = new SourceDiagnosticScope();
        // The gradient (3,4) points at (3s,4s) on the radius-5s disc. The same closed edge
        // holds that maximum. Exact source expansion must retain the radical equality.
        float s = 262145f / 1048576f;
        using var scene = new SupportFootprintScene();
        Vector3 Local(float x, float z) => new(3 * x + 4 * z, z, x);
        scene.Install(new TriangleMeshShape([Local(-s, 7 * s), Local(7 * s, s), Local(-8 * s, -8 * s)],
            [0, 2, 1]), new(Vector3.Zero, new Quaternion(0.5f, 0.5f, 0.5f, 0.5f)));
        var request = Request(radius: 5 * s, upper: 7);
        var polygon = Assert.Single(CapturedMesh(scene, request));
        CheckMaximum(Measure(polygon, request), SupportFootprintOracle.Polygon(Assert.Single(scene.Faces), request)!);
        AssertSourceFallbacks();
    }

    [Fact]
    public void GeneralInstalledRotationKeepsSharedDiagonalCorrelation()
    {
        using var diagnostics = new SourceDiagnosticScope();
        using var scene = new SupportFootprintScene();
        scene.Install(new TriangleMeshShape([new(-2, 0, -2), new(2, 0, -2), new(2, 0, 2), new(-2, 0, 2)],
            [0, 1, 2, 0, 2, 3]), new(new(0, 0.125f, 0), Quaternion.CreateFromYawPitchRoll(0.3f, 0.2f, 0.65f)));
        using var lease = scene.World.AcquireQueryReadLease();
        var members = new SupportFootprintMember[256];
        var joins = new ulong[1024];
        var request = Request();
        CheckQueryOracle(scene, request, Capability(scene.World).QuerySupportFootprint(lease, request, members, joins), members, joins);
        AssertSourceFallbacks();
    }

    [Fact]
    public void RotatedObliqueTangentRetainsClosedDiscWitness()
    {
        using var diagnostics = new SourceDiagnosticScope();
        // The tangent point (3s,4s) has radius 5s. The edge direction (-4s,3s) is perpendicular.
        // This binary32 s makes every installed vertex and the radius exact, while the fourth-degree
        // discriminant needs more than binary64 precision. The pose cycles local axes exactly.
        float s = 1048577f / 4194304f;
        using var scene = new SupportFootprintScene();
        Vector3 Local(float x, float z) => new(0, z, x);
        scene.Install(new TriangleMeshShape([Local(-s, 7 * s), Local(7 * s, s), Local(8 * s, 8 * s)],
            [0, 1, 2]), new(new(0, 0.125f, 0), new Quaternion(0.5f, 0.5f, 0.5f, 0.5f)));
        var request = Request(radius: 5 * s);
        var polygon = Assert.Single(CapturedMesh(scene, request));
        var axis = FeaturePoint.Exact(Vector3.Zero);
        var radius = FeatureNumber.Exact(request.Radius);
        Assert.Equal(GeometrySign.Zero, polygon.SourcePredicates.EdgeDiscriminant(polygon, 2, axis, radius));
        CheckMaximum(Measure(polygon, request), SupportFootprintOracle.Polygon(Assert.Single(scene.Faces), request)!);
        Assert.Equal(GeometrySign.Positive, SupportFootprintProposalGeometry.Lowest(polygon, request.FootProposal, out var lowest));
        ContainsReference(lowest, SupportFootprintExactOracle.Contact(Assert.Single(scene.Faces), request.FootProposal)!.Value);
        AssertSourceFallbacks();
    }

    [Fact]
    public void RotatedCircleEndpointsKeepBothClosedRootOrders()
    {
        using var diagnostics = new SourceDiagnosticScope();
        float s = 1048577f / 4194304f;
        using var scene = new SupportFootprintScene();
        Vector3 Local(float x, float z) => new(0, z, x);
        scene.Install(new TriangleMeshShape([Local(3 * s, 4 * s), Local(-3 * s, 4 * s), Local(-8 * s, -8 * s)],
            [0, 1, 2]), new(new(0, 0.125f, 0), new Quaternion(0.5f, 0.5f, 0.5f, 0.5f)));
        var request = Request(radius: 5 * s);
        var polygon = Assert.Single(CapturedMesh(scene, request));
        var axis = FeaturePoint.Exact(Vector3.Zero);
        var radius = FeatureNumber.Exact(request.Radius);
        Assert.Equal(GeometrySign.Zero, polygon.SourcePredicates.EdgeRoot(polygon, 2, axis, radius, -1, high: false));
        Assert.Equal(GeometrySign.Zero, polygon.SourcePredicates.EdgeRoot(polygon, 2, axis, radius, 1, high: true));
        Assert.Equal(GeometrySign.Positive, polygon.SourcePredicates.EdgeOtherRoot(polygon, 2, axis, false, high: false));
        Assert.Equal(GeometrySign.Zero, polygon.SourcePredicates.EdgeOtherRoot(polygon, 2, axis, false, high: true));
        CheckMaximum(Measure(polygon, request), SupportFootprintOracle.Polygon(Assert.Single(scene.Faces), request)!);
        AssertSourceFallbacks();
    }

    [Fact]
    public void InstalledRepresentedCoefficientBoundaryIsExactlyOnTheDisc()
    {
        Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.65f);
        var represented = InstalledPoseOracle.Represented(rotation);
        float radius = represented.X.X.AsSingle();
        using var scene = new SupportFootprintScene();
        scene.Install(new TriangleMeshShape([new(1, 0, 0), new(1.125f, 0, -0.125f), new(1.125f, 0, 0.125f)],
            [0, 1, 2]), new(Vector3.Zero, rotation));
        var request = Request(radius: radius, upper: 1);
        var polygon = Assert.Single(CapturedMesh(scene, request));
        Assert.Equal(InstalledPoseOracle.R.Zero, represented.X.X.Square() - InstalledPoseOracle.R.From(radius).Square());
        Assert.Equal(GeometrySign.Zero, polygon.SourcePredicates.VertexDisc(polygon, 0,
            FeaturePoint.Exact(Vector3.Zero), FeatureNumber.Exact(radius)));
        using var lease = scene.World.AcquireQueryReadLease();
        var members = MemberSentinels(256);
        var before = (SupportFootprintMember[])members.Clone();
        var joins = Enumerable.Repeat(73UL, 1024).ToArray();
        var result = Capability(scene.World).QuerySupportFootprint(lease, request, members, joins);
        CheckQueryOracle(scene, request, result, members, joins);
        Assert.True(Assert.Single(members.Take(result.Members)).HasDiscMaximum);
        Assert.Equal(before.Skip(result.Members), members.Skip(result.Members));
        Assert.All(joins.Skip(result.Members * result.JoinWordsPerRow), word => Assert.Equal(73UL, word));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WarmRotatedSourceFallbackAllocatesNothing(bool general)
    {
        using var scene = new SupportFootprintScene();
        Vector3 Local(float x, float z) => new(x, general ? 0.25f * x + 0.125f * z : 0, z);
        Quaternion rotation = general ? Quaternion.CreateFromYawPitchRoll(0.3f, 0.2f, 0.65f) :
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.65f);
        scene.Install(new TriangleMeshShape([Local(-2, -2), Local(2, -2), Local(2, 2), Local(-2, 2)],
            [0, 1, 2, 0, 2, 3]), new(new(0, 0.125f, 0), rotation));
        using var lease = scene.World.AcquireQueryReadLease();
        var members = new SupportFootprintMember[256];
        var joins = new ulong[1024];
        var request = Request();
        var capability = Capability(scene.World);
        SupportQueryDiagnostics.Enable(true);
        try
        {
            using var region = SupportAllocationRegion.Start();
            long coldBefore = GC.GetAllocatedBytesForCurrentThread();
            var cold = capability.QuerySupportFootprint(lease, request, members, joins);
            long coldBytes = GC.GetAllocatedBytesForCurrentThread() - coldBefore;
            Assert.Equal(CapsuleFeatureStatus.Complete, cold.Status);
            SupportQueryDiagnostics.Reset();
            long before = GC.GetAllocatedBytesForCurrentThread();
            var result = capability.QuerySupportFootprint(lease, request, members, joins);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            region.Dispose();
            Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
            Assert.Equal(0, bytes);
            Assert.True(SupportQueryDiagnostics.Snapshot().ExactFallbacks > 0);
            object scratch = FootprintScratch(scene.World);
            long payload = (long)typeof(KhaozEngine.Tests.Locomotion.Contacts.SupportCostScratch)
                .GetMethod("Visit", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(null, [scratch, new HashSet<object>(ReferenceEqualityComparer.Instance)])!;
            _output.WriteLine($"Rotated source fallback general={general} coldBytes={coldBytes} warmBytes={bytes} scratchPayloadBytes={payload} exactFallbacks={SupportQueryDiagnostics.Snapshot().ExactFallbacks}");
        }
        finally { SupportQueryDiagnostics.Enable(false); }
    }

    [Fact]
    public void WarmSourceStrataAllocateNothing()
    {
        using var diagnostics = new SourceDiagnosticScope();
        float s = 262145f / 1048576f;
        using var scene = new SupportFootprintScene();
        Vector3 Local(float x, float z) => new(3 * x + 4 * z, z, x);
        scene.Install(new TriangleMeshShape([Local(-s, 7 * s), Local(7 * s, s), Local(-8 * s, -8 * s)],
            [0, 2, 1]), new(Vector3.Zero, new Quaternion(0.5f, 0.5f, 0.5f, 0.5f)));
        var request = Request(radius: 5 * s, upper: 7);
        var polygon = Assert.Single(CapturedMesh(scene, request));
        Assert.Equal(GeometrySign.Positive, SupportFootprintPolygon.Measure(polygon, request, out _));
        Assert.Equal(GeometrySign.Positive, SupportFootprintProposalGeometry.Lowest(polygon, request.FootProposal, out _));
        SupportQueryDiagnostics.Reset();
        GeometrySign footprint = default, contact = default;
        long bytes = AllocationMeasurement.Measure(nameof(WarmSourceStrataAllocateNothing), () =>
        {
            footprint = SupportFootprintPolygon.Measure(polygon, request, out _);
            contact = SupportFootprintProposalGeometry.Lowest(polygon, request.FootProposal, out _);
        });
        Assert.Equal(GeometrySign.Positive, footprint);
        Assert.Equal(GeometrySign.Positive, contact);
        Assert.Equal(0, bytes);
        _output.WriteLine($"Point-map source strata warmBytes={bytes}");
        AssertSourceFallbacks();
    }

    static List<SupportPolygon> CapturedMesh(SupportFootprintScene scene, SupportFootprintRequest request)
    {
        var owner = scene.Faces[0].Static;
        var entry = HandlesOf(scene.World)[owner.Value];
        var simulation = SimulationOf(scene.World);
        simulation.Statics.GetDescription(entry.Handle, out var description);
        Assert.True(SupportFootprintBounds.TryCreate(request, out var bounds));
        var polygons = new List<SupportPolygon>();
        Assert.Equal(CapsuleFeatureStatus.Complete, SupportNeighborhoodMesh.Polygons(simulation, description.Shape,
            description.Pose, owner, bounds, [], new SupportPolygonPool(), polygons));
        Assert.NotEmpty(polygons);
        return polygons;
    }
}
