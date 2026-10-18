using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Physics;
using Xunit;
using StaticHandle = KhaozEngine.Physics.StaticHandle;

namespace KhaozEngine.Tests.Locomotion.Contacts;

[Collection("AllocSensitive")]
public sealed class SupportFootprintStairStratumTests
{
    static readonly JsonSerializerOptions MemberJson = new() { IncludeFields = true };

    [Theory]
    [InlineData(0, 0, 0.25f, true)]
    [InlineData(1, 0, 0.25f, true)]
    [InlineData(2, 0, 1, true)]
    [InlineData(2, 1, 1, true)]
    [InlineData(1.75f, 2, 1.25f, true)]
    [InlineData(1.75f, 1.75f, 1, false)]
    public void StairStratumMatchesGenericAtTangency(float x, float z, float radius, bool supported)
    {
        SupportPolygon polygon = Rectangle();
        SupportFootprintMember member = Matches(polygon, Request(x, z, radius), out SupportQueryCounts counts);
        Assert.Equal(supported, member.HasDiscMaximum);
        Assert.True(counts.StairStratum > 0);
    }

    [Fact]
    public void StairStratumPreservesWitnessOrder()
    {
        for (int first = 0; first < 4; first++)
        {
            SupportPolygon polygon = Rectangle(first: first);
            SupportFootprintMember covered = Matches(polygon, Request(0, 0, 2), out SupportQueryCounts counts);
            Assert.Equal(new Vector3(0, 0.5f, 0), covered.First.Position);
            Assert.True(counts.Extrema > 2);
            SupportFootprintMember outside = Matches(polygon, Request(2, 0, 2), out _);
            Vector3 expected = first == 3 ? new(1, 0.5f, -1) : new(1, 0.5f, 1);
            Assert.Equal(expected, outside.First.Position);
            Assert.Equal(0f, outside.First.PositionErrorMetres);
        }
    }

