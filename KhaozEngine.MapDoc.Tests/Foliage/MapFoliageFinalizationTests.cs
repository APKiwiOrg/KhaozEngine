using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

/// <summary>The public document finalizer: pure dependent edits publish the batch rows Generate would publish on each
/// side, keep their own effects and writes, and refuse atomically.</summary>
public sealed class MapFoliageFinalizationTests
{
    const string Id = FoliageFixtures.DependentLayerId;
    static readonly MapFoliageChunkKey Home = new(0, 0);
    const MapNativeInvalidation Geometry = MapNativeInvalidation.Placements | MapNativeInvalidation.Physics |
        MapNativeInvalidation.Nav | MapNativeInvalidation.Residency;

    [Fact]
    public void PureDependentEdits_IncludeFoliageEffects()
    {
        FoliageFixture f = FoliageFixtures.Dependents();
        FoliageFixture control = FoliageFixtures.Dependents(foliage: false);
        string original = MapDocumentFile.SaveText(f.Document);

        // A raw unframed standalone move, its placement writes and its base bounds, finalized through the public boundary.
        MapDocument candidate = Detached(f.Document);
        candidate.Placements.Single(p => p.Id == FoliageFixtures.ShrubId).X = 8;
        var baseEffects = new MapNativeEditEffects(new MapBox3(9.5, -2.5, -10.5, 10.5, -1.5, -9.5), new MapBox3(7.5, -2.5, -10.5, 8.5, -1.5, -9.5),
            [], [], [], [], Geometry);
        MapNativeInvalidation baseGeometryFlags = baseEffects.Invalidates;
        MapPrefabEditResult finalized = MapDocumentEditFinalizer.Finalize(f.Document,
            new(candidate, MapNativeWriteSet.PlacementsOnly, baseEffects), f.Assets);
        MapFoliageChunkChange moved = Assert.Single(finalized.Effects.FoliageChunks);
        Assert.Equal((Id, Home, new MapFoliageRect(-16, -16, 16, 16)), (moved.LayerId, moved.Chunk, moved.OldBounds!.Value));
        Assert.Equal(moved.OldBounds, moved.NewBounds);
        Assert.NotEqual(moved.BeforeDigest, moved.AfterDigest);
        AssertExact(f, new(finalized.After, f.Assets), finalized.Effects.FoliageChunks);
        Assert.Equal(baseGeometryFlags, finalized.Effects.Invalidates & ~MapNativeInvalidation.Foliage);
        Assert.True(finalized.Effects.Invalidates.HasFlag(MapNativeInvalidation.Foliage));
        Assert.Equal((baseEffects.OldBounds, baseEffects.NewBounds), (finalized.Effects.OldBounds, finalized.Effects.NewBounds));
        Assert.True(finalized.WriteSet.Placements);
        Assert.False(finalized.WriteSet.Foliage);
        Assert.Contains("\"foliageChunks\":[{\"layerId\":\"cover-d\",\"x\":0,\"z\":0,", finalized.Effects.Describe());

        // Finalizing again reuses the carried sides and returns the same rows, with no capture and no expansion.
        long captures = MapFoliageGenerationContext.ThreadCaptures, expansions = MapPrefabResolver.ThreadExpansions;
        MapPrefabEditResult finalizedAgain = MapDocumentEditFinalizer.Finalize(f.Document, finalized, f.Assets);
        Assert.Equal((0L, 0L), (MapFoliageGenerationContext.ThreadCaptures - captures, MapPrefabResolver.ThreadExpansions - expansions));
        Assert.Equal(finalized.Effects.FoliageChunks, finalizedAgain.Effects.FoliageChunks);
        Assert.Equal(finalized.Effects.Describe(), finalizedAgain.Effects.Describe());

        // Room, roof-link and marker edits are finalized by their own public preparation and keep every released member.
        MapPrismDomain room = FoliageFixtures.DependentRoom(f.Document);
        MapLocalMarkerDoc post = FoliageFixtures.DependentMarker(f.Document, FoliageFixtures.PostId);
        MapLocalMarkerDoc far = FoliageFixtures.DependentMarker(f.Document, FoliageFixtures.FarId);
        var edits = new (string Name, Func<FoliageFixture, MapPrefabEditResult> Prepare, bool Foliage)[]
        {
            ("room", x => MapInteriorEdits.Prepare(x.Document, x.Assets, new MapSetInterior(room with { UpperY = 2.5f })), true),
            ("roof", x => MapInteriorEdits.Prepare(x.Document, x.Assets, new MapSetRoofLink(FoliageFixtures.DependentRoofLink())), false),
            ("post", x => MapMarkerEdits.Prepare(x.Document, x.Assets, new MapSetMarker(FoliageFixtures.MovedMarker(post, 4))), true),
            ("far", x => MapMarkerEdits.Prepare(x.Document, x.Assets, new MapSetMarker(FoliageFixtures.MovedMarker(far, 13))), false),
        };
        foreach (var (name, prepare, changes) in edits)
        {
            MapPrefabEditResult result = prepare(f);
            MapPrefabEditResult released = prepare(control);
            Assert.Empty(released.Effects.FoliageChunks);
            Assert.DoesNotContain("foliageChunks", released.Effects.Describe());
            Assert.False(released.WriteSet.Foliage);
            // Stripping the foliage channel leaves exactly the released effects and writes.
            Assert.Equal(released.Effects.Describe(), (result.Effects with
            {
                FoliageChunks = [],
                Invalidates = result.Effects.Invalidates & ~MapNativeInvalidation.Foliage,
            }).Describe());
            Assert.Equal(Writes(released.WriteSet), Writes(result.WriteSet));
            Assert.False(result.WriteSet.Foliage);
            Assert.Equal(changes, result.Effects.Invalidates.HasFlag(MapNativeInvalidation.Foliage));
            // A room is read by every batch whose scope acquires its anchor's slot, so it can reach past the home batch.
            if (changes) Assert.Contains(result.Effects.FoliageChunks, r => r.LayerId == Id && r.Chunk == Home);
            else Assert.Empty(result.Effects.FoliageChunks);
            AssertExact(f, new(result.After, f.Assets), result.Effects.FoliageChunks, name);
            Assert.Empty(result.Effects.FoliageLayers);
        }
        Assert.Equal(original, MapDocumentFile.SaveText(f.Document));
    }

