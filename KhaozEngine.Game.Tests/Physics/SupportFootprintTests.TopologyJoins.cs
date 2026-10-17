using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Locomotion.Contacts;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    [Fact]
    public void SameLeafSharedEdgeJoinsByTopology()
    {
        using var counters = new WorldFormCounters();
        using var scene = new WorldFormScene(CostRows().Single(row => row.Name == "stairs box"));
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
        Assert.True(warmCounts.TopologyJoins > 0, "stairs box: no join came from the captured topology.");
        Assert.True(warmCounts.JoinsProved < referenceCounts.JoinsProved,
            $"stairs box: {warmCounts.JoinsProved} geometric joins against the reference's {referenceCounts.JoinsProved}.");
        // Every topology join replaces exactly one geometric join of the reference, over the same pairs.
        Assert.Equal(referenceCounts.JoinsProved, warmCounts.JoinsProved + warmCounts.TopologyJoins);
        Assert.Equal(referenceCounts.JoinPairs, warmCounts.JoinPairs);
        Assert.Equal(referenceCounts.JoinsPruned, warmCounts.JoinsPruned);
        Assert.Equal(0, referenceCounts.TopologyJoins);
        // The building Find takes its joins from the topology it just built, so cold and warm count alike.
        Assert.Equal(coldCounts with { WorldFormBuilds = 0 }, warmCounts);

        scene.Checked("stairs box warm", out _);
        scene.CheckedFootprint("stairs box raw members");
        scene.CheckedSubstep("stairs box substep");
        foreach (SupportCostRow row in CostRows())
        {
            using var other = new WorldFormScene(row);
            other.CheckedFootprint($"{row.Name} cold raw members");
            other.CheckedFootprint($"{row.Name} warm raw members");
            other.Checked($"{row.Name} warm", out _);
        }
    }

    [Fact]
    public void DifferentStaticsStillJoinGeometrically()
    {
        using var counters = new WorldFormCounters();
        using var scene = new SupportFootprintScene();
        // Two box statics whose top faces meet along the edge x = 0 and whose facing sides coincide.
        StaticHandle left = scene.Install(new BoxShape(new(0.5f, 0.5f, 0.5f)), Pose.At(new(-0.5f, -0.5f, 0)));
        StaticHandle right = scene.Install(new BoxShape(new(0.5f, 0.5f, 0.5f)), Pose.At(new(0.5f, -0.5f, 0)));
        IPhysicsSupportFootprint footprint = Capability(scene.World);
        SupportFootprintRequest request = Request();
        using IPhysicsQueryLease lease = scene.World.AcquireQueryReadLease();
        var members = new SupportFootprintMember[SupportFootprintResult.MaximumElements];
        var joins = new ulong[1024];

        SupportQueryDiagnostics.Reset();
        footprint.QuerySupportFootprint(lease, request, members, joins);
        SupportQueryCounts cold = SupportQueryDiagnostics.Snapshot();
        SupportQueryDiagnostics.Reset();
        SupportFootprintResult result = footprint.QuerySupportFootprint(lease, request, members, joins);
        SupportQueryCounts warm = SupportQueryDiagnostics.Snapshot();
        Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
        int stride = result.JoinWordsPerRow, sameStatic = 0, crossStatic = 0;
        bool topsJoined = false;
        for (int first = 0; first < result.Members; first++)
            for (int second = first + 1; second < result.Members; second++)
            {
                if ((joins[first * stride + second / 64] & (1UL << (second % 64))) == 0) continue;
                if (members[first].Static == members[second].Static) sameStatic++;
                else
                {
                    crossStatic++;
                    topsJoined |= members[first].Static == left && members[second].Static == right &&
                        IsTop(scene, members[first]) && IsTop(scene, members[second]);
                }
            }
        Assert.True(topsJoined, "The two top faces meeting along x = 0 are not joined.");
        Assert.True(crossStatic > 0 && sameStatic > 0, $"{sameStatic} same-static and {crossStatic} cross-static joins.");
        // Box faces join only where they share an edge, so every same-static join is a topology join and every
        // join across the two statics stays geometric.
        Assert.Equal(sameStatic, warm.TopologyJoins);
        Assert.Equal(crossStatic, warm.JoinsProved);
        Assert.Equal(cold with { WorldFormBuilds = 0 }, warm);

        SupportQueryDiagnostics.Reset();
        using (SupportReferenceEvaluation.Begin()) footprint.QuerySupportFootprint(lease, request, members, joins);
        SupportQueryCounts reference = SupportQueryDiagnostics.Snapshot();
        Assert.Equal(0, reference.TopologyJoins);
        Assert.Equal(sameStatic + crossStatic, reference.JoinsProved);
        SupportFootprintEquivalence.AssertMatchesReference(footprint, lease, request, "two touching boxes");
    }

    [Fact]
    public void DifferentLeavesOfOneCompoundStillJoinGeometrically()
    {
        using var counters = new WorldFormCounters();
        using var scene = new SupportFootprintScene();
        // One compound whose two box children have top faces meeting along the edge x = 0 and coincident facing
        // sides. The children are separate leaves of one world form, so no captured edge links them.
        StaticHandle owner = scene.Install(new CompoundShape([
            new(new BoxShape(new(0.5f, 0.5f, 0.5f)), Pose.At(new(-0.5f, -0.5f, 0))),
            new(new BoxShape(new(0.5f, 0.5f, 0.5f)), Pose.At(new(0.5f, -0.5f, 0)))]), Pose.Identity);
        IPhysicsSupportFootprint footprint = Capability(scene.World);
        SupportFootprintRequest request = Request();
        using IPhysicsQueryLease lease = scene.World.AcquireQueryReadLease();
        var members = new SupportFootprintMember[SupportFootprintResult.MaximumElements];
        var joins = new ulong[1024];

        SupportQueryDiagnostics.Reset();
        footprint.QuerySupportFootprint(lease, request, members, joins);
        SupportQueryCounts cold = SupportQueryDiagnostics.Snapshot();
        SupportQueryDiagnostics.Reset();
        SupportFootprintResult result = footprint.QuerySupportFootprint(lease, request, members, joins);
        SupportQueryCounts warm = SupportQueryDiagnostics.Snapshot();
        Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
        int stride = result.JoinWordsPerRow, sameLeaf = 0, crossLeaf = 0;
        bool topsJoined = false;
        for (int first = 0; first < result.Members; first++)
            for (int second = first + 1; second < result.Members; second++)
            {
                if ((joins[first * stride + second / 64] & (1UL << (second % 64))) == 0) continue;
                Assert.Equal(owner, members[first].Static);
                Assert.Equal(owner, members[second].Static);
                int firstLeaf = members[first].ElementId / SupportNeighborhoodPolyhedra.FaceStride;
                int secondLeaf = members[second].ElementId / SupportNeighborhoodPolyhedra.FaceStride;
                if (firstLeaf == secondLeaf) sameLeaf++;
                else
                {
                    crossLeaf++;
                    topsJoined |= IsTop(scene, members[first]) && IsTop(scene, members[second]);
                }
            }
        Assert.True(topsJoined, "The two top faces meeting along x = 0 are not joined.");
        Assert.True(crossLeaf > 0 && sameLeaf > 0, $"{sameLeaf} same-leaf and {crossLeaf} cross-leaf joins.");
        // Every same-leaf join comes from the leaf's captured topology, and no join across the two leaves does.
        Assert.Equal(sameLeaf, warm.TopologyJoins);
        Assert.Equal(crossLeaf, warm.JoinsProved);
        Assert.Equal(cold with { WorldFormBuilds = 0 }, warm);

        SupportQueryDiagnostics.Reset();
        using (SupportReferenceEvaluation.Begin()) footprint.QuerySupportFootprint(lease, request, members, joins);
        SupportQueryCounts reference = SupportQueryDiagnostics.Snapshot();
        Assert.Equal(0, reference.TopologyJoins);
        Assert.Equal(sameLeaf + crossLeaf, reference.JoinsProved);
        SupportFootprintEquivalence.AssertMatchesReference(footprint, lease, request, "two touching compound boxes");
    }

    [Fact]
    public void MutualBelowStillCertifiedForTopologyJoins()
    {
        // Each row shares the edge from (0, 0, -1) to (0, 0, 1) with the first face, as two faces of one leaf do. A
        // pair flagged as meeting along that edge still runs the mutual-below certification, so a failed or
        // unresolved certification keeps the reference verdict rather than becoming a topology join.
        var a = Polygon([new(0, 0, -1), new(0, 0, 1), new(-1, 0, 0)], out _);
        Vector3[][] rows =
        [
            [new(0, 0, -1), new(1, 0, 0), new(0, 0, 1)], // Coplanar continuation.
            [new(0, 0, -1), new(1, -1, 0), new(0, 0, 1)], // Convex crease.
            [new(0, 0, -1), new(1, 1, 0), new(0, 0, 1)], // Concave crease, not mutually below.
            [new(0, 0, -1), new(1, 0.5f, 0), new(0, 0, 1)], // Shallower concave crease.
        ];
        var verdicts = new System.Collections.Generic.List<GeometrySign>();
        foreach (Vector3[] row in rows)
        {
            SupportPolygon b = Polygon(row, out _);
            verdicts.Add(CheckTopologyJoin(a, b));
            verdicts.Add(CheckTopologyJoin(b, a));
        }

        // A vertex enclosure wider than the band above the plane fails the certification.
        var uncertain = new SupportPolygon();
        uncertain.Begin(new(91), 0, 3);
        for (int i = 0; i < 3; i++)
        {
            FeaturePoint v = a.Vertices[i];
            uncertain.Set(i, new(v.X, FeatureNumber.Enclosed(GeometryInterval.Enclose(-0.0002, 0.0002)), v.Z), a.Edges[i]);
        }
        uncertain.Finish(a.Normal);
        verdicts.Add(CheckTopologyJoin(a, uncertain));

        // An unresolved vertex height refuses the certification.
        var unresolved = new SupportPolygon();
        SupportPolygon crease = Polygon(rows[1], out _);
        unresolved.Begin(new(92), 0, 3);
        for (int i = 0; i < 3; i++)
            unresolved.Set(i, i == 1 ? new(crease.Vertices[i].X, default, crease.Vertices[i].Z) : crease.Vertices[i],
                crease.Edges[i]);
        unresolved.Finish(crease.Normal);
        verdicts.Add(CheckTopologyJoin(a, unresolved));

        Assert.Contains(GeometrySign.Positive, verdicts);
        Assert.Contains(GeometrySign.Negative, verdicts);
        Assert.Contains(GeometrySign.Unresolved, verdicts);
    }

    static GeometrySign CheckTopologyJoin(SupportPolygon first, SupportPolygon second)
    {
        GeometrySign expected = ReleasedJoin(first, second, SupportJoinIncidence.ContactBand, new());
        Assert.Equal(expected, SupportNeighborhoodJoins.Joined(first, second, SupportJoinIncidence.ContactBand, new(),
            null, 0, 1, topologyMeeting: true));
        return expected;
    }

    static bool IsTop(SupportFootprintScene scene, in SupportFootprintMember member)
    {
        StaticHandle owner = member.Static;
        int element = member.ElementId;
        OracleFace face = scene.Faces.Single(f => f.Static == owner && f.ElementId == element);
        return face.Vertices.All(v => v.Y == 0);
    }
}
