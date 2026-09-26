using System;
using KhaozEngine.Dungeon;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using Xunit;

namespace KhaozEngine.Tests.MapEditor;

public class GenerateDungeonCommandTests
{
    private static DungeonConfig Config() => new()
    {
        RoomCountTarget = 4,
        RoomMinTiles = 3,
        RoomMaxTiles = 4,
        PlotWidthTiles = 24,
        PlotDepthTiles = 24,
        MaxFloors = 1,
        LockCount = 0,
        LoopEdgeBudget = 0,
        CriticalPathTarget = 2,
    };

    private static MapDocument Document() => new()
    {
        Id = "zone",
        DisplayName = "Zone",
        Bounds = new MapBounds { MinX = -10f, MinZ = -20f, MaxX = 10f, MaxZ = 20f },
        Placements = { new MapPlacement { Id = "existing", Kind = "tree", X = 1f, Z = 2f } },
    };

    private static DungeonPlotTransform Plot() => new(100f, 200f, 3f, 0f);

    [Fact]
    public void ExecuteUndoRedo_RestoresExactMapAndOneHistoryStep()
    {
        MapDocument doc = Document();
        var editor = new EditorDocument(doc);
        string before = MapDocumentHash.OfWorld(doc);
        editor.MarkSaved();

        editor.Execute(new GenerateDungeonCommand(Config(), 42UL, DungeonKitMap.Greybox(), Plot()));

        string baked = MapDocumentHash.OfWorld(doc);
        Assert.NotEqual(before, baked);
        Assert.True(doc.Placements.Count > 1);
        Assert.NotEmpty(doc.Regions);
        Assert.NotEmpty(doc.Terrain.Features);
        Assert.True(editor.IsDirty);
        Assert.True(editor.WorldRebuildPending);
        Assert.Equal("Generate dungeon", editor.History.UndoLabel);

        Assert.True(editor.Undo());
        Assert.Equal(before, MapDocumentHash.OfWorld(doc));
        Assert.False(editor.IsDirty);
        Assert.Equal("existing", Assert.Single(doc.Placements).Id);

        Assert.True(editor.Redo());
        Assert.Equal(baked, MapDocumentHash.OfWorld(doc));
        Assert.True(editor.IsDirty);
    }

    [Fact]
    public void Redo_ReusesTheFirstBakeAfterKitMappingChanges()
    {
        MapDocument doc = Document();
        var editor = new EditorDocument(doc);
        DungeonKitMap kit = DungeonKitMap.Greybox();
        var command = new GenerateDungeonCommand(Config(), 42UL, kit, Plot());
        editor.Execute(command);
        string baked = MapDocumentHash.OfWorld(doc);

        Assert.True(editor.Undo());
        kit.Map(DungeonPiece.Floor, "different-floor");
        Assert.True(editor.Redo());

        Assert.Equal(baked, MapDocumentHash.OfWorld(doc));
        Assert.DoesNotContain(doc.Placements, p => p.Kind == "different-floor");
    }

    [Fact]
    public void RepeatingTheSameBake_RejectsDuplicateIdsWithoutAnUndoStep()
    {
        MapDocument doc = Document();
        var editor = new EditorDocument(doc);
        editor.Execute(new GenerateDungeonCommand(Config(), 42UL, DungeonKitMap.Greybox(), Plot()));
        string first = MapDocumentHash.OfWorld(doc);
        int depth = editor.History.UndoDepth;

        Assert.Throws<InvalidOperationException>(() =>
            editor.Execute(new GenerateDungeonCommand(Config(), 42UL, DungeonKitMap.Greybox(), Plot())));

        Assert.Equal(first, MapDocumentHash.OfWorld(doc));
        Assert.Equal(depth, editor.History.UndoDepth);
    }

    [Fact]
    public void MissingKitPiece_DoesNotPartiallyAppendOrEnterHistory()
    {
        MapDocument doc = Document();
        var editor = new EditorDocument(doc);
        string before = MapDocumentHash.OfWorld(doc);
        var kit = new DungeonKitMap();
        kit.Map(DungeonPiece.Floor, "floor");

        Assert.Throws<InvalidOperationException>(() =>
            editor.Execute(new GenerateDungeonCommand(Config(), 42UL, kit, Plot())));

        Assert.Equal(before, MapDocumentHash.OfWorld(doc));
        Assert.False(editor.History.CanUndo);
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public void InvalidConfig_LeavesDocumentUntouched()
    {
        MapDocument doc = Document();
        var editor = new EditorDocument(doc);
        string before = MapDocumentHash.OfWorld(doc);
        DungeonConfig invalid = Config();
        invalid.CellSizeMeters = 0f;

        Assert.Throws<DungeonJsonException>(() =>
            editor.Execute(new GenerateDungeonCommand(invalid, 42UL, DungeonKitMap.Greybox(), Plot())));

        Assert.Equal(before, MapDocumentHash.OfWorld(doc));
        Assert.False(editor.History.CanUndo);
    }

    [Fact]
    public void NonFinitePlot_LeavesDocumentUntouched()
    {
        MapDocument doc = Document();
        var editor = new EditorDocument(doc);
        string before = MapDocumentHash.OfWorld(doc);

        Assert.Throws<ArgumentException>(() =>
            editor.Execute(new GenerateDungeonCommand(Config(), 42UL, DungeonKitMap.Greybox(),
                new DungeonPlotTransform(float.NaN, 0f, 0f, 0f))));

        Assert.Equal(before, MapDocumentHash.OfWorld(doc));
        Assert.False(editor.History.CanUndo);
    }

    [Fact]
    public void PartialTiledDocument_RequiresAWindowAndRejectsACrossingPlot()
    {
        MapDocument doc = Document();
        doc.TileSize = 512f;
        doc.Tiles = new MapTileIndex(512f, MapDocumentHash.SchemeVersion, "/tmp/zone", new[]
        {
            new MapTileEntry(new MapTileCoord(0, 0), new string('0', 64), Loaded: true),
            new MapTileEntry(new MapTileCoord(1, 0), new string('1', 64), Loaded: false),
        });
        var editor = new EditorDocument(doc);
        var crossing = new DungeonPlotTransform(500f, 100f, 0f, 0f);
        var window = new MapTileRect(new MapTileCoord(0, 0), new MapTileCoord(0, 0));

        Assert.Throws<InvalidOperationException>(() =>
            editor.Execute(new GenerateDungeonCommand(Config(), 42UL, DungeonKitMap.Greybox(), crossing)));
        Assert.Throws<InvalidOperationException>(() =>
            editor.Execute(new GenerateDungeonCommand(Config(), 42UL, DungeonKitMap.Greybox(), crossing,
                loadedWindow: window)));

        Assert.Equal("existing", Assert.Single(doc.Placements).Id);
        Assert.False(editor.History.CanUndo);
        Assert.False(editor.IsDirty);
    }
}
