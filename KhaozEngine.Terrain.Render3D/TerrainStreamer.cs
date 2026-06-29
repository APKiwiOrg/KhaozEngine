using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Terrain
{
    /// <summary>Keeps the world loaded in a ring around the player. Each <see cref="Update"/>: unloads chunks beyond
    /// <c>UnloadRadius</c> (immediate), enqueues loads for chunks inside the <c>LoadRadius</c> disk that are not yet
    /// loaded and re-LODs for loaded chunks whose <see cref="TerrainLod.PickLod"/> tier changed, then processes at
    /// most <c>MaxLoadsPerFrame</c> of those (nearest first) through the injected <see cref="IChunkSink"/>. Pure
    /// bookkeeping (no GPU, no field), so it is fully headless-testable; the sink does the real work. Load/unload
    /// use Euclidean chunk-distance; the hysteresis band (UnloadRadius &gt; LoadRadius) stops churn at boundaries.
    /// <para>Teardown: <see cref="UnloadAll"/> flushes the loaded ring through the sink (frees every loaded chunk),
    /// which a streaming rebuild (level change / world reload / teleport) must call first so the previous ring's GPU
    /// meshes are freed instead of leaked. <see cref="Dispose"/> does that and then disposes the sink if it is
    /// <see cref="IDisposable"/> - i.e. it assumes it owns the sink it was given (turn-key teardown). To rebuild
    /// streaming while REUSING the same sink, call <see cref="UnloadAll"/> and hand the same sink to the new
    /// streamer; call <see cref="Dispose"/> only when the sink (and its GPU resources) should go too.</para></summary>
    public sealed class TerrainStreamer : IDisposable
    {
        readonly StreamerConfig _config;
        readonly IChunkSink _sink;
        readonly Dictionary<ChunkCoord, Entry> _loaded = new();
        bool _disposed;

        sealed class Entry { public object Handle = null!; public int Lod; }

        public TerrainStreamer(StreamerConfig config, IChunkSink sink)
        {
            _config = config;
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        }

        /// <summary>The chunks currently loaded (after this frame's ops).</summary>
        public IReadOnlyCollection<ChunkCoord> Loaded => _loaded.Keys;

        /// <summary>The LOD tier a loaded chunk is currently built at, or -1 if not loaded.</summary>
        public int LodOf(ChunkCoord coord) => _loaded.TryGetValue(coord, out Entry? e) ? e.Lod : -1;

        /// <summary>Unload every currently-loaded chunk through the sink and clear the ring (after this <see cref="Loaded"/>
        /// is empty). Call before a streaming rebuild that keeps the same sink/scene alive so the previous ring's GPU
        /// meshes are freed rather than leaked. Does NOT dispose the sink (see <see cref="Dispose"/> for that).</summary>
        public void UnloadAll()
        {
            foreach (KeyValuePair<ChunkCoord, Entry> kv in _loaded)
                _sink.Unload(kv.Key, kv.Value.Handle);
            _loaded.Clear();
        }

        /// <summary>Flush the loaded ring (<see cref="UnloadAll"/>) then dispose the sink if it is
        /// <see cref="IDisposable"/>. Idempotent. Use this for full teardown; use <see cref="UnloadAll"/> to rebuild
        /// while keeping the sink.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            UnloadAll();
            (_sink as IDisposable)?.Dispose();
        }

        public void Update(Vector3 playerPos, float dt)
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
