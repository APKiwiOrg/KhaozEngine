using System;
using System.Linq;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Locomotion.Contacts;
using Xunit;
using Xunit.Abstractions;
using R = KhaozEngine.Tests.Physics.InstalledPoseOracle.R;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    readonly ITestOutputHelper _output;
    public SupportFootprintTests(ITestOutputHelper output) => _output = output;
    [Fact]
    public void ProposalContactHeightCertifiesVertexEdgeInteriorAndTangency()
    {
        Vector3[][] rows =
        [
            [new(0.0625f, 0.125f, 0), new(0.125f, 0, -0.03125f), new(0.125f, 0, 0.03125f)],
            [new(-1, -1, 0.125f), new(1, 1, 0.125f), new(1, 1, 1), new(-1, -1, 1)],
            [new(-1, -1, -1), new(1, 1, -1), new(1, 1, 1), new(-1, -1, 1)],
            [new(-1, 0.125f, -1), new(1, 0.125f, -1), new(1, 0.125f, 1), new(-1, 0.125f, 1)],
            [new(-1, 0.125f, 0.25f), new(1, 0.125f, 0.25f), new(1, 0.125f, 1), new(-1, 0.125f, 1)],
        ];
        var proposal = Request(radius: 0.25f).FootProposal;
        foreach (Vector3[] row in rows)
        {
            SupportPolygon polygon = Polygon(row, out OracleFace face);
            var reference = SupportFootprintExactOracle.Contact(face, proposal)!.Value;
            Assert.Equal(GeometrySign.Positive, SupportFootprintProposalGeometry.Lowest(polygon, proposal, out var height));
            ContainsReference(height, reference);
        }
        SupportPolygon down = Polygon(rows[3], out _, upward: false);
        Assert.Equal(GeometrySign.Negative, SupportFootprintProposalGeometry.Lowest(down, proposal, out _));
    }

    [Theory]
    [InlineData(0.125f, 0.125, SupportProposalStatus.Contact)]
    [InlineData(0.1255f, 0.125, SupportProposalStatus.Contact)]
    [InlineData(0.25f, 0.125, SupportProposalStatus.InvalidStart)]
    public void RoofRaisedStartAndEqualityKeepSeparateVerdicts(float height, double upper, SupportProposalStatus status)
    {
        using var scene = new SupportFootprintScene();
        scene.Install(new BoxShape(new(2, 1, 2)), Pose.At(new(0, height - 1, 0)));
        var request = Request(upper: upper);
        using var lease = scene.World.AcquireQueryReadLease();
        var members = new SupportFootprintMember[256];
        var joins = new ulong[1024];
        var result = Capability(scene.World).QuerySupportFootprint(lease, request, members, joins);
        Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
        Assert.Equal(status, result.AxisVerdict.Status);
        Assert.Equal(status, result.FootVerdict.Status);
        if (status == SupportProposalStatus.Contact)
        {
            var face = scene.Faces.Single(f => f.Normal.Y > 0);
            var expected = SupportFootprintExactOracle.Contact(face, request.FootProposal)!.Value;
            Assert.True(R.From(result.FootVerdict.LowestLower) <= expected.Lower);
            Assert.True(R.From(result.FootVerdict.LowestUpper) >= expected.Upper);
            if (height == 0.125f) Assert.Equal(upper, result.FootVerdict.LowestUpper);
            else Assert.True(result.FootVerdict.LowestLower > upper);
        }
    }

    [Fact]
    public void UncertainContactBandSignRefusesBothSpans()
    {
        using var scene = new SupportFootprintScene();
        scene.Install(new TriangleMeshShape([new(-1, -1, -1), new(1, 1, -1), new(1, 1, 1), new(-1, -1, 1)],
            [0, 1, 2, 0, 2, 3]), Pose.Identity);
        var request = Request(radius: 0.25f, upper: (Math.Sqrt(2) - 1) / 4);
        using var lease = scene.World.AcquireQueryReadLease();
        var members = MemberSentinels(256);
        var before = (SupportFootprintMember[])members.Clone();
        var joins = Enumerable.Repeat(77UL, 1024).ToArray();
        var result = Capability(scene.World).QuerySupportFootprint(lease, request, members, joins);
        Assert.Equal(CapsuleFeatureStatus.Unsupported, result.Status);
        Assert.Equal(before, members);
        Assert.All(joins, word => Assert.Equal(77UL, word));
    }

    [Fact]
    public void RotatedMeshVolumeEnclosesAllEightCorners()
    {
        using var scene = new SupportFootprintScene();
        var request = Request();
        Assert.True(SupportFootprintBounds.TryCreate(request, out var bounds));
        Quaternion rotation = Quaternion.CreateFromYawPitchRoll(0.47f, 0.63f, 0.79f);
        var pose = new Pose(new(0.1f, -0.05f, 0.2f), rotation);
        var vertices = new List<Vector3>();
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) == 0 ? bounds.Minimum.X + 0.001f : bounds.Maximum.X - 0.001f,
                (i & 2) == 0 ? bounds.Minimum.Y + 0.001f : bounds.Maximum.Y - 0.001f,
                (i & 4) == 0 ? bounds.Minimum.Z + 0.001f : bounds.Maximum.Z - 0.001f);
            Vector3 local = Vector3.Transform(corner - pose.Position, Quaternion.Conjugate(rotation));
            vertices.AddRange([local, local + new Vector3(0.0001f, 0, 0), local + new Vector3(0, 0, 0.0001f)]);
        }
        var handle = scene.Install(new TriangleMeshShape([.. vertices], Enumerable.Range(0, 24).ToArray()), pose);
        var simulation = SimulationOf(scene.World);
        var entry = HandlesOf(scene.World)[handle.Value];
        simulation.Statics.GetDescription(entry.Handle, out var description);
        var polygons = new List<SupportPolygon>();
        Assert.Equal(CapsuleFeatureStatus.Complete, SupportNeighborhoodMesh.Polygons(simulation, description.Shape,
            description.Pose, handle, bounds, [], new SupportPolygonPool(), polygons));
        Assert.Equal(Enumerable.Range(0, 8), polygons.Select(p => p.ElementId));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.05f)]
    public void RotatedMeshFootprintMatchesExactOracle(float x)
    {
        using var scene = new SupportFootprintScene();
        scene.Install(new TriangleMeshShape([new(-2, 0, -2), new(2, 0, -2), new(2, 0, 2), new(-2, 0, 2)],
            [0, 1, 2, 0, 2, 3]), new Pose(new(0, 0.125f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.65f)));
        using var lease = scene.World.AcquireQueryReadLease();
        var members = new SupportFootprintMember[256];
        var joins = new ulong[1024];
        var request = Request(x: x);
        CheckQueryOracle(scene, request, Capability(scene.World).QuerySupportFootprint(lease, request, members, joins), members, joins);
    }

    [Fact]
    public void AmbiguousInstalledHullRefusesWithoutPublishingFloor()
    {
        using var scene = SupportFootprintScenes.HiddenCrate(FootprintPartition.Hulls);
        var simulation = SimulationOf(scene.World);
        var entry = HandlesOf(scene.World)[scene.Crate.Value];
        simulation.Statics.GetDescription(entry.Handle, out var description);
        ref var compound = ref simulation.Shapes.GetShape<BepuPhysics.Collidables.Compound>(description.Shape.Index);
        ref var hull = ref simulation.Shapes.GetShape<BepuPhysics.Collidables.ConvexHull>(compound.Children[0].ShapeIndex.Index);
        hull.GetVertexIndicesForFace(0, out var indices);
        indices[1] = indices[0];
        using var lease = scene.World.AcquireQueryReadLease();
        var members = MemberSentinels(256);
        var before = (SupportFootprintMember[])members.Clone();
        var joins = Enumerable.Repeat(17UL, 1024).ToArray();
        var result = Capability(scene.World).QuerySupportFootprint(lease, Request(), members, joins);
        Assert.Equal(CapsuleFeatureStatus.Ambiguous, result.Status);
        Assert.Equal(before, members);
        Assert.All(joins, word => Assert.Equal(17UL, word));
    }

    [Fact]
    public void CorruptedResultMetadataAndForeignSourcesAreRejected()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using var other = new BepuPhysicsWorld(Vector3.Zero);
        using var lease = world.AcquireQueryReadLease();
        var capability = Capability(world);
        var result = capability.QuerySupportFootprint(lease, Request(), [], []);
        foreach (var change in new (string Property, object Value)[]
        {
            (nameof(result.SourceWorld), other), (nameof(result.Origin), Vector3.One),
            (nameof(result.GeometryGeneration), result.GeometryGeneration + 1),
        })
        {
            object boxed = result;
            typeof(SupportFootprintResult).GetField($"<{change.Property}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(boxed, change.Value);
            var corrupt = (SupportFootprintResult)boxed;
            Assert.Throws<InvalidOperationException>(() => capability.AssertFootprintCurrent(corrupt, lease));
        }
        using var foreign = other.AcquireQueryReadLease();
        var members = MemberSentinels(1);
        Assert.Throws<ArgumentException>(() => capability.QuerySupportFootprint(foreign, Request(), members, new ulong[1]));
        Assert.Equal(new StaticHandle(987), members[0].Static);
    }

    [Theory]
    [InlineData(FootprintPartition.Boxes)]
    [InlineData(FootprintPartition.Hulls)]
    [InlineData(FootprintPartition.Compound)]
    [InlineData(FootprintPartition.Mesh)]
    public void WarmFootprintQueryAllocatesNothing(FootprintPartition partition)
    {
        using var scene = SupportFootprintScenes.HiddenCrate(partition);
        using var lease = scene.World.AcquireQueryReadLease();
        var request = Request();
        var members = new SupportFootprintMember[256];
        var joins = new ulong[1024];
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
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            region.Dispose();
            Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
            Assert.Equal(0, allocated);
            Assert.Equal(2, SupportQueryDiagnostics.Snapshot().Proposals);
            Assert.Equal(1, SupportQueryDiagnostics.Snapshot().Volumes);
            object scratch = FootprintScratch(scene.World);
            long payload = (long)typeof(KhaozEngine.Tests.Locomotion.Contacts.SupportCostScratch)
                .GetMethod("Visit", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [scratch, new HashSet<object>(ReferenceEqualityComparer.Instance)])!;
            _output.WriteLine($"{partition} coldBytes={coldBytes} warmBytes={allocated} scratchPayloadBytes={payload} members={result.Members}");
        }
        finally { SupportQueryDiagnostics.Enable(false); }
    }

    internal static BepuPhysics.Simulation SimulationOf(BepuPhysicsWorld world) =>
        (BepuPhysics.Simulation)typeof(BepuPhysicsWorld).GetField("_sim", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world)!;

    internal static Dictionary<int, (BepuPhysics.StaticHandle Handle, BepuPhysics.Collidables.TypedIndex Shape)> HandlesOf(BepuPhysicsWorld world) =>
        (Dictionary<int, (BepuPhysics.StaticHandle Handle, BepuPhysics.Collidables.TypedIndex Shape)>)typeof(BepuPhysicsWorld)
            .GetField("_handles", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world)!;

    static void ContainsReference(GeometryInterval actual, SupportFootprintExactOracle.Bounds reference)
    {
        Assert.True(R.From(actual.Lower) <= reference.Lower, $"{actual.Lower:R} exceeds {reference.Lower}");
        Assert.True(R.From(actual.Upper) >= reference.Upper, $"{actual.Upper:R} is below {reference.Upper}");
    }

    static void CheckPublishedPlane(SupportFootprintMember member, SupportFootprintRequest request,
        SupportFootprintExactOracle.Bounds reference)
    {
        static GeometryInterval Component(float value, float error) => GeometryInterval.Exact(value)
            .Add(GeometryInterval.Enclose(-error, error));
        GeometryInterval x = Component(member.PlanePoint.X, member.PlanePointErrorMetres);
        GeometryInterval y = Component(member.PlanePoint.Y, member.PlanePointErrorMetres);
        GeometryInterval z = Component(member.PlanePoint.Z, member.PlanePointErrorMetres);
        GeometryInterval nx = Component(member.Normal.X, member.NormalError);
        GeometryInterval ny = Component(member.Normal.Y, member.NormalError);
        GeometryInterval nz = Component(member.Normal.Z, member.NormalError);
        var plane = y.Add(nx.Multiply(x.Subtract(GeometryInterval.Exact(request.Axis.X)))
            .Add(nz.Multiply(z.Subtract(GeometryInterval.Exact(request.Axis.Y)))).Divide(ny));
        Assert.True(plane.IsResolved);
        ContainsReference(plane, reference);
    }
}
