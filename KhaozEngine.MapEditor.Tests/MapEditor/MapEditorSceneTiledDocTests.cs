using System;
using System.IO;
using System.Linq;
using KhaozEngine.Game;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using Xunit;
using TiledFixture = KhaozEngine.Tests.MapDoc.TiledDocFixture;

namespace KhaozEngine.Tests.MapEditor
{
    /// <summary>Headless tests for <see cref="MapEditorScene"/>'s tiled-document form support (#334 stage 3):
    /// the <see cref="MapDocumentFile.DetectForm"/>-aware load gate (the regression test for the two data-loss
    /// paths the design's review killed, see the design doc's "two ways it could have destroyed a world"
    /// section), the whole-load-vs-windowed dispatch (<see cref="MapDocumentWindowing"/>), and the
    /// moved-into-an-unloaded-tile save failure surfacing through the status strip instead of throwing. A
    /// separate file from <c>MapEditorSceneTests.cs</c> because that file is frozen by the KESIZE size
    /// ratchet at its current line count.</summary>
    public class MapEditorSceneTiledDocTests
    {
        // Records BuildWorld / TeardownWorld as no-ops (the MapEditorSceneTests.SpyScene idiom), but does NOT
        // override CreateDocument: these tests exercise the real DetectForm-based load dispatch.
        sealed class SpyScene : MapEditorScene
        {
            protected override void BuildWorld() { }
            protected override void TeardownWorld() { }
        }

        [Fact]
        public void OpenTiledDirectory_LoadsItsContent_NotBlank()
        {
            string dir = TiledFixture.NewDirectory();
            try
            {
                MapDocumentFile.SaveTiled(TiledFixture.SampleDoc(), dir);

                var scene = new SpyScene();
                scene.Init(null!, null!, null!, new MapEditorOptions { DocumentPath = dir });
                var m = new SceneManager();
                m.Push(scene);

                Assert.Equal("tiled-zone", scene.Document.Doc.Id);
                Assert.NotEmpty(scene.Document.Doc.Placements);
                Assert.Null(scene.Window);   // 4 occupied tiles, under the default 512-tile limit: whole loaded.
            }
            finally { TiledFixture.Delete(dir); }
        }

        [Fact]
        public void CtrlS_RoundTripsTiledDirectory_PreservesEveryTile()
        {
            string dir = TiledFixture.NewDirectory();
            try
            {
                MapDocument original = TiledFixture.SampleDoc();
                MapDocumentFile.SaveTiled(original, dir);
                string expectedHash = MapDocumentHash.OfWorld(MapDocumentFile.LoadTiled(dir));

                var scene = new SpyScene();
                scene.Init(null!, null!, null!, new MapEditorOptions { DocumentPath = dir });
                var m = new SceneManager();
                m.Push(scene);

                bool saved = scene.SaveDocument();

                Assert.True(saved);
                Assert.Contains("Saved", scene.StatusText);
                MapDocument reloaded = MapDocumentFile.LoadTiled(dir);
                Assert.Equal(expectedHash, MapDocumentHash.OfWorld(reloaded));
                Assert.Equal(original.Placements.Count, reloaded.Placements.Count);
                Assert.Equal(original.Spawns.Count, reloaded.Spawns.Count);
                Assert.Equal(original.PlayerSpawns.Count, reloaded.PlayerSpawns.Count);
            }
            finally { TiledFixture.Delete(dir); }
        }

        [Fact]
        public void WholeLoad_UnderLimit_LoadsEveryTileNoWindow()
        {
            string dir = TiledFixture.NewDirectory();
            try
            {
                MapDocumentFile.SaveTiled(TiledFixture.SampleDoc(), dir);

                var scene = new SpyScene();
                scene.Init(null!, null!, null!, new MapEditorOptions
                {
                    DocumentPath = dir,
                    WholeWorldTileLimit = 10,
                });
                var m = new SceneManager();
                m.Push(scene);

                Assert.Null(scene.Window);
                Assert.False(scene.Document.Doc.Tiles!.IsPartial);
                Assert.Equal(4, scene.Document.Doc.Tiles!.LoadedCount);
            }
            finally { TiledFixture.Delete(dir); }
        }

        [Fact]
        public void Windowed_OverLimit_LoadsOnlyTheWindow_StatusLineShowsIt()
        {
            string dir = TiledFixture.NewDirectory();
            try
            {
                MapDocumentFile.SaveTiled(TiledFixture.SampleDoc(), dir);

                var scene = new SpyScene();
                scene.Init(null!, null!, null!, new MapEditorOptions
                {
                    DocumentPath = dir,
                    WholeWorldTileLimit = 1,
                    EditorWindowRadius = 0,
                });
                var m = new SceneManager();
                m.Push(scene);

                Assert.NotNull(scene.Window);
                Assert.True(scene.Document.Doc.Tiles!.IsPartial);
                Assert.True(scene.Document.Doc.Tiles!.LoadedCount < scene.Document.Doc.Tiles!.Entries.Count);
                Assert.Contains("window:", scene.StatusLine());
            }
            finally { TiledFixture.Delete(dir); }
        }

        [Fact]
        public void SaveDocument_MovedContentIntoUnloadedTile_SurfacesAsStatusNotCrash()
        {
            string dir = TiledFixture.NewDirectory();
            try
            {
                MapDocumentFile.SaveTiled(TiledFixture.SampleDoc(), dir);

                var scene = new SpyScene();
                scene.Init(null!, null!, null!, new MapEditorOptions
                {
                    DocumentPath = dir,
                    WholeWorldTileLimit = 1,
                    EditorWindowRadius = 0,
                });
                var m = new SceneManager();
                m.Push(scene);

                // p-a is loaded (tile (0, 0)); move it into tile (-2, 0), which the index marks occupied but
                // this window never loaded.
                MapPlacement moved = scene.Document.Doc.Placements.Single(p => p.Id == "p-a");
                moved.X = -600f;

                bool saved = scene.SaveDocument();

                Assert.False(saved);
                Assert.Contains("Save failed", scene.StatusText);
                Assert.Contains("p-a", scene.StatusText);
            }
            finally { TiledFixture.Delete(dir); }
        }

        [Fact]
        public void OpenBlankPath_StillStartsUntitled_NoWindow()
        {
            // A None-form path (nothing on disk) must still behave exactly as before: a blank untitled
            // document, not a DetectForm throw.
            var scene = new SpyScene();
            scene.Init(null!, null!, null!, new MapEditorOptions
            {
                DocumentPath = Path.Combine(Path.GetTempPath(), "ke-editor-tiled-tests", Guid.NewGuid().ToString("N") + ".map.json"),
            });
            var m = new SceneManager();
            m.Push(scene);

            Assert.Equal("untitled", scene.Document.Doc.Id);
            Assert.Null(scene.Window);
        }
    }
}
