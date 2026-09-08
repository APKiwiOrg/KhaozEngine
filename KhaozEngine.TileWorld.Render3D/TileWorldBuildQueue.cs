using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Terrain;

namespace KhaozEngine.TileWorld;

internal enum TileWorldBuildKind
{
    FullGround,
    CoarseGround,
    Hlod,
}

internal readonly record struct TileWorldBuildKey(
    RegionCoord Region, int Plane, TileWorldBuildKind Kind, string LayerId = "");

internal sealed record TileWorldBuildRequest<TInput>(TileWorldBuildKey Key, long Generation, TInput Input);

internal sealed record TileWorldBuildResult<TOutput>(TileWorldBuildKey Key, long Generation, TOutput Payload);

internal sealed record TileWorldBuildQueueOptions
{
    public int MaxConcurrentBuilds { get; init; } = 2;
    public int MaxFullGroundAppliesPerPump { get; init; } = 2;
    public int MaxCoarseGroundAppliesPerPump { get; init; } = 2;
    public int MaxHlodAppliesPerPump { get; init; } = 2;

    public void Validate()
    {
        if (MaxConcurrentBuilds < 1) throw new ArgumentOutOfRangeException(nameof(MaxConcurrentBuilds));
        if (MaxFullGroundAppliesPerPump < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxFullGroundAppliesPerPump));
        if (MaxCoarseGroundAppliesPerPump < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxCoarseGroundAppliesPerPump));
        if (MaxHlodAppliesPerPump < 0) throw new ArgumentOutOfRangeException(nameof(MaxHlodAppliesPerPump));
    }
}

/// <summary>Bounded worker dispatch and frame-thread apply bookkeeping for detached TileWorld CPU inputs.</summary>
internal sealed class TileWorldBuildQueue<TInput, TOutput> : IDisposable
{
    readonly Func<TileWorldBuildRequest<TInput>, TOutput> _build;
    readonly Action<TileWorldBuildResult<TOutput>> _apply;
    readonly TileWorldBuildQueueOptions _options;
    readonly IChunkBuildDispatcher _dispatcher;
    readonly Queue<TileWorldBuildRequest<TInput>> _pending = new();
    readonly Dictionary<TileWorldBuildKey, Slot> _tracked = new();
    readonly Dictionary<TileWorldBuildKey, TileWorldBuildResult<TOutput>> _ready = new();
    readonly ConcurrentQueue<Completion> _completed = new();
    int _running;
    bool _disposed;

    enum WorkState { Pending, Running, Ready }

    readonly record struct Slot(long Generation, WorkState State, ScheduledWork? Work);

    readonly record struct Completion(
        TileWorldBuildKey Key, long Generation, TOutput Payload, Exception? Error, bool CountsWorker);

    sealed class ScheduledWork
    {
        readonly Action _body;
        readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _claimed;

        public ScheduledWork(Action body) => _body = body;

        public void Run()
        {
            if (Interlocked.CompareExchange(ref _claimed, 1, 0) != 0) return;
            try { _body(); }
            finally { _finished.TrySetResult(); }
        }

        public void RunOrWait()
        {
            Run();
            _finished.Task.GetAwaiter().GetResult();
        }
    }

    public TileWorldBuildQueue(Func<TileWorldBuildRequest<TInput>, TOutput> build,
                               Action<TileWorldBuildResult<TOutput>> apply,
                               TileWorldBuildQueueOptions? options = null,
                               IChunkBuildDispatcher? dispatcher = null)
    {
        _build = build ?? throw new ArgumentNullException(nameof(build));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _options = options ?? new TileWorldBuildQueueOptions();
        _options.Validate();
        _dispatcher = dispatcher ?? new TaskChunkBuildDispatcher();
    }

    public int TrackedCount => _tracked.Count;
    public int InFlightCount => _running + PendingCurrentCount();
    public int ReadyCount => _ready.Count;

    public void Request(TileWorldBuildRequest<TInput> request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (request is null) throw new ArgumentNullException(nameof(request));
        _tracked[request.Key] = new Slot(request.Generation, WorkState.Pending, Work: null);
        _ready.Remove(request.Key);
        _pending.Enqueue(request);
        DispatchAvailable();
    }