    [Fact]
    public void StairStratumFallsThroughOffExactDomain()
    {
        FeaturePoint[] horizontal = Points();
        FeaturePoint[] tilted = Points();
        // A represented hull face can differ from the authored horizontal face by one double ulp.
        tilted[2] = tilted[2] with { Y = FeatureNumber.Exact(Math.BitIncrement(0.5)) };
        tilted[3] = tilted[3] with { Y = FeatureNumber.Exact(Math.BitIncrement(0.5)) };
        FeaturePoint[] enclosed = Points();
        enclosed[0] = enclosed[0] with { X = FeatureNumber.Enclosed(GeometryInterval.Enclose(-1.0000001, -0.9999999)) };
        FeaturePoint[] yawed = [.. horizontal.Select(p => new FeaturePoint(p.X.Add(p.Z), p.Y, p.Z.Subtract(p.X)))];
        SupportPolygon contrary = Polygon([.. horizontal.Reverse()]);
        contrary.Finish(new(FeatureNumber.Exact(0), FeatureNumber.Exact(4), FeatureNumber.Exact(0)));
        foreach (SupportPolygon polygon in new[] { Polygon(tilted), Polygon(enclosed), Polygon(yawed),
            Polygon(horizontal[..3]), Polygon([.. horizontal.Reverse()]), contrary,
            Installed(Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.37f)),
            Installed(Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.00000001f)),
            // An exact translation leaves the rotation as the only reason to fall through.
            Installed(Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.37f), Vector3.Zero),
            Installed(Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.00000001f), Vector3.Zero) })
        {
            Matches(polygon, Request(0, 0, 0.25f), out SupportQueryCounts counts);
            Assert.Equal(0, counts.StairStratum);
        }
        Matches(Rectangle(offset: Math.ScaleB(1, -30)), Request(0, 0, 0.25f), out SupportQueryCounts exact);
        Assert.True(exact.StairStratum > 0);
        Matches(Installed(Quaternion.Identity, Vector3.Zero), Request(0, 0, 0.25f), out SupportQueryCounts unrotated);
        Assert.True(unrotated.StairStratum > 0);
    }

    [Fact]
    public void StairStratumMatchesGenericOnBothSidesOfClosedBoundaries()
    {
        foreach (float x in new[] { MathF.BitDecrement(2), 2, MathF.BitIncrement(2) })
        {
            SupportFootprintMember edge = Matches(Rectangle(), Request(x, 0, 1), out _);
            Assert.Equal(x <= 2, edge.HasDiscMaximum);
            SupportFootprintMember vertex = Matches(Rectangle(), Request(1.75f, x, 1.25f), out _);
            Assert.Equal(x <= 2, vertex.HasDiscMaximum);
        }
        Matches(Rectangle(), Request(-0f, -0f, 0.25f), out _);
    }

    [Fact]
    public void ReferenceEvaluationBypassesStairStratum()
    {
        SupportPolygon polygon = Rectangle();
        Measure(polygon, Request(0, 0, 0.25f), false, out _, out SupportQueryCounts optimized);
        Measure(polygon, Request(0, 0, 0.25f), true, out _, out SupportQueryCounts reference);
        Assert.Equal(2, optimized.StairStratum);
        Assert.Equal(0, reference.StairStratum);
        Assert.Equal(reference.Extrema, optimized.Extrema);
    }

    [Theory]
    [InlineData(256)]
    [InlineData(257)]
    public void StairStratumKeepsCapacityAndBandVerdicts(int count)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        for (int i = 0; i < count - 1; i++)
            world.AddStatic(new BoxShape(new(2, 2, 2)), Pose.At(new(0, -2.40005f, 0)));
        // Each below-band rectangle meets this in-band slope within the released contact band. Its retained
        // context must still consume capacity, even though the rectangle itself cannot contribute in the band.
        StaticHandle slope = world.AddStatic(new TriangleMeshShape(
            [new(-0.5f, -0.399985f, -0.5f), new(-0.5f, -0.399985f, 0.5f), new(0.5f, -0.399975f, 0)],
            [0, 2, 1]), Pose.Identity);
        using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
        SupportFootprintRequest request = SupportFootprintTests.Request(radius: 0.25f, lower: -(double)0.4f);
        SupportQueryDiagnostics.ThreadState prior = SupportQueryDiagnostics.Exchange(
            SupportQueryDiagnostics.ThreadState.Fresh(true));
        try
        {
            SupportFootprintResult result = SupportFootprintEquivalence.AssertMatchesReference(world, lease, request);
            Assert.True(SupportQueryDiagnostics.Snapshot().StairStratum >= 2 * (count - 1));
            Assert.Equal(count == 256 ? CapsuleFeatureStatus.Complete : CapsuleFeatureStatus.CapacityExceeded,
                result.Status);
            Assert.Equal(count == 256 ? count : 0, result.Members);
            if (count == 256)
            {
                var members = new SupportFootprintMember[256];
                world.QuerySupportFootprint(lease, request, members, new ulong[1024]);
                Assert.All(members.Where(m => m.Static != slope), m =>
                {
                    Assert.True(m.HasDiscMaximum);
                    Assert.True(m.DiscHeightUpper < request.BandLower);
                });
                Assert.Single(members, m => m.Static == slope && m.DiscHeightLower > request.BandLower);
            }
            else
            {
                Assert.Equal(257, result.RequiredElements);
                var sentinel = default(SupportFootprintMember) with { ElementId = -123 };
                SupportFootprintMember[] members = Enumerable.Repeat(sentinel, 256).ToArray();
                ulong[] joins = Enumerable.Repeat(99UL, 1024).ToArray();
                Assert.Equal(CapsuleFeatureStatus.CapacityExceeded,
                    world.QuerySupportFootprint(lease, request, members, joins).Status);
                Assert.All(members, m => Assert.Equal(sentinel, m));
                Assert.All(joins, word => Assert.Equal(99UL, word));
            }
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }
    }

    // Optimized Extrema counts before the stratum, for the scenes whose reference skips SC-14 certificates.
    static readonly Dictionary<string, int> PreStratumExtrema = new()
    {
        ["stairs box"] = 18, ["stairs hull"] = 18, ["hidden crate"] = 23,
    };

    [Fact]
    public void StairStratumMatchesReferenceOnEveryCostScene()
    {
        SupportQueryDiagnostics.ThreadState prior = SupportQueryDiagnostics.Exchange(
            SupportQueryDiagnostics.ThreadState.Fresh(true));
        try
        {
            foreach (SupportCostRow row in SupportGateCostScenes.Rows().Select(r => (SupportCostRow)r[0]))
            {
                using SupportFootprintScene scene = SupportGateCostScenes.Build(row);
                using IPhysicsQueryLease lease = scene.World.AcquireQueryReadLease();
                Func<float, float, float>? height = row.Analytic ? (x, _) => row.HeightAt(x) : null;
                Func<float, float, Vector3>? normal = row.Analytic ? (x, _) => row.NormalAt(x) : null;
                SupportQueryDiagnostics.Reset();
                var runs = new List<SupportQueryCounts>();
                SupportFootprintEquivalence.AssertMatchesReference(() =>
                {
                    SupportQueryCounts before = SupportQueryDiagnostics.Snapshot();
                    SupportSample sample = FootSupport.Find(height, normal, scene.World, lease, row.Query);
                    runs.Add(SupportQueryDiagnostics.Snapshot() - before);
                    return sample;
                }, row.Name);
                if (row.Name == "stairs box") Assert.True(runs[0].StairStratum > 0);
                // The stratum adds every candidate the generic path adds. The vertical admission certificate (SC-14)
                // skips the reference's admission extrema of the faces it decides, so those scenes compare against
                // the optimized count recorded before the stratum existed (976c487d7 and the research 3 trace).
                if (runs[0].AdmissionCertificates == 0) Assert.Equal(runs[1].Extrema, runs[0].Extrema);
                else
                {
                    Assert.True(PreStratumExtrema.TryGetValue(row.Name, out int golden), row.Name);
                    Assert.Equal(golden, runs[0].Extrema);
                }
                SupportFootprintEquivalence.AssertMatchesReference(scene.World, lease,
                    FootSupport.DescribeFootprint(row.Query), row.Name);
            }
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }
    }

    static SupportFootprintRequest Request(float x, float z, float radius) =>
        SupportFootprintTests.Request(x, z, radius, lower: 0, upper: 1);

    static FeaturePoint[] Points(double offset = 0) =>
    [
        Point(-1 + offset, 0.5, -1), Point(-1 + offset, 0.5, 1),
        Point(1 + offset, 0.5, 1), Point(1 + offset, 0.5, -1),
    ];

    static FeaturePoint Point(double x, double y, double z) =>
        new(FeatureNumber.Exact(x), FeatureNumber.Exact(y), FeatureNumber.Exact(z));

    static SupportPolygon Rectangle(int first = 0, double offset = 0)
    {
        FeaturePoint[] points = Points(offset);
        return Polygon([.. Enumerable.Range(0, 4).Select(i => points[(i + first) % 4])]);
    }

    static SupportPolygon Polygon(FeaturePoint[] points)
    {
        var polygon = new SupportPolygon();
        polygon.Begin(new StaticHandle(42), 7, points.Length);
        for (int i = 0; i < points.Length; i++)
            polygon.Set(i, points[i], FeaturePoint.Subtract(points[(i + 1) % points.Length], points[i]));
        polygon.Finish(FeaturePoint.Cross(polygon.Edges[0], FeaturePoint.Subtract(points[2], points[0]), true));
        return polygon;
    }

    static SupportPolygon Installed(Quaternion rotation) => Installed(rotation, new Vector3(0.00000001f, 0, 0));

    static SupportPolygon Installed(Quaternion rotation, Vector3 translation)
    {
        Assert.True(InstalledPoseOperator.TryCreate(new BepuPhysics.RigidPose(translation, rotation),
            out InstalledPoseOperator map));
        Vector3[] local = [new(-1, 0.5f, -1), new(-1, 0.5f, 1), new(1, 0.5f, 1), new(1, 0.5f, -1)];
        var polygon = new SupportPolygon();
        polygon.Begin(new StaticHandle(42), 7, 4);
        polygon.BeginSource(map);
        for (int i = 0; i < 4; i++)
        {
            polygon.SetSource(i, local[i]);
            polygon.Set(i, map.Point(local[i], true), map.Edge(local[i], local[(i + 1) % 4], true));
        }
        polygon.Finish(FeaturePoint.Cross(polygon.Edges[0],
            FeaturePoint.Subtract(polygon.Vertices[2], polygon.Vertices[0]), true));
        Assert.True(polygon.HasSource);
        return polygon;
    }

    static SupportFootprintMember Matches(SupportPolygon polygon, SupportFootprintRequest request,
        out SupportQueryCounts counts)
    {
        GeometrySign reference = Measure(polygon, request, true, out SupportFootprintMember expected,
            out SupportQueryCounts referenceCounts);
        Assert.Equal(reference, Measure(polygon, request, false, out SupportFootprintMember actual, out counts));
        Assert.Equal(expected, actual);
        // The round-trip JSON comparison also distinguishes signed zeros in every published numeric field.
        Assert.Equal(JsonSerializer.Serialize(expected, MemberJson), JsonSerializer.Serialize(actual, MemberJson));
        Assert.Equal(referenceCounts.Extrema, counts.Extrema);
        return actual;
    }

    static GeometrySign Measure(SupportPolygon polygon, SupportFootprintRequest request, bool reference,
        out SupportFootprintMember member, out SupportQueryCounts counts)
    {
        SupportQueryDiagnostics.ThreadState prior = SupportQueryDiagnostics.Exchange(
            SupportQueryDiagnostics.ThreadState.Fresh(true));
        try
        {
            GeometrySign result;
            if (reference)
                using (SupportReferenceEvaluation.Begin()) result = SupportFootprintPolygon.Measure(polygon, request, out member);
            else result = SupportFootprintPolygon.Measure(polygon, request, out member);
            counts = SupportQueryDiagnostics.Snapshot();
            return result;
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }
    }
}