    [Fact]
    public void ConsultedRowsSequencesAndTerrain_FinalizeOnlyDependentBatches()
    {
        FoliageFixture f = FoliageFixtures.Dependents();
        MapFoliageLayer layer = f.Document.Foliage!.Layers[0];

        // A consulted compatibility row and a newly mapped surface change only the batches that consult them.
        foreach (MapFoliageCompatibilityOrdinal[] ordinals in new[] { new MapFoliageCompatibilityOrdinal[] { new("exterior", 1) },
            [new("exterior", 0), new("a/floor", 0)] })
        {
            MapPrefabEditResult changed = MapFoliageEdits.Prepare(f.Document, f.Assets,
                new MapSetFoliageLayer(FoliageFixtures.ClearanceLayer(layer, ordinals: ordinals)));
            Assert.True(changed.WriteSet.Foliage);
            Assert.All(changed.Effects.FoliageChunks, r => Assert.Equal(Home, r.Chunk));
            Assert.Empty(changed.Effects.FoliageLayers);
            AssertExact(f, new(changed.After, f.Assets), changed.Effects.FoliageChunks);
        }

        // A terrain edit under the home batch, through the complete document wrapper, keeps the surface effects.
        var heights = new MapSetCornerHeights(new("exterior", 0, 0), 9, 9, 2, 2, [-150, -150, -150, -150]);
        MapTerrainEditResult surfaces = MapTerrainEdits.Prepare(f.Document.Surfaces, heights);
        MapPrefabEditResult terrain = MapDocumentTerrainEdits.Prepare(f.Document, f.Assets, heights);
        Assert.Equal(surfaces.Effects.Invalidates, terrain.Effects.Invalidates & ~MapNativeInvalidation.Foliage);
        // The whole patch's digest is a dependency, so every batch whose scope acquires the patch reports.
        Assert.Contains(terrain.Effects.FoliageChunks, r => r.Chunk == Home);
        AssertExact(f, new(terrain.After, f.Assets), terrain.Effects.FoliageChunks);

        // Removing an owner alone leaves dangling foliage owners. One sequence removes it with its rows and finalizes once.
        FoliageFixture owned = FoliageFixtures.OwnedByInstance();
        string ownedState = MapDocumentFile.SaveText(owned.Document);
        Assert.Contains("p/prop", Assert.Throws<MapDocumentException>(() =>
            MapPrefabEdits.Prepare(owned.Document, owned.Assets, new MapRemovePrefab("p"))).Message, StringComparison.Ordinal);
        long captures = MapFoliageGenerationContext.ThreadCaptures;
        MapPrefabEditResult sequence = MapFoliageEdits.Prepare(owned.Document, owned.Assets, new MapFoliageEditSequence(
            [new MapSetFoliageLayer(FoliageFixtures.WithoutInstanceOwner(owned))], new MapRemovePrefab("p")));
        Assert.Equal(2, MapFoliageGenerationContext.ThreadCaptures - captures);
        Assert.True(sequence.WriteSet.Prefabs && sequence.WriteSet.Foliage);
        Assert.True(sequence.Effects.Invalidates.HasFlag(MapNativeInvalidation.Placements | MapNativeInvalidation.Foliage));
        Assert.NotEmpty(sequence.Effects.FoliageChunks);
        AssertExact(owned, new(sequence.After, owned.Assets), sequence.Effects.FoliageChunks);
        Assert.Equal(ownedState, MapDocumentFile.SaveText(owned.Document));

        // Lost support refuses even though other floors cover the same cells, and writes nothing.
        FoliageFixture clearance = FoliageFixtures.Clearance("solid");
        clearance.Document.Placements[0].SupportBinding = FoliageFixtures.Surface("peer-seat");
        string before = MapDocumentFile.SaveText(clearance.Document);
        var lost = new MapReplaceTopology([], clearance.Document.Surfaces.Patches.Keys.Where(k => k.SurfaceId == "floor-1").ToArray(), [], ["floor-1"]);
        var error = Assert.Throws<MapDocumentException>(() => MapDocumentTerrainEdits.Prepare(clearance.Document, clearance.Assets, lost));
        Assert.Contains("floor-1", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, MapDocumentFile.SaveText(clearance.Document));
    }

