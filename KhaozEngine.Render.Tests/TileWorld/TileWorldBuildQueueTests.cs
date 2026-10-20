using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Terrain;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public class TileWorldBuildQueueTests
{
    [Fact]
    public void Requests_dispatch_only_up_to_the_concurrency_bound()
    {
        var dispatcher = new ManualTileBuildDispatcher();
        var applied = new List<TileWorldBuildResult<int>>();
        using var queue = Queue(dispatcher, applied, maxConcurrency: 2);

        for (int x = 0; x < 5; x++) queue.Request(Request(x, TileWorldBuildKind.FullGround, generation: 1));

        Assert.Equal(2, dispatcher.PendingCount);
        dispatcher.RunAt(0);
        queue.Pump(default);
        Assert.Equal(2, dispatcher.PendingCount);
        Assert.Single(applied);
    }

    [Fact]
    public void Worker_receives_the_detached_snapshot_not_later_source_mutation()
    {
        var dispatcher = new ManualTileBuildDispatcher();
        var observed = new List<int>();
        var source = new MutableSource { Value = 17 };
        var input = new DetachedInput(source.Value);
        using var queue = new TileWorldBuildQueue<DetachedInput, int>(
            request => request.Input.Value,
            result => observed.Add(result.Payload),
            new TileWorldBuildQueueOptions { MaxConcurrentBuilds = 1 }, dispatcher);

        queue.Request(new TileWorldBuildRequest<DetachedInput>(
            new TileWorldBuildKey(new RegionCoord(0, 0), 0, TileWorldBuildKind.Hlod), 1, input));
        source.Value = 99;
        dispatcher.RunAll();
        queue.Pump(default);

        Assert.Equal(new[] { 17 }, observed);
    }

    [Fact]
    public void Pump_applies_nearest_first_under_three_independent_budgets()
    {
        var dispatcher = new ManualTileBuildDispatcher();
        var applied = new List<TileWorldBuildResult<int>>();
        using var queue = new TileWorldBuildQueue<int, int>(
            request => request.Input,
            applied.Add,
            new TileWorldBuildQueueOptions
            {
                MaxConcurrentBuilds = 20,
                MaxFullGroundAppliesPerPump = 1,
                MaxCoarseGroundAppliesPerPump = 2,
                MaxHlodAppliesPerPump = 1,
            }, dispatcher);
        queue.Request(Request(9, TileWorldBuildKind.FullGround, 1));
        queue.Request(Request(1, TileWorldBuildKind.FullGround, 1));
        queue.Request(Request(8, TileWorldBuildKind.CoarseGround, 1));
        queue.Request(Request(2, TileWorldBuildKind.CoarseGround, 1));
        queue.Request(Request(3, TileWorldBuildKind.CoarseGround, 1));
        queue.Request(Request(7, TileWorldBuildKind.Hlod, 1));
        queue.Request(Request(4, TileWorldBuildKind.Hlod, 1));
        dispatcher.RunReverse();

        queue.Pump(new RegionCoord(10, 0));

        Assert.Equal(new[] { 9, 8, 7, 3 }, applied.ConvertAll(result => result.Key.Region.Rx));
        Assert.Equal(3, queue.ReadyCount);
    }

    [Fact]
    public void Pump_applies_cover_nearest_first_with_independent_caps()
    {
        var dispatcher = new ManualTileBuildDispatcher();
        var applied = new List<TileWorldBuildResult<int>>();
        using var queue = new TileWorldBuildQueue<int, int>(
            request => request.Input, applied.Add,
            new TileWorldBuildQueueOptions
            {
                MaxConcurrentBuilds = 20,
                MaxFullGroundAppliesPerPump = 1,
                MaxCoverAppliesPerPump = 1,
                MaxCoarseGroundAppliesPerPump = 2,
                MaxHlodAppliesPerPump = 1,
            }, dispatcher);
        queue.Request(Request(9, TileWorldBuildKind.FullGround, 1));
        queue.Request(Request(1, TileWorldBuildKind.FullGround, 1));
        queue.Request(Request(5, TileWorldBuildKind.Cover, 1));
        queue.Request(Request(0, TileWorldBuildKind.Cover, 1));
        queue.Request(Request(6, TileWorldBuildKind.Cover, 1));
        queue.Request(Request(8, TileWorldBuildKind.CoarseGround, 1));
        queue.Request(Request(2, TileWorldBuildKind.CoarseGround, 1));
        queue.Request(Request(3, TileWorldBuildKind.CoarseGround, 1));
        queue.Request(Request(7, TileWorldBuildKind.Hlod, 1));
        queue.Request(Request(4, TileWorldBuildKind.Hlod, 1));
        dispatcher.RunReverse();

        queue.Pump(new RegionCoord(10, 0));

        Assert.Equal(new[] { 9, 8, 7, 6, 3 }, applied.ConvertAll(result => result.Key.Region.Rx));
        TileWorldBuildResult<int> cover = Assert.Single(applied.FindAll(result => result.Key.Kind == TileWorldBuildKind.Cover));
        Assert.Equal(new RegionCoord(6, 0), cover.Key.Region);
        Assert.Equal(4, Math.Abs(cover.Key.Region.Rx - 10));
        Assert.Single(applied.FindAll(result => result.Key.Kind == TileWorldBuildKind.FullGround));
        Assert.Equal(2, applied.FindAll(result => result.Key.Kind == TileWorldBuildKind.CoarseGround).Count);
        Assert.Single(applied.FindAll(result => result.Key.Kind == TileWorldBuildKind.Hlod));
        Assert.Equal(5, queue.ReadyCount);
        Assert.Equal(5, queue.TrackedCount);
        Assert.Equal(0, queue.InFlightCount);
    }

    [Fact]
    public void Pump_with_zero_cover_cap_keeps_cover_ready()
    {
        var dispatcher = new ManualTileBuildDispatcher();
        var applied = new List<TileWorldBuildResult<int>>();
        using var queue = new TileWorldBuildQueue<int, int>(
            request => request.Input, applied.Add,
            new TileWorldBuildQueueOptions { MaxCoverAppliesPerPump = 0 }, dispatcher);
        queue.Request(Request(0, TileWorldBuildKind.Cover, 1));
        queue.Request(Request(1, TileWorldBuildKind.FullGround, 1));
        dispatcher.RunAll();

        queue.Pump(default);

        Assert.Equal(TileWorldBuildKind.FullGround, Assert.Single(applied).Key.Kind);
        Assert.Equal(1, queue.ReadyCount);
        Assert.Equal(1, queue.TrackedCount);
        Assert.Equal(0, queue.InFlightCount);
    }

    [Fact]
    public void Pump_uses_two_cover_applies_by_default()
    {
        var dispatcher = new ManualTileBuildDispatcher();
        var applied = new List<TileWorldBuildResult<int>>();
        using var queue = new TileWorldBuildQueue<int, int>(
            request => request.Input, applied.Add,
            new TileWorldBuildQueueOptions { MaxConcurrentBuilds = 3 }, dispatcher);
        for (int x = 0; x < 3; x++) queue.Request(Request(x, TileWorldBuildKind.Cover, 1));
        dispatcher.RunReverse();

        queue.Pump(default);

        Assert.Equal(new[] { 0, 1 }, applied.ConvertAll(result => result.Key.Region.Rx));
        Assert.Equal(1, queue.ReadyCount);
    }

    [Fact]
    public void Negative_cover_cap_is_rejected()
    {
        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TileWorldBuildQueue<int, int>(request => request.Input, _ => { },
                new TileWorldBuildQueueOptions { MaxCoverAppliesPerPump = -1 }));

        Assert.Equal(nameof(TileWorldBuildQueueOptions.MaxCoverAppliesPerPump), error.ParamName);
    }

    [Fact]
    public void New_generation_rejects_an_older_completed_result()
    {
        var dispatcher = new ManualTileBuildDispatcher();
        var applied = new List<TileWorldBuildResult<int>>();
        using var queue = Queue(dispatcher, applied, maxConcurrency: 2);
        TileWorldBuildKey key = Request(2, TileWorldBuildKind.Hlod, 1).Key;

        queue.Request(new TileWorldBuildRequest<int>(key, 1, 10));
        queue.Request(new TileWorldBuildRequest<int>(key, 2, 20));
        dispatcher.RunReverse();
        queue.Pump(default);

        TileWorldBuildResult<int> result = Assert.Single(applied);
        Assert.Equal(2, result.Generation);
        Assert.Equal(20, result.Payload);
    }

    [Fact]
    public void Cancel_rejects_running_and_ready_results()
    {
        var dispatcher = new ManualTileBuildDispatcher();
        var applied = new List<TileWorldBuildResult<int>>();
        using var queue = Queue(dispatcher, applied, maxConcurrency: 2);
        TileWorldBuildRequest<int> running = Request(1, TileWorldBuildKind.FullGround, 1);
        TileWorldBuildRequest<int> ready = Request(2, TileWorldBuildKind.FullGround, 1);
        queue.Request(running);
        queue.Request(ready);

        dispatcher.RunAt(1);
        queue.CollectCompleted();
        queue.Cancel(running.Key);
        queue.Cancel(ready.Key);
        dispatcher.RunAll();
        queue.Pump(default);

        Assert.Empty(applied);
        Assert.Equal(0, queue.TrackedCount);
    }

    [Fact]
    public void PrimeGameplay_blocks_for_full_ground_but_keeps_decor_apply_budgeted()
    {
        var dispatcher = new ManualTileBuildDispatcher();
        var applied = new List<TileWorldBuildResult<int>>();
        using var queue = new TileWorldBuildQueue<int, int>(
            request => request.Input,
            applied.Add,
            new TileWorldBuildQueueOptions
            {
                MaxConcurrentBuilds = 2,
                MaxFullGroundAppliesPerPump = 1,
                MaxCoarseGroundAppliesPerPump = 1,
                MaxHlodAppliesPerPump = 1,
            }, dispatcher);
        queue.Request(Request(4, TileWorldBuildKind.FullGround, 1));
        queue.Request(Request(3, TileWorldBuildKind.FullGround, 1));
        queue.Request(Request(2, TileWorldBuildKind.CoarseGround, 1));
        queue.Request(Request(1, TileWorldBuildKind.CoarseGround, 1));
        queue.Request(Request(6, TileWorldBuildKind.Hlod, 1));
        queue.Request(Request(5, TileWorldBuildKind.Hlod, 1));

        queue.PrimeGameplay(default, new HashSet<RegionCoord> { new(4, 0), new(3, 0) });

        Assert.Equal(new[] { 3, 4 }, applied.ConvertAll(result => result.Key.Region.Rx));
        Assert.Equal(4, queue.TrackedCount);
        Assert.Equal(0, queue.ReadyCount);
    }

    [Fact]
    public void PrimeGameplay_leaves_completed_decor_ready_for_a_later_Pump()
    {
        var dispatcher = new ManualTileBuildDispatcher();
        var applied = new List<TileWorldBuildResult<int>>();
        using var queue = Queue(dispatcher, applied, maxConcurrency: 2);
        queue.Request(Request(0, TileWorldBuildKind.CoarseGround, 1));
        queue.Request(Request(1, TileWorldBuildKind.FullGround, 1));
        dispatcher.RunAll();
        queue.CollectCompleted();

        queue.PrimeGameplay(default, new HashSet<RegionCoord> { new(1, 0) });

        Assert.Equal(new[] { TileWorldBuildKind.FullGround },
            applied.ConvertAll(result => result.Key.Kind));
        Assert.Equal(1, queue.ReadyCount);

        queue.Pump(default);
        Assert.Equal(new[] { TileWorldBuildKind.FullGround, TileWorldBuildKind.CoarseGround },
            applied.ConvertAll(result => result.Key.Kind));
    }

    [Fact]
    public async Task PrimeGameplay_does_not_wait_for_blocked_decor_ahead_of_gameplay()
    {
        using var decorGate = new ManualResetEventSlim();
        using var decorStarted = new ManualResetEventSlim();
        var dispatcher = new ThreadDispatcher();
        var applied = new List<(TileWorldBuildKind Kind, int Thread)>();
        using var queue = new TileWorldBuildQueue<int, int>(
            request =>
            {
                if (request.Key.Kind == TileWorldBuildKind.CoarseGround)
                {
                    decorStarted.Set();
                    decorGate.Wait();
                }
                return request.Input;
            },
            result => applied.Add((result.Key.Kind, Environment.CurrentManagedThreadId)),
            new TileWorldBuildQueueOptions
            {
                MaxConcurrentBuilds = 1,
                MaxFullGroundAppliesPerPump = 1,
                MaxCoarseGroundAppliesPerPump = 1,
                MaxHlodAppliesPerPump = 1,
            }, dispatcher);
        // Using declarations unwind in reverse order. This opens the gate before queue disposal drains the worker,
        // including when a timing assertion exits before the explicit Set below.
        using var releaseDecorBeforeQueue = new GateRelease(decorGate);
        queue.Request(Request(1, TileWorldBuildKind.CoarseGround, 1));
        queue.Request(Request(0, TileWorldBuildKind.FullGround, 1));
        queue.Request(Request(2, TileWorldBuildKind.CoarseGround, 1));
        Assert.True(decorStarted.Wait(TimeSpan.FromSeconds(5)));

        int primeThread = 0;
        Task prime = Task.Factory.StartNew(
            () =>
            {
                primeThread = Environment.CurrentManagedThreadId;
                queue.PrimeGameplay(default, new HashSet<RegionCoord> { new(0, 0) });
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Task early = await Task.WhenAny(prime, Task.Delay(TimeSpan.FromMilliseconds(500)));
        bool returnedBeforeDecor = ReferenceEquals(early, prime);
        decorGate.Set();
        await prime.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(returnedBeforeDecor, "gameplay prime waited for blocked decor work");
        Assert.Equal(new[] { (TileWorldBuildKind.FullGround, primeThread) }, applied);
        Assert.Equal(2, queue.TrackedCount);

        dispatcher.Drain();
        queue.Pump(default);
        Assert.Equal(2, applied.Count);
        Assert.Equal(TileWorldBuildKind.CoarseGround, applied[1].Kind);
        dispatcher.Drain();
        queue.Pump(default);
        Assert.Equal(3, applied.Count);
        Assert.All(applied.GetRange(1, 2), result => Assert.Equal(TileWorldBuildKind.CoarseGround, result.Kind));
    }

    [Fact]
    public async Task Blocked_decor_cleanup_releases_the_worker_when_the_test_body_unwinds_early()
    {
        Task<Exception> unwind = Task.Factory.StartNew(
            () => Record.Exception(RunBlockedDecorHarnessUntilPlannedFailure),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        Exception? error = await unwind.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.IsType<PlannedCleanupException>(error);
    }

    static void RunBlockedDecorHarnessUntilPlannedFailure()
    {
        using var decorGate = new ManualResetEventSlim();
        using var decorStarted = new ManualResetEventSlim();
        var dispatcher = new ThreadDispatcher();
        using var queue = new TileWorldBuildQueue<int, int>(
            request =>
            {
                decorStarted.Set();
                decorGate.Wait();
                return request.Input;
            },
            _ => { },
            new TileWorldBuildQueueOptions { MaxConcurrentBuilds = 1 }, dispatcher);
        using var releaseDecorBeforeQueue = new GateRelease(decorGate);
        queue.Request(Request(1, TileWorldBuildKind.CoarseGround, 1));
        Assert.True(decorStarted.Wait(TimeSpan.FromSeconds(5)));

        throw new PlannedCleanupException();
    }

    [Fact]
    public void Dispose_drains_workers_rejects_results_and_is_idempotent()
    {
        var dispatcher = new ManualTileBuildDispatcher();
        var applied = new List<TileWorldBuildResult<int>>();
        var queue = Queue(dispatcher, applied, maxConcurrency: 2);
        queue.Request(Request(1, TileWorldBuildKind.FullGround, 1));
        queue.Request(Request(2, TileWorldBuildKind.Hlod, 1));

        queue.Dispose();
        queue.Dispose();

        Assert.Equal(0, dispatcher.PendingCount);
        Assert.Empty(applied);
        Assert.Equal(0, queue.TrackedCount);
        Assert.Throws<ObjectDisposedException>(() => queue.Request(Request(3, TileWorldBuildKind.FullGround, 1)));
    }

    [Fact]
    public void Interior_cut_has_an_independent_apply_budget_and_is_never_gameplay_primed_inline()
    {
        var dispatcher = new ManualTileBuildDispatcher();
        var applied = new List<TileWorldBuildResult<int>>();
        using var queue = new TileWorldBuildQueue<int, int>(request => request.Input, applied.Add,
            new TileWorldBuildQueueOptions { MaxConcurrentBuilds = 5, MaxInteriorCutAppliesPerPump = 1,
                MaxHlodAppliesPerPump = 0 }, dispatcher);
        queue.Request(Request(0, TileWorldBuildKind.InteriorCut, 1));
        queue.Request(Request(1, TileWorldBuildKind.InteriorCut, 1));
        queue.Request(Request(2, TileWorldBuildKind.Hlod, 1));
        queue.PrimeGameplay(default, new HashSet<RegionCoord> { new(0, 0) });
        Assert.Empty(applied);
        dispatcher.RunAll();
        queue.Pump(default);
        Assert.Equal(TileWorldBuildKind.InteriorCut, Assert.Single(applied).Key.Kind);
        Assert.Equal(2, queue.ReadyCount);
    }

    [Fact]
    public void Negative_interior_cap_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TileWorldBuildQueue<int, int>(
            request => request.Input, _ => { }, new TileWorldBuildQueueOptions { MaxInteriorCutAppliesPerPump = -1 }));
    }

    static TileWorldBuildQueue<int, int> Queue(ManualTileBuildDispatcher dispatcher,
                                                List<TileWorldBuildResult<int>> applied,
                                                int maxConcurrency) =>
        new(request => request.Input, applied.Add,
            new TileWorldBuildQueueOptions
            {
                MaxConcurrentBuilds = maxConcurrency,
                MaxFullGroundAppliesPerPump = 10,
                MaxCoarseGroundAppliesPerPump = 10,
                MaxHlodAppliesPerPump = 10,
            }, dispatcher);

    static TileWorldBuildRequest<int> Request(int x, TileWorldBuildKind kind, long generation) =>
        new(new TileWorldBuildKey(new RegionCoord(x, 0), kind == TileWorldBuildKind.Cover ? -1 : 0, kind), generation, x);

    sealed record DetachedInput(int Value);

    sealed class MutableSource
    {
        public int Value { get; set; }
    }

    sealed class ThreadDispatcher : IChunkBuildDispatcher
    {
        readonly object _gate = new();
        readonly List<Task> _tasks = new();

        public void Schedule(Action build)
        {
            Task task = Task.Factory.StartNew(
                build,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            lock (_gate) _tasks.Add(task);
        }

        public void Drain()
        {
            Task[] tasks;
            lock (_gate)
            {
                tasks = _tasks.ToArray();
                _tasks.Clear();
            }
            if (tasks.Length > 0) Task.WaitAll(tasks);
        }
    }

    sealed class PlannedCleanupException : Exception;

    sealed class GateRelease : IDisposable
    {
        readonly ManualResetEventSlim _gate;

        public GateRelease(ManualResetEventSlim gate) => _gate = gate;

        public void Dispose() => _gate.Set();
    }
}
