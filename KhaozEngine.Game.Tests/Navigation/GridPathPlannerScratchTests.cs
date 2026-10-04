using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

/// <summary>
/// The planner's reused search scratch (KhaozEngine#1288): a warmed query allocates only the path it
/// returns, routes stay right across the generation stamp's wraparound, and overlapping queries on one
/// planner never share working memory.
/// </summary>
[Collection("AllocSensitive")]
public class GridPathPlannerScratchTests
{
    const int Size = 128;
    const float AgentRadius = 0.25f;
    static readonly PathQueryBudget Budget = new() { MaxExpandedNodes = 100_000, SnapRadius = 3f };

    // Open ground with a wall the straight line from Start to Goal crosses, so a point query runs A* and
    // the string pull rather than the line-of-sight fast path.
    static readonly NavSpace WalledSpace = NavSpace.Single(NavGrid.FromSurfaces(Size, Size, 0.5f, 0f, 0f,
        (x, z) => new NavSurfaceSample(!(x == 64 && z >= 40 && z < 90), 0f, 2f), 0.5f, 1f, -1f, 2f));

    static readonly Vector3 Start = new(20f, 0f, 32f);
    static readonly Vector3 Goal = new(44f, 0f, 32f);

    [Fact]
    public void WarmedPointQueryAllocatesOnlyItsPath()
    {
        var planner = new GridPathPlanner(WalledSpace);
        AssertAllocatesOnlyThePath("point query",
            () => planner.FindPath(Start, Goal, AgentRadius, Budget));
    }

    [Fact]
    public void WarmedRegionQueryAllocatesOnlyItsPath()
    {
        var planner = new GridPathPlanner(WalledSpace);
        var region = new NavGoalRegion(Goal, 1f, feet => Vector3.Distance(feet, Goal) <= 1f);
        AssertAllocatesOnlyThePath("region query",
            () => planner.FindPath(Start, region, AgentRadius, Budget));
    }

    [Fact]
    public void RoutesStayIdenticalAcrossTheGenerationWraparound()
    {
        for (int seed = 1; seed <= 6; seed++)
        {
            var corpus = new PlannerCorpus(3000 + seed);
            NavSpace space = corpus.NextSpace(surfaces: true);
            var planner = new GridPathPlanner(space);
            var reference = new ReferenceGridPathPlanner(space);
            for (int q = 0; q < 24; q++)
            {
                // Two searches before the wrap leave stamps at the counter's top. Without the wrap's
                // clear they would read as live nodes with stale scores in every later search.
                if (q == 4) planner.SeedScratchGeneration(int.MaxValue - 5);
                Vector3 start = corpus.NextPoint();
                NavGoalRegion region = corpus.NextRegion();
                Vector3 goal = corpus.NextPoint();
                GridPathPlannerEqualityCorpusTests.AssertSame(
                    reference.FindPath(start, goal, 0f, PathQueryBudget.Default),
                    planner.FindPath(start, goal, 0f, PathQueryBudget.Default), null, $"seed {seed} point {q}");
                GridPathPlannerEqualityCorpusTests.AssertSame(
                    reference.FindPath(start, region, 0f, PathQueryBudget.Default),
                    planner.FindPath(start, region, 0f, PathQueryBudget.Default), null, $"seed {seed} region {q}");
            }
        }
    }

    [Fact]
    public void ConcurrentQueriesOnOnePlannerMatchSequentialRoutes()
    {
        var corpus = new PlannerCorpus(4001);
        NavSpace space = corpus.NextSpace(surfaces: true);
        var reference = new ReferenceGridPathPlanner(space);
        var queries = new List<(Vector3 Start, Vector3 Goal, NavGoalRegion Region)>();
        for (int i = 0; i < 24; i++) queries.Add((corpus.NextPoint(), corpus.NextPoint(), corpus.NextRegion()));
        var expected = new List<(NavPath Point, NavPath Region)>();
        foreach ((Vector3 start, Vector3 goal, NavGoalRegion region) in queries)
            expected.Add((reference.FindPath(start, goal, 0f, PathQueryBudget.Default),
                reference.FindPath(start, region, 0f, PathQueryBudget.Default)));

        var planner = new GridPathPlanner(space);
        const int threads = 4;
        using var gate = new Barrier(threads);
        var failures = new List<Exception>();
        var workers = new Thread[threads];
        for (int t = 0; t < threads; t++)
        {
            int offset = t * 5;
            workers[t] = new Thread(() =>
            {
                try
                {
                    gate.SignalAndWait();
                    for (int round = 0; round < 10; round++)
                        for (int i = 0; i < queries.Count; i++)
                        {
                            int q = (i + offset + round) % queries.Count;
                            (Vector3 start, Vector3 goal, NavGoalRegion region) = queries[q];
                            GridPathPlannerEqualityCorpusTests.AssertSame(expected[q].Point,
                                planner.FindPath(start, goal, 0f, PathQueryBudget.Default), null, $"point {q}");
                            GridPathPlannerEqualityCorpusTests.AssertSame(expected[q].Region,
                                planner.FindPath(start, region, 0f, PathQueryBudget.Default), null, $"region {q}");
                        }
                }
                catch (Exception e)
                {
                    lock (failures) failures.Add(e);
                }
            });
            workers[t].Start();
        }
        foreach (Thread worker in workers) worker.Join();

        Assert.Empty(failures);
    }

    [Fact]
    public void RegionPredicateMayQueryTheSamePlanner()
    {
        var planner = new GridPathPlanner(WalledSpace);
        var reference = new ReferenceGridPathPlanner(WalledSpace);
        NavGoalRegion Region(IPathPlanner inner) => new(Goal, 1f, feet =>
            Vector3.Distance(feet, Goal) <= 1f &&
            inner.FindPath(Goal, feet, AgentRadius, Budget).Status == NavPathStatus.Complete);

        GridPathPlannerEqualityCorpusTests.AssertSame(
            reference.FindPath(Start, Region(reference), AgentRadius, Budget),
            planner.FindPath(Start, Region(planner), AgentRadius, Budget), null, "re-entrant region");
    }

    static void AssertAllocatesOnlyThePath(string description, Func<NavPath> query)
    {
        NavPath path = query();
        Assert.Equal(NavPathStatus.Complete, path.Status);
        Assert.True(path.Waypoints.Count > 1, $"{description} should exercise the search, not the straight shot");
        query();

        // An equal path built directly allocates its waypoint array, the read-only view and the path.
        Func<NavPath> pathOnly = () => new NavPath(path.Status, new NavWaypoint[path.Waypoints.Count]);
        pathOnly();
        long expected = Measure(pathOnly);
        long actual = Measure(query);
        if (actual != expected)
        {
            // One retry, as AllocAssert does, for a foreign collection landing in the window.
            expected = Measure(pathOnly);
            actual = Measure(query);
        }
        Assert.True(actual == expected, $"{description} allocated {actual} bytes, its path alone costs {expected}");
    }

    static long Measure(Func<NavPath> work)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        NavPath path = work();
        long after = GC.GetAllocatedBytesForCurrentThread();
        GC.KeepAlive(path);
        return after - before;
    }
}
