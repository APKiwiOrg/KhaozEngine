using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Terrain
{
    /// <summary>Keeps the world loaded in a ring around the player. Each <see cref="Update"/>: unloads chunks beyond
    /// <c>UnloadRadius</c> (farthest first, up to <c>MaxUnloadsPerFrame</c>), enqueues loads for chunks inside the
    /// <c>LoadRadius</c> disk that are not yet
    /// loaded and re-LODs for loaded chunks whose tier changed (picked with the
    /// <see cref="StreamerConfig.LodHysteresis"/> dead zone, so a chunk parked on a boundary does not re-tier on
    /// every step), then brings at most
    /// <c>MaxLoadsPerFrame</c> of those into the scene (nearest first) through the injected <see cref="IChunkSink"/>.
    /// Pure bookkeeping (no GPU, no field), so it is fully headless-testable, and the sink does the real work. Load/unload
    /// use Euclidean chunk-distance; the hysteresis band (UnloadRadius &gt; LoadRadius) stops churn at boundaries.
    /// <para><b>Async build (default).</b> When <see cref="StreamerConfig.Async"/> is set and the sink is an
    /// <see cref="IAsyncChunkSink"/> (the production <c>Scene3DChunkSink</c> is), the streamer builds each
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
        readonly TerrainLodConfig _lodConfig;
        readonly Dictionary<ChunkCoord, Entry> _loaded = new();

        // Set only when async build is active (config asked for it AND the sink supports the split seam). Null => the
        // streamer runs the synchronous build+upload-inline path.
        readonly bool _async;
        readonly IAsyncChunkSink? _asyncSink;
        readonly ChunkBuildScheduler<object>? _scheduler;

        // NearestFirst comparison state (issue #374): UpdateAsync used to build a fresh Comparison<ChunkCoord>
        // closure over playerPos/cs on every call (once per frame on the streaming path). The comparison is now
        // bound ONCE, at construction, to an instance method that reads these two fields instead. UpdateAsync just
        // restamps them right before the single-threaded TakeReady call that consumes them.
        Vector3 _nearestFirstPlayerPos;
        float _nearestFirstChunkSize;
        readonly Comparison<ChunkCoord> _nearestFirst;

        // Contained build failures (issue #402). _attempts counts consecutive failed builds per chunk and is cleared
        // the moment one applies, so only a REPEATED failure counts toward the cap. _abandoned is the set that hit the
        // cap: those are skipped by the ring scan, which is what stops a permanently poisoned chunk from being rebuilt
        // (and re-logged) every single frame for the rest of the session.
        readonly Dictionary<ChunkCoord, int> _attempts = new();
        readonly HashSet<ChunkCoord> _abandoned = new();
        int _failedBuilds;
        ChunkBuildException? _lastBuildFault;   // kept so a total priming failure can report the real cause

        // Per-reason build counters (see StreamerBuildReasons). Every one of these sites is on the frame thread, so
        // plain adds are enough here, unlike the sink's merge counters, which are bumped from the build threads.
        long _freshLoads, _tierChanges, _ringChanges, _invalidates, _anchorRecentres;
        // Whether this streamer has ever anchored. Survives UnloadAll's anchor reset, so the first anchor of a
        // session is not counted as a re-centre while a post-teleport re-anchor onto a different chunk is.
        bool _anchoredOnce;

        bool _disposed;

        // Which ring-scan site asked for a build, so the counter is picked where the decision is made rather than
        // re-derived at the request call.
        enum BuildReason { FreshLoad, TierChange, RingChange }

        sealed class Entry { public object Handle = null!; public int Lod; public ChunkRing Ring; }

        /// <summary>Build the streamer over a config and sink. <paramref name="dispatcher"/> chooses how background
        /// builds run when async is active (null uses the thread pool). Tests inject a manual dispatcher to control
        /// completion order. It is ignored in synchronous mode. Throws if the hysteresis band is degenerate
        /// (<see cref="StreamerConfig.UnloadRadius"/> must exceed the outer load radius).</summary>
        public TerrainStreamer(StreamerConfig config, IChunkSink sink, IChunkBuildDispatcher? dispatcher = null)
        {
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            _nearestFirst = NearestFirstCompare;
            if (config.UnloadRadius <= config.OuterRadius)
                throw new ArgumentException(
                    $"UnloadRadius ({config.UnloadRadius}) must exceed the outer load radius ({config.OuterRadius}) so the hysteresis band stops churn.",
                    nameof(config));
            _config = config;
            _lodConfig = config.ResolvedLodConfig;
            if (config.Async && sink is IAsyncChunkSink asyncSink)
            {
                _async = true;
                _asyncSink = asyncSink;
                _scheduler = new ChunkBuildScheduler<object>(asyncSink.BuildCpu, dispatcher)
                {
                    // Contain a faulted background build here instead of letting it out of Update into the game's
                    // frame loop, where it terminates the process (issue #402).
                    BuildFailed = OnBuildFailed,
                };
            }
        }

        /// <summary>A background chunk build threw. Log it, count it against this chunk's attempt budget, and either
        /// leave it to be re-requested by the next ring scan or abandon it for good.
        /// <para>This runs on the frame thread (the scheduler pumps completions back before invoking us), so touching
        /// the streamer's own bookkeeping here is safe.</para></summary>
        void OnBuildFailed(ChunkBuildException fault)
        {
            _failedBuilds++;
            _lastBuildFault = fault;
            int attempts = _attempts.TryGetValue(fault.Coord, out int n) ? n + 1 : 1;
            _attempts[fault.Coord] = attempts;

            int cap = _config.MaxChunkBuildAttempts;
            if (attempts >= cap)
            {
                _attempts.Remove(fault.Coord);
                _abandoned.Add(fault.Coord);
                Log.For<TerrainStreamer>().Error(
                    $"Terrain chunk {fault.Coord} failed to build {attempts} time(s) at LOD {fault.Lod}; abandoning it. " +
                    "That chunk stays absent for this session and will not be requested again.", fault);
                return;
            }

            Log.For<TerrainStreamer>().Error(
                $"Terrain chunk {fault.Coord} failed to build at LOD {fault.Lod} (attempt {attempts} of {cap}); retrying on a later pass.",
                fault);
        }

        /// <summary>Optional build gate. Null (the default) preserves today's behaviour byte for byte: every chunk
        /// in the ring is eligible. When set, a chunk the gate refuses is DEFERRED - not requested, not marked
        /// loaded, and reconsidered on the next <see cref="Update"/> - which is how a streamer composed with an
        /// asynchronous document-residency layer avoids building a chunk whose authored data has not arrived yet.
        /// See <see cref="IChunkBuildGate"/>.
        /// <para>The gate governs the RING SCAN only (fresh loads and re-LOD / ring changes). It deliberately does
        /// not gate <see cref="Invalidate(RectArea)"/>, which is the explicit "this data just changed, rebuild it"
        /// call a residency layer makes ON ARRIVAL: gating that would refuse the very rebuild the arrival exists to
        /// trigger. Unloads are never gated either, so a deferred chunk can still leave the ring.</para></summary>
        public IChunkBuildGate? BuildGate { get; set; }

        /// <summary>Metres a player must travel INTO a new chunk before the ring re-centres on it.
        /// <para>The tier pick has had a dead zone since it existed (<see cref="StreamerConfig.LodHysteresis"/>),
        /// because a chunk parked on a tier boundary would otherwise re-tier on every step. The RING had none, and
        /// it is the same failure one level up. Everything the ring scan decides per chunk (its residency ring, and
        /// whether it is inside the load disk at all) is a function of the player's CHUNK COORD, so a player standing
        /// on a chunk boundary with any jitter at all - a network reconciliation of a tenth of a millimetre is
        /// enough - flips that coord every frame. Each flip shifts the whole disk by one chunk, which moves every
        /// chunk's integer distance, which changes the ring for the band straddling
        /// <see cref="StreamerConfig.LoadRadius"/> and the membership of the band straddling
        /// <see cref="StreamerConfig.OuterRadius"/>. The result is a continuous rebuild storm on a player who is not
        /// moving: measured at about nine builds and four applies per frame on a radius-5 disk, most of the builds
        /// superseded before they could be applied (which is what shows up downstream as HLOD merges built and then
        /// discarded).</para>
        /// <para>Two metres is the smallest margin that clearly beats reconciliation jitter and animation-driven
        /// sub-centimetre motion while staying far under a chunk (60 m typically), so a walking player re-centres
        /// essentially where it always did.</para></summary>
        public const float ChunkAnchorHysteresis = 2f;

        // The chunk the ring is currently centred on. Not simply CoordOf(playerPos) - see ChunkAnchorHysteresis.
        ChunkCoord _anchor;
        bool _hasAnchor;

        /// <summary>The chunk to centre this pass's ring on: the player's chunk, but only once they are
        /// <see cref="ChunkAnchorHysteresis"/> metres inside it. The first call anchors undamped, so a fresh
        /// streamer (and every <see cref="PrimeAround"/>) centres exactly where the player is.</summary>
        ChunkCoord AnchorChunk(Vector3 playerPos)
        {
            float cs = _config.ChunkSize;
            ChunkCoord c = ChunkGrid.CoordOf(playerPos.X, playerPos.Z, cs);
            if (!_hasAnchor) { Recentre(c); _hasAnchor = true; return c; }
            int dx = c.X - _anchor.X, dz = c.Z - _anchor.Z;
            if (dx == 0 && dz == 0) return _anchor;
            // A jump of more than one chunk is real movement (a teleport, a respawn, a warp), never boundary jitter,
            // so it re-centres immediately. Only a step to a NEIGHBOUR can be a flip across a boundary.
            if (dx < -1 || dx > 1 || dz < -1 || dz > 1) { Recentre(c); return c; }

            // Re-centre only when the player is clear of every edge of the chunk they are now in, on the axes that
            // actually changed. An axis that did not change is already clear by definition.
            Vector2 center = ChunkGrid.CenterOf(c, cs);
            float clear = cs * 0.5f - ChunkAnchorHysteresis;
            bool deepX = dx == 0 || MathF.Abs(playerPos.X - center.X) <= clear;
            bool deepZ = dz == 0 || MathF.Abs(playerPos.Z - center.Y) <= clear;
            if (deepX && deepZ) Recentre(c);
            return _anchor;
        }

        // Move the ring's centre chunk, counting the move unless it is this streamer's first anchor ever.
        void Recentre(ChunkCoord c)
        {
            if (_anchoredOnce && c != _anchor) _anchorRecentres++;
            _anchor = c;
            _anchoredOnce = true;
        }

        // One build request, attributed. Kept next to the counters rather than inlined at the three request sites so
        // the mapping from decision to counter is readable in one place.
        void CountBuild(BuildReason reason)
        {
            switch (reason)
            {
                case BuildReason.TierChange: _tierChanges++; break;
                case BuildReason.RingChange: _ringChanges++; break;
                default: _freshLoads++; break;
            }
        }

        // A loaded chunk whose tier or ring no longer matches the scan: the tier wins the attribution when both moved,
        // because a tier flip is the metre-distance signal and a ring flip is the integer-distance one.
        static BuildReason ReasonForRebuild(int appliedLod, int wantedLod) =>
            appliedLod != wantedLod ? BuildReason.TierChange : BuildReason.RingChange;

        /// <summary>The residency ring for a chunk at Euclidean chunk-distance-squared <paramref name="chunkDistSq"/>
        /// from the player's chunk: <see cref="ChunkRing.Gameplay"/> within <see cref="StreamerConfig.LoadRadius"/>,
        /// else <see cref="ChunkRing.Decor"/>.</summary>
        ChunkRing RingAt(int chunkDistSq) =>
            chunkDistSq <= _config.LoadRadius * _config.LoadRadius ? ChunkRing.Gameplay : ChunkRing.Decor;

        /// <summary>The chunks currently loaded (applied, with a live GPU mesh) after this frame's ops. In async mode a
        /// chunk whose build is still in flight is NOT counted here until it is applied.</summary>
        public IReadOnlyCollection<ChunkCoord> Loaded => _loaded.Keys;

        /// <summary>The LOD tier a loaded chunk is currently built at, or -1 if not loaded.</summary>
        public int LodOf(ChunkCoord coord) => _loaded.TryGetValue(coord, out Entry? e) ? e.Lod : -1;

        /// <summary>The residency ring a loaded chunk is currently built for, or null if not loaded. A decor chunk is
        /// render-only (no scatter or physics); a gameplay chunk is simulated.</summary>
        public ChunkRing? RingOf(ChunkCoord coord) => _loaded.TryGetValue(coord, out Entry? e) ? e.Ring : null;

        /// <summary>How many background chunk builds have faulted over this streamer's life, counting every retry
        /// separately. A boot step can report this after <see cref="PrimeAround"/> to say the world primed with
        /// failures rather than silently shipping a world with holes in it.</summary>
        public int FailedBuildCount => _failedBuilds;

        /// <summary>Cumulative per-reason build counters (see <see cref="StreamerBuildReasons"/>): what this
        /// streamer has asked the sink to build, split by WHY it asked. Read it the way
        /// <c>Scene3DChunkSink.MergeStats</c> is read, as a value snapshot, and difference two samples for a rate.
        /// Life-of-streamer totals: <see cref="UnloadAll"/> does not reset them.</summary>
        public StreamerBuildReasons BuildReasons =>
            new(_freshLoads, _tierChanges, _ringChanges, _invalidates, _anchorRecentres);

        /// <summary>Chunks whose builds failed <see cref="StreamerConfig.MaxChunkBuildAttempts"/> times and are no
        /// longer requested. These are the permanent holes in the world: everything else either loaded or is still
        /// being retried. Cleared by <see cref="UnloadAll"/>, so a world rebuild gives every chunk a fresh start.</summary>
        public IReadOnlyCollection<ChunkCoord> AbandonedChunks => _abandoned;

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
            // A rebuild re-centres wherever the next Update puts the player, undamped: there is no ring left for the
            // old anchor to be stale against.
            _hasAnchor = false;
            // A rebuild is a fresh start: the field, the document, or the kit may be different now, so a chunk that
            // could not build against the OLD state has earned another go. FailedBuildCount is a life-of-streamer
            // total and deliberately keeps counting.
            _attempts.Clear();
            _abandoned.Clear();
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

        public void Update(Vector3 playerPos, float dt) => UpdateOnce(playerPos);

        /// <summary>One <see cref="Update"/> pass, reporting whether it actually DID anything: freed a chunk, or
        /// applied a load / re-LOD. <see cref="PrimeAround"/> settles on that instead of on the resident count.</summary>
        bool UpdateOnce(Vector3 playerPos) => _async ? UpdateAsync(playerPos) : UpdateSync(playerPos);

        /// <summary>Deterministically load the full ring around <paramref name="playerPos"/> right now (a loading
        /// moment, not a frame): requests + applies every chunk in range, blocking on any async builds, ignoring the
        /// per-frame apply budget. Use at level load to fill the first ring before the first frame. Works in both
        /// async and synchronous modes.
        /// <para>It settles on WORK DONE, not on the resident count. Counting was right only while unloads were
        /// unbudgeted and all landed in the first pass: with <see cref="StreamerConfig.MaxUnloadsPerFrame"/> in play, a
        /// pass whose budgeted unloads happen to cancel out its loads leaves the count untouched while the ring still
        /// has holes in it and stale chunks resident, and a count-based loop stops right there.</para>
        /// <para>It terminates because the position is fixed, so every pass that does work makes progress that cannot
        /// be undone: an unload frees a chunk past <see cref="StreamerConfig.UnloadRadius"/>, which can never come back
        /// (the ctor forces that radius past the outer load radius, so the two regions are disjoint), and an apply
        /// fills one of the finitely many chunks in range that are missing or on the wrong tier, which stays filled (a
        /// chunk applied at the tier the hysteresis picked re-picks that same tier next pass). A load budget of 0 or
        /// less simply means no pass ever does work, so this returns after one.</para>
        /// <para><b>Isolated build failures do not fail the prime</b> (issue #402). A chunk whose background build
        /// throws is logged at ERROR and left out, and priming carries on with the rest: a world of 377 chunks missing
        /// one beats no world. Read <see cref="FailedBuildCount"/> / <see cref="AbandonedChunks"/> afterwards to report
        /// what was lost. A prime that loads NOTHING is still a real boot failure and throws, because at that point
        /// there is no world to walk into - the exception carries the last underlying build fault as its cause.</para></summary>
        public void PrimeAround(Vector3 playerPos)
        {
            int failedBefore = _failedBuilds;
            while (true)
            {
                // Both run every pass: in async mode the requests go out in the update and the applies land in the
                // flush, so either half on its own is an incomplete picture of the pass.
                bool updated = UpdateOnce(playerPos);
                bool flushed = FlushBuilds();
                if (!updated && !flushed) break;
            }

            int failed = _failedBuilds - failedBefore;
            if (failed > 0 && _loaded.Count == 0)
                throw new InvalidOperationException(
                    $"Terrain priming around {playerPos} loaded no chunks at all: every build attempt failed ({failed} in total). " +
                    "This is a world that cannot prime, not one chunk degrading.", _lastBuildFault);
        }

        /// <summary>Force every outstanding async build to complete and apply it now (GPU upload on this thread),
        /// ignoring the per-frame budget. Turns the async streamer into a blocking load for this call: used by
        /// <see cref="PrimeAround"/> and by editors/tools that want deterministic loads. A no-op in synchronous mode
        /// (builds are already applied inline). Call from the frame thread only (it touches the GPU device).</summary>
        public void FlushPendingBuilds() => FlushBuilds();

        /// <summary>The flush, reporting whether it applied anything (always false in synchronous mode, where there
        /// is nothing outstanding to flush).</summary>
        bool FlushBuilds()
        {
            if (_scheduler is null) return false;
            _scheduler.Flush();
            return ApplyBuilds(_scheduler.TakeReady(int.MaxValue, static (_, _) => 0)) > 0;
        }

        // --- Synchronous path (pre-async behaviour: build + upload happen inline, budget caps build ops) ------------
        bool UpdateSync(Vector3 playerPos)
        {
            float cs = _config.ChunkSize;
            ChunkCoord pc = AnchorChunk(playerPos);

            // 1. Unload chunks past the hysteresis radius, within this frame's budget.
            float unloadSq = _config.UnloadRadius * (float)_config.UnloadRadius;
            List<ChunkCoord> far = FarChunksToUnload(pc, unloadSq);
            foreach (ChunkCoord c in far)
            {
                _sink.Unload(c, _loaded[c].Handle);
                _loaded.Remove(c);
            }

            // 2. Gather pending load + re-LOD ops over the load disk (out to the OUTER radius, so decor chunks load
            //    too), each with a metre distance for nearest-first. A loaded chunk needs a rebuild when its tier OR
            //    its ring changed (a ring change adds/drops scatter + colliders even at the same tier).
            int r = _config.OuterRadius;
            float loadSq = r * (float)r;
            var pending = new List<Pending>();
            for (int dz = -r; dz <= r; dz++)
            for (int dx = -r; dx <= r; dx++)
            {
                int chunkDistSq = dx * dx + dz * dz;
                if (chunkDistSq > loadSq) continue;
                var c = new ChunkCoord(pc.X + dx, pc.Z + dz);
                if (BuildGate is { } gate && !gate.CanBuild(c)) continue;   // deferred: reconsidered next Update

                Vector2 center = ChunkGrid.CenterOf(c, cs);
                float mdx = center.X - playerPos.X, mdz = center.Y - playerPos.Z;
                float metreDist = MathF.Sqrt(mdx * mdx + mdz * mdz);
                ChunkRing ring = RingAt(chunkDistSq);
                // The applied tier is the hysteresis reference, so a chunk sitting on a boundary keeps its mesh
                // until the player really commits to the crossing. An unloaded chunk (-1) picks undamped.
                bool loaded = _loaded.TryGetValue(c, out Entry? e);
                int lod = _lodConfig.PickLod(metreDist, loaded ? e!.Lod : -1, _config.LodHysteresis);

                if (!loaded)
                    pending.Add(new Pending(c, lod, ring, metreDist, isLoad: true, BuildReason.FreshLoad));
                else if (e!.Lod != lod || e.Ring != ring)
                    pending.Add(new Pending(c, lod, ring, metreDist, isLoad: false, ReasonForRebuild(e.Lod, lod)));
            }

            // 3. Process nearest-first, capped at MaxLoadsPerFrame.
            pending.Sort(static (a, b) => a.Dist.CompareTo(b.Dist));
            int budget = _config.MaxLoadsPerFrame;
            int ops = 0;
            for (int i = 0; i < pending.Count && i < budget; i++)
            {
                Pending p = pending[i];
                if (p.IsLoad)
                {
                    object handle = _sink.Load(p.Coord, p.Lod, p.Ring);
                    _loaded[p.Coord] = new Entry { Handle = handle, Lod = p.Lod, Ring = p.Ring };
                }
                else
                {
                    Entry e = _loaded[p.Coord];
                    _sink.ReLod(p.Coord, e.Handle, p.Lod, p.Ring);
                    e.Lod = p.Lod;
                    e.Ring = p.Ring;
                }
                CountBuild(p.Reason);
                ops++;
            }
            return far.Count > 0 || ops > 0;
        }

        // --- Async path (background CPU build, frame thread requests + applies within budget) ------------------------
        bool UpdateAsync(Vector3 playerPos)
        {
            ChunkBuildScheduler<object> sched = _scheduler!;
            float cs = _config.ChunkSize;
            ChunkCoord pc = AnchorChunk(playerPos);
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

            // 1b. Unload APPLIED chunks past the hysteresis radius, within this frame's budget. Cancel any in-flight
            //     re-LOD for the ones that actually go (1a already cancelled every far chunk's build).
            List<ChunkCoord> far = FarChunksToUnload(pc, unloadSq);
            foreach (ChunkCoord c in far)
            {
                _sink.Unload(c, _loaded[c].Handle);
                _loaded.Remove(c);
                sched.Cancel(c);
            }

            // 2. Request builds over the load disk out to the OUTER radius (UNBUDGETED - the build runs off the frame
            //    thread), so decor chunks load too. Fresh load for unloaded chunks. Re-LOD when the tier OR the ring
            //    changed (a ring change adds/drops scatter + colliders). The scheduler's last-request-wins drops stale
            //    builds.
            int r = _config.OuterRadius;
            float loadSq = r * (float)r;
            for (int dz = -r; dz <= r; dz++)
            for (int dx = -r; dx <= r; dx++)
            {
                int chunkDistSq = dx * dx + dz * dz;
                if (chunkDistSq > loadSq) continue;
                var c = new ChunkCoord(pc.X + dx, pc.Z + dz);
                // Abandoned: its build threw its way through MaxChunkBuildAttempts. Never request it again, which is
                // what turns a permanently failing chunk into one hole instead of a per-frame rebuild + error (#402).
                if (_abandoned.Contains(c)) continue;
                if (BuildGate is { } gate && !gate.CanBuild(c)) continue;   // deferred: reconsidered next Update

                Vector2 center = ChunkGrid.CenterOf(c, cs);
                float mdx = center.X - playerPos.X, mdz = center.Y - playerPos.Z;
                ChunkRing ring = RingAt(chunkDistSq);
                // The applied tier is the hysteresis reference, so a chunk sitting on a boundary keeps its mesh
                // until the player really commits to the crossing. An unloaded chunk (-1) picks undamped.
                bool loaded = _loaded.TryGetValue(c, out Entry? e);
                int lod = _lodConfig.PickLod(MathF.Sqrt(mdx * mdx + mdz * mdz), loaded ? e!.Lod : -1, _config.LodHysteresis);
                int reqLod = sched.RequestedLod(c);
                // The in-flight request already targets exactly this (tier, ring). When untracked reqLod is -1, so
                // this is false and a fresh request goes out.
                bool requestMatches = reqLod == lod && sched.RequestedRing(c) == ring;

                if (loaded)
                {
                    if (e!.Lod != lod || e.Ring != ring)
                    {
                        if (!requestMatches)
                        {
                            sched.Request(c, lod, ring);   // re-LOD / ring change (supersede a stale one)
                            CountBuild(ReasonForRebuild(e.Lod, lod));
                        }
                    }
                    else if (reqLod != -1)
                    {
                        sched.Cancel(c);   // tier + ring returned to the applied state, drop the now-stale in-flight rebuild
                    }
                }
                else if (!requestMatches)
                {
                    sched.Request(c, lod, ring);   // fresh load, or re-target an in-flight load whose tier/ring changed
                    _freshLoads++;
                }
            }

            // 3. Apply completed builds, nearest-first, capped at MaxLoadsPerFrame (the GPU upload is what we budget).
            // Restamp the comparison state, then reuse the bound delegate (_nearestFirst) rather than closing over
            // playerPos/cs afresh: TakeReady's Sort call runs synchronously on this thread before either field could
            // change again.
            _nearestFirstPlayerPos = playerPos;
            _nearestFirstChunkSize = cs;
            sched.Pump();
            int applied = ApplyBuilds(sched.TakeReady(_config.MaxLoadsPerFrame, _nearestFirst));
            return far.Count > 0 || applied > 0;
        }

        /// <summary>Rebuild every currently loaded chunk intersecting <paramref name="area"/> in place, at its
        /// CURRENT LOD tier (no tier change), by re-issuing the sink's re-LOD rebuild. This is the partial
        /// invalidation seam for editors: an edit only pays for the loaded chunks it actually overlaps, not the
        /// whole ring. The rect maps to an inclusive chunk-coord range via floor(min/chunkSize)..floor(max/chunkSize),
        /// so a rect that merely touches a chunk's border still invalidates both neighbours. Chunks not currently
        /// loaded are left untouched: they pick up the new state the next time they load naturally. In async mode
        /// any in-flight builds are flushed first (<see cref="FlushPendingBuilds"/>) so a stale build cannot land
        /// after the invalidation and overwrite it.</summary>
        public void Invalidate(RectArea area)
        {
            FlushPendingBuilds();
            float cs = _config.ChunkSize;
            ChunkCoord min = ChunkGrid.CoordOf(area.MinX, area.MinZ, cs);
            ChunkCoord max = ChunkGrid.CoordOf(area.MaxX, area.MaxZ, cs);
            for (int z = min.Z; z <= max.Z; z++)
            for (int x = min.X; x <= max.X; x++)
                InvalidateLoaded(new ChunkCoord(x, z));
        }

        /// <summary>Single-chunk form of <see cref="Invalidate(RectArea)"/>: rebuild the chunk in place at its
        /// current LOD if it is loaded, no-op otherwise.</summary>
        public void Invalidate(ChunkCoord coord)
        {
            FlushPendingBuilds();
            InvalidateLoaded(coord);
        }

        void InvalidateLoaded(ChunkCoord coord)
        {
            if (!_loaded.TryGetValue(coord, out Entry? e)) return;
            _sink.ReLod(coord, e.Handle, e.Lod, e.Ring);
            _invalidates++;
        }

        /// <summary>The loaded chunks past <paramref name="unloadSq"/> that this frame should free: farthest first,
        /// capped at <see cref="StreamerConfig.MaxUnloadsPerFrame"/> (0 or less = all of them). Returned as a
        /// snapshot so the caller can mutate the dictionary while walking it.
        /// <para>Deferring the rest needs no queue: the next Update recomputes the far set from the player's new
        /// position, so a chunk that came back into range is simply not selected any more. That is what makes a
        /// deferred unload cancel itself instead of turning into an unload-then-reload.</para>
        /// <para>The order is fully determined by the coords, never by the dictionary: the candidates come out of
        /// <c>_loaded</c> in an enumeration order that is not a contract, and a run of equidistant chunks (a ring
        /// shift exposes them in pairs) would otherwise be cut by the budget differently from run to run. Sorting
        /// the whole set, not just a budgeted one, keeps the unload SEQUENCE reproducible too.</para></summary>
        List<ChunkCoord> FarChunksToUnload(ChunkCoord pc, float unloadSq)
        {
            var far = new List<ChunkCoord>();
            if (_loaded.Count == 0) return far;
            foreach (KeyValuePair<ChunkCoord, Entry> kv in _loaded)
            {
                int dx = kv.Key.X - pc.X, dz = kv.Key.Z - pc.Z;
                if (dx * dx + dz * dz > unloadSq) far.Add(kv.Key);
            }
            if (far.Count < 2) return far;
            // Farthest first, so the survivors are the ones nearest the ring, which are also the likeliest to come
            // back into range and have their unload cancelled. Coord breaks a distance tie.
            far.Sort((a, b) =>
            {
                int cmp = DistSq(b, pc).CompareTo(DistSq(a, pc));
                if (cmp != 0) return cmp;
                cmp = a.X.CompareTo(b.X);
                return cmp != 0 ? cmp : a.Z.CompareTo(b.Z);
            });
            int budget = _config.MaxUnloadsPerFrame;
            if (budget > 0 && far.Count > budget) far.RemoveRange(budget, far.Count - budget);
            return far;
        }

        static int DistSq(ChunkCoord c, ChunkCoord from)
        {
            int dx = c.X - from.X, dz = c.Z - from.Z;
            return dx * dx + dz * dz;
        }

        /// <summary>Apply a batch of completed builds and report how many landed, so a caller can tell a pass that
        /// did work from one that did none.</summary>
        int ApplyBuilds(IReadOnlyList<ChunkBuild<object>> builds)
        {
            for (int i = 0; i < builds.Count; i++)
            {
                ChunkBuild<object> rb = builds[i];
                object? existing = _loaded.TryGetValue(rb.Coord, out Entry? e) ? e.Handle : null;
                object handle = _asyncSink!.Apply(rb.Coord, rb.Lod, rb.Ring, rb.Payload, existing);
                _loaded[rb.Coord] = new Entry { Handle = handle, Lod = rb.Lod, Ring = rb.Ring };
                // A build that landed clears this chunk's failure streak, so the cap only ever counts CONSECUTIVE
                // failures and a chunk that recovers is not abandoned later for damage it already walked off.
                if (_attempts.Count > 0) _attempts.Remove(rb.Coord);
            }
            return builds.Count;
        }

        // The comparison body itself, bound once into _nearestFirst at construction (see the field doc comment)
        // rather than re-closed over playerPos/cs on every UpdateAsync call.
        int NearestFirstCompare(ChunkCoord a, ChunkCoord b)
        {
            Vector2 ca = ChunkGrid.CenterOf(a, _nearestFirstChunkSize), cb = ChunkGrid.CenterOf(b, _nearestFirstChunkSize);
            float da = Dist2(ca, _nearestFirstPlayerPos), db = Dist2(cb, _nearestFirstPlayerPos);
            return da.CompareTo(db);
        }

        // Internal (not private): lets KhaozEngine.Render.Tests (InternalsVisibleTo) restamp the comparison state
        // and hand back the bound delegate exactly the way UpdateAsync's call site does, to prove the delegate
        // itself is not (re)allocated per call (issue #374).
        internal Comparison<ChunkCoord> NearestFirstForTest(Vector3 playerPos, float cs)
        {
            _nearestFirstPlayerPos = playerPos;
            _nearestFirstChunkSize = cs;
            return _nearestFirst;
        }

        static float Dist2(Vector2 center, Vector3 p)
        {
            float dx = center.X - p.X, dz = center.Y - p.Z;
            return dx * dx + dz * dz;
        }

        readonly struct Pending
        {
            public readonly ChunkCoord Coord;
            public readonly int Lod;
            public readonly ChunkRing Ring;
            public readonly float Dist;
            public readonly bool IsLoad;
            public readonly BuildReason Reason;
            public Pending(ChunkCoord coord, int lod, ChunkRing ring, float dist, bool isLoad, BuildReason reason)
            { Coord = coord; Lod = lod; Ring = ring; Dist = dist; IsLoad = isLoad; Reason = reason; }
        }
    }
}
