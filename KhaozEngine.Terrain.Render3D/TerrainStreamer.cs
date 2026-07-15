using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Terrain
{
    /// <summary>Keeps the world loaded in a ring around the player. Each <see cref="Update"/>: unloads chunks beyond
    /// <c>UnloadRadius</c> (immediate), enqueues loads for chunks inside the <c>LoadRadius</c> disk that are not yet
    /// loaded and re-LODs for loaded chunks whose <see cref="TerrainLod.PickLod"/> tier changed, then brings at most
    /// <c>MaxLoadsPerFrame</c> of those into the scene (nearest first) through the injected <see cref="IChunkSink"/>.
    /// Pure bookkeeping (no GPU, no field), so it is fully headless-testable, and the sink does the real work. Load/unload
    /// use Euclidean chunk-distance; the hysteresis band (UnloadRadius &gt; LoadRadius) stops churn at boundaries.
    /// <para><b>Async build (default).</b> When <see cref="StreamerConfig.Async"/> is set and the sink is an
    /// <see cref="IAsyncChunkSink"/> (the production <see cref="Scene3DChunkSink"/> is), the streamer builds each
    /// chunk's mesh on a background thread and only pays the GPU upload on the frame thread, so a streamed chunk is no
    /// longer a full CPU-mesh-build hitch. <c>Update</c> then (a) REQUESTS builds for the pending loads/re-LODs
    /// (unbudgeted, off the frame thread) and (b) APPLIES up to <c>MaxLoadsPerFrame</c> COMPLETED builds (GPU upload +
    /// handle swap). Correctness under churn is handled by a per-chunk generation token in
    /// <see cref="ChunkBuildScheduler{T}"/>: a chunk that leaves the ring mid-build is cancelled and its result
    /// discarded, and a re-LOD supersedes an earlier in-flight build of the same chunk (last request wins). Call
    /// <see cref="FlushPendingBuilds"/> to force all outstanding builds to complete + apply synchronously
    /// (deterministic drain, for priming or blocking loads). Use <see cref="StreamerConfig.Synchronous"/> to opt out
    /// of async entirely (build+upload happen inline, the pre-async behaviour). A sink that is not an
    /// <see cref="IAsyncChunkSink"/> always runs synchronously regardless of the config flag.</para>
    /// <para>Teardown: <see cref="UnloadAll"/> flushes the loaded ring through the sink (frees every loaded chunk) and
    /// discards any outstanding builds, which a streaming rebuild (level change / world reload / teleport) must call
    /// first so the previous ring's GPU meshes are freed instead of leaked. <see cref="Dispose"/> does that and then
    /// disposes the sink if it is <see cref="IDisposable"/> - i.e. it assumes it owns the sink it was given (turn-key
    /// teardown). To rebuild streaming while REUSING the same sink, call <see cref="UnloadAll"/> and hand the same sink
    /// to the new streamer. Call <see cref="Dispose"/> only when the sink (and its GPU resources) should go too.</para></summary>
    public sealed class TerrainStreamer : IDisposable
    {
        readonly StreamerConfig _config;
        readonly IChunkSink _sink;
        readonly Dictionary<ChunkCoord, Entry> _loaded = new();

        // Set only when async build is active (config asked for it AND the sink supports the split seam). Null => the
        // streamer runs the synchronous build+upload-inline path.
        readonly bool _async;
        readonly IAsyncChunkSink? _asyncSink;
        readonly ChunkBuildScheduler<object>? _scheduler;

        bool _disposed;

        sealed class Entry { public object Handle = null!; public int Lod; }

        /// <summary>Build the streamer over a config and sink. <paramref name="dispatcher"/> chooses how background
        /// builds run when async is active (null uses the thread pool). Tests inject a manual dispatcher to control
        /// completion order. It is ignored in synchronous mode.</summary>
        public TerrainStreamer(StreamerConfig config, IChunkSink sink, IChunkBuildDispatcher? dispatcher = null)
        {
            _config = config;
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            if (config.Async && sink is IAsyncChunkSink asyncSink)
            {
                _async = true;
                _asyncSink = asyncSink;
                _scheduler = new ChunkBuildScheduler<object>(asyncSink.BuildCpu, dispatcher);
            }
        }

        /// <summary>The chunks currently loaded (applied, with a live GPU mesh) after this frame's ops. In async mode a
        /// chunk whose build is still in flight is NOT counted here until it is applied.</summary>
        public IReadOnlyCollection<ChunkCoord> Loaded => _loaded.Keys;

        /// <summary>The LOD tier a loaded chunk is currently built at, or -1 if not loaded.</summary>
        public int LodOf(ChunkCoord coord) => _loaded.TryGetValue(coord, out Entry? e) ? e.Lod : -1;

        /// <summary>Unload every currently-loaded chunk through the sink and clear the ring (after this <see cref="Loaded"/>
        /// is empty), discarding any outstanding async builds. Call before a streaming rebuild that keeps the same
        /// sink/scene alive so the previous ring's GPU meshes are freed rather than leaked. Does NOT dispose the sink
        /// (see <see cref="Dispose"/> for that).</summary>
        public void UnloadAll()
        {
            foreach (KeyValuePair<ChunkCoord, Entry> kv in _loaded)
                _sink.Unload(kv.Key, kv.Value.Handle);
            _loaded.Clear();
            _scheduler?.Reset();
        }

        /// <summary>Flush the loaded ring (<see cref="UnloadAll"/>) then dispose the sink if it is
        /// <see cref="IDisposable"/>. Idempotent. Use this for full teardown; use <see cref="UnloadAll"/> to rebuild
        /// while keeping the sink.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            UnloadAll();
            _scheduler?.Dispose();
            (_sink as IDisposable)?.Dispose();
        }

        public void Update(Vector3 playerPos, float dt)
        {
            if (_async) UpdateAsync(playerPos);
            else UpdateSync(playerPos);
        }

        /// <summary>Deterministically load the full ring around <paramref name="playerPos"/> right now (a loading
        /// moment, not a frame): requests + applies every chunk in range, blocking on any async builds, ignoring the
        /// per-frame apply budget. Use at level load to fill the first ring before the first frame. Works in both
        /// async and synchronous modes.</summary>
        public void PrimeAround(Vector3 playerPos)
        {
            int before = -1;
            while (_loaded.Count != before)
            {
                before = _loaded.Count;
                Update(playerPos, 0f);
                FlushPendingBuilds();
            }
        }

        /// <summary>Force every outstanding async build to complete and apply it now (GPU upload on this thread),
        /// ignoring the per-frame budget. Turns the async streamer into a blocking load for this call: used by
        /// <see cref="PrimeAround"/> and by editors/tools that want deterministic loads. A no-op in synchronous mode
        /// (builds are already applied inline). Call from the frame thread only (it touches the GPU device).</summary>
        public void FlushPendingBuilds()
        {
            if (_scheduler is null) return;
            _scheduler.Flush();
            ApplyBuilds(_scheduler.TakeReady(int.MaxValue, static (_, _) => 0));
        }

        // --- Synchronous path (pre-async behaviour: build + upload happen inline, budget caps build ops) ------------
        void UpdateSync(Vector3 playerPos)
        {
            float cs = _config.ChunkSize;
            ChunkCoord pc = ChunkGrid.CoordOf(playerPos.X, playerPos.Z, cs);

            // 1. Unload everything past the hysteresis radius (immediate, unbudgeted).
            float unloadSq = _config.UnloadRadius * (float)_config.UnloadRadius;
            if (_loaded.Count > 0)
            {
                // Snapshot the far keys so we can mutate the dictionary while iterating.
                var far = new List<ChunkCoord>();
                foreach (KeyValuePair<ChunkCoord, Entry> kv in _loaded)
                {
                    int dx = kv.Key.X - pc.X, dz = kv.Key.Z - pc.Z;
                    if (dx * dx + dz * dz > unloadSq) far.Add(kv.Key);
                }
                foreach (ChunkCoord c in far)
                {
                    _sink.Unload(c, _loaded[c].Handle);
                    _loaded.Remove(c);
                }
            }

            // 2. Gather pending load + re-LOD ops over the load disk, each with a metre distance for nearest-first.
            int r = _config.LoadRadius;
            float loadSq = r * (float)r;
            var pending = new List<Pending>();
            for (int dz = -r; dz <= r; dz++)
            for (int dx = -r; dx <= r; dx++)
            {
                if (dx * dx + dz * dz > loadSq) continue;
                var c = new ChunkCoord(pc.X + dx, pc.Z + dz);

                Vector2 center = ChunkGrid.CenterOf(c, cs);
                float mdx = center.X - playerPos.X, mdz = center.Y - playerPos.Z;
                float metreDist = MathF.Sqrt(mdx * mdx + mdz * mdz);
                int lod = TerrainLod.PickLod(metreDist);

                if (!_loaded.TryGetValue(c, out Entry? e))
                    pending.Add(new Pending(c, lod, metreDist, isLoad: true));
                else if (e.Lod != lod)
                    pending.Add(new Pending(c, lod, metreDist, isLoad: false));
            }

            // 3. Process nearest-first, capped at MaxLoadsPerFrame.
            pending.Sort(static (a, b) => a.Dist.CompareTo(b.Dist));
            int budget = _config.MaxLoadsPerFrame;
            for (int i = 0; i < pending.Count && i < budget; i++)
            {
                Pending p = pending[i];
                if (p.IsLoad)
                {
                    object handle = _sink.Load(p.Coord, p.Lod);
                    _loaded[p.Coord] = new Entry { Handle = handle, Lod = p.Lod };
                }
                else
                {
                    Entry e = _loaded[p.Coord];
                    _sink.ReLod(p.Coord, e.Handle, p.Lod);
                    e.Lod = p.Lod;
                }
            }
        }

        // --- Async path (background CPU build, frame thread requests + applies within budget) ------------------------
        void UpdateAsync(Vector3 playerPos)
        {
            ChunkBuildScheduler<object> sched = _scheduler!;
            float cs = _config.ChunkSize;
            ChunkCoord pc = ChunkGrid.CoordOf(playerPos.X, playerPos.Z, cs);
            float unloadSq = _config.UnloadRadius * (float)_config.UnloadRadius;

            // 1a. Cancel in-flight/ready builds for chunks now beyond the unload radius. These were requested but never
            //     applied, so cancelling drops their result (invariant: unloaded-while-building -> discarded, no leak).
            if (sched.Tracked.Count > 0)
            {
                var cancel = new List<ChunkCoord>();
                foreach (ChunkCoord c in sched.Tracked)
                {
                    int dx = c.X - pc.X, dz = c.Z - pc.Z;
                    if (dx * dx + dz * dz > unloadSq) cancel.Add(c);
                }
                foreach (ChunkCoord c in cancel) sched.Cancel(c);
            }

            // 1b. Unload APPLIED chunks past the hysteresis radius (immediate). Cancel any in-flight re-LOD for them.
            if (_loaded.Count > 0)
            {
                var far = new List<ChunkCoord>();
                foreach (KeyValuePair<ChunkCoord, Entry> kv in _loaded)
                {
                    int dx = kv.Key.X - pc.X, dz = kv.Key.Z - pc.Z;
                    if (dx * dx + dz * dz > unloadSq) far.Add(kv.Key);
                }
                foreach (ChunkCoord c in far)
                {
                    _sink.Unload(c, _loaded[c].Handle);
                    _loaded.Remove(c);
                    sched.Cancel(c);
                }
            }

            // 2. Request builds over the load disk (UNBUDGETED - the build runs off the frame thread). Fresh load for
            //    unloaded chunks. Re-LOD when the tier changed. The scheduler's last-request-wins drops stale builds.
            int r = _config.LoadRadius;
            float loadSq = r * (float)r;
            for (int dz = -r; dz <= r; dz++)
            for (int dx = -r; dx <= r; dx++)
            {
                if (dx * dx + dz * dz > loadSq) continue;
                var c = new ChunkCoord(pc.X + dx, pc.Z + dz);

                Vector2 center = ChunkGrid.CenterOf(c, cs);
                float mdx = center.X - playerPos.X, mdz = center.Y - playerPos.Z;
                int lod = TerrainLod.PickLod(MathF.Sqrt(mdx * mdx + mdz * mdz));
                int reqLod = sched.RequestedLod(c);

                if (_loaded.TryGetValue(c, out Entry? e))
                {
                    if (e.Lod != lod)
                    {
                        if (reqLod != lod) sched.Request(c, lod);   // re-LOD (supersede a different in-flight re-LOD)
                    }
                    else if (reqLod != -1)
                    {
                        sched.Cancel(c);   // tier returned to the applied LOD, drop the now-stale in-flight re-LOD
                    }
                }
                else if (reqLod != lod)
                {
                    sched.Request(c, lod);   // fresh load, or re-target an in-flight load whose tier changed
                }
            }

            // 3. Apply completed builds, nearest-first, capped at MaxLoadsPerFrame (the GPU upload is what we budget).
            sched.Pump();
            ApplyBuilds(sched.TakeReady(_config.MaxLoadsPerFrame, NearestFirst(playerPos, cs)));
        }

        void ApplyBuilds(IReadOnlyList<ChunkBuild<object>> builds)
        {
            for (int i = 0; i < builds.Count; i++)
            {
                ChunkBuild<object> rb = builds[i];
                object? existing = _loaded.TryGetValue(rb.Coord, out Entry? e) ? e.Handle : null;
                object handle = _asyncSink!.Apply(rb.Coord, rb.Lod, rb.Payload, existing);
                _loaded[rb.Coord] = new Entry { Handle = handle, Lod = rb.Lod };
            }
        }

        static Comparison<ChunkCoord> NearestFirst(Vector3 playerPos, float cs) => (a, b) =>
        {
            Vector2 ca = ChunkGrid.CenterOf(a, cs), cb = ChunkGrid.CenterOf(b, cs);
            float da = Dist2(ca, playerPos), db = Dist2(cb, playerPos);
            return da.CompareTo(db);
        };

        static float Dist2(Vector2 center, Vector3 p)
        {
            float dx = center.X - p.X, dz = center.Y - p.Z;
            return dx * dx + dz * dz;
        }

        readonly struct Pending
        {
            public readonly ChunkCoord Coord;
            public readonly int Lod;
            public readonly float Dist;
            public readonly bool IsLoad;
            public Pending(ChunkCoord coord, int lod, float dist, bool isLoad)
            { Coord = coord; Lod = lod; Dist = dist; IsLoad = isLoad; }
        }
    }
}
