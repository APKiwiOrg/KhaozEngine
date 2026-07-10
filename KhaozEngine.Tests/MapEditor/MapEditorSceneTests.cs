using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Gui;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.MapEditor
{
    /// <summary>Headless lifecycle + wiring tests for <see cref="MapEditorScene"/>, using the FakeScene idiom from
    /// <c>SceneManagerTests</c>: the GPU-touching work lives behind the <see cref="MapEditorScene.BuildWorld"/> /
    /// <see cref="MapEditorScene.TeardownWorld"/> and per-step seams, which these subclasses override to record
    /// calls instead of touching a device. Covers build-once / teardown-once guards, the OnUpdate step order
    /// (camera then tools then rebuild), the save-failure path landing in the status strip without throwing, and a
    /// null-batch UI draw being a safe no-op before any font is loaded.</summary>
    public class MapEditorSceneTests
    {
        // Records BuildWorld / TeardownWorld, and skips every device call so OnEnter/OnExit run headless.
        sealed class SpyScene : MapEditorScene
        {
            public int Builds, Teardowns;
            protected override void BuildWorld() => Builds++;
            protected override void TeardownWorld() => Teardowns++;
        }

        // Records the per-frame step order; the chrome / streaming steps are neutralized so nothing touches a device.
        sealed class OrderScene : MapEditorScene
        {
            public readonly List<string> Log = new();
            protected override void BuildWorld() { }
            protected override void TeardownWorld() { }
            protected override void UpdateCamera(float dt) => Log.Add("camera");
            protected override void UpdateTools(float dt) => Log.Add("tools");
            protected override void CheckWorldRebuild() => Log.Add("rebuild");
            protected override void UpdateChrome(float dt) { }
            protected override void UpdateStreaming(float dt) { }
        }

        // Injects an invalid document (default bounds fail MaxX > MinX), so a save validates-and-throws internally.
        sealed class InvalidDocScene : MapEditorScene
        {
            protected override void BuildWorld() { }
            protected override void TeardownWorld() { }
            protected override MapDocument CreateDocument(MapDocRegistry registry) => new MapDocument { Id = "bad" };
        }

        // Injects a caller-supplied document, so the inspector tests can select features/exclusions/regions headless.
        sealed class DocScene : MapEditorScene
        {
            readonly Func<MapDocument> _factory;
            public DocScene(Func<MapDocument> factory) => _factory = factory;
            protected override void BuildWorld() { }
            protected override void TeardownWorld() { }
            protected override MapDocument CreateDocument(MapDocRegistry registry) => _factory();
        }

        // Injects a fixed kit-id -> category map (a manifest-free stand-in for ViewportWorld.KindCategories), so the
        // palette-tree tests exercise the grouping / filtering / selection surface without a device or manifest.
        sealed class PaletteScene : MapEditorScene
        {
            readonly IReadOnlyDictionary<string, string> _categories;
            public PaletteScene(IReadOnlyDictionary<string, string> categories) => _categories = categories;
            protected override void BuildWorld() { }
            protected override void TeardownWorld() { }
            protected override IReadOnlyDictionary<string, string> PaletteKindCategories() => _categories;
        }

        static PaletteScene PushPaletteScene(IReadOnlyDictionary<string, string> categories, params string[] spawns)
        {
            var scene = new PaletteScene(categories);
            var options = new MapEditorOptions();
            options.SpawnArchetypes.AddRange(spawns);
            scene.Init(null!, null!, null!, options);
            new SceneManager().Push(scene);
            return scene;
        }

        static Dictionary<string, string> KitCategories() => new(StringComparer.Ordinal)
        {
            ["oak"] = "trees", ["pine"] = "trees", ["boulder"] = "rocks",
        };

        // A press-origin tap on a TreeView (press and release both at `at`), the way the pointer fires taps.
        static void TapTree(TreeView tree, InputManager input, Vector2 at)
        {
            input.Update(MouseFrame(at, leftDown: false)); tree.Update(input);
            input.Update(MouseFrame(at, leftDown: true)); tree.Update(input);
            input.Update(MouseFrame(at, leftDown: false)); tree.Update(input);
        }

        static string TempPath() => Path.Combine(Path.GetTempPath(), $"ke-editor-{Guid.NewGuid():N}.map.json");

        static MapDocument ValidDoc()
        {
            return new MapDocument
            {
                Id = "inspector-zone",
                Bounds = new MapBounds { MinX = -50f, MinZ = -50f, MaxX = 50f, MaxZ = 50f },
            };
        }

        static DocScene PushDocScene(Func<MapDocument> factory)
        {
            var scene = new DocScene(factory);
            scene.Init(null!, null!, null!, new MapEditorOptions());
            new SceneManager().Push(scene);
            return scene;
        }

        // A minimal mouse frame for driving InputManager headless (the SceneManagerTests Frame idiom).
        static InputState MouseFrame(Vector2 pos, bool leftDown)
        {
            var down = new HashSet<MouseButton>();
            if (leftDown) down.Add(MouseButton.Left);
            return new InputState(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                down, new HashSet<MouseButton>(), pos, Vector2.Zero, 0, 960, 540);
        }

        // A keyboard frame: the given keys fire their press edge this frame (and read as held), with an
        // optional shift modifier held. Each frame is an independent snapshot, so two consecutive chord
        // presses just construct this twice.
        static InputState KeyFrame(bool shiftDown, params Key[] pressed)
        {
            var down = new HashSet<Key>(pressed);
            if (shiftDown) down.Add(Key.LeftShift);
            return new InputState(down, new HashSet<Key>(pressed), new HashSet<Key>(),
                new HashSet<MouseButton>(), new HashSet<MouseButton>(),
                Vector2.Zero, Vector2.Zero, 0, 960, 540);
        }

        static MapSpawn NewSpawn(string id) => new MapSpawn { Id = id, ArchetypeId = "wolf", X = 1f, Z = 1f };

        static FloatRow FloatRowByLabel(PropertyGrid grid, string label)
        {
            foreach (PropertyRow row in grid.Rows)
                if (row is FloatRow f && f.Label.Resolve() == label) return f;
            Assert.Fail($"no FloatRow labeled '{label}' (rows: {grid.Rows.Count})");
            return null!;   // unreachable
        }

        static ReadOnlyRow ReadOnlyRowByLabel(PropertyGrid grid, string label)
        {
            foreach (PropertyRow row in grid.Rows)
                if (row is ReadOnlyRow r && r.Label.Resolve() == label) return r;
            Assert.Fail($"no ReadOnlyRow labeled '{label}' (rows: {grid.Rows.Count})");
            return null!;   // unreachable
        }

        static void Near(float expected, float actual, float eps = 1e-3f) =>
            Assert.True(MathF.Abs(expected - actual) < eps, $"expected ~{expected} but got {actual}");

        [Fact]
        public void Enter_BuildsOnce_Exit_TearsDownOnce()
        {
            var scene = new SpyScene();
            scene.Init(null!, null!, null!, new MapEditorOptions());
            var m = new SceneManager();

            m.Push(scene);                       // OnEnter builds
            Assert.Equal(1, scene.Builds);
            scene.OnEnter();                     // guarded: no second build
            Assert.Equal(1, scene.Builds);

            m.Pop();                             // OnExit tears down
            Assert.Equal(1, scene.Teardowns);
            scene.OnExit();                      // guarded: no second teardown
            Assert.Equal(1, scene.Teardowns);
        }

        [Fact]
        public void Enter_WiresDocumentAndController()
        {
            var scene = new SpyScene();
            scene.Init(null!, null!, null!, new MapEditorOptions());
            var m = new SceneManager();
            m.Push(scene);

            Assert.NotNull(scene.Document);
            Assert.NotNull(scene.Controller);
            Assert.Equal(EditorToolMode.Select, scene.Controller.Mode);
        }

        [Fact]
        public void Update_StepOrder_CameraThenToolsThenRebuild()
        {
            var scene = new OrderScene();
            scene.Init(null!, null!, null!, new MapEditorOptions());
            var m = new SceneManager { Input = InputState.Empty };
            m.Push(scene);
            scene.Log.Clear();

            m.Update(0.016f);

            Assert.Equal(new[] { "camera", "tools", "rebuild" }, scene.Log);
        }

        [Fact]
        public void Save_Failure_SetsStatus_DoesNotThrow()
        {
            string path = TempPath();
            var scene = new InvalidDocScene();
            scene.Init(null!, null!, null!, new MapEditorOptions { DocumentPath = path });
            var m = new SceneManager();
            m.Push(scene);

            scene.SaveDocument();   // validation throws MapDocumentException internally; must be caught

            Assert.Contains("failed", scene.StatusText, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(path));   // an invalid document is never written
        }

        [Fact]
        public void Save_Valid_WritesFile_AndReportsSaved()
        {
            string path = TempPath();
            var scene = new SpyScene();
            scene.Init(null!, null!, null!, new MapEditorOptions { DocumentPath = path });
            var m = new SceneManager();
            try
            {
                m.Push(scene);
                scene.SaveDocument();

                Assert.True(File.Exists(path));
                Assert.Contains("Saved", scene.StatusText);
                Assert.False(scene.Document.IsDirty);   // MarkSaved cleared the dirty flag
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Fact]
        public void Save_NoPath_SetsStatus_WritesNothing()
        {
            var scene = new SpyScene();
            scene.Init(null!, null!, null!, new MapEditorOptions());
            var m = new SceneManager();
            m.Push(scene);

            scene.SaveDocument();

            Assert.Contains("path", scene.StatusText, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void DrawUi_NullBatch_BeforeFont_IsSafeNoOp()
        {
            var scene = new SpyScene();
            scene.Init(null!, null!, null!, new MapEditorOptions());
            var m = new SceneManager();
            m.Push(scene);

            // No font was injected (headless), so both draw passes early-out without touching the null batch.
            scene.OnDraw2D(null!);
            scene.OnDrawUi(null!);
        }

        [Fact]
        public void Update_BeforeEnter_IsSafeNoOp()
        {
            var scene = new SpyScene();
            scene.Init(null!, null!, null!, new MapEditorOptions());
            scene.OnUpdate(0.016f);   // never entered: built flag false, no throw
        }

        // ---- Shift+Escape exit chord -----------------------------------------------------------------------
        // The scene is pushed onto a REAL SceneManager (the file's standard idiom), so a pop is observable
        // directly: Manager.Pop() queued during Update is applied at the end of the pass, and m.Count drops to
        // zero. No WantsExit flag is needed.

        [Fact]
        public void ShiftEscape_CleanDocument_Pops()
        {
            var scene = new SpyScene();
            scene.Init(null!, null!, null!, new MapEditorOptions());
            var m = new SceneManager();
            m.Push(scene);

            m.Input = KeyFrame(shiftDown: true, Key.Escape);
            m.Update(0.016f);

            Assert.Equal(0, m.Count);   // clean document: popped immediately, no warning step
        }

        [Fact]
        public void ShiftEscape_DirtyDocument_ArmsThenPops()
        {
            var scene = new SpyScene();
            scene.Init(null!, null!, null!, new MapEditorOptions());
            var m = new SceneManager();
            m.Push(scene);
            scene.Document.Execute(new AddSpawnCommand(NewSpawn("s1")));   // dirty
            Assert.True(scene.Document.IsDirty);

            m.Input = KeyFrame(shiftDown: true, Key.Escape);
            m.Update(0.016f);

            Assert.Equal(1, m.Count);   // first press only arms
            Assert.True(scene.ExitArmed);
            Assert.Contains("unsaved", scene.StatusText, StringComparison.OrdinalIgnoreCase);

            m.Update(0.016f);           // second consecutive Shift+Escape press

            Assert.Equal(0, m.Count);   // armed: discards and pops
        }

        [Fact]
        public void ShiftEscape_ArmedThenSave_Disarms()
        {
            string path = TempPath();
            var scene = new SpyScene();
            scene.Init(null!, null!, null!, new MapEditorOptions { DocumentPath = path });
            var m = new SceneManager();
            try
            {
                m.Push(scene);
                scene.Document.Execute(new AddSpawnCommand(NewSpawn("s1")));   // dirty

                m.Input = KeyFrame(shiftDown: true, Key.Escape);
                m.Update(0.016f);
                Assert.True(scene.ExitArmed);

                scene.SaveDocument();                    // Ctrl+S path: disarms (and cleans)
                Assert.False(scene.ExitArmed);

                scene.Document.Execute(new AddSpawnCommand(NewSpawn("s2")));   // dirty again
                m.Update(0.016f);                        // Shift+Escape after the disarm

                Assert.Equal(1, m.Count);                // re-arms instead of popping
                Assert.True(scene.ExitArmed);

                scene.Document.Execute(new AddSpawnCommand(NewSpawn("s3")));   // any mutation disarms too
                Assert.False(scene.ExitArmed);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        // ---- inspector bindings --------------------------------------------------------------------------

        [Fact]
        public void SelectFeature_InspectorBindsParams_ThroughEditFeatureCommand()
        {
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Terrain.Features.Add(new LakeFeatureDoc { CenterX = 1f, CenterZ = 2f, Radius = 6f, Depth = 3f });
                return doc;
            });

            scene.Document.Selection.Set(SelectionKind.Feature, "0");
            FloatRow radius = FloatRowByLabel(scene.Inspector, "Radius");

            // Scrub the row's NumberField headless: press inside the editor cell, then drag +100 px. The scrub
            // path calls Field.SetValue and the row writes the change through its setter (EditFeatureCommand).
            var cell = new Rect(0f, 0f, 200f, 28f);
            var ui = new InputManager();
            ui.Update(MouseFrame(new Vector2(100f, 10f), leftDown: false));
            radius.Update(cell, ui, 0.016f);
            ui.Update(MouseFrame(new Vector2(100f, 10f), leftDown: true));    // press inside (grab-gate origin)
            radius.Update(cell, ui, 0.016f);
            ui.Update(MouseFrame(new Vector2(200f, 10f), leftDown: true));    // +100 px at DragScale 0.01 = +1.0
            bool changed = radius.Update(cell, ui, 0.016f);

            Assert.True(changed);
            var lake = Assert.IsType<LakeFeatureDoc>(scene.Document.Doc.Terrain.Features[0]);
            Near(7f, lake.Radius);                                  // 6 + 1 scrub
            Assert.True(scene.Document.WorldRebuildPending);        // features affect the streamed world
            Assert.True(scene.Document.History.CanUndo);

            Assert.True(scene.Document.Undo());
            lake = Assert.IsType<LakeFeatureDoc>(scene.Document.Doc.Terrain.Features[0]);
            Near(6f, lake.Radius);                                  // undo restores the pre-scrub DTO
        }

        [Fact]
        public void TerrainNode_InspectorEditsWaterLevel_TriggersRebuild()
        {
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Terrain.WaterLevel = -1f;
                return doc;
            });

            scene.Document.Selection.Set(SelectionKind.Terrain, "");
            FloatRow water = FloatRowByLabel(scene.Inspector, "WaterLevel");

            // Scrub the water-level row headless, same NumberField idiom as the feature test: press inside the
            // editor cell, then drag +100 px (DragScale 0.01 -> +1.0), so the row writes through EditTerrainCommand.
            var cell = new Rect(0f, 0f, 200f, 28f);
            var ui = new InputManager();
            ui.Update(MouseFrame(new Vector2(100f, 10f), leftDown: false));
            water.Update(cell, ui, 0.016f);
            ui.Update(MouseFrame(new Vector2(100f, 10f), leftDown: true));   // press inside (grab-gate origin)
            water.Update(cell, ui, 0.016f);
            ui.Update(MouseFrame(new Vector2(200f, 10f), leftDown: true));   // +100 px at DragScale 0.01 = +1.0
            bool changed = water.Update(cell, ui, 0.016f);

            Assert.True(changed);
            Near(0f, scene.Document.Doc.Terrain.WaterLevel);   // -1 + 1 scrub
            Assert.True(scene.Document.WorldRebuildPending);   // water level feeds scatter, so the world rebuilds
            Assert.True(scene.Document.History.CanUndo);

            Assert.True(scene.Document.Undo());
            Near(-1f, scene.Document.Doc.Terrain.WaterLevel);  // undo restores the pre-scrub level
        }

        [Fact]
        public void TerrainNode_InspectorShowsSeedAndBiomeReadouts()
        {
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Terrain.Seed = 99;
                doc.Terrain.Biomes.Add(new MapBiomeBand { Biome = KhaozEngine.Terrain.BiomeId.Meadow });
                doc.Terrain.Biomes.Add(new MapBiomeBand { Biome = KhaozEngine.Terrain.BiomeId.Marsh });
                return doc;
            });

            scene.Document.Selection.Set(SelectionKind.Terrain, "");

            // The editable water level is a FloatRow; seed and biome count are read-only displays.
            Assert.IsType<FloatRow>(FloatRowByLabel(scene.Inspector, "WaterLevel"));
            ReadOnlyRow seed = ReadOnlyRowByLabel(scene.Inspector, "Seed");
            ReadOnlyRow biomes = ReadOnlyRowByLabel(scene.Inspector, "Biomes");

            // ReadOnlyRow polls its display getter on Update, so drive each row once before reading Display.
            var cell = new Rect(0f, 0f, 200f, 28f);
            var ui = new InputManager();
            seed.Update(cell, ui, 0f);
            biomes.Update(cell, ui, 0f);
            Assert.Equal("99", seed.Display);
            Assert.Equal("2", biomes.Display);
        }

        [Fact]
        public void SelectExclusion_InspectorShowsShapeRows()
        {
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Exclusions.Add(new MapExclusion { Shape = new DiscShapeDoc { CenterX = 1f, CenterZ = 2f, Radius = 5f } });
                return doc;
            });

            scene.Document.Selection.Set(SelectionKind.Exclusion, "0");

            Assert.NotEmpty(scene.Inspector.Rows);                  // shape read-only rows, not a blank panel
            Assert.All(scene.Inspector.Rows, row => Assert.IsType<ReadOnlyRow>(row));
        }

        [Fact]
        public void SelectRegion_InspectorHasRenameRow()
        {
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Regions.Add(new MapRegion { Name = "region-1", Shape = new DiscShapeDoc { Radius = 4f } });
                return doc;
            });

            scene.Document.Selection.Set(SelectionKind.Region, "region-1");

            Assert.NotEmpty(scene.Inspector.Rows);
            Assert.IsType<TextRow>(scene.Inspector.Rows[0]);        // the rename row, bound through RenameRegionCommand
        }

        [Fact]
        public void RegionRename_ThenSelectingAnotherElement_KeepsTheNewSelection()
        {
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Regions.Add(new MapRegion { Name = "region-1", Shape = new DiscShapeDoc { Radius = 4f } });
                doc.Placements.Add(new MapPlacement { Id = "placement-1", Kind = "prop" });
                return doc;
            });

            scene.Document.Selection.Set(SelectionKind.Region, "region-1");
            var row = Assert.IsType<TextRow>(scene.Inspector.Rows[0]);

            // Drive the rename row headless, matching the NumberField-scrub idiom above but for text: focus the
            // field, replace its buffer, then run one row Update so the TextChanged write-through fires the
            // setter (RenameRegionCommand) and queues the deferred re-select.
            var ui = new InputManager();
            ui.Update(InputState.Empty);
            row.Input.IsFocused = true;
            row.Input.SetText("region-1-renamed");
            row.Update(new Rect(0f, 0f, 200f, 28f), ui, 0.016f);

            Assert.Equal("region-1-renamed", scene.Document.Doc.Regions[0].Name);
            Assert.True(row.Input.IsFocused);   // still typing: the pending re-select sync has NOT fired yet

            // The user picks a different element (outline click / viewport pick) before ever blurring the
            // rename row.
            scene.Document.Selection.Set(SelectionKind.Placement, "placement-1");

            // One Update frame: the pending re-select sync lives in UpdateChrome. Without the fix, the rebuilt
            // inspector's now-null name row lets the stale pending re-select fire and stomp this pick.
            scene.OnUpdate(0.016f);

            Assert.Equal(SelectionKind.Placement, scene.Document.Selection.Kind);
            Assert.Equal("placement-1", scene.Document.Selection.Id);
        }

        [Fact]
        public void PlacementRename_SelectionFollows_AndYieldsToNewSelections()
        {
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Placements.Add(new MapPlacement { Id = "placement-1", Kind = "prop" });
                doc.Placements.Add(new MapPlacement { Id = "placement-2", Kind = "prop" });
                return doc;
            });

            scene.Document.Selection.Set(SelectionKind.Placement, "placement-1");
            var row = Assert.IsType<TextRow>(scene.Inspector.Rows[0]);   // the Name rename row leads the inspector

            // ---- half 1: the selection follows the rename once the row blurs ----
            // Drive the rename row headless (the region-rename idiom): focus the field, replace its buffer, then run
            // one row Update so the TextChanged write-through fires the setter (RenamePlacementCommand) and queues
            // the deferred re-select.
            var ui = new InputManager();
            ui.Update(InputState.Empty);
            row.Input.IsFocused = true;
            row.Input.SetText("placement-1-renamed");
            row.Update(new Rect(0f, 0f, 200f, 28f), ui, 0.016f);

            Assert.Equal("placement-1-renamed", scene.Document.Doc.Placements[0].Id);
            Assert.True(row.Input.IsFocused);   // still typing: the deferred re-select has NOT fired yet

            row.Input.IsFocused = false;        // the user tabs / clicks away from the rename row
            scene.OnUpdate(0.016f);             // the deferred sync lives in UpdateChrome
            Assert.Equal(SelectionKind.Placement, scene.Document.Selection.Kind);
            Assert.Equal("placement-1-renamed", scene.Document.Selection.Id);   // selection followed the new id

            // ---- half 2: a fresh pick made mid-rename yields (is not stomped by the pending sync) ----
            var row2 = Assert.IsType<TextRow>(scene.Inspector.Rows[0]);
            row2.Input.IsFocused = true;
            row2.Input.SetText("placement-1-again");
            row2.Update(new Rect(0f, 0f, 200f, 28f), ui, 0.016f);
            Assert.Equal("placement-1-again", scene.Document.Doc.Placements[0].Id);

            // The user picks a different placement (outline click / viewport pick) before ever blurring the row.
            scene.Document.Selection.Set(SelectionKind.Placement, "placement-2");

            // One Update frame: without the yield fix, the stale pending re-select fires here and stomps this pick.
            scene.OnUpdate(0.016f);
            Assert.Equal(SelectionKind.Placement, scene.Document.Selection.Kind);
            Assert.Equal("placement-2", scene.Document.Selection.Id);
        }

        [Fact]
        public void SpawnRename_SelectionFollows()
        {
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Spawns.Add(new MapSpawn { Id = "spawn-1", ArchetypeId = "wolf" });
                return doc;
            });

            scene.Document.Selection.Set(SelectionKind.Spawn, "spawn-1");
            var row = Assert.IsType<TextRow>(scene.Inspector.Rows[0]);   // the Name rename row leads the inspector

            var ui = new InputManager();
            ui.Update(InputState.Empty);
            row.Input.IsFocused = true;
            row.Input.SetText("spawn-1-renamed");
            row.Update(new Rect(0f, 0f, 200f, 28f), ui, 0.016f);

            Assert.Equal("spawn-1-renamed", scene.Document.Doc.Spawns[0].Id);
            Assert.True(row.Input.IsFocused);   // still typing: the deferred re-select has NOT fired yet

            row.Input.IsFocused = false;        // the user tabs / clicks away from the rename row
            scene.OnUpdate(0.016f);
            Assert.Equal(SelectionKind.Spawn, scene.Document.Selection.Kind);
            Assert.Equal("spawn-1-renamed", scene.Document.Selection.Id);   // selection followed the new id
        }

        // ---- categorized palette + filters ---------------------------------------------------------------

        [Fact]
        public void Palette_GroupsKindsByCategory()
        {
            var scene = PushPaletteScene(KitCategories());

            TreeView tree = scene.PaletteTree;
            // Roots are the distinct categories, ordinal-sorted, expanded by default.
            Assert.Equal(new[] { "rocks", "trees" }, tree.Roots.Select(r => r.Label.Resolve()).ToArray());
            Assert.All(tree.Roots, r => Assert.True(r.Expanded));
            // Leaves land under the right root, sorted ordinal within the category.
            Assert.Equal(new[] { "boulder" }, tree.Roots[0].Children.Select(c => c.Label.Resolve()).ToArray());
            Assert.Equal(new[] { "oak", "pine" }, tree.Roots[1].Children.Select(c => c.Label.Resolve()).ToArray());
        }

        [Fact]
        public void Palette_FilterNarrowsAndHidesEmptyCategories()
        {
            var scene = PushPaletteScene(KitCategories());

            scene.PaletteFilter.SetText("OAK");   // case-insensitive substring
            scene.RefreshPalettes();

            // Only the trees category survives, with its single matching leaf; rocks (no match) is hidden.
            Assert.Equal(new[] { "trees" }, scene.PaletteTree.Roots.Select(r => r.Label.Resolve()).ToArray());
            Assert.Equal(new[] { "oak" }, scene.PaletteTree.Roots[0].Children.Select(c => c.Label.Resolve()).ToArray());

            scene.PaletteFilter.SetText("");      // clearing restores the full tree
            scene.RefreshPalettes();
            Assert.Equal(new[] { "rocks", "trees" }, scene.PaletteTree.Roots.Select(r => r.Label.Resolve()).ToArray());
        }

        [Fact]
        public void Palette_LeafSelection_SetsPlaceKind()
        {
            var scene = PushPaletteScene(KitCategories());
            scene.PaletteTree.Bounds = new Rect(0f, 0f, 200f, 240f);
            scene.PaletteTree.RowHeight = 22f;

            // VisibleRows (both categories expanded): rocks(0), boulder(1), trees(2), oak(3), pine(4). Tap the
            // "oak" leaf at visible index 3, x well past the caret zone so it selects rather than toggles.
            var input = new InputManager();
            TapTree(scene.PaletteTree, input, new Vector2(120f, 3 * 22f + 11f));

            Assert.Equal("oak", scene.Controller.PlaceKind);
        }

        [Fact]
        public void SpawnList_FilterNarrows()
        {
            var scene = PushPaletteScene(new Dictionary<string, string>(StringComparer.Ordinal), "wolf", "bear", "wolfpup");

            Assert.Equal(3, scene.SpawnList.Roots.Count);                 // full flat list
            Assert.All(scene.SpawnList.Roots, r => Assert.Empty(r.Children));   // no categories: every root is a leaf

            scene.SpawnFilter.SetText("WOLF");   // case-insensitive substring
            scene.RefreshPalettes();
            Assert.Equal(new[] { "wolf", "wolfpup" }, scene.SpawnList.Roots.Select(r => r.Label.Resolve()).ToArray());

            scene.SpawnFilter.SetText("");
            scene.RefreshPalettes();
            Assert.Equal(3, scene.SpawnList.Roots.Count);                 // clearing restores the full list
        }

        // ---- status strip --------------------------------------------------------------------------------

        [Fact]
        public void StatusLine_LeadsWithModeAndHint()
        {
            var scene = new SpyScene();
            scene.Init(null!, null!, null!, new MapEditorOptions());
            var m = new SceneManager();
            m.Push(scene);

            scene.Controller.Mode = EditorToolMode.PlacePlacement;
            scene.Controller.PlaceKind = "hut";

            string line = scene.StatusLine();
            string hint = scene.Controller.ModeHint;

            // The active mode name leads the line, its hint follows, and both sit ahead of the undo/redo tail.
            Assert.StartsWith("PlacePlacement", line);
            Assert.Contains(hint, line);
            Assert.True(line.IndexOf(hint, StringComparison.Ordinal) < line.IndexOf("undo:", StringComparison.Ordinal));
        }
    }
}
