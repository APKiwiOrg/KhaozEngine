using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapEdit;
using KhaozEngine.MapEditor;
using KhaozEngine.Tests.MapDoc;
using KhaozEngine.Tests.MapDoc.Foliage;
using Xunit;

namespace KhaozEngine.Tests.MapEditTool;

/// <summary>A session callback that stages a command and then changes foliage publishes one snapshot: one event, one
/// history entry, the effects of a generic callback making the same final edit, and a redo of exactly that snapshot.</summary>
public sealed class NativeFoliagePublicationTests
{
    [Theory]
    [InlineData("display-name")]
    [InlineData("terrain-globals")]
    [InlineData("placements")]
    public void LateCallbackMembers_PublishOneSnapshotEffectsAndRedo(string member)
    {
        // Regression for https://github.com/APKiwiOrg/KhaozEngine/issues/1481
        FoliageFixture f = FoliageFixtures.Dependents();
        MapEditSession session = FoliageEditorFixtures.Session(f);
        MapEditSession generic = FoliageEditorFixtures.Session(f);
        try
        {
            string original = FoliageEditorFixtures.Bytes(session);
            var events = new List<MapNativeEditEffects>();
            session.NativeEdited += events.Add;
            var terrain = new MapSetCornerHeights(new("exterior", 0, 0), 9, 9, 2, 2, [-150, -150, -150, -150]);
            session.Mutate((doc, registry) =>
            {
                session.ApplyNative(new TerrainEditCommand(terrain), doc, registry);
                ChangeMember(doc, member);
                return 0;
            }, true);
            generic.Mutate((doc, _) =>
            {
                doc.Surfaces = MapTerrainEdits.Prepare(doc.Surfaces, terrain).Candidate;
                MapPrefabAttachmentDescriptors.Refresh(doc);
                ChangeMember(doc, member);
                return 0;
            }, true);
            Assert.Equal(FoliageEditorFixtures.Bytes(generic), FoliageEditorFixtures.Bytes(session));
            Assert.Equal(generic.LastNativeEffects!.Describe(), Assert.Single(events).Describe());
            Assert.Equal((1, 1, 1L), session.NativeHistoryState);
            Assert.Equal((terrain.Label, (string?)null), session.NativeHistoryLabels);
            string published = FoliageEditorFixtures.Bytes(session);
            Assert.NotNull(session.UndoNative());
            Assert.Equal(original, FoliageEditorFixtures.Bytes(session));
            Assert.NotNull(session.RedoNative());
            Assert.Equal(published, FoliageEditorFixtures.Bytes(session));
        }
        finally
        {
            FoliageEditorFixtures.Delete(session);
            FoliageEditorFixtures.Delete(generic);
        }
    }

    static void ChangeMember(MapDocument document, string member)
    {
        switch (member)
        {
            case "display-name": document.DisplayName = "Late callback name"; break;
            case "terrain-globals": document.Terrain.Seed = 94; break;
            case "placements":
                document.Placements = [.. document.Placements, new() { Id = "late", Kind = "item", AssetId = "item", X = 12, Y = -2, Z = -6 }];
                break;
        }
    }

    [Fact]
    public void LatePartialTileIndex_RefusesEvenWhenStagedCommandIsANoOp()
    {
        // Regression for https://github.com/APKiwiOrg/KhaozEngine/issues/1481
        FoliageFixture f = FoliageFixtures.Dependents();
        MapEditSession session = FoliageEditorFixtures.Session(f);
        try
        {
            string before = FoliageEditorFixtures.State(session);
            var error = Assert.Throws<MapDocumentException>(() => session.Mutate((doc, registry) =>
            {
                var command = new NativeSessionSnapshotCommand("No change", doc, doc, f.Assets, f.Assets,
                    new(null, null, [], [], [], [], MapNativeInvalidation.None));
                session.ApplyNative(command, doc, registry);
                doc.Tiles = new(doc.TileSize, MapDocumentHash.SchemeVersion, null, [new(new(0, 0), "unloaded", false)]);
                return 0;
            }, true));
            Assert.Equal(MapDocumentRefusal.CompleteDocumentRequired, error.Reason);
            Assert.Equal(before, FoliageEditorFixtures.State(session));
            session.Mutate((doc, _) => { doc.DisplayName = "Valid callback"; return 0; }, false);
            Assert.Equal("Valid callback", session.WithDocument((doc, _) => doc.DisplayName));
        }
        finally { FoliageEditorFixtures.Delete(session); }
    }

