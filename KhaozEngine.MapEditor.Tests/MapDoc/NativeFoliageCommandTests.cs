using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapEdit;
using KhaozEngine.MapEditor;
using KhaozEngine.Tests.MapDoc.Foliage;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

/// <summary>The bound foliage command: one undoable step on the direct history and the session, the same rows as the
/// pure edit, atomic refusal and no history for a no-op.</summary>
public sealed class NativeFoliageCommandTests
{
    const string Id = FoliageFixtures.LayerId;

    [Fact]
    public void FoliageCommand_DirectSessionUndoRedoAndRefusalAreAtomic()
    {
        FoliageFixture f = FoliageFixtures.Create();
        MapEditSession session = FoliageEditorFixtures.Session(f);
        try
        {
            var (gui, history) = FoliageEditorFixtures.Gui(f);
            var events = new List<MapNativeEditEffects>();
            session.NativeEdited += events.Add;
            string original = FoliageEditorFixtures.Bytes(session);

            // One density edit: pure, direct history and session publish the same rows, and the session advances once.
            var edit = new MapSetFoliageDensity(Id, 1, 2, 2, 1, [7, 9]);
            MapPrefabEditResult pure = MapFoliageEdits.Prepare(f.Document, f.Assets, edit);
            int beforeCursor = session.NativeHistoryState.Cursor;
            MapNativeEditEffects editorEffects = session.ExecuteNativeCommand(new FoliageEditCommand(edit));
            int acceptedCursor = session.NativeHistoryState.Cursor;
            history.Execute(gui, new FoliageEditCommand(edit));
            Assert.Equal(beforeCursor + 1, acceptedCursor);
            Assert.Equal(FoliageEditorFixtures.Channel(pure.Effects), FoliageEditorFixtures.Channel(editorEffects));
            Assert.Equal(pure.Effects.Describe(), editorEffects.Describe());
            Assert.Equal(pure.Effects.Describe(), history.LastNativeEffects!.Describe());
            Assert.Equal(MapNativeInvalidation.Foliage, editorEffects.Invalidates);
            Assert.Same(editorEffects, Assert.Single(events));
            Assert.Equal(("Set foliage density", (string?)null), session.NativeHistoryLabels);
            string published = FoliageEditorFixtures.Bytes(session);
            Assert.Equal(MapDocumentFile.SaveText(gui), published);
            Assert.Equal(MapDocumentFile.SaveText(pure.After), published);

            // Undo reverses the rows and restores every byte, redo republishes the same rows and bytes.
            MapNativeEditEffects undo = session.UndoNative()!;
            Assert.True(history.Undo(gui));
            Assert.Equal(FoliageEditorFixtures.Reversed(editorEffects), FoliageEditorFixtures.Channel(undo));
            Assert.Equal(original, FoliageEditorFixtures.Bytes(session));
            Assert.Equal(original, MapDocumentFile.SaveText(gui));
            MapNativeEditEffects redo = session.RedoNative()!;
            Assert.True(history.Redo(gui));
            Assert.Equal(FoliageEditorFixtures.Channel(editorEffects), FoliageEditorFixtures.Channel(redo));
            Assert.Equal(published, FoliageEditorFixtures.Bytes(session));
            Assert.Equal(3, events.Count);

            // A refusal writes nothing on either path, and a no-op adds no history and raises no event.
            string beforeState = FoliageEditorFixtures.State(session);
            string guiBefore = MapDocumentFile.SaveText(gui);
            var bad = new MapSetFoliageDensity(Id, 1, 2, 2, 1, [7, 256]);
            var a = Assert.Throws<MapDocumentException>(() => session.ExecuteNativeCommand(new FoliageEditCommand(bad)));
            var b = Assert.Throws<MapDocumentException>(() => history.Execute(gui, new FoliageEditCommand(bad)));
            Assert.Equal(a.Message, b.Message);
            Assert.Contains("must be from 0 to 255", a.Message, StringComparison.Ordinal);
            string refusedState = FoliageEditorFixtures.State(session);
            Assert.Equal(beforeState, refusedState);
            Assert.Equal(guiBefore, MapDocumentFile.SaveText(gui));
            MapNativeEditEffects noOp = session.ExecuteNativeCommand(new FoliageEditCommand(edit));
            Assert.Empty(noOp.FoliageChunks);
            Assert.Empty(noOp.FoliageLayers);
            Assert.Equal(beforeState, FoliageEditorFixtures.State(session));
            Assert.Equal(3, events.Count);

            // Local application has no verified closure, so foliage refuses before any copy.
            MapDocument local = MapDocumentFile.LoadText(published);
            long clones = NativeDocumentSnapshot.ThreadClones;
            Assert.Contains("verified asset closure", Assert.Throws<MapDocumentException>(() =>
                new FoliageEditCommand(new MapPaintFoliage(Id, 1, -1, 2, 9, 1)).Apply(local)).Message, StringComparison.Ordinal);
            Assert.Equal(0, NativeDocumentSnapshot.ThreadClones - clones);
            Assert.Equal(published, MapDocumentFile.SaveText(local));

            // A settings change on a layer without stored chunks names the root but publishes no row and no flag.
            MapFoliageLayer empty = FoliageFixtures.BasicLayer([]);
            var chunkless = new MapSetFoliageLayer(FoliageStorageFixtures.Change(empty, l => l["id"] = "bare"));
            MapPrefabEditResult pureBare = MapFoliageEdits.Prepare(FoliageEditorFixtures.Fixture(session).Document, f.Assets, chunkless);
            MapNativeEditEffects bare = session.ExecuteNativeCommand(new FoliageEditCommand(chunkless));
            Assert.True(pureBare.WriteSet.Foliage);
            Assert.Equal((MapNativeInvalidation.None, 0, 0), (bare.Invalidates, bare.FoliageChunks.Count, bare.FoliageLayers.Count));
            Assert.Equal(pureBare.Effects.Describe(), bare.Describe());
            Assert.Contains("\"bare\"", FoliageEditorFixtures.Bytes(session), StringComparison.Ordinal);
            MapNativeEditEffects seeded = session.ExecuteNativeCommand(new FoliageEditCommand(new MapSetFoliageLayer(
                FoliageStorageFixtures.Change(empty, l => { l["id"] = "bare"; l["seed"] = 99; }))));
            Assert.Equal((MapNativeInvalidation.None, 0, 0), (seeded.Invalidates, seeded.FoliageChunks.Count, seeded.FoliageLayers.Count));
        }
        finally { FoliageEditorFixtures.Delete(session); }
    }

