using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public class TileWorldBuildQueueScopedPrimeTests
{
    [Theory]
    [InlineData("Pending")]
    [InlineData("Running")]
    [InlineData("Ready")]
    public async Task Scoped_prime_builds_waits_and_applies_only_selected_full_ground(string excludedState)
    {
        using var excludedGate = new ManualResetEventSlim();
        using var excludedStarted = new ManualResetEventSlim();
        var dispatcher = new ManualTileBuildDispatcher();
        var applied = new List<TileWorldBuildResult<int>>();
        var builtInline = new List<TileWorldBuildKey>();
        int primeThread = Environment.CurrentManagedThreadId;
        using var queue = new TileWorldBuildQueue<int, int>(
            request =>
            {
                if (excludedState == "Running" && request.Key.Region == new RegionCoord(1, 0))
                {
                    excludedStarted.Set();
                    if (!excludedGate.Wait(TimeSpan.FromSeconds(10)))
                        throw new TimeoutException("excluded worker gate was not released");
                }
                if (Environment.CurrentManagedThreadId == primeThread) builtInline.Add(request.Key);
                return request.Input;
            }, applied.Add,
            new TileWorldBuildQueueOptions { MaxConcurrentBuilds = 1 }, dispatcher);
        Task? excludedWorker = null;
        Task? prime = null;
        try
        {
            if (excludedState == "Pending")
            {
                TileWorldBuildRequest<int> blocker = Request(2, TileWorldBuildKind.CoarseGround);
                queue.Request(blocker);
                queue.Request(Request(1, TileWorldBuildKind.FullGround));
                queue.Request(Request(0, TileWorldBuildKind.FullGround));
                queue.Cancel(blocker.Key);
                dispatcher.RunAt(0);
            }
            else
            {
                queue.Request(Request(1, TileWorldBuildKind.FullGround));
                if (excludedState == "Ready")
                {
                    dispatcher.RunAt(0);
                    queue.CollectCompleted();
                }
                queue.Request(Request(0, TileWorldBuildKind.FullGround));
            }
            builtInline.Clear();

            if (excludedState == "Running")
            {
                excludedWorker = Task.Factory.StartNew(() => dispatcher.RunAt(0),
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                Assert.True(excludedStarted.Wait(TimeSpan.FromSeconds(5)));
                prime = Task.Factory.StartNew(() =>
                {
                    primeThread = Environment.CurrentManagedThreadId;
                    queue.PrimeGameplay(default, new HashSet<RegionCoord> { new(0, 0) });
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                await prime.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(excludedGate.IsSet);
            }
            else
            {
                queue.PrimeGameplay(default, new HashSet<RegionCoord> { new(0, 0) });
            }

            Assert.Equal(new RegionCoord(0, 0), Assert.Single(applied).Key.Region);
            Assert.All(applied, result => Assert.Equal(new RegionCoord(0, 0), result.Key.Region));
            Assert.DoesNotContain(builtInline, key => key.Region == new RegionCoord(1, 0));
            Assert.Equal(new RegionCoord(0, 0), Assert.Single(builtInline).Region);
            Assert.Equal(1, queue.TrackedCount);
            Assert.Equal(excludedState == "Ready" ? 1 : 0, queue.ReadyCount);
            Assert.Equal(excludedState == "Ready" ? 0 : 1, queue.InFlightCount);
        }
        finally
        {
            excludedGate.Set();
            if (excludedWorker is not null) await excludedWorker.WaitAsync(TimeSpan.FromSeconds(5));
            if (prime is not null) await prime.WaitAsync(TimeSpan.FromSeconds(5));
        }

        queue.Pump(default);
        dispatcher.RunAll();
        queue.Pump(default);
        Assert.Equal(new[] { 0, 1 }, applied.ConvertAll(result => result.Key.Region.Rx));
        Assert.Equal(0, queue.TrackedCount);
        Assert.Equal(0, queue.InFlightCount);
    }

    [Fact]
    public void Empty_scoped_prime_builds_and_applies_nothing_and_retains_other_completions()
    {
        var dispatcher = new ManualTileBuildDispatcher();
        var applied = new List<TileWorldBuildResult<int>>();
        var built = new List<TileWorldBuildKey>();
        using var queue = new TileWorldBuildQueue<int, int>(
            request =>
            {
                built.Add(request.Key);
                return request.Input;
            }, applied.Add,
            new TileWorldBuildQueueOptions { MaxConcurrentBuilds = 1 }, dispatcher);
        queue.Request(Request(0, TileWorldBuildKind.FullGround));
        queue.Request(Request(1, TileWorldBuildKind.FullGround));
        dispatcher.RunAt(0);
        built.Clear();

        queue.PrimeGameplay(default, new HashSet<RegionCoord>());

        Assert.Empty(built);
        Assert.Empty(applied);
        Assert.Equal(2, queue.TrackedCount);
        Assert.Equal(1, queue.ReadyCount);
        Assert.Equal(1, queue.InFlightCount);
        Assert.Equal(0, dispatcher.PendingCount);
    }

    [Fact]
    public void Scoped_prime_leaves_selected_cover_coarse_and_hlod_ready_for_Pump()
    {
        var dispatcher = new ManualTileBuildDispatcher();
        var applied = new List<TileWorldBuildResult<int>>();
        using var queue = new TileWorldBuildQueue<int, int>(
            request => request.Input, applied.Add,
            new TileWorldBuildQueueOptions { MaxConcurrentBuilds = 4 }, dispatcher);
        queue.Request(Request(0, TileWorldBuildKind.Cover));
        queue.Request(Request(0, TileWorldBuildKind.CoarseGround));
        queue.Request(Request(0, TileWorldBuildKind.Hlod));
        queue.Request(Request(0, TileWorldBuildKind.FullGround));
        dispatcher.RunAll();

        queue.PrimeGameplay(default, new HashSet<RegionCoord> { new(0, 0) });

        Assert.Equal(TileWorldBuildKind.FullGround, Assert.Single(applied).Key.Kind);
        Assert.Equal(3, queue.ReadyCount);
        Assert.Equal(3, queue.TrackedCount);

        queue.Pump(default);
        Assert.Equal(4, applied.Count);
        Assert.Equal(0, queue.TrackedCount);
    }

    [Fact]
    public void Capture_prime_settles_all_ground_and_cover_without_applying_hlod()
    {
        var dispatcher = new ManualTileBuildDispatcher();
        var applied = new List<TileWorldBuildResult<int>>();
        var built = new List<TileWorldBuildKey>();
        using var queue = new TileWorldBuildQueue<int, int>(
            request =>
            {
                built.Add(request.Key);
                return request.Input;
            }, applied.Add,
            new TileWorldBuildQueueOptions
            {
                MaxConcurrentBuilds = 1,
                MaxFullGroundAppliesPerPump = 0,
                MaxCoverAppliesPerPump = 0,
                MaxCoarseGroundAppliesPerPump = 0,
                MaxHlodAppliesPerPump = 0,
            }, dispatcher);
        queue.Request(Request(4, TileWorldBuildKind.Cover));
        dispatcher.RunAt(0);
        queue.CollectCompleted();
        queue.Request(Request(1, TileWorldBuildKind.Cover));
        queue.Request(Request(3, TileWorldBuildKind.FullGround));
        queue.Request(Request(2, TileWorldBuildKind.FullGround));
        queue.Request(Request(5, TileWorldBuildKind.CoarseGround));
        queue.Request(Request(6, TileWorldBuildKind.Hlod));

        queue.PrimeForCapture(default);

        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, applied.ConvertAll(result => result.Key.Region.Rx));
        Assert.Equal(5, built.Count);
        Assert.All(built, key => Assert.True(key.Kind is TileWorldBuildKind.FullGround or TileWorldBuildKind.CoarseGround or TileWorldBuildKind.Cover));
        Assert.Equal(1, queue.TrackedCount);
        Assert.Equal(1, queue.InFlightCount);
        Assert.Equal(0, queue.ReadyCount);
    }

    [Fact]
    public void Hlod_prime_keeps_ground_and_cover_budgeted()
    {
        var dispatcher = new ManualTileBuildDispatcher();
        var applied = new List<TileWorldBuildResult<int>>();
        using var queue = new TileWorldBuildQueue<int, int>(
            request => request.Input, applied.Add,
            new TileWorldBuildQueueOptions { MaxConcurrentBuilds = 4, MaxHlodAppliesPerPump = 0 }, dispatcher);
        queue.Request(Request(0, TileWorldBuildKind.FullGround));
        queue.Request(Request(1, TileWorldBuildKind.Cover));
        queue.Request(Request(2, TileWorldBuildKind.CoarseGround));
        queue.Request(Request(3, TileWorldBuildKind.Hlod));
        dispatcher.RunAll();

        queue.PrimeHlod(default);

        Assert.Equal(TileWorldBuildKind.Hlod, Assert.Single(applied).Key.Kind);
        Assert.Equal(3, queue.ReadyCount);
        Assert.Equal(3, queue.TrackedCount);
    }

    [Theory]
    [InlineData("FullGround")]
    [InlineData("Cover")]
    public void Capture_prime_propagates_build_failures(string kind)
    {
        var dispatcher = new ManualTileBuildDispatcher();
        var applied = new List<TileWorldBuildResult<int>>();
        using var queue = new TileWorldBuildQueue<int, int>(
            _ => throw new ArgumentException("planned build failure"), applied.Add,
            dispatcher: dispatcher);
        queue.Request(Request(0, Enum.Parse<TileWorldBuildKind>(kind)));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => queue.PrimeForCapture(default));

        Assert.IsType<ArgumentException>(error.InnerException);
        Assert.Empty(applied);
        Assert.Equal(0, queue.TrackedCount);
        Assert.Equal(0, queue.InFlightCount);
    }

    [Fact]
    public void Capture_prime_finishes_superseded_interior_work_before_the_current_build()
    {
        var dispatcher = new ManualTileBuildDispatcher();
        var built = new List<int>();
        var applied = new List<TileWorldBuildResult<int>>();
        using var queue = new TileWorldBuildQueue<int, int>(request =>
        {
            built.Add(request.Input);
            return request.Input;
        }, applied.Add, new TileWorldBuildQueueOptions
        {
            MaxConcurrentBuilds = 1,
            MaxInteriorCutAppliesPerPump = 0,
        }, dispatcher);
        var key = new TileWorldBuildKey(default, -1, TileWorldBuildKind.InteriorCut);
        queue.Request(new TileWorldBuildRequest<int>(key, 1, 1));
        queue.Request(new TileWorldBuildRequest<int>(key, 2, 2));

        queue.PrimeForCapture(default);

        Assert.Equal(new[] { 1, 2 }, built);
        Assert.Equal(2, Assert.Single(applied).Payload);
        Assert.Equal(0, queue.InFlightCount);
        dispatcher.RunAll();
        Assert.Equal(new[] { 1, 2 }, built);
    }

    static TileWorldBuildRequest<int> Request(int x, TileWorldBuildKind kind) =>
        new(new TileWorldBuildKey(new RegionCoord(x, 0), kind == TileWorldBuildKind.Cover ? -1 : 0, kind), 1, x);
}
