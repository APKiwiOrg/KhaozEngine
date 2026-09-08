using System;
using System.IO;
using KhaozEngine.Game;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using Xunit;

namespace KhaozEngine.Tests.MapEditor;

public partial class MapEditorSceneTests
{
    sealed class ReloadScene : MapEditorScene
    {
        public bool FailReloadBuild;
        public int ReloadBuilds;

        protected override void BuildWorld() { }
        protected override void TeardownWorld() { }

        protected override ViewportWorld? BuildReloadViewport(EditorDocument candidate)
        {
            ReloadBuilds++;
            if (FailReloadBuild)
                throw new MapDocumentException("scripted reload build failure");
            return null;
        }
    }

    static void WriteReloadDocument(string path, string id)
    {
        MapDocumentFile.Save(new MapDocument
        {
            Id = id,
            Bounds = new MapBounds { MinX = -20f, MinZ = -20f, MaxX = 20f, MaxZ = 20f },
        }, path, MapDocRegistry.CreateDefault());
    }

    static (ReloadScene Scene, SceneManager Manager) OpenReloadScene(string path)
    {
        var scene = new ReloadScene();
        scene.Init(null!, null!, null!, new MapEditorOptions { DocumentPath = path });
        var manager = new SceneManager();
        manager.Push(scene);
        return (scene, manager);
    }

    [Fact]
    public void Reload_replaces_a_clean_document_with_the_current_disk_version()
    {
        string path = TempPath();
        try
        {
            WriteReloadDocument(path, "before");
            (ReloadScene scene, _) = OpenReloadScene(path);
            EditorDocument before = scene.Document;
            WriteReloadDocument(path, "after");

            Assert.True(scene.ReloadDocument());

            Assert.NotSame(before, scene.Document);
            Assert.Equal("after", scene.Document.Doc.Id);
            Assert.False(scene.Document.IsDirty);
            Assert.Equal(1, scene.ReloadBuilds);
            Assert.Contains("Reloaded", scene.StatusText, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Reload_refuses_to_discard_unsaved_edits()
    {
        string path = TempPath();
        try
        {
            WriteReloadDocument(path, "before");
            (ReloadScene scene, _) = OpenReloadScene(path);
            scene.Document.Execute(new EditTerrainCommand(
                newSeed: 42, oldSeed: scene.Document.Doc.Terrain.Seed));
            WriteReloadDocument(path, "after");

            Assert.False(scene.ReloadDocument());

            Assert.Equal("before", scene.Document.Doc.Id);
            Assert.Equal(42, scene.Document.Doc.Terrain.Seed);
            Assert.True(scene.Document.IsDirty);
            Assert.Equal(0, scene.ReloadBuilds);
            Assert.Contains("unsaved", scene.StatusText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Reload_load_failure_keeps_the_current_document()
    {
        string path = TempPath();
        try
        {
            WriteReloadDocument(path, "before");
            (ReloadScene scene, _) = OpenReloadScene(path);
            EditorDocument before = scene.Document;
            File.WriteAllText(path, "{ this is not json");

            Assert.False(scene.ReloadDocument());

            Assert.Same(before, scene.Document);
            Assert.Equal("before", scene.Document.Doc.Id);
            Assert.Equal(0, scene.ReloadBuilds);
            Assert.Contains("failed", scene.StatusText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Reload_world_build_failure_keeps_the_current_document()
    {
        string path = TempPath();
        try
        {
            WriteReloadDocument(path, "before");
            (ReloadScene scene, _) = OpenReloadScene(path);
            EditorDocument before = scene.Document;
            WriteReloadDocument(path, "after");
            scene.FailReloadBuild = true;

            Assert.False(scene.ReloadDocument());

            Assert.Same(before, scene.Document);
            Assert.Equal("before", scene.Document.Doc.Id);
            Assert.Equal(1, scene.ReloadBuilds);
            Assert.Contains("scripted reload build failure", scene.StatusText, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Reload_shortcut_waits_until_editor_text_focus_is_released()
    {
        string path = TempPath();
        try
        {
            WriteReloadDocument(path, "before");
            (ReloadScene scene, SceneManager manager) = OpenReloadScene(path);
            WriteReloadDocument(path, "after");
            scene.Controller.Mode = EditorToolMode.PlacePlacement;
            scene.PaletteFilter.Focus();

            manager.Input = CtrlKeyFrame(KhaozEngine.Windowing.Key.R);
            manager.Update(0.016f);
            Assert.Equal("before", scene.Document.Doc.Id);
            Assert.Equal(0, scene.ReloadBuilds);
            Assert.False(scene.ReloadDocument());
            Assert.Contains("focus", scene.StatusText, StringComparison.OrdinalIgnoreCase);

            scene.PaletteFilter.Unfocus();
            manager.Input = CtrlKeyFrame(KhaozEngine.Windowing.Key.R);
            manager.Update(0.016f);
            Assert.Equal("after", scene.Document.Doc.Id);
            Assert.Equal(1, scene.ReloadBuilds);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