    [Fact]
    public void FoliageSequence_RemovesOwnerAndRowsInOneUndoStep()
    {
        FoliageFixture f = FoliageFixtures.OwnedByInstance();
        MapEditSession session = FoliageEditorFixtures.Session(f);
        try
        {
            string original = FoliageEditorFixtures.Bytes(session);
            var sequence = new MapFoliageEditSequence([new MapSetFoliageLayer(FoliageFixtures.WithoutInstanceOwner(f))], new MapRemovePrefab("p"));
            MapPrefabEditResult pure = MapFoliageEdits.Prepare(f.Document, f.Assets, sequence);
            var (cursor, depth, events) = session.NativeHistoryState;
            MapNativeEditEffects effects = session.ExecuteNativeCommand(new FoliageEditCommand(sequence));
            Assert.Equal((cursor + 1, depth + 1, events + 1), session.NativeHistoryState);
            Assert.Equal(FoliageEditorFixtures.Channel(pure.Effects), FoliageEditorFixtures.Channel(effects));
            Assert.NotEmpty(effects.FoliageChunks);
            Assert.True(effects.Invalidates.HasFlag(MapNativeInvalidation.Placements | MapNativeInvalidation.Foliage));
            FoliageFixture removed = FoliageEditorFixtures.Fixture(session);
            Assert.True(removed.Document.Prefabs!.Instances.Single().Removed);
            Assert.Empty(removed.Document.Foliage!.Layers[0].ExceptionMasks);
            Assert.Empty(removed.Document.Foliage.Layers[0].OwnerSupports);
            string published = FoliageEditorFixtures.Bytes(session);

            // One undo restores the instance and both owned rows together.
            Assert.Equal(FoliageEditorFixtures.Reversed(effects), FoliageEditorFixtures.Channel(session.UndoNative()!));
            Assert.Equal(original, FoliageEditorFixtures.Bytes(session));
            FoliageFixture restored = FoliageEditorFixtures.Fixture(session);
            Assert.False(restored.Document.Prefabs!.Instances.Single().Removed);
            Assert.Single(restored.Document.Foliage!.Layers[0].ExceptionMasks);
            Assert.Single(restored.Document.Foliage.Layers[0].OwnerSupports);
            Assert.Equal(FoliageEditorFixtures.Channel(effects), FoliageEditorFixtures.Channel(session.RedoNative()!));
            Assert.Equal(published, FoliageEditorFixtures.Bytes(session));
        }
        finally { FoliageEditorFixtures.Delete(session); }
    }

