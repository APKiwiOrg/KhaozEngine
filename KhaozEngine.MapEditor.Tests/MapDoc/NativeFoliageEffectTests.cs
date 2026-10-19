using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
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
                IReadOnlyList<MapFoliageChunkChange> expected = FoliageEditorFixtures.Expected(before.Document, after.Document, before.Assets);
                Assert.True(expected.SequenceEqual(effects.FoliageChunks), name);
                Assert.Equal(expected.Count != 0, effects.Invalidates.HasFlag(MapNativeInvalidation.Foliage));
                if (dependent) Assert.True(expected.Count != 0, name + " reports its dependent batches");
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
                        Invalidates = effects.Invalidates & ~MapNativeInvalidation.Foliage,
                    }).Describe());
                }
                Assert.Equal(FoliageEditorFixtures.Reversed(effects.FoliageChunks), session.UndoNative()!.FoliageChunks);
                Assert.Equal(effects.FoliageChunks, session.RedoNative()!.FoliageChunks);
                Assert.Equal(MapDocumentFile.SaveText(after.Document), FoliageEditorFixtures.Bytes(session));
            }
        }
        finally
        {
            FoliageEditorFixtures.Delete(session);
            FoliageEditorFixtures.Delete(control);
        }
    }

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
        yield return ("seed", new FoliageEditCommand(new MapSetFoliageLayer(FoliageStorageFixtures.Change(layer, l => l["seed"] = 18))), true);
        yield return ("rule", new FoliageEditCommand(new MapSetFoliageLayer(FoliageFixtures.ClearanceLayer(layer,
            rules: [new("shrub-solid", "item", "shrub", MapFoliageClearanceKind.Solid, new(-1, -1, 1, 1))]))), true);
        yield return ("mask", new FoliageEditCommand(new MapSetFoliageLayer(FoliageFixtures.ClearanceLayer(layer,
            masks: [new("post-mask", FoliageFixtures.Owner(FoliageFixtures.PostId, MapPrefabChildKind.Marker), FoliageFixtures.Surface("exterior"),
                MapFoliageClearanceKind.Solid, new(-2, -2, 2, 2))]))), true);
    }
}
