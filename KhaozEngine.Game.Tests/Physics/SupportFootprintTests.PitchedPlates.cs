using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using R = KhaozEngine.Tests.Physics.InstalledPoseOracle.R;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    public static IEnumerable<object[]> PitchedPlateRows()
    {
        (float Yaw, float[] Pitches)[] angles =
        [
            (0.37f, [-0.1f, -0.05f, 0.05f, 0.1f]),
            (0.77f, [-0.10471976f, -0.05235988f, 0.05235988f, 0.10471976f]),
        ];
        foreach (var (yaw, pitches) in angles)
            foreach (float pitch in pitches)
                foreach (string placement in new[] { "floating", "resting", "intersecting" })
                    foreach (string partition in new[] { "compound", "split", "standalone" })
                        yield return [yaw, pitch, placement, partition];
    }

    [Theory]
    [MemberData(nameof(PitchedPlateRows))]
    public void PitchedPlateSupportsAcrossPartitions(float yaw, float pitch, string placement, string partition)
    {
        // The oracle reads each installation's actual backend shape and composed pose. Its rational plane,
        // closed-disc maximum and direct same-kind caps are independent of both production evaluators.
        for (int rebuild = 0; rebuild < 2; rebuild++)
        {
            using SupportFootprintScene scene = PitchedPlate(yaw, pitch, placement, partition);
            FootSupportQuery query = PlateQuery();
            SupportFootprintRequest request = FootSupport.DescribeFootprint(query);
            FootprintOracleSupport expected = SupportFootprintOracle.Select(Members(scene, request), request);
            Assert.Equal(SupportStatus.Walkable, expected.Status);
            using IPhysicsQueryLease lease = scene.World.AcquireQueryReadLease();
            Check(FootSupport.Find(null, scene.World, lease, query));
            Check(FootSupport.Find(null, scene.World, lease, query));
            FootSupport.Find(null, scene.World, lease, query with { Axis = new(0.15f, -0.1f) });
            Check(FootSupport.Find(null, scene.World, lease, query));
            using (SupportReferenceEvaluation.Begin())
            {
                Check(FootSupport.Find(null, scene.World, lease, query));
                Check(FootSupport.Find(null, scene.World, lease, query));
                FootSupport.Find(null, scene.World, lease, query with { Axis = new(-0.1f, 0.1f) });
                Check(FootSupport.Find(null, scene.World, lease, query));
            }

            void Check(SupportSample sample)
            {
                Assert.Equal(SupportStatus.Walkable, sample.Status);
                Assert.Equal(expected.Static, sample.Static);
                Assert.Equal(expected.ElementId, sample.FeatureId);
                Assert.True((double)sample.Height - sample.HeightError <= expected.HeightLower);
                Assert.True((double)sample.Height + sample.HeightError >= expected.HeightUpper);
                Assert.InRange(sample.HeightError, 0f, 0.0005f);
            }
        }
    }

    [Theory]
    [InlineData(0.37f, 0.05f)]
    [InlineData(0.37f, 0.1f)]
    [InlineData(0.77f, 0.05235988f)]
    public void PitchedPlateDiscardsOutsideExtremumBeforePlaneHeight(float yaw, float pitch)
    {
        foreach (string partition in new[] { "compound", "standalone" })
        {
            using SupportFootprintScene scene = PitchedPlate(yaw, pitch, "resting", partition);
            FootSupportQuery query = PlateQuery();
            SupportFootprintRequest request = FootSupport.DescribeFootprint(query);
            int sideId = partition == "compound" ? 256 : 0;
            SupportPolygon side = CapturedPolygons(scene, request)
                .Single(p => p.Static == scene.Crate && p.ElementId == sideId);
            OracleFace face = scene.Faces.Single(f => f.Static == scene.Crate && f.ElementId == sideId);
            var vertices = SupportFootprintExactOracle.Vertices(face);
            var normal = SupportFootprintExactOracle.Normal(vertices);
            int orientation = normal.Y.Numerator.Sign;
            Assert.NotEqual(0, orientation);
            Assert.True(side.Normal.Y.Bounds.Lower < 0 && side.Normal.Y.Bounds.Upper > 0);
            var radius = R.From(request.Radius) + R.From(request.ContactBand);
            var nx = -normal.X * new R(orientation, 1);
            var nz = -normal.Z * new R(orientation, 1);
            var squared = nx.Square() + nz.Square();
            // Membership is decided by rational signs of k*sqrt(squared)+l, without computing plane Y.
            int outsideEdge = Enumerable.Range(0, vertices.Length).First(i =>
            {
                var edge = vertices[(i + 1) % vertices.Length] - vertices[i];
                var k = -edge.Z * vertices[i].X + edge.X * vertices[i].Z;
                var l = radius * (edge.Z * nx - edge.X * nz);
                return PlateRadicalSign(l, k, squared) * orientation < 0;
            });
            GeometrySign expectedSide = orientation > 0 ? GeometrySign.Negative : GeometrySign.Positive;
            Assert.Equal(expectedSide, side.SourcePredicates.Interior(side, outsideEdge,
                FeaturePoint.Exact(Vector3.Zero), FeatureNumber.Exact((double)request.Radius + request.ContactBand),
                -orientation));
            FeatureNumber uphillX = side.Normal.X.Multiply(FeatureNumber.Exact(-orientation), true);
            FeatureNumber uphillZ = side.Normal.Z.Multiply(FeatureNumber.Exact(-orientation), true);
            FeatureNumber length = FeatureNumber.Enclosed(uphillX.Multiply(uphillX, true)
                .Add(uphillZ.Multiply(uphillZ, true)).Bounds.Sqrt());
            FeatureNumber scale = FeatureNumber.Exact((double)request.Radius + request.ContactBand).Divide(length);
            FeaturePoint first = side.Vertices[0];
            FeatureNumber height = first.Y.Add(side.Normal.X.Multiply(first.X.Subtract(uphillX.Multiply(scale, true)), true)
                .Add(side.Normal.Z.Multiply(first.Z.Subtract(uphillZ.Multiply(scale, true)), true)).Divide(side.Normal.Y));
            Assert.False(height.IsResolved);
            AssertPlateQueryComplete(scene, query);
        }
    }

    [Fact]
    public void PitchedPlateSeparatesOutsideFaceBeforeOrientation()
    {
        foreach (string partition in new[] { "compound", "standalone" })
        {
            using SupportFootprintScene scene = PitchedPlate(0.77f, 0.10471976f, "resting", partition);
            SupportFootprintRequest request = FootSupport.DescribeFootprint(PlateQuery());
            int sideId = partition == "compound" ? 256 : 0;
            SupportPolygon side = CapturedPolygons(scene, request)
                .Single(p => p.Static == scene.Crate && p.ElementId == sideId);
            Assert.Equal(GeometrySign.Unresolved, SupportFootprintPolygon.Orientation(side));
            OracleFace face = scene.Faces.Single(f => f.Static == scene.Crate && f.ElementId == sideId);
            R nx = R.From(-0.7), nz = R.From(0.7);
            R radius = R.From(request.Radius) + R.From(request.ContactBand);
            foreach (var vertex in SupportFootprintExactOracle.Vertices(face))
            {
                R projection = nx * vertex.X + nz * vertex.Z;
                Assert.True(projection > R.Zero);
                Assert.True(projection.Square() - radius.Square() * (nx.Square() + nz.Square()) > R.Zero);
            }
            var zero = FeatureNumber.Exact(0);
            var expanded = FeatureNumber.Exact((double)request.Radius + request.ContactBand);
            Assert.Equal(GeometrySign.Positive, side.SourcePredicates.ProjectedSeparation(side, -0.7, 0.7,
                zero, zero, expanded));
            Assert.Equal(GeometrySign.Positive, SupportProjectedPolygonSeparation.Certify(side, zero, zero, expanded));
            AssertPlateQueryComplete(scene, PlateQuery());
        }
    }

    [Fact]
    public void ProjectedSeparationKeepsClosedTangencyAndRingContext()
    {
        var zero = FeatureNumber.Exact(0);
        var radius = FeatureNumber.Exact(0.25);
        foreach (float x in new[] { MathF.BitDecrement(0.25f), 0.25f, MathF.BitIncrement(0.25f) })
            foreach (bool reverse in new[] { false, true })
            {
                GeometrySign expected = x < 0.25f ? GeometrySign.Negative :
                    x > 0.25f ? GeometrySign.Positive : GeometrySign.Zero;
                Vector3[][] projections =
                [
                    [new(x, -0.1f, -1), new(x, 0.1f, -1), new(x, 0.1f, 1), new(x, -0.1f, 1)],
                    [new(x, -0.1f, 0), new(x, 0, 0), new(x, 0.1f, 0)],
                    [new(x, 0, -1), new(x, 0, 0), new(x, 0, 1)],
                ];
                foreach (Vector3[] vertices in projections)
                {
                    if (reverse) Array.Reverse(vertices);
                    SupportPolygon polygon = PlateProjection(vertices);
                    Assert.Equal(expected, SupportProjectedPolygonSeparation.Certify(polygon, zero, zero, radius));
                }
                SupportPolygon source = PlanarSource(projections[0]);
                Assert.Equal(expected, source.SourcePredicates.ProjectedSeparation(source, 1, 0, zero, zero, radius));
            }

        using var scene = new SupportFootprintScene();
        float ringX = 0.2f + 0.00005f;
        StaticHandle slope = scene.Install(new TriangleMeshShape(
            [new(0.1998f, 0.1f - 0.25f * 0.1998f, -0.001f),
                new(0.1998f, 0.1f - 0.25f * 0.1998f, 0.001f),
                new(ringX, 0.1f - 0.25f * ringX, 0)], [0, 2, 1]), Pose.Identity);
        StaticHandle context = scene.Install(new TriangleMeshShape(
            [new(ringX, 0.05f - 0.00001f, -0.001f), new(0.20009f, 0.05f - 0.00001f, -0.001f),
                new(ringX, 0.05f + 0.00001f, 0.001f)], [0, 1, 2]), Pose.Identity);
        FootSupportQuery query = PlateQuery();
        SupportFootprintRequest request = FootSupport.DescribeFootprint(query);
        SupportPolygon ring = CapturedPolygons(scene, request).Single(p => p.Static == context);
        Assert.NotEqual(GeometrySign.Zero, ring.Normal.Z.Sign);
        Assert.Equal(GeometrySign.Positive, SupportProjectedPolygonSeparation.Certify(ring, zero, zero,
            FeatureNumber.Exact(request.Radius)));
        Assert.Equal(GeometrySign.Negative, SupportProjectedPolygonSeparation.Certify(ring, zero, zero,
            FeatureNumber.Exact((double)request.Radius + request.ContactBand)));
        var expectedSupport = SupportFootprintOracle.Select(Members(scene, request), request);
        Assert.Equal(SupportStatus.Walkable, expectedSupport.Status);
        Assert.InRange(expectedSupport.Height, (double)0.05f - 1e-8, (double)0.05f + 1e-8);
        foreach (bool reference in new[] { false, true })
        {
            using IDisposable? evaluation = reference ? SupportReferenceEvaluation.Begin() : null;
            using IPhysicsQueryLease lease = scene.World.AcquireQueryReadLease();
            var members = new SupportFootprintMember[256];
            var joins = new ulong[1024];
            SupportFootprintResult result = Capability(scene.World).QuerySupportFootprint(lease, request, members, joins);
            Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
            int contextIndex = Array.FindIndex(members, 0, result.Members, m => m.Static == context);
            int slopeIndex = Array.FindIndex(members, 0, result.Members, m => m.Static == slope);
            Assert.True(contextIndex >= 0 && slopeIndex >= 0);
            Assert.False(members[contextIndex].HasDiscMaximum);
            Assert.Equal(0, members[contextIndex].WitnessCount);
            Assert.Equal(1UL << contextIndex, joins[slopeIndex * result.JoinWordsPerRow] & (1UL << contextIndex));
            SupportSample sample = FootSupport.Find(null, scene.World, lease, query);
            Assert.Equal(SupportStatus.Walkable, sample.Status);
            Assert.True((double)sample.Height - sample.HeightError <= expectedSupport.HeightLower);
            Assert.True((double)sample.Height + sample.HeightError >= expectedSupport.HeightUpper);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PitchedPlateRelevantUncertaintyStillRefusesAtomically(bool membership)
    {
        using var scene = new SupportFootprintScene();
        scene.Install(new BoxShape(new(2, 1, 2)), Pose.At(new(0, -1, 0)));
        scene.Crate = scene.Install(new BoxShape(new(1, 0.05f, 1)), Pose.At(new(0, 0.05f, 0)));
        FootSupportQuery query = PlateQuery();
        SupportFootprintRequest request = FootSupport.DescribeFootprint(query);
        using IPhysicsQueryLease lease = scene.World.AcquireQueryReadLease();
        Capability(scene.World).QuerySupportFootprint(lease, request, new SupportFootprintMember[256], new ulong[1024]);
        SupportFootprintCollector scratch = FootprintScratch(scene.World);
        var polygons = (List<SupportPolygon>)typeof(SupportFootprintCollector)
            .GetField("_polygons", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scratch)!;
        int topId = scene.Faces.Single(f => f.Static == scene.Crate && f.Normal.Y > 0.5).ElementId;
        int injected = 0;
        ContactAllocationDiagnostics.SetObserver((phase, entering) =>
        {
            if (!entering || phase != ContactAllocationPhase.SupportExtrema) return;
            foreach (SupportPolygon polygon in polygons)
                if (polygon.Static == scene.Crate && polygon.ElementId == topId)
                {
                    PlateUncertainFixture(polygon, membership);
                    Assert.False(polygon.HasSource);
                    injected++;
                }
        });
        try
        {
            foreach (bool reference in new[] { false, true })
            {
                using IDisposable? evaluation = reference ? SupportReferenceEvaluation.Begin() : null;
                var members = MemberSentinels(256);
                var before = (SupportFootprintMember[])members.Clone();
                var joins = Enumerable.Repeat(73UL, 1024).ToArray();
                SupportFootprintResult result = Capability(scene.World).QuerySupportFootprint(lease, request, members, joins);
                Assert.Equal(CapsuleFeatureStatus.Unsupported, result.Status);
                Assert.Equal(0, result.Members);
                Assert.Equal(before, members);
                Assert.All(joins, word => Assert.Equal(73UL, word));
                Assert.Equal(SupportStatus.Refused, FootSupport.Find(null, scene.World, lease, query).Status);
            }
            Assert.True(injected >= 4);
        }
        finally { ContactAllocationDiagnostics.SetObserver(null); }
    }

    [Fact]
    public void ProjectedSeparationChecksTheWholeFaceAndKeepsUncertainProjection()
    {
        // Every boundary point is outside this disc, but the convex interior contains the whole disc.
        // Squared support comparisons without their projection sign guard would wrongly exclude it.
        SupportPolygon source = PlanarSource([new(0.5f, 0, -1), new(0.5f, 0, 1),
            new(-0.5f, 0, 1), new(-0.5f, 0, -1)]);
        var zero = FeatureNumber.Exact(0);
        var radius = FeatureNumber.Exact(0.25);
        Assert.Equal(GeometrySign.Negative,
            source.SourcePredicates.ProjectedSeparation(source, 1, 0, zero, zero, radius));
        Assert.NotEqual(GeometrySign.Positive, SupportProjectedPolygonSeparation.Certify(source, zero, zero, radius));

        SupportPolygon uncertain = PlateProjection([new(0.25f, -1, 0), new(0.25f, 0, 0), new(0.25f, 1, 0)]);
        FeatureNumber x = FeatureNumber.Enclosed(GeometryInterval.Enclose(0.24, 0.26));
        for (int i = 0; i < uncertain.Vertices.Length; i++)
            uncertain.Set(i, uncertain.Vertices[i] with { X = x }, uncertain.Edges[i]);
        uncertain.Finish(FeaturePoint.Exact(Vector3.UnitX));
        Assert.False(uncertain.HasSource);
        Assert.Equal(GeometrySign.Unresolved, SupportProjectedPolygonSeparation.Certify(uncertain, zero, zero, radius));
    }

    static void PlateUncertainFixture(SupportPolygon polygon, bool membership)
    {
        FeatureNumber uncertain = FeatureNumber.Enclosed(GeometryInterval.Enclose(-0.01, 0.01));
        FeatureNumber zero = FeatureNumber.Exact(0), one = FeatureNumber.Exact(1);
        FeaturePoint[] vertices = membership
            ? [new(uncertain, FeatureNumber.Exact(0.1), one.Negate()),
                new(uncertain, FeatureNumber.Exact(0.1), one),
                new(one, FeatureNumber.Exact(0.1), one), new(one, FeatureNumber.Exact(0.1), one.Negate())]
            : [FeaturePoint.Exact(new(0, 0, -1)), FeaturePoint.Exact(new(0, 0.2f, -1)),
                FeaturePoint.Exact(new(0, 0.2f, 1)), FeaturePoint.Exact(new(0, 0, 1))];
        polygon.Begin(polygon.Static, polygon.ElementId, vertices.Length);
        for (int i = 0; i < vertices.Length; i++)
            polygon.Set(i, vertices[i], FeaturePoint.Subtract(vertices[(i + 1) % vertices.Length], vertices[i]));
        polygon.Finish(membership ? new(zero, one, zero) : new(one, uncertain, zero));
    }

    static SupportPolygon PlateProjection(Vector3[] vertices)
    {
        var polygon = new SupportPolygon();
        polygon.Begin(new StaticHandle(42), 7, vertices.Length);
        for (int i = 0; i < vertices.Length; i++)
        {
            FeaturePoint vertex = FeaturePoint.Exact(vertices[i]);
            polygon.Set(i, vertex, FeaturePoint.Subtract(FeaturePoint.Exact(vertices[(i + 1) % vertices.Length]), vertex));
        }
        polygon.Finish(FeaturePoint.Exact(Vector3.UnitX));
        return polygon;
    }

    static int PlateRadicalSign(R k, R l, R d)
    {
        int ks = k.Numerator.Sign, ls = l.Numerator.Sign;
        if (ls == 0 || d == R.Zero) return ks;
        if (ks == 0 || ks == ls) return ls;
        int comparison = (k.Square() - l.Square() * d).Numerator.Sign;
        return comparison == 0 ? 0 : comparison > 0 ? ks : ls;
    }

    static void AssertPlateQueryComplete(SupportFootprintScene scene, FootSupportQuery query)
    {
        using IPhysicsQueryLease lease = scene.World.AcquireQueryReadLease();
        foreach (bool reference in new[] { false, true })
        {
            using IDisposable? evaluation = reference ? SupportReferenceEvaluation.Begin() : null;
            Assert.Equal(CapsuleFeatureStatus.Complete, Capability(scene.World).QuerySupportFootprint(lease,
                FootSupport.DescribeFootprint(query), new SupportFootprintMember[256], new ulong[1024]).Status);
            Assert.Equal(SupportStatus.Walkable, FootSupport.Find(null, scene.World, lease, query).Status);
        }
    }

    static FootSupportQuery PlateQuery() => new(Vector2.Zero, 0f, 0.2f, 0.4f, 0.4f,
        MathF.Cos(MathF.PI / 4));

    static SupportFootprintScene PitchedPlate(float yaw, float pitch, string placement, string partition)
    {
        var scene = new SupportFootprintScene();
        float y = placement switch
        {
            "floating" => 1.1f,
            "resting" => (float)(1 + Math.Cos(pitch) * 0.05 + Math.Abs(Math.Sin(pitch)) * 0.25),
            "intersecting" => 1.05f,
            _ => throw new ArgumentOutOfRangeException(nameof(placement)),
        };
        var local = new Pose(new(0.1f, y, 0.05f), Quaternion.CreateFromYawPitchRoll(yaw, pitch, 0));
        var floor = new BoxShape(new(2, 1, 2));
        var plate = new BoxShape(new(0.25f, 0.05f, 0.25f));
        if (partition == "compound")
            scene.Crate = scene.Install(new CompoundShape(
                [new CompoundChild(floor, Pose.Identity), new CompoundChild(plate, local)]), Pose.At(new(0, -1, 0)));
        else
        {
            if (partition == "split") scene.Install(floor, Pose.At(new(0, -1, 0)));
            else Assert.Equal("standalone", partition);
            // This is the same represented composition as the identity-oriented compound parent.
            scene.Crate = scene.Install(plate, local with { Position = local.Position + new Vector3(0, -1, 0) });
        }
        return scene;
    }
}