    [Fact]
    public void Finalizer_RefusesOverBudgetAndStaleContextsBeforeAnyResult()
    {
        FoliageFixture f = FoliageFixtures.Dependents();
        MapFoliageGenerationContext context = FoliageFixtures.Context(f);
        MapDocument candidate = Detached(f.Document);
        candidate.Placements.Single(p => p.Id == FoliageFixtures.ShrubId).X = 8;
        MapFoliageGenerationContext moved = FoliageFixtures.Context(new(candidate, f.Assets));
        var trigger = new MapNativeEditEffects(null, null, [], [], [], [], MapNativeInvalidation.None);
        IReadOnlyList<MapFoliageChunkChange> rows = MapFoliageEffects.Describe(f.Document, candidate, context, moved, trigger);
        Assert.Equal(Home, Assert.Single(rows).Chunk);
        // A context captured from another snapshot never stands in for the document's own, whether its foliage differs
        // or matches, as the before context of a moved placement does.
        Assert.Throws<ArgumentException>(() => MapFoliageEffects.Describe(f.Document, candidate, context, FoliageFixtures.Context(
            new(MapFoliageEdits.Prepare(candidate, f.Assets, new MapPaintFoliage(Id, 1, 1, 2, 9, 1)).After, f.Assets)), trigger));
        Assert.Throws<ArgumentException>(() => MapFoliageEffects.Describe(f.Document, candidate, context, context, trigger));
        Assert.Throws<ArgumentException>(() => MapFoliageEffects.Describe(f.Document, candidate, moved, moved, trigger));
        // A snapshot expanded from another document is refused as a capture source.
        MapEffectiveResources movedResources = moved.Resources;
        Assert.Equal(MapFoliageStatus.Invalid, MapFoliageGenerationContext.CaptureEffective(f.Document, f.Assets, movedResources).Status);
        Assert.Equal(MapFoliageStatus.Complete, MapFoliageGenerationContext.CaptureEffective(candidate, f.Assets, movedResources).Status);

        // More than the default 4096 paint targets refuses every public preparation before any copy or expansion.
        FoliageFixture grown = FoliageFixtures.PaintTargets(4097);
        string state = MapDocumentFile.SaveText(grown.Document);
        MapAssetClosure assets = grown.Assets;
        MapDocument document = grown.Document;
        var entries = new Action[]
        {
            () => MapFoliageEdits.Prepare(document, assets, new MapPaintFoliage(FoliageFixtures.LayerId, 1, -1, 2, 9, 1)),
            () => MapFoliageEdits.Prepare(document, assets, new MapFoliageEditSequence([], new MapRemovePrefab("missing"))),
            () => MapPrefabEdits.Prepare(document, assets, new MapRemovePrefab("missing")),
            () => MapPrefabEdits.PrepareAssetUpdate(document, assets, assets, new MapUpdatePrefabAsset("hut", "old", "hut", "new")),
            () => MapInteriorEdits.Prepare(document, assets, new MapRemoveInterior("missing")),
            () => MapMarkerEdits.Prepare(document, assets, new MapRemoveMarker("missing")),
            () => MapDocumentTerrainEdits.Prepare(document, assets, new MapSetCornerHeights(new(FoliageFixtures.FloorId, 0, 0), 0, 0, 1, 1, [120])),
        };
        foreach (Action entry in entries)
        {
            long expansions = MapPrefabResolver.ThreadExpansions, copies = MapPrefabEditPreparation.ThreadCopies;
            Assert.StartsWith("Foliage CapacityExceeded", Assert.Throws<MapDocumentException>(entry).Message, StringComparison.Ordinal);
            Assert.Equal((0L, 0L), (MapPrefabResolver.ThreadExpansions - expansions, MapPrefabEditPreparation.ThreadCopies - copies));
        }
        Assert.Equal(state, MapDocumentFile.SaveText(grown.Document));
    }

