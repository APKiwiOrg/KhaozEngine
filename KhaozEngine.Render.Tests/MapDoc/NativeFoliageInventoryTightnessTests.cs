using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeFoliageInventoryTightnessTests
{
    // Ledger objects, delegates and reporting are a fixed kernel, never a per-row allowance.
    const long KernelAllowance = 4096;
    [Fact]
    public void Columns_OldAndPendingViewsShareOneAbsoluteRootAndWindowInventory()
    {
        var fixture = new InventoryFixture(129, false);
        long rangeBytes = ArrayBytes(2, 16), oneView = 24 + 64 + rangeBytes;
        long both = fixture.FrozenBytes + 104 + 2 * oneView;
        long simultaneous = Math.Max(fixture.SimultaneousBytes, both);
        var root = new MapSceneAdmission(new(MaxPreparedBytes: checked(4 * simultaneous + KernelAllowance)));
        using var active = root.Enter();
        using var window = root.LimitPreparation(checked(2 * simultaneous + KernelAllowance), "replacement-window");
        var binding = (MapPreparationLedger.Window)typeof(MapSceneAdmission.PreparationWindow)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Single(f => f.FieldType == typeof(MapPreparationLedger.Window)).GetValue(window)!;
        long windowPeak = 0;
        void Take(long bytes) { root.ReserveBytes(bytes); windowPeak = Math.Max(windowPeak, binding.Live); }
        MapFoliageInstanceBatch batch;
        MapPreparationReservation data;
        using (var operation = root.BeginPreparation())
        {
            var limits = new MapFoliageLimits(MaxQueryRetainedBytes: long.MaxValue) { AllocateInstances = Take, ReleaseInstances = root.ReleaseUnusedBytes };
            using var builder = new MapFoliageInstanceBuilder(fixture.Layer, fixture.Policy, default, limits);
            for (int i = 0; i < 129; i++)
            {
                Assert.True(builder.TryPrepare(fixture.Supports[i], fixture.Models[i]));
                builder.AppendPrepared(i, -i, new(fixture.Models[i], new(i, 0, -i), Matrix4x4.Identity, .25f));
            }
            batch = builder.Freeze(fixture.Digest, new(129, 129, 0, 0, 0, 0, 0, 0));
            Assert.Equal(fixture.FrozenBytes, batch.Work.RetainedBytes);
            Take(104); data = operation.Complete(fixture.FrozenBytes + 104, publishToParent: false);
        }
        var owner = new MapFoliageColumnOwner(batch, data);
        MapFoliageColumnOwner.Reader oldReader, pendingReader;
        GroundCoverBatch old, pending;
        MapPreparationReservation oldReceipt, pendingReceipt;
        using (var operation = root.BeginPreparation())
        { Take(oneView); oldReader = owner.Retain(); old = new(oldReader); oldReceipt = operation.Complete(oneView, publishToParent: false); }
        using (var operation = root.BeginPreparation())
        { Take(oneView); pendingReader = owner.Retain(); pending = new(pendingReader); pendingReceipt = operation.Complete(oneView, publishToParent: false); }
        owner.Release();
        Assert.Same(oldReader.Columns, pendingReader.Columns);
        Assert.Equal(both, root.RetainedBytes);
        Assert.InRange(root.PeakBytes, simultaneous, checked(2 * simultaneous + KernelAllowance));
        Assert.InRange(windowPeak, simultaneous, checked(2 * simultaneous + KernelAllowance));
        old.ReleaseResident(); oldReader.Dispose(); oldReceipt.Dispose();
        Assert.Equal(fixture.FrozenBytes + 104 + oneView, root.RetainedBytes);
        Assert.Equal(129, pending.Count);
        pending.ReleaseResident(); pendingReader.Dispose(); pendingReceipt.Dispose();
        Assert.Equal(0, root.RetainedBytes);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(129, false)]
    [InlineData(1024, false)]
    [InlineData(4097, false)]
    [InlineData(257, true)]
    public void Columns_AbsoluteFrozenAndSimultaneousInventoriesBoundRootAndWindow(int rows, bool maximalTables)
    {
        var fixture = new InventoryFixture(rows, maximalTables);
        long frozen = fixture.FrozenBytes, simultaneous = fixture.SimultaneousBytes;
        var root = new MapSceneAdmission(new(MaxPreparedBytes: checked(4 * simultaneous + KernelAllowance)));
        using var active = root.Enter();
        using var window = root.LimitPreparation(checked(2 * simultaneous + KernelAllowance), "column-window");
        var binding = (MapPreparationLedger.Window)typeof(MapSceneAdmission.PreparationWindow)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Single(f => f.FieldType == typeof(MapPreparationLedger.Window)).GetValue(window)!;
        using var operation = root.BeginPreparation();
        long windowPeak = 0;
        var limits = new MapFoliageLimits(MaxQueryRetainedBytes: long.MaxValue)
        {
            AllocateInstances = bytes => { root.ReserveBytes(bytes); windowPeak = Math.Max(windowPeak, binding.Live); },
            ReleaseInstances = root.ReleaseUnusedBytes,
        };
        MapFoliageInstanceBatch batch;
        using (var builder = new MapFoliageInstanceBuilder(fixture.Layer, fixture.Policy, default, limits))
        {
            for (int i = 0; i < rows; i++)
            {
                Assert.True(builder.TryPrepare(fixture.Supports[i], fixture.Models[i]));
                builder.AppendPrepared(i, -i, new(fixture.Models[i], new(i, .125f, -i), Matrix4x4.CreateTranslation(i + .25f, 2, -i), .25f));
            }
            batch = builder.Freeze(fixture.Digest, new(rows, rows, 0, 0, 0, 0, 0, 0));
        }
        Assert.Equal(frozen, FrozenCensus(batch));
        Assert.Equal(frozen, batch.Work.RetainedBytes);
        Assert.Equal(frozen, operation.Bytes);
        Assert.InRange(root.PeakBytes, simultaneous, checked(2 * simultaneous + KernelAllowance));
        Assert.InRange(windowPeak, simultaneous, checked(2 * simultaneous + KernelAllowance));
        Assert.Equal(root.PeakBytes, windowPeak);
        using var receipt = operation.Complete(frozen, publishToParent: false);
        receipt.Dispose(); Assert.Equal(0, root.RetainedBytes);
    }

    internal static long FrozenCensus(MapFoliageInstanceBatch batch)
    {
        // Independently pinned object controls and the actual owned array lengths, not Work or production prices.
        long bytes = 112 + 56 + 80;
        foreach (var array in Fields(batch).OfType<Array>().Concat(Fields(batch.Columns).OfType<Array>())) bytes += ArrayBytes(array);
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Text(string? value) { if (value is not null) seen.Add(value); }
        Text(batch.LayerId); Text(batch.DependencyDigest); seen.Add(batch.Policy); Text(batch.Policy.Id);
        foreach (var array in Fields(batch).OfType<string[]>().Concat(Fields(batch.Columns).OfType<string[]>()))
            foreach (var value in array) Text(value);
        foreach (var value in seen) bytes += value is string text ? TextBytes(text) : 32;
        return bytes;
    }
    internal static IEnumerable<object?> Fields(object value) => value.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Select(f => f.GetValue(value));
    internal static long ArrayBytes(Array array)
    {
        var element = array.GetType().GetElementType();
        int width = element == typeof(Matrix4x4) ? 64 : element == typeof(Vector3) ? 12 :
            element == typeof(int) || element == typeof(float) ? 4 : element == typeof(MapFoliageSampleKey) ? 40 : 8;
        return ArrayBytes(array.Length, width);
    }
    internal static long ArrayBytes(int count, int width) => (24L + (long)count * width + 7) & ~7L;
    internal static long TextBytes(string value) => (22L + 2L * value.Length + 7) & ~7L;

    sealed class InventoryFixture
    {
        internal readonly string Layer = "layer", Digest = "digest";
        internal readonly MapFoliagePolicy Policy = new("rule", 1, 0);
        internal readonly string[] Models, Supports;
        internal readonly long FrozenBytes, SimultaneousBytes;
        internal InventoryFixture(int count, bool maximal)
        {
            Models = Enumerable.Range(0, count).Select(i => maximal ? "m" + i : i % 2 == 0 ? "A" : "B").ToArray();
            Supports = Enumerable.Range(0, count).Select(i => maximal ? "s" + i : "floor").ToArray();
            int m = Models.Distinct(StringComparer.Ordinal).Count(), s = Supports.Distinct(StringComparer.Ordinal).Count();
            long arrays = ArrayBytes(count, 64) + ArrayBytes(count, 12) + 5 * ArrayBytes(count, 4) + ArrayBytes(m, 8) + ArrayBytes(s, 8);
            long metadata = 32 + TextBytes(Layer) + TextBytes(Policy.Id) + TextBytes(Digest) +
                Models.Distinct(StringComparer.Ordinal).Sum(TextBytes) + Supports.Distinct(StringComparer.Ordinal).Sum(TextBytes);
            FrozenBytes = 248 + arrays + metadata;
            long page = count == 0 ? 0 : ArrayBytes(Power(Math.Max(4, count)), 96);
            long indexes = IndexBytes(m) + IndexBytes(s);
            // Frozen columns coexist with rows/indexes. Batch and final-work objects are created after those
            // arrays detach, while the builder control shells remain alive until the caller leaves its using.
            SimultaneousBytes = 256 + metadata + arrays + Math.Max(page + indexes + 56, 248);
        }
        static int Power(int minimum) { int value = 4; while (value < minimum) value *= 2; return value; }
        static long IndexBytes(int count) => count == 0 ? 0 : ArrayBytes(Power(2 * count), 8) + ArrayBytes(Power(2 * count), 4);
    }
}

