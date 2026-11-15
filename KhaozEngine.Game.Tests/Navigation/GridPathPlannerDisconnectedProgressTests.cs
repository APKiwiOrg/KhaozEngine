using System;
using System.Collections;
using System.Numerics;
using KhaozEngine.Navigation;
using Xunit;
using static KhaozEngine.Tests.Navigation.DisconnectedPlannerFixture;

namespace KhaozEngine.Tests.Navigation;

public class GridPathPlannerDisconnectedProgressTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PublicCollectionStorageCannotInvalidateCachedConnectivity(bool guarded)
    {
        NavSpace space = NavSpace.Single(Grid(3, 2, (x, z) => z == 0 && x != 1));
        NavGrid replacement = Grid(3, 2, (x, z) => z == 1 || x != 1);
        NavTraversalGraph graph = Graph(space);
        var planner = guarded ? new GridPathPlanner(space, graph) : new GridPathPlanner(space);
        NavSpace owned = guarded ? graph.Space : space;
        Exception? mutation = Record.Exception(() =>
        {
            ((NavGrid[])((ICollection)owned.Layers).SyncRoot)[0] = replacement;
            if (guarded)
                ((NavTraversalLayer[])((ICollection)graph.Layers).SyncRoot)[0] =
                    Graph(NavSpace.Single(replacement)).Layers[0];
        });

        NavPath path = Check(planner, new Vector3(0.5f, 0f, 0.5f),
            Member(new Vector3(2.5f, 0f, 0.5f)), Budget(8), certified: true);
        Assert.IsType<NotSupportedException>(mutation);
        Assert.Same(NavPath.Unreachable, path);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(4096)]
    public void DisconnectedTerminalCertifiesButEarlierUsefulPartialStillSearches(int expansions)
    {
        NavSpace space = NavSpace.Single(Grid(7, 1, (x, _) => x != 4));
        var planner = new GridPathPlanner(space);
        var goal = new Vector3(6.5f, 0f, 0.5f);
        PathQueryBudget budget = Budget(expansions);
        Check(planner, new Vector3(3.5f, 0f, 0.5f), goal, budget, certified: true);
        Check(planner, new Vector3(3.5f, 0f, 0.5f), Member(goal), budget, certified: true);
        NavPath earlier = Check(planner, new Vector3(0.5f, 0f, 0.5f), Member(goal), budget, certified: false);
        Assert.Equal(expansions <= 1 ? NavPathStatus.Unreachable : NavPathStatus.Partial, earlier.Status);
        if (expansions > 1)
            Assert.Equal(new[] { new NavWaypoint(new Vector2(1.5f, 0.5f), 0),
                new NavWaypoint(new Vector2(2.5f, 0.5f), 0), new NavWaypoint(new Vector2(3.5f, 0.5f), 0) }, earlier.Waypoints);
    }

    [Fact]
    public void EqualProgressCannotReplaceTheStartAndAnyMemberCanPreventCertification()
    {
        NavSpace space = NavSpace.Single(Grid(5, 3, (x, z) =>
            (x == 1 || x == 2) && z == 0 || x == 0 && z == 2 || x == 4 && z == 0));
        var planner = new GridPathPlanner(space);
        var start = new Vector3(1.5f, 0f, 0.5f);
        // The anchor is only a discovery bound. Progress must reduce the actual members in order.
        var equal = new NavGoalRegion(new Vector3(2f, 0f, 1f), 3f,
            feet => feet.X == 0.5f && feet.Z == 2.5f || feet.X == 4.5f && feet.Z == 0.5f);
        // The second member makes x=2.5 strictly closer, even though the first member is farther away.
        NavPath path = Check(planner, start, equal, Budget(8), certified: false);
        Assert.Equal(NavPathStatus.Partial, path.Status);
        Assert.Equal(new[] { new NavWaypoint(new Vector2(2.5f, 0.5f), 0) }, path.Waypoints);

        NavSpace tied = NavSpace.Single(Grid(3, 3, (x, z) => z == 0 && x != 1 || x == 1 && z == 2));
        var link = new NavLink(0, 0, 0, 0, 2, 0);
        tied = new NavSpace(tied.Layers, new[] { link });
        Check(new GridPathPlanner(tied), new Vector3(0.5f, 0f, 0.5f),
            Member(new Vector3(1.5f, 0f, 2.5f)), Budget(8), certified: true);
    }

    [Theory]
    [InlineData(NavLinkKind.Stair, false)]
    [InlineData(NavLinkKind.Hop, false)]
    [InlineData(NavLinkKind.Drop, false)]
    [InlineData(NavLinkKind.Stair, true)]
    [InlineData(NavLinkKind.Hop, true)]
    [InlineData(NavLinkKind.Drop, true)]
    public void EveryPossibleCrossLayerLinkKeepsEqualLabelsOnWalkOnlyQueries(NavLinkKind kind, bool walkOnly)
    {
        var link = new NavLink(0, 0, 0, 1, 0, 0) { Kind = kind };
        var space = new NavSpace(new[] { Grid(1, 1, (_, _) => true, 4f), Grid(1, 1, (_, _) => true) }, new[] { link });
        var graph = new NavTraversalGraph(space, 0f, 1f,
            new[] { new NavTraversalLayer(1, 1, new[] { true }, new byte[1]),
                new NavTraversalLayer(1, 1, new[] { true }, new byte[1]) }, new[] { link });
        foreach (GridPathPlanner planner in new[] { new GridPathPlanner(space), new GridPathPlanner(space, graph) })
        {
            NavPath path = Check(planner, new Vector3(0.5f, 4f, 0.5f), Member(new Vector3(0.5f, 0f, 0.5f)),
                Budget(8) with { WalkOnly = walkOnly }, certified: false);
            Assert.Equal(walkOnly && kind != NavLinkKind.Stair ? NavPathStatus.Unreachable : NavPathStatus.Complete, path.Status);
            Check(planner, new Vector3(0.5f, 0f, 0.5f), new Vector3(0.5f, 4f, 0.5f), Budget(8), certified: false);
        }
    }

    [Fact]
    public void OneWayOrdinaryEdgeDoesNotProveDirectedReachability()
    {
        NavSpace space = NavSpace.Single(Grid(2, 1, (_, _) => true));
        var graph = new NavTraversalGraph(space, 0f, 1f,
            new[] { new NavTraversalLayer(2, 1, new[] { true, true }, new byte[] { 1, 0 }) }, Array.Empty<NavLink>());
        var planner = new GridPathPlanner(space, graph);
        NavPath path = Check(planner, new Vector3(1.5f, 0f, 0.5f), Member(new Vector3(0.5f, 0f, 0.5f)), Budget(8), false);
        Assert.Same(NavPath.Unreachable, path);
        Check(planner, new Vector3(0.5f, 0f, 0.5f), new Vector3(1.5f, 0f, 0.5f), Budget(8), false);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void EveryDirectedOrdinaryExitCanCarryUsefulProgress(int direction)
    {
        NavSpace space = NavSpace.Single(Grid(5, 5, (_, _) => true));
        var accepted = new bool[25];
        Array.Fill(accepted, true);
        var exits = new byte[25];
        exits[12] = (byte)(1 << direction);
        var graph = new NavTraversalGraph(space, 0f, 1f,
            new[] { new NavTraversalLayer(5, 5, accepted, exits) }, Array.Empty<NavLink>());
        int dx = NavTraversalLayer.NeighborX(direction), dz = NavTraversalLayer.NeighborZ(direction);
        var goal = new Vector3(2.5f + 2 * dx, 0f, 2.5f + 2 * dz);
        NavPath path = Check(new GridPathPlanner(space, graph), new Vector3(2.5f, 0f, 2.5f), Member(goal), Budget(8), false);
        Assert.Equal(NavPathStatus.Partial, path.Status);
        Assert.Equal(new[] { new NavWaypoint(new Vector2(2.5f + dx, 2.5f + dz), 0) }, path.Waypoints);
    }

    [Theory]
    [InlineData(NavLinkKind.Stair)]
    [InlineData(NavLinkKind.Hop)]
    [InlineData(NavLinkKind.Drop)]
    public void SameLayerLinksCannotDisappearFromTheLabelGraph(NavLinkKind kind)
    {
        var link = new NavLink(0, 0, 0, 0, 2, 0) { Kind = kind };
        NavGrid grid = kind == NavLinkKind.Drop
            ? NavGrid.FromBlockedSurfaces(3, 1, 1f, 0f, 0f, new[] { false, true, false }, new[] { 4f, 0f, 0f })
            : Grid(3, 1, (x, _) => x != 1);
        var space = new NavSpace(new[] { grid }, new[] { link });
        foreach (GridPathPlanner planner in new[] { new GridPathPlanner(space), new GridPathPlanner(space, Graph(space)) })
        {
            NavPath path = Check(planner, new Vector3(0.5f, kind == NavLinkKind.Drop ? 4f : 0f, 0.5f),
                Member(new Vector3(2.5f, 0f, 0.5f)), Budget(8), false);
            Assert.Equal(NavPathStatus.Complete, path.Status);
        }
    }

    [Fact]
    public void SeededPointAndRegionCorpusMatchesTheUnchangedSearchIncludingWalkOnly()
    {
        var seen = new GridPathPlannerEqualityCorpusTests.StatusTally();
        for (int seed = 1; seed <= 12; seed++)
        {
            var corpus = new PlannerCorpus(5000 + seed);
            NavSpace space = corpus.NextSpace(surfaces: true);
            foreach (GridPathPlanner planner in new[] { new GridPathPlanner(space), new GridPathPlanner(space, corpus.NextGraph()) })
                for (int q = 0; q < 24; q++)
                {
                    Vector3 start = corpus.NextPoint(), goal = corpus.NextPoint();
                    NavGoalRegion region = corpus.NextRegion();
                    PathQueryBudget budget = corpus.NextBudget() with { WalkOnly = q % 2 == 0 };
                    GridPathPlannerEqualityCorpusTests.AssertSame(planner.FindPathWithoutCertificateForTests(start, goal, 0f, budget),
                        planner.FindPath(start, goal, 0f, budget), seen, $"point seed {seed}, query {q}");
                    GridPathPlannerEqualityCorpusTests.AssertSame(planner.FindPathWithoutCertificateForTests(start, region, 0f, budget),
                        planner.FindPath(start, region, 0f, budget), seen, $"region seed {seed}, query {q}");
                }
        }
        seen.AssertCoversEveryStatus();
    }

    [Fact]
    public void LegacySnapTieAndExactMembershipRunBeforeTheCertificate()
    {
        NavSpace space = NavSpace.Single(Grid(5, 1, (x, _) => x is 0 or 2 or 4));
        var planner = new GridPathPlanner(space);
        var blockedStart = new Vector3(1.5f, 0f, 0.5f);
        var goal = new Vector3(4.5f, 0f, 0.5f);
        Check(planner, blockedStart, Member(goal), Budget(8), false, searches: false);
        Check(planner, blockedStart, Member(goal), Budget(8) with { SnapRadius = 3f }, true);
        // A wrong snap tie picks x=2.5, which is already this member and must complete.
        Check(planner, blockedStart, Member(new Vector3(2.5f, 0f, 0.5f)), Budget(8) with { SnapRadius = 3f }, true);
        Check(planner, new Vector3(0.5f, 0f, 0.5f),
            Member(new Vector3(3.5f, 0f, 0.5f)), Budget(8) with { SnapRadius = 3f }, false, searches: false);
    }

    [Fact]
    public void RoundedLegacySnapUsesTheActualSearchNodeOrFallsThrough()
    {
        // At 2^24 the float spacing is 2. Cell 1's centre rounds to origin+2, which CellOf
        // resolves as blocked cell 2. The old search can leave that cell and progress through cell 3.
        const float origin = 16_777_216f;
        NavSpace space = NavSpace.Single(Grid(7, 1, (x, _) => x is 1 or 3 or 6, originX: origin));
        NavPath path = Check(new GridPathPlanner(space), new Vector3(origin + 2f, 0f, 0.5f),
            Member(new Vector3(origin + 6f, 0f, 0.5f)), Budget(8) with { SnapRadius = 3f }, false);
        Assert.Equal(NavPathStatus.Partial, path.Status);
        Assert.Equal(new[] { new NavWaypoint(new Vector2(origin + 4f, 0.5f), 0) }, path.Waypoints);
    }

    [Fact]
    public void GuardedRejectionsAndQueryRestrictionsRemainConservative()
    {
        NavSpace space = NavSpace.Single(Grid(3, 1, (_, _) => true));
        var graph = new NavTraversalGraph(space, 0.2f, 1f,
            new[] { new NavTraversalLayer(3, 1, new[] { false, true, true }, new byte[] { 0, 1, 0 }) }, Array.Empty<NavLink>());
        var planner = new GridPathPlanner(space, graph);
        Assert.Throws<ArgumentException>(() => planner.FindPath(Vector3.Zero, Member(Vector3.Zero), 0f, Budget(8)));
        Assert.Same(NavPath.Unreachable, planner.FindPath(new Vector3(0.5f, 0f, 0.5f),
            Member(new Vector3(2.5f, 0f, 0.5f)), 0.2f, Budget(8) with { SnapRadius = 3f }));
        Assert.Equal((0, 0), planner.WorkForTests);
        var restricted = new GridPathPlanner(space, graph, edge => edge.FromX == edge.ToX);
        NavPath path = restricted.FindPath(new Vector3(1.5f, 0f, 0.5f), Member(new Vector3(2.5f, 0f, 0.5f)), 0.2f, Budget(8));
        Assert.Same(NavPath.Unreachable, path);
        Assert.Equal((1, 0), restricted.WorkForTests);
    }

    [Theory]
    [InlineData(32768, 1, true)]
    [InlineData(32769, 1, false)]
    [InlineData(32768, 8, true)]
    [InlineData(32768, 9, false)]
    public void CertificateCapsFallThroughRatherThanInferFromAnIncompleteProof(int componentNodes, int members, bool certified)
    {
        NavSpace space = NavSpace.Single(Grid(componentNodes + 2, members, (x, z) =>
            x < componentNodes && z == 0 || x == componentNodes + 1));
        var planner = new GridPathPlanner(space);
        var start = new Vector3(componentNodes - 0.5f, 0f, 0.5f);
        var region = new NavGoalRegion(new Vector3(componentNodes + 1.5f, 0f, 0.5f), members,
            feet => feet.X == componentNodes + 1.5f);
        Check(planner, start, region, Budget(1), certified);
    }

    [Fact]
    public void ExactDuckTerminalRequestHasEightMembersAndSkipsTheSearch()
    {
        NavSpace space = DuckSpace();
        var planner = new GridPathPlanner(space, Graph(space, 0.2f));
        NavGoalRegion region = DuckRegion();
        int members = 0;
        NavGrid grid = space.Layers[0];
        for (int z = 587; z <= 589; z++)
            for (int x = 75; x <= 77; x++)
            {
                Vector2 p = grid.CellCenter(x, z);
                if (region.Contains(new Vector3(p.X, -0.6700001f, p.Y))) members++;
            }
        Assert.Equal(8, members);
        Check(planner, DuckStart, region, new PathQueryBudget { MaxExpandedNodes = 4096, SnapRadius = 3f, WalkOnly = false }, true, radius: 0.2f);
    }

    internal static PathQueryBudget Budget(int expansions) => new() { MaxExpandedNodes = expansions, SnapRadius = 0f };

    static NavPath Check(GridPathPlanner planner, Vector3 start, Vector3 goal, PathQueryBudget budget,
        bool certified, bool searches = true)
        => CheckCore(planner, () => planner.FindPathWithoutCertificateForTests(start, goal, 0f, budget),
            () => planner.FindPath(start, goal, 0f, budget), certified, searches);

    static NavPath Check(GridPathPlanner planner, Vector3 start, NavGoalRegion goal, PathQueryBudget budget,
        bool certified, bool searches = true, float radius = 0f)
        => CheckCore(planner, () => planner.FindPathWithoutCertificateForTests(start, goal, radius, budget),
            () => planner.FindPath(start, goal, radius, budget), certified, searches);

    static NavPath CheckCore(GridPathPlanner planner, Func<NavPath> unchanged, Func<NavPath> enabled, bool certified, bool searches)
    {
        NavPath expected = unchanged();
        (int beforeSearches, int beforeCertificates) = planner.WorkForTests;
        NavPath actual = enabled();
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.Waypoints, actual.Waypoints);
        for (int i = 0; i < expected.Waypoints.Count; i++)
        {
            Assert.Equal(BitConverter.SingleToInt32Bits(expected.Waypoints[i].Position.X), BitConverter.SingleToInt32Bits(actual.Waypoints[i].Position.X));
            Assert.Equal(BitConverter.SingleToInt32Bits(expected.Waypoints[i].Position.Y), BitConverter.SingleToInt32Bits(actual.Waypoints[i].Position.Y));
            Assert.Equal(expected.IsCollinearPassThrough(i), actual.IsCollinearPassThrough(i));
        }
        Assert.Equal(beforeCertificates + (certified ? 1 : 0), planner.WorkForTests.Certificates);
        Assert.Equal(beforeSearches + (!certified && searches ? 1 : 0), planner.WorkForTests.Searches);
        if (certified) Assert.Same(NavPath.Unreachable, actual);
        return actual;
    }
}
