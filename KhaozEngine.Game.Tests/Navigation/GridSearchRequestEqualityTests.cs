using System.Numerics;
using KhaozEngine.Navigation;
using Xunit;
using static KhaozEngine.Tests.Navigation.GridSearchRequestFixtures;

namespace KhaozEngine.Tests.Navigation;

public partial class GridSearchRequestEqualityTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(64)]
    [InlineData(PathWorkWeights.Largest - 1)]
    [InlineData(PathWorkWeights.Largest)]
    [InlineData(int.MaxValue)]
    public void MatchesSynchronous(int quantum)
    {
        foreach (var query in TargetedQueries())
        {
            var planner = Incremental(query.Planner);
            using var pool = planner.CreateWorkspaces(1);
            NavPath expected = planner.FindPath(query.Start, query.Goal, 0f, query.Budget);
            Assert.Equal(query.Status, expected.Status);
            using var request = planner.BeginPath(query.Start, query.Goal, 0f, query.Budget, pool);
            EqualBits(expected, Finish(request, quantum));
        }

        for (int seed = 1; seed <= 40; seed++)
        {
            var corpus = new PlannerCorpus(1000 + seed);
            NavSpace space = corpus.NextSpace(surfaces: true);
            float hopCost = corpus.NextBool() ? 4f : 2f;
            CompareCorpus(corpus, new GridPathPlanner(space, hopCost), quantum, guarded: false);

            corpus = new PlannerCorpus(2000 + seed);
            space = corpus.NextSpace(surfaces: true);
            CompareCorpus(corpus, new GridPathPlanner(space, corpus.NextGraph()), quantum, guarded: true);
        }
    }

    static void CompareCorpus(PlannerCorpus corpus, GridPathPlanner grid, int quantum, bool guarded)
    {
        var planner = Incremental(grid);
        using var pool = planner.CreateWorkspaces(1);
        for (int query = 0; query < 40; query++)
        {
            float radius = guarded ? 0f : corpus.NextRadius();
            Vector3 start = corpus.NextPoint();
            PathQueryBudget budget = corpus.NextBudget();
            if (corpus.NextBool())
            {
                corpus.NextPoint();
                continue;
            }
            NavGoalRegion goal = corpus.NextRegion();
            using var request = planner.BeginPath(start, goal, radius, budget, pool);
            EqualBits(planner.FindPath(start, goal, radius, budget), Finish(request, quantum));
        }
    }

    [Fact]
    public void VaryingQuotaPerCall()
    {
        var planner = Incremental(Corridor());
        using var pool = planner.CreateWorkspaces(2);
        using var baseline = planner.BeginPath(Start, End(), 0f, PathQueryBudget.Default, pool);
        NavPath expected = Finish(baseline, 1);
        using var request = planner.BeginPath(Start, End(), 0f, PathQueryBudget.Default, pool);
        int[] quotas = { 3, 1, 32, 2, 7, 13, 1 };
        for (int i = 0; request.State == PathPlanState.Pending; i++)
        {
            Assert.True(i < baseline.WorkSpent);
            request.Advance(new PathWorkBudget(quotas[i % quotas.Length]));
        }
        EqualBits(expected, Assert.IsType<NavPath>(request.Result));
        Assert.Equal(baseline.WorkSpent, request.WorkSpent);
    }

    [Fact]
    public void WorkSpentIsInputDetermined()
    {
        var planner = Incremental(Corridor());
        using var firstPool = planner.CreateWorkspaces(1);
        using var secondPool = planner.CreateWorkspaces(1);
        using var sliced = planner.BeginPath(Start, End(), 0f, PathQueryBudget.Default, firstPool);
        using var whole = planner.BeginPath(Start, End(), 0f, PathQueryBudget.Default, secondPool);
        var cold = Finish(whole, int.MaxValue);
        EqualBits(cold, Finish(sliced, 2));
        Assert.Equal(whole.WorkSpent, sliced.WorkSpent);
        using var warm = planner.BeginPath(Start, End(), 0f, PathQueryBudget.Default, secondPool);
        EqualBits(cold, Finish(warm, 7));
        Assert.Equal(whole.WorkSpent, warm.WorkSpent);
    }

    [Fact]
    public void QuotaExhaustionStaysPending()
    {
        var planner = Incremental(Corridor());
        using var pool = planner.CreateWorkspaces(1);
        using var request = planner.BeginPath(Start, End(), 0f, PathQueryBudget.Default, pool);
        Assert.Equal(0, request.WorkSpent);
        request.Advance(new PathWorkBudget(1));
        Assert.Equal(PathPlanState.Pending, request.State);
        Assert.True(request.WorkSpent > 0);
        Assert.Null(request.Result);
        Assert.Equal(NavPathStatus.Complete, Finish(request, 1).Status);
    }

    [Fact]
    public void InterleavedRequestsAndSynchronousQueries()
    {
        var planner = Incremental(Corridor());
        using var pool = planner.CreateWorkspaces(2);
        var otherStart = End().Anchor;
        var otherGoal = DisconnectedPlannerFixture.Member(Start);
        using var first = planner.BeginPath(Start, End(), 0f, PathQueryBudget.Default, pool);
        using var other = planner.BeginPath(otherStart, otherGoal, 0f, PathQueryBudget.Default, pool);
        NavPath expected = planner.FindPath(Start, End(), 0f, PathQueryBudget.Default);
        NavPath otherExpected = planner.FindPath(otherStart, otherGoal, 0f, PathQueryBudget.Default);
        for (int i = 0; first.State == PathPlanState.Pending || other.State == PathPlanState.Pending; i++)
        {
            Assert.True(i < 1000);
            first.Advance(new PathWorkBudget(5));
            EqualBits(otherExpected, planner.FindPath(otherStart, otherGoal, 0f, PathQueryBudget.Default));
            other.Advance(new PathWorkBudget(3));
        }
        EqualBits(expected, Assert.IsType<NavPath>(first.Result));
        EqualBits(otherExpected, Assert.IsType<NavPath>(other.Result));
        Assert.Equal(0, pool.Waits);
    }

    [Fact]
    public void StartsFromUsesTheResolvedCell()
    {
        var planner = Incremental(Corridor());
        using var pool = planner.CreateWorkspaces(1);
        using var request = planner.BeginPath(Start, End(), 0f, GridPathPlannerDisconnectedProgressTests.Budget(64), pool);
        Finish(request, 7);
        Assert.True(request.StartsFrom(Start));
        Assert.True(request.StartsFrom(Start with { X = 0.9f }));
        Assert.False(request.StartsFrom(Start with { X = 1.1f }));
        Assert.False(request.StartsFrom(Start with { X = -1f }));
    }
}
