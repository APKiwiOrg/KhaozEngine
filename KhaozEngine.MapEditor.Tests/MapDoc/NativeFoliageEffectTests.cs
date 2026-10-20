using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.MapEdit;
using KhaozEngine.MapEditor;
using KhaozEngine.Tests.MapDoc.Foliage;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

/// <summary>Every kind of native transaction a foliage batch can depend on publishes exactly the old and new batch rows
/// the public effects boundary computes from fresh contexts, beside its released effects.</summary>
public sealed class NativeFoliageEffectTests
{
    [Fact]
    public void EveryDependencyTransaction_ReportsOldAndNewFoliageChunks()
    {
        FoliageFixture f = FoliageFixtures.Dependents();
        MapEditSession session = FoliageEditorFixtures.Session(f);
        MapEditSession control = FoliageEditorFixtures.Session(FoliageFixtures.Dependents(foliage: false));
        try
        {
            var steps = Transactions(f, control: false).ToArray();
            var plain = Transactions(f, control: true).ToArray();
            for (int i = 0; i < steps.Length; i++)
            {
                var (name, command, dependent) = steps[i];
                EditorCommand? released = i < plain.Length ? plain[i].Command : null;
                FoliageFixture before = FoliageEditorFixtures.Fixture(session);
                MapNativeEditEffects effects = session.ExecuteNativeCommand(command);
                FoliageFixture after = FoliageEditorFixtures.Fixture(session);
                string expected = FoliageEditorFixtures.Expected(before.Document, after.Document, before.Assets);
                Assert.Equal(name + expected, name + FoliageEditorFixtures.Channel(effects));
                bool reports = effects.FoliageChunks.Count + effects.FoliageLayers.Count != 0;
                Assert.Equal(reports, effects.Invalidates.HasFlag(MapNativeInvalidation.Foliage));
                if (dependent) Assert.True(reports, name + " reports its dependent batches");
                // Seed and rule are whole-layer members and publish one entry. Every other edit publishes rows only.
                Assert.Equal(name + (name is "seed" or "rule" ? 1 : 0), name + effects.FoliageLayers.Count);
                if (name is "seed" or "rule") Assert.Empty(effects.FoliageChunks);
                if (name == "move") AssertGenerated(before, after, effects);
                if (released is null)
                {
                    // Density, seed, rule and mask edits add Foliage alone, and foliage bounds never become world bounds.
                    Assert.Equal(MapNativeInvalidation.Foliage, effects.Invalidates);
                    Assert.Equal((null, null, null, null), (effects.OldBounds, effects.NewBounds, effects.OldSpatialBounds, effects.NewSpatialBounds));
                }
                else
                {
                    // Every other member and flag is exactly what the same transaction publishes without foliage.
                    MapNativeEditEffects withoutFoliage = control.ExecuteNativeCommand(released);
                    Assert.Equal(name + withoutFoliage.Describe(), name + (effects with
                    {
                        FoliageChunks = [],
                        FoliageLayers = [],
                        Invalidates = effects.Invalidates & ~MapNativeInvalidation.Foliage,
                    }).Describe());
                }
                Assert.Equal(FoliageEditorFixtures.Reversed(effects), FoliageEditorFixtures.Channel(session.UndoNative()!));
                Assert.Equal(FoliageEditorFixtures.Channel(effects), FoliageEditorFixtures.Channel(session.RedoNative()!));
                Assert.Equal(MapDocumentFile.SaveText(after.Document), FoliageEditorFixtures.Bytes(session));
            }
        }
        finally
        {
            FoliageEditorFixtures.Delete(session);
            FoliageEditorFixtures.Delete(control);
        }
    }

