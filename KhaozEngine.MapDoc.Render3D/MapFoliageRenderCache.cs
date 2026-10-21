using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;

namespace KhaozEngine.MapDoc.Render3D;

public readonly record struct MapFoliageCacheKey(string LayerId, MapFoliageChunkKey Chunk);
public sealed record MapFoliageGenerationKey(string LayerId, MapFoliageChunkKey Chunk, string DependencyDigest);

/// <summary>Context-owned CPU samples only. Render policy edits reuse them without rerolling distribution.</summary>
public sealed class MapFoliageRenderCache
{
    static readonly ConditionalWeakTable<MapWindowFoliageContext, MapFoliageRenderCache> Caches = new();
    readonly MapWindowFoliageContext _context;
    readonly Dictionary<MapFoliageCacheKey, MapFoliageSampleBatch> _batches = new();
    readonly object _gate = new();
    long _generationCalls, _retainedBytes;
    MapFoliageRenderCache(MapWindowFoliageContext context) => _context = context;
    public static MapFoliageRenderCache For(MapWindowFoliageContext context)
    { ArgumentNullException.ThrowIfNull(context); return Caches.GetValue(context, static c => new(c)); }
    public int Count { get { lock (_gate) return _batches.Count; } }
    public long GenerationCalls { get { lock (_gate) return _generationCalls; } }
    internal MapFoliageSampleBatch Get(string layer, MapFoliageChunkKey chunk, MapFoliageLimits limits)
    {
        lock (_gate)
        {
            var key = new MapFoliageCacheKey(layer, chunk);
            if (_batches.TryGetValue(key, out var existing)) return existing;
            var bounded = limits with { MaxQueryRetainedBytes = Math.Min(limits.MaxQueryRetainedBytes, 67_108_864 - _retainedBytes) };
            _generationCalls++;
            var batch = MapFoliageDistribution.Generate(_context, layer, chunk, bounded);
            if (batch.Status != MapFoliageStatus.Complete)
                throw new MapRenderAssetRefusal(batch.Status == MapFoliageStatus.CapacityExceeded ? MapRenderAssetStatus.CapacityExceeded :
                    batch.Status == MapFoliageStatus.NotRepresentable ? MapRenderAssetStatus.NotRepresentable : MapRenderAssetStatus.Invalid,
                    "Native foliage generation refused: " + batch.Status);
            _batches.Add(key, batch); _retainedBytes = checked(_retainedBytes + batch.Work.RetainedBytes);
            return batch;
        }
    }
    /// <summary>Whole-layer entries invalidate every resident row even when their two digests agree.</summary>
    public IReadOnlyList<MapFoliageCacheKey> Invalidate(MapNativeEditEffects effects)
    {
        ArgumentNullException.ThrowIfNull(effects);
        lock (_gate)
        {
            var layers = effects.FoliageLayers.Select(l => l.LayerId).ToHashSet(StringComparer.Ordinal);
            var chunks = effects.FoliageChunks.Select(c => new MapFoliageCacheKey(c.LayerId, c.Chunk)).ToHashSet();
            var changed = _batches.Keys.Where(k => (effects.Invalidates & MapNativeInvalidation.Unbounded) != 0 ||
                layers.Contains(k.LayerId) || chunks.Contains(k)).ToArray();
            foreach (var key in changed) { _retainedBytes -= _batches[key].Work.RetainedBytes; _batches.Remove(key); }
            return Array.AsReadOnly(changed);
        }
    }
}