    [Fact]
    public void LayerEntries_PublishWholeLayerChangesAndDegradeOverCeiling()
    {
        FoliageFixture f = FoliageFixtures.Dependents();
        MapFoliageLayer layer = f.Document.Foliage!.Layers[0];

        // Whole-layer members publish one entry and no rows, with no preparation.
        foreach (MapFoliageLayer changed in new[]
        {
            FoliageStorageFixtures.Change(layer, l => l["seed"] = 18),
            FoliageFixtures.ClearanceLayer(layer, rules: [new("shrub-solid", "item", "shrub", MapFoliageClearanceKind.Solid, new(-1, -1, 1, 1))]),
        })
        {
            long captures = MapFoliageGenerationContext.ThreadCaptures;
            MapPrefabEditResult whole = MapFoliageEdits.Prepare(f.Document, f.Assets, new MapSetFoliageLayer(changed));
            Assert.Equal(0, MapFoliageGenerationContext.ThreadCaptures - captures);
            Assert.Empty(whole.Effects.FoliageChunks);
            MapFoliageLayerChange entry = Assert.Single(whole.Effects.FoliageLayers);
            Assert.Equal(new MapFoliageLayerChange(Id, MapFoliageDependents.LayerDigest(layer), MapFoliageDependents.LayerDigest(whole.After.Foliage!.Layers[0])), entry);
            Assert.Equal(MapNativeInvalidation.Foliage, whole.Effects.Invalidates);
        }

        // A mask change is localized to the batches its old and new footprints reach.
        var widerMask = new MapSetFoliageLayer(FoliageFixtures.ClearanceLayer(layer,
            masks: [new("post-mask", FoliageFixtures.Owner(FoliageFixtures.PostId, MapPrefabChildKind.Marker), FoliageFixtures.Surface("exterior"),
                MapFoliageClearanceKind.Solid, new(-2, -2, 2, 2))]));
        MapPrefabEditResult mask = MapFoliageEdits.Prepare(f.Document, f.Assets, widerMask);
        Assert.Empty(mask.Effects.FoliageLayers);
        Assert.Equal(Home, Assert.Single(mask.Effects.FoliageChunks).Chunk);
        AssertExact(f, new(mask.After, f.Assets), mask.Effects.FoliageChunks);

        // A layer with no stored chunks on either side publishes nothing, though the root is written.
        var bare = new MapSetFoliageLayer(FoliageStorageFixtures.Change(layer, l => { l["id"] = "bare"; l["chunks"] = new System.Text.Json.Nodes.JsonArray(); }));
        MapPrefabEditResult added = MapFoliageEdits.Prepare(f.Document, f.Assets, bare);
        Assert.True(added.WriteSet.Foliage);
        Assert.Equal((0, 0, MapNativeInvalidation.None), (added.Effects.FoliageChunks.Count, added.Effects.FoliageLayers.Count, added.Effects.Invalidates));
        MapPrefabEditResult reseeded = MapFoliageEdits.Prepare(added.After, f.Assets,
            new MapSetFoliageLayer(FoliageStorageFixtures.Change(added.After.Foliage!.Layers[1], l => l["seed"] = 99)));
        Assert.Equal((0, 0, MapNativeInvalidation.None), (reseeded.Effects.FoliageChunks.Count, reseeded.Effects.FoliageLayers.Count, reseeded.Effects.Invalidates));

        // A selection past a per-edit ceiling degrades its layer to an entry and never refuses.
        MapFoliageGenerationContext before = FoliageFixtures.Context(f), after = FoliageFixtures.Context(new(mask.After, f.Assets));
        var (rows, layers) = MapFoliageDependents.Describe(f.Document, mask.After, () => before, () => after, limits: new(MaxPreparedSides: 1));
        Assert.Empty(rows);
        Assert.Equal(new MapFoliageLayerChange(Id, MapFoliageDependents.LayerDigest(layer), MapFoliageDependents.LayerDigest(mask.After.Foliage!.Layers[0])),
            Assert.Single(layers));
        (rows, layers) = MapFoliageDependents.Describe(f.Document, mask.After, () => before, () => after, limits: new(MaxScannedBatches: 0));
        Assert.Empty(rows);
        Assert.Single(layers);
        (rows, layers) = MapFoliageDependents.Describe(f.Document, mask.After, () => before, () => after, limits: new(MaxPreparedSides: 2));
        Assert.Equal(mask.Effects.FoliageChunks, rows);
        Assert.Empty(layers);

        // A correspondence rewritten by a placement rename publishes only that owner's rows.
        FoliageFixture renamable = FoliageFixtures.Renamable();
        MapDocument renamed = MapPrefabReferences.RemapWorld(Detached(renamable.Document), MapPrefabChildKind.Placement, "prop", "q");
        renamed.Placements.Single(p => p.Id == "prop").Id = "q";
        MapPrefabEditResult rename = MapDocumentEditFinalizer.Finalize(renamable.Document,
            new(renamed, MapNativeWriteSet.PlacementsOnly, new(null, null, [], [], [], [], MapNativeInvalidation.Placements)), renamable.Assets);
        Assert.True(rename.WriteSet.Foliage);
        Assert.Empty(rename.Effects.FoliageLayers);
        Assert.NotEmpty(rename.Effects.FoliageChunks);
        AssertExact(renamable, new(rename.After, renamable.Assets), rename.Effects.FoliageChunks);
    }