    [Fact]
    public void TerrainTransaction_PureAndEditorAgreeOnReseatAndWater()
    {
        FoliageFixture f = ReseatOverWater();
        var edit = new MapSetCornerHeights(new("exterior", 0, 0), 0, 0, 4, 4, Enumerable.Repeat(-250, 16).ToArray());
        MapEffectiveTransform Pose(MapDocument d) =>
            d.NativeResourceTransforms!.Single(t => t.Kind == MapPrefabChildKind.Placement && t.OwnerId == "a/item").Transform;
        MapPrefabEditResult pure = MapDocumentTerrainEdits.Prepare(f.Document, f.Assets, edit);

        // The editor command's preparation is the same document transaction: candidate bytes, writes and effects.
        var (gui, history) = FoliageEditorFixtures.Gui(f);
        MapDocument candidate = NativeDocumentSnapshot.Clone(gui, MapDocRegistry.CreateDefault());
        NativeDocumentPreparation prepared = new TerrainEditCommand(edit).Prepare(candidate, false, f.Assets);
        Assert.Equal(MapDocumentFile.SaveText(pure.After), MapDocumentFile.SaveText(candidate));
        Assert.Equal(pure.Effects.Describe(), prepared.Effects.Describe());
        Assert.Equal(Writes(pure.WriteSet), Writes(prepared.WriteSet));

        // The published editor transaction re-seats the Y-null framed placement and names the water body identically.
        history.Execute(gui, new TerrainEditCommand(edit));
        MapNativeEditEffects published = history.LastNativeEffects!;
        Assert.Equal(MapDocumentFile.SaveText(pure.After), MapDocumentFile.SaveText(gui));
        Assert.NotEqual(Pose(f.Document).Y, Pose(pure.After).Y);
        Assert.Equal(Pose(pure.After), Pose(gui));
        Assert.Equal(new[] { "pond" }, pure.Effects.WaterBodyIds);
        Assert.Equal(pure.Effects.WaterBodyIds, published.WaterBodyIds);
        Assert.True(pure.Effects.Invalidates.HasFlag(MapNativeInvalidation.Medium) && published.Invalidates.HasFlag(MapNativeInvalidation.Medium));
        Assert.True(pure.WriteSet.NativeResourceTransforms && prepared.WriteSet.NativeResourceTransforms);
    }

    // The unpacked hut with its item made a Y-null framed placement seated on the exterior floor, and a pond body covering
    // the exterior space on the same patch.
    static FoliageFixture ReseatOverWater()
    {
        PrefabFixture f = PrefabFixtures.Create();
        MapDocument doc = MapPrefabEdits.Prepare(f.Document, f.Assets, new MapUnpackPrefab("a")).After;
        MapPlacement item = doc.Placements.Single(p => p.Id == "a/item");
        item.Y = null;
        item.SupportBinding = FoliageFixtures.Surface("exterior");
        MapPlacementSeats.Reseat(doc, f.Assets);
        var water = new MapWaterSet();
        water.Media.Add(new MapWaterMediumDoc("pond-water", MapWaterMediumKind.Water, 1f));
        water.Bodies.Add(new MapWaterBodyDoc("pond", -1.5f, "pond-water", "material",
            new MapWaterDomainDoc(MapLatticeFrame.ImportedMetreCentimetre, new[] { new MapWaterRect(8, 8, 12, 12) }, Array.Empty<MapWaterRing>()),
            new[] { new MapRecordRef("exterior", new("exterior", 0, 0)) }));
        doc.Water = water;
        MapBoundDocumentValidation.Validate(doc, f.Assets);
        return new(doc, f.Assets);
    }

    // The move's rows are exactly the batches whose independent Generate digests differ, with those digests.
    static void AssertGenerated(FoliageFixture before, FoliageFixture after, MapNativeEditEffects effects)
    {
        Dictionary<MapFoliageChunkKey, string> a = Generated(before), b = Generated(after);
        var expected = a.Keys.Where(k => a[k] != b[k]).OrderBy(k => k.Z).ThenBy(k => k.X).Select(k => (k, a[k], b[k])).ToArray();
        Assert.NotEmpty(expected);
        Assert.Equal(expected, effects.FoliageChunks.Select(r => (r.Chunk, r.BeforeDigest!, r.AfterDigest!)).ToArray());
    }

    static Dictionary<MapFoliageChunkKey, string> Generated(FoliageFixture fixture)
    {
        MapFoliageGenerationContext context = FoliageFixtures.Context(fixture);
        var digests = new Dictionary<MapFoliageChunkKey, string>();
        for (int z = 0; z <= 1; z++)
            for (int x = 0; x <= 1; x++)
            {
                MapFoliageSampleBatch batch = MapFoliageDistribution.Generate(context, FoliageFixtures.DependentLayerId, new(x, z));
                Assert.Equal(MapFoliageStatus.Complete, batch.Status);
                digests.Add(new(x, z), batch.DependencyDigest);
            }
        return digests;
    }