    [Fact]
    public void LateCallbackFoliage_PublishesOneSnapshotEffectsAndRedo()
    {
        FoliageFixture f = FoliageFixtures.Dependents();
        MapEditSession session = FoliageEditorFixtures.Session(f);
        MapEditSession generic = FoliageEditorFixtures.Session(f);
        try
        {
            var events = new List<MapNativeEditEffects>();
            session.NativeEdited += events.Add;
            string original = FoliageEditorFixtures.Bytes(session);
            var (cursor, depth, _) = session.NativeHistoryState;
            var terrain = new MapSetCornerHeights(new("exterior", 0, 0), 9, 9, 2, 2, [-150, -150, -150, -150]);
            session.Mutate((doc, registry) =>
            {
                session.ApplyNative(new TerrainEditCommand(terrain), doc, registry);
                doc.Foliage = new(1, [Changed(doc.Foliage!.Layers[0])]);
                return 0;
            }, true);
            generic.Mutate((doc, _) =>
            {
                doc.Surfaces = MapTerrainEdits.Prepare(doc.Surfaces, terrain).Candidate;
                MapPrefabAttachmentDescriptors.Refresh(doc);
                doc.Foliage = new(1, [Changed(doc.Foliage!.Layers[0])]);
                return 0;
            }, true);
            MapNativeEditEffects published = Assert.Single(events);
            Assert.Same(published, session.LastNativeEffects);
            Assert.Equal(FoliageEditorFixtures.Bytes(generic), FoliageEditorFixtures.Bytes(session));
            Assert.Equal(generic.LastNativeEffects!.Describe(), published.Describe());
            FoliageFixture final = FoliageEditorFixtures.Fixture(session);
            Assert.Equal(FoliageEditorFixtures.Expected(f.Document, final.Document, f.Assets), FoliageEditorFixtures.Channel(published));
            // The rule is a whole-layer member, so the late edit publishes the layer entry and no rows.
            MapFoliageLayerChange entry = Assert.Single(published.FoliageLayers);
            Assert.Empty(published.FoliageChunks);
            Assert.True(published.Invalidates.HasFlag(MapNativeInvalidation.Foliage | MapNativeInvalidation.Terrain));
            Assert.Equal((cursor + 1, depth + 1), (session.NativeHistoryState.Cursor, session.NativeHistoryState.Depth));
            Assert.Equal((terrain.Label, (string?)null), session.NativeHistoryLabels);
            Assert.Single(final.Document.Foliage!.Layers[0].ClearanceRules, r => r.Id == "late-rule");

            string publishedBytes = FoliageEditorFixtures.Bytes(session);
            string publishedChunkEffects = FoliageEditorFixtures.Channel(published);
            Assert.Equal(FoliageEditorFixtures.Reversed(published), FoliageEditorFixtures.Channel(session.UndoNative()!));
            Assert.Equal(original, FoliageEditorFixtures.Bytes(session));
            string redoChunkEffects = FoliageEditorFixtures.Channel(session.RedoNative()!);
            string redoBytes = FoliageEditorFixtures.Bytes(session);
            Assert.Equal(publishedBytes, redoBytes);
            Assert.Equal(publishedChunkEffects, redoChunkEffects);
            Assert.Equal(3, events.Count);

            // The next edit starts from the one published snapshot's layer digest.
            MapFoliageLayer current = FoliageEditorFixtures.Fixture(session).Document.Foliage!.Layers[0];
            MapNativeEditEffects next = session.ExecuteNativeCommand(new FoliageEditCommand(new MapSetFoliageLayer(
                FoliageStorageFixtures.Change(current, l => l["seed"] = 99))));
            string? publishedDependencyDigest = entry.AfterDigest;
            string? nextEditBeforeDigest = Assert.Single(next.FoliageLayers).BeforeDigest;
            Assert.Equal(publishedDependencyDigest, nextEditBeforeDigest);
        }
        finally
        {
            FoliageEditorFixtures.Delete(session);
            FoliageEditorFixtures.Delete(generic);
        }
    }

    // A density stroke and a new rule on the same layer.
    static MapFoliageLayer Changed(MapFoliageLayer layer) => FoliageFixtures.ClearanceLayer(
        MapFoliageBrush.Apply(layer, new(layer.Id, 12, -6, 1.5f, 40, 1)),
        rules: layer.ClearanceRules.Append(new("late-rule", "item", "first", MapFoliageClearanceKind.Solid, new(-1, -1, 1, 1))).ToArray());
}