    [Fact]
    public void NoFoliageCommands_KeepReleasedEffectBytes()
    {
        FoliageFixture control = FoliageFixtures.Dependents(foliage: false);
        MapEditSession session = FoliageEditorFixtures.Session(control);
        try
        {
            var descriptions = new List<string>();
            foreach (EditorCommand command in ReleasedCommands(control))
            {
                long captures = MapFoliageGenerationContext.ThreadCaptures;
                MapNativeEditEffects effects = session.ExecuteNativeCommand(command);
                Assert.Equal(0, MapFoliageGenerationContext.ThreadCaptures - captures);
                Assert.Empty(effects.FoliageChunks);
                Assert.Empty(effects.FoliageLayers);
                Assert.Null(session.LastNativeStamp);
                descriptions.Add(effects.Describe());
                descriptions.Add(session.UndoNative()!.Describe());
                session.RedoNative();
            }
            Assert.Equal(Released, descriptions.ToArray());
        }
        finally { FoliageEditorFixtures.Delete(session); }
    }

    // The released dependency commands over the no-foliage control, in order.
    internal static IEnumerable<EditorCommand> ReleasedCommands(FoliageFixture f)
    {
        yield return new MovePlacementCommand(FoliageFixtures.ShrubId, 8, -10, null);
        yield return new InteriorEditCommand(new MapSetInterior(FoliageFixtures.DependentRoom(f.Document) with { UpperY = 2.5f }));
        yield return new InteriorEditCommand(new MapSetRoofLink(FoliageFixtures.DependentRoofLink()));
        yield return new MarkerEditCommand(new MapSetMarker(FoliageFixtures.MovedMarker(FoliageFixtures.DependentMarker(f.Document, FoliageFixtures.PostId), 4)));
        yield return new TerrainEditCommand(new MapSetCornerHeights(new("exterior", 0, 0), 9, 9, 2, 2, [-150, -150, -150, -150]));
        MapPrefabAsset hut = f.Assets.GetPrefab("hut");
        yield return new PrefabEditCommand(new MapPlacePrefab("b", "hut", new(new(8, 0, 0), 0, 1), new("exterior", new("exterior", 0, 0)))
        { InitialOverrides = PrefabFixtures.MarkerNames(hut, "b") });
        // An asset switch that moves the hut's item in the placed instance, between two verified closures.
        var item = (MapPrefabPlacementChild)hut.Children.Single(c => c.Key.Key == "item");
        MapPlacement moved = MapPrefabReferenceWalker.Copy(item.Value, item.Value.Id, item.Value.SupportBinding);
        moved.X = 2;
        MapAssetClosure after = PrefabFixtures.PublishPrefab(f.Assets, MapPrefabAssetEdits.Replace(hut, item with { Value = moved }));
        yield return new PrefabAssetEditCommand(new MapUpdatePrefabAsset("hut", f.Assets.GetResource("hut").Reference.Sha256, "hut",
            after.GetResource("hut").Reference.Sha256), f.Assets, after);
    }

