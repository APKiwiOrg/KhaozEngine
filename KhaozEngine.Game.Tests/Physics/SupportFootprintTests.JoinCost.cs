using System;
using System.Collections;
using System.Linq;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    [Fact]
    public void SharedApexFastPathPreservesWholePolygonVerdicts()
    {
        const int count = 96;
        var polygons = new SupportPolygon[count];
        for (int i = 0; i < count; i++)
        {
            float a = 2 * MathF.PI * i / count, b = 2 * MathF.PI * (i + 1) / count;
            polygons[i] = Polygon([Vector3.Zero, new(MathF.Cos(a), 0, MathF.Sin(a)),
                new(MathF.Cos(b), 0, MathF.Sin(b))], out _);
            Assert.True(polygons[i].HasJoinIncidenceCertificate,
                $"Fan {i}, resolved {polygons[i].IsResolved}, normal exact {polygons[i].Normal.IsExact}, " +
                $"normal X {polygons[i].Normal.X.IsExact}/{polygons[i].Normal.X.Value}, " +
                $"normal Z {polygons[i].Normal.Z.IsExact}/{polygons[i].Normal.Z.Value}, " +
                $"length {polygons[i].NormalLength.Lower}, edges {string.Join(',', polygons[i].Edges.ToArray().Select(e => e.IsExact))}");
        }
        var released = new SupportSegmentPolygon();
        var optimized = new SupportSegmentPolygon();
        int pairs = 0;
        for (int i = 0; i < count; i++)
            for (int j = i + 1; j < count; j++)
            {
                GeometrySign expected = ReleasedJoin(polygons[i], polygons[j], 0.0001f, released);
                Assert.Equal(GeometrySign.Positive, expected);
                Assert.Equal(expected,
                    SupportNeighborhoodJoins.Joined(polygons[i], polygons[j], 0.0001f, optimized));
                pairs++;
            }
        Assert.Equal(4560, pairs);
        Assert.Equal(96, polygons.Length);
        // A discharged meeting leaves the distance scratch untouched, rather than hiding fewer raw pairs.
        var candidates = (ICollection)typeof(SupportSegmentPolygon).GetField("_candidates",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(optimized)!;
        Assert.Empty(candidates);

        using var scene = SupportFootprintScenes.Fan(count);
        using IPhysicsQueryLease lease = scene.World.AcquireQueryReadLease();
        var members = new SupportFootprintMember[256];
        var joins = new ulong[1024];
        SupportFootprintResult result = Capability(scene.World).QuerySupportFootprint(lease, Request(), members, joins);
        Assert.Equal(count, result.Members);
        CheckQueryOracle(scene, Request(), result, members, joins);
    }

    [Fact]
    public void IncidenceKeepsCracksCreasesUncertaintyAndRefusal()
    {
        Vector3[] first = [new(0, 0, -1), new(0, 0, 1), new(-1, 0, 0)];
        var a = Polygon(first, out _);
        Vector3[][] rows =
        [
            [new(0, 0, -1), new(1, 0, 0), new(0, 0, 1)], // Shared edge.
            [new(0, 0, 1), new(1, 0, 2), new(0, 0, 3)], // Remote vertex.
            [new(0, 0, -1), new(0.5f, 0, 0), new(0, 0, 1)], // Non-manifold incidence.
            [new(MathF.BitDecrement(0.0001f), 0, -1), new(1, 0, 0), new(MathF.BitDecrement(0.0001f), 0, 1)],
            [new(MathF.BitIncrement(0.0001f), 0, -1), new(1, 0, 0), new(MathF.BitIncrement(0.0001f), 0, 1)],
            [new(0, 0, -1), new(1, -1, 0), new(0, 0, 1)], // Convex crease.
            [new(0, 0, -1), new(1, 1, 0), new(0, 0, 1)], // Concave crease.
            [new(-1, -1, -1), new(1, 1, 0), new(-1, -1, 1)], // Crossing planes.
        ];
        foreach (Vector3[] row in rows)
        {
            SupportPolygon b = Polygon(row, out _);
            foreach (double band in new[] { 0, (double)0.0001f })
            {
                Assert.Equal(ReleasedJoin(a, b, band, new()), SupportNeighborhoodJoins.Joined(a, b, band, new()));
                Assert.Equal(ReleasedJoin(b, a, band, new()), SupportNeighborhoodJoins.Joined(b, a, band, new()));
            }
        }
        var uncertain = new SupportPolygon();
        uncertain.Begin(new(91), 0, 3);
        for (int i = 0; i < 3; i++)
        {
            FeaturePoint v = a.Vertices[i];
            uncertain.Set(i, new(v.X, FeatureNumber.Enclosed(GeometryInterval.Enclose(-0.0002, 0.0002)), v.Z), a.Edges[i]);
        }
        uncertain.Finish(a.Normal);
        Assert.Equal(ReleasedJoin(a, uncertain, 0.0001f, new()),
            SupportNeighborhoodJoins.Joined(a, uncertain, 0.0001f, new()));

        var degenerate = new SupportPolygon();
        degenerate.Begin(new(92), 0, 3);
        for (int i = 0; i < 3; i++) degenerate.Set(i, a.Vertices[i], i == 1 ? default : a.Edges[i]);
        degenerate.Finish(a.Normal);
        Assert.Equal(GeometrySign.Unresolved, ReleasedJoin(a, degenerate, 0.0001f, new()));
        Assert.Equal(GeometrySign.Unresolved, SupportNeighborhoodJoins.Joined(a, degenerate, 0.0001f, new()));

        using var split = new SupportFootprintScene();
        split.Install(new TriangleMeshShape(first, [0, 1, 2]), Pose.Identity);
        split.Install(new TriangleMeshShape(rows[0], [0, 1, 2]), Pose.Identity);
        using IPhysicsQueryLease lease = split.World.AcquireQueryReadLease();
        var members = new SupportFootprintMember[256];
        var joins = new ulong[1024];
        SupportFootprintResult result = Capability(split.World).QuerySupportFootprint(lease, Request(), members, joins);
        CheckQueryOracle(split, Request(), result, members, joins);
        Assert.Equal(2, result.Members);
        Assert.NotEqual(members[0].Static, members[1].Static);
        Assert.True((joins[0] & 2) != 0);
    }

    [Fact]
    public void ColdWarmInterleavedAndPartitionsAgree()
    {
        var query = new FootSupportQuery(Vector2.Zero, 0, 0.2f, 0.125f, 0.4f, MathF.Cos(MathF.PI / 4));
        SupportSample? reference = null;
        double referenceGeometryHeight = 0;
        foreach (FootprintPartition partition in new[] { FootprintPartition.Boxes, FootprintPartition.Hulls,
                     FootprintPartition.Compound, FootprintPartition.Mesh })
        {
            using var scene = SupportFootprintScenes.HiddenCrate(partition);
            using var rebuilt = SupportFootprintScenes.HiddenCrate(partition);
            using IPhysicsQueryLease lease = scene.World.AcquireQueryReadLease();
            using IPhysicsQueryLease rebuiltLease = rebuilt.World.AcquireQueryReadLease();
            SupportSample cold = FootSupport.Find(null, null, scene.World, lease, query);
            SupportSample warm = FootSupport.Find(null, null, scene.World, lease, query);
            FootSupport.Find(null, null, scene.World, lease, query with { Axis = new(0.18f, 0.02f) });
            SupportSample interleaved = FootSupport.Find(null, null, scene.World, lease, query);
            SupportSample fresh = FootSupport.Find(null, null, rebuilt.World, rebuiltLease, query);
            Assert.Equal(cold, warm);
            Assert.Equal(cold, interleaved);
            Assert.Equal(cold, fresh);
            Assert.Equal(SupportStatus.Walkable, cold.Status);
            Assert.Equal(scene.Crate, cold.Static);
            Assert.Contains(scene.Faces, f => f.Static == cold.Static && f.ElementId == cold.FeatureId);
            double geometryHeight = SupportFootprintOracle.Select(Members(scene, Request()), Request()).Height;
            if (reference is { } other)
            {
                Assert.Equal(other.Status, cold.Status);
                double installedGeometryGap = Math.Abs(geometryHeight - referenceGeometryHeight);
                Assert.True(Math.Abs(other.Height - cold.Height) <= other.HeightError + cold.HeightError + installedGeometryGap);
            }
            reference = cold;
            referenceGeometryHeight = geometryHeight;

            var members = new SupportFootprintMember[256];
            var joins = new ulong[1024];
            SupportFootprintResult result = Capability(scene.World).QuerySupportFootprint(lease, Request(), members, joins);
            var savedMembers = members.Take(result.Members).ToArray();
            var savedJoins = joins.Take(result.Members * result.JoinWordsPerRow).ToArray();
            Capability(scene.World).QuerySupportFootprint(lease, Request(x: 0.18f), members, joins);
            SupportFootprintResult replay = Capability(scene.World).QuerySupportFootprint(lease, Request(), members, joins);
            Assert.Equal(result, replay);
            Assert.Equal(savedMembers, members.Take(replay.Members));
            Assert.Equal(savedJoins, joins.Take(replay.Members * replay.JoinWordsPerRow));
            CheckQueryOracle(scene, Request(), replay, members, joins);
        }
    }

    // Frozen released predicate, independent of the optimized incidence path.
    static GeometrySign ReleasedJoin(SupportPolygon first, SupportPolygon second, double band, SupportSegmentPolygon kernel)
    {
        if (SupportGeometry.BoxGap(first.Low, first.High, second.Low, second.High) > band) return GeometrySign.Negative;
        foreach (var pair in new[] { (first, second), (second, first) })
            foreach (FeaturePoint vertex in pair.Item1.Vertices)
            {
                GeometryInterval height = pair.Item2.Height(vertex);
                if (!height.IsResolved) return GeometrySign.Unresolved;
                if (height.Upper > band) return GeometrySign.Negative;
            }
        GeometrySign meets = ReleasedMeeting(first, second, band, kernel);
        return meets != GeometrySign.Negative ? meets : ReleasedMeeting(second, first, band, kernel);
    }

    static GeometrySign ReleasedMeeting(SupportPolygon edges, SupportPolygon polygon, double band, SupportSegmentPolygon kernel)
    {
        for (int i = 0; i < edges.Edges.Length; i++)
        {
            SupportDistance distance = kernel.Measure(edges.Vertices[i], edges.Edges[i], polygon, band);
            if (!distance.IsResolved) return GeometrySign.Unresolved;
            if (distance.Lower <= band) return GeometrySign.Positive;
        }
        return GeometrySign.Negative;
    }
}
