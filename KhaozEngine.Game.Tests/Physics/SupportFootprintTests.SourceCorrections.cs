using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Locomotion.Contacts;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    [Theory]
    [InlineData(FootprintPartition.Boxes)]
    [InlineData(FootprintPartition.Hulls)]
    [InlineData(FootprintPartition.Mesh)]
    public void YawRotatedCrateEdgeKeepsVerticalWallsInCompleteQuery(FootprintPartition partition)
    {
        using var diagnostics = new SourceDiagnosticScope();
        using var scene = new SupportFootprintScene();
        Vector3 axis = InstallYawCrate(scene, partition);
        var request = Request(x: axis.X, z: axis.Z);
        using var lease = scene.World.AcquireQueryReadLease();
        var members = new SupportFootprintMember[256];
        var joins = new ulong[1024];
        var result = Capability(scene.World).QuerySupportFootprint(lease, request, members, joins);
        CheckQueryOracle(scene, request, result, members, joins);
        Assert.Equal(SupportProposalStatus.Contact, result.FootVerdict.Status);
        Assert.Contains(members.Take(result.Members), m => !m.HasDiscMaximum);
        Assert.Contains(members.Take(result.Members), m => m.HasDiscMaximum && m.DiscHeightLower <= 0.25 && m.DiscHeightUpper >= 0.25);
        AssertSourceFallbacks();
        var walls = CapturedPolygons(scene, request).Where(p => p.Normal.Y.Sign == GeometrySign.Unresolved).ToArray();
        Assert.NotEmpty(walls);
        foreach (SupportPolygon wall in walls)
        {
            SupportQueryDiagnostics.Reset();
            Assert.Equal(GeometrySign.Negative, SupportFootprintProposalGeometry.Lowest(wall, request.FootProposal, out _));
            AssertSourceFallbacks();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProposalInteriorKeepsEveryDecidedFastEdge(bool boundary)
    {
        using var diagnostics = new SourceDiagnosticScope();
        using var scene = new SupportFootprintScene();
        scene.Install(new TriangleMeshShape([new(-2, 0, -2), new(2, 0, -2), new(2, 0, 2)], [0, 1, 2]),
            new(new(0, 0.125f, 0), Quaternion.CreateFromAxisAngle(boundary ? Vector3.UnitY : Vector3.UnitZ, 0.65f)));
        var request = Request();
        var polygon = Assert.Single(CapturedMesh(scene, request));
        var axis = FeaturePoint.Exact(Vector3.Zero);
        var radius = FeatureNumber.Exact(request.Radius);
        FeatureNumber length = FeatureNumber.Enclosed(FeaturePoint.Dot(polygon.Normal, polygon.Normal).Bounds.Sqrt());
        FeatureNumber scale = radius.Divide(length);
        FeatureNumber x = axis.X.Subtract(polygon.Normal.X.Multiply(scale, exactZeroProducts: true));
        FeatureNumber z = axis.Z.Subtract(polygon.Normal.Z.Multiply(scale, exactZeroProducts: true));
        FeaturePoint first = polygon.Vertices[0];
        FeatureNumber y = first.Y.Add(polygon.Normal.X.Multiply(first.X.Subtract(x), exactZeroProducts: true)
            .Add(polygon.Normal.Z.Multiply(first.Z.Subtract(z), exactZeroProducts: true)).Divide(polygon.Normal.Y));
        var point = new FeaturePoint(x, y, z);
        for (int i = 0; i < polygon.Edges.Length; i++)
            Assert.Equal(boundary && i == 0 ? GeometrySign.Unresolved : GeometrySign.Positive, polygon.EdgeSide(point, i));
        Assert.Equal(boundary ? GeometrySign.Zero : GeometrySign.Positive,
            polygon.SourcePredicates.ProposalInterior(polygon, point, axis, radius));
        Assert.Equal(boundary ? 1 : 0, SupportQueryDiagnostics.Snapshot().ExactFallbacks);
    }

    [Theory]
    [InlineData(false, 4)]
    [InlineData(true, 9)]
    public void WarmEnclosedSourceInteriorKeepsScratchBounded(bool general, int variables)
    {
        using var diagnostics = new SourceDiagnosticScope();
        using var scene = new SupportFootprintScene();
        Vector3 Local(float x, float z) => new(x, 0.25f * x + 0.125f * z, z);
        Quaternion rotation = general ? Quaternion.CreateFromYawPitchRoll(0.3f, 0.2f, 0.65f) :
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.65f);
        scene.Install(new TriangleMeshShape([Local(-2, -2), Local(2, -2), Local(2, 2)], [0, 1, 2]),
            new(new(0, 0.125f, 0), rotation));
        var request = Request();
        var polygon = Assert.Single(CapturedMesh(scene, request));
        var map = polygon.SourceMap;
        Assert.Equal(variables, new[] { map.X.X, map.X.Y, map.X.Z, map.Y.X, map.Y.Y, map.Y.Z,
            map.Z.X, map.Z.Y, map.Z.Z }.Count(c => c.Lower != c.Upper));
        var axis = FeaturePoint.Exact(Vector3.Zero);
        var radius = FeatureNumber.Exact(request.Radius);
        long coldBefore = GC.GetAllocatedBytesForCurrentThread();
        GeometrySign cold = polygon.SourcePredicates.Interior(polygon, 1, axis, radius, -1, spatial: true);
        long coldBytes = GC.GetAllocatedBytesForCurrentThread() - coldBefore;
        Assert.Equal(GeometrySign.Positive, cold);
        long payload = SourceScratchPayload(polygon.SourcePredicates);
        _output.WriteLine($"Enclosed source variables={variables} coldBytes={coldBytes} scratchPayloadBytes={payload}");
        Assert.True(payload < 4 * 1024 * 1024, $"Retained source scratch is {payload} bytes");
        SupportQueryDiagnostics.Reset();
        var recorder = new SupportCostAttributionTests.PhaseRecorder(false);
        ContactAllocationDiagnostics.SetObserver(recorder.Observe);
        GeometrySign warm;
        long warmBytes;
        try
        {
            using var region = SupportAllocationRegion.Start();
            recorder.Reset();
            long warmBefore = GC.GetAllocatedBytesForCurrentThread();
            using (ContactAllocationDiagnostics.Begin(ContactAllocationPhase.SupportExtrema))
                warm = polygon.SourcePredicates.Interior(polygon, 1, axis, radius, -1, spatial: true);
            warmBytes = GC.GetAllocatedBytesForCurrentThread() - warmBefore;
        }
        finally { ContactAllocationDiagnostics.SetObserver(null); }
        Assert.Equal(0, recorder.Depth);
        Assert.Equal(warmBytes, recorder.Bytes.Sum());
        Assert.All(recorder.Rows(), phase => Assert.Equal(0, phase.Collections));
        Assert.Equal(GeometrySign.Positive, warm);
        Assert.Equal(0, warmBytes);
        AssertSourceFallbacks();
        Assert.Equal(payload, SourceScratchPayload(polygon.SourcePredicates));
        _output.WriteLine($"Enclosed source variables={variables} warmBytes={warmBytes}");
    }

    static long SourceScratchPayload(object scratch) => (long)typeof(KhaozEngine.Tests.Locomotion.Contacts.SupportCostScratch)
        .GetMethod("Visit", BindingFlags.Static | BindingFlags.NonPublic)!
        .Invoke(null, [scratch, new HashSet<object>(ReferenceEqualityComparer.Instance)])!;

    static void AssertSourceFallbacks() => Assert.True(SupportQueryDiagnostics.Snapshot().ExactFallbacks > 0);

    sealed class SourceDiagnosticScope : IDisposable
    {
        internal SourceDiagnosticScope() { SupportQueryDiagnostics.Enable(true); SupportQueryDiagnostics.Reset(); }
        public void Dispose() => SupportQueryDiagnostics.Enable(false);
    }
}