    // Each released command's effect description, then its undo's, captured from the released engine at 44d75d728 on these
    // fixtures.
    static readonly string[] Released =
    [
        """{"oldBounds":[9.899999998509884,-2.100000001490116,-10.100000001490116,10.100000001490116,-1.0999999642372131,-9.899999998509884],"newBounds":[7.899999998509884,-2.100000001490116,-10.100000001490116,8.100000001490116,-1.0999999642372131,-9.899999998509884],"oldSpatialBounds":[9.899998044835531,-2.10000195516447,-10.100001955164469,10.100001955164469,-1.0999980105628595,-9.899998044835531],"newSpatialBounds":[7.89999804483553,-2.10000195516447,-10.100001955164469,8.100001955164469,-1.0999980105628595,-9.899998044835531],"patches":[],"spaceIds":[],"dependencyIds":[],"childIds":["shrub"],"digestChanges":[],"invalidates":54}""",
        """{"oldBounds":[7.899999998509884,-2.100000001490116,-10.100000001490116,8.100000001490116,-1.0999999642372131,-9.899999998509884],"newBounds":[9.899999998509884,-2.100000001490116,-10.100000001490116,10.100000001490116,-1.0999999642372131,-9.899999998509884],"oldSpatialBounds":[7.89999804483553,-2.10000195516447,-10.100001955164469,8.100001955164469,-1.0999980105628595,-9.899998044835531],"newSpatialBounds":[9.899998044835531,-2.10000195516447,-10.100001955164469,10.100001955164469,-1.0999980105628595,-9.899998044835531],"patches":[],"spaceIds":[],"dependencyIds":[],"childIds":["shrub"],"digestChanges":[],"invalidates":54}""",
        """{"oldBounds":[0,0,-4,4,3,0],"newBounds":[0,0,-4,4,2.5,0],"patches":[["a/floor","0","0"]],"spaceIds":["a/room"],"dependencyIds":["a/room","a/room-domain"],"instanceIds":["a"],"childIds":["a/room","a/room-domain"],"roofIds":["a/roof"],"lightIds":["a/lamp"],"digestChanges":[{"key":"Record:a/room-domain","before":"b115d5718aa2fc94c7e86b62d4be4b02fa92dd7072d6839aa4b710f09f918f2e","after":"3dd8ef67f1bf4ea3d30adac4609b0412577339b1eaecc28b787953de563f33f6"}],"invalidates":55}""",
        """{"oldBounds":[0,0,-4,4,2.5,0],"newBounds":[0,0,-4,4,3,0],"patches":[["a/floor","0","0"]],"spaceIds":["a/room"],"dependencyIds":["a/room","a/room-domain"],"instanceIds":["a"],"childIds":["a/room","a/room-domain"],"roofIds":["a/roof"],"lightIds":["a/lamp"],"digestChanges":[{"key":"Record:a/room-domain","before":"3dd8ef67f1bf4ea3d30adac4609b0412577339b1eaecc28b787953de563f33f6","after":"b115d5718aa2fc94c7e86b62d4be4b02fa92dd7072d6839aa4b710f09f918f2e"}],"invalidates":55}""",
        """{"oldBounds":null,"newBounds":null,"patches":[],"spaceIds":[],"dependencyIds":[],"childIds":["link"],"roofIds":["a/roof"],"lightIds":["a/lamp"],"digestChanges":[],"invalidates":55}""",
        """{"oldBounds":null,"newBounds":null,"patches":[],"spaceIds":[],"dependencyIds":[],"childIds":["link"],"roofIds":["a/roof"],"lightIds":["a/lamp"],"digestChanges":[],"invalidates":55}""",
        """{"oldBounds":[6,-2,-12,6,-2,-12],"newBounds":[4,-2,-12,4,-2,-12],"patches":[],"spaceIds":[],"dependencyIds":[],"markerIds":["post"],"digestChanges":[],"invalidates":16}""",
        """{"oldBounds":[4,-2,-12,4,-2,-12],"newBounds":[6,-2,-12,6,-2,-12],"patches":[],"spaceIds":[],"dependencyIds":[],"markerIds":["post"],"digestChanges":[],"invalidates":16}""",
        """{"oldBounds":[0,-2,-16,16,-2,0],"newBounds":[0,-2,-16,16,-1.5,0],"patches":[["exterior","0","0"]],"spaceIds":["a/room","exterior"],"dependencyIds":["a/room","a/room-domain","exterior","exterior-domain"],"digestChanges":[{"key":"patch/exterior/0/0","before":"5d240e8a30bd0c8bcb1afd124d8ae656f31b7e5499cfa681448bbef78436fe85","after":"4c1e6c57336024d0db07fc4718ea882bb85a369523c897d6b2bb76622c012cf6"},{"key":"surface/exterior","before":"295fcb0aa3131036bd59b1de6dfb6e3e824d10b4358cb7de492d997df13df70f","after":"9c1a27540034f29bf02ded6c934f09d32ed6dfd41f3f8b55493e683bedcc2156"}],"invalidates":23}""",
        """{"oldBounds":[0,-2,-16,16,-1.5,0],"newBounds":[0,-2,-16,16,-2,0],"patches":[["exterior","0","0"]],"spaceIds":["a/room","exterior"],"dependencyIds":["a/room","a/room-domain","exterior","exterior-domain"],"digestChanges":[{"key":"patch/exterior/0/0","before":"4c1e6c57336024d0db07fc4718ea882bb85a369523c897d6b2bb76622c012cf6","after":"5d240e8a30bd0c8bcb1afd124d8ae656f31b7e5499cfa681448bbef78436fe85"},{"key":"surface/exterior","before":"9c1a27540034f29bf02ded6c934f09d32ed6dfd41f3f8b55493e683bedcc2156","after":"295fcb0aa3131036bd59b1de6dfb6e3e824d10b4358cb7de492d997df13df70f"}],"invalidates":23}""",
        """{"oldBounds":null,"newBounds":[6,-2,-4,16,6,4],"newSpatialBounds":[5.940388623135236,-2,-4,16,6,4],"patches":[["prefab/b/Surface/ceiling","0","0"],["prefab/b/Surface/floor","0","0"]],"spaceIds":["prefab/b/Record/room"],"dependencyIds":["prefab/b/Record/door-bottom","prefab/b/Record/door-top","prefab/b/Record/room","prefab/b/Record/room-domain","prefab/b/Record/room-portal"],"instanceIds":["b"],"childIds":["prefab/b/Light/lamp","prefab/b/Marker/entry","prefab/b/Placement/doorway","prefab/b/Placement/item","prefab/b/Placement/roof","prefab/b/Record/door-bottom","prefab/b/Record/door-top","prefab/b/Record/room","prefab/b/Record/room-domain","prefab/b/Record/room-portal","prefab/b/RoofLink/roof-link","prefab/b/Storey/lower","prefab/b/Surface/ceiling","prefab/b/Surface/floor"],"roofIds":["a/roof","prefab/b/Placement/roof"],"lightIds":["a/lamp","prefab/b/Light/lamp"],"markerIds":["prefab/b/Marker/entry"],"markerNameChanges":[{"markerId":"prefab/b/Marker/entry","before":null,"after":"Entry b"}],"digestChanges":[],"invalidates":55}""",
        """{"oldBounds":[6,-2,-4,16,6,4],"newBounds":null,"oldSpatialBounds":[5.940388623135236,-2,-4,16,6,4],"patches":[["prefab/b/Surface/ceiling","0","0"],["prefab/b/Surface/floor","0","0"]],"spaceIds":["prefab/b/Record/room"],"dependencyIds":["prefab/b/Record/door-bottom","prefab/b/Record/door-top","prefab/b/Record/room","prefab/b/Record/room-domain","prefab/b/Record/room-portal"],"instanceIds":["b"],"childIds":["prefab/b/Light/lamp","prefab/b/Marker/entry","prefab/b/Placement/doorway","prefab/b/Placement/item","prefab/b/Placement/roof","prefab/b/Record/door-bottom","prefab/b/Record/door-top","prefab/b/Record/room","prefab/b/Record/room-domain","prefab/b/Record/room-portal","prefab/b/RoofLink/roof-link","prefab/b/Storey/lower","prefab/b/Surface/ceiling","prefab/b/Surface/floor"],"roofIds":["a/roof","prefab/b/Placement/roof"],"lightIds":["a/lamp","prefab/b/Light/lamp"],"markerIds":["prefab/b/Marker/entry"],"markerNameChanges":[{"markerId":"prefab/b/Marker/entry","before":"Entry b","after":null}],"digestChanges":[],"invalidates":55}""",
        """{"oldBounds":[8,-0.10000000149011612,-4,12,3,0],"newBounds":[8,-0.10000000149011612,-4,12,3,0],"oldSpatialBounds":[8,-0.10000195516446977,-4,12,3,0],"newSpatialBounds":[8,-0.10000195516446977,-4,12,3,0],"patches":[["prefab/b/Surface/ceiling","0","0"],["prefab/b/Surface/floor","0","0"]],"spaceIds":["prefab/b/Record/room"],"dependencyIds":["prefab/b/Record/door-bottom","prefab/b/Record/door-top","prefab/b/Record/room","prefab/b/Record/room-domain","prefab/b/Record/room-portal"],"instanceIds":["b"],"childIds":["prefab/b/Light/lamp","prefab/b/Marker/entry","prefab/b/Placement/doorway","prefab/b/Placement/item","prefab/b/Placement/roof","prefab/b/Record/door-bottom","prefab/b/Record/door-top","prefab/b/Record/room","prefab/b/Record/room-domain","prefab/b/Record/room-portal","prefab/b/RoofLink/roof-link","prefab/b/Storey/lower","prefab/b/Surface/ceiling","prefab/b/Surface/floor"],"roofIds":["a/roof","prefab/b/Placement/roof"],"lightIds":["a/lamp","prefab/b/Light/lamp"],"digestChanges":[],"invalidates":55}""",
        """{"oldBounds":[8,-0.10000000149011612,-4,12,3,0],"newBounds":[8,-0.10000000149011612,-4,12,3,0],"oldSpatialBounds":[8,-0.10000195516446977,-4,12,3,0],"newSpatialBounds":[8,-0.10000195516446977,-4,12,3,0],"patches":[["prefab/b/Surface/ceiling","0","0"],["prefab/b/Surface/floor","0","0"]],"spaceIds":["prefab/b/Record/room"],"dependencyIds":["prefab/b/Record/door-bottom","prefab/b/Record/door-top","prefab/b/Record/room","prefab/b/Record/room-domain","prefab/b/Record/room-portal"],"instanceIds":["b"],"childIds":["prefab/b/Light/lamp","prefab/b/Marker/entry","prefab/b/Placement/doorway","prefab/b/Placement/item","prefab/b/Placement/roof","prefab/b/Record/door-bottom","prefab/b/Record/door-top","prefab/b/Record/room","prefab/b/Record/room-domain","prefab/b/Record/room-portal","prefab/b/RoofLink/roof-link","prefab/b/Storey/lower","prefab/b/Surface/ceiling","prefab/b/Surface/floor"],"roofIds":["a/roof","prefab/b/Placement/roof"],"lightIds":["a/lamp","prefab/b/Light/lamp"],"digestChanges":[],"invalidates":55}""",
    ];
}
