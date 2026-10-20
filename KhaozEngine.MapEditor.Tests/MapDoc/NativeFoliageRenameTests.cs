using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapEdit;
using KhaozEngine.MapEditor;
using KhaozEngine.Tests.MapDoc.Foliage;
using KhaozEngine.Tests.MapEditTool;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

/// <summary>A standalone placement rename carries the foliage rows that name it, through the one authoritative walker,
/// every write-set boundary, replay and both storage forms. A placement no foliage row names never writes the root.</summary>
[Collection(McpWireCollection.Name)]
public sealed class NativeFoliageRenameTests
{
    static readonly MapPrefabReference Q = FoliageFixtures.Owner("q");

    [Fact]
    public void PlacementReferencesRemap_CopiesAuthoritativeFoliageRoot()
    {
        FoliageFixture f = FoliageFixtures.Renamable();
        string original = MapDocumentFile.SaveText(f.Document);
        MapDocument candidate = NativeDocumentSnapshot.Clone(f.Document, MapDocRegistry.CreateDefault());
        MapFoliageSet authoritative = MapPrefabReferences.RemapWorld(candidate, MapPrefabChildKind.Placement, "prop", "q").Foliage!;
        NativePlacementReferences.Remap(candidate, "prop", "q");
        MapFoliageLayer layer = candidate.Foliage!.Layers[0];
        Assert.Equal(Q, Assert.Single(layer.ExceptionMasks).Owner);
        Assert.Equal(Q, Assert.Single(layer.OwnerSupports).Owner);
        Assert.Equal(FoliageStorageFixtures.Json(authoritative), FoliageStorageFixtures.Json(candidate.Foliage));
        Assert.Equal(f.Document.Foliage!.Layers[0].ChunkDigests, layer.ChunkDigests);
        Assert.Equal(original, MapDocumentFile.SaveText(f.Document));
    }

