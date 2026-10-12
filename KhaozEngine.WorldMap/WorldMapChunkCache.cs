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
    bool _disposed;

    /// <summary>Creates a cache and subscribes to source changes for automatic invalidation.</summary>
    /// <param name="source">The authored world, permitting concurrent reads during builds.</param>
    /// <param name="style">The immutable raster and feature style used for every chunk.</param>
    public WorldMapChunkCache(IWorldMapSource source, WorldMapStyle style)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(style);
        Source = source;
        Style = style;
        Source.Changed += Invalidate;
    }

    /// <summary>The authored world sampled by the cache.</summary>
    public IWorldMapSource Source { get; }

    /// <summary>The raster resolution, appearance and feature rules used by this cache.</summary>
    public WorldMapStyle Style { get; }

    internal event Action? WorkQueued;

    /// <summary>Queues an overlapping chunk, replacing its priority with the latest request.</summary>
    /// <remarks>Lower priorities build first. Current, failed, outside-bounds and disposed requests are ignored.</remarks>
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
                _chunks.Add(chunk, entry = new Entry());
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
    /// <remarks>The change area includes the shared surface and footprint raster reaches.</remarks>
    /// <param name="area">The map-space area affected by an authored change.</param>
    public void Invalidate(MapRect area)
    {
        float margin = WorldMapRasterReach.SurfaceMetres(Style, Source.SurfaceCellSize) +
            WorldMapRasterReach.FootprintMetres(Style);
        Action? wake;
        lock (_gate)
        {
            if (_disposed) return;
            foreach (WorldMapChunkCoord chunk in WorldMapChunking.ChunksTouching(area.Expand(margin), Style.ChunkMetres))
            {
                // Unrequested chunks have no result or active generation to invalidate.
                if (!_chunks.TryGetValue(chunk, out Entry? entry)) continue;
                entry.Generation++;
                entry.Failed = false;
                entry.Pending = true;
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
        int generation;
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
            if (selected is null) return false;
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
                if (built is not null) selected.Built = built;
                if (selected.Generation == generation)
                {
                    selected.Failed = failure is not null;
                    selected.Pending = false;
                }
                // An earlier wake may have been consumed while this chunk was still building.
                if (selected.Pending) wake = WorkQueued;
            }
        }
        if (failure is not null)
            Trace.TraceError("World map chunk ({0}, {1}) failed: {2}", chunk.X, chunk.Y, failure);
        wake?.Invoke();
        return true;
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
        public int Generation;
        public float Priority;
        public WorldMapChunk? Built;
        public bool Pending;
        public bool Building;
        public bool Failed;
    }
}
