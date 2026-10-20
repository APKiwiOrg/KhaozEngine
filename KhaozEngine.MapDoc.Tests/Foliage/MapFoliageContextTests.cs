using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.MapDoc.Prefabs;
using KhaozEngine.Tests.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

/// <summary>Context capture is budgeted before expansion and runs once per snapshot. Chunk-scoped reads use the captured
/// spatial metadata, so unrelated growth elsewhere in the document never grows them.</summary>
public sealed class MapFoliageContextTests
{
    // Chunk (0, 0) of the grown layer spans world X [-3, 59] and Z [-5, 57]. The fixed request adds a 2 m halo.
    static readonly MapFoliageRect Near = new(-5, -7, 61, 59);

    static MapSurfaceScope NearScope() => new(WorldFrame.Origin, new Vector2(Near.MinX, Near.MinZ), new Vector2(Near.MaxX, Near.MaxZ),
        null, null, Enum.GetValues<MapSurfaceRole>(), null, new MapQueryLimits());

    [Fact]
    public void ContextAcquisition_UnrelatedGrowthDoesNotGrowChunkWork()
    {
        FoliageFixture smallFixture = Grown(chunks: 2, placements: 8, paintTargets: 1, wide: 8);
        FoliageFixture largeFixture = Grown(chunks: 64, placements: 128, paintTargets: 32, wide: 128);
        MapFoliageContextResult small = MapFoliageGenerationContext.Capture(smallFixture.Document, smallFixture.Assets);
        MapFoliageContextResult large = MapFoliageGenerationContext.Capture(largeFixture.Document, largeFixture.Assets);
        Assert.Equal((MapFoliageStatus.Complete, MapFoliageStatus.Complete), (small.Status, large.Status));
        MapFoliageContextResult captured = large;
        Assert.Equal(1, captured.Work.Expansions);
        Assert.Equal(1, small.Work.Expansions);

        // Capture accounts for the growth once.
        Assert.Equal((2L, 64L), (small.Work.ChunkDescriptors, large.Work.ChunkDescriptors));
        Assert.Equal(240, large.Work.Placements - small.Work.Placements);
        Assert.Equal((1L, 32L), (small.Work.PaintTargets, large.Work.PaintTargets));
        Assert.True(large.Work.SourceUtf8Bytes > small.Work.SourceUtf8Bytes);
        Assert.True(large.Work.SurfacePatches > small.Work.SurfacePatches);

        string fixedChunkWork = ChunkWork(small.Context!, out _, out _);
        string unrelatedGrowthChunkWork = ChunkWork(large.Context!, out long chunkPhaseExpansions, out long chunkPhaseSnapshotSerializations);
        Assert.Equal(0, chunkPhaseExpansions);
        Assert.Equal(0, chunkPhaseSnapshotSerializations);
        Assert.Equal(fixedChunkWork, unrelatedGrowthChunkWork);
        Assert.StartsWith("Complete | 4 | ", fixedChunkWork, StringComparison.Ordinal);       // the four floor slots meeting the request
        Assert.Contains(" | 2 | tree-0,tree-1 | ", fixedChunkWork, StringComparison.Ordinal);   // two owner candidates, both inside
        // Every distant 400 m owner is indexed and found by a query that reaches it, and none is inspected near chunk (0, 0).
        var distant = new MapFoliageRect(19000, 19000, 90000, 21000);
        Assert.Equal(128, large.Context!.PlacementsIn(distant).Count(p => p.PlacementId.StartsWith("wide-", StringComparison.Ordinal)));
        Assert.Equal(8, small.Context!.PlacementsIn(distant).Count);

        // The context is reused, never rebuilt by scoped calls.
        Assert.Equal(large.Work, large.Context!.Work);
        string snapshot = large.Context.SnapshotId;
        _ = ChunkWork(large.Context, out _, out _);
        Assert.Equal(snapshot, large.Context.SnapshotId);

        // A fully loaded tiled document captures and answers the same scoped requests.
        FoliageFixture tiledFixture = FoliageFixtures.Reload(largeFixture, tiled: true);
        Assert.NotNull(tiledFixture.Document.Tiles);
        MapFoliageContextResult tiled = MapFoliageGenerationContext.Capture(tiledFixture.Document, tiledFixture.Assets);
        Assert.Equal(MapFoliageStatus.Complete, tiled.Status);
        Assert.Equal(fixedChunkWork, ChunkWork(tiled.Context!, out long tiledExpansions, out long tiledWrites));
        Assert.Equal((0L, 0L), (tiledExpansions, tiledWrites));
    }