    [Fact]
    public void BatchCapacityRefusal_PublishesRefusalDigestAndSucceeds()
    {
        // 4097 clearing owners inside the first batch exceed its 4096 rule candidates, a deterministic refused side.
        FoliageFixture f = FoliageFixtures.Create();
        for (int i = 0; i < 4097; i++)
            f.Document.Placements.Add(new MapPlacement
            {
                Id = "crowd-" + i,
                Kind = "tree",
                AssetId = "tree",
                NumericId = MapNumericIds.Allocate(f.Document),
                X = 1 + (i % 64) * 0.5f,
                Y = 1,
                Z = -4 + (i / 64) * 0.125f,
                Scale = 1,
            });
        f.Document.Foliage = new(1, [FoliageFixtures.ClearanceLayer(f.Document.Foliage!.Layers[0],
            rules: [new("crowd", "tree", null, MapFoliageClearanceKind.Solid, new(-0.1f, -0.1f, 0.1f, 0.1f))])]);
        MapFoliageGenerationContext context = FoliageFixtures.Context(f);
        Assert.Equal(MapFoliageStatus.CapacityExceeded, MapFoliageDistribution.Generate(context, FoliageFixtures.LayerId, new(0, 0)).Status);
        MapPrefabEditResult painted = MapFoliageEdits.Prepare(f.Document, f.Assets, new MapPaintFoliage(FoliageFixtures.LayerId, 1, -1, 2, 9, 1));
        MapFoliageChunkChange row = Assert.Single(painted.Effects.FoliageChunks);
        Assert.Equal(new MapFoliageChunkKey(0, 0), row.Chunk);
        Assert.True(row.BeforeDigest is { Length: 64 } && row.AfterDigest is { Length: 64 } && row.BeforeDigest != row.AfterDigest);
        Assert.Empty(painted.Effects.FoliageLayers);
    }

