using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

public sealed class MapWindowFoliageTests
{
    [Fact]
    public void FullAndWindowedFoliage_MatchEveryKeyMatrixRankAndDigest()
    {
        foreach (var fixture in new[] { FoliageFixtures.Create(), FoliageFixtures.Stacked(), FoliageFixtures.Cave(), FoliageFixtures.Pins() })
            foreach (var head in FoliageFixtures.Heads(fixture))
                foreach (bool tiled in new[] { false, true })
                {
                    using var saved = WindowFoliageFixtures.Scene(head);
                    var source = NativeSceneFixtures.Open(saved, tiled);
                    var full = FoliageFixtures.Context(head);
                    foreach (var layer in full.Foliage!.Layers.Reverse())
                        foreach (var key in layer.ChunkDigests.Select(c => c.Key).Reverse())
                        {
                            var result = MapFoliageGenerationContext.CaptureWindow(source, WindowFoliageFixtures.Scope(layer, key));
                            Assert.Equal(MapFoliageStatus.Complete, result.Status);
                            var window = Assert.IsType<MapWindowFoliageContext>(result.Context);
                            Assert.Equal(source.ClosureHash, window.ClosureHash);
                            Assert.Equal(source.SourceGeneration, window.Witness.SourceGeneration);
                            Assert.True(window.Witness.CompleteForScope);
                            long expansions = MapPrefabResolver.ThreadExpansions, writes = MapFoliageJson.ThreadPayloadWrites;
                            Same(MapFoliageDistribution.Generate(full, layer.Id, key), MapFoliageDistribution.Generate(window, layer.Id, key));
                            Assert.Equal(0, MapPrefabResolver.ThreadExpansions - expansions);
                            Assert.Equal(0, MapFoliageJson.ThreadPayloadWrites - writes);
                        }
                }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void WindowedFoliage_ThreeLatticesUseOneDerivedReadClosure(float edge)
    {
        var f = WindowFoliageFixtures.ThreeLattices(edge);
        foreach (bool tiled in new[] { false, true })
        {
            using var saved = WindowFoliageFixtures.Scene(f);
            var source = NativeSceneFixtures.Open(saved, tiled);
            var layer = f.Document.Foliage!.Layers[0];
            var result = MapFoliageGenerationContext.CaptureWindow(source, WindowFoliageFixtures.Scope(layer, new(0, 0)));
            Assert.True(result.Status == MapFoliageStatus.Complete, result.Status + " " + result.Work + " " + string.Join("|", result.Findings));
            Same(MapFoliageDistribution.Generate(FoliageFixtures.Context(f), layer.Id, new(0, 0)),
                MapFoliageDistribution.Generate(result.Context!, layer.Id, new(0, 0)));
            Assert.Contains(result.Context!.Witness.Required, k => k.Kind == "Patch" && k.Id.StartsWith("fine/", StringComparison.Ordinal));
            Assert.Contains(result.Context.Witness.Required, k => k.Kind == "Patch" && k.Id.StartsWith("medium/", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void WindowedFoliage_MovedPrefabAndNegativeSeamsKeepOwnership()
    {
        var f = FoliageFixtures.PrefabCaves();
        var moved = MapPrefabEdits.Prepare(f.Document, f.Assets, new MapMovePrefab("a", new MapTransform(new(2.5f, -1, 1.25f), 0.371f, 1.2f)));
        Compare(new(moved.After, f.Assets), new(0, 0));
        Compare(FoliageFixtures.Clearance("prefab"), new(0, 0));
        var rounded = WindowFoliageFixtures.Rounded();
        var layer = rounded.Document.Foliage!.Layers[0];
        float seam = MapFoliageDistribution.Rectangle(layer, new(1, 0)).MinX;
        using var saved = WindowFoliageFixtures.Scene(rounded);
        var source = NativeSceneFixtures.Open(saved, true);
        var scope = WindowFoliageFixtures.Scope(MathF.BitDecrement(seam), -1, seam + 1, 1);
        var window = MapFoliageGenerationContext.CaptureWindow(source, scope).Context!;
        var full = FoliageFixtures.Context(rounded);
        var expected = MapFoliageDistribution.Query(full, layer.Id, new(MathF.BitDecrement(seam), -1, seam + 1, 1));
        var actual = MapFoliageDistribution.Query(window, layer.Id, new(MathF.BitDecrement(seam), -1, seam + 1, 1));
        Assert.Equal(MapFoliageStatus.Complete, actual.Status);
        Assert.Equal(expected.Batches.Count, actual.Batches.Count);
        for (int i = 0; i < expected.Batches.Count; i++) Same(expected.Batches[i], actual.Batches[i]);
        foreach (var key in new[] { new MapFoliageChunkKey(-1, -1), new(1, 0) }) Compare(rounded, key);
    }

    [Fact]
    public void WindowedFoliage_AbsentZeroDiffersFromMissingPack()
    {
        var zero = FoliageFixtures.Create();
        zero.Document.Foliage = new(1, [FoliageFixtures.BasicLayer([])]);
        using var saved = WindowFoliageFixtures.Scene(zero);
        var layer = zero.Document.Foliage.Layers[0];
        var source = NativeSceneFixtures.Open(saved, true);
        var result = MapFoliageGenerationContext.CaptureWindow(source, WindowFoliageFixtures.Scope(layer, new(0, 0)));
        Assert.Equal(MapFoliageStatus.Complete, result.Status);
        Assert.Contains(result.Context!.Witness.KnownEmpty, k => k.Kind == "Foliage");
        var empty = MapFoliageDistribution.Generate(result.Context, layer.Id, new(0, 0));
        Assert.Equal(MapFoliageStatus.Complete, empty.Status);
        Assert.Empty(empty.Samples);
        Same(MapFoliageDistribution.Generate(FoliageFixtures.Context(zero), layer.Id, new(0, 0)), empty);

        foreach (bool corrupt in new[] { false, true })
        {
            using var stored = WindowFoliageFixtures.Scene(FoliageFixtures.Create());
            var pinned = NativeSceneFixtures.Open(stored, true);
            string pack = FoliageStorageFixtures.Packs(Assert.Single(stored.StorageDirectories))[0];
            if (corrupt) File.WriteAllBytes(pack, [1, 2, 3]);
            else File.Delete(pack);
            var missingPack = MapFoliageGenerationContext.CaptureWindow(pinned, WindowFoliageFixtures.Scope(layer, new(0, 0)));
            Assert.Equal(MapFoliageStatus.Unavailable, missingPack.Status);
            Assert.Null(missingPack.Context);
        }
    }

    [Fact]
    public void WindowedFoliage_BudgetAndStaleRefusalsPublishNothing()
    {
        var f = FoliageFixtures.Create();
        using var saved = WindowFoliageFixtures.Scene(f);
        var source = NativeSceneFixtures.Open(saved, true);
        var layer = f.Document.Foliage!.Layers[0];
        var scope = WindowFoliageFixtures.Scope(layer, new(0, 0));
        foreach (var limits in new[] { new MapFoliageContextLimits(MaxSourceUtf8Bytes: 0), new(MaxChunkDescriptors: 0),
            new(MaxChunkDescriptors: 1), new(MaxSurfacePatches: 0) })
        {
            var refused = MapFoliageGenerationContext.CaptureWindow(source, scope, limits);
            Assert.Equal(MapFoliageStatus.CapacityExceeded, refused.Status);
            Assert.Null(refused.Context);
        }
        var result = MapFoliageGenerationContext.CaptureWindow(source, scope);
        Assert.Equal(MapFoliageStatus.Complete, result.Status);
        var capped = MapFoliageDistribution.Generate(result.Context!, layer.Id, new(0, 0), new(MaxQuerySamples: 0));
        Assert.Equal(MapFoliageStatus.CapacityExceeded, capped.Status);
        Assert.Empty(capped.Samples);
        string manifest = Path.Combine(Assert.Single(saved.StorageDirectories), "map.json");
        File.AppendAllText(manifest, "\n");
        var stale = MapFoliageGenerationContext.CaptureWindow(source, scope);
        Assert.Equal(MapFoliageStatus.Unavailable, stale.Status);
        Assert.Null(stale.Context);
    }

    [Fact]
    public void WindowedFoliage_UnrelatedGrowthKeepsScopedPayloadAndWork()
    {
        var small = FoliageComparisonFixtures.Grown(2, 8, 1, 8);
        var large = FoliageComparisonFixtures.Grown(64, 128, 32, 128);
        foreach (bool tiled in new[] { false, true })
        {
            var results = new List<MapWindowFoliageContextResult>();
            foreach (var f in new[] { small, large })
            {
                using var saved = WindowFoliageFixtures.Scene(f);
                var source = NativeSceneFixtures.Open(saved, tiled);
                var layer = f.Document.Foliage!.Layers[0];
                var captured = MapFoliageGenerationContext.CaptureWindow(source, WindowFoliageFixtures.Scope(layer, new(0, 0)));
                Assert.Equal(MapFoliageStatus.Complete, captured.Status);
                var selected = Assert.Single(((IMapFoliageInputs)captured.Context!).Foliage!.Layers);
                Assert.Equal(2, selected.ChunkDigests.Count);
                results.Add(captured);
            }
            Assert.Equal(results[0].Work, results[1].Work);
            var a = MapFoliageDistribution.Generate(results[0].Context!, "cover-a", new(0, 0));
            var b = MapFoliageDistribution.Generate(results[1].Context!, "cover-a", new(0, 0));
            Same(a, b);
            Assert.Equal(a.Work, b.Work);
        }
    }

    [Fact]
    public void WindowedFoliage_WholeLayerEffectsInvalidateUnchangedDensityGenerations()
    {
        var before = WindowFoliageFixtures.Rounded();
        var after = FoliageFixtures.Reload(before, false);
        foreach (var patch in after.Document.Surfaces.Patches.Values)
            for (int i = 0; i < patch.Heights.Length; i++) patch.Heights[i] += 25;
        FoliageFixtures.Rehash(after.Document);
        var oldFull = FoliageFixtures.Context(before);
        var newFull = FoliageFixtures.Context(after);
        var effects = MapFoliageDependents.Describe(before.Document, after.Document, () => oldFull, () => newFull,
            limits: new(MaxPreparedSides: 0));
        var invalidation = Assert.Single(effects.Layers);
        Assert.Equal(invalidation.BeforeDigest, invalidation.AfterDigest);
        Assert.Empty(effects.Rows);
        var generations = new List<MapWindowFoliageContext>();
        foreach (var f in new[] { before, after })
        {
            using var saved = WindowFoliageFixtures.Scene(f);
            var source = NativeSceneFixtures.Open(saved, false);
            var layer = f.Document.Foliage!.Layers[0];
            var result = MapFoliageGenerationContext.CaptureWindow(source, WindowFoliageFixtures.Scope(layer, new(1, 0)));
            Assert.Equal(MapFoliageStatus.Complete, result.Status);
            generations.Add(result.Context!);
        }
        Assert.NotEqual(generations[0].SnapshotId, generations[1].SnapshotId);
        var oldBatch = MapFoliageDistribution.Generate(generations[0], "cover-a", new(1, 0));
        var newBatch = MapFoliageDistribution.Generate(generations[1], "cover-a", new(1, 0));
        Same(MapFoliageDistribution.Generate(oldFull, "cover-a", new(1, 0)), oldBatch);
        Same(MapFoliageDistribution.Generate(newFull, "cover-a", new(1, 0)), newBatch);
        Assert.NotEqual(oldBatch.DependencyDigest, newBatch.DependencyDigest);
        Assert.NotEmpty(oldBatch.Samples);
        Assert.NotEmpty(newBatch.Samples);
    }

    [Fact]
    public void WindowedFoliage_ClearanceCandidateRefusalMatchesCompleteMetadata()
    {
        var f = FoliageComparisonFixtures.TwoSamplesPerChunk();
        var layer = f.Document.Foliage!.Layers[0];
        f.Document.Placements.Add(FoliageFixtures.ClearingPlacement("outside-read", "floor", 25, 0, true));
        f.Document.Foliage = new(1, [FoliageFixtures.ClearanceLayer(layer,
            rules: [new("clear", "tree", null, MapFoliageClearanceKind.Solid, new(-0.1f, -0.1f, 0.1f, 0.1f))],
            owners: [new(FoliageFixtures.Owner("outside-read"), FoliageFixtures.Surface("floor"))])]);
        using var saved = WindowFoliageFixtures.Scene(f);
        var source = NativeSceneFixtures.Open(saved, false);
        var result = MapFoliageGenerationContext.CaptureWindow(source, WindowFoliageFixtures.Scope(layer, new(0, 0)));
        Assert.Equal(MapFoliageStatus.Complete, result.Status);
        var limits = new MapFoliageLimits(MaxRuleCandidates: 0);
        var full = MapFoliageDistribution.Generate(FoliageFixtures.Context(f), layer.Id, new(0, 0), limits);
        var window = MapFoliageDistribution.Generate(result.Context!, layer.Id, new(0, 0), limits);
        Assert.Equal(MapFoliageStatus.CapacityExceeded, full.Status);
        Assert.Equal(full.Status, window.Status);
        Assert.Empty(window.Samples);
    }

    [Fact]
    public void WindowedFoliage_OffRasterNeedsOnlyRasterWitness()
    {
        var f = FoliageFixtures.Create();
        using var saved = WindowFoliageFixtures.Scene(f);
        var source = NativeSceneFixtures.Open(saved, true);
        string directory = Assert.Single(saved.StorageDirectories);
        foreach (var key in f.Document.Surfaces.Patches.Keys) SurfaceStorageFixtures.DeletePayload(directory, key);
        foreach (string path in FoliageStorageFixtures.Packs(directory)) File.Delete(path);
        var layer = f.Document.Foliage!.Layers[0];
        var keyOutside = new MapFoliageChunkKey(-1, -1);
        using var storage = MapFoliageStorageWork.Capture();
        var result = MapFoliageGenerationContext.CaptureWindow(source, WindowFoliageFixtures.Scope(layer, keyOutside));
        Assert.Equal(MapFoliageStatus.Complete, result.Status);
        Assert.Equal(0, result.Work.SurfacePatches);
        Assert.Equal(0, storage.PackReads);
        var actual = MapFoliageDistribution.Generate(result.Context!, layer.Id, keyOutside);
        var expected = MapFoliageDistribution.Generate(FoliageFixtures.Context(f), layer.Id, keyOutside);
        Same(expected, actual);
        Assert.Equal(0, actual.Work.PrepareScopes);
    }

    [Fact]
    public void WindowedFoliage_SourceValidationMatchesReleasedBoundGate()
    {
        var f = FoliageFixtures.Create();
        f.Document.Foliage = new(1, [FoliageComparisonFixtures.Change(f.Document.Foliage!.Layers[0],
            l => l["materials"]![0]!["resourceId"] = "mesh")]);
        Assert.Equal(MapFoliageStatus.Invalid, MapFoliageGenerationContext.Capture(f.Document, f.Assets).Status);
        using var document = MapDocumentSource.FromDocument(f.Document);
        var source = MapSceneSource.Capture(document, f.Assets);
        Assert.Equal(MapSceneSourceStatus.Invalid, source.Status);
        Assert.Null(source.Source);
    }

    static void Compare(FoliageFixture fixture, MapFoliageChunkKey key)
    {
        using var saved = WindowFoliageFixtures.Scene(fixture);
        var source = NativeSceneFixtures.Open(saved, false);
        var full = FoliageFixtures.Context(fixture);
        foreach (var layer in full.Foliage!.Layers)
        {
            var result = MapFoliageGenerationContext.CaptureWindow(source, WindowFoliageFixtures.Scope(layer, key));
            Assert.Equal(MapFoliageStatus.Complete, result.Status);
            Same(MapFoliageDistribution.Generate(full, layer.Id, key), MapFoliageDistribution.Generate(result.Context!, layer.Id, key));
        }
    }

    static void Same(MapFoliageSampleBatch expected, MapFoliageSampleBatch actual)
    {
        Assert.Equal(MapFoliageStatus.Complete, expected.Status);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.Chunk, actual.Chunk);
        Assert.Equal(expected.DependencyDigest, actual.DependencyDigest);
        Assert.Equal(expected.Samples.Select(s => s.Key), actual.Samples.Select(s => s.Key));
        Assert.Equal(expected.Samples.SelectMany(s => MatrixBits(s.Transform)), actual.Samples.SelectMany(s => MatrixBits(s.Transform)));
        Assert.Equal(expected.Samples.Select(s => BitConverter.SingleToInt32Bits(s.ThinningRank)), actual.Samples.Select(s => BitConverter.SingleToInt32Bits(s.ThinningRank)));
        Assert.Equal(expected.Samples.Select(s => (s.AssetId, s.VisibleMaterial, s.Face, s.OccupiedSpaceId)),
            actual.Samples.Select(s => (s.AssetId, s.VisibleMaterial, s.Face, s.OccupiedSpaceId)));
    }

    static IEnumerable<int> MatrixBits(Matrix4x4 m) => new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 }.Select(BitConverter.SingleToInt32Bits);
}

[CollectionDefinition("window-foliage-retention", DisableParallelization = true)]
public sealed class WindowFoliageRetentionCollection { }

[Collection("window-foliage-retention")]
public sealed class MapWindowFoliageRetentionTests
{
    [Fact]
    public void WindowContext_ReleasesUnrelatedDensityPayloads()
    {
        var (context, producer) = Capture();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
        Assert.False(producer.TryGetTarget(out _));
        var batch = MapFoliageDistribution.Generate(context, "cover-a", new(0, 0));
        Assert.Equal(MapFoliageStatus.Complete, batch.Status);
        Assert.NotEmpty(batch.Samples);
        GC.KeepAlive(context);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static (MapWindowFoliageContext Context, WeakReference<MapFoliageChunk> Producer) Capture()
    {
        using var fixture = WindowFoliageFixtures.Scene(WindowFoliageFixtures.Rounded());
        var source = NativeSceneFixtures.Open(fixture, false);
        var layer = fixture.Document.Foliage!.Layers[0];
        var captured = MapFoliageGenerationContext.CaptureWindow(source, WindowFoliageFixtures.Scope(layer, new(0, 0)));
        Assert.Equal(MapFoliageStatus.Complete, captured.Status);
        Assert.NotNull(source.Data.ResidentFoliage);
        return (captured.Context!, new(source.Data.ResidentFoliage.Layers[0].Chunks.Single(c => c.Key == new MapFoliageChunkKey(2, 0))));
    }
}
