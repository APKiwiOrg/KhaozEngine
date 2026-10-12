using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Locomotion.Contacts;
using Xunit;

namespace KhaozEngine.Tests.Physics;

[Collection("AllocSensitive")]
public partial class SupportFootprintTests
{
    [Fact]
    public void PolygonMaximumUsesVertexEdgeAndDiscExtrema()
    {
        SupportFootprintRequest request = Request();
        Vector3[][] rows =
        [
            [new(0.05f, 0.05f, 0), new(0.1f, 0.1f, 0), new(0.05f, 0.05f, 0.05f)],
            [new(-0.3f, -0.3f, 0.1f), new(0.3f, 0.3f, 0.1f), new(-0.3f, -0.3f, 0.3f)],
            [new(-0.3f, -0.3f, -0.3f), new(0.3f, 0.3f, -0.3f),
                new(0.3f, 0.3f, 0.3f), new(-0.3f, -0.3f, 0.3f)],
            [new(-0.3f, 0.1f, -0.3f), new(0.3f, 0.1f, -0.3f),
                new(0.3f, 0.1f, 0.3f), new(-0.3f, 0.1f, 0.3f)],
        ];
        double[] expected = [0.1f, Math.Sqrt((double)request.Radius * request.Radius - (double)0.1f * 0.1f),
            request.Radius, 0.1f];
        for (int i = 0; i < rows.Length; i++)
        {
            SupportPolygon polygon = Polygon(rows[i], out OracleFace face);
            FootprintOracleMember oracle = SupportFootprintOracle.Polygon(face, request)!;
            Assert.Equal(GeometrySign.Positive, SupportFootprintPolygon.Measure(polygon, request, out var member));
            CheckMaximum(member, oracle);
            Assert.True(member.DiscHeightLower <= expected[i]);
            Assert.True(member.DiscHeightUpper >= expected[i]);
            Assert.Equal(member, Measure(polygon, request));
        }
        SupportPolygon flatCut = Polygon([new(-0.3f, 0.1f, -0.3f), new(0.3f, 0.1f, -0.3f),
            new(-0.3f, 0.1f, 0.3f)], out OracleFace flatFace);
        SupportFootprintMember flat = Measure(flatCut, request);
        CheckMaximum(flat, SupportFootprintOracle.Polygon(flatFace, request)!);
        Assert.Equal(new Vector3(0, 0.1f, 0), flat.First.Position);
        Assert.Equal(0, flat.First.PositionErrorMetres);

        // A represented tangent vertex and the next float on either side keep closed-disc membership.
        request = Request(radius: 0.25f, cosine: 1);
        foreach (float x in new[] { MathF.BitDecrement(0.25f), 0.25f, MathF.BitIncrement(0.25f) })
        {
            SupportPolygon polygon = Polygon([new(x, 0.1f, 0), new(x + 0.05f, 0.1f, -0.05f),
                new(x + 0.05f, 0.1f, 0.05f)], out OracleFace face);
            FootprintOracleMember oracle = SupportFootprintOracle.Polygon(face, request)!;
            SupportFootprintMember member = Measure(polygon, request);
            Assert.Equal(x <= 0.25f, member.HasDiscMaximum);
            if (member.HasDiscMaximum) CheckMaximum(member, oracle);
            else Assert.Equal(0, member.WitnessCount);
        }
        // Tangency in an edge interior exercises the zero discriminant fallback.
        SupportPolygon tangent = Polygon([new(0.25f, 0.1f, -0.5f), new(0.25f, 0.1f, 0.5f),
            new(0.5f, 0.1f, 0.5f)], out OracleFace tangentFace);
        FootprintOracleMember tangentReference = SupportFootprintOracle.Polygon(tangentFace, request)!;
        using var region = SupportAllocationRegion.Start();
        CheckMaximum(Measure(tangent, request), tangentReference);
        // The warmed kernel has no per-query storage, including with predicate counters enabled.
        SupportQueryDiagnostics.Enable(true);
        try
        {
            Measure(tangent, request);
            SupportQueryDiagnostics.Reset();
            long before = GC.GetAllocatedBytesForCurrentThread();
            GeometrySign measured = SupportFootprintPolygon.Measure(tangent, request, out _);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(GeometrySign.Positive, measured);
            Assert.Equal(0, allocated);
            SupportQueryCounts counts = SupportQueryDiagnostics.Snapshot();
            Assert.Equal(1, counts.Polygons);
            Assert.True(counts.Extrema > 0);
        }
        finally { SupportQueryDiagnostics.Enable(false); }
        long disabledBefore = GC.GetAllocatedBytesForCurrentThread();
        SupportFootprintPolygon.Measure(tangent, request, out _);
        long disabledBytes = GC.GetAllocatedBytesForCurrentThread() - disabledBefore;
        region.Dispose();
        Assert.Equal(0, disabledBytes);
    }

