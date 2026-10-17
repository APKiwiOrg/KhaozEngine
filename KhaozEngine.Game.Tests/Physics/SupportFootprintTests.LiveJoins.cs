using System.Linq;
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
            Assert.Equal(full.Shape, live.Shape);
            if (full.Result.Status != CapsuleFeatureStatus.Complete) continue;
            for (int first = 0; first < full.Result.Members; first++)
                for (int second = 0; second < full.Result.Members; second++)
                {
                    bool joined = full.Joined(first, second);
                    bool read = Read(full.Members[first]) && Read(full.Members[second]);
                    Assert.True((joined && read) == live.Joined(first, second),
                        $"{row.Name}: join {first} {second} is {live.Joined(first, second)}, full {joined}, read {read}.");
                    if (joined && !read && first < second) skippedJoins++;
                }
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
        int warmJoins = warmCounts.JoinsProved + warmCounts.TopologyJoins;
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
