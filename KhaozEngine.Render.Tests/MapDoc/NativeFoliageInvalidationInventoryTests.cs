using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed partial class NativeMapFoliageTests
{
    [Fact]
    public void Columns_InvalidationRefusesIndexGrowthBeforeAllocationAndKeepsEarlierOwnership()
    {
        using var fixture = new Fixture();
        var cache = MapFoliageRenderCache.For(fixture.Context);
        var keys = fixture.Context.ChunksFor("cover").Take(4).ToArray();
        var first = new MapSceneAdmission(new(MaxPreparedBytes: 100_000_000));
        foreach (var key in keys) _ = cache.Get("cover", key, new(), first, null);
        var old = CacheOwners(cache).Values.ToArray();
        var readers = old.Select(o => o.Retain()).ToArray();
        try
        {
            InvalidateColumns(cache, keys.Skip(1));
            long earlierBytes = first.RetainedBytes, calls = cache.GenerationCalls;
            Assert.Equal(7, CacheColumnCapacity(cache));
            var current = new MapSceneAdmission(new(MaxPreparedBytes: 256));
            using var window = current.LimitPreparation(256);
            int allocations = 0;
            var limits = new MapFoliageLimits { AllocateInstances = _ => allocations++ };
            Assert.Throws<MapSceneCapacityException>(() => cache.Get("cover", keys[1], limits, current, null));
            Assert.Equal(0, allocations);
            Assert.Equal(7, CacheColumnCapacity(cache));
            Assert.Equal(calls, cache.GenerationCalls);
            Assert.Equal(earlierBytes, first.RetainedBytes);
            Assert.Equal(0, current.RetainedBytes + current.WorkspaceBytes);
            Assert.All(readers, reader => Assert.NotNull(reader.Columns));
        }
        finally { cache.Clear(); foreach (var reader in readers) reader.Dispose(); }
        Assert.Equal(0, first.RetainedBytes);
    }

    [Fact]
    public void Columns_InvalidationIndexAndMetadataGrowthStayWithinIndependentRootAndWindowPeaks()
    {
        using var fixture = new Fixture();
        var cache = MapFoliageRenderCache.For(fixture.Context);
        var keys = fixture.Context.ChunksFor("cover").Take(4).ToArray();
        Assert.Equal(4, keys.Length);
        var first = new MapSceneAdmission(new(MaxPreparedBytes: 100_000_000));
        foreach (var key in keys) _ = cache.Get("cover", key, new(), first, null);
        var old = CacheOwners(cache).Values.ToArray();
        var readers = old.Select(o => o.Retain()).ToArray();
        var current = new MapSceneAdmission(new(MaxPreparedBytes: 100_000_000));
        try
        {
            InvalidateColumns(cache, keys.Skip(1));
            long earlierBytes = CacheDomainCensus(cache, old);
            Assert.Equal(earlierBytes, first.RetainedBytes);
            using var window = current.LimitPreparation(100_000_000);
            using var earlierView = current.ReferenceWindow(earlierBytes);
            var binding = NativeFoliageRetainedInventoryTests.Binding(window);
            long windowPeak = binding.Live;
            int metadataCapacity = MetadataCapacity(cache), indexCapacity = CacheColumnCapacity(cache);
            var ledger = Assert.Single(NativeFoliageInventoryTightnessTests.Fields(current).OfType<MapPreparationLedger>());
            ledger.Changed = () => windowPeak = Math.Max(windowPeak, binding.Live);
            bool metadataGrew = false, indexReplaced = false;
            foreach (var key in keys.Skip(1))
            {
                var limits = new MapFoliageLimits { AllocateInstances = _ => windowPeak = Math.Max(windowPeak, binding.Live) };
                _ = cache.Get("cover", key, limits, current, null);
                windowPeak = Math.Max(windowPeak, binding.Live);
                metadataGrew |= MetadataCapacity(cache) > metadataCapacity;
                indexReplaced |= CacheColumnCapacity(cache) != indexCapacity;
                Assert.Equal(CacheDomainCensus(cache, old), first.RetainedBytes + current.RetainedBytes);
            }
            Assert.True(indexReplaced);
            Assert.True(metadataGrew);
            long retained = CacheDomainCensus(cache, old);
            // Final old/new columns coexist with one accepted-row builder and old registry/index arrays.
            long scratch = CacheOwners(cache).Values.Max(ColumnBuilderScratch) +
                48 + 36L * indexCapacity + 48 + 28L * metadataCapacity;
            long simultaneous = retained + scratch;
            Assert.InRange(earlierBytes + current.PeakBytes, retained, 2 * simultaneous + 4096);
            Assert.InRange(windowPeak, retained, 2 * simultaneous + 4096);
            Assert.InRange(first.RetainedBytes, 0, earlierBytes);
            Assert.All(readers, reader => Assert.NotNull(reader.Columns));
        }
        finally { cache.Clear(); foreach (var reader in readers) reader.Dispose(); }
        Assert.Equal(0, first.RetainedBytes + current.RetainedBytes);
    }

    static void InvalidateColumns(MapFoliageRenderCache cache, IEnumerable<MapFoliageChunkKey> keys) =>
        cache.Invalidate(new(null, null, [], [], [], [], MapNativeInvalidation.Foliage)
        { FoliageChunks = keys.Select(key => new MapFoliageChunkChange("cover", key, null, null, "same", "same")).ToArray() });
    static Dictionary<MapFoliageCacheKey, MapFoliageColumnOwner> CacheOwners(MapFoliageRenderCache cache) =>
        (Dictionary<MapFoliageCacheKey, MapFoliageColumnOwner>)typeof(MapFoliageRenderCache)
            .GetField("_owners", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
    static int MetadataCapacity(MapFoliageRenderCache cache)
    {
        var registry = typeof(MapFoliageRenderCache).GetField("_metadata", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
        return Assert.Single(NativeFoliageInventoryTightnessTests.Fields(registry).OfType<Dictionary<object, int>>()).EnsureCapacity(0);
    }
    static long ColumnBuilderScratch(MapFoliageColumnOwner owner)
    {
        var batch = Assert.Single(NativeFoliageInventoryTightnessTests.Fields(owner).OfType<MapFoliageInstanceBatch>());
        static int Power(int minimum) { int value = 4; while (value < minimum) value *= 2; return value; }
        static long Index(int count) => count == 0 ? 0 :
            NativeFoliageInventoryTightnessTests.ArrayBytes(Power(2 * count), 8) +
            NativeFoliageInventoryTightnessTests.ArrayBytes(Power(2 * count), 4);
        long rows = batch.Count == 0 ? 0 : NativeFoliageInventoryTightnessTests.ArrayBytes(Power(batch.Count), 96);
        return 256 + rows + Index(batch.Columns.ModelCount) + Index(batch.SupportIds.Length);
    }
}