    [Fact]
    public void AdmissionKeepsFullPolygonAndContextRing()
    {
        SupportFootprintRequest request = Request(upper: 0.125);
        SupportPolygon fragment = Polygon([new(0.199f, 0.1f + 0.199f, -0.001f),
            new(0.201f, 0.1f + 0.201f, -0.001f), new(0.201f, 0.1f + 0.201f, 0.001f),
            new(0.199f, 0.1f + 0.199f, 0.001f)], out OracleFace face);
        FootprintOracleMember oracle = SupportFootprintOracle.Polygon(face, request)!;
        Assert.True(oracle.Maximum > request.BandUpper);
        CheckMaximum(Measure(fragment, request), oracle);
        SupportPolygon ring = Polygon([new(0.20005f, 0.1f, -0.001f), new(0.20009f, 0.1f, -0.001f),
            new(0.20009f, 0.1f, 0.001f), new(0.20005f, 0.1f, 0.001f)], out OracleFace ringFace);
        Assert.False(SupportFootprintOracle.Polygon(ringFace, request)!.HasDiscMaximum);
        SupportFootprintMember context = Measure(ring, request);
        Assert.False(context.HasDiscMaximum);
        Assert.Equal(0, context.WitnessCount);

        // With axis -e the expanded disc's represented rightmost coordinate is exactly 0.25.
        SupportFootprintRequest radial = Request(x: -0.0001f, radius: 0.25f, cosine: 1);
        foreach (float x in new[] { MathF.BitDecrement(0.25f), 0.25f, MathF.BitIncrement(0.25f) })
        {
            SupportPolygon polygon = Polygon([new(x, 0.1f, 0), new(x + 0.05f, 0.1f, -0.05f),
                new(x + 0.05f, 0.1f, 0.05f)], out _);
            Assert.Equal(x <= 0.25f ? GeometrySign.Positive : GeometrySign.Negative,
                SupportFootprintPolygon.Measure(polygon, radial, out _));
        }
        SupportPolygon ringTangent = Polygon([new(0.25f, 0.1f, -0.5f), new(0.25f, 0.1f, 0.5f),
            new(0.5f, 0.1f, 0.5f)], out _);
        SupportQueryDiagnostics.Enable(true);
        try
        {
            SupportQueryDiagnostics.Reset();
            Assert.False(Measure(ringTangent, radial).HasDiscMaximum);
            Assert.True(SupportQueryDiagnostics.Snapshot().ExactFallbacks > 0);
        }
        finally { SupportQueryDiagnostics.Enable(false); }

        Assert.True(SupportFootprintBounds.TryCreate(request, out var bounds));
        Assert.True(SupportFootprintVolume.TryBounds(request, out var minimum, out var maximum));
        (double bottom, double top) = SupportFootprintOracle.Volume(request);
        Assert.True(bounds.Minimum.Y <= bottom);
        Assert.True(bounds.Maximum.Y >= top);
        Assert.Equal(bounds.Minimum.Y, minimum.Y.Lower);
        Assert.Equal(bounds.Maximum.Y, maximum.Y.Upper);
        Assert.True(bounds.Minimum.X <= -(double)request.Radius - request.ContactBand);
        Assert.True(bounds.Maximum.X >= (double)request.Radius + request.ContactBand);
        foreach (float height in new[] { (float)bottom, MathF.BitDecrement((float)bottom),
            MathF.BitIncrement((float)bottom), (float)top, MathF.BitDecrement((float)top), MathF.BitIncrement((float)top) })
        {
            SupportPolygon polygon = Polygon([new(-0.1f, height, -0.1f), new(0.1f, height, -0.1f),
                new(0.1f, height, 0.1f), new(-0.1f, height, 0.1f)], out OracleFace row);
            bool admitted = SupportFootprintOracle.Polygon(row, request) is not null;
            GeometrySign sign = SupportFootprintPolygon.Measure(polygon, request, out _);
            Assert.True(sign == (admitted ? GeometrySign.Positive : GeometrySign.Negative),
                $"height {height:R}, oracle column [{bottom:R}, {top:R}], verdict {sign}, admitted {admitted}");
        }
        // Rotate a narrow, ordinary triangle in the installed float frame.
        Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.37f);
        Vector3[] sliver = [new(-0.3f, 0.05f, 0), new(0.3f, 0.05f, 0), new(0.3f, 0.05001f, 0.00002f)];
        sliver = sliver.Select(v => Vector3.Transform(v, rotation)).ToArray();
        SupportPolygon narrow = Polygon(sliver, out OracleFace narrowFace);
        CheckMaximum(Measure(narrow, request), SupportFootprintOracle.Polygon(narrowFace, request)!);
        Vector3[] local = [new(-0.3f, 0.05f, -0.000007f), new(0.3f, 0.05f, -0.000007f),
            new(0.3f, 0.05001f, 0.000013f)];
        if (Vector3.Cross(local[1] - local[0], local[2] - local[0]).Y < 0) Array.Reverse(local);
        var pose = new Pose(new(0.01f, 0, 0), rotation);
        Assert.True(InstalledPoseOperator.TryCreate(new BepuPhysics.RigidPose(pose.Position, pose.Orientation),
            out var installed));
        var captured = new SupportPolygon();
        captured.Begin(new StaticHandle(42), 7, local.Length);
        for (int i = 0; i < local.Length; i++)
            captured.Set(i, installed.Point(local[i]), installed.Edge(local[i], local[(i + 1) % local.Length]));
        captured.Finish(FeaturePoint.Cross(installed.Edge(local[0], local[1]), installed.Edge(local[0], local[2])));
        OracleFace capturedFace = new(new StaticHandle(42), 7, local.Select(v =>
            OracleVector.Rotate(pose.Orientation, OracleVector.From(v)) + OracleVector.From(pose.Position)).ToArray())
        { ExactVertices = SupportFootprintExactOracle.Transform(local, pose) };
        CheckMaximum(Measure(captured, request), SupportFootprintOracle.Polygon(capturedFace, request)!);

