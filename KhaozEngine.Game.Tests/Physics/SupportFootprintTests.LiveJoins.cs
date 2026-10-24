using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Locomotion.Contacts;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    [Fact]
    public void LiveJoinsMatchReferenceOnEveryCostScene()
    {
        using var counters = new WorldFormCounters();
        int skippedJoins = 0;
        foreach (SupportCostRow row in CostRows())
        {
            using var scene = new WorldFormScene(row);
            scene.Checked($"{row.Name} cold", out _);
            scene.Checked($"{row.Name} warm", out _);
            scene.CheckedSubstep($"{row.Name} substep");

            // The armed query keeps every member and verdict and exactly the full matrix's joins between two read
            // members. The reference ignores the request and builds every join.
            Footprint full = Raw(scene, armed: false);
            Footprint live = Raw(scene, armed: true);
            Footprint reference;
            using (SupportReferenceEvaluation.Begin()) reference = Raw(scene, armed: true);
            Assert.Equal(full.Text, reference.Text);
            skippedJoins += AssertReadJoins(row.Name, full, live);
        }
        Assert.True(skippedJoins > 0, "No cost scene has a join certification never reads.");
    }

    [Fact]
    public void RawFootprintKeepsTheFullJoinMatrix()
    {
        using var counters = new WorldFormCounters();
        foreach (SupportCostRow row in CostRows())
        {
            using var scene = new WorldFormScene(row);
            scene.Find();
            SupportQueryDiagnostics.Reset();
            scene.CheckedFootprint($"{row.Name} raw after a Find");
            Assert.Equal(0, SupportQueryDiagnostics.Snapshot().JoinsSkipped);
        }

        // Stairs and the hidden crate publish joins with a refused member, which only the full matrix carries.
        foreach (string name in new[] { "stairs box", "hidden crate" })
        {
            using var scene = new WorldFormScene(CostRows().Single(row => row.Name == name));
            Footprint full = Raw(scene, armed: false);
            Assert.True(UnreadJoins(full) > 0, $"{name}: the raw matrix has no unread join.");

            // One request serves one query, so a second query inside the same scope publishes the full matrix.
            using IPhysicsQueryLease lease = scene.AcquireLease();
            Footprint first, second;
            using (SupportCertificationJoins.Arm())
            {
                first = Query(scene, lease);
                second = Query(scene, lease);
            }
            Assert.Equal(0, UnreadJoins(first));
            Assert.Equal(full.Text, second.Text);
            Assert.Equal(second.Text, Query(scene, lease).Text);
        }
    }

    [Fact]
    public void SupportPathProvesFewerJoinsOnTheHiddenCrate()
    {
        using var counters = new WorldFormCounters();
        using var scene = new WorldFormScene(CostRows().Single(row => row.Name == "hidden crate"));
        SupportQueryDiagnostics.Reset();
        SupportSample cold = scene.Find();
        SupportQueryCounts coldCounts = SupportQueryDiagnostics.Snapshot();
        SupportQueryDiagnostics.Reset();
        SupportSample warm = scene.Find();
        SupportQueryCounts warmCounts = SupportQueryDiagnostics.Snapshot();
        SupportQueryDiagnostics.Reset();
        SupportSample reference;
        using (SupportReferenceEvaluation.Begin()) reference = scene.Find();
        SupportQueryCounts referenceCounts = SupportQueryDiagnostics.Snapshot();

        Assert.Equal(cold.ToString(), warm.ToString());
        Assert.Equal(reference.ToString(), warm.ToString());
        long warmJoins = warmCounts.JoinsProved + warmCounts.TopologyJoins;
        Assert.True(warmJoins < referenceCounts.JoinsProved,
            $"hidden crate: {warmJoins} joins against the reference's {referenceCounts.JoinsProved}.");
        Assert.True(warmCounts.JoinsSkipped > 0, "hidden crate: the support path skipped no pair.");
        Assert.True(warmCounts.JoinsPruned <= referenceCounts.JoinsPruned);
        // Every pair is either evaluated or skipped, and the reference skips none.
        Assert.Equal(referenceCounts.JoinPairs, warmCounts.JoinPairs + warmCounts.JoinsSkipped);
        Assert.Equal(0, referenceCounts.JoinsSkipped);
        Assert.Equal(coldCounts with { WorldFormBuilds = 0 }, warmCounts);
        scene.Checked("hidden crate warm", out _);
    }

    [Fact]
    public void SupportPathTakesTopologyJoinsBetweenWalkableFaces()
    {
        using var counters = new WorldFormCounters();
        using var installed = new SupportFootprintScene();
        // A convex ridge: two faces of one hull leaf, each about 26.6 degrees from level, share the edge along x = 0.
        installed.Install(new ConvexHullShape([new(-1, -0.5f, -1), new(-1, -0.5f, 1), new(1, -0.5f, -1),
            new(1, -0.5f, 1), new(0, 0, -1), new(0, 0, 1)]), Pose.Identity);
        using var scene = new WorldFormScene(installed.World,
            new FootSupportQuery(Vector2.Zero, 0, 0.2f, 0.4f, 0.4f, 0.70710677f));
        scene.Checked("ridge cold", out _);
        SupportQueryDiagnostics.Reset();
        SupportSample warm = scene.Find();
        SupportQueryCounts warmCounts = SupportQueryDiagnostics.Snapshot();
        SupportQueryDiagnostics.Reset();
        SupportSample reference;
        using (SupportReferenceEvaluation.Begin()) reference = scene.Find();
        SupportQueryCounts referenceCounts = SupportQueryDiagnostics.Snapshot();

        Assert.Equal(SupportStatus.Walkable, warm.Status);
        Assert.Equal(reference.ToString(), warm.ToString());
        Assert.True(warmCounts.TopologyJoins > 0, "ridge: the support path took no join from the captured topology.");
        Assert.Equal(0, referenceCounts.TopologyJoins);
        Assert.True(referenceCounts.JoinsProved >= warmCounts.JoinsProved + warmCounts.TopologyJoins);
        scene.Checked("ridge warm", out _);

        // The armed raw query joins the two walkable faces through the topology, as the reference does geometrically.
        Footprint full = Raw(scene, armed: false);
        SupportQueryDiagnostics.Reset();
        Footprint live = Raw(scene, armed: true);
        SupportQueryCounts liveCounts = SupportQueryDiagnostics.Snapshot();
        Footprint armedReference;
        using (SupportReferenceEvaluation.Begin()) armedReference = Raw(scene, armed: true);
        Assert.Equal(full.Text, armedReference.Text);
        AssertReadJoins("ridge", full, live);
        Assert.True(liveCounts.TopologyJoins > 0, "ridge: the armed query took no join from the captured topology.");
        int walkableJoins = 0;
        for (int first = 0; first < live.Result.Members; first++)
            for (int second = first + 1; second < live.Result.Members; second++)
                if (live.Joined(first, second) && live.Members[first].UpwardLower >= 0.70710677f &&
                    live.Members[second].UpwardLower >= 0.70710677f) walkableJoins++;
        Assert.True(walkableJoins > 0, "ridge: the two walkable faces are not joined.");
    }

    [Fact]
    public void UnreadUnresolvedJoinRefusesOnlyTheFullMatrix()
    {
        // Installed geometry gives no scene for this ruling. Admission refuses a polygon whose vertices or normalized
        // normal do not resolve, so every admitted pair's mutual-below heights resolve, and the segment kernel takes
        // exact fallbacks. The pin drives the collector's publication and certification directly: an admitted
        // walkable floor, and a refused wall whose join with the floor is unresolved.
        using var counters = new WorldFormCounters();
        SupportFootprintRequest request = Request();
        SupportPolygon floor = JoinFace(1, [new(-1, 0, -1), new(-1, 0, 1), new(1, 0, 1), new(1, 0, -1)]);
        SupportPolygon wall = JoinFace(2, [new(0.1f, -0.1f, -0.5f), new(0.1f, 0.05f, -0.5f), new(0.1f, 0.05f, 0.5f),
            new(0.1f, -0.1f, 0.5f)]);
        Assert.Equal(GeometrySign.Positive, SupportFootprintPolygon.Measure(floor, request, out var floorValue));
        Assert.Equal(GeometrySign.Positive, SupportFootprintPolygon.Measure(wall, request, out var wallValue));
        Assert.True(floorValue.UpwardLower > 0 && floorValue.HasDiscMaximum);
        Assert.False(wallValue.UpwardLower > 0);
        // The wall's twin has an unresolved height at its plane point, so its join with the floor is unresolved.
        var unresolved = new SupportPolygon();
        unresolved.Begin(wall.Static, wall.ElementId, wall.Vertices.Length);
        for (int i = 0; i < wall.Vertices.Length; i++)
            unresolved.Set(i, i == 0 ? new(wall.Vertices[i].X, default, wall.Vertices[i].Z) : wall.Vertices[i],
                wall.Edges[i]);
        unresolved.Finish(wall.Normal);
        Assert.Equal(GeometrySign.Unresolved,
            SupportNeighborhoodJoins.Joined(floor, unresolved, request.ContactBand, new()));

        using var installed = new SupportFootprintScene();
        IPhysicsSupportFootprint features = Capability(installed.World);
        using IPhysicsQueryLease lease = installed.World.AcquireQueryReadLease();
        (SupportPolygon, SupportFootprintMember)[] pair = [(floor, floorValue), (unresolved, wallValue)];
        Assert.Equal(CapsuleFeatureStatus.Unsupported,
            Publish(installed.World, lease, request, certificationJoins: false, pair).Result.Status);
        using (SupportReferenceEvaluation.Begin())
            Assert.Equal(CapsuleFeatureStatus.Unsupported,
                Publish(installed.World, lease, request, certificationJoins: true, pair).Result.Status);

        // The support path skips the unread pair, keeps both members and certifies the floor's support exactly as
        // the reference does with the wall excluded.
        SupportQueryDiagnostics.Reset();
        Footprint live = Publish(installed.World, lease, request, certificationJoins: true, pair);
        Assert.Equal(1, SupportQueryDiagnostics.Snapshot().JoinsSkipped);
        Assert.Equal(CapsuleFeatureStatus.Complete, live.Result.Status);
        Assert.Equal([floorValue, wallValue], live.Members.Take(live.Result.Members));
        Assert.False(live.Joined(0, 1) || live.Joined(1, 0));
        Footprint excluded;
        using (SupportReferenceEvaluation.Begin())
            excluded = Publish(installed.World, lease, request, certificationJoins: false, [(floor, floorValue)]);
        FootprintContribution[] supported = Certify(features, lease, request, live);
        Assert.Equal(CertifiedSupportKind.Walkable, Assert.Single(supported).Contribution.Kind);
        Assert.Equal(Certify(features, lease, request, excluded), supported);
    }

    sealed record Footprint(SupportFootprintResult Result, SupportFootprintMember[] Members, ulong[] Joins)
    {
        // Everything but the join words, as text that exposes signed zeros.
        internal string Shape => $"{Result.Status} {Result.Members} {Result.Witnesses} {Result.RequiredElements} " +
            $"{Result.JoinWordsPerRow} {Result.AxisVerdict} {Result.FootVerdict} " +
            string.Join(";", Members.Take(Result.Members));

        internal string Text => Shape + " " + string.Join(",", Joins.Take(Result.Members * Result.JoinWordsPerRow));

        internal bool Joined(int first, int second) =>
            (Joins[first * Result.JoinWordsPerRow + second / 64] & (1UL << (second % 64))) != 0;
    }

    // Asserts the armed footprint keeps every member and verdict and exactly the full matrix's joins between two read
    // members. Returns the full matrix's joins the armed one left out.
    static int AssertReadJoins(string context, Footprint full, Footprint live)
    {
        Assert.Equal(full.Shape, live.Shape);
        if (full.Result.Status != CapsuleFeatureStatus.Complete) return 0;
        int skipped = 0;
        for (int first = 0; first < full.Result.Members; first++)
            for (int second = 0; second < full.Result.Members; second++)
            {
                bool joined = full.Joined(first, second);
                bool read = Read(full.Members[first]) && Read(full.Members[second]);
                Assert.True((joined && read) == live.Joined(first, second),
                    $"{context}: join {first} {second} is {live.Joined(first, second)}, full {joined}, read {read}.");
                if (joined && !read && first < second) skipped++;
            }
        return skipped;
    }

    static bool Read(in SupportFootprintMember member) =>
        member.Kind == SupportElementKind.Polygon && member.UpwardLower > 0;

    static int UnreadJoins(Footprint footprint)
    {
        Assert.Equal(CapsuleFeatureStatus.Complete, footprint.Result.Status);
        int unread = 0;
        for (int first = 0; first < footprint.Result.Members; first++)
            for (int second = first + 1; second < footprint.Result.Members; second++)
                if (footprint.Joined(first, second) &&
                    !(Read(footprint.Members[first]) && Read(footprint.Members[second]))) unread++;
        return unread;
    }

    static SupportPolygon JoinFace(int element, Vector3[] vertices)
    {
        var polygon = new SupportPolygon();
        polygon.Begin(new StaticHandle(42), element, vertices.Length);
        for (int i = 0; i < vertices.Length; i++)
        {
            FeaturePoint a = FeaturePoint.Exact(vertices[i]);
            polygon.Set(i, a, FeaturePoint.Subtract(FeaturePoint.Exact(vertices[(i + 1) % vertices.Length]), a));
        }
        polygon.Finish(FeaturePoint.Cross(polygon.Edges[0], FeaturePoint.Subtract(polygon.Vertices[2],
            polygon.Vertices[0])));
        return polygon;
    }

    // Publishes the given admitted members from a fresh collector with neither proposal in contact.
    static Footprint Publish(IPhysicsWorld receiver, IPhysicsQueryLease lease, in SupportFootprintRequest request,
        bool certificationJoins, (SupportPolygon Polygon, SupportFootprintMember Value)[] admitted)
    {
        var collector = new SupportFootprintCollector();
        Assert.True(collector.Begin(request));
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        Type memberType = typeof(SupportFootprintCollector).GetNestedType("Member", BindingFlags.NonPublic)!;
        var stored = (Array)typeof(SupportFootprintCollector).GetField("_members", Private)!.GetValue(collector)!;
        for (int i = 0; i < admitted.Length; i++)
            stored.SetValue(Activator.CreateInstance(memberType, admitted[i].Polygon, default(SupportCurvedLeaf), 0,
                admitted[i].Value), i);
        typeof(SupportFootprintCollector).GetField("_count", Private)!.SetValue(collector, admitted.Length);
        var members = new SupportFootprintMember[SupportFootprintResult.MaximumElements];
        var joins = new ulong[SupportFootprintResult.MaximumElements * SupportFootprintResult.JoinWordsFor(
            SupportFootprintResult.MaximumElements)];
        return new(collector.Publish(receiver, lease, members, joins, certificationJoins), members, joins);
    }

    static FootprintContribution[] Certify(IPhysicsSupportFootprint features, IPhysicsQueryLease lease,
        in SupportFootprintRequest request, Footprint footprint)
    {
        var contributions = new FootprintContribution[2 * SupportFootprintResult.MaximumElements];
        int count = SupportCertification.CertifyFootprint(features, lease, footprint.Result, footprint.Members,
            footprint.Joins, request.Axis, request.CosMaxSlope, contributions);
        Assert.True(count >= 0, "Certification refused the footprint.");
        return contributions[..count];
    }

    static Footprint Raw(WorldFormScene scene, bool armed)
    {
        using IPhysicsQueryLease lease = scene.AcquireLease();
        if (!armed) return Query(scene, lease);
        using (SupportCertificationJoins.Arm()) return Query(scene, lease);
    }

    static Footprint Query(WorldFormScene scene, IPhysicsQueryLease lease)
    {
        var members = new SupportFootprintMember[SupportFootprintResult.MaximumElements];
        var joins = new ulong[SupportFootprintResult.MaximumElements * SupportFootprintResult.JoinWordsFor(
            SupportFootprintResult.MaximumElements)];
        SupportFootprintResult result = ((IPhysicsSupportFootprint)scene.Target).QuerySupportFootprint(lease,
            FootSupport.DescribeFootprint(scene.Query), members, joins);
        return new(result, members, joins);
    }
}