    static MapDocument Detached(MapDocument document) => MapDocumentFile.LoadText(MapDocumentFile.SaveText(document));

    static string Writes(MapNativeWriteSet w) => string.Join("|", string.Join(",", w.Patches), string.Join(",", w.SurfaceIds),
        string.Join(",", w.RecordIds), string.Join(",", w.SpaceIds), w.OwnerChanges.Count, string.Join(",", w.Materials), w.Placements,
        w.NativeAssets, w.Water, w.Foliage, w.Prefabs, w.BuildingIntent, w.NativeResourceTransforms, w.NumericReservations, w.LegacySpawns);

    // The rows are exactly the existing batches whose Generate digest differs, with those digests on each side.
    static void AssertExact(FoliageFixture before, FoliageFixture after, IReadOnlyList<MapFoliageChunkChange> rows, string name = "")
    {
        Dictionary<(string, MapFoliageChunkKey), string> a = Digests(before), b = Digests(after);
        var expected = a.Keys.Union(b.Keys).Where(k => a.GetValueOrDefault(k) != b.GetValueOrDefault(k))
            .OrderBy(k => k.Item1, StringComparer.Ordinal).ThenBy(k => k.Item2.Z).ThenBy(k => k.Item2.X).ToArray();
        var actual = rows.Select(r => (r.LayerId, r.Chunk)).ToArray();
        if (!expected.SequenceEqual(actual)) Assert.Fail($"{name}: expected rows {string.Join(" ", expected)} but published {string.Join(" ", actual)}");
        foreach (MapFoliageChunkChange row in rows)
            Assert.Equal((a.GetValueOrDefault((row.LayerId, row.Chunk)), b.GetValueOrDefault((row.LayerId, row.Chunk))), (row.BeforeDigest, row.AfterDigest));
    }

    static Dictionary<(string, MapFoliageChunkKey), string> Digests(FoliageFixture fixture)
    {
        var digests = new Dictionary<(string, MapFoliageChunkKey), string>();
        if (fixture.Document.Foliage is null) return digests;
        MapFoliageGenerationContext context = FoliageFixtures.Context(fixture);
        foreach (MapFoliageLayer layer in context.Foliage!.Layers)
            foreach (MapFoliageChunkKey key in MapFoliageDependents.Present(layer))
            {
                MapFoliageSampleBatch batch = MapFoliageDistribution.Generate(context, layer.Id, key);
                Assert.Equal(MapFoliageStatus.Complete, batch.Status);
                digests.Add((layer.Id, key), batch.DependencyDigest);
            }
        return digests;
    }
}