    [Fact]
    public async Task StandaloneRename_DirectAndWirePreserveFoliageThroughReplay()
    {
        FoliageFixture f = FoliageFixtures.Renamable();
        var (gui, history) = FoliageEditorFixtures.Gui(f);
        string original = MapDocumentFile.SaveText(gui);

        // The direct bound editor: the staged publication, the command's accepted writes and the state union name Foliage.
        var rename = new RemapPlacementIdCommand("prop", "q");
        NativeStagedPublication staged = history.StageExecute(gui, rename);
        Assert.True(staged.WriteSet.Foliage);
        NativeDocumentTransaction.Publish(gui, staged);
        staged.Accept();
        MapNativeWriteSet renamedWrites = ((INativePlacementWriteSetCommand)rename).AcceptedWrites!;
        Assert.True(renamedWrites.Foliage);
        Assert.NotEmpty(staged.Effects.FoliageChunks);
        Assert.Equal(FoliageEditorFixtures.Expected(MapDocumentFile.LoadText(original), gui, f.Assets), FoliageEditorFixtures.Channel(staged.Effects));
        Assert.Empty(staged.Effects.FoliageLayers);
        AssertOwner(gui, Q);
        string renamed = MapDocumentFile.SaveText(gui);
        NativeStagedPublication undo = history.StageUndo(gui)!;
        Assert.True(undo.WriteSet.Foliage);
        NativeDocumentTransaction.Publish(gui, undo);
        undo.Accept();
        Assert.Equal(original, MapDocumentFile.SaveText(gui));
        Assert.True(history.Redo(gui));
        Assert.Equal(renamed, MapDocumentFile.SaveText(gui));

        // An otherwise identical rename of a placement that no foliage row names never writes the foliage root.
        var other = new RemapPlacementIdCommand("other", "other-2");
        NativeStagedPublication plain = history.StageExecute(gui, other);
        Assert.False(plain.WriteSet.Foliage);
        NativeDocumentTransaction.Publish(gui, plain);
        plain.Accept();
        Assert.False(((INativePlacementWriteSetCommand)other).AcceptedWrites!.Foliage);
        Assert.Equal(FoliageEditorFixtures.Channel([], []), FoliageEditorFixtures.Channel(plain.Effects));
        string unrelated = MapDocumentFile.SaveText(gui);
        Assert.True(history.Undo(gui));

        // The replay state's captures and their union name Foliage for the rename and never for the unrelated placement.
        MapDocument before = MapDocumentFile.LoadText(original), after = MapDocumentFile.LoadText(renamed), moved = MapDocumentFile.LoadText(unrelated);
        Assert.Equal(MapFoliageProjection.Digest(after.Foliage), MapFoliageProjection.Digest(moved.Foliage));
        MapNativeWriteSet between = NativePlacementResourceState.Between(NativePlacementResourceState.Capture(before, "prop"),
            NativePlacementResourceState.Capture(after, "q"));
        MapNativeWriteSet unrelatedPlacementWrites = NativePlacementResourceState.Between(NativePlacementResourceState.Capture(after, "other"),
            NativePlacementResourceState.Capture(moved, "other-2"));
        Assert.True(between.Foliage);
        Assert.False(unrelatedPlacementWrites.Foliage);
        Assert.True(NativePlacementResourceState.Union(unrelatedPlacementWrites, between).Foliage);
        Assert.True(NativePlacementResourceState.Union(between, unrelatedPlacementWrites).Foliage);
        Assert.False(NativePlacementResourceState.Union(unrelatedPlacementWrites, unrelatedPlacementWrites).Foliage);

        // The session's placement verb publishes the same document, effects and replay, and both storage forms keep it.
        MapEditSession session = FoliageEditorFixtures.Session(f);
        MapEditSession seed = FoliageEditorFixtures.Session(f);
        try
        {
            new MutationService(session).PlacementRemapId("prop", "q");
            Assert.Equal(staged.Effects.Describe(), session.LastNativeEffects!.Describe());
            Assert.Equal(renamed, FoliageEditorFixtures.Bytes(session));
            Assert.Equal(FoliageEditorFixtures.Reversed(staged.Effects), FoliageEditorFixtures.Channel(session.UndoNative()!));
            Assert.Equal(original, FoliageEditorFixtures.Bytes(session));
            Assert.Equal(FoliageEditorFixtures.Channel(staged.Effects), FoliageEditorFixtures.Channel(session.RedoNative()!));
            Assert.Equal(renamed, FoliageEditorFixtures.Bytes(session));
            session.Save();
            MapDocument saved = MapDocumentFile.Load(session.DocumentPath!);
            foreach (FoliageFixture reloaded in new[] { new FoliageFixture(saved, f.Assets), FoliageFixtures.Reload(new(saved, f.Assets), tiled: true) })
            {
                Assert.Equal(FoliageStorageFixtures.Json(gui.Foliage!), FoliageStorageFixtures.Json(reloaded.Document.Foliage!));
                Assert.Equal(gui.Foliage!.Layers[0].Chunks.Select(c => c.CopyDensity()), reloaded.Document.Foliage!.Layers[0].Chunks.Select(c => c.CopyDensity()));
                AssertOwner(reloaded.Document, Q);
                Assert.Equal(gui.Placements.Select(p => (p.Id, p.X, p.Z)).Order(), reloaded.Document.Placements.Select(p => (p.Id, p.X, p.Z)).Order());
            }

            // The released wire verb for a native placement edits its display label only, so no foliage row moves.
            await using var harness = await McpWire.StartAsync();
            Assert.NotEqual(true, (await harness.Client.CallAsync("map_open", new Dictionary<string, object?> { ["path"] = seed.DocumentPath })).IsError);
            var live = harness.Services.GetRequiredService<MapEditSession>();
            string foliage = FoliageEditorFixtures.Foliage(live);
            CallToolResult labelled = await harness.Client.CallAsync("placement_rename", new Dictionary<string, object?> { ["oldId"] = "prop", ["newId"] = "q" });
            Assert.NotEqual(true, labelled.IsError);
            Assert.Equal(foliage, FoliageEditorFixtures.Foliage(live));
            Assert.Equal(FoliageEditorFixtures.Channel([], []), FoliageEditorFixtures.Channel(live.LastNativeEffects!));
            AssertOwner(FoliageEditorFixtures.Fixture(live).Document, FoliageFixtures.Owner("prop"));
            new MutationService(live).PlacementRemapId("prop", "q");
            Assert.Equal(FoliageEditorFixtures.Channel(session.LastNativeEffects!), FoliageEditorFixtures.Channel(live.LastNativeEffects!));
            AssertOwner(FoliageEditorFixtures.Fixture(live).Document, Q);
        }
        finally
        {
            FoliageEditorFixtures.Delete(session);
            FoliageEditorFixtures.Delete(seed);
        }
    }

    static void AssertOwner(MapDocument document, MapPrefabReference owner)
    {
        MapFoliageLayer layer = document.Foliage!.Layers[0];
        Assert.Equal(owner, Assert.Single(layer.ExceptionMasks).Owner);
        Assert.Equal(owner, Assert.Single(layer.OwnerSupports).Owner);
    }
}