[Collection("AllocSensitive")]
public sealed class NativeFoliageInventoryTightnessLayoutTests
{
    [Fact]
    public void ResidentAndMetadataControls_MatchTheIndependentCensusWidths()
    {
        (Type Type, long Bytes)[] layouts =
        [
            (typeof(MapFoliageRenderCache), 64), (typeof(MapFoliageColumnOwner), 80),
            (typeof(MapFoliageColumnOwner.Reader), 24), (typeof(MapFoliageSharedMetadata), 56),
            (typeof(MapFoliageRetainedInventory), 48), (typeof(GroundCoverBatch), 64),
            (typeof(MapFoliageResource), 88),
        ];
        foreach (var layout in layouts)
        {
            object held = RuntimeHelpers.GetUninitializedObject(layout.Type);
            long bytes = AllocationMeasurement.Measure("resident foliage control " + layout.Type.Name,
                () => held = RuntimeHelpers.GetUninitializedObject(layout.Type));
            GC.KeepAlive(held); Assert.Equal(layout.Bytes, bytes);
        }
    }
}

public sealed partial class NativeMapFoliageTests
{
    [Fact]
    public void Inventory_CacheCountsSharedMetadataAndOwnersOnceAcrossReplacement()
    {
        using var fixture = new Fixture();
        using var first = NativeMapFoliage.Prepare(fixture.Scene, fixture.Context, fixture.Assets, fixture.Policy, fixture.Routing);
        var cache = MapFoliageRenderCache.For(fixture.Context);
        long actual = CacheDomainCensus(cache);
        Assert.Equal(actual, cache.RetainedBytes);
        long generated = cache.GenerationCalls;
        using var pending = NativeMapFoliage.Prepare(fixture.Scene, fixture.Context, fixture.Assets, fixture.Policy, fixture.Routing);
        Assert.Equal(generated, cache.GenerationCalls);
        Assert.Equal(actual, CacheDomainCensus(cache));
        Assert.Equal(actual, cache.RetainedBytes);
        var before = ColumnGroups(first).First(g => !g.Explicit);
        var after = ColumnGroups(pending).Single(g => g.OwnerId == before.OwnerId);
        Assert.Same(ColumnSource(before.Cover)!.Columns, ColumnSource(after.Cover)!.Columns);
        Assert.Equal(Bits(before.Cover.Select(i => i.Transform).ToArray()), Bits(after.Cover.Select(i => i.Transform).ToArray()));
    }
    static long CacheDomainCensus(MapFoliageRenderCache cache)
    {
        var owners = (Dictionary<MapFoliageCacheKey, MapFoliageColumnOwner>)typeof(MapFoliageRenderCache)
            .GetField("_owners", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
        // Cache/gate/inventory/CPU-list/dictionary controls and their actual index capacity.
        long bytes = 248 + 48 + 36L * owners.EnsureCapacity(0);
        var metadata = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var owner in owners.Values)
        {
            var batch = Assert.Single(NativeFoliageInventoryTightnessTests.Fields(owner).OfType<MapFoliageInstanceBatch>());
            var refs = Assert.Single(NativeFoliageInventoryTightnessTests.Fields(owner).OfType<object[]>());
            // Owner/gate 104, batch 112, columns 56, work 80 and one exact metadata reference array.
            bytes += 104 + 248 + NativeFoliageInventoryTightnessTests.ArrayBytes(refs);
            foreach (var array in NativeFoliageInventoryTightnessTests.Fields(batch).OfType<Array>()
                .Concat(NativeFoliageInventoryTightnessTests.Fields(batch.Columns).OfType<Array>()))
                bytes += NativeFoliageInventoryTightnessTests.ArrayBytes(array);
            foreach (var value in refs) metadata.Add(value);
        }
        var registry = typeof(MapFoliageRenderCache).GetField("_metadata", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
        var references = Assert.Single(NativeFoliageInventoryTightnessTests.Fields(registry).OfType<Dictionary<object, int>>());
        bytes += 240 + 48 + 28L * references.EnsureCapacity(0);
        foreach (var value in metadata) bytes += value is string text ? NativeFoliageInventoryTightnessTests.TextBytes(text) : 32;
        return bytes;
    }
}