    [Fact]
    public void PartialSnapshot_RefusesBeforeCapture()
    {
        using var dir = new FoliageStorageFixtures.DirectoryScope();
        FoliageFixture fixture = FoliageStorageFixtures.ManyPacks(count: 4);
        MapDocumentFile.SaveTiled(fixture.Document, dir.Root);
        MapDocument windowed = MapDocumentFile.LoadTiled(dir.Root, new MapTileRect(new(0, 0), new(0, 0)));
        Assert.True(windowed.Tiles!.IsPartial);

        // Only the density bytes are missing: an advertised chunk without its resident bytes and no tile index.
        MapDocument foliageOnly = FoliageFixtures.Create().Document;
        MapFoliageLayer layer = foliageOnly.Foliage!.Layers[0];
        foliageOnly.Foliage = new(1, [MapFoliageLayer.FromStorage(layer, layer.ChunkDigests, layer.Chunks.Take(1).ToArray())]);
        Assert.Null(foliageOnly.Tiles);

        foreach (MapDocument document in new[] { windowed, foliageOnly })
        {
            long expansions = MapPrefabResolver.ThreadExpansions, writes = MapFoliageJson.ThreadPayloadWrites;
            MapFoliageContextResult partial;
            using (var storage = MapFoliageStorageWork.Capture())
            {
                partial = MapFoliageGenerationContext.Capture(document, fixture.Assets);
                Assert.Equal(0, storage.PackReads);
            }
            Assert.Equal(MapFoliageStatus.PartialResidency, partial.Status);
            Assert.Null(partial.Context);
            Assert.Equal(0, partial.Work.Expansions);
            Assert.Equal((0L, 0L), (MapPrefabResolver.ThreadExpansions - expansions, MapFoliageJson.ThreadPayloadWrites - writes));
            Assert.Equal(MapFoliageStatus.PartialResidency, MapFoliageContextPreflight.Measure(document, fixture.Assets, new()).Status);
        }

        // The complete tiled load of the same files captures.
        MapFoliageContextResult complete = MapFoliageGenerationContext.Capture(MapDocumentFile.LoadTiled(dir.Root), fixture.Assets);
        Assert.Equal(MapFoliageStatus.Complete, complete.Status);
        Assert.Equal(1, complete.Work.Expansions);
    }

