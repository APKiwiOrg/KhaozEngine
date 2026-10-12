using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using KhaozEngine.WorldMap.Raster;

namespace KhaozEngine.WorldMap;

/// <summary>Caches generation-tagged chunks with thread-safe requests, invalidation and publication.</summary>
public sealed class WorldMapChunkCache : IDisposable
{
    readonly object _gate = new();
    readonly Dictionary<WorldMapChunkCoord, Entry> _chunks = [];
    long _generationCounter;
    long _requestSequence;
    int _retainedCount;
    bool _disposed;

    /// <summary>Creates a cache and subscribes to source changes for automatic invalidation.</summary>
    /// <param name="source">The authored world, permitting concurrent reads during builds.</param>
    /// <param name="style">The immutable raster and feature style used for every chunk.</param>
    /// <param name="maxRetainedChunks">The positive cap on retained built chunks, defaulting to 256.</param>
    /// <exception cref="ArgumentException">A style property is outside its supported finite range.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The retention cap is less than one.</exception>
    public WorldMapChunkCache(IWorldMapSource source, WorldMapStyle style, int maxRetainedChunks = 256)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(style);
        WorldMapStyleValidation.Validate(style);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRetainedChunks, 1);
        Source = source;
        Style = style;
        MaxRetainedChunks = maxRetainedChunks;
        Source.Changed += Invalidate;
    }

    /// <summary>The authored world sampled by the cache.</summary>
    public IWorldMapSource Source { get; }

    /// <summary>The raster resolution, appearance and feature rules used by this cache.</summary>
    public WorldMapStyle Style { get; }

    /// <summary>The maximum number of retained built chunks when eligible images can be evicted.</summary>
    public int MaxRetainedChunks { get; }

    /// <summary>The number of retained images, including stale images during pending or active rebuilds.</summary>
    public int RetainedCount
    {
        get { lock (_gate) return _retainedCount; }
    }

    internal event Action? WorkQueued;

    /// <summary>Queues an overlapping chunk, replacing its priority with the latest request.</summary>
    /// <remarks>
    /// Lower priorities build first. Every accepted request refreshes retention age.
    /// Current and failed chunks are not queued. Outside-bounds and disposed requests are ignored.
    /// </remarks>
    /// <param name="chunk">The map-space chunk address.</param>
    /// <param name="priority">The caller's current build priority.</param>
    public void Request(WorldMapChunkCoord chunk, float priority)
    {
        if (!WorldMapChunking.ChunkRect(chunk, Style.ChunkMetres).Intersects(Source.Bounds)) return;
        Action? wake;
        lock (_gate)
        {
            if (_disposed) return;
            if (!_chunks.TryGetValue(chunk, out Entry? entry))
                _chunks.Add(chunk, entry = new Entry { Generation = ++_generationCounter });
            entry.RequestSequence = ++_requestSequence;
            if (entry.Failed || entry.Built?.Generation == entry.Generation) return;
            entry.Priority = priority;
            entry.Pending = true;
            wake = WorkQueued;
        }
        wake?.Invoke();
    }

    /// <summary>Reads the latest successfully built chunk, including a stale result during rebuilds.</summary>
    /// <param name="chunk">The map-space chunk address.</param>
    /// <param name="built">The latest published chunk, or null when none has been built.</param>
    /// <returns>Whether a built chunk is available.</returns>
    public bool TryGet(WorldMapChunkCoord chunk, [NotNullWhen(true)] out WorldMapChunk? built)
    {
        lock (_gate)
        {
            built = _chunks.TryGetValue(chunk, out Entry? entry) ? entry.Built : null;
            return built is not null;
        }
    }

    /// <summary>Reports whether the latest built chunk matches its current cache generation.</summary>
    /// <param name="chunk">The map-space chunk address.</param>
    public bool IsCurrent(WorldMapChunkCoord chunk)
    {
        lock (_gate)
            return _chunks.TryGetValue(chunk, out Entry? entry) && entry.Built?.Generation == entry.Generation;
    }

    /// <summary>Reports whether this generation failed and requires an invalidation before retrying.</summary>
    /// <param name="chunk">The map-space chunk address.</param>
    public bool IsFailed(WorldMapChunkCoord chunk)
    {
        lock (_gate)
            return _chunks.TryGetValue(chunk, out Entry? entry) && entry.Failed;
    }

    /// <summary>Advances touched generations, clears their failures and queues requested chunks again.</summary>
    /// <remarks>The change area includes each cached level's shared surface and footprint raster reaches.</remarks>
    /// <param name="area">The map-space area affected by an authored change.</param>
    public void Invalidate(MapRect area)
    {
        Action? wake;
        lock (_gate)
        {
            if (_disposed) return;
            var levels = new HashSet<int>();
            foreach (WorldMapChunkCoord chunk in _chunks.Keys) levels.Add(chunk.Level);
            foreach (int level in levels)
            {
                float cellSize = WorldMapRasterReach.SampleCellSize(Source.SurfaceCellSize, 2f / Style.LevelPixelsPerMetre(level));
                float margin = WorldMapRasterReach.SurfaceMetres(Style, cellSize, level) +
                    WorldMapRasterReach.FootprintMetres(Style, level);
                MapRect affected = area.Expand(margin);
                // Visit requested entries rather than every possible chunk in a large change rectangle.
                foreach (var pair in _chunks)
                {
                    if (pair.Key.Level != level || !WorldMapChunking.ChunkRect(pair.Key, Style.ChunkMetres).Intersects(affected)) continue;
                    Entry entry = pair.Value;
                    entry.Generation = ++_generationCounter;
                    entry.Failed = false;
                    entry.Pending = true;
                }
            }
            wake = WorkQueued;
        }
        wake?.Invoke();
    }

    /// <summary>Builds the lowest-priority pending chunk on the calling thread, outside the bookkeeping lock.</summary>
    /// <remarks>
    /// A concurrent invalidation leaves the published result stale. Source failures are traced and quarantine
    /// only the generation that failed. Each chunk permits one active build at a time.
    /// </remarks>
    /// <returns>True when a pending build was attempted, including a failed build. False when none is available.</returns>
    public bool BuildNext()
    {
        WorldMapChunkCoord chunk = default;
        Entry? selected = null;
        long generation;
        lock (_gate)
        {
            if (_disposed) return false;
            foreach (var pair in _chunks)
            {
                Entry entry = pair.Value;
                if (!entry.Pending || entry.Building || entry.Failed) continue;
                if (selected is not null && entry.Priority.CompareTo(selected.Priority) >= 0) continue;
                chunk = pair.Key;
                selected = entry;
            }
            if (selected is null)
            {
                EvictOldest();
                return false;
            }
            generation = selected.Generation;
            selected.Pending = false;
            selected.Building = true;
        }

        WorldMapChunk? built = null;
        Exception? failure = null;
        try
        {
            WorldMapChunkImage image = WorldMapRasterizer.Rasterize(Source, Style, chunk);
            WorldMapChunkFeatures features = WorldMapFeatureCollector.Collect(Source, Style, chunk);
            built = new WorldMapChunk(image, features, generation);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Action? wake = null;
        lock (_gate)
        {
            selected.Building = false;
            if (!_disposed)
            {
                if (built is not null)
                {
                    if (selected.Built is null) _retainedCount++;
                    selected.Built = built;
                }
                if (selected.Generation == generation)
                {
                    selected.Failed = failure is not null;
                    selected.Pending = false;
                }
                // An earlier wake may have been consumed while this chunk was still building.
                if (selected.Pending) wake = WorkQueued;
                EvictOldest();
            }
        }
        if (failure is not null)
            Trace.TraceError("World map chunk ({0}, {1}) failed: {2}", chunk.X, chunk.Y, failure);
        wake?.Invoke();
        return true;
    }

    void EvictOldest()
    {
        while (_retainedCount > MaxRetainedChunks)
        {
            WorldMapChunkCoord oldestChunk = default;
            Entry? oldest = null;
            foreach (var pair in _chunks)
            {
                Entry entry = pair.Value;
                if (entry.Built is null || entry.Pending || entry.Building) continue;
                if (oldest is not null && entry.RequestSequence >= oldest.RequestSequence) continue;
                oldestChunk = pair.Key;
                oldest = entry;
            }
            if (oldest is null) break;
            _chunks.Remove(oldestChunk);
            _retainedCount--;
        }
    }

    /// <summary>Unsubscribes from source changes and stops accepting work, preserving readable built chunks.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Source.Changed -= Invalidate;
    }

    sealed class Entry
    {
        public long Generation;
        public float Priority;
        public long RequestSequence;
        public WorldMapChunk? Built;
        public bool Pending;
        public bool Building;
        public bool Failed;
    }
}
