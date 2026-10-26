using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Reflection;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed partial class NativeMapFoliageTests
{
    [Fact]
    public void Columns_CacheIndexShrinksBelowHalfAtPublication()
    {
        using var fixture = new Fixture();
        var cache = MapFoliageRenderCache.For(fixture.Context);
        var keys = fixture.Context.ChunksFor("cover").Take(4).ToArray();
        Assert.Equal(4, keys.Length);
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 100_000_000));
        foreach (var key in keys) _ = cache.Get("cover", key, new(), admission, null);
        Assert.Equal(7, CacheColumnCapacity(cache));
        cache.Invalidate(new(null, null, [], [], [], [], MapNativeInvalidation.Foliage)
        { FoliageChunks = keys.Skip(1).Select(key => new MapFoliageChunkChange("cover", key, null, null, "same", "same")).ToArray() });
        Assert.Equal(1, cache.Count);
        cache.Publish(admission, null, 67_108_864);
        Assert.Equal(3, CacheColumnCapacity(cache));
        cache.Clear();
    }
    static int CacheColumnCapacity(MapFoliageRenderCache cache) =>
        ((Dictionary<MapFoliageCacheKey, MapFoliageColumnOwner>)typeof(MapFoliageRenderCache).GetField("_owners", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!).EnsureCapacity(0);
    [Fact]
    public void Columns_CacheReusesTheSameBackingThroughPolicyReplacementAndInvalidation()
    {
        using var fixture = new Fixture();
        using var first = NativeMapFoliage.Prepare(fixture.Scene, fixture.Context, fixture.Assets, fixture.Policy, fixture.Routing);
        var firstGroup = ColumnGroups(first).First(g => !g.Explicit);
        var original = ColumnSource(firstGroup.Cover)!;
        var cache = MapFoliageRenderCache.For(fixture.Context);
        long calls = cache.GenerationCalls;
        using var replacement = NativeMapFoliage.Prepare(fixture.Scene, fixture.Context, fixture.Assets, fixture.Policy, fixture.Routing);
        var other = ColumnSource(ColumnGroups(replacement).Single(g => g.OwnerId == firstGroup.OwnerId).Cover)!;
        Assert.Same(original.Columns, other.Columns);
        Assert.Equal(calls, cache.GenerationCalls);
        var before = firstGroup.Cover.ToArray();
        cache.Invalidate(new(null, null, [], [], [], [], MapNativeInvalidation.Foliage)
        { FoliageLayers = [new("cover", "same", "same")] });
        Assert.Equal(0, cache.Count);
        Assert.Equal(before, firstGroup.Cover);
        using var rebuilt = NativeMapFoliage.Prepare(fixture.Scene, fixture.Context, fixture.Assets, fixture.Policy, fixture.Routing);
        var fresh = ColumnSource(ColumnGroups(rebuilt).Single(g => g.OwnerId == firstGroup.OwnerId).Cover)!;
        Assert.NotSame(original.Columns, fresh.Columns);
        Assert.Equal(before, ColumnGroups(rebuilt).Single(g => g.OwnerId == firstGroup.OwnerId).Cover);
    }

    [Fact]
    public void Columns_CacheReaderIsTheSameLiveOwnerAsTheAcceptedView()
    {
        using var fixture = new Fixture();
        using var batch = NativeMapFoliage.Prepare(fixture.Scene, fixture.Context, fixture.Assets, fixture.Policy, fixture.Routing);
        var key = batch.GenerationKeys.First();
        var group = ColumnGroups(batch).Single(g => g.OwnerId == $"{key.LayerId}/{key.Chunk.X}/{key.Chunk.Z}");
        var cache = MapFoliageRenderCache.For(fixture.Context);
        var held = cache.Acquire(key.LayerId, key.Chunk, new(), null, null, null);
        using (held)
        {
            var source = Assert.IsAssignableFrom<IGroundCoverInstanceSource>(held);
            Assert.Same(ColumnSource(group.Cover)!.Columns, source.Columns);
            cache.Clear(); Assert.Equal(0, cache.Count);
            batch.Dispose();
            Assert.True(source.Count > 0);
        }
        Assert.Throws<ObjectDisposedException>(() => ((IGroundCoverInstanceSource)(object)held).Columns);
    }

    [Fact]
    public void Columns_CacheBackingArraysDieAfterTheLastReaderEvenWithDisposedShellsAlive()
    {
        var probe = ReleaseColumnPayload();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        Assert.All(probe.Arrays, reference => Assert.False(reference.IsAlive));
        Assert.NotEmpty(probe.Keys);
        GC.KeepAlive(probe);
    }

    sealed record ColumnPayloadProbe(Fixture Fixture, MapNativeFoliageBatch Plan, IDisposable Reader,
        MapFoliageResource Resource, IReadOnlyList<MapFoliageSampleKey> Keys, WeakReference[] Arrays);
    [MethodImpl(MethodImplOptions.NoInlining)]
    static ColumnPayloadProbe ReleaseColumnPayload()
    {
        var fixture = new Fixture();
        var plan = NativeMapFoliage.Prepare(fixture.Scene, fixture.Context, fixture.Assets, fixture.Policy, fixture.Routing);
        var key = plan.GenerationKeys.First();
        var cache = MapFoliageRenderCache.For(fixture.Context);
        var held = cache.Acquire(key.LayerId, key.Chunk, new(), null, null, null);
        var source = Assert.IsAssignableFrom<IGroundCoverInstanceSource>(held);
        var arrays = ColumnFields(source.Columns).OfType<Array>().Concat(ColumnFields(held.Batch).OfType<Array>())
            .Select(array => new WeakReference(array)).ToArray();
        Assert.Equal(9, arrays.Length);
        var recorder = new MapDrawRecorder(); plan.RegisterResident(fixture.Stamp, recorder);
        var resource = Assert.IsType<MapFoliageResource>(Assert.Single(recorder.Snapshot().Resources,
            r => r.Reference.Key.OwnerId == $"{key.LayerId}/{key.Chunk.X}/{key.Chunk.Z}" && r.Payload is MapFoliageResource).Payload);
        var keys = plan.GeneratedIdentities;
        cache.Clear(); plan.Dispose(); fixture.Dispose();
        Assert.All(arrays, reference => Assert.True(reference.IsAlive));
        held.Dispose(); held.Dispose();
        return new(fixture, plan, held, resource, keys, arrays);
    }
}