    [Fact]
    public void ContextBudget_RefusesBeforeExpansionOrStagingAllocation()
    {
        FoliageFixture f = Grown(chunks: 2, placements: 3, paintTargets: 1);
        string before = MapDocumentFile.SaveText(f.Document);
        MapFoliageContextMeasure measure = MapFoliageContextPreflight.Measure(f.Document, f.Assets, new());
        Assert.Equal(MapFoliageStatus.Complete, measure.Status);
        MapFoliageContextWork demand = measure.Work;
        Assert.True(demand.SourceUtf8Bytes > 0 && demand.ChunkDescriptors > 0 && demand.Placements > 0 &&
            demand.SurfacePatches > 0 && demand.PaintTargets > 0);
        Assert.Equal((0, 0), (demand.Expansions, demand.SnapshotSerializations));

        var lowered = new[]
        {
            new MapFoliageContextLimits(MaxSourceUtf8Bytes: demand.SourceUtf8Bytes - 1),
            new MapFoliageContextLimits(MaxChunkDescriptors: (int)demand.ChunkDescriptors - 1),
            new MapFoliageContextLimits(MaxPlacements: (int)demand.Placements - 1),
            new MapFoliageContextLimits(MaxSurfacePatches: (int)demand.SurfacePatches - 1),
            new MapFoliageContextLimits(MaxPaintTargets: (int)demand.PaintTargets - 1),
        };
        foreach (MapFoliageContextLimits limits in lowered)
        {
            long expansions = MapPrefabResolver.ThreadExpansions, writes = MapFoliageJson.ThreadPayloadWrites;
            MapFoliageContextResult refused = MapFoliageGenerationContext.Capture(f.Document, f.Assets, limits);
            Assert.Equal(MapFoliageStatus.CapacityExceeded, refused.Status);
            Assert.Null(refused.Context);
            Assert.Equal(0, refused.Work.Expansions);
            Assert.Equal(0, refused.Work.SnapshotSerializations);
            Assert.Equal((0L, 0L), (MapPrefabResolver.ThreadExpansions - expansions, MapFoliageJson.ThreadPayloadWrites - writes));
            Assert.Equal(MapFoliageStatus.CapacityExceeded, MapFoliageContextPreflight.Measure(f.Document, f.Assets, limits).Status);
        }
        // The exact demand is admitted.
        var exact = new MapFoliageContextLimits(demand.SourceUtf8Bytes, (int)demand.ChunkDescriptors, (int)demand.Placements,
            (int)demand.SurfacePatches, (int)demand.PaintTargets);
        MapFoliageContextResult captured = MapFoliageGenerationContext.Capture(f.Document, f.Assets, exact);
        Assert.Equal(MapFoliageStatus.Complete, captured.Status);
        Assert.Equal(1, captured.Work.Expansions);
        Assert.Equal(demand with { Expansions = 1, SnapshotSerializations = captured.Work.SnapshotSerializations }, captured.Work);
        Assert.Equal(before, MapDocumentFile.SaveText(f.Document));                              // capture never edits its input

        // Later edits make a new context and never reach the captured one.
        MapFoliageGenerationContext context = captured.Context!;
        string foliageDigest = MapFoliageProjection.Digest(context.Foliage);
        f.Document.Foliage = new(1, [FoliageFixtures.BasicLayer()]);
        Assert.Equal(foliageDigest, MapFoliageProjection.Digest(context.Foliage));
        MapFoliageContextResult edited = MapFoliageGenerationContext.Capture(f.Document, f.Assets);
        Assert.NotEqual(context.SnapshotId, edited.Context!.SnapshotId);

        // Exact resources a transaction already expanded are reused without another expansion.
        MapEffectiveResources resources = MapPrefabResolver.Expand(f.Document, f.Assets);
        long beforeReuse = MapPrefabResolver.ThreadExpansions;
        MapFoliageContextResult reused = MapFoliageGenerationContext.CaptureEffective(f.Document, f.Assets, resources);
        Assert.Equal(MapFoliageStatus.Complete, reused.Status);
        Assert.Equal(0, reused.Work.Expansions);
        Assert.Equal(0, MapPrefabResolver.ThreadExpansions - beforeReuse);
        Assert.Equal(edited.Context.SnapshotId, reused.Context!.SnapshotId);
        Assert.Equal(MapFoliageStatus.CapacityExceeded, MapFoliageGenerationContext.CaptureEffective(f.Document, f.Assets, resources,
            new MapFoliageContextLimits(MaxPlacements: (int)demand.Placements - 1)).Status);
        FoliageFixture other = Grown(chunks: 2, placements: 4, paintTargets: 1);
        MapFoliageContextResult mismatched = MapFoliageGenerationContext.CaptureEffective(FoliageFixtures.Create().Document, f.Assets, resources);
        Assert.Equal(MapFoliageStatus.Invalid, mismatched.Status);
        Assert.Null(mismatched.Context);
        Assert.Equal(MapFoliageStatus.Invalid, MapFoliageGenerationContext.CaptureEffective(other.Document, other.Assets, resources).Status);
    }

