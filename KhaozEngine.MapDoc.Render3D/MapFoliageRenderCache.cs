using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Scene;

namespace KhaozEngine.MapDoc.Render3D;

public readonly record struct MapFoliageCacheKey(string LayerId, MapFoliageChunkKey Chunk);
public sealed record MapFoliageGenerationKey(string LayerId, MapFoliageChunkKey Chunk, string DependencyDigest);

/// <summary>Context-owned CPU samples only. Render policy edits reuse them without rerolling distribution.</summary>
public sealed class MapFoliageRenderCache
{
    static readonly ConditionalWeakTable<MapWindowFoliageContext, MapFoliageRenderCache> Caches = new();
    readonly MapWindowFoliageContext _context;
    readonly Dictionary<MapFoliageCacheKey, MapFoliageSampleBatch> _batches = new();
    readonly Dictionary<MapFoliageCacheKey, SampleOwner> _owners = new();
    readonly object _gate = new();
    long _generationCalls, _retainedBytes;
    MapFoliageRenderCache(MapWindowFoliageContext context) => _context = context;
    public static MapFoliageRenderCache For(MapWindowFoliageContext context)
    { ArgumentNullException.ThrowIfNull(context); return Caches.GetValue(context, static c => new(c)); }
    internal long RetainedBytes { get { lock (_gate) return _retainedBytes; } }
    public int Count { get { lock (_gate) return _batches.Count; } }
    public long GenerationCalls { get { lock (_gate) return _generationCalls; } }
    internal MapFoliageSampleBatch Get(string layer, MapFoliageChunkKey chunk, MapFoliageLimits limits) =>
        Get(layer, chunk, limits, null, null);
    internal MapFoliageSampleBatch Get(string layer, MapFoliageChunkKey chunk, MapFoliageLimits limits,
        MapSceneAdmission? admission, Func<long, IDisposable>? retainSamples, long? aggregateAllowance = null)
    {
        lock (_gate)
        {
            var key = new MapFoliageCacheKey(layer, chunk);
            if (_batches.TryGetValue(key, out var existing)) return existing;
            long allowance = aggregateAllowance ?? limits.MaxQueryRetainedBytes;
            var bounded = limits with { MaxQueryRetainedBytes = Math.Min(limits.MaxQueryRetainedBytes, Math.Max(0, allowance - _retainedBytes)) };
            using var preparation = admission?.BeginPreparation();
            long reserved = 0;
            if (admission is not null)
            {
                var definition = ((IMapFoliageInputs)_context).Foliage!.Layers.Single(l => l.Id == layer);
                reserved = MapFoliageGenerationAdmission.RetentionDemand(_context, definition, chunk, limits, _retainedBytes, allowance);
                MapFoliageGenerationAdmission.Charge(admission, reserved);
                bounded = bounded with { MaxQueryRetainedBytes = reserved };
            }
            try
            {
                _generationCalls++;
                var batch = MapFoliageDistribution.Generate(_context, layer, chunk, bounded);
                if (batch.Status != MapFoliageStatus.Complete)
                    throw new MapRenderAssetRefusal(batch.Status == MapFoliageStatus.CapacityExceeded ? MapRenderAssetStatus.CapacityExceeded :
                        batch.Status == MapFoliageStatus.NotRepresentable ? MapRenderAssetStatus.NotRepresentable : MapRenderAssetStatus.Invalid,
                        "Native foliage generation refused: " + batch.Status);
                // Samples keep derived values. Their captured context has its own retaining owner.
                var receipt = preparation?.Complete(batch.Work.RetainedBytes, publishToParent: false, retainDependencies: false);
                IDisposable? cpu = null;
                try
                {
                    cpu = retainSamples?.Invoke(batch.Work.RetainedBytes);
                    _owners.Add(key, new(batch, receipt, cpu));
                }
                catch { cpu?.Dispose(); receipt?.Dispose(); throw; }
                _batches.Add(key, batch); _retainedBytes = checked(_retainedBytes + batch.Work.RetainedBytes);
                return batch;
            }
            finally { }
        }
    }
    internal Reader Acquire(string layer, MapFoliageChunkKey chunk, MapFoliageLimits limits,
        MapSceneAdmission? admission, Func<long, IDisposable>? retainSamples, long? aggregateAllowance)
    {
        lock (_gate)
        {
            _ = Get(layer, chunk, limits, admission, retainSamples, aggregateAllowance);
            return _owners[new(layer, chunk)].Retain();
        }
    }
    void Remove(MapFoliageCacheKey key)
    {
        _retainedBytes -= _batches[key].Work.RetainedBytes; _batches.Remove(key);
        _owners[key].Release(); _owners.Remove(key);
    }
    internal void Clear()
    { lock (_gate) { foreach (var key in _batches.Keys.ToArray()) Remove(key); } }
    internal sealed class Readers : IDisposable
    {
        readonly List<Reader> _leases = new();
        internal MapFoliageSampleBatch Add(Reader lease) { _leases.Add(lease); return lease.Batch; }
        public void Dispose() { foreach (var lease in _leases) lease.Dispose(); _leases.Clear(); }
    }
    internal sealed class Reader(SampleOwner owner) : IDisposable
    {
        bool _disposed;
        internal MapFoliageSampleBatch Batch => owner.Batch;
        public void Dispose() { if (_disposed) return; _disposed = true; owner.Release(); }
    }
    internal sealed class SampleOwner(MapFoliageSampleBatch batch, MapPreparationReservation? receipt, IDisposable? cpu)
    {
        readonly object _gate = new();
        int _owners = 1;
        internal MapFoliageSampleBatch Batch => batch;
        internal Reader Retain() { lock (_gate) { checked { _owners++; } return new(this); } }
        internal void Release()
        { lock (_gate) { if (--_owners == 0) { receipt?.Dispose(); cpu?.Dispose(); } } }
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
            foreach (var key in changed) Remove(key);
            return Array.AsReadOnly(changed);
        }
    }
}
