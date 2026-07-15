using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace KhaozEngine.Terrain
{
    /// <summary>A completed background chunk build, ready for the frame thread to turn into GPU buffers. Carries the
    /// coord, the LOD it was built at, and the opaque CPU payload the sink produced (mesh + scatter). The generation
    /// token is internal bookkeeping (last-request-wins).</summary>
    public readonly struct ChunkBuild<T>
    {
        /// <summary>The chunk this build is for.</summary>
        public ChunkCoord Coord { get; }
        /// <summary>The LOD tier the payload was built at.</summary>
        public int Lod { get; }
        /// <summary>The sink's opaque CPU payload (hand back to its apply step on the frame thread).</summary>
        public T Payload { get; }
        internal long Generation { get; }

        internal ChunkBuild(ChunkCoord coord, int lod, T payload, long generation)
        {
            Coord = coord; Lod = lod; Payload = payload; Generation = generation;
        }
    }

    /// <summary>Thrown on the frame thread when a background chunk build body faulted. The pipeline records the fault
    /// on the completion and re-throws it here (during <see cref="ChunkBuildScheduler{T}.Pump"/> /
    /// <see cref="ChunkBuildScheduler{T}.Flush"/>) so a build bug surfaces deterministically on the frame thread
    /// instead of silently leaving the chunk stuck in flight.</summary>
    public sealed class ChunkBuildException : Exception
    {
        /// <summary>The chunk whose build faulted.</summary>
        public ChunkCoord Coord { get; }
        /// <summary>The LOD the faulted build targeted.</summary>
        public int Lod { get; }

        internal ChunkBuildException(ChunkCoord coord, int lod, Exception inner)
            : base($"Terrain chunk build failed for {coord} at LOD {lod}.", inner)
        {
            Coord = coord; Lod = lod;
        }
    }

    /// <summary>The GPU-free heart of async terrain streaming: it owns the per-chunk generation tokens, dispatches
    /// each CPU build onto an <see cref="IChunkBuildDispatcher"/>, collects the finished builds, and drops the ones
    /// that were superseded (a newer re-LOD of the same chunk) or cancelled (the chunk left the ring) before they can
    /// be applied. All of that is pure bookkeeping over <see cref="ChunkCoord"/> keys with no device, so it is fully
    /// headless-testable: feed it a fake build function and a manual dispatcher and drive load / unload / re-LOD churn
    /// with controlled completion order.
    /// <para>Threading contract: every method here is called from the frame thread. The only cross-thread handoff is
    /// the concurrent completion queue a build body enqueues into when it finishes on a worker thread, which
    /// <see cref="Pump"/> drains back onto the frame thread. <typeparamref name="T"/> is the sink's opaque
    /// CPU payload (mesh + scatter for the production sink) and is never touched here beyond being carried through.</para></summary>
    public sealed class ChunkBuildScheduler<T> : IDisposable
    {
        readonly Func<ChunkCoord, int, T> _build;
        readonly IChunkBuildDispatcher _dispatcher;

        // Frame-thread-only bookkeeping. _current tracks every chunk with an outstanding or ready build (last request
        // wins via Generation). _ready holds finished-and-still-current builds waiting for the apply budget.
        readonly Dictionary<ChunkCoord, Slot> _current = new();
        readonly Dictionary<ChunkCoord, ChunkBuild<T>> _ready = new();

        // Worker-thread -> frame-thread handoff. A build body enqueues exactly one completion when it finishes.
        readonly ConcurrentQueue<Completion> _done = new();

        long _nextGen = 1;
        bool _disposed;

        struct Slot { public long Gen; public int Lod; public bool Ready; }

        readonly struct Completion
        {
            public readonly ChunkCoord Coord;
            public readonly int Lod;
            public readonly long Gen;
            public readonly T Payload;
            public readonly Exception? Error;
            public Completion(ChunkCoord coord, int lod, long gen, T payload, Exception? error)
            { Coord = coord; Lod = lod; Gen = gen; Payload = payload; Error = error; }
        }

        /// <summary>Build the scheduler over <paramref name="build"/> (the sink's CPU build step, run on a worker
        /// thread). <paramref name="dispatcher"/> chooses how builds run, or null for the default <see cref="TaskChunkBuildDispatcher"/>
        /// (the thread pool).</summary>
        public ChunkBuildScheduler(Func<ChunkCoord, int, T> build, IChunkBuildDispatcher? dispatcher = null)
        {
            _build = build ?? throw new ArgumentNullException(nameof(build));
            _dispatcher = dispatcher ?? new TaskChunkBuildDispatcher();
        }

        /// <summary>Chunks with a build outstanding but not yet moved to the ready set (running, or finished but not yet
        /// <see cref="Pump"/>ed). Test-facing.</summary>
        public int InFlightCount
        {
            get { int n = 0; foreach (Slot s in _current.Values) if (!s.Ready) n++; return n; }
        }

        /// <summary>Finished builds that are still current and waiting for the apply budget. Test-facing.</summary>
        public int ReadyCount => _ready.Count;

        /// <summary>Every chunk the scheduler is tracking (outstanding or ready). The streamer sweeps this to cancel
        /// builds for chunks that fell outside the unload radius before they were applied.</summary>
        public IReadOnlyCollection<ChunkCoord> Tracked => _current.Keys;

        /// <summary>The LOD of the most recent request for <paramref name="coord"/> (whether still running or already
        /// ready), or -1 if the scheduler is not tracking it. Lets the streamer avoid re-requesting a build it already
        /// asked for at the same LOD.</summary>
        public int RequestedLod(ChunkCoord coord) => _current.TryGetValue(coord, out Slot s) ? s.Lod : -1;

        /// <summary>(Re)request a build for <paramref name="coord"/> at <paramref name="lod"/>. Bumps the chunk's
        /// generation, so any earlier build for it still running or sitting ready is now stale and will be discarded
        /// instead of applied (last request wins). Dispatches the CPU build onto the dispatcher.</summary>
        public void Request(ChunkCoord coord, int lod)
        {
            long gen = _nextGen++;
            _current[coord] = new Slot { Gen = gen, Lod = lod, Ready = false };
            _ready.Remove(coord);   // an earlier ready build for this coord is superseded by the newer request

            Func<ChunkCoord, int, T> build = _build;
            ConcurrentQueue<Completion> done = _done;
            _dispatcher.Schedule(() =>
            {
                T payload = default!;
                Exception? error = null;
                try { payload = build(coord, lod); }
                catch (Exception e) { error = e; }
                done.Enqueue(new Completion(coord, lod, gen, payload, error));
            });
        }

        /// <summary>Discard any outstanding or ready build for <paramref name="coord"/> (the chunk left the ring). The
        /// running body, if any, still finishes on its worker thread, but its result is dropped at <see cref="Pump"/>
        /// because the generation no longer matches. Idempotent. A later <see cref="Request"/> for the same coord
        /// builds again (a fresh generation), so a cancelled chunk that re-enters the ring is not stuck.</summary>
        public void Cancel(ChunkCoord coord)
        {
            _current.Remove(coord);
            _ready.Remove(coord);
        }

        /// <summary>Move every finished build from the worker-thread queue into the ready set, dropping stale ones
        /// (superseded or cancelled). Call once per frame before <see cref="TakeReady"/>. Re-throws a
        /// <see cref="ChunkBuildException"/> if a still-current build faulted.</summary>
        public void Pump()
        {
            while (_done.TryDequeue(out Completion c))
            {
                if (!_current.TryGetValue(c.Coord, out Slot s) || s.Gen != c.Gen)
                    continue;   // stale: cancelled or superseded by a newer request. Drop the payload.

                if (c.Error is not null)
                {
                    _current.Remove(c.Coord);   // clear so a later request for this coord can retry
                    throw new ChunkBuildException(c.Coord, c.Lod, c.Error);
                }

                s.Ready = true;
                _current[c.Coord] = s;
                _ready[c.Coord] = new ChunkBuild<T>(c.Coord, c.Lod, c.Payload, c.Gen);
            }
        }

        /// <summary>Take up to <paramref name="max"/> ready builds, ordered by <paramref name="nearestFirst"/>, and
        /// remove them from the scheduler (the caller is applying them this frame). Returns them so the caller can
        /// create the GPU buffers on the frame thread. Fewer than <paramref name="max"/> when fewer are ready.</summary>
        public IReadOnlyList<ChunkBuild<T>> TakeReady(int max, Comparison<ChunkCoord> nearestFirst)
        {
            ArgumentNullException.ThrowIfNull(nearestFirst);
            if (max <= 0 || _ready.Count == 0) return Array.Empty<ChunkBuild<T>>();

            var coords = new List<ChunkCoord>(_ready.Keys);
            coords.Sort(nearestFirst);
            int take = Math.Min(max, coords.Count);
            var taken = new List<ChunkBuild<T>>(take);
            for (int i = 0; i < take; i++)
            {
                ChunkCoord c = coords[i];
                taken.Add(_ready[c]);
                _ready.Remove(c);
                _current.Remove(c);   // applied this frame: the scheduler is done tracking it
            }
            return taken;
        }

        /// <summary>Block until every outstanding build has finished running, then <see cref="Pump"/> so all their
        /// results are ready. Turns the async pipeline into a synchronous one for this call (deterministic drain):
        /// used to prime the first ring and by editors/tools that want blocking loads. Does not apply anything - the
        /// caller then <see cref="TakeReady"/>s the ready set.</summary>
        public void Flush()
        {
            _dispatcher.Drain();
            Pump();
        }

        /// <summary>Discard all tracking and drain outstanding builds without applying them (for teardown / rebuild).
        /// Waits for running bodies so no worker thread is still executing when the caller frees state around it.</summary>
        public void Reset()
        {
            _dispatcher.Drain();
            while (_done.TryDequeue(out _)) { }
            _current.Clear();
            _ready.Clear();
        }

        /// <summary>Reset and mark disposed. Idempotent.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Reset();
        }
    }
}
