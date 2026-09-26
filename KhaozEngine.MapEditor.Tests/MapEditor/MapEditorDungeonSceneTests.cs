using System;
using KhaozEngine.Dungeon;
using KhaozEngine.Game;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using Xunit;
using TiledFixture = KhaozEngine.Tests.MapDoc.TiledDocFixture;

namespace KhaozEngine.Tests.MapEditor;

public class MapEditorDungeonSceneTests
{
    private sealed class SpyScene : MapEditorScene
    {
        public int CameraSteps { get; private set; }
        public int ToolSteps { get; private set; }

        protected override void BuildWorld() { }
        protected override void TeardownWorld() { }
        protected override void UpdateCamera(float dt) => CameraSteps++;
        protected override void UpdateTools(float dt) => ToolSteps++;
    }

    private static SpyScene Open(MapEditorOptions options)
    {
        var scene = new SpyScene();
        scene.Init(null!, null!, null!, options);
        new SceneManager().Push(scene);
        return scene;
    }

    [Fact]
    public void GenerateAction_IsAbsentWithoutAGameKit()
    {
        SpyScene scene = Open(new MapEditorOptions());

        Assert.Null(scene.GenerateDungeonButton);
        Assert.Null(scene.DungeonDialog);
    }

    [Fact]
    public void InvalidGamePreset_ReportsErrorInsteadOfCrashingOnOpen()
    {
        SpyScene scene = Open(new MapEditorOptions
        {
            DungeonKit = DungeonKitMap.Greybox(),
            DungeonPreset = new DungeonConfig { CellSizeMeters = 0f },
        });

        scene.GenerateDungeonButton!.OnClick!.Invoke();

        Assert.Null(scene.DungeonDialog);
        Assert.Contains("preset", scene.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.False(scene.Document.IsDirty);
    }

    [Fact]
    public void GenerateButton_OpensModalThatBlocksToolsAndCancelsCleanly()
    {
        SpyScene scene = Open(new MapEditorOptions { DungeonKit = DungeonKitMap.Greybox() });
        string before = MapDocumentHash.OfWorld(scene.Document.Doc);

        scene.GenerateDungeonButton!.OnClick!.Invoke();
        Assert.NotNull(scene.DungeonDialog);
        scene.OnUpdate(1f / 60f);
        Assert.Equal(0, scene.CameraSteps);
        Assert.Equal(0, scene.ToolSteps);

        scene.DungeonDialog!.CancelButton.OnClick!.Invoke();
        scene.OnUpdate(1f / 60f);

        Assert.Null(scene.DungeonDialog);
        Assert.Equal(before, MapDocumentHash.OfWorld(scene.Document.Doc));
        Assert.False(scene.Document.IsDirty);
        Assert.False(scene.Document.History.CanUndo);
    }

    [Fact]
    public void ValidGeneration_ChangesMapThroughOneUndoStep()
    {
        SpyScene scene = Open(new MapEditorOptions
        {
            DungeonKit = DungeonKitMap.Greybox(),
            DungeonPreset = new DungeonConfig { RoomCountTarget = 4, LockCount = 0 },
        });
        string before = MapDocumentHash.OfWorld(scene.Document.Doc);
        scene.OpenDungeonDialog();
        scene.DungeonDialog!.SeedText = "42";

        scene.TryGenerateDungeon();

        Assert.Null(scene.DungeonDialog);
        Assert.NotEqual(before, MapDocumentHash.OfWorld(scene.Document.Doc));
        Assert.True(scene.Document.IsDirty);
        Assert.True(scene.Document.WorldRebuildPending);
        Assert.Equal("Generate dungeon", scene.Document.History.UndoLabel);
        Assert.True(scene.Document.Undo());
        Assert.Equal(before, MapDocumentHash.OfWorld(scene.Document.Doc));
    }

    [Fact]
    public void WindowCrossingGeneration_ReportsErrorWithoutDirtyingMap()
    {
        string dir = TiledFixture.NewDirectory();
        try
        {
            MapDocumentFile.SaveTiled(TiledFixture.SampleDoc(), dir);
            SpyScene scene = Open(new MapEditorOptions
            {
                DocumentPath = dir,
                WholeWorldTileLimit = 1,
                EditorWindowRadius = 0,
                DungeonKit = DungeonKitMap.Greybox(),
            });
            Assert.NotNull(scene.Window);
            string before = MapDocumentHash.OfWorld(scene.Document.Doc);
            scene.OpenDungeonDialog();
            scene.DungeonDialog!.OriginX = 10000f;
            scene.DungeonDialog.OriginZ = 10000f;

            scene.TryGenerateDungeon();

            Assert.NotNull(scene.DungeonDialog);
            Assert.Contains("window", scene.DungeonDialog!.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(before, MapDocumentHash.OfWorld(scene.Document.Doc));
            Assert.False(scene.Document.IsDirty);
            Assert.False(scene.Document.History.CanUndo);
        }
        finally { TiledFixture.Delete(dir); }
    }
}
