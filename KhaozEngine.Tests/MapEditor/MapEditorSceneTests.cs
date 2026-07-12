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

        // Injects a caller-supplied document AND a pure flat field on the controller (no GPU), so a viewport pick
        // can be driven straight on the controller headless. Everything else is the DocScene idiom.
        sealed class FieldDocScene : MapEditorScene
        {
            readonly Func<MapDocument> _factory;
            public FieldDocScene(Func<MapDocument> factory) => _factory = factory;
            protected override MapDocument CreateDocument(MapDocRegistry registry) => _factory();
            protected override void BuildWorld() => Controller.Field =
                new KhaozEngine.Terrain.TerrainField(new KhaozEngine.Terrain.TerrainConfig { GentleAmplitude = 0f });
            protected override void TeardownWorld() { }
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

        // A press-origin tap driven through a PropertyGrid (press and release both at `at`), the PropertyGridTests
        // idiom, so a ChoiceRow's dropdown open/pick runs exactly as it does live (block regions included).
        static void TapGrid(PropertyGrid grid, InputManager input, Vector2 at)
        {
            input.Update(MouseFrame(at, leftDown: false)); grid.Update(input, 0.016f);
            input.Update(MouseFrame(at, leftDown: true)); grid.Update(input, 0.016f);
            input.Update(MouseFrame(at, leftDown: false)); grid.Update(input, 0.016f);
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

        // A keyboard frame with Ctrl held: the given keys fire their press edge this frame (and read as held),
        // with LeftControl down so the scene's ctrl-modified shortcuts (undo/redo, feature reorder) trigger.
        static InputState CtrlKeyFrame(params Key[] pressed)
        {
            var down = new HashSet<Key>(pressed) { Key.LeftControl };
            return new InputState(down, new HashSet<Key>(pressed), new HashSet<Key>(),
                new HashSet<MouseButton>(), new HashSet<MouseButton>(),
                Vector2.Zero, Vector2.Zero, 0, 960, 540);
        }

        // A keyboard frame with Super (Cmd) held instead of Ctrl: same idiom as CtrlKeyFrame, so a test can prove
        // the scene's chords fire identically off IsCommandDown's other modifier key.
        static InputState SuperKeyFrame(params Key[] pressed)
        {
            var down = new HashSet<Key>(pressed) { Key.LeftSuper };
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

        // Taps a FloatRow's NumberField into typing mode (press+release at the same point inside its editor cell,
        // the TapToEdit idiom from NumberFieldTests/PropertyGridTests), so a test can put a specific inspector
        // field into an active-edit state headlessly without wiring up the grid's own scene-driven Update path.
        static void BeginEditing(FloatRow row)
        {
            var cell = new Rect(0f, 0f, 200f, 28f);
            var ui = new InputManager();
            var at = new Vector2(100f, 14f);
            ui.Update(MouseFrame(at, leftDown: false)); row.Update(cell, ui, 0.016f);
            ui.Update(MouseFrame(at, leftDown: true)); row.Update(cell, ui, 0.016f);
            ui.Update(MouseFrame(at, leftDown: false)); row.Update(cell, ui, 0.016f);
        }

        static ReadOnlyRow ReadOnlyRowByLabel(PropertyGrid grid, string label)
        {
            foreach (PropertyRow row in grid.Rows)
                if (row is ReadOnlyRow r && r.Label.Resolve() == label) return r;
            Assert.Fail($"no ReadOnlyRow labeled '{label}' (rows: {grid.Rows.Count})");
            return null!;   // unreachable
        }

        static BoolRow BoolRowByLabel(PropertyGrid grid, string label)
        {
            foreach (PropertyRow row in grid.Rows)
                if (row is BoolRow b && b.Label.Resolve() == label) return b;
            Assert.Fail($"no BoolRow labeled '{label}' (rows: {grid.Rows.Count})");
            return null!;   // unreachable
        }

        static TextRow TextRowByLabel(PropertyGrid grid, string label)
        {
            foreach (PropertyRow row in grid.Rows)
                if (row is TextRow t && t.Label.Resolve() == label) return t;
            Assert.Fail($"no TextRow labeled '{label}' (rows: {grid.Rows.Count})");
            return null!;   // unreachable
        }

        // Drive one press-origin tap through a BoolRow's toggle cell (up, press, release), the way the pointer fires
        // a tap. Returns whether the toggle flipped this frame.
        static bool TapBool(BoolRow row)
        {
            var cell = new Rect(0f, 0f, 200f, 28f);
            var ui = new InputManager();
            ui.Update(MouseFrame(new Vector2(100f, 14f), leftDown: false)); row.Update(cell, ui, 0.016f);
            ui.Update(MouseFrame(new Vector2(100f, 14f), leftDown: true)); row.Update(cell, ui, 0.016f);
            ui.Update(MouseFrame(new Vector2(100f, 14f), leftDown: false));
            return row.Update(cell, ui, 0.016f);
        }

        // Whether the outline lists a placement by id (its node label is "<id> (<kind>)"). The outline is rebuilt
        // from the document, so a still-listed placement means the document still holds it.
        static bool OutlineListsPlacement(TreeView outline, string id)
        {
            foreach (TreeNode root in outline.Roots)
                foreach (TreeNode child in root.Children)
                    if (child.Label.Resolve().StartsWith(id + " ", StringComparison.Ordinal)) return true;
            return false;
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
        public void SelectFeature_InspectorShowsApplyOrderRow()
        {
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Terrain.Features.Add(new LakeFeatureDoc { CenterX = 0f, CenterZ = 0f, Radius = 5f, Depth = 2f });
                doc.Terrain.Features.Add(new FlattenFeatureDoc { CenterX = 1f, CenterZ = 1f, Radius = 4f, TargetHeight = 1f });
                return doc;
            });

            scene.Document.Selection.Set(SelectionKind.Feature, "0");
            ReadOnlyRow order = ReadOnlyRowByLabel(scene.Inspector, "Apply order");

            // ReadOnlyRow polls its display getter on Update, so drive it once before reading Display.
            order.Update(new Rect(0f, 0f, 200f, 28f), new InputManager(), 0f);
            Assert.Equal("1 of 2 (last wins overlap)", order.Display);
        }

        [Fact]
        public void RidgeInspector_ExposesDirectionRows()
        {
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Terrain.Features.Add(new RidgeFeatureDoc { PointX = 0f, PointZ = 0f, Height = 5f, Width = 10f });
                return doc;
            });

            scene.Document.Selection.Set(SelectionKind.Feature, "0");

            // Direction was previously not exposed, leaving the ridge's heading fixed at the DTO default.
            Assert.NotNull(FloatRowByLabel(scene.Inspector, "DirectionX"));
            Assert.NotNull(FloatRowByLabel(scene.Inspector, "DirectionZ"));
        }

        [Fact]
        public void CtrlDown_MovesSelectedFeatureAndSelectionFollows()
        {
            var lake = new LakeFeatureDoc { CenterX = 0f, CenterZ = 0f, Radius = 5f, Depth = 2f };
            var flatten = new FlattenFeatureDoc { CenterX = 1f, CenterZ = 1f, Radius = 4f, TargetHeight = 1f };
            var scene = new DocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Terrain.Features.Add(lake);
                doc.Terrain.Features.Add(flatten);
                return doc;
            });
            scene.Init(null!, null!, null!, new MapEditorOptions());
            var m = new SceneManager();
            m.Push(scene);

            scene.Document.Selection.Set(SelectionKind.Feature, "0");   // select the lake (folds first)

            m.Input = CtrlKeyFrame(Key.Down);
            m.Update(0.016f);

            // The lake moved one step later (index 1: it now folds last and wins overlaps) and the index-string
            // selection followed it, so the same feature stays selected.
            Assert.Same(flatten, scene.Document.Doc.Terrain.Features[0]);
            Assert.Same(lake, scene.Document.Doc.Terrain.Features[1]);
            Assert.Equal(SelectionKind.Feature, scene.Document.Selection.Kind);
            Assert.Equal("1", scene.Document.Selection.Id);
            Assert.True(scene.Document.History.CanUndo);
        }

        [Fact]
        public void HiddenFeature_FollowsCtrlDownReorder()
        {
            var lake = new LakeFeatureDoc { CenterX = 0f, CenterZ = 0f, Radius = 5f, Depth = 2f };
            var flatten = new FlattenFeatureDoc { CenterX = 1f, CenterZ = 1f, Radius = 4f, TargetHeight = 1f };
            var scene = new DocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Terrain.Features.Add(lake);
                doc.Terrain.Features.Add(flatten);
                return doc;
            });
            scene.Init(null!, null!, null!, new MapEditorOptions());
            var m = new SceneManager();
            m.Push(scene);

            scene.Visibility.SetElementHidden(SelectionKind.Feature, "0", true);   // hide the lake, at index 0
            scene.Document.Selection.Set(SelectionKind.Feature, "0");

            m.Input = CtrlKeyFrame(Key.Down);
            m.Update(0.016f);   // Ctrl+Down: the lake moves 0 -> 1 (ReorderSelectedFeature, not the outline drop)

            Assert.Same(lake, scene.Document.Doc.Terrain.Features[1]);
            Assert.True(scene.Visibility.IsElementHidden(SelectionKind.Feature, "1"));    // the hide followed the lake
            Assert.False(scene.Visibility.IsElementHidden(SelectionKind.Feature, "0"));   // the vacated slot 0 is not hidden
        }

        // The visible-row index of a node in a tree (reference identity), or -1.
        static int RowOf(TreeView tree, TreeNode node)
        {
            var rows = tree.VisibleRows();
            for (int i = 0; i < rows.Count; i++)
                if (ReferenceEquals(rows[i].Node, node)) return i;
            return -1;
        }

        // The center point of a node's visible row, so a tap lands on that row.
        static Vector2 RowCenter(TreeView tree, TreeNode node)
        {
            Rect r = tree.RowBounds(RowOf(tree, node));
            return new Vector2(r.X + r.Width * 0.5f, r.Y + r.Height * 0.5f);
        }

        // The child at `childIndex` under the outline category labelled `label`.
        static TreeNode CategoryChild(TreeView tree, string label, int childIndex)
        {
            foreach (TreeNode root in tree.Roots)
                if (root.Label.Resolve() == label) return root.Children[childIndex];
            throw new Xunit.Sdk.XunitException($"no outline category '{label}'");
        }

        // Drive a real drag on a TreeView: press the source row's label, drag onto the target row's upper/lower
        // half, release. Mirrors the pointer's press-move-release sequence so the widget's own gesture runs.
        static void DragTreeRow(TreeView tree, InputManager input, int fromRow, int toRow, bool afterTarget)
        {
            Rect src = tree.RowBounds(fromRow);
            var press = new Vector2(src.X + src.Width * 0.5f, src.Y + src.Height * 0.5f);
            Rect dst = tree.RowBounds(toRow);
            var move = new Vector2(press.X, dst.Y + dst.Height * (afterTarget ? 0.75f : 0.25f));
            input.Update(MouseFrame(press, leftDown: false)); tree.Update(input);
            input.Update(MouseFrame(press, leftDown: true)); tree.Update(input);
            input.Update(MouseFrame(move, leftDown: true)); tree.Update(input);
            input.Update(MouseFrame(move, leftDown: false)); tree.Update(input);
        }

        [Fact]
        public void OutlineDrop_ReordersFeature_AndSelectionFollows()
        {
            var lake = new LakeFeatureDoc { CenterX = 0f, CenterZ = 0f, Radius = 5f, Depth = 2f };
            var flatten = new FlattenFeatureDoc { CenterX = 1f, CenterZ = 1f, Radius = 4f, TargetHeight = 1f };
            var ridge = new RidgeFeatureDoc { PointX = 5f, PointZ = 5f, Height = 2f, Width = 4f };
            var scene = new DocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Terrain.Features.Add(lake);
                doc.Terrain.Features.Add(flatten);
                doc.Terrain.Features.Add(ridge);
                return doc;
            });
            scene.Init(null!, null!, null!, new MapEditorOptions());
            new SceneManager().Push(scene);

            TreeView outline = scene.Outline;
            outline.Bounds = new Rect(0f, 0f, 240f, 400f);   // tall enough for every outline row
            TreeNode f0 = CategoryChild(outline, "Features", 0);
            TreeNode f1 = CategoryChild(outline, "Features", 1);

            var input = new InputManager();
            DragTreeRow(outline, input, RowOf(outline, f0), RowOf(outline, f1), afterTarget: true);   // lake -> after flatten

            Assert.Same(flatten, scene.Document.Doc.Terrain.Features[0]);
            Assert.Same(lake, scene.Document.Doc.Terrain.Features[1]);   // lake now folds after flatten
            Assert.Same(ridge, scene.Document.Doc.Terrain.Features[2]);
            Assert.Equal(SelectionKind.Feature, scene.Document.Selection.Kind);
            Assert.Equal("1", scene.Document.Selection.Id);              // selection follows the moved feature
            Assert.True(scene.Document.History.CanUndo);
        }

        [Fact]
        public void HiddenFeature_FollowsReorder()
        {
            var lake = new LakeFeatureDoc { CenterX = 0f, CenterZ = 0f, Radius = 5f, Depth = 2f };
            var flatten = new FlattenFeatureDoc { CenterX = 1f, CenterZ = 1f, Radius = 4f, TargetHeight = 1f };
            var ridge = new RidgeFeatureDoc { PointX = 5f, PointZ = 5f, Height = 2f, Width = 4f };
            var scene = new DocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Terrain.Features.Add(lake);
                doc.Terrain.Features.Add(flatten);
                doc.Terrain.Features.Add(ridge);
                return doc;
            });
            scene.Init(null!, null!, null!, new MapEditorOptions());
            new SceneManager().Push(scene);

            scene.Visibility.SetElementHidden(SelectionKind.Feature, "2", true);   // hide the ridge, at index 2

            TreeView outline = scene.Outline;
            outline.Bounds = new Rect(0f, 0f, 240f, 400f);
            TreeNode f0 = CategoryChild(outline, "Features", 0);
            TreeNode f2 = CategoryChild(outline, "Features", 2);

            var input = new InputManager();
            DragTreeRow(outline, input, RowOf(outline, f2), RowOf(outline, f0), afterTarget: false);   // ridge -> before lake (2 to 0)

            Assert.Same(ridge, scene.Document.Doc.Terrain.Features[0]);   // the ridge now folds first

            // The hide followed the ridge to its new slot 0. The vacated slot 2 (now the flatten) is NOT hidden.
            Assert.True(scene.Visibility.IsElementHidden(SelectionKind.Feature, "0"));
            Assert.False(scene.Visibility.IsElementHidden(SelectionKind.Feature, "2"));
        }

        [Fact]
        public void OutlineDrop_OnPlacementsCategory_IsNoOp()
        {
            var p0 = new MapPlacement { Id = "p0", Kind = "rock", X = 0f, Z = 0f };
            var p1 = new MapPlacement { Id = "p1", Kind = "tree", X = 5f, Z = 5f };
            var scene = new DocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Placements.Add(p0);
                doc.Placements.Add(p1);
                return doc;
            });
            scene.Init(null!, null!, null!, new MapEditorOptions());
            new SceneManager().Push(scene);

            TreeView outline = scene.Outline;
            outline.Bounds = new Rect(0f, 0f, 240f, 400f);
            TreeNode c0 = CategoryChild(outline, "Placements", 0);
            TreeNode c1 = CategoryChild(outline, "Placements", 1);

            var input = new InputManager();
            DragTreeRow(outline, input, RowOf(outline, c0), RowOf(outline, c1), afterTarget: true);

            // Placements carry no reorder semantics: the drop is dropped, the document and undo stack untouched.
            Assert.Same(p0, scene.Document.Doc.Placements[0]);
            Assert.Same(p1, scene.Document.Doc.Placements[1]);
            Assert.False(scene.Document.History.CanUndo);
            Assert.False(scene.Document.WorldRebuildPending);
        }

        [Fact]
        public void CtrlDown_AtEnd_IsNoOp()
        {
            var lake = new LakeFeatureDoc { CenterX = 0f, CenterZ = 0f, Radius = 5f, Depth = 2f };
            var flatten = new FlattenFeatureDoc { CenterX = 1f, CenterZ = 1f, Radius = 4f, TargetHeight = 1f };
            var scene = new DocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Terrain.Features.Add(lake);
                doc.Terrain.Features.Add(flatten);
                return doc;
            });
            scene.Init(null!, null!, null!, new MapEditorOptions());
            var m = new SceneManager();
            m.Push(scene);

            scene.Document.Selection.Set(SelectionKind.Feature, "1");   // the last feature: cannot move later

            m.Input = CtrlKeyFrame(Key.Down);
            m.Update(0.016f);

            // Clamped at the end: no reorder command lands on the undo stack, and the order is untouched.
            Assert.Same(lake, scene.Document.Doc.Terrain.Features[0]);
            Assert.Same(flatten, scene.Document.Doc.Terrain.Features[1]);
            Assert.False(scene.Document.History.CanUndo);
        }

        [Fact]
        public void RKey_SnapsPlacementToGround()
        {
            var scene = new DocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Placements.Add(new MapPlacement { Id = "hut", Kind = "prop", X = 3f, Z = 4f, Y = 12f });
                return doc;
            });
            scene.Init(null!, null!, null!, new MapEditorOptions());
            var m = new SceneManager();
            m.Push(scene);

            scene.Document.Selection.Set(SelectionKind.Placement, "hut");
            Assert.NotNull(scene.Document.Doc.Placements[0].Y);   // starts airborne

            m.Input = KeyFrame(shiftDown: false, Key.R);
            m.Update(0.016f);

            // R re-issued the move with a null Y: the placement ground-snaps and its X/Z are preserved.
            Assert.Null(scene.Document.Doc.Placements[0].Y);
            Near(3f, scene.Document.Doc.Placements[0].X);
            Near(4f, scene.Document.Doc.Placements[0].Z);
            Assert.True(scene.Document.History.CanUndo);
        }

        [Fact]
        public void RKey_NoOpWhenAlreadyGrounded()
        {
            var scene = new DocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Placements.Add(new MapPlacement { Id = "hut", Kind = "prop", X = 3f, Z = 4f, Y = null });
                return doc;
            });
            scene.Init(null!, null!, null!, new MapEditorOptions());
            var m = new SceneManager();
            m.Push(scene);

            scene.Document.Selection.Set(SelectionKind.Placement, "hut");

            m.Input = KeyFrame(shiftDown: false, Key.R);
            m.Update(0.016f);

            // Already grounded (null Y): no empty command lands on the undo stack.
            Assert.Null(scene.Document.Doc.Placements[0].Y);
            Assert.False(scene.Document.History.CanUndo);
        }

        // ---- Cmd-aware chords + focused-field gating ------------------------------------------------------

        [Fact]
        public void Chords_FireWithCtrl_AndWithSuper()
        {
            var scene = new DocScene(() => ValidDoc());
            scene.Init(null!, null!, null!, new MapEditorOptions());
            var m = new SceneManager();
            m.Push(scene);

            scene.Document.Execute(new AddSpawnCommand(NewSpawn("s1")));
            scene.Document.Execute(new AddSpawnCommand(NewSpawn("s2")));
            Assert.Equal(2, scene.Document.Doc.Spawns.Count);

            m.Input = CtrlKeyFrame(Key.Z);
            m.Update(0.016f);
            Assert.Single(scene.Document.Doc.Spawns);      // Ctrl+Z undid s2

            m.Input = SuperKeyFrame(Key.Z);
            m.Update(0.016f);
            Assert.Empty(scene.Document.Doc.Spawns);       // Cmd+Z (Super) undid s1 too
        }

        [Fact]
        public void Chords_DoNotFire_WhileFieldFocused()
        {
            var scene = new DocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Terrain.Features.Add(new LakeFeatureDoc { CenterX = 0f, CenterZ = 0f, Radius = 5f, Depth = 2f });
                return doc;
            });
            scene.Init(null!, null!, null!, new MapEditorOptions());
            var m = new SceneManager();
            m.Push(scene);

            scene.Document.Selection.Set(SelectionKind.Feature, "0");
            scene.Document.Execute(new AddSpawnCommand(NewSpawn("s1")));
            Assert.True(scene.Document.History.CanUndo);

            FloatRow radius = FloatRowByLabel(scene.Inspector, "Radius");
            BeginEditing(radius);
            Assert.True(radius.Field.IsEditing);   // precondition: the row owns an active edit

            m.Input = CtrlKeyFrame(Key.Z);
            m.Update(0.016f);

            // The field owns this Escape/chord frame: Ctrl+Z must not reach the document, and the field's own
            // in-progress edit is left completely untouched (not even implicitly cancelled).
            Assert.True(scene.Document.History.CanUndo);
            Assert.True(radius.Field.IsEditing);
        }

        [Fact]
        public void Escape_WhileEditing_CancelsFieldOnly_NotTool()
        {
            var scene = new FieldDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Placements.Add(new MapPlacement { Id = "hut", Kind = "prop", X = 3f, Z = 4f, Y = null });
                return doc;
            });
            scene.Init(null!, null!, null!, new MapEditorOptions());
            var m = new SceneManager();
            m.Push(scene);

            scene.Document.Selection.Set(SelectionKind.Placement, "hut");
            scene.Controller.Mode = EditorToolMode.PlacePlacement;   // a mode Escape would normally cancel to Select

            FloatRow x = FloatRowByLabel(scene.Inspector, "X");
            BeginEditing(x);
            Assert.True(x.Field.IsEditing);

            m.Input = KeyFrame(shiftDown: false, Key.Escape);
            m.Update(0.016f);

            // Escape belonged to the field this frame: BuildFrameInput suppresses the tool-cancel edge while the
            // inspector has an active editor, so the tool mode is untouched (the ledgered double-fire is fixed).
            Assert.Equal(EditorToolMode.PlacePlacement, scene.Controller.Mode);

            // The live grid (UiViewport-driven) is what would normally run the field's own Escape->CancelEdit path
            // this same frame. This headless suite drives PropertyRows directly (no UiViewport), so close the edit
            // the same way the grid's cull path does, then confirm a clean-state Escape cancels the tool as usual.
            x.Field.CancelEdit();
            Assert.False(x.Field.IsEditing);

            m.Input = KeyFrame(shiftDown: false, Key.Escape);
            m.Update(0.016f);

            Assert.Equal(EditorToolMode.Select, scene.Controller.Mode);
        }

        [Fact]
        public void BareR_UsesAggregateGuard()
        {
            // R while a FloatRow (not the rename row) is focused must not snap - the old guard only checked
            // _nameRow, so this pins the generalization to the aggregate PropertyGrid.HasActiveEditor query.
            var scene = new DocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Placements.Add(new MapPlacement { Id = "hut", Kind = "prop", X = 3f, Z = 4f, Y = 12f });
                return doc;
            });
            scene.Init(null!, null!, null!, new MapEditorOptions());
            var m = new SceneManager();
            m.Push(scene);

            scene.Document.Selection.Set(SelectionKind.Placement, "hut");
            FloatRow xRow = FloatRowByLabel(scene.Inspector, "X");
            BeginEditing(xRow);
            Assert.True(xRow.Field.IsEditing);

            m.Input = KeyFrame(shiftDown: false, Key.R);
            m.Update(0.016f);

            // Still airborne: R did not snap while the X field was focused, and no command landed on the stack.
            Assert.NotNull(scene.Document.Doc.Placements[0].Y);
            Assert.False(scene.Document.History.CanUndo);
        }

        [Fact]
        public void Chords_DoNotFire_WhileFilterFocused()
        {
            // Ctrl+Z while the kit-palette filter is focused must not undo - the filter is a focusable editor
            // outside the inspector's aggregate query, so AnyEditorFocused must catch it too, not just the grid.
            var scene = new DocScene(() => ValidDoc());
            scene.Init(null!, null!, null!, new MapEditorOptions());
            var m = new SceneManager();
            m.Push(scene);

            scene.Document.Execute(new AddSpawnCommand(NewSpawn("s1")));
            Assert.True(scene.Document.History.CanUndo);

            scene.Controller.Mode = EditorToolMode.PlacePlacement;   // the mode that shows the kit palette filter
            scene.PaletteFilter.Focus();
            Assert.True(scene.PaletteFilter.IsFocused);   // precondition: the filter owns focus

            m.Input = CtrlKeyFrame(Key.Z);
            m.Update(0.016f);

            // The filter owns this chord frame: Ctrl+Z must not reach the document.
            Assert.True(scene.Document.History.CanUndo);
        }

        [Fact]
        public void BareR_DoesNotSnap_WhileFilterFocused()
        {
            // R while the kit-palette filter is focused must not snap the selected placement to ground - typing a
            // kit name that contains "r" (e.g. "oak tree") would otherwise fire the ground-snap hotkey mid-keystroke.
            var scene = new DocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Placements.Add(new MapPlacement { Id = "hut", Kind = "prop", X = 3f, Z = 4f, Y = 12f });
                return doc;
            });
            scene.Init(null!, null!, null!, new MapEditorOptions());
            var m = new SceneManager();
            m.Push(scene);

            scene.Document.Selection.Set(SelectionKind.Placement, "hut");
            scene.Controller.Mode = EditorToolMode.PlacePlacement;   // the mode that shows the kit palette filter
            scene.PaletteFilter.Focus();
            Assert.True(scene.PaletteFilter.IsFocused);   // precondition: the filter owns focus

            m.Input = KeyFrame(shiftDown: false, Key.R);
            m.Update(0.016f);

            // Still airborne: R did not snap while the filter was focused, and no command landed on the stack.
            Assert.NotNull(scene.Document.Doc.Placements[0].Y);
            Assert.False(scene.Document.History.CanUndo);
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
        public void TerrainNode_InspectorShowsEveryScalar_AndBiomeCount()
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

            // Every terrain scalar is now an editable FloatRow (decision 8 widened EditTerrainCommand + inspector).
            foreach (string label in new[]
                { "WaterLevel", "Seed", "BiomeBlend", "GentleFrequency", "GentleAmplitude", "DetailFrequency", "DetailOctaves" })
                Assert.IsType<FloatRow>(FloatRowByLabel(scene.Inspector, label));

            // The seed FloatRow shows the live seed value, and the biome count stays a read-only display.
            Assert.Equal(99f, FloatRowByLabel(scene.Inspector, "Seed").Field.Value);
            ReadOnlyRow biomes = ReadOnlyRowByLabel(scene.Inspector, "Biomes");
            biomes.Update(new Rect(0f, 0f, 200f, 28f), new InputManager(), 0f);
            Assert.Equal("2", biomes.Display);
        }

        [Fact]
        public void TerrainNode_SeedRow_EditsSeedThroughCommand()
        {
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Terrain.Seed = 5;
                return doc;
            });
            scene.Document.Selection.Set(SelectionKind.Terrain, "");
            FloatRow seed = FloatRowByLabel(scene.Inspector, "Seed");

            // Scrub +100 px at the seed row's DragScale (1.0 per px -> +100), which rounds to an int seed.
            var cell = new Rect(0f, 0f, 200f, 28f);
            var ui = new InputManager();
            ui.Update(MouseFrame(new Vector2(100f, 10f), leftDown: false)); seed.Update(cell, ui, 0.016f);
            ui.Update(MouseFrame(new Vector2(100f, 10f), leftDown: true)); seed.Update(cell, ui, 0.016f);
            ui.Update(MouseFrame(new Vector2(101f, 10f), leftDown: true)); seed.Update(cell, ui, 0.016f);

            Assert.Equal(6, scene.Document.Doc.Terrain.Seed);   // +1 scrub, stored as an int
            Assert.True(scene.Document.WorldRebuildPending);
            Assert.True(scene.Document.History.CanUndo);
            Assert.True(scene.Document.Undo());
            Assert.Equal(5, scene.Document.Doc.Terrain.Seed);
        }

        // ---- biome bands (outline category + inspector) ------------------------------------------------

        [Fact]
        public void BiomesCategory_InOutline_SelectableEditable()
        {
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Terrain.Biomes.Add(new MapBiomeBand
                    { Start = 0f, End = 40f, Biome = KhaozEngine.Terrain.BiomeId.Meadow, BaseHeight = 2f, HillAmplitude = 3f });
                doc.Terrain.Biomes.Add(new MapBiomeBand
                    { Start = 40f, End = null, Biome = KhaozEngine.Terrain.BiomeId.Mountains });
                return doc;
            });

            // A Biomes category sits beside Terrain, with one selectable node per band plus a trailing add action.
            TreeNode band0 = CategoryChild(scene.Outline, "Biomes", 0);
            TreeNode band1 = CategoryChild(scene.Outline, "Biomes", 1);
            Assert.Equal("[0] Meadow 0..40", band0.Label.Resolve());
            Assert.Equal("[1] Mountains 40..*", band1.Label.Resolve());   // open end edge renders as "*"

            // Selecting the band node builds its editable inspector (Biome choice + scalar rows).
            scene.Document.Selection.Set(SelectionKind.BiomeBand, "0");
            Assert.IsType<ChoiceRow>(scene.Inspector.Rows[0]);
            Assert.Equal("Meadow", ((ChoiceRow)scene.Inspector.Rows[0]).Selected);
            Assert.Equal(2f, FloatRowByLabel(scene.Inspector, "BaseHeight").Field.Value);
            Assert.Equal(3f, FloatRowByLabel(scene.Inspector, "HillAmplitude").Field.Value);
            Assert.Equal(0f, FloatRowByLabel(scene.Inspector, "Start").Field.Value);
            Assert.Equal(40f, FloatRowByLabel(scene.Inspector, "End").Field.Value);
        }

        [Fact]
        public void BiomeBandInspector_NullableEdges()
        {
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Terrain.Biomes.Add(new MapBiomeBand
                    { Start = 0f, End = 40f, Biome = KhaozEngine.Terrain.BiomeId.Meadow });
                return doc;
            });
            scene.Document.Selection.Set(SelectionKind.BiomeBand, "0");

            // Start is a concrete 0 to begin with, so "Start open" reads off (the edge is not open).
            BoolRow startOpen = BoolRowByLabel(scene.Inspector, "Start open");
            Assert.False(startOpen.Toggle.IsOn);

            // Tapping "Start open" flips the nullable Start edge to null (open, -infinity).
            Assert.True(TapBool(startOpen));
            Assert.Null(scene.Document.Doc.Terrain.Biomes[0].Start);

            // Editing the Start FloatRow to a concrete value closes the open edge back to that value.
            FloatRow start = FloatRowByLabel(scene.Inspector, "Start");
            var cell = new Rect(0f, 0f, 200f, 28f);
            var ui = new InputManager();
            ui.Update(MouseFrame(new Vector2(100f, 10f), leftDown: false)); start.Update(cell, ui, 0.016f);
            ui.Update(MouseFrame(new Vector2(100f, 10f), leftDown: true)); start.Update(cell, ui, 0.016f);   // press inside (grab-gate origin)
            ui.Update(MouseFrame(new Vector2(200f, 10f), leftDown: true)); start.Update(cell, ui, 0.016f);   // +100 px at 0.01 = +1.0 from 0
            Assert.NotNull(scene.Document.Doc.Terrain.Biomes[0].Start);

            // The End edge opens the same way through its own paired toggle.
            BoolRow endOpen = BoolRowByLabel(scene.Inspector, "End open");
            Assert.True(TapBool(endOpen));
            Assert.Null(scene.Document.Doc.Terrain.Biomes[0].End);
        }

        [Fact]
        public void BiomeBand_AddViaOutlineAction_AppendsAndSelects()
        {
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Terrain.Biomes.Add(new MapBiomeBand { Biome = KhaozEngine.Terrain.BiomeId.Meadow });
                return doc;
            });

            TreeView outline = scene.Outline;
            outline.Bounds = new Rect(0f, 0f, 240f, 600f);   // tall enough for every outline row
            // The add action is the last child under the Biomes category (after the single band node).
            TreeNode addNode = CategoryChild(outline, "Biomes", 1);
            Assert.Equal("[+ add band]", addNode.Label.Resolve());

            var input = new InputManager();
            TapTree(outline, input, RowCenter(outline, addNode));

            Assert.Equal(2, scene.Document.Doc.Terrain.Biomes.Count);   // a band was appended
            Assert.Equal(SelectionKind.BiomeBand, scene.Document.Selection.Kind);
            Assert.Equal("1", scene.Document.Selection.Id);             // the new band is selected
            Assert.True(scene.Document.WorldRebuildPending);
        }

        [Fact]
        public void BandDelete_ViaOutlineSelection()
        {
            var a = new MapBiomeBand { Biome = KhaozEngine.Terrain.BiomeId.Meadow };
            var b = new MapBiomeBand { Biome = KhaozEngine.Terrain.BiomeId.Desert };
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Terrain.Biomes.Add(a);
                doc.Terrain.Biomes.Add(b);
                return doc;
            });

            // Select the first band from the outline, then press Delete: the standard delete path removes it.
            scene.Document.Selection.Set(SelectionKind.BiomeBand, "0");
            scene.Controller.Update(new EditorFrameInput(Vector3.Zero, Vector3.UnitZ, deletePressed: true));

            Assert.Single(scene.Document.Doc.Terrain.Biomes);
            Assert.Same(b, scene.Document.Doc.Terrain.Biomes[0]);
            Assert.Equal(SelectionKind.None, scene.Document.Selection.Kind);   // selection cleared after delete
            Assert.True(scene.Document.History.CanUndo);
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

            // A disc exclusion gets the editable shape surface: the kind selector plus one FloatRow per param.
            Assert.IsType<ChoiceRow>(scene.Inspector.Rows[0]);
            Assert.NotNull(FloatRowByLabel(scene.Inspector, "CenterX"));
            Assert.NotNull(FloatRowByLabel(scene.Inspector, "CenterZ"));
            Assert.NotNull(FloatRowByLabel(scene.Inspector, "Radius"));
        }

        // ---- naming + layer targeting -------------------------------------------------------------------

        [Fact]
        public void FeatureNode_ShowsNameWhenSet_IndexTypeOtherwise()
        {
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Terrain.Features.Add(new LakeFeatureDoc { Name = "Big Lake", CenterX = 0f, CenterZ = 0f, Radius = 5f, Depth = 2f });
                doc.Terrain.Features.Add(new FlattenFeatureDoc { CenterX = 1f, CenterZ = 1f, Radius = 4f, TargetHeight = 1f });
                return doc;
            });

            TreeNode named = CategoryChild(scene.Outline, "Features", 0);
            TreeNode unnamed = CategoryChild(scene.Outline, "Features", 1);

            Assert.Equal("Big Lake", named.Label.Resolve());
            Assert.Equal("[1] flatten", unnamed.Label.Resolve());
        }

        [Fact]
        public void ExclusionNode_ShowsTargetingHint()
        {
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.ScatterLayers.Add(new MapScatterLayer { Name = "trees" });
                doc.ScatterLayers.Add(new MapScatterLayer { Name = "groundcover" });
                doc.Exclusions.Add(new MapExclusion { Shape = new DiscShapeDoc { Radius = 5f }, Layers = null });
                doc.Exclusions.Add(new MapExclusion
                {
                    Name = "no-trees",
                    Shape = new DiscShapeDoc { Radius = 3f },
                    Layers = new List<string> { "trees", "groundcover" },
                });
                return doc;
            });

            TreeNode all = CategoryChild(scene.Outline, "Exclusions", 0);
            TreeNode named = CategoryChild(scene.Outline, "Exclusions", 1);

            // Unnamed falls back to the index label. Named or not, every exclusion carries the targeting hint.
            Assert.Equal("exclusion[0] (all)", all.Label.Resolve());
            Assert.Equal("no-trees (trees, groundcover)", named.Label.Resolve());
        }

        [Fact]
        public void FeatureRenameRow_ExecutesRenameCommand_SelectionStaysOnIndex()
        {
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Terrain.Features.Add(new LakeFeatureDoc { CenterX = 0f, CenterZ = 0f, Radius = 5f, Depth = 2f });
                doc.Terrain.Features.Add(new FlattenFeatureDoc
                {
                    Name = "taken", CenterX = 1f, CenterZ = 1f, Radius = 4f, TargetHeight = 1f,
                });
                return doc;
            });

            scene.Document.Selection.Set(SelectionKind.Feature, "0");
            TextRow name = TextRowByLabel(scene.Inspector, "Name");

            var ui = new InputManager();
            ui.Update(InputState.Empty);
            name.Input.IsFocused = true;
            name.Input.SetText("north-lake");
            name.Update(new Rect(0f, 0f, 200f, 28f), ui, 0.016f);

            // Features are index-addressed: unlike the region/placement/spawn Name row, a rename never moves the
            // selection (it stays glued to the same list index), so there is no pending re-select to wait on.
            Assert.Equal("north-lake", scene.Document.Doc.Terrain.Features[0].Name);
            Assert.Equal(SelectionKind.Feature, scene.Document.Selection.Kind);
            Assert.Equal("0", scene.Document.Selection.Id);
            Assert.True(scene.Document.History.CanUndo);

            // A collision with another feature's live name is rejected before RenameFeatureCommand's own guard
            // would throw, so no command lands and the name is left untouched.
            name.Input.SetText("taken");
            name.Update(new Rect(0f, 0f, 200f, 28f), ui, 0.016f);
            Assert.Equal("north-lake", scene.Document.Doc.Terrain.Features[0].Name);

            // Clearing to blank is a legal target (Name is optional): the feature falls back to its index label.
            name.Input.SetText("");
            name.Update(new Rect(0f, 0f, 200f, 28f), ui, 0.016f);
            Assert.Null(scene.Document.Doc.Terrain.Features[0].Name);
        }

        [Fact]
        public void ExclusionRenameRow_ExecutesRenameCommand_SelectionStaysOnIndex()
        {
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Exclusions.Add(new MapExclusion { Shape = new DiscShapeDoc { Radius = 5f } });
                return doc;
            });

            scene.Document.Selection.Set(SelectionKind.Exclusion, "0");
            TextRow name = TextRowByLabel(scene.Inspector, "Name");

            var ui = new InputManager();
            ui.Update(InputState.Empty);
            name.Input.IsFocused = true;
            name.Input.SetText("no-scatter-zone");
            name.Update(new Rect(0f, 0f, 200f, 28f), ui, 0.016f);

            Assert.Equal("no-scatter-zone", scene.Document.Doc.Exclusions[0].Name);
            Assert.Equal(SelectionKind.Exclusion, scene.Document.Selection.Kind);
            Assert.Equal("0", scene.Document.Selection.Id);
            Assert.True(scene.Document.History.CanUndo);
        }

        [Fact]
        public void ExclusionLayerRows_AllToggle_NullSemantics()
        {
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.ScatterLayers.Add(new MapScatterLayer { Name = "trees" });
                doc.ScatterLayers.Add(new MapScatterLayer { Name = "rocks" });
                doc.Exclusions.Add(new MapExclusion { Shape = new DiscShapeDoc { Radius = 5f }, Layers = null });
                return doc;
            });

            scene.Document.Selection.Set(SelectionKind.Exclusion, "0");

            // All-on (Layers null): only the All toggle shows, the per-layer membership rows are hidden.
            Assert.Null(scene.Document.Doc.Exclusions[0].Layers);
            Assert.DoesNotContain(scene.Inspector.Rows.OfType<BoolRow>(), b => b.Label.Resolve() == "trees");

            BoolRow all = BoolRowByLabel(scene.Inspector, "All layers");
            Assert.True(TapBool(all));   // flips All off
            Assert.Equal(new[] { "trees", "rocks" }, scene.Document.Doc.Exclusions[0].Layers);   // materializes the full list
            Assert.True(scene.Document.WorldRebuildPending);

            // The per-layer rows reflow into view the next chrome step (the shape-kind-conversion idiom).
            scene.OnUpdate(0.016f);
            BoolRow trees = BoolRowByLabel(scene.Inspector, "trees");

            Assert.True(TapBool(trees));   // uncheck trees: stays an explicit list, minus "trees"
            Assert.Equal(new[] { "rocks" }, scene.Document.Doc.Exclusions[0].Layers);

            // Manually re-checking every layer stays an explicit list: only the All toggle itself produces null.
            Assert.True(TapBool(trees));
            Assert.NotNull(scene.Document.Doc.Exclusions[0].Layers);
            Assert.Equal(new[] { "rocks", "trees" }, scene.Document.Doc.Exclusions[0].Layers);
        }

        [Fact]
        public void ExclusionLayerRow_TogglesMembership_WorldRebuildPending()
        {
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.ScatterLayers.Add(new MapScatterLayer { Name = "trees" });
                doc.ScatterLayers.Add(new MapScatterLayer { Name = "rocks" });
                doc.Exclusions.Add(new MapExclusion
                {
                    Shape = new DiscShapeDoc { Radius = 5f },
                    Layers = new List<string> { "trees", "rocks" },
                });
                return doc;
            });

            scene.Document.Selection.Set(SelectionKind.Exclusion, "0");
            Assert.False(scene.Document.WorldRebuildPending);

            BoolRow trees = BoolRowByLabel(scene.Inspector, "trees");
            Assert.True(TapBool(trees));

            Assert.Equal(new[] { "rocks" }, scene.Document.Doc.Exclusions[0].Layers);
            Assert.True(scene.Document.WorldRebuildPending);   // exclusion targeting affects the streamed scatter
            Assert.True(scene.Document.History.CanUndo);

            Assert.True(scene.Document.Undo());
            Assert.Equal(new[] { "trees", "rocks" }, scene.Document.Doc.Exclusions[0].Layers);
        }

        [Fact]
        public void RegionInspector_EditsDiscParams()
        {
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Regions.Add(new MapRegion { Name = "town", Shape = new DiscShapeDoc { CenterX = 0f, CenterZ = 18f, Radius = 12f } });
                return doc;
            });

            scene.Document.Selection.Set(SelectionKind.Region, "town");
            FloatRow radius = FloatRowByLabel(scene.Inspector, "Radius");

            // Scrub the radius row headless (the NumberField grab-gate idiom): press inside the editor cell,
            // then drag +100 px at DragScale 0.01 = +1.0, so the row writes through EditRegionShapeCommand.
            var cell = new Rect(0f, 0f, 200f, 28f);
            var ui = new InputManager();
            ui.Update(MouseFrame(new Vector2(100f, 10f), leftDown: false));
            radius.Update(cell, ui, 0.016f);
            ui.Update(MouseFrame(new Vector2(100f, 10f), leftDown: true));    // press inside (grab-gate origin)
            radius.Update(cell, ui, 0.016f);
            ui.Update(MouseFrame(new Vector2(200f, 10f), leftDown: true));    // +100 px at DragScale 0.01 = +1.0
            bool changed = radius.Update(cell, ui, 0.016f);

            Assert.True(changed);
            var disc = Assert.IsType<DiscShapeDoc>(scene.Document.Doc.Regions[0].Shape);
            Near(13f, disc.Radius);                                // 12 + 1 scrub
            Near(0f, disc.CenterX);                                // the clone changed ONLY the scrubbed field
            Near(18f, disc.CenterZ);
            Assert.False(scene.Document.WorldRebuildPending);      // regions never force a world rebuild
            Assert.True(scene.Document.History.CanUndo);

            Assert.True(scene.Document.Undo());
            disc = Assert.IsType<DiscShapeDoc>(scene.Document.Doc.Regions[0].Shape);
            Near(12f, disc.Radius);                                // undo restores the pre-scrub shape
        }

        [Fact]
        public void ShapeKindChoice_ConvertsDiscToRectPreservingCenter()
        {
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Exclusions.Add(new MapExclusion { Shape = new DiscShapeDoc { CenterX = 2f, CenterZ = 3f, Radius = 5f } });
                return doc;
            });

            scene.Document.Selection.Set(SelectionKind.Exclusion, "0");
            scene.Inspector.Bounds = new Rect(0f, 0f, 300f, 400f);
            Assert.IsType<ChoiceRow>(scene.Inspector.Rows[0]);

            // Row 0's editor cell is x 135..300, y 0..28 (LabelFraction 0.45 of 300); with two options the open
            // list sits at y 28..56 ("disc") and y 56..84 ("rect"). Tap the trigger, then pick "rect".
            var ui = new InputManager();
            TapGrid(scene.Inspector, ui, new Vector2(200f, 14f));   // open the kind list
            TapGrid(scene.Inspector, ui, new Vector2(200f, 70f));   // pick "rect"

            // Disc to rect converts center-preservingly: the square of side 2r around the disc center.
            var rect = Assert.IsType<RectShapeDoc>(scene.Document.Doc.Exclusions[0].Shape);
            Near(-3f, rect.MinX);
            Near(-2f, rect.MinZ);
            Near(7f, rect.MaxX);
            Near(8f, rect.MaxZ);
            Assert.True(scene.Document.WorldRebuildPending);        // exclusion shape edits rebuild the world
            Assert.True(scene.Document.History.CanUndo);

            // The kind changed, so the inspector reflows to rect param rows on the next chrome step.
            scene.OnUpdate(0.016f);
            Assert.NotNull(FloatRowByLabel(scene.Inspector, "MinX"));

            // Converting back picks the rect's center plus half its max extent, landing on the original disc.
            TapGrid(scene.Inspector, ui, new Vector2(200f, 14f));   // open the kind list (now showing "rect")
            TapGrid(scene.Inspector, ui, new Vector2(200f, 42f));   // pick "disc" (option 0)
            var disc = Assert.IsType<DiscShapeDoc>(scene.Document.Doc.Exclusions[0].Shape);
            Near(2f, disc.CenterX);
            Near(3f, disc.CenterZ);
            Near(5f, disc.Radius);
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

        // ---- visibility layers ---------------------------------------------------------------------------

        [Fact]
        public void HiddenElement_NotPickable_StillInOutline()
        {
            var scene = new FieldDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Placements.Add(new MapPlacement { Id = "hut", Kind = "prop", X = 0f, Z = 0f, Y = null });
                return doc;
            });
            scene.Init(null!, null!, null!, new MapEditorOptions());
            var m = new SceneManager();
            m.Push(scene);

            var down = new Vector3(0f, -1f, 0f);
            EditorFrameInput Press() => new EditorFrameInput(new Vector3(0f, 100f, 0f), down,
                pointerPressed: true, pointerDown: true, dt: 0.016f);

            // Sanity: while visible, a viewport pick over the hut selects it.
            scene.Controller.Update(Press());
            Assert.Equal(SelectionKind.Placement, scene.Document.Selection.Kind);
            Assert.Equal("hut", scene.Document.Selection.Id);

            scene.Document.Selection.Clear();
            scene.Visibility.SetElementHidden(SelectionKind.Placement, "hut", true);   // hide it

            // The same pick now skips the hut and falls through to the bare ground: nothing is selectable there.
            scene.Controller.Update(Press());
            Assert.Equal(SelectionKind.None, scene.Document.Selection.Kind);

            // But it is still in the outline: visibility is view-only and never mutates the document.
            Assert.Contains(scene.Document.Doc.Placements, p => p.Id == "hut");
            Assert.True(OutlineListsPlacement(scene.Outline, "hut"), "hidden placement still appears in the outline");
        }

        [Fact]
        public void HiddenExclusion_SurvivesDeleteOfEarlierIndex()
        {
            // Delete runs through EditorToolController.Update, which UpdateTools gates on a built Field, so this
            // needs FieldDocScene (not the plain DocScene the other feature/exclusion tests use).
            var scene = new FieldDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Exclusions.Add(new MapExclusion { Shape = new DiscShapeDoc { Radius = 1f } });   // index 0: about to be deleted
                doc.Exclusions.Add(new MapExclusion { Shape = new DiscShapeDoc { Radius = 2f } });   // index 1
                doc.Exclusions.Add(new MapExclusion { Shape = new DiscShapeDoc { Radius = 3f } });   // index 2: hidden
                return doc;
            });
            scene.Init(null!, null!, null!, new MapEditorOptions());
            var m = new SceneManager();
            m.Push(scene);

            scene.Visibility.SetElementHidden(SelectionKind.Exclusion, "2", true);
            scene.Document.Selection.Set(SelectionKind.Exclusion, "0");   // select the one about to be deleted

            m.Input = KeyFrame(shiftDown: false, Key.Delete);
            m.Update(0.016f);

            Assert.Equal(2, scene.Document.Doc.Exclusions.Count);   // the earlier exclusion was removed
            Assert.True(scene.Visibility.IsElementHidden(SelectionKind.Exclusion, "1"));    // the hidden one shifted down to index 1
            Assert.False(scene.Visibility.IsElementHidden(SelectionKind.Exclusion, "2"));   // nothing hidden at the old tail slot
        }

        [Fact]
        public void EmptySelection_InspectorShowsLayersPanel()
        {
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.ScatterLayers.Add(new MapScatterLayer { Name = "trees" });
                doc.ScatterLayers.Add(new MapScatterLayer { Name = "rocks" });
                return doc;
            });

            // Nothing is selected at startup, so the inspector is the Layers panel: one BoolRow per visibility group
            // then one per named scatter layer.
            Assert.Equal(SelectionKind.None, scene.Document.Selection.Kind);
            List<BoolRow> bools = scene.Inspector.Rows.OfType<BoolRow>().ToList();
            List<string> labels = bools.Select(b => b.Label.Resolve()).ToList();

            Assert.Equal(6 + 2, bools.Count);   // six groups + two scatter layers
            Assert.Contains("Placements", labels);
            Assert.Contains("Spawns", labels);
            Assert.Contains("Water", labels);
            Assert.Contains("Exclusions", labels);
            Assert.Contains("Regions", labels);
            Assert.Contains("Feature markers", labels);
            Assert.Contains("trees", labels);
            Assert.Contains("rocks", labels);
        }

        [Fact]
        public void SelectedElement_VisibleRowTogglesHiddenSet()
        {
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Placements.Add(new MapPlacement { Id = "hut", Kind = "prop", X = 1f, Z = 2f });
                return doc;
            });

            scene.Document.Selection.Set(SelectionKind.Placement, "hut");
            BoolRow visible = BoolRowByLabel(scene.Inspector, "Visible");

            // Starts visible: the toggle is on, the hidden set is empty.
            Assert.False(scene.Visibility.IsElementHidden(SelectionKind.Placement, "hut"));

            // Tap the toggle: the placement flips to hidden.
            Assert.True(TapBool(visible));
            Assert.True(scene.Visibility.IsElementHidden(SelectionKind.Placement, "hut"));

            // Tapping again shows it: the hidden entry is removed.
            Assert.True(TapBool(visible));
            Assert.False(scene.Visibility.IsElementHidden(SelectionKind.Placement, "hut"));
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

        // ---- palette visibility ----------------------------------------------------------------------------

        [Fact]
        public void Palette_VisibleOnlyInPlaceMode()
        {
            var scene = PushPaletteScene(KitCategories(), "wolf");

            // The kit palette shows ONLY in the prop-place mode; the spawn picker owns the panel in spawn mode.
            foreach (EditorToolMode mode in Enum.GetValues<EditorToolMode>())
            {
                scene.Controller.Mode = mode;
                Assert.Equal(mode == EditorToolMode.PlacePlacement, scene.KitPaletteVisible);
            }

            // Outside the two Place modes the panel region is empty and the outline reflows over the freed space.
            scene.Controller.Mode = EditorToolMode.Select;
            Rect outlineFull = scene.OutlineRect(1000f, 600f);
            Near(0f, scene.PaletteRect(1000f, 600f).Height);
            Near(scene.StatusRect(1000f, 600f).Y, outlineFull.Bottom);   // the outline runs to the status strip

            // In the place mode the panel returns and the outline gives the space back (the two stack exactly).
            scene.Controller.Mode = EditorToolMode.PlacePlacement;
            Rect outlineHalf = scene.OutlineRect(1000f, 600f);
            Rect palette = scene.PaletteRect(1000f, 600f);
            Assert.True(palette.Height > 0f);
            Near(palette.Y, outlineHalf.Bottom);
            Near(scene.StatusRect(1000f, 600f).Y, palette.Bottom);

            // The spawn tool also shows the bottom panel (the spawn picker), just not the kit palette.
            scene.Controller.Mode = EditorToolMode.PlaceSpawn;
            Assert.True(scene.PaletteRect(1000f, 600f).Height > 0f);
            Assert.False(scene.KitPaletteVisible);
        }

        // ---- feature-type picker -------------------------------------------------------------------------

        [Fact]
        public void FeatureList_ShowsRegistryTypes_AndSelectionSetsPlaceFeatureType()
        {
            var scene = PushDocScene(ValidDoc);

            // The feature-type picker lists the registry's built-in feature types in registration order, flat.
            Assert.Equal(new[] { "lake", "flatten", "ridge", "rim" },
                scene.FeatureList.Roots.Select(r => r.Label.Resolve()).ToArray());
            Assert.All(scene.FeatureList.Roots, r => Assert.Empty(r.Children));

            // The default placed type is the first registered type.
            Assert.Equal("lake", scene.Controller.PlaceFeatureType);

            // Tapping the "ridge" leaf (visible index 2) sets the controller's PlaceFeatureType.
            scene.FeatureList.Bounds = new Rect(0f, 0f, 200f, 240f);
            scene.FeatureList.RowHeight = 22f;
            var input = new InputManager();
            TapTree(scene.FeatureList, input, new Vector2(120f, 2 * 22f + 11f));
            Assert.Equal("ridge", scene.Controller.PlaceFeatureType);
        }

        [Fact]
        public void FeaturePanel_ShowsOnlyInEditFeatureMode()
        {
            var scene = PushDocScene(ValidDoc);

            scene.Controller.Mode = EditorToolMode.EditFeature;
            Assert.True(scene.PaletteRect(1000f, 600f).Height > 0f);   // the feature picker owns the bottom panel
            Assert.False(scene.KitPaletteVisible);                     // but it is not the kit palette

            scene.Controller.Mode = EditorToolMode.Select;
            Near(0f, scene.PaletteRect(1000f, 600f).Height);           // no panel outside the picker tools
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

        // ---- toolbar mode sync ---------------------------------------------------------------------------

        [Fact]
        public void Toolbar_ReflectsOneShotReturnToSelect()
        {
            var scene = new SpyScene();
            scene.Init(null!, null!, null!, new MapEditorOptions());
            var m = new SceneManager { Input = InputState.Empty };
            m.Push(scene);

            // The user taps the Region tab: the toolbar and controller both enter the one-shot DrawRegion tool.
            scene.Toolbar.ActiveIndex = (int)EditorToolMode.DrawRegion;
            scene.Controller.Mode = EditorToolMode.DrawRegion;

            // Drive a completed disc-drag over a flat field straight on the controller (the viewport gesture the
            // scene would feed it). A finished draw commits one region and returns the controller to Select on its
            // own, which the one-way tab tap never observes.
            scene.Controller.Field = new KhaozEngine.Terrain.TerrainField(
                new KhaozEngine.Terrain.TerrainConfig { GentleAmplitude = 0f });
            var down = new Vector3(0f, -1f, 0f);
            scene.Controller.Update(new EditorFrameInput(new Vector3(0f, 100f, 0f), down,
                pointerPressed: true, pointerDown: true, dt: 0.016f));
            scene.Controller.Update(new EditorFrameInput(new Vector3(3f, 100f, 0f), down,
                pointerReleased: true, dt: 0.016f));

            Assert.Equal(EditorToolMode.Select, scene.Controller.Mode);                  // one shot returned to Select
            Assert.Equal((int)EditorToolMode.DrawRegion, scene.Toolbar.ActiveIndex);     // toolbar still stale

            m.Update(0.016f);   // the scene's per-frame toolbar-to-mode sync fires

            Assert.Equal((int)EditorToolMode.Select, scene.Toolbar.ActiveIndex);
        }

        // ---- status-strip bottom offset ------------------------------------------------------------------

        [Fact]
        public void StatusStrip_HonorsBottomOffset()
        {
            var flush = new SpyScene();
            flush.Init(null!, null!, null!, new MapEditorOptions());

            var reserved = new SpyScene();
            reserved.Init(null!, null!, null!, new MapEditorOptions { StatusBottomOffset = 36f });

            Rect a = flush.StatusRect(1000f, 600f);
            Rect b = reserved.StatusRect(1000f, 600f);

            Near(36f, a.Y - b.Y);          // the strip shifts UP by exactly the reserved offset
            Near(a.Height, b.Height);      // same strip height, just relocated
            Near(a.Width, b.Width);
        }
    }
}
