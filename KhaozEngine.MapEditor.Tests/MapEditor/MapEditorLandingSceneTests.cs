using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Gui;
using KhaozEngine.MapEditor;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.MapEditor
{
    /// <summary>Headless tests for <see cref="MapEditorLandingScene"/> (decision 6): opening a recent map pushes the
    /// editor and touches the store, a missing recent is pruned with a note instead of crashing, New Map validates
    /// the typed name before calling the create hook, and Quit routes to the wired quit action. Also covers the Open
    /// Map section (issue 359): normalizing and sorting a head's <c>DiscoverMaps</c> result, filtering out a path
    /// already in the recents store, activating a discovered map (migrating it into Open Recent, or re-querying with
    /// a note when the file has vanished), the unwired (null hook) case, a real tap driven through
    /// <c>SceneManager.Update</c>, the re-query-on-re-expose requirement, and a long scrollable list. The
    /// scene's widget-drive is viewport-gated (null <c>UiViewport</c> headless), so these drive the internal action
    /// seams the buttons and the Enter key both call, the way <c>MapEditorSceneTests</c> drives
    /// <c>SaveDocument</c>.</summary>
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

        [Fact]
        public void Landing_OnUpdate_RefreshesRecentList_WhenStoreMutatedExternally()
        {
            EditorRecentFiles store = NewStore();
            store.Touch("/maps/alpha.map.json");

            var scene = Landing(new MapEditorLandingOptions { Recent = store });
            var m = new SceneManager();
            m.Push(scene);   // OnEnter already builds one button from the store's current contents
            m.UiViewport = new UiViewport(800, 600, 800, 600);

            Assert.Equal(1, scene.RecentButtonCount);

            m.Input = InputState.Empty;
            m.Update(0.016f);   // nothing changed since OnEnter: no rebuild needed, still 1
            Assert.Equal(1, scene.RecentButtonCount);

            // Mutate the store directly, bypassing the landing scene's own Touch/ActivateRecent seam entirely (the
            // stand-in for a future Save-As happening from the editor scene pushed on top of this menu). SceneManager
            // gives this scene no re-exposure hook, so the only way it can notice is the live-vs-cached compare in
            // RefreshRecentIfChanged, run every driven frame.
            store.Touch("/maps/beta.map.json");
            Assert.Equal(1, scene.RecentButtonCount);   // not yet rebuilt: no OnUpdate has run since the mutation

            m.Update(0.016f);

            Assert.Equal(2, scene.RecentButtonCount);   // this frame's OnUpdate noticed the change and rebuilt
        }

        [Fact]
        public void Landing_OnUpdate_EnterInFocusedNameField_CommitsCreate()
        {
            var created = new List<string>();
            string? opened = null;
            EditorRecentFiles store = NewStore();

            var scene = Landing(new MapEditorLandingOptions
            {
                Recent = store,
                CreateMap = name => { created.Add(name); return "/maps/" + name + ".map.json"; },
                OpenEditor = path => { opened = path; return new StubEditorScene(); },
            });
            var m = new SceneManager();
            m.Push(scene);
            m.UiViewport = new UiViewport(800, 600, 800, 600);

            // Focus the field directly (as a click inside its bounds would) and type via SetText, then drive one
            // REAL OnUpdate frame with Enter pressed: this exercises the actual TextInput.Update + Enter-commits-Create
            // wiring in MapEditorLandingScene.OnUpdate, not the internal CreateMapNamed seam the other tests call.
            scene.NameInput.SetText("meadow");
            scene.NameInput.Focus();

            m.Input = KeyFrame(Key.Enter);
            m.Update(0.016f);

            Assert.Equal(new[] { "meadow" }, created);
            Assert.Equal("/maps/meadow.map.json", opened);
            Assert.Equal(2, m.Count);   // the stub editor was pushed on top
        }

        [Fact]
        public void Landing_OnUpdate_TapRecentButton_ActivatesRecent()
        {
            string existing = Path.Combine(Path.GetTempPath(), "ke-landing-tap-" + Path.GetRandomFileName() + ".map.json");
            File.WriteAllText(existing, "{}");
            try
            {
                EditorRecentFiles store = NewStore();
                store.Touch(existing);

                string? opened = null;
                var scene = Landing(new MapEditorLandingOptions
                {
                    Recent = store,
                    OpenEditor = path => { opened = path; return new StubEditorScene(); },
                });
                var m = new SceneManager();
                m.Push(scene);
                m.UiViewport = new UiViewport(800, 600, 800, 600);

                m.Input = InputState.Empty;
                m.Update(0.016f);   // lays out this frame's recent button, so its Bounds are current

                Button button = scene.RecentButtonAt(0) ?? throw new InvalidOperationException("expected a recent button");
                var at = new Vector2(button.Bounds.X + button.Bounds.Width * 0.5f, button.Bounds.Y + button.Bounds.Height * 0.5f);

                // A real press-then-release tap at the button's center (the TapTree/TapGrid idiom MapEditorSceneTests
                // uses), driven through SceneManager.Update -> MapEditorLandingScene.OnUpdate, not ActivateRecent directly.
                m.Input = MouseFrame(at, leftDown: false); m.Update(0.016f);
                m.Input = MouseFrame(at, leftDown: true); m.Update(0.016f);
                m.Input = MouseFrame(at, leftDown: false); m.Update(0.016f);

                Assert.Equal(existing, opened);
                Assert.Equal(2, m.Count);
            }
            finally { if (File.Exists(existing)) File.Delete(existing); }
        }

        [Fact]
        public void Landing_DiscoverMaps_NormalizesAndSortsPaths()
        {
            // Two same-named files in different directories: proves the sort's tiebreak (full path, Ordinal) fires
            // when the file-name comparison (OrdinalIgnoreCase) is tied, not just that both entries survive dedup.
            string root = Path.Combine(Path.GetTempPath(), "ke-landing-sort-" + Path.GetRandomFileName());
            string dirA = Path.Combine(root, "aaa");
            string dirB = Path.Combine(root, "bbb");
            Directory.CreateDirectory(dirA);
            Directory.CreateDirectory(dirB);
            string commonA = Path.Combine(dirA, "common.map.json");
            string commonB = Path.Combine(dirB, "common.map.json");
            string alpha = Path.Combine(root, "alpha.map.json");
            string beta = Path.Combine(root, "beta.map.json");
            string zeta = Path.Combine(root, "Zeta.map.json");   // capital: proves the file-name compare ignores case
            foreach (string p in new[] { commonA, commonB, alpha, beta, zeta }) File.WriteAllText(p, "{}");

            try
            {
                var opened = new List<string>();
                var scene = Landing(new MapEditorLandingOptions
                {
                    // Out of order, with a whitespace entry and an ordinal-identical duplicate: both must vanish.
                    DiscoverMaps = () => new[] { zeta, beta, "   ", alpha, alpha, commonB, commonA },
                    OpenEditor = path => { opened.Add(path); return new StubEditorScene(); },
                });
                var m = new SceneManager();
                m.Push(scene);

                Assert.Equal(5, scene.DiscoveredButtonCount);   // 7 raw entries - 1 duplicate - 1 whitespace
                Assert.Equal("alpha.map.json", scene.DiscoveredButtonAt(0)!.Resolved);
                Assert.Equal("beta.map.json", scene.DiscoveredButtonAt(1)!.Resolved);
                Assert.Equal("common.map.json", scene.DiscoveredButtonAt(2)!.Resolved);
                Assert.Equal("common.map.json", scene.DiscoveredButtonAt(3)!.Resolved);
                Assert.Equal("Zeta.map.json", scene.DiscoveredButtonAt(4)!.Resolved);

                // Disambiguate the two identically-labeled "common.map.json" buttons by activating them: index 3
                // first (its removal only re-indexes what comes after it, index 4, never index 2), then index 2,
                // so neither activation shifts the other button out from under us mid-check.
                scene.DiscoveredButtonAt(3)!.OnClick!.Invoke();
                scene.DiscoveredButtonAt(2)!.OnClick!.Invoke();
                Assert.Equal(new[] { commonB, commonA }, opened);   // dirA sorted before dirB, per the tiebreak
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        [Fact]
        public void Landing_DiscoveredMap_AlreadyInRecents_IsNotListedTwice()
        {
            // A discovered path already in the recents store must render exactly once, in Open Recent, never twice.
            EditorRecentFiles store = NewStore();
            store.Touch("/maps/known.map.json");

            var scene = Landing(new MapEditorLandingOptions
            {
                Recent = store,
                DiscoverMaps = () => new[] { "/maps/known.map.json", "/maps/other.map.json" },
            });
            var m = new SceneManager();
            m.Push(scene);

            Assert.Equal(1, scene.RecentButtonCount);
            Assert.Equal("known.map.json", scene.RecentButtonAt(0)!.Resolved);

            Assert.Equal(1, scene.DiscoveredButtonCount);   // "known" filtered out: only "other" remains
            Assert.Equal("other.map.json", scene.DiscoveredButtonAt(0)!.Resolved);
        }

        [Fact]
        public void Landing_ActivateDiscovered_TouchesStore_PushesEditor_MigratesToRecent()
        {
            // A real temp file: activating a discovered map touches the store, pushes the editor, and migrates the
            // entry out of Open Map into Open Recent.
            string existing = Path.Combine(Path.GetTempPath(), "ke-landing-discovered-" + Path.GetRandomFileName() + ".map.json");
            File.WriteAllText(existing, "{}");
            try
            {
                EditorRecentFiles store = NewStore();
                string? opened = null;
                var scene = Landing(new MapEditorLandingOptions
                {
                    Recent = store,
                    DiscoverMaps = () => new[] { existing },
                    OpenEditor = path => { opened = path; return new StubEditorScene(); },
                });
                var m = new SceneManager();
                m.Push(scene);
                Assert.Equal(1, scene.DiscoveredButtonCount);

                scene.ActivateDiscovered(existing);

                Assert.Equal(existing, opened);                 // the editor was built for the activated path
                Assert.Equal(2, m.Count);                        // and pushed on top of the landing scene
                Assert.Equal(existing, store.Paths[0]);          // activation touched it to the front
                Assert.Equal(0, scene.DiscoveredButtonCount);    // migrated into Open Recent: dropped from Open Map
            }
            finally { if (File.Exists(existing)) File.Delete(existing); }
        }

        [Fact]
        public void Landing_ActivateDiscovered_MissingFile_RequeriesWithNote()
        {
            // A discovered path that vanished from disk since the last query: ActivateDiscovered has no store entry
            // to prune (the head owns the directory, not this scene), so the re-query itself IS the prune.
            string missing = Path.Combine(Path.GetTempPath(), "ke-landing-discovered-missing-" + Guid.NewGuid().ToString("N") + ".map.json");
            int calls = 0;
            var scene = Landing(new MapEditorLandingOptions
            {
                DiscoverMaps = () =>
                {
                    calls++;
                    return calls == 1 ? new[] { missing } : Array.Empty<string>();
                },
            });
            var m = new SceneManager();
            m.Push(scene);
            Assert.Equal(1, scene.DiscoveredButtonCount);
            Assert.Equal(1, calls);

            scene.ActivateDiscovered(missing);

            Assert.Equal(1, m.Count);                          // nothing pushed
            Assert.False(string.IsNullOrEmpty(scene.Note));    // a note explains why
            Assert.Equal(2, calls);                            // the miss re-queried the head
            Assert.Equal(0, scene.DiscoveredButtonCount);      // and the button is gone
        }

        [Fact]
        public void Landing_NoDiscoverHook_ListsNothing()
        {
            // A head that never wires DiscoverMaps keeps exactly the menu it had before this feature: no entries,
            // and a driven frame must not throw despite the null hook.
            var scene = Landing(new MapEditorLandingOptions());
            var m = new SceneManager();
            m.Push(scene);
            m.UiViewport = new UiViewport(800, 600, 800, 600);

            Assert.Equal(0, scene.DiscoveredButtonCount);

            m.Input = InputState.Empty;
            m.Update(0.016f);

            Assert.Equal(0, scene.DiscoveredButtonCount);
        }

        [Fact]
        public void Landing_OnUpdate_TapDiscoveredButton_ActivatesDiscovered()
        {
            string existing = Path.Combine(Path.GetTempPath(), "ke-landing-discovered-tap-" + Path.GetRandomFileName() + ".map.json");
            File.WriteAllText(existing, "{}");
            try
            {
                string? opened = null;
                var scene = Landing(new MapEditorLandingOptions
                {
                    DiscoverMaps = () => new[] { existing },
                    OpenEditor = path => { opened = path; return new StubEditorScene(); },
                });
                var m = new SceneManager();
                m.Push(scene);
                m.UiViewport = new UiViewport(800, 600, 800, 600);

                m.Input = InputState.Empty;
                m.Update(0.016f);   // lays out this frame's discovered button, so its Bounds are current

                Button button = scene.DiscoveredButtonAt(0) ?? throw new InvalidOperationException("expected a discovered button");
                var at = new Vector2(button.Bounds.X + button.Bounds.Width * 0.5f, button.Bounds.Y + button.Bounds.Height * 0.5f);

                // A real press-then-release tap at the button's center, driven through SceneManager.Update ->
                // MapEditorLandingScene.OnUpdate, not ActivateDiscovered directly (mirrors the Open Recent tap test).
                m.Input = MouseFrame(at, leftDown: false); m.Update(0.016f);
                m.Input = MouseFrame(at, leftDown: true); m.Update(0.016f);
                m.Input = MouseFrame(at, leftDown: false); m.Update(0.016f);

                Assert.Equal(existing, opened);
                Assert.Equal(2, m.Count);
            }
            finally { if (File.Exists(existing)) File.Delete(existing); }
        }

        [Fact]
        public void Landing_ReExpose_RequeriesDiscoveredMaps()
        {
            // The load-bearing case for issue 359's re-query-on-scene-enter requirement: a map created (or deleted)
            // outside the editor while this menu was not driving must appear (or disappear) the next time the menu
            // becomes the top of the stack again, without restarting the head.
            string mapA = Path.Combine(Path.GetTempPath(), "ke-landing-reexpose-a-" + Path.GetRandomFileName() + ".map.json");
            string mapB = Path.Combine(Path.GetTempPath(), "ke-landing-reexpose-b-" + Path.GetRandomFileName() + ".map.json");
            File.WriteAllText(mapA, "{}");
            File.WriteAllText(mapB, "{}");
            try
            {
                EditorRecentFiles store = NewStore();
                int calls = 0;
                bool includeB = false;
                var scene = Landing(new MapEditorLandingOptions
                {
                    Recent = store,
                    DiscoverMaps = () =>
                    {
                        calls++;
                        return includeB ? new[] { mapA, mapB } : new[] { mapA };
                    },
                    OpenEditor = _ => new StubEditorScene(),
                });
                var m = new SceneManager();
                m.Push(scene);   // OnEnter queries once
                m.UiViewport = new UiViewport(800, 600, 800, 600);

                Assert.Equal(1, calls);
                Assert.Equal(1, scene.DiscoveredButtonCount);   // mapA

                // Several driven frames while this scene stays the top of the stack: the hook is head file IO (a
                // directory enumeration), so it must not ride the per-frame path the way the recents compare does.
                m.Input = InputState.Empty;
                m.Update(0.016f);
                m.Update(0.016f);
                m.Update(0.016f);
                Assert.Equal(1, calls);   // no climb: still just the one OnEnter query

                // Activate mapA (migrates it into Open Recent, pushes the stub editor), then let mapB "appear" as
                // if the game wrote it while the editor sat on top of this menu.
                includeB = true;
                scene.ActivateDiscovered(mapA);
                Assert.Equal(2, m.Count);   // the stub editor is pushed on top
                Assert.Equal(1, calls);     // activating does not itself re-query (only RebuildRecentButtons ran)

                m.Pop();                // back to the landing scene (outside Update, so this applies immediately)
                m.Update(0.016f);       // this scene is top again: RefreshDiscoveredOnReExpose must notice and requery

                Assert.Equal(2, calls);                          // re-queried exactly once on re-expose
                Assert.Equal(1, scene.DiscoveredButtonCount);     // mapB: mapA is now filtered, already in Open Recent
                Assert.Equal(Path.GetFileName(mapB), scene.DiscoveredButtonAt(0)!.Resolved);
            }
            finally
            {
                if (File.Exists(mapA)) File.Delete(mapA);
                if (File.Exists(mapB)) File.Delete(mapB);
            }
        }

        [Fact]
        public void Landing_LongMapLists_KeepChromeVisible_AndLastMapCanBeTappedAfterScroll()
        {
            string root = Path.Combine(Path.GetTempPath(), "ke-landing-scroll-" + Path.GetRandomFileName());
            Directory.CreateDirectory(root);
            try
            {
                EditorRecentFiles store = NewStore();
                var discovered = new string[15];
                for (int i = 0; i < 10; i++)
                {
                    string path = Path.Combine(root, "recent-" + i.ToString("D2") + ".map.json");
                    File.WriteAllText(path, "{}");
                    store.Touch(path);
                }
                for (int i = 0; i < discovered.Length; i++)
                {
                    discovered[i] = Path.Combine(root, "world-" + i.ToString("D2") + ".map.json");
                    File.WriteAllText(discovered[i], "{}");
                }

                string? opened = null;
                var scene = Landing(new MapEditorLandingOptions
                {
                    Recent = store,
                    DiscoverMaps = () => discovered,
                    OpenEditor = path => { opened = path; return new StubEditorScene(); },
                });
                var m = new SceneManager { UiViewport = new UiViewport(1280, 720, 1280, 720) };
                m.Push(scene);
                m.Input = MouseFrame(Vector2.Zero, false, width: 1280, height: 720);
                m.Update(0.016f);

                Assert.Equal(discovered.Length, scene.DiscoveredButtonCount);
                Assert.True(scene.NameInput.Bounds.Y >= 0f);
                Assert.True(scene.CreateButton.Bounds.Bottom <= 720f);
                Assert.True(scene.QuitButton.Bounds.Bottom <= 720f);

                Rect list = scene.MapListBounds;
                var overList = new Vector2(list.X + list.Width * 0.5f, list.Y + list.Height * 0.5f);

                // A small drag keeps both the original press and the release inside this row after it moves.
                // The gesture is still a scroll, so its release must not activate the map under it.
                Button dragRow = scene.RecentButtonAt(2) ?? throw new InvalidOperationException("expected a drag row");
                var dragStart = new Vector2(dragRow.Bounds.X + dragRow.Bounds.Width * 0.5f,
                    dragRow.Bounds.Y + dragRow.Bounds.Height * 0.5f);
                var dragEnd = dragStart - new Vector2(0f, 10f);
                m.Input = MouseFrame(dragStart, false, width: 1280, height: 720); m.Update(0.016f);
                m.Input = MouseFrame(dragStart, true, width: 1280, height: 720); m.Update(0.016f);
                m.Input = MouseFrame(dragEnd, true, width: 1280, height: 720); m.Update(0.016f);
                m.Input = MouseFrame(dragEnd, false, width: 1280, height: 720); m.Update(0.016f);
                Assert.Null(opened);
                Assert.Equal(1, m.Count);

                m.Input = MouseFrame(overList, false, scrollDelta: -20f, width: 1280, height: 720);
                m.Update(0.016f);

                Button firstRecent = scene.RecentButtonAt(0) ?? throw new InvalidOperationException("expected a recent button");
                Assert.True(firstRecent.Bounds.Bottom <= list.Y);
                var hidden = new Vector2(firstRecent.Bounds.X + firstRecent.Bounds.Width * 0.5f,
                    firstRecent.Bounds.Y + firstRecent.Bounds.Height * 0.5f);
                m.Input = MouseFrame(hidden, false, width: 1280, height: 720); m.Update(0.016f);
                m.Input = MouseFrame(hidden, true, width: 1280, height: 720); m.Update(0.016f);
                m.Input = MouseFrame(hidden, false, width: 1280, height: 720); m.Update(0.016f);
                Assert.Null(opened);

                Button last = scene.DiscoveredButtonAt(discovered.Length - 1)
                    ?? throw new InvalidOperationException("expected the final discovered button");
                Assert.True(last.Bounds.Y >= list.Y);
                Assert.True(last.Bounds.Bottom <= list.Bottom);
                var at = new Vector2(last.Bounds.X + last.Bounds.Width * 0.5f, last.Bounds.Y + last.Bounds.Height * 0.5f);
                m.Input = MouseFrame(at, false, width: 1280, height: 720); m.Update(0.016f);
                m.Input = MouseFrame(at, true, width: 1280, height: 720); m.Update(0.016f);
                m.Input = MouseFrame(at, false, width: 1280, height: 720); m.Update(0.016f);

                Assert.Equal(discovered[^1], opened);
                Assert.Equal(2, m.Count);
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        // A minimal mouse frame for driving the scene's real OnUpdate headless (mirrors the MapEditorSceneTests
        // MouseFrame idiom: a press/release edge is read from the transition between consecutive frames).
        // One per test-class instance (xUnit builds a fresh instance per fact), so the mouse press and
        // release edges derive from this test's own frame sequence and nothing crosses between tests.
        readonly MouseFrames _mouse = new();

        InputState MouseFrame(Vector2 pos, bool leftDown, float scrollDelta = 0f, int width = 800, int height = 600)
        {
            var down = new HashSet<MouseButton>();
            if (leftDown) down.Add(MouseButton.Left);
            var (edgePressed, edgeReleased) = _mouse.Advance(down);
            return new InputState(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                down, edgePressed, pos, Vector2.Zero, scrollDelta, width, height, mouseReleased: edgeReleased);
        }

        // A keyboard frame: the given keys fire their press edge this frame (and read as held).
        static InputState KeyFrame(params Key[] pressed)
        {
            var down = new HashSet<Key>(pressed);
            return new InputState(down, new HashSet<Key>(pressed), new HashSet<Key>(),
                new HashSet<MouseButton>(), new HashSet<MouseButton>(),
                Vector2.Zero, Vector2.Zero, 0, 800, 600);
        }
    }
}