    [Fact]
    public void ContextPaint_ReturnsOnlyRegionsTouchingTheScope()
    {
        PrefabFixture painted = PrefabFixtures.Create("uneven-painted");
        MapFoliageContextResult result = MapFoliageGenerationContext.Capture(painted.Document, painted.Assets);
        Assert.Equal(MapFoliageStatus.Complete, result.Status);
        MapFoliageGenerationContext context = result.Context!;
        MapPaintRegionSet all = context.Resources.PaintRegions["ground"];
        Assert.NotEmpty(all.Regions);
        var whole = new MapSurfaceScope(WorldFrame.Origin, new Vector2(-64, -64), new Vector2(64, 64), null, null,
            Enum.GetValues<MapSurfaceRole>(), null, new MapQueryLimits());
        IReadOnlyDictionary<string, MapPaintRegionSet> touching = context.PaintFor(whole);
        Assert.Equal(new[] { "ground" }, touching.Keys);
        Assert.Equal(all.Regions, touching["ground"].Regions);
        var far = whole with { LocalMin = new Vector2(500, 500), LocalMax = new Vector2(520, 520) };
        Assert.Empty(context.PaintFor(far));

        // One painted unit square: the subset holds exactly the regions whose exact bounds touch it, in published order.
        MapExactPoint first = all.Regions[0].Polygon[0];
        float x = first.X.ToSingle(), z = first.Z.ToSingle();
        var cell = whole with { LocalMin = new Vector2(x - 0.25f, z - 0.25f), LocalMax = new Vector2(x + 0.25f, z + 0.25f) };
        MapPaintRegionSet subset = context.PaintFor(cell)["ground"];
        Assert.Contains(all.Regions[0], subset.Regions);
        Assert.True(subset.Regions.Count <= all.Regions.Count);
        Assert.Equal(subset.Regions, all.Regions.Where(subset.Regions.Contains));
        Assert.Equal(subset.Digest, context.PaintFor(cell)["ground"].Digest);
    }

    [Fact]
    public void ContextSourceCharge_DensityTokensAreChargedOnceAndAFullPayloadFits()
    {
        // {"x":n,"z":n,"density":"..."}: 8 for the object, names x and z at 8 each, two numbers at 72 each, name density
        // at 44, then the escape-free token at 8 plus 1368 characters plus 2 quotes.
        Assert.Equal(8 + 8 + 72 + 8 + 72 + 44 + 8 + 1368 + 2, MapFoliageContextPreflight.ChunkBytes);
        Assert.Equal(1590, MapFoliageContextPreflight.ChunkBytes);
        FoliageFixture two = Grown(chunks: 2, placements: 0, paintTargets: 0), three = Grown(chunks: 3, placements: 0, paintTargets: 0);
        Assert.Equal(1590, MapFoliageContextPreflight.Measure(three.Document, three.Assets, new()).Work.SourceUtf8Bytes -
            MapFoliageContextPreflight.Measure(two.Document, two.Assets, new()).Work.SourceUtf8Bytes);

        // The document ceiling of 65,536 chunks spread over the maximum 256 layers, each with the maximum 256 models.
        // Chunks are advertised digest rows only, so no density byte is allocated.
        Assert.Equal(65_536, MapFoliagePayloadLimits.MaxDocumentChunks);
        var models = new JsonArray(Enumerable.Range(0, MapFoliagePayloadLimits.MaxModels)
            .Select(i => (JsonNode)new JsonObject { ["assetId"] = $"model-{i:000}", ["weight"] = 1 }).ToArray());
        MapFoliageLayer template = FoliageStorageFixtures.Change(FoliageFixtures.BasicLayer([]), l => l["models"] = models);
        int perLayer = MapFoliagePayloadLimits.MaxDocumentChunks / MapFoliagePayloadLimits.MaxLayers;
        MapFoliageChunkDigest[] rows = Enumerable.Range(0, perLayer)
            .Select(i => new MapFoliageChunkDigest(new(i % 16, i / 16), new string('a', 64))).ToArray();
        MapFoliageLayer[] layers = Enumerable.Range(0, MapFoliagePayloadLimits.MaxLayers)
            .Select(i => FoliageStorageFixtures.Change(template, l => l["id"] = $"layer-{i:000}")).ToArray();
        long full = MapFoliageContextPreflight.SourceBytes(new(1, layers.Select(l => MapFoliageLayer.FromStorage(l, rows, [])).ToArray()), long.MaxValue);
        long metadata = MapFoliageContextPreflight.SourceBytes(new(1, layers), long.MaxValue);
        Assert.Equal(65_536L * 1590, full - metadata);
        Assert.True(full <= new MapFoliageContextLimits().MaxSourceUtf8Bytes, $"{full} bytes exceed the default ceiling");
    }

