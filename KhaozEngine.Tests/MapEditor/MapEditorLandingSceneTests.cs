using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.Game;
using KhaozEngine.Gui;
using KhaozEngine.MapEditor;
using Xunit;

namespace KhaozEngine.Tests.MapEditor
{
    /// <summary>Headless tests for <see cref="MapEditorLandingScene"/> (decision 6): opening a recent map pushes the
    /// editor and touches the store, a missing recent is pruned with a note instead of crashing, New Map validates
    /// the typed name before calling the create hook, and Quit routes to the wired quit action. The scene's
    /// widget-drive is viewport-gated (null <c>UiViewport</c> headless), so these drive the internal action seams the
    /// buttons and the Enter key both call, the way <c>MapEditorSceneTests</c> drives <c>SaveDocument</c>.</summary>
    public class MapEditorLandingSceneTests
    {
        // A bare scene the OpenEditor hook returns as its "built editor" stand-in, so a Push is observable headless.
        sealed class StubEditorScene : GameScene { }

        static MapEditorLandingScene Landing(MapEditorLandingOptions options)
        {
            var scene = new MapEditorLandingScene();
            scene.Init(null!, null!, options);
            return scene;
        }

        static EditorRecentFiles NewStore() => new EditorRecentFiles(new InMemorySettingsStorage());

        [Fact]
        public void Landing_OpenRecent_PushesEditorScene_TouchesStore()
        {
            string existing = Path.Combine(Path.GetTempPath(), "ke-landing-" + Path.GetRandomFileName() + ".map.json");
            File.WriteAllText(existing, "{}");
            try
            {
                EditorRecentFiles store = NewStore();
                store.Touch("/maps/other.map.json");
                store.Touch(existing);
                store.Touch("/maps/newest.map.json");   // existing is now the middle entry, not the front

                string? opened = null;
                var scene = Landing(new MapEditorLandingOptions
                {
                    Recent = store,
                    OpenEditor = path => { opened = path; return new StubEditorScene(); },
                });
                var m = new SceneManager();
                m.Push(scene);
                Assert.Equal(1, m.Count);

                scene.ActivateRecent(existing);

                Assert.Equal(existing, opened);          // the editor was built for the activated path
                Assert.Equal(2, m.Count);                // and pushed on top of the landing scene
                Assert.Equal(existing, store.Paths[0]);  // activation touched it to the front
            }
            finally { if (File.Exists(existing)) File.Delete(existing); }
        }

        [Fact]
        public void Landing_MissingRecent_PrunedWithNote()
        {
            string missing = Path.Combine(Path.GetTempPath(), "ke-landing-missing-" + Guid.NewGuid().ToString("N") + ".map.json");
            EditorRecentFiles store = NewStore();
            store.Touch(missing);   // recorded, but the file does not exist on disk
            Assert.Contains(missing, store.Paths);

            bool opened = false;
            var scene = Landing(new MapEditorLandingOptions
            {
                Recent = store,
                OpenEditor = _ => { opened = true; return new StubEditorScene(); },
            });
            var m = new SceneManager();
            m.Push(scene);

            scene.ActivateRecent(missing);

            Assert.False(opened);                       // no editor opened for a missing file
            Assert.Equal(1, m.Count);                   // nothing pushed
            Assert.DoesNotContain(missing, store.Paths);   // pruned from the store
            Assert.False(string.IsNullOrEmpty(scene.Note));   // and a note explains why
        }

        [Fact]
        public void Landing_NewMap_ValidatesName_CallsCreateMap()
        {
            var created = new List<string>();
            string? createResult = "/maps/meadow.map.json";
            EditorRecentFiles store = NewStore();
            string? opened = null;

            var scene = Landing(new MapEditorLandingOptions
            {
                Recent = store,
                CreateMap = name => { created.Add(name); return createResult; },
                OpenEditor = path => { opened = path; return new StubEditorScene(); },
            });
            var m = new SceneManager();
            m.Push(scene);

            // Empty (after trim) is rejected without calling the create hook or pushing anything.
            scene.CreateMapNamed("   ");
            Assert.Empty(created);
            Assert.Equal(1, m.Count);
            Assert.False(string.IsNullOrEmpty(scene.Note));

            // A name with a path separator is rejected the same way.
            scene.CreateMapNamed("a/b");
            Assert.Empty(created);
            scene.CreateMapNamed("a\\b");
            Assert.Empty(created);
            Assert.Equal(1, m.Count);

            // A valid name calls CreateMap (trimmed), touches the returned path, and pushes the editor for it.
            scene.CreateMapNamed("  meadow  ");
            Assert.Equal(new[] { "meadow" }, created);
            Assert.Equal("/maps/meadow.map.json", opened);
            Assert.Equal("/maps/meadow.map.json", store.Paths[0]);
            Assert.Equal(2, m.Count);

            // A create hook that returns null (the head's file IO failed) shows a failure note and pushes nothing.
            createResult = null;
            scene.CreateMapNamed("swamp");
            Assert.Equal(new[] { "meadow", "swamp" }, created);   // hook was called
            Assert.Equal(2, m.Count);                             // but nothing new was pushed
            Assert.False(string.IsNullOrEmpty(scene.Note));
        }

        [Fact]
        public void Landing_Quit_InvokesRequestQuit()
        {
            bool quit = false;
            var scene = Landing(new MapEditorLandingOptions { RequestQuit = () => quit = true });
            new SceneManager().Push(scene);

            scene.RequestQuitLanding();

            Assert.True(quit);

            // With no quit action wired, the same call is a safe no-op that leaves a note instead of throwing.
            var noQuit = Landing(new MapEditorLandingOptions());
            new SceneManager().Push(noQuit);
            noQuit.RequestQuitLanding();
            Assert.False(string.IsNullOrEmpty(noQuit.Note));
        }
    }
}
