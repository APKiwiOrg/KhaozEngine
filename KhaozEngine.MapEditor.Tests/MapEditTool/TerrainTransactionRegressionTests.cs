using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapEditor;
using KhaozEngine.Tests.MapDoc;
using Xunit;

namespace KhaozEngine.Tests.MapEditTool;

public sealed class TerrainTransactionRegressionTests
{
    static string Text(EditorDocument editor) => MapDocumentFile.SaveText(editor.Doc, editor.Registry);
    static string[] Ids(MapDocument doc) => doc.Surfaces.Refs.Select(s => s.Id).ToArray();

    [Fact]
    public void TerrainFrameUndoAndRedoReportBoundsInTheStandaloneSurfaceWorldFrame()
    {
        // Regression #1903: editor history replays the effective bounds in either direction.
        MapDocument document = SurfaceStorageFixtures.ThreeSurfaces();
        MapSurfaceRef ridge = document.Surfaces.Refs.Single(s => s.Id == "ridge");
        document.NativeResourceTransforms =
            [new(MapPrefabChildKind.Surface, ridge.Id, new(64, 5, 256, Math.PI / 2, 2), new string('a', 64))];
        var edit = new MapReplaceTopology([], [], [ridge with { Frame = ridge.Frame with { HeightUnitMetres = new(1, 50) } }], []);
        MapTerrainEditResult local = MapTerrainEdits.Prepare(document.Surfaces, edit);
        Assert.Equal(new MapBox3(60, 15, 0, 64, 15, 4), local.Effects.OldBounds);

        var editor = new EditorDocument(document);
        editor.BindNativeAssets(SurfaceStorageFixtures.Assets());
        editor.Execute(new TerrainEditCommand(edit));
        var before = new MapBox3(64, 35, 128, 72, 35, 136);
        var after = new MapBox3(64, 65, 128, 72, 65, 136);
        Assert.Equal((before, after), Bounds());
        Assert.True(editor.Undo());
        Assert.Equal((after, before), Bounds());
        Assert.True(editor.Redo());
        Assert.Equal((before, after), Bounds());

        (MapBox3?, MapBox3?) Bounds() => (editor.LastNativeEffects!.OldBounds, editor.LastNativeEffects.NewBounds);
    }

    [Fact]
    public void TopologyUndo_RestoresARemovedLeadingSurfaceAtItsPosition()
    {
        EditorDocument editor = TransactionFixtures.NativeWithSurfaces();
        string before = Text(editor);
        Assert.Equal(new[] { "ground", "ridge", "far" }, Ids(editor.Doc));
        var removeGround = new MapReplaceTopology(Array.Empty<MapSurfacePatch>(),
            new MapPatchKey[] { new("ground", 0, 0), new("ground", 1, 0) }, Array.Empty<MapSurfaceRef>(), new[] { "ground" });
        editor.Execute(new TerrainEditCommand(removeGround));
        string removed = Text(editor);
        Assert.Equal(new[] { "ridge", "far" }, Ids(editor.Doc));
        Assert.True(editor.Undo());
        Assert.Equal(new[] { "ground", "ridge", "far" }, Ids(editor.Doc));
        Assert.Equal(before, Text(editor));
        Assert.True(editor.Redo());
        Assert.Equal(removed, Text(editor));
        Assert.True(editor.Undo());
        Assert.Equal(before, Text(editor));
    }

    [Fact]
    public void PublishWriteSet_PlacesAnAddedSurfaceRefInCandidateOrder()
    {
        MapDocument candidate = SurfaceStorageFixtures.ThreeSurfaces();
        MapDocument target = SurfaceStorageFixtures.ThreeSurfaces();
        target.Surfaces.Refs.RemoveAll(s => s.Id == "ridge");
        var writes = new MapNativeWriteSet(Array.Empty<MapPatchKey>(), new[] { "ridge" }, Array.Empty<string>(),
            Array.Empty<string>(), Array.Empty<MapCornerOwnerChange>(), Array.Empty<ushort>(), false);
        NativeDocumentSnapshot.PublishWriteSet(target, candidate, writes);
        Assert.Equal(new[] { "ground", "ridge", "far" }, Ids(target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Undo_ReportsOwnerChangesOnlyForResultPatches_FromNewToOld(bool patchSurvives)
    {
        // Regression #1337: undo must agree with a forward edit to the same result, including removed patches.
        EditorDocument editor = TransactionFixtures.NativeWithSurfaces();
        MapSurfacePatch original = TerrainEditFixtures.GroundBelowSlot();
        if (patchSurvives)
        {
            // Move an existing self-owned patch onto the shared edge, so undo keeps the patch.
            original.CellMinZ--;
            editor.Execute(new TerrainEditCommand(new MapReplaceTopology(new[] { original },
                Array.Empty<MapPatchKey>(), Array.Empty<MapSurfaceRef>(), Array.Empty<string>())));
        }
        string before = Text(editor);
        var command = new TerrainEditCommand(new MapReplaceTopology(new[] { TerrainEditFixtures.GroundBelowSlot() },
            Array.Empty<MapPatchKey>(), Array.Empty<MapSurfaceRef>(), Array.Empty<string>()));
        editor.Execute(command);
        string after = Text(editor);
        var below = new MapPatchKey("ground", 0, -1);
        var owner = new MapPatchKey("ground", 0, 0);
        INativeDocumentCommand native = command;
        MapCornerOwnerChange[] forward = native.Prepare(NativeDocumentSnapshot.Clone(editor.Doc, editor.Registry), false)
            .WriteSet.OwnerChanges.ToArray();
        MapDocument undone = NativeDocumentSnapshot.Clone(editor.Doc, editor.Registry);
        MapCornerOwnerChange[] backward = native.Prepare(undone, true).WriteSet.OwnerChanges.ToArray();
        var inverse = patchSurvives
            ? new MapReplaceTopology(new[] { original }, Array.Empty<MapPatchKey>(), Array.Empty<MapSurfaceRef>(), Array.Empty<string>())
            : new MapReplaceTopology(Array.Empty<MapSurfacePatch>(), new[] { below }, Array.Empty<MapSurfaceRef>(), Array.Empty<string>());
        MapTerrainEditResult reversed = MapTerrainEdits.Prepare(editor.Doc.Surfaces, inverse);
        Assert.Equal(5, forward.Length);
        Assert.All(forward, c =>
        {
            Assert.Equal(below, c.Dependent);
            Assert.Null(c.OldOwner);
            Assert.Equal(new MapVertexOwner(owner, MapLatticeAddress.Corner(60 + c.CornerX, 0)), c.NewOwner);
        });
        Assert.Equal(patchSurvives, undone.Surfaces.Patches.ContainsKey(below));
        Assert.Equal(patchSurvives, reversed.Candidate.Patches.ContainsKey(below));
        Assert.Equal(reversed.WriteSet.OwnerChanges, backward);
        Assert.All(backward, c => Assert.True(undone.Surfaces.Patches.ContainsKey(c.Dependent)));
        if (patchSurvives)
            Assert.Equal(forward.Select(c => new MapCornerOwnerChange(below, c.CornerX, c.CornerZ, c.NewOwner,
                new MapVertexOwner(below, original.CornerAddress(c.CornerX, c.CornerZ)))), backward);
        else Assert.Empty(backward);
        Assert.True(editor.Undo());
        Assert.Equal(before, Text(editor));
        Assert.Equal(forward, native.Prepare(NativeDocumentSnapshot.Clone(editor.Doc, editor.Registry), false).WriteSet.OwnerChanges);
        Assert.True(editor.Redo());
        Assert.Equal(after, Text(editor));
    }
}