    static string Writes(MapNativeWriteSet w) => string.Join("|", string.Join(",", w.Patches), string.Join(",", w.SurfaceIds),
        string.Join(",", w.RecordIds), string.Join(",", w.SpaceIds), w.OwnerChanges.Count, string.Join(",", w.Materials), w.Placements,
        w.NativeAssets, w.Water, w.Foliage, w.Prefabs, w.BuildingIntent, w.NativeResourceTransforms, w.NumericReservations, w.LegacySpawns);

    // Each transaction with its name, its command and whether it must report a dependent batch. The control sequence is
    // the same commands over the fixture without foliage, and has no foliage commands.
    static IEnumerable<(string Name, EditorCommand Command, bool Dependent)> Transactions(FoliageFixture f, bool control)
    {
        MapLocalMarkerDoc post = FoliageFixtures.DependentMarker(f.Document, FoliageFixtures.PostId);
        MapLocalMarkerDoc far = FoliageFixtures.DependentMarker(f.Document, FoliageFixtures.FarId);
        MapFoliageLayer layer = f.Document.Foliage!.Layers[0];
        var shrub = new MapPlacement
        {
            Id = "shrub-2",
            Kind = "item",
            AssetId = "item",
            X = 4,
            Z = -12,
            Scale = 1,
            Tags = ["shrub"],
            SupportBinding = FoliageFixtures.Surface("exterior"),
        };
        MapPrefabAsset hut = f.Assets.GetPrefab("hut");
        var place = new MapPlacePrefab("b", "hut", new(new(8, 0, 0), 0, 1), new("exterior", new("exterior", 0, 0)))
        { InitialOverrides = PrefabFixtures.MarkerNames(hut, "b") };
        yield return ("add", new AddPlacementCommand(shrub), true);
        yield return ("move", new MovePlacementCommand(FoliageFixtures.ShrubId, 8, -10, null), true);
        yield return ("rotate", new RotatePlacementCommand(FoliageFixtures.ShrubId, 0.5f), true);
        yield return ("remove", new RemovePlacementCommand("shrub-2"), true);
        yield return ("room", new InteriorEditCommand(new MapSetInterior(FoliageFixtures.DependentRoom(f.Document) with { UpperY = 2.5f })), true);
        yield return ("roof", new InteriorEditCommand(new MapSetRoofLink(FoliageFixtures.DependentRoofLink())), false);
        yield return ("marker", new MarkerEditCommand(new MapSetMarker(FoliageFixtures.MovedMarker(post, 4))), true);
        yield return ("unrelated marker", new MarkerEditCommand(new MapSetMarker(FoliageFixtures.MovedMarker(far, 13))), false);
        yield return ("terrain", new TerrainEditCommand(new MapSetCornerHeights(new("exterior", 0, 0), 9, 9, 2, 2, [-150, -150, -150, -150])), true);
        yield return ("prefab", new PrefabEditCommand(place), true);
        if (control) yield break;
        yield return ("density", new FoliageEditCommand(new MapPaintFoliage(layer.Id, 20, 20, 2, 9, 1)), true);
        // Each foliage step builds on the previous one's layer, so only its own member changes.
        MapFoliageLayer seeded = FoliageStorageFixtures.Change(layer, l => l["seed"] = 18);
        MapFoliageLayer ruled = FoliageFixtures.ClearanceLayer(seeded,
            rules: [new("shrub-solid", "item", "shrub", MapFoliageClearanceKind.Solid, new(-1, -1, 1, 1))]);
        yield return ("seed", new FoliageEditCommand(new MapSetFoliageLayer(seeded)), true);
        yield return ("rule", new FoliageEditCommand(new MapSetFoliageLayer(ruled)), true);
        yield return ("mask", new FoliageEditCommand(new MapSetFoliageLayer(FoliageFixtures.ClearanceLayer(ruled,
            masks: [new("post-mask", FoliageFixtures.Owner(FoliageFixtures.PostId, MapPrefabChildKind.Marker), FoliageFixtures.Surface("exterior"),
                MapFoliageClearanceKind.Solid, new(-2, -2, 2, 2))]))), true);
    }
}
