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

/// <summary>Context-owned immutable native columns. Cache, accepted and pending plans share their live owners.</summary>
public sealed class MapFoliageRenderCache
{
    static readonly ConditionalWeakTable<MapWindowFoliageContext, MapFoliageRenderCache> Caches = new();
    readonly MapWindowFoliageContext _context;
    readonly object _gate = new();
    readonly MapFoliageRetainedInventory _storage = new();
    Dictionary<MapFoliageCacheKey, MapFoliageColumnOwner>? _owners;
    MapFoliageSharedMetadata? _metadata;
    long _generationCalls;
    MapFoliageRenderCache(MapWindowFoliageContext context) => _context = context;
    public static MapFoliageRenderCache For(MapWindowFoliageContext context)
    { ArgumentNullException.ThrowIfNull(context); return Caches.GetValue(context, static c => new(c)); }
    internal long RetainedBytes { get { lock (_gate) return checked(_storage.Bytes + (_metadata?.RetainedBytes ?? 0)); } }
    public int Count { get { lock (_gate) return _owners?.Count ?? 0; } }
    public long GenerationCalls { get { lock (_gate) return _generationCalls; } }
    internal MapFoliageInstanceBatch Get(string layer, MapFoliageChunkKey chunk, MapFoliageLimits limits) => Get(layer, chunk, limits, null, null);
    internal MapFoliageInstanceBatch Get(string layer, MapFoliageChunkKey chunk, MapFoliageLimits limits,
        MapSceneAdmission? admission, Func<long, IDisposable>? retainSamples, long? aggregateAllowance = null)
    {
        lock (_gate)
        {
            var key = new MapFoliageCacheKey(layer, chunk);
            if (_owners is not null && _owners.TryGetValue(key, out var existing))
            { using var borrow = existing.Retain(); return borrow.Batch; }
            long allowance = aggregateAllowance ?? limits.MaxQueryRetainedBytes;
            using var activation = admission?.Enter();
            using var preparation = admission?.BeginPreparation();
            var definition = MapFoliageDistribution.Layer(_context, layer);
            long prepaid = MapFoliageGenerationAdmission.Begin(_context, definition, chunk, limits, RetainedBytes, allowance, admission);
            long temporary = 0;
            void Check(long bytes)
            {
                long current = checked(RetainedBytes + temporary);
                if (bytes > allowance - current)
                    throw new MapSceneCapacityException(nameof(MapFoliageLimits.MaxQueryRetainedBytes), (decimal)current + bytes, allowance);
            }
            void Take(long bytes)
            {
                Check(bytes);
                if (prepaid != 0) { if (bytes != prepaid) throw new InvalidOperationException("Unexpected first foliage allocation."); prepaid = 0; }
                else admission?.ReserveBytes(bytes);
                limits.AllocateInstances?.Invoke(bytes); temporary = checked(temporary + bytes);
            }
            void Give(long bytes)
            { limits.ReleaseInstances?.Invoke(bytes); admission?.ReleaseUnusedBytes(bytes); temporary -= bytes; }
            try
            {
                EnsureStorage(checked((_owners?.Count ?? 0) + 1), admission, retainSamples, Check);
                var bounded = limits with
                {
                    MaxQueryRetainedBytes = Math.Min(limits.MaxQueryRetainedBytes, Math.Max(0, allowance - RetainedBytes)),
                    AllocateInstances = Take,
                    ReleaseInstances = Give,
                };
                _generationCalls++;
                var batch = MapFoliageDistribution.GenerateInstances(_context, layer, chunk, bounded);
                if (batch.Status != MapFoliageStatus.Complete)
                    throw new MapRenderAssetRefusal(batch.Status == MapFoliageStatus.CapacityExceeded ? MapRenderAssetStatus.CapacityExceeded :
                        batch.Status == MapFoliageStatus.NotRepresentable ? MapRenderAssetStatus.NotRepresentable : MapRenderAssetStatus.Invalid,
                        "Native foliage generation refused: " + batch.Status);
                int capacity = Math.Max(3, batch.MetadataReferenceCapacity);
                long workspace = checked(128L + 40L * capacity);
                long referenceUpper = MapFoliageInventory.ReferenceArray(capacity);
                Take(checked(workspace + referenceUpper));
                var references = batch.CollectMetadata();
                Give(checked(workspace + referenceUpper - MapFoliageInventory.ReferenceArray(references.Length)));
                Take(MapFoliageColumnOwner.ControlBytes);
                if (_metadata is null || _metadata.Disposed) _metadata = new();
                _metadata.Add(references, admission, retainSamples, Check);
                long dataBytes = checked(batch.Work.RetainedBytes - MapFoliageSharedMetadata.ObjectBytes(references) +
                    MapFoliageColumnOwner.ControlBytes + MapFoliageInventory.ReferenceArray(references.Length));
                MapPreparationReservation? receipt = null; IDisposable? cpu = null;
                try
                {
                    receipt = preparation?.Complete(dataBytes, publishToParent: false, retainDependencies: false);
                    cpu = retainSamples?.Invoke(dataBytes);
                    var owner = new MapFoliageColumnOwner(batch, receipt, cpu, _metadata, references, dataBytes);
                    _metadata.AddDirect(dataBytes);
                    _owners!.Add(key, owner);
                    return batch;
                }
                catch
                {
                    cpu?.Dispose(); receipt?.Dispose();
                    long objects = _metadata.RemoveReferences(references); _metadata.SettleRelease(objects, 0);
                    throw;
                }
            }
            catch
            {
                if ((_owners?.Count ?? 0) == 0) { _owners = null; _storage.Dispose(); }
                throw;
            }
        }
    }
    internal MapFoliageColumnOwner.Reader Acquire(string layer, MapFoliageChunkKey chunk, MapFoliageLimits limits,
        MapSceneAdmission? admission, Func<long, IDisposable>? retainSamples, long? aggregateAllowance)
    {
        lock (_gate)
        {
            _ = Get(layer, chunk, limits, admission, retainSamples, aggregateAllowance);
            admission?.ReserveBytes(24);
            return _owners![new(layer, chunk)].Retain();
        }
    }
    void EnsureStorage(int needed, MapSceneAdmission? admission, Func<long, IDisposable>? retain, Action<long> check)
    {
        int capacity = _owners?.EnsureCapacity(0) ?? 0;
        bool resize = needed > capacity || 2L * needed < capacity;
        if (!resize) return;
        long oldArrays = capacity == 0 ? 0 : StorageArrays(capacity);
        long controls = _owners is null ? 248 : 0;
        long upper = StorageArrays(Math.Max(3, checked(needed * 2)));
        long before = _storage.Bytes;
        check(checked(controls + upper)); _storage.Grow(checked(controls + upper), admission, retain);
        _owners ??= new();
        if (needed > capacity) _owners.EnsureCapacity(needed); else _owners.TrimExcess(needed);
        _storage.ReduceTo(checked(before + controls + StorageArrays(_owners.EnsureCapacity(0)) - oldArrays));
    }
    static long StorageArrays(int capacity) => checked(48L + 36L * capacity);
    internal void Publish(MapSceneAdmission? admission, Func<long, IDisposable>? retain, long allowance)
    {
        lock (_gate)
        {
            if (_owners is null) return;
            void Check(long bytes)
            {
                long current = RetainedBytes;
                if (bytes > allowance - current)
                    throw new MapSceneCapacityException(nameof(MapFoliageLimits.MaxQueryRetainedBytes), (decimal)current + bytes, allowance);
            }
            EnsureStorage(_owners.Count, admission, retain, Check);
            if (_metadata is { Disposed: false }) _metadata.Add([], admission, retain, Check);
        }
    }
    void Remove(MapFoliageCacheKey key)
    {
        var owner = _owners![key]; _owners.Remove(key); owner.Release();
        if (_owners.Count == 0) { _owners = null; _storage.Dispose(); }
    }
    internal void Clear()
    {
        lock (_gate)
            while (_owners is { Count: > 0 }) Remove(_owners.First().Key);
    }
    internal sealed class Readers : IDisposable
    {
        readonly List<MapFoliageColumnOwner.Reader> _leases = new();
        internal MapFoliageInstanceBatch Add(MapFoliageColumnOwner.Reader lease) { _leases.Add(lease); return lease.Batch; }
        internal void Transfer() => _leases.Clear();
        public void Dispose() { foreach (var lease in _leases) lease.Dispose(); _leases.Clear(); }
    }
    /// <summary>Whole-layer entries invalidate every resident row even when their two digests agree.</summary>
    public IReadOnlyList<MapFoliageCacheKey> Invalidate(MapNativeEditEffects effects)
    {
        ArgumentNullException.ThrowIfNull(effects);
        lock (_gate)
        {
            if (_owners is null) return [];
            var layers = effects.FoliageLayers.Select(l => l.LayerId).ToHashSet(StringComparer.Ordinal);
            var chunks = effects.FoliageChunks.Select(c => new MapFoliageCacheKey(c.LayerId, c.Chunk)).ToHashSet();
            var changed = _owners.Where(e => (effects.Invalidates & MapNativeInvalidation.Unbounded) != 0 || layers.Contains(e.Key.LayerId) || chunks.Contains(e.Key))
                .Select(e => e.Key).ToArray();
            foreach (var key in changed) Remove(key);
            return Array.AsReadOnly(changed);
        }
    }
}
