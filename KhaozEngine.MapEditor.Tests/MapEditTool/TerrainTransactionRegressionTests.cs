using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
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

    [Fact]
    public void Undo_ReportsOwnerChangesFromNewToOld()
    {
        EditorDocument editor = TransactionFixtures.NativeWithSurfaces();
        var command = new TerrainEditCommand(new MapReplaceTopology(new[] { TerrainEditFixtures.GroundBelowSlot() },
            Array.Empty<MapPatchKey>(), Array.Empty<MapSurfaceRef>(), Array.Empty<string>()));
        editor.Execute(command);
        var below = new MapPatchKey("ground", 0, -1);
        var owner = new MapPatchKey("ground", 0, 0);
        INativeDocumentCommand native = command;
        MapCornerOwnerChange[] forward = native.Prepare(NativeDocumentSnapshot.Clone(editor.Doc, editor.Registry), false)
            .WriteSet.OwnerChanges.ToArray();
        MapCornerOwnerChange[] backward = native.Prepare(NativeDocumentSnapshot.Clone(editor.Doc, editor.Registry), true)
            .WriteSet.OwnerChanges.ToArray();
        Assert.Equal(5, forward.Length);
        Assert.All(forward, c =>
        {
            Assert.Equal(below, c.Dependent);
            Assert.Null(c.OldOwner);
            Assert.Equal(new MapVertexOwner(owner, MapLatticeAddress.Corner(60 + c.CornerX, 0)), c.NewOwner);
        });
        Assert.Equal(forward.Select(c => new MapCornerOwnerChange(below, c.CornerX, c.CornerZ, c.NewOwner,
            new MapVertexOwner(below, MapLatticeAddress.Corner(60 + c.CornerX, 0)))), backward);
    }
}