    [Fact]
    public void SharedRootCopy_ExpandsAndEditsWithoutWholeFoliageWrites()
    {
        // Capture and a direct expansion.
        FoliageFixture f = FoliageFixtures.Create();
        MapFoliageSet set = f.Document.Foliage!;
        string before = MapDocumentFile.SaveText(f.Document);
        long writes = MapFoliageJson.ThreadPayloadWrites;
        MapFoliageContextResult captured = MapFoliageGenerationContext.Capture(f.Document, f.Assets);
        Assert.Equal((MapFoliageStatus.Complete, 1L, 0L), (captured.Status, captured.Work.Expansions, captured.Work.SnapshotSerializations));
        _ = MapPrefabResolver.Expand(f.Document, f.Assets);
        Assert.Equal(0, MapFoliageJson.ThreadPayloadWrites - writes);
        Assert.Same(set, f.Document.Foliage);
        Assert.Equal(before, MapDocumentFile.SaveText(f.Document));

        // Pure prefab and marker edits keep the immutable foliage set on their candidates without encoding it.
        PrefabFixture p = PrefabWithFoliage();
        MapFoliageSet prefabSet = p.Document.Foliage!;
        string prefabBefore = MapDocumentFile.SaveText(p.Document);
        writes = MapFoliageJson.ThreadPayloadWrites;
        MapDocument removed = MapPrefabEdits.Prepare(p.Document, p.Assets, new MapRemovePrefab("a")).After;
        MapDocument marked = MapMarkerEdits.Prepare(p.Document, p.Assets, new MapSetMarker(
            new MapLocalMarkerDoc(1, "fresh", "fresh-name", new Vector3(2, 3.5f, -4), 0, "entry-role", true, new[] { "new" }))).After;
        Assert.Equal(0, MapFoliageJson.ThreadPayloadWrites - writes);
        Assert.Same(prefabSet, removed.Foliage);
        Assert.Same(prefabSet, marked.Foliage);
        Assert.DoesNotContain(removed.Prefabs?.Instances ?? [], i => i.Id == "a" && !i.Removed);
        Assert.Contains(marked.BuildingIntent!.Markers, m => m.Id == "fresh");
        Assert.Same(prefabSet, p.Document.Foliage);
        Assert.Equal(prefabBefore, MapDocumentFile.SaveText(p.Document));
    }

    // The released hut prefab fixture with a foliage layer bound to its exterior floor and its verified resources.
    static PrefabFixture PrefabWithFoliage()
    {
        PrefabFixture p = PrefabFixtures.Create();
        p.Document.Foliage = new(1, [FoliageStorageFixtures.Change(FoliageFixtures.BasicLayer(), l =>
        {
            l["support"] = new JsonObject { ["kind"] = "Surface", ["surfaceId"] = "exterior" };
            l["compatibilityOrdinals"] = new JsonArray(new JsonObject { ["surfaceId"] = "exterior", ["ordinal"] = 0 });
            l["models"] = new JsonArray(new JsonObject { ["assetId"] = "item", ["weight"] = 1 });
            l["allowedVisibleMaterials"] = new JsonArray(10);
            l["materials"] = new JsonArray(new JsonObject { ["id"] = 10, ["resourceId"] = "material", ["kind"] = "Ordinary" });
        })]);
        Assert.Empty(MapFoliageValidation.ValidateBound(p.Document, p.Assets));
        return p;
    }