        SupportPolygon zero = Polygon([Vector3.Zero, Vector3.UnitX, 2 * Vector3.UnitX], out _);
        Assert.Equal(GeometrySign.Negative, SupportFootprintPolygon.Measure(zero, request, out _));
        Assert.Equal(GeometrySign.Unresolved, SupportFootprintPolygon.Measure(new SupportPolygon(),
            Request(x: 10, z: 10), out var unresolved));
        Assert.Equal(default, unresolved);
        SupportPolygon back = Polygon([new(-0.1f, 0, -0.1f), new(0.1f, 0, -0.1f),
            new(0, 0, 0.1f)], out _, upward: false);
        SupportFootprintMember underside = Measure(back, request);
        Assert.False(underside.HasDiscMaximum);
        Assert.Equal(0, underside.WitnessCount);
        SupportPolygon vertical = Polygon([new(0, -0.1f, -0.1f), new(0, 0.1f, -0.1f),
            new(0, 0.1f, 0.1f), new(0, -0.1f, 0.1f)], out _);
        Assert.False(Measure(vertical, request).HasDiscMaximum);
        Assert.False(SupportFootprintBounds.TryCreate(request with { CosMaxSlope = 0 }, out _));
        Assert.False(SupportFootprintBounds.TryCreate(request with { BandUpper = double.MaxValue }, out _));
        Assert.False(SupportFootprintBounds.TryCreate(request with { Radius = 0.009f }, out _));
        // Bounds include both real and represented centre/end arithmetic of the retained paths.
        var displaced = request with
        {
            FootProposal = request.FootProposal with
            { Start = Pose.At(new(0, 1000, 0)), Distance = 1000.3f }
        };
        Assert.True(SupportFootprintBounds.TryCreate(displaced, out var paths));
        float representedEnd = displaced.FootProposal.Start.Position.Y - displaced.FootProposal.Distance;
        float half = displaced.FootProposal.Probe.Radius + displaced.FootProposal.Probe.Length / 2;
        Assert.True(paths.Minimum.Y <= representedEnd - half - displaced.ContactBand);
        Assert.True(paths.Maximum.Y >= displaced.FootProposal.Start.Position.Y + half + displaced.ContactBand);
    }

    static SupportFootprintMember Measure(SupportPolygon polygon, SupportFootprintRequest request)
    {
        Assert.Equal(GeometrySign.Positive, SupportFootprintPolygon.Measure(polygon, request, out var member));
        return member;
    }

    static void CheckMaximum(SupportFootprintMember member, FootprintOracleMember oracle)
    {
        Assert.True(member.HasDiscMaximum);
        var reference = oracle.MaximumBounds!.Value;
        Assert.True(InstalledPoseOracle.R.From(member.DiscHeightLower) <= reference.Lower,
            $"{member.DiscHeightLower:R} exceeds exact reference lower {reference.Lower}");
        Assert.True(InstalledPoseOracle.R.From(member.DiscHeightUpper) >= reference.Upper,
            $"{member.DiscHeightUpper:R} is below exact reference upper {reference.Upper}");
        Assert.True(oracle.WitnessInsideClosedDisc);
        Assert.Equal(1, member.WitnessCount);
        Assert.Equal(SupportWitnessRole.Footprint, member.First.Role);
        Assert.Equal(oracle.Static, member.Static);
        Assert.Equal(oracle.ElementId, member.ElementId);
        double distance = (member.First.Position.X - oracle.Witnesses[0].Position.X) *
            (member.First.Position.X - oracle.Witnesses[0].Position.X) +
            (member.First.Position.Y - oracle.Maximum) * (member.First.Position.Y - oracle.Maximum) +
            (member.First.Position.Z - oracle.Witnesses[0].Position.Z) *
            (member.First.Position.Z - oracle.Witnesses[0].Position.Z);
        // Flat faces have many attained maxima, so their deterministic tie may differ from the oracle's tie.
        if (oracle.Normal.X != 0 || oracle.Normal.Z != 0)
            Assert.True(distance <= (double)member.First.PositionErrorMetres * member.First.PositionErrorMetres);
    }

    static SupportPolygon Polygon(Vector3[] vertices, out OracleFace face, bool upward = true)
    {
        Vector3 normal = Vector3.Cross(vertices[1] - vertices[0], vertices[2] - vertices[0]);
        if ((upward && normal.Y < 0) || (!upward && normal.Y > 0)) Array.Reverse(vertices);
        face = new(new StaticHandle(42), 7, vertices.Select(v => new OracleVector(v.X, v.Y, v.Z)).ToArray());
        var polygon = new SupportPolygon();
        polygon.Begin(face.Static, face.ElementId, vertices.Length);
        for (int i = 0; i < vertices.Length; i++)
        {
            FeaturePoint a = FeaturePoint.Exact(vertices[i]);
            polygon.Set(i, a, FeaturePoint.Subtract(FeaturePoint.Exact(vertices[(i + 1) % vertices.Length]), a));
        }
        polygon.Finish(FeaturePoint.Cross(polygon.Edges[0], FeaturePoint.Subtract(
            polygon.Vertices[2], polygon.Vertices[0])));
        return polygon;
    }

    [Fact]
    public void FootprintCapabilityIsSeparateAndSlotsKeepRawIds()
    {
        Type capability = Required("IPhysicsSupportFootprint");
        Assert.True(capability.IsInterface);
        Assert.Empty(capability.GetInterfaces());
        Assert.Equal(["AssertFootprintCurrent", "QuerySupportFootprint"],
            capability.GetMethods().Select(m => m.Name).Order().ToArray());
        Assert.Equal(["AssertNeighborhoodCurrent", "QuerySupportNeighborhood"],
            typeof(IPhysicsSupportNeighborhood).GetMethods().Select(m => m.Name).Order().ToArray());
        Assert.Equal(["AssertFeatureCurrent", "QueryCapsuleFeature"],
            typeof(IPhysicsCapsuleFeatures).GetMethods().Select(m => m.Name).Order().ToArray());
        Type result = Required("SupportFootprintResult"), member = Required("SupportFootprintMember");
        Assert.Equal(256, result.GetField("MaximumElements")!.GetRawConstantValue());
        Assert.Equal(512, result.GetField("MaximumWitnesses")!.GetRawConstantValue());
        Assert.Equal(4, result.GetMethod("JoinWordsFor")!.Invoke(null, [256]));
        MethodInfo query = capability.GetMethod("QuerySupportFootprint")!;
        Assert.Equal(result, query.ReturnType);
        Assert.Equal(new[] { typeof(IPhysicsQueryLease), Required("SupportFootprintRequest").MakeByRefType(),
            typeof(Span<>).MakeGenericType(member), typeof(Span<ulong>), typeof(QueryFilter) },
            query.GetParameters().Select(p => p.ParameterType).ToArray());
        Assert.True(query.GetParameters()[1].IsIn);
        Assert.True(query.GetParameters()[^1].HasDefaultValue);
        MethodInfo current = capability.GetMethod("AssertFootprintCurrent")!;
        Assert.Equal(typeof(void), current.ReturnType);
        Assert.Equal(new[] { result.MakeByRefType(), typeof(IPhysicsQueryLease) },
            current.GetParameters().Select(p => p.ParameterType).ToArray());
        Type witness = Required("SupportFootprintWitness"), role = Required("SupportWitnessRole");
        Assert.Equal(["Axis", "Footprint"], Enum.GetNames(role));
        Assert.Equal(role, witness.GetProperty("Role")!.PropertyType);
        Assert.NotNull(witness.GetProperty("UpwardLower"));
        Assert.NotNull(member.GetProperty("UpwardLower"));
        Assert.Equal(typeof(double), witness.GetProperty("UpwardLower")!.PropertyType);
        Assert.Equal(typeof(double), member.GetProperty("UpwardLower")!.PropertyType);
        Assert.Equal(typeof(int), member.GetProperty("ElementId")!.PropertyType);
        Assert.Equal(witness, member.GetProperty("First")!.PropertyType);
        Assert.Equal(witness, member.GetProperty("Second")!.PropertyType);
        Assert.Null(witness.GetProperty("ElementId"));
    }

    static Type Required(string name)
    {
        Type? type = typeof(IPhysicsWorld).Assembly.GetType("KhaozEngine.Physics." + name);
        Assert.NotNull(type);
        return type;
    }

    internal static SupportFootprintRequest Request(float x = 0, float z = 0, float radius = 0.2f,
        float cosine = 0.70710677f, double lower = -0.4, double upper = 0.4)
    {
        SupportFootprintProposal Proposal(float r)
        {
            var probe = new CapsuleShape(r, 0.01f);
            float lowest = (float)upper + 0.001f;
            float centre = lowest + r + 0.01f / 2;
            return new(probe, Pose.At(new(x, centre, z)), (float)(upper - lower) + 2 * 0.001f);
        }
        return new(new(x, z), radius, lower, upper, cosine, 0.0001f, Proposal(0.01f), Proposal(radius));
    }

    internal static List<FootprintOracleMember> Members(SupportFootprintScene scene, SupportFootprintRequest request)
    {
        var members = new List<FootprintOracleMember>();
        foreach (OracleFace face in scene.Faces)
            if (SupportFootprintOracle.Polygon(face, request) is { } member) members.Add(member);
        foreach (OracleTangent curve in scene.Curves)
            if (SupportFootprintOracle.Curved(curve, request) is { } member) members.Add(member);
        return members.OrderBy(m => m.Static.Value).ThenBy(m => m.ElementId).ToList();
    }

    [Fact]
    public void ResultFactoriesKeepCountsVerdictsAndTheOriginalLiveInterval()
    {
        using var scene = new SupportFootprintScene();
        using IPhysicsQueryLease lease = scene.World.AcquireQueryReadLease();
        var contact = new SupportProposalVerdict(SupportProposalStatus.Contact, 0.1, 0.100001);
        SupportFootprintResult result = SupportFootprintResult.Completed(scene.World, lease, 256, 512, contact, default);
        Assert.Equal(256, result.Members);
        Assert.Equal(512, result.Witnesses);
        Assert.Equal(4, result.JoinWordsPerRow);
        Assert.Same(scene.World, result.QueryWorld);
        Assert.Same(lease.SourceWorld, result.SourceWorld);
        Assert.Same(lease, result.Lease);
        Assert.Equal(lease.GeometryGeneration, result.GeometryGeneration);
        Assert.Equal(lease.Origin, result.Origin);
        Assert.Equal(contact, result.AxisVerdict);
        Assert.Equal(default, result.FootVerdict);
        SupportFootprintResult refusal = SupportFootprintResult.Refused(CapsuleFeatureStatus.CapacityExceeded, 257);
        Assert.Equal(257, refusal.RequiredElements);
        Assert.Equal(0, refusal.Members);
        Assert.Equal(0, refusal.Witnesses);
        Assert.Null(refusal.Lease);
        Assert.Equal(CapsuleFeatureStatus.Unresolved, default(SupportFootprintResult).Status);
        Assert.Throws<ArgumentOutOfRangeException>(() => SupportFootprintResult.Completed(scene.World, lease, 257, 0, default, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => SupportFootprintResult.Completed(scene.World, lease, 1, 3, default, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => SupportFootprintResult.Refused(CapsuleFeatureStatus.Complete));
        Assert.Throws<ArgumentOutOfRangeException>(() => SupportFootprintResult.Refused(CapsuleFeatureStatus.Unsupported, 1));
        Assert.Throws<ArgumentException>(() => SupportFootprintResult.Completed(scene.World, lease, 0, 0,
            contact with { LowestLower = double.NaN }, default));
        lease.Dispose();
        Assert.ThrowsAny<InvalidOperationException>(() => SupportFootprintResult.Completed(scene.World, lease, 0, 0, default, default));
    }

    [Theory]
    [InlineData(FootprintPartition.Boxes, false)]
    [InlineData(FootprintPartition.Hulls, false)]
    [InlineData(FootprintPartition.Compound, false)]
    [InlineData(FootprintPartition.Mesh, false)]
    [InlineData(FootprintPartition.Boxes, true)]
    [InlineData(FootprintPartition.Hulls, true)]
    [InlineData(FootprintPartition.Compound, true)]
    [InlineData(FootprintPartition.Mesh, true)]
    public void HiddenCrateOracleUsesInstalledGeometryAndIgnoresOcclusion(FootprintPartition partition, bool reverse)
    {
        using SupportFootprintScene scene = SupportFootprintScenes.HiddenCrate(partition, reverse);
        SupportFootprintRequest request = Request();
        List<FootprintOracleMember> members = Members(scene, request);
        FootprintOracleSupport support = SupportFootprintOracle.Select(members, request);
        Assert.Equal(SupportStatus.Walkable, support.Status);
        Assert.Equal(scene.Crate, support.Static);
        Assert.InRange(support.Height, 0.0999999, 0.1000001);
        Assert.Contains(members, m => m.Normal.Y > 0 && m.Normal.Y < request.CosMaxSlope &&
            Math.Abs(m.Plane - (-0.09641016)) < 0.000001);
        Assert.All(members.Where(m => m.HasDiscMaximum), m => Assert.True(m.WitnessInsideClosedDisc));
    }

    [Theory]
    [InlineData(FootprintPartition.Boxes, false)]
    [InlineData(FootprintPartition.Hulls, false)]
    [InlineData(FootprintPartition.Compound, false)]
    [InlineData(FootprintPartition.Mesh, false)]
    [InlineData(FootprintPartition.Boxes, true)]
    [InlineData(FootprintPartition.Compound, true)]
    public void ClusterCountsAdmittedRawPairsRatherThanStatics(FootprintPartition partition, bool curved)
    {
        using SupportFootprintScene scene = SupportFootprintScenes.Cluster(257, curved, partition);
        List<FootprintOracleMember> members = Members(scene, Request());
        Assert.Equal(257, members.Count);
        Assert.Equal(257, members.Select(m => (m.Static, m.ElementId)).Distinct().Count());
        Assert.Equal(SupportStatus.Refused, SupportFootprintOracle.Select(members, Request()).Status);
    }

    [Fact]
    public void FanAndGridKeepEveryInstalledTriangleAndRawId()
    {
        using SupportFootprintScene fan = SupportFootprintScenes.Fan(32);
        Assert.Equal(32, Members(fan, Request()).Count);
        Assert.Equal(Enumerable.Range(0, 32), fan.Faces.Select(f => f.ElementId));
        using SupportFootprintScene grid = SupportFootprintScenes.Grid(2);
        Assert.Equal(8, Members(grid, Request()).Count);
    }

    [Fact]
    public void PolygonUsesTheFullDiscAfterVolumeAdmissionAndRetainsRingContext()
    {
        // All ramp vertices lie above the band top, while its axis plane is valid support in the band.
        OracleFace ramp = Face(1, [new(0.15f, 0.075f, -0.02f), new(0.18f, 0.09f, -0.02f),
            new(0.18f, 0.09f, 0.02f), new(0.15f, 0.075f, 0.02f)]);
        FootprintOracleMember member = SupportFootprintOracle.Polygon(ramp, Request(upper: 0.01))!;
        Assert.True(member.HasDiscMaximum);
        Assert.InRange(member.Maximum, 0.08999999, 0.09000001);
        Assert.InRange(member.Plane, -1e-7, 1e-7);
        OracleFace ring = Face(2, [new(0.20004, 0, -0.001), new(0.20006, 0, -0.001),
            new(0.20006, 0, 0.001), new(0.20004, 0, 0.001)]);
        FootprintOracleMember context = SupportFootprintOracle.Polygon(ring, Request())!;
        Assert.True(context.Admitted);
        Assert.False(context.HasDiscMaximum);
        Assert.Empty(context.Witnesses);
        OracleFace high = Face(3, [new(0.15, 3.1, -0.1), new(0.25, 3.25, -0.1),
            new(0.25, 3.25, 0.1), new(0.15, 3.1, 0.1)]);
        Assert.Null(SupportFootprintOracle.Polygon(high, Request()));
        Assert.Throws<NotSupportedException>(() => SupportFootprintOracle.Volume(Request(cosine: 0)));
    }

    [Fact]
    public void FullPolygonsJoinAtRemoteVerticesAndContextPlanesStillCap()
    {
        OracleFace flat = Face(1, [new(-1, 0, -1), new(3, 0, -1), new(3, 0, 1), new(-1, 0, 1)]);
        OracleFace ramp = Face(2, [new(0.1, 0.29, -1), new(3, 0, -1), new(3, 0, 1), new(0.1, 0.29, 1)]);
        Assert.True(SupportFootprintOracle.Joined(flat, ramp, 0.3));
        Assert.False(SupportFootprintOracle.Joined(flat, ramp, 0.0001f));
        // These two convex patches meet only at (0,0,3), outside the admission column.
        OracleFace left = Face(3, [new(-0.001, -0.0001, -0.1), new(0, 0, 3), new(-0.001, -0.0001, 0.1)]);
        OracleFace right = Face(4, [new(0, 0, 0.20004), new(0.1, -0.01, 0.20004),
            new(0.1, -0.01, 3), new(0, 0, 3)]);
        SupportFootprintRequest request = Request(x: 0.05f);
        FootprintOracleMember l = SupportFootprintOracle.Polygon(left, request)!;
        FootprintOracleMember context = SupportFootprintOracle.Polygon(right, request)!;
        Assert.False(context.HasDiscMaximum);
        Assert.True(SupportFootprintOracle.Joined(left, right, 0.0001f));
        FootprintOracleSupport support = SupportFootprintOracle.Select([l, context], request);
        Assert.InRange(support.Height, -0.00500001, -0.00499999);
    }

    [Fact]
    public void CurvedOracleSphereFlankUsesGeometricFootprintWitnessAndSlopeEqualityIsWalkable()
    {
        using var scene = new SupportFootprintScene();
        scene.Install(new SphereShape(0.25f), Pose.At(new(0, -0.25f, 0)));
        SupportFootprintRequest request = Request(x: 0.35f);
        FootprintOracleMember member = Assert.Single(Members(scene, request));
        FootprintOracleWitness witness = Assert.Single(member.Witnesses);
        Assert.Equal(SupportWitnessRole.Footprint, witness.Role);
        Assert.InRange(witness.Position.X, 0.1499999, 0.1500001);
        Assert.InRange(witness.Position.Y, -0.0500001, -0.0499999);
        FootprintOracleSupport support = SupportFootprintOracle.Select([member], request);
        Assert.Equal(SupportStatus.Walkable, support.Status);
        Assert.InRange(support.Height, -0.2000001, -0.1999999);
        (double lower, double upper) = SupportFootprintOracle.PerPointSupremum(scene.Curves[0], request);
        Assert.InRange(-0.2, lower - 1e-7, upper + 1e-7);
        FootprintOracleMember top = Assert.Single(Members(scene, Request(cosine: 1)));
        Assert.Equal([SupportWitnessRole.Axis, SupportWitnessRole.Footprint], top.Witnesses.Select(w => w.Role));
        Assert.Equal(SupportStatus.Walkable, SupportFootprintOracle.Select([top], Request(cosine: 1)).Status);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 45)]
    [InlineData(false, 90)]
    [InlineData(true, 0)]
    [InlineData(true, 45)]
    [InlineData(true, 90)]
    public void CurvedOracleInstalledUprightTiltedAndLyingPosesHaveAttainedRoles(bool cylinder, float degrees)
    {
        using var scene = new SupportFootprintScene();
        Quaternion orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, degrees * MathF.PI / 180);
        StaticHandle owner = scene.Install(cylinder ? new CylinderShape(0.25f, 0.5f) : new CapsuleShape(0.25f, 0.5f),
            new(new(0, -0.25f, 0), orientation));
        SupportFootprintRequest request = Request();
        var curves = SupportFootprintCurvedProofOracle.Installed(scene.World, owner).Select(p => p.Tangent).ToArray();
        List<FootprintOracleMember> members = curves.Select(p => SupportFootprintOracle.Curved(p, request))
            .OfType<FootprintOracleMember>().ToList();
        Assert.NotEmpty(members);
        Assert.Contains(members, m => m.Witnesses.Count > 0);
        foreach (FootprintOracleMember member in members)
        {
            Assert.InRange(member.Witnesses.Count, 0, 2);
            Assert.True(member.WitnessInsideClosedDisc, $"Outside disc: {member}");
            foreach (FootprintOracleWitness witness in member.Witnesses)
            {
                OracleTangent part = curves.Single(c => c.ElementId == member.ElementId);
                Assert.InRange(part.Closest(witness.Position).Distance, 0, 1e-7);
                Assert.True(witness.Normal.Y > 0);
                (double lower, double upper) = SupportFootprintOracle.PerPointSupremum(part, request);
                double tangent = witness.Position.Y + (witness.Normal.X * (witness.Position.X - request.Axis.X) +
                    witness.Normal.Z * (witness.Position.Z - request.Axis.Y)) / witness.Normal.Y;
                Assert.True(lower <= upper);
                Assert.True(upper - lower <= 1e-7);
                Assert.True(Math.Min(tangent, witness.Position.Y) <= upper + 1e-12);
                _output.WriteLine($"Installed cylinder={cylinder} degrees={degrees} part={member.ElementId} role={witness.Role} geometric={Math.Min(tangent, witness.Position.Y):R} perPoint=[{lower:R}, {upper:R}]");
            }
        }
    }

    [Fact]
    public void CurvedOracleRawCapAdmissionDoesNotMoveItsSurfaceWhenReversingTheNormal()
    {
        var cap = new OracleTangent(new StaticHandle(0), 2, OracleSurface.CylinderBottom,
            new(0, 1.25, 0), new(0, 1, 0), 0.25, 0.25);
        Assert.Null(SupportFootprintOracle.Curved(cap, Request()));
    }

    [Fact]
    public void CurvedOracleTiltedSideChoosesTheHighestWalkablePointWhenItsMaximumIsSteep()
    {
        var side = new OracleTangent(new StaticHandle(0), 0, OracleSurface.CylinderSide,
            new(0, 0, 0), new OracleVector(Math.Sqrt(3) / 2, 0.5, 0), 0.25, 0.5);
        SupportFootprintRequest request = Request(z: 0.29f, cosine: 0.8f);
        FootprintOracleMember member = SupportFootprintOracle.Curved(side, request)!;
        FootprintOracleWitness witness = Assert.Single(member.Witnesses);
        Assert.Equal(SupportWitnessRole.Footprint, witness.Role);
        Assert.True(witness.Normal.Y >= request.CosMaxSlope);
        Assert.True(witness.Position.Y < member.Maximum);
        Assert.True(member.WitnessInsideClosedDisc);
        Assert.InRange(side.Closest(witness.Position).Distance, 0, 1e-12);
    }

    [Fact]
    public void CurvedOracleClosedDiscTangencyKeepsAxisAndFootprintRolesWithoutInventingSupport()
    {
        var sphere = new OracleTangent(new StaticHandle(0), 0, OracleSurface.Round,
            new(0, 0, 0), new(0, 1, 0), 0.25, 0);
        FootprintOracleMember member = SupportFootprintOracle.Curved(sphere, Request(x: 0.5f, radius: 0.25f))!;
        FootprintOracleWitness footprint = Assert.Single(member.Witnesses);
        Assert.Equal(SupportWitnessRole.Footprint, footprint.Role);
        Assert.Equal(new OracleVector(0.25, 0, 0), footprint.Position);
        Assert.Equal(0, footprint.Normal.Y);
        Assert.Equal(SupportStatus.None, SupportFootprintOracle.Select([member], Request(x: 0.5f, radius: 0.25f)).Status);
        Assert.Equal((double.NegativeInfinity, double.NegativeInfinity),
            SupportFootprintOracle.PerPointSupremum(sphere, Request(x: 0.5f, radius: 0.25f)));
        FootprintOracleMember axis = SupportFootprintOracle.Curved(sphere, Request(x: 0.25f))!;
        Assert.Equal(SupportWitnessRole.Axis, axis.Witnesses[0].Role);
        Assert.Equal(new OracleVector(0.25, 0, 0), axis.Witnesses[0].Position);
    }

    [Fact]
    public void CurvedOracleHorizontalCapsuleEquatorAxisRetainsWitnessAndFinitePerPointEnclosure()
    {
        using var scene = new SupportFootprintScene();
        scene.Install(new CapsuleShape(0.25f, 1f), new(Vector3.Zero, new Quaternion(0.5f, 0.5f, 0.5f, 0.5f)));
        OracleTangent capsule = Assert.Single(scene.Curves);
        Assert.Equal(OracleSurface.Round, capsule.Surface);
        Assert.Equal(new OracleVector(0, 0, 1), capsule.Axis);
        Assert.Equal(0.25, capsule.Radius);
        Assert.Equal(0.5, capsule.HalfLength);
        SupportFootprintRequest request = Request(x: 0.25f);
        FootprintOracleMember member = SupportFootprintOracle.Curved(capsule, request)!;
        Assert.NotNull(member);
        Assert.True(member.HasDiscMaximum);
        Assert.True(member.WitnessInsideClosedDisc);
        Assert.Equal(2, member.Witnesses.Count);
        FootprintOracleWitness axis = member.Witnesses[0];
        Assert.Equal(SupportWitnessRole.Axis, axis.Role);
        Assert.Equal(new OracleVector(0.25, 0, 0), axis.Position);
        Assert.Equal(new OracleVector(1, 0, 0), axis.Normal);
        Assert.Equal(SupportWitnessRole.Footprint, member.Witnesses[1].Role);
        Assert.True(member.Witnesses[1].Normal.Y > 0);
        (double lower, double upper) = SupportFootprintOracle.PerPointSupremum(capsule, request);
        Assert.True(double.IsFinite(lower));
        Assert.True(double.IsFinite(upper));
        Assert.True(lower <= upper);
        Assert.True(upper - lower <= 1e-7);
        // The ends miss D. At x = 0.25 - (double)0.2f, the side tangent attains this reading.
        Assert.InRange(0.20412414776667051, lower, upper);
        Assert.Equal(SupportFootprintOracle.PerPointSupremum(capsule with { Surface = OracleSurface.CylinderSide }, request),
            (lower, upper));
    }

    [Fact]
    public void CurvedOracleHorizontalCylinderAxisTangencyRetainsEquatorialWitness()
    {
        using var scene = new SupportFootprintScene();
        scene.Install(new CylinderShape(0.25f, 1f), new(Vector3.Zero, new Quaternion(0.5f, 0.5f, 0.5f, 0.5f)));
        OracleTangent side = scene.Curves.Single(c => c.Surface == OracleSurface.CylinderSide);
        Assert.Equal(new OracleVector(0, 0, 1), side.Axis);
        Assert.Equal(0.5, side.HalfLength);
        FootprintOracleMember member = SupportFootprintOracle.Curved(side, Request(x: 0.25f))!;
        Assert.NotNull(member);
        Assert.True(member.HasDiscMaximum);
        Assert.True(member.WitnessInsideClosedDisc);
        Assert.Equal(2, member.Witnesses.Count);
        FootprintOracleWitness axis = member.Witnesses[0];
        Assert.Equal(SupportWitnessRole.Axis, axis.Role);
        Assert.Equal(new OracleVector(0.25, 0, 0), axis.Position);
        Assert.Equal(new OracleVector(1, 0, 0), axis.Normal);
        Assert.Equal(SupportWitnessRole.Footprint, member.Witnesses[1].Role);
        Assert.True(member.Witnesses[1].Normal.Y > 0);
    }

    [Fact]
    public void CurvedOracleHorizontalCylinderFootprintTangencyHasNoSupportOrPerPointReading()
    {
        using var scene = new SupportFootprintScene();
        scene.Install(new CylinderShape(0.25f, 1f), new(Vector3.Zero, new Quaternion(0.5f, 0.5f, 0.5f, 0.5f)));
        OracleTangent side = scene.Curves.Single(c => c.Surface == OracleSurface.CylinderSide);
        SupportFootprintRequest request = Request(x: 0.5f, radius: 0.25f);
        FootprintOracleMember member = SupportFootprintOracle.Curved(side, request)!;
        Assert.NotNull(member);
        Assert.True(member.HasDiscMaximum);
        Assert.True(member.WitnessInsideClosedDisc);
        Assert.Equal(0, member.Maximum);
        FootprintOracleWitness footprint = Assert.Single(member.Witnesses);
        Assert.Equal(SupportWitnessRole.Footprint, footprint.Role);
        Assert.Equal(new OracleVector(0.25, 0, 0), footprint.Position);
        Assert.Equal(new OracleVector(1, 0, 0), footprint.Normal);
        Assert.Equal(SupportStatus.None, SupportFootprintOracle.Select([member], request).Status);
        Assert.Equal((double.NegativeInfinity, double.NegativeInfinity),
            SupportFootprintOracle.PerPointSupremum(side, request));
    }

    [Fact]
    public void VolumeTangencyAtOneRemoteVertexCannotAdmitAnEmptyDiscIntersection()
    {
        SupportFootprintRequest request = Request();
        double top = SupportFootprintOracle.Volume(request).Top;
        OracleFace remote = Face(0, [new(2, top, 0), new(3, top + 1, 0), new(2, top + 1, 1)]);
        Assert.Null(SupportFootprintOracle.Polygon(remote, request));
    }

    static OracleFace Face(int owner, OracleVector[] vertices)
    {
        OracleFace face = new(new StaticHandle(owner), 0, vertices);
        return face.Normal.Y < 0 ? new(face.Static, 0, vertices.Reverse().ToArray()) : face;
    }
}