    public void Cancel(TileWorldBuildKey key)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _tracked.Remove(key);
        _ready.Remove(key);
    }

    public void CollectCompleted()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CollectCompletedCore(reportFailures: true);
        DispatchAvailable();
    }

    public void Pump(RegionCoord focus)
    {
        CollectCompleted();
        ApplyReady(focus,
            _options.MaxFullGroundAppliesPerPump,
            _options.MaxCoarseGroundAppliesPerPump,
            _options.MaxHlodAppliesPerPump);
    }

    public void PrimeGameplay(RegionCoord focus)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RunPendingGameplayInline();
        FinishScheduledGameplay();
        CollectCompletedCore(reportFailures: true);
        ApplyReady(focus, int.MaxValue, coarseBudget: 0, hlodBudget: 0);
    }

    void RunPendingGameplayInline()
    {
        foreach (TileWorldBuildRequest<TInput> request in _pending)
        {
            if (request.Key.Kind != TileWorldBuildKind.FullGround ||
                !_tracked.TryGetValue(request.Key, out Slot slot) ||
                slot.Generation != request.Generation || slot.State != WorkState.Pending)
                continue;
            _tracked[request.Key] = slot with { State = WorkState.Running };
            Build(request, countsWorker: false);
        }
    }

    void FinishScheduledGameplay()
    {
        var running = new List<ScheduledWork>();
        foreach (KeyValuePair<TileWorldBuildKey, Slot> item in _tracked)
            if (item.Key.Kind == TileWorldBuildKind.FullGround &&
                item.Value.State == WorkState.Running && item.Value.Work is { } work)
                running.Add(work);
        for (int i = 0; i < running.Count; i++) running[i].RunOrWait();
    }

    void ApplyReady(RegionCoord focus, int fullBudget, int coarseBudget, int hlodBudget)
    {
        if (_ready.Count == 0) return;
        var ordered = new List<TileWorldBuildResult<TOutput>>(_ready.Values);
        ordered.Sort((a, b) => NearestFirst(a, b, focus));
        for (int i = 0; i < ordered.Count; i++)
        {
            TileWorldBuildResult<TOutput> result = ordered[i];
            ref int budget = ref Budget(result.Key.Kind, ref fullBudget, ref coarseBudget, ref hlodBudget);
            if (budget <= 0) continue;
            if (!_tracked.TryGetValue(result.Key, out Slot slot) ||
                slot.Generation != result.Generation || slot.State != WorkState.Ready)
            {
                _ready.Remove(result.Key);
                continue;
            }
            _ready.Remove(result.Key);
            _tracked.Remove(result.Key);
            budget--;
            _apply(result);
        }
    }

    static ref int Budget(TileWorldBuildKind kind, ref int full, ref int coarse, ref int hlod)
    {
        if (kind == TileWorldBuildKind.FullGround) return ref full;
        if (kind == TileWorldBuildKind.CoarseGround) return ref coarse;
        return ref hlod;
    }

    void DispatchAvailable()
    {
        while (_running < _options.MaxConcurrentBuilds && _pending.Count > 0)
        {
            TileWorldBuildRequest<TInput> request = _pending.Dequeue();
            if (!_tracked.TryGetValue(request.Key, out Slot slot) || slot.Generation != request.Generation ||
                slot.State != WorkState.Pending)
                continue;

            var work = new ScheduledWork(() => Build(request, countsWorker: true));
            _tracked[request.Key] = slot with { State = WorkState.Running, Work = work };
            _running++;
            try
            {
                _dispatcher.Schedule(work.Run);
            }
            catch
            {
                _running--;
                if (_tracked.TryGetValue(request.Key, out Slot current) && current.Generation == request.Generation)
                    _tracked.Remove(request.Key);
                throw;
            }
        }
    }

    void Build(TileWorldBuildRequest<TInput> request, bool countsWorker)
    {
        TOutput payload = default!;
        Exception? error = null;
        try { payload = _build(request); }
        catch (Exception ex) { error = ex; }
        _completed.Enqueue(new Completion(request.Key, request.Generation, payload, error, countsWorker));
    }

    void CollectCompletedCore(bool reportFailures)
    {
        while (_completed.TryDequeue(out Completion completion))
        {
            if (completion.CountsWorker) _running--;
            if (!_tracked.TryGetValue(completion.Key, out Slot slot) ||
                slot.Generation != completion.Generation || slot.State != WorkState.Running)
                continue;
            if (completion.Error is not null)
            {
                _tracked.Remove(completion.Key);
                if (reportFailures)
                    throw new InvalidOperationException(
                        $"TileWorld {completion.Key.Kind} build failed for {completion.Key.Region} plane {completion.Key.Plane}.",
                        completion.Error);
                continue;
            }
            _tracked[completion.Key] = slot with { State = WorkState.Ready };
            _ready[completion.Key] = new TileWorldBuildResult<TOutput>(
                completion.Key, completion.Generation, completion.Payload);
        }
    }

    int PendingCurrentCount()
    {
        int count = 0;
        foreach (Slot slot in _tracked.Values)
            if (slot.State == WorkState.Pending) count++;
        return count;
    }

    static int NearestFirst(TileWorldBuildResult<TOutput> a, TileWorldBuildResult<TOutput> b, RegionCoord focus)
    {
        int ad = Distance(a.Key.Region, focus);
        int bd = Distance(b.Key.Region, focus);
        if (ad != bd) return ad.CompareTo(bd);
        if (a.Key.Region.Rz != b.Key.Region.Rz) return a.Key.Region.Rz.CompareTo(b.Key.Region.Rz);
        if (a.Key.Region.Rx != b.Key.Region.Rx) return a.Key.Region.Rx.CompareTo(b.Key.Region.Rx);
        if (a.Key.Plane != b.Key.Plane) return a.Key.Plane.CompareTo(b.Key.Plane);
        if (a.Key.Kind != b.Key.Kind) return a.Key.Kind.CompareTo(b.Key.Kind);
        return string.CompareOrdinal(a.Key.LayerId, b.Key.LayerId);
    }

    static int Distance(RegionCoord region, RegionCoord focus) =>
        Math.Max(Math.Abs(region.Rx - focus.Rx), Math.Abs(region.Rz - focus.Rz));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pending.Clear();
        _tracked.Clear();
        _ready.Clear();
        _dispatcher.Drain();
        while (_completed.TryDequeue(out Completion completion))
            if (completion.CountsWorker) _running--;
        while (_completed.TryDequeue(out _)) { }
    }
}