    // The basic fixture widened to 64 chunk columns, with a Solid rule on asset tree. Chunk columns [0, chunks) are
    // stored. Placements 0 and 1 stand inside chunk (0, 0), the rest far away. Each paint target is a distinct far
    // support floor painted by its own released paint override.
    static FoliageFixture Grown(int chunks, int placements, int paintTargets, int wide = 0)
    {
        FoliageFixture f = FoliageFixtures.Create();
        MapFoliageLayer layer = FoliageStorageFixtures.Change(FoliageFixtures.BasicLayer([]), l =>
        {
            l["width"] = 64 * MapFoliageChunk.Size;
            l["depth"] = MapFoliageChunk.Size;
            l["clearanceRules"] = new JsonArray(new JsonObject
            {
                ["id"] = "solid-tree",
                ["assetId"] = "tree",
                ["kind"] = "Solid",
                ["footprint"] = new JsonObject { ["minX"] = -1, ["minZ"] = -1, ["maxX"] = 1, ["maxZ"] = 1 },
            }, new JsonObject
            {
                ["id"] = "solid-wide",
                ["assetId"] = "reed-a",
                ["kind"] = "Solid",
                ["footprint"] = new JsonObject { ["minX"] = -200, ["minZ"] = -200, ["maxX"] = 200, ["maxZ"] = 200 },
            });
            l["chunks"] = new JsonArray(Enumerable.Range(0, chunks).Select(x => (JsonNode)new JsonObject
            {
                ["x"] = x,
                ["z"] = 0,
                ["density"] = Convert.ToBase64String(Enumerable.Repeat((byte)(x + 1), MapFoliageChunk.ByteLength).ToArray()),
            }).ToArray());
        });
        f.Document.Foliage = new(1, [layer]);
        for (int i = 0; i < placements; i++)
            f.Document.Placements.Add(new MapPlacement
            {
                Id = "tree-" + i,
                AssetId = "tree",
                Kind = "scenery",
                NumericId = MapNumericIds.Allocate(f.Document),
                X = i < 2 ? 4 + 6 * i : 3000 + 8 * i,
                Z = i < 2 ? 2 : 3000,
                Y = 1,
            });
        // Distant owners of 400 m footprints, each spanning more than the finest index level allows.
        for (int i = 0; i < wide; i++)
            f.Document.Placements.Add(new MapPlacement
            {
                Id = "wide-" + i,
                AssetId = "reed-a",
                Kind = "scenery",
                NumericId = MapNumericIds.Allocate(f.Document),
                X = 20000 + 512 * i,
                Z = 20000,
                Y = 1,
            });
        var added = new MapSurfaceSet();
        for (int k = 0; k < paintTargets; k++)
        {
            long x = 4096 + 64L * k;
            WaterFixtures.Surface(added, "far-" + k, MapSurfaceRole.SupportFloor, MapLatticeFrame.ImportedMetreCentimetre, x, 4096, x + 2, 4098, (_, _) => 0);
            WaterFixtures.Surface(added, "paint-" + k, MapSurfaceRole.PaintOverride, MapLatticeFrame.ImportedMetreCentimetre, x, 4096, x + 2, 4098, (_, _) => 0);
            added.Refs[^1] = added.Refs[^1] with { PaintTargetSurfaceId = "far-" + k };
        }
        foreach (var patch in added.Patches) f.Document.Surfaces.Patches.Add(patch.Key, patch.Value);
        foreach (MapSurfaceRef surface in added.Refs)
            f.Document.Surfaces.Refs.Add(surface with
            {
                SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface, added.Patches.Where(p => p.Key.SurfaceId == surface.Id)
                    .Select(p => new KeyValuePair<MapPatchKey, string>(p.Key, MapSurfaceSemantics.PatchDigest(p.Value)))),
            });
        return f;
    }

    // Every chunk-phase observation for the fixed request, taken twice to prove the context holds no call state.
    static string ChunkWork(MapFoliageGenerationContext context, out long expansions, out long writes)
    {
        long expansions0 = MapPrefabResolver.ThreadExpansions, writes0 = MapFoliageJson.ThreadPayloadWrites;
        var rows = new List<string>();
        for (int repeat = 0; repeat < 2; repeat++)
        {
            MapScopedSurfaces view = context.Acquire(NearScope());
            long faces = view.Witness.Present.Sum(p => (long)MapSurfaceCompiler.Compile(view.Surfaces.Single(s => s.Id == p.Key.SurfaceId),
                view.Patch(p.Key).Patch!).Faces.Count);
            IReadOnlyList<MapResolvedPlacement> owners = context.PlacementsIn(Near, out long candidates);
            IReadOnlyDictionary<string, MapPaintRegionSet> paint = context.PaintFor(NearScope());
            rows.Add(string.Join(" | ", view.Status, view.Witness.Present.Count, string.Join(",", view.Witness.Present.Select(p => p.Key.ToString())),
                faces, candidates, string.Join(",", owners.Select(p => p.PlacementId)),
                string.Join(",", paint.Select(p => p.Key + ":" + p.Value.Digest))));
        }
        Assert.Equal(rows[0], rows[1]);
        expansions = MapPrefabResolver.ThreadExpansions - expansions0;
        writes = MapFoliageJson.ThreadPayloadWrites - writes0;
        return rows[0];
    }
}
