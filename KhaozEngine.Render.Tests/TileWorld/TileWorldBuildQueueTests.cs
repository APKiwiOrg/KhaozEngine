using System;
using System.Collections.Generic;
using KhaozEngine.Terrain;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public class TileWorldBuildQueueTests
{
    [Fact]
    public void Requests_dispatch_only_up_to_the_concurrency_bound()
    {
        var dispatcher = new ManualDispatcher();
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
        var dispatcher = new ManualDispatcher();
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
        var dispatcher = new ManualDispatcher();
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
    public void New_generation_rejects_an_older_completed_result()
    {
        var dispatcher = new ManualDispatcher();
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
        var dispatcher = new ManualDispatcher();
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
        var dispatcher = new ManualDispatcher();
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

        queue.PrimeGameplay(default);

        Assert.Equal(new[] { 1, 3, 4, 5 }, applied.ConvertAll(result => result.Key.Region.Rx));
        Assert.Equal(2, queue.ReadyCount);
    }

    [Fact]
    public void Dispose_drains_workers_rejects_results_and_is_idempotent()
    {
        var dispatcher = new ManualDispatcher();
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

    static TileWorldBuildQueue<int, int> Queue(ManualDispatcher dispatcher,
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
        new(new TileWorldBuildKey(new RegionCoord(x, 0), 0, kind), generation, x);

    sealed record DetachedInput(int Value);

    sealed class MutableSource
    {
        public int Value { get; set; }
    }

    sealed class ManualDispatcher : IChunkBuildDispatcher
    {
        readonly List<Action> _pending = new();
        public int PendingCount => _pending.Count;
        public void Schedule(Action build) => _pending.Add(build);
        public void RunAt(int index)
        {
            Action action = _pending[index];
            _pending.RemoveAt(index);
            action();
        }
        public void RunAll()
        {
            while (_pending.Count > 0) RunAt(0);
        }
        public void RunReverse()
        {
            while (_pending.Count > 0) RunAt(_pending.Count - 1);
        }
        public void Drain() => RunAll();
    }
}
