using System.Numerics;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.Shared;
using Xunit;
using static KhaozEngine.Tests.Navigation.GridSearchRequestFixtures;

namespace KhaozEngine.Tests.Navigation;

public sealed class PathPublicationDigestTests
{
    [Theory]
    [InlineData(0, "?")]
    [InlineData(1, "?")]
    [InlineData(2, "?")]
    public void SeededSearchPublishesIdenticalRouteBitsAndCalls(int schedule, string expected)
    {
        var digest = new PathPublicationDigest();
        foreach (int seed in new[] { 1001, 1019, 1037, 2002, 2020, 2038 })
        {
            var corpus = new PlannerCorpus(seed);
            NavSpace space = corpus.NextSpace(surfaces: true);
            bool guarded = seed >= 2000;
            IIncrementalRegionPathPlanner planner = guarded
                ? new GridPathPlanner(space, corpus.NextGraph())
                : new GridPathPlanner(space, hopCostCells: 2f);
            using var pool = planner.CreateWorkspaces(1, PathQueryBudget.Default);
            for (int query = 0; query < 12; query++)
            {
                float radius = guarded ? 0f : corpus.NextRadius();
                Vector3 start = corpus.NextPoint();
                NavGoalRegion goal = corpus.NextRegion();
                using var request = planner.BeginPath(start, goal, radius, corpus.NextBudget(), pool);
                for (int call = 0; call < 20_000; call++)
                {
                    request.Advance(new(PathPublicationDigest.Quota(schedule, call)));
                    if (request.Result is not { } route) continue;
                    digest.Append(route, call + 1);
                    break;
                }
            }
        }

        Assert.Equal(72, digest.Count);
        Assert.Equal(expected, digest.Hex);
    }

    [Theory]
    [InlineData(int.MaxValue, "?")]
    [InlineData(64, "?")]
    [InlineData(17, "?")]
    public void WalkingFollowerAdoptsEveryRouteOnTheSameCall(int quota, string expected)
    {
        const int cells = 9;
        var planner = Corridor(cells);
        var budget = new PathQueryBudget { MaxExpandedNodes = cells, SnapRadius = 0f };
        using var pool = Incremental(planner).CreateWorkspaces(1, budget);
        var follower = new PathFollower(planner, new PathFollowConfig
        {
            Planning = new(new(quota), pool),
            Budget = budget,
            ReplanCooldownSeconds = 0.5f,
        });
        var digest = new PathPublicationDigest();
        NavPath? previous = null;
        Vector3 feet = Start;
        bool arrived = false;
        for (int call = 1; call <= 256; call++)
        {
            var step = follower.Tick(feet, End(cells), 0f, 0.25f);
            if (follower.ActivePath is { } route && !ReferenceEquals(previous, route))
            {
                digest.Append(route, call);
                previous = route;
            }
            if (step.State == PathFollowState.Arrived)
            {
                arrived = true;
                break;
            }
            feet += new Vector3(step.WorldDir.X, 0f, step.WorldDir.Y) * 0.5f;
        }
        follower.Reset();

        Assert.True(arrived);
        Assert.True(digest.Count > 0);
        Assert.Equal(expected, digest.Hex);
    }
}
