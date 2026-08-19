using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Game;
using KhaozEngine.Gui;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Primitives;
using KhaozEngine.Terrain;
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
    public partial class MapEditorSceneTests
    {
        // Records BuildWorld / TeardownWorld, and skips every device call so OnEnter/OnExit run headless.
        sealed class SpyScene : MapEditorScene
        {
            public int Builds, Teardowns;
            protected override void BuildWorld() => Builds++;
            protected override void TeardownWorld() => Teardowns++;
        }

        // Records the per-frame step order: every step just logs its name instead of touching a device.
        sealed class OrderScene : MapEditorScene
        {
            public readonly List<string> Log = new();
            protected override void BuildWorld() { }
            protected override void TeardownWorld() { }
            protected override void UpdateCamera(float dt) => Log.Add("camera");
            protected override void UpdateTools(float dt) => Log.Add("tools");
            protected override void UpdateChrome(float dt) => Log.Add("chrome");
            protected override void CheckWorldRebuild(float dt) => Log.Add("rebuild");
            protected override void UpdateStreaming(float dt) => Log.Add("streaming");
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

        // The DocScene idiom plus a RebuildWorldForVisibility spy: BuildWorld/TeardownWorld stay no-ops (so the
        // viewport is never actually built, matching DocScene), but the protected rebuild seam a Layers-panel
        // scatter-layer or Textured-props toggle calls is counted instead of running (which would no-op anyway
        // since _viewport.IsBuilt is false headless). Also spies the InvalidateViewportKitMeshes seam the
        // Textured-props toggle calls before the rebuild (the scatter-layer toggle does not call it), recording
        // both into one ordered Log so a test can assert the textured toggle invalidates BEFORE it rebuilds.
        sealed class RebuildSpyDocScene : MapEditorScene
        {
            readonly Func<MapDocument> _factory;
            public int Rebuilds;
            public int KitMeshInvalidations;
            public readonly List<string> Log = new();
            public RebuildSpyDocScene(Func<MapDocument> factory) => _factory = factory;
            protected override void BuildWorld() { }
            protected override void TeardownWorld() { }
            protected override MapDocument CreateDocument(MapDocRegistry registry) => _factory();
            protected override void RebuildWorldForVisibility() { Rebuilds++; Log.Add("rebuild"); }
            protected override void InvalidateViewportKitMeshes() { KitMeshInvalidations++; Log.Add("invalidate"); }
        }

        // Records which rebuild seam CheckWorldRebuild dispatches to (a bounded region -> partial, a null region ->
        // full) without touching a device: BuildWorld stays a no-op (so the viewport is never built), and both
        // rebuild seams are overridden to log + return a scripted result. RunRebuildCheck exposes the protected step
        // so a test can drive the routing directly on a document it has set up.
        sealed class RebuildDispatchScene : MapEditorScene
        {
            readonly Func<MapDocument> _factory;
            public bool PartialSucceeds = true;
            public readonly List<string> Log = new();
            public RectArea? LastDirty;
            public RebuildDispatchScene(Func<MapDocument> factory) => _factory = factory;
            protected override MapDocument CreateDocument(MapDocRegistry registry) => _factory();
            protected override void BuildWorld() { }
            protected override void TeardownWorld() { }
            protected override bool PartialRebuildWorld(RectArea dirty) { Log.Add("partial"); LastDirty = dirty; return PartialSucceeds; }
            protected override bool RebuildWorld() { Log.Add("full"); return true; }
            public void RunRebuildCheck(float dt = 0f) => CheckWorldRebuild(dt);
        }

        // The RebuildDispatchScene spy idiom PLUS a flat Controller.Field (the FieldDocScene idiom), so a test can
        // both observe which rebuild seam fires AND arm IsDragging / IsDrawing on the real EditorToolController via
        // ordinary EditorFrameInput frames (a press in a draw mode, or a gizmo drag), exercising CheckWorldRebuild's
        // gesture throttle end to end. RunRebuildCheck exposes the protected step with its dt parameter.
        sealed class ThrottleScene : MapEditorScene
        {
            readonly Func<MapDocument> _factory;
            public bool PartialSucceeds = true;
            public readonly List<string> Log = new();
            public ThrottleScene(Func<MapDocument> factory) => _factory = factory;
            protected override MapDocument CreateDocument(MapDocRegistry registry) => _factory();
            protected override void BuildWorld() => Controller.Field =
                new KhaozEngine.Terrain.TerrainField(new KhaozEngine.Terrain.TerrainConfig { GentleAmplitude = 0f });
            protected override void TeardownWorld() { }
            protected override bool PartialRebuildWorld(RectArea dirty) { Log.Add("partial"); return PartialSucceeds; }
            protected override bool RebuildWorld() { Log.Add("full"); return true; }
            public void RunRebuildCheck(float dt) => CheckWorldRebuild(dt);
        }

        // A world-affecting command executes DURING the chrome step, standing in for the PropertyGrid inspector's
        // row setter (UpdateChrome -> UpdateWidgets -> the live UiViewport-driven _inspector.Update, which this
        // headless suite cannot drive without a device, see the UiViewport guard in UpdateWidgets) calling
        // EditorDocument.Execute mid-chrome. Proves the OnUpdate reorder: CheckWorldRebuild now runs AFTER chrome,
        // so this edit's rebuild fires the SAME frame instead of lagging one frame behind.
        sealed class InspectorSameFrameScene : MapEditorScene
        {
            readonly Func<MapDocument> _factory;
            public readonly List<string> Log = new();
            public int FullRebuilds;
            public InspectorSameFrameScene(Func<MapDocument> factory) => _factory = factory;
            protected override MapDocument CreateDocument(MapDocRegistry registry) => _factory();
            protected override void BuildWorld() { }
            protected override void TeardownWorld() { }
            protected override void UpdateCamera(float dt) { }
            protected override void UpdateTools(float dt) { }
            protected override void UpdateChrome(float dt)
            {
                Log.Add("chrome");
                Document.Execute(new EditTerrainCommand(newWaterLevel: 5f, oldWaterLevel: 3f));
            }
            protected override void CheckWorldRebuild(float dt) { Log.Add("rebuild"); base.CheckWorldRebuild(dt); }
            protected override bool RebuildWorld() { FullRebuilds++; return true; }
            protected override void UpdateStreaming(float dt) { }
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

        // A bare scene with no behaviour, pushed BENEATH the editor so a QuitEditor Pop has somewhere to land
        // (Manager.Count == 2), exercising decision 1's "pop when a scene sits beneath it" branch.
        sealed class StubScene : GameScene { }

        // Drives a fixed downward viewport pick every tool step (ignoring Manager.Input), so a test can prove the
        // exit dialog's open gate suppresses the tool step: while the dialog is open OnUpdate never calls UpdateTools,
        // so this pick never runs. The FieldDocScene flat-field idiom otherwise.
        sealed class PickOnToolsScene : MapEditorScene
        {
            readonly Func<MapDocument> _factory;
            public PickOnToolsScene(Func<MapDocument> factory) => _factory = factory;
            protected override MapDocument CreateDocument(MapDocRegistry registry) => _factory();
            protected override void BuildWorld() => Controller.Field =
                new KhaozEngine.Terrain.TerrainField(new KhaozEngine.Terrain.TerrainConfig { GentleAmplitude = 0f });
            protected override void TeardownWorld() { }
            protected override void UpdateTools(float dt) => Controller.Update(
                new EditorFrameInput(new Vector3(0f, 100f, 0f), new Vector3(0f, -1f, 0f),
                    pointerPressed: true, pointerDown: true, dt: dt));
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
        void TapTree(TreeView tree, InputManager input, Vector2 at)
        {
            input.Update(MouseFrame(at, leftDown: false)); tree.Update(input);
            input.Update(MouseFrame(at, leftDown: true)); tree.Update(input);
            input.Update(MouseFrame(at, leftDown: false)); tree.Update(input);
        }

        // A press-origin tap driven through a PropertyGrid (press and release both at `at`), the PropertyGridTests
        // idiom, so a ChoiceRow's dropdown open/pick runs exactly as it does live (block regions included).
        void TapGrid(PropertyGrid grid, InputManager input, Vector2 at)
        {
            input.Update(MouseFrame(at, leftDown: false)); grid.Update(input, 0.016f);
            input.Update(MouseFrame(at, leftDown: true)); grid.Update(input, 0.016f);
            input.Update(MouseFrame(at, leftDown: false)); grid.Update(input, 0.016f);
        }

        // Opens the ChoiceRow at `rowIndex` (tap its trigger) then picks the option at `optionIndex` (tap the open
        // list item, which Dropdown.OptionBounds stacks directly below the trigger at one trigger-height per
        // option). Coordinates are derived from the grid's own RowEditorBounds rather than hand-computed pixel
        // offsets, so a test stays correct regardless of which rows or group headers precede the ChoiceRow.
        void OpenAndPickOption(PropertyGrid grid, InputManager ui, int rowIndex, int optionIndex)
        {
            Rect trigger = grid.RowEditorBounds(rowIndex);
            var triggerCenter = new Vector2(trigger.X + trigger.Width * 0.5f, trigger.Y + trigger.Height * 0.5f);
            TapGrid(grid, ui, triggerCenter);   // open the list
            float optionCenterY = trigger.Bottom + trigger.Height * optionIndex + trigger.Height * 0.5f;
            TapGrid(grid, ui, new Vector2(triggerCenter.X, optionCenterY));   // pick the option
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
        void BeginEditing(FloatRow row)
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

        // Type-scoped lookup (unlike the generic RowByLabel further down): the shape-kind selector used to share
        // the label "Shape" with its own "Shape" group HeaderRow (Task 5 grouping), so a plain label search could
        // return either one depending on row order. Renamed to "Kind" to un-confuse the two (see AddShapeKindRow),
        // kept type-scoped here since only a ChoiceRow is ever the kind selector.
        static ChoiceRow ChoiceRowByLabel(PropertyGrid grid, string label)
        {
            foreach (PropertyRow row in grid.Rows)
                if (row is ChoiceRow c && c.Label.Resolve() == label) return c;
            Assert.Fail($"no ChoiceRow labeled '{label}' (rows: {grid.Rows.Count})");
            return null!;   // unreachable
        }

        // Drive one press-origin tap through a BoolRow's toggle cell (up, press, release), the way the pointer fires
        // a tap. Returns whether the toggle flipped this frame.
        bool TapBool(BoolRow row)
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
        public void Update_StepOrder_CameraThenToolsThenChromeThenRebuildThenStreaming()
        {
            var scene = new OrderScene();
            scene.Init(null!, null!, null!, new MapEditorOptions());
            var m = new SceneManager { Input = InputState.Empty };
            m.Push(scene);
            scene.Log.Clear();

            m.Update(0.016f);

            // Rebuild now runs AFTER chrome (not right after tools), so an inspector-row edit executed inside
            // UpdateChrome (the PropertyGrid poll) rebuilds the same frame instead of lagging one frame behind.
            Assert.Equal(new[] { "camera", "tools", "chrome", "rebuild", "streaming" }, scene.Log);
        }

        // ---- CheckWorldRebuild dispatch: partial vs full -----------------------------------------------

        static MapDocument SampleWithFeatures() => KhaozEngine.Tests.MapDoc.MapDocumentFileTests.SampleDoc();

        // A depth-only clone of a sample lake, so its footprint matches the original's (a bounded dirty region).
        static LakeFeatureDoc LakeDepth(LakeFeatureDoc src, float depth) =>
            new() { CenterX = src.CenterX, CenterZ = src.CenterZ, Radius = src.Radius, Depth = depth,
                InnerFraction = src.InnerFraction, OuterFraction = src.OuterFraction };

        static RebuildDispatchScene PushDispatchScene(bool partialSucceeds = true)
        {
            var scene = new RebuildDispatchScene(SampleWithFeatures) { PartialSucceeds = partialSucceeds };
            scene.Init(null!, null!, null!, new MapEditorOptions());
            new SceneManager().Push(scene);
            scene.Document.AcknowledgeWorldRebuild();   // ignore any pending state from the initial load
            scene.Log.Clear();
            return scene;
        }

        [Fact]
        public void CheckWorldRebuild_NothingPending_CallsNeitherSeam()
        {
            RebuildDispatchScene scene = PushDispatchScene();
            scene.RunRebuildCheck();
            Assert.Empty(scene.Log);
        }

        [Fact]
        public void CheckWorldRebuild_RectRegion_RoutesToPartialAndAcknowledges()
        {
            RebuildDispatchScene scene = PushDispatchScene();
            var lake = (LakeFeatureDoc)scene.Document.Doc.Terrain.Features[0];
            scene.Document.Execute(new EditFeatureCommand(0, LakeDepth(lake, 9f), lake));
            Assert.True(scene.Document.WorldRebuildPending);

            scene.RunRebuildCheck();

            Assert.Equal(new[] { "partial" }, scene.Log);   // a bounded region takes the partial path only
            Assert.NotNull(scene.LastDirty);
            Assert.False(scene.Document.WorldRebuildPending);   // acknowledged after the partial rebuild
        }

        [Fact]
        public void CheckWorldRebuild_NullRegion_RoutesToFullAndAcknowledges()
        {
            RebuildDispatchScene scene = PushDispatchScene();
            scene.Document.Execute(new EditTerrainCommand(newWaterLevel: 5f, oldWaterLevel: 3f));   // whole-world edit
            Assert.True(scene.Document.WorldRebuildPending);
            Assert.Null(scene.Document.PendingRebuildRegion);

            scene.RunRebuildCheck();

            Assert.Equal(new[] { "full" }, scene.Log);   // a null region takes the full rebuild
            Assert.False(scene.Document.WorldRebuildPending);
        }

        [Fact]
        public void CheckWorldRebuild_PartialReportsNotBuilt_FallsBackToFull()
        {
            RebuildDispatchScene scene = PushDispatchScene(partialSucceeds: false);
            var lake = (LakeFeatureDoc)scene.Document.Doc.Terrain.Features[0];
            scene.Document.Execute(new EditFeatureCommand(0, LakeDepth(lake, 9f), lake));

            scene.RunRebuildCheck();

            Assert.Equal(new[] { "partial", "full" }, scene.Log);   // partial declined, full picked it up
            Assert.False(scene.Document.WorldRebuildPending);   // acknowledged after the fallback full rebuild
        }

        // ---- gesture-aware full-rebuild throttle -------------------------------------------------------

        static readonly Vector3 ThrottleDown = new(0f, -1f, 0f);

        static ThrottleScene PushThrottleScene(float gestureRebuildInterval, bool partialSucceeds = true)
        {
            var scene = new ThrottleScene(SampleWithFeatures) { PartialSucceeds = partialSucceeds };
            scene.Init(null!, null!, null!, new MapEditorOptions { GestureRebuildInterval = gestureRebuildInterval });
            new SceneManager().Push(scene);
            scene.Document.AcknowledgeWorldRebuild();   // ignore any pending state from the initial load
            scene.Log.Clear();
            return scene;
        }

        // Presses in DrawExclusion mode over the flat field, which arms EditorToolController.IsDrawing without
        // needing a placement to grab a gizmo handle on. The gesture stays live until EndDrawGesture releases it.
        static void ArmDrawGesture(ThrottleScene scene)
        {
            scene.Controller.Mode = EditorToolMode.DrawExclusion;
            scene.Controller.Update(new EditorFrameInput(new Vector3(0f, 100f, 0f), ThrottleDown,
                pointerPressed: true, pointerDown: true, dt: 0.016f));
            Assert.True(scene.Controller.IsDrawing);
        }

        // Releases over the SAME point as the press, a degenerate (zero-extent) gesture that commits nothing, so
        // ending the gesture never itself perturbs WorldRebuildPending / PendingRebuildRegion.
        static void EndDrawGesture(ThrottleScene scene)
        {
            scene.Controller.Update(new EditorFrameInput(new Vector3(0f, 100f, 0f), ThrottleDown,
                pointerReleased: true, dt: 0.016f));
            Assert.False(scene.Controller.IsDrawing);
        }

        // A whole-world edit (AffectsWorld true, DirtyRegion null): marks WorldRebuildPending with a null (full)
        // region, exactly what an inspector-driven terrain scrub does.
        static void DirtyFull(ThrottleScene scene) =>
            scene.Document.Execute(new EditTerrainCommand(newWaterLevel: 5f, oldWaterLevel: 3f));

        // A bounded-region edit (the sample doc's lake at feature index 0): marks WorldRebuildPending with a rect.
        static void DirtyPartial(ThrottleScene scene)
        {
            var lake = (LakeFeatureDoc)scene.Document.Doc.Terrain.Features[0];
            scene.Document.Execute(new EditFeatureCommand(0, LakeDepth(lake, 9f), lake));
        }

        [Fact]
        public void Throttle_MidGesture_FullRebuildsAtMostOncePerInterval()
        {
            ThrottleScene scene = PushThrottleScene(gestureRebuildInterval: 0.25f);
            ArmDrawGesture(scene);

            // 6 frames of 0.1s (0.6s total) mid-gesture, with a fresh full-dirtying edit each frame (the continuous
            // edit stream a live drag produces). At 0.25s/rebuild that is 2 rebuilds (fired at the 0.3s and 0.6s
            // marks), not 6.
            int fullCount = 0;
            for (int i = 0; i < 6; i++)
            {
                DirtyFull(scene);
                scene.RunRebuildCheck(0.1f);
                fullCount = scene.Log.Count(s => s == "full");
            }

            Assert.Equal(2, fullCount);
        }

        [Fact]
        public void Throttle_SkippedFrame_LeavesWorldRebuildPendingTrue()
        {
            ThrottleScene scene = PushThrottleScene(gestureRebuildInterval: 0.25f);
            ArmDrawGesture(scene);
            DirtyFull(scene);

            scene.RunRebuildCheck(0.1f);   // 0.1s < 0.25s interval: throttled, skipped

            Assert.Empty(scene.Log);
            Assert.True(scene.Document.WorldRebuildPending);   // NOT acknowledged on a skipped frame
        }

        [Fact]
        public void Throttle_FirstCheckAfterGestureEnds_RebuildsImmediately()
        {
            ThrottleScene scene = PushThrottleScene(gestureRebuildInterval: 0.25f);
            ArmDrawGesture(scene);
            DirtyFull(scene);
            scene.RunRebuildCheck(0.1f);   // throttled, skipped (0.1s < 0.25s)
            Assert.Empty(scene.Log);

            EndDrawGesture(scene);
            scene.RunRebuildCheck(0.01f);   // gesture over: the throttle no longer applies at all

            Assert.Equal(new[] { "full" }, scene.Log);
            Assert.False(scene.Document.WorldRebuildPending);
        }

        [Fact]
        public void Throttle_PartialRebuilds_BypassThrottle_AndDoNotFeedFullAccumulator()
        {
            ThrottleScene scene = PushThrottleScene(gestureRebuildInterval: 0.25f);
            ArmDrawGesture(scene);

            // Every one of these 5 frames carries enough dt (0.2s each) that, if a partial check wrongly fed the
            // full-rebuild accumulator, the very next full check below would fire immediately instead of needing
            // its own 0.25s of accumulation.
            for (int i = 0; i < 5; i++)
            {
                DirtyPartial(scene);
                scene.RunRebuildCheck(0.2f);
            }
            Assert.Equal(5, scene.Log.Count(s => s == "partial"));
            Assert.DoesNotContain("full", scene.Log);

            DirtyFull(scene);
            scene.RunRebuildCheck(0.1f);   // 0.1s < 0.25s: still throttled, proving the partial frames above never
                                            // advanced the full-rebuild timer.
            Assert.DoesNotContain("full", scene.Log);
        }

        [Fact]
        public void Throttle_IntervalZero_RebuildsEveryFrame()
        {
            ThrottleScene scene = PushThrottleScene(gestureRebuildInterval: 0f);
            ArmDrawGesture(scene);

            for (int i = 0; i < 4; i++)
            {
                DirtyFull(scene);
                scene.RunRebuildCheck(0f);
            }

            Assert.Equal(4, scene.Log.Count(s => s == "full"));
        }

        // ---- exclusion gizmo drag: partial rebuild, never throttled (the choppy-drag fix) --------------

        // A minimal document holding one disc exclusion at the origin, so a gizmo drag on it reuses the exact
        // press/drag geometry EditorToolTests.ShapeDrag_MovesCenterThroughCommand already verifies (a +X arrow
        // grab at (0.6, 100, 0) on a DiscShapeDoc CenterX=0 CenterZ=0 Radius=5).
        static MapDocument SampleWithExclusion()
        {
            var doc = new MapDocument { Id = "exclusion-throttle", Bounds = new MapBounds { MinX = -100f, MinZ = -100f, MaxX = 100f, MaxZ = 100f } };
            doc.Exclusions.Add(new MapExclusion { Shape = new DiscShapeDoc { CenterX = 0f, CenterZ = 0f, Radius = 5f } });
            return doc;
        }

        [Fact]
        public void ExclusionGizmoDrag_RoutesToPartial_NeverThrottled()
        {
            // EditExclusionShapeCommand now reports a bounded DirtyRegion (ShapeGeometry.TryBounds), so a gizmo
            // drag on a selected exclusion takes the PARTIAL rebuild seam every frame, and CheckWorldRebuild's
            // gesture throttle only ever wraps the FULL path, never partial. Mirrors
            // Throttle_PartialRebuilds_BypassThrottle_AndDoNotFeedFullAccumulator above but drives it through a
            // REAL selection + gizmo drag instead of the synthetic DirtyPartial helper, proving the fix through
            // the actual editing gesture a map author performs, not just the command's own DirtyRegion getter.
            var scene = new ThrottleScene(SampleWithExclusion) { PartialSucceeds = true };
            scene.Init(null!, null!, null!, new MapEditorOptions { GestureRebuildInterval = 0.25f });
            new SceneManager().Push(scene);
            scene.Document.AcknowledgeWorldRebuild();   // ignore any pending state from the initial load
            scene.Log.Clear();

            scene.Document.Selection.Set(SelectionKind.Exclusion, "0");

            // Grab the +X translate arrow on the shape-center gizmo.
            scene.Controller.Update(new EditorFrameInput(new Vector3(0.6f, 100f, 0f), ThrottleDown,
                pointerPressed: true, pointerDown: true, dt: 0.016f));
            Assert.True(scene.Controller.IsDragging);

            // 5 drag frames, each carrying only 0.1s (well under the 0.25s gesture-rebuild interval): a
            // full-rebuild-throttled path would stay silent for several of these, but the partial path never is.
            for (int i = 0; i < 5; i++)
            {
                scene.Controller.Update(new EditorFrameInput(new Vector3(1.6f + i, 100f, 0f), ThrottleDown, pointerDown: true, dt: 0.016f));
                scene.RunRebuildCheck(0.1f);
            }

            Assert.Equal(5, scene.Log.Count(s => s == "partial"));
            Assert.DoesNotContain("full", scene.Log);
            Assert.False(scene.Document.WorldRebuildPending);   // each partial rebuild acknowledges immediately

            // Releasing seals the gesture into one coalesced undo step (drag coalescing), and the shape actually
            // moved: this is a real edit, not just a rebuild-routing no-op.
            scene.Controller.Update(new EditorFrameInput(new Vector3(5.6f, 100f, 0f), ThrottleDown, pointerReleased: true, dt: 0.016f));
            Assert.False(scene.Controller.IsDragging);
            Assert.Equal(1, scene.Document.History.UndoDepth);
            var disc = Assert.IsType<DiscShapeDoc>(scene.Document.Doc.Exclusions[0].Shape);
            Assert.True(disc.CenterX > 0f);
        }

        // ---- same-frame inspector rebuild regression ---------------------------------------------------

        [Fact]
        public void Update_WorldAffectingChromeEdit_RebuildsSameFrame()
        {
            var scene = new InspectorSameFrameScene(SampleWithFeatures);
            scene.Init(null!, null!, null!, new MapEditorOptions());
            var m = new SceneManager { Input = InputState.Empty };
            m.Push(scene);
            scene.Document.AcknowledgeWorldRebuild();   // ignore any pending state from the initial load
            scene.Log.Clear();

            m.Update(0.016f);

            Assert.Equal(new[] { "chrome", "rebuild" }, scene.Log);
            Assert.Equal(1, scene.FullRebuilds);                 // the rebuild seam actually ran
            Assert.False(scene.Document.WorldRebuildPending);    // acknowledged the SAME frame, no one-frame lag
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

        // ---- Shift+Escape exit dialog (decisions 1 + 3) ----------------------------------------------------
        // The _exitArmed double-press flow was DELETED. Shift+Escape now opens a scene-owned PopupPanel modal.
        // These pins REPLACE the three old _exitArmed tests (ShiftEscape_CleanDocument_Pops,
        // ShiftEscape_DirtyDocument_ArmsThenPops, ShiftEscape_ArmedThenSave_Disarms). The scene is pushed onto a
        // REAL SceneManager (the file's standard idiom): a Manager.Pop() queued during Update applies at the end of
        // the pass, and RequestQuit (decision 1) fires only when the editor is the bottom scene (Count == 1).

        [Fact]
        public void ShiftEsc_OpensExitDialog_DirtyShowsFourActions()
        {
            var scene = new DocScene(() => ValidDoc());
            scene.Init(null!, null!, null!, new MapEditorOptions());
            var m = new SceneManager();
            m.Push(scene);
            scene.Document.Execute(new AddSpawnCommand(NewSpawn("s1")));   // dirty
            Assert.True(scene.Document.IsDirty);

            m.Input = KeyFrame(shiftDown: true, Key.Escape);
            m.Update(0.016f);

            Assert.NotNull(scene.ExitDialog);
            Assert.Equal(1, m.Count);   // opening the dialog never pops
            var actions = scene.ExitDialog!.FooterButtons;
            Assert.Equal(4, actions.Count);
            Assert.Equal("Save and Close", actions[0].Label.Resolve());   // index 0 = default (Enter, green)
            Assert.Equal("Save", actions[1].Label.Resolve());
            Assert.Equal("Discard", actions[2].Label.Resolve());
            Assert.Equal("Cancel", actions[3].Label.Resolve());           // last = Esc target (CancelIndex default)
        }

        [Fact]
        public void ExitDialog_CleanShowsCloseCancelOnly()
        {
            var scene = new DocScene(() => ValidDoc());
            scene.Init(null!, null!, null!, new MapEditorOptions());
            var m = new SceneManager();
            m.Push(scene);
            Assert.False(scene.Document.IsDirty);

            m.Input = KeyFrame(shiftDown: true, Key.Escape);
            m.Update(0.016f);

            Assert.NotNull(scene.ExitDialog);
            var actions = scene.ExitDialog!.FooterButtons;
            Assert.Equal(2, actions.Count);
            Assert.Equal("Close", actions[0].Label.Resolve());
            Assert.Equal("Cancel", actions[1].Label.Resolve());
        }

        [Fact]
        public void ExitDialog_SaveAndClose_SavesThenQuits()
        {
            string path = TempPath();
            bool quit = false;
            var scene = new DocScene(() => ValidDoc());
            scene.Init(null!, null!, null!, new MapEditorOptions { DocumentPath = path, RequestQuit = () => quit = true });
            var m = new SceneManager();
            try
            {
                m.Push(scene);
                scene.Document.Execute(new AddSpawnCommand(NewSpawn("s1")));   // dirty
                Assert.True(scene.Document.IsDirty);

                m.Input = KeyFrame(shiftDown: true, Key.Escape);   // open the dirty dialog
                m.Update(0.016f);
                Assert.NotNull(scene.ExitDialog);

                m.Input = KeyFrame(shiftDown: false, Key.Enter);   // Enter = index 0 = Save and Close
                m.Update(0.016f);

                Assert.True(File.Exists(path));         // saved
                Assert.False(scene.Document.IsDirty);   // MarkSaved cleared dirty
                Assert.True(quit);                      // quit fired (Count == 1, RequestQuit set)
                Assert.Null(scene.ExitDialog);          // dialog dismissed
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Fact]
        public void ExitDialog_SaveFailure_AbortsClose()
        {
            bool quit = false;
            var scene = new DocScene(() => ValidDoc());
            // No DocumentPath set: SaveDocument fails ("No document path set"), so Save and Close must abort.
            scene.Init(null!, null!, null!, new MapEditorOptions { RequestQuit = () => quit = true });
            var m = new SceneManager();
            m.Push(scene);
            scene.Document.Execute(new AddSpawnCommand(NewSpawn("s1")));   // dirty
            Assert.True(scene.Document.IsDirty);

            m.Input = KeyFrame(shiftDown: true, Key.Escape);   // open the dirty dialog
            m.Update(0.016f);
            Assert.NotNull(scene.ExitDialog);

            m.Input = KeyFrame(shiftDown: false, Key.Enter);   // Enter = Save and Close, but the save fails
            m.Update(0.016f);

            Assert.NotNull(scene.ExitDialog);          // dialog stays open on save failure
            Assert.False(quit);                        // never quit
            Assert.True(scene.Document.IsDirty);       // still unsaved
            Assert.Contains("path", scene.StatusText, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ExitDialog_CloseWithoutSaving_QuitsDirty()
        {
            string path = TempPath();
            bool quit = false;
            var scene = new DocScene(() => ValidDoc());
            scene.Init(null!, null!, null!, new MapEditorOptions { DocumentPath = path, RequestQuit = () => quit = true });
            var m = new SceneManager();
            try
            {
                m.Push(scene);
                scene.Document.Execute(new AddSpawnCommand(NewSpawn("s1")));   // dirty
                Assert.True(scene.Document.IsDirty);

                m.Input = KeyFrame(shiftDown: true, Key.Escape);   // open the dirty dialog
                m.Update(0.016f);
                Assert.NotNull(scene.ExitDialog);

                scene.ExitDialog!.FooterButtons[2].OnClick!.Invoke();   // Discard (index 2), the real button callback

                Assert.True(quit);                    // quit without saving
                Assert.True(scene.Document.IsDirty);  // never saved
                Assert.False(File.Exists(path));      // nothing written
                Assert.Null(scene.ExitDialog);        // dismissed
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Fact]
        public void ExitDialog_EscCancels_EditorInputBlockedWhileOpen()
        {
            var scene = new PickOnToolsScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Placements.Add(new MapPlacement { Id = "hut", Kind = "prop", X = 0f, Z = 0f, Y = null });
                return doc;
            });
            scene.Init(null!, null!, null!, new MapEditorOptions());
            var m = new SceneManager();
            m.Push(scene);

            scene.Document.Execute(new AddSpawnCommand(NewSpawn("s1")));   // something undoable on the stack
            Assert.True(scene.Document.History.CanUndo);

            m.Input = KeyFrame(shiftDown: true, Key.Escape);   // open the dialog (this frame's pick is reset below)
            m.Update(0.016f);
            Assert.NotNull(scene.ExitDialog);
            scene.Document.Selection.Clear();                  // reset the open-frame pick

            // While the dialog is open: a Cmd+Z chord does not undo, and the tool step never runs (no pick).
            m.Input = SuperKeyFrame(Key.Z);
            m.Update(0.016f);
            Assert.True(scene.Document.History.CanUndo);                       // chord suppressed
            Assert.Equal(SelectionKind.None, scene.Document.Selection.Kind);   // tool step suppressed

            // Esc dismisses the dialog (Cancel), and the Esc frame does nothing else to the editor.
            m.Input = KeyFrame(shiftDown: false, Key.Escape);
            m.Update(0.016f);
            Assert.Null(scene.ExitDialog);

            // Input flows again: the tool step picks the hut and a Cmd+Z now undoes.
            m.Input = SuperKeyFrame(Key.Z);
            m.Update(0.016f);
            Assert.Equal(SelectionKind.Placement, scene.Document.Selection.Kind);   // picked
            Assert.Empty(scene.Document.Doc.Spawns);                                // undid s1
        }

        [Fact]
        public void QuitEditor_PopsWhenSceneBeneath_RequestQuitWhenBottom()
        {
            // Bottom scene (Count == 1) + RequestQuit set: Close invokes RequestQuit, never pops.
            {
                bool quit = false;
                var scene = new DocScene(() => ValidDoc());
                scene.Init(null!, null!, null!, new MapEditorOptions { RequestQuit = () => quit = true });
                var m = new SceneManager();
                m.Push(scene);

                m.Input = KeyFrame(shiftDown: true, Key.Escape);   // open the clean dialog
                m.Update(0.016f);
                m.Input = KeyFrame(shiftDown: false, Key.Enter);   // Enter = index 0 = Close
                m.Update(0.016f);

                Assert.True(quit);          // RequestQuit fired
                Assert.Equal(1, m.Count);   // and the scene was NOT popped
            }

            // A scene sits beneath the editor (Count == 2): Close pops back to it even with RequestQuit set.
            {
                bool quit = false;
                var scene = new DocScene(() => ValidDoc());
                scene.Init(null!, null!, null!, new MapEditorOptions { RequestQuit = () => quit = true });
                var m = new SceneManager();
                m.Push(new StubScene());
                m.Push(scene);
                Assert.Equal(2, m.Count);

                m.Input = KeyFrame(shiftDown: true, Key.Escape);   // open the clean dialog
                m.Update(0.016f);
                m.Input = KeyFrame(shiftDown: false, Key.Enter);   // Enter = index 0 = Close
                m.Update(0.016f);

                Assert.False(quit);         // RequestQuit not fired: a scene sits beneath
                Assert.Equal(1, m.Count);   // popped back to the scene beneath
            }
        }

        // ---- toolbar save button + camera suppression (decisions 4 + 5) -----------------------------------

        [Fact]
        public void SaveButton_InToolbar_ClickSaves_LabelTracksDirty()
        {
            string path = TempPath();
            var scene = new DocScene(() => ValidDoc());
            scene.Init(null!, null!, null!, new MapEditorOptions { DocumentPath = path });
            var m = new SceneManager();
            try
            {
                m.Push(scene);

                m.Input = InputState.Empty;
                m.Update(0.016f);                                   // syncs the label headless (outside the UiViewport guard)
                Assert.Equal("Save", scene.SaveButton.Resolved);   // clean

                scene.Document.Execute(new AddSpawnCommand(NewSpawn("s1")));   // dirty
                m.Update(0.016f);
                Assert.Equal("Save*", scene.SaveButton.Resolved);

                // A press-origin tap on the button fires SaveDocument (its OnClick).
                var ui = new InputManager();
                scene.SaveButton.Bounds = new Rect(0f, 0f, 96f, 28f);
                var at = new Vector2(48f, 14f);
                ui.Update(MouseFrame(at, leftDown: false)); scene.SaveButton.Update(ui.Pointer);
                ui.Update(MouseFrame(at, leftDown: true)); scene.SaveButton.Update(ui.Pointer);
                ui.Update(MouseFrame(at, leftDown: false)); scene.SaveButton.Update(ui.Pointer);

                Assert.True(File.Exists(path));
                Assert.False(scene.Document.IsDirty);

                m.Update(0.016f);   // label re-syncs to clean after the save
                Assert.Equal("Save", scene.SaveButton.Resolved);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Fact]
        public void CommandHeld_SuppressesCameraMovement()
        {
            var scene = new SpyScene();   // real UpdateCamera, device work skipped
            scene.Init(null!, null!, null!, new MapEditorOptions());
            var m = new SceneManager();
            m.Push(scene);

            Vector3 before = scene.Camera.Position;
            m.Input = SuperKeyFrame(Key.D);   // Cmd+D held: without the guard the fly camera nudges +right one frame
            m.Update(0.016f);

            Assert.Equal(before, scene.Camera.Position);   // decision 5: the camera step is skipped while a command modifier is down
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
        void DragTreeRow(TreeView tree, InputManager input, int fromRow, int toRow, bool afterTarget)
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

        // A biome band's order is meaningless (the blend is order-independent), so a band row must not even arm a
        // drag: the outline's CanReorder gate blocks it up front (no phantom insertion line), while a feature row
        // still arms. Drives a real drag on a band row and asserts the document and undo stack are untouched, then
        // pins the predicate directly on a band node (blocked) versus a feature node (allowed).
        [Fact]
        public void OutlineDrag_OnBiomeBand_DoesNotArm_FeatureStillDoes()
        {
            var band0 = new MapBiomeBand { Start = null, End = 20f, Biome = KhaozEngine.Terrain.BiomeId.Meadow };
            var band1 = new MapBiomeBand { Start = 20f, End = null, Biome = KhaozEngine.Terrain.BiomeId.Forest };
            var lake = new LakeFeatureDoc { CenterX = 0f, CenterZ = 0f, Radius = 5f, Depth = 2f };
            var scene = new DocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Terrain.Biomes.Add(band0);
                doc.Terrain.Biomes.Add(band1);
                doc.Terrain.Features.Add(lake);
                return doc;
            });
            scene.Init(null!, null!, null!, new MapEditorOptions());
            new SceneManager().Push(scene);

            TreeView outline = scene.Outline;
            outline.Bounds = new Rect(0f, 0f, 240f, 600f);   // tall enough for every outline row
            TreeNode b0 = CategoryChild(outline, "Biomes", 0);
            TreeNode b1 = CategoryChild(outline, "Biomes", 1);

            var input = new InputManager();
            DragTreeRow(outline, input, RowOf(outline, b0), RowOf(outline, b1), afterTarget: true);

            // Nothing armed, so nothing committed: the band order and the undo stack are untouched.
            Assert.Same(band0, scene.Document.Doc.Terrain.Biomes[0]);
            Assert.Same(band1, scene.Document.Doc.Terrain.Biomes[1]);
            Assert.False(scene.Document.History.CanUndo);
            Assert.False(scene.Document.WorldRebuildPending);

            // The predicate itself: a band node is not reorderable, a feature node is.
            Assert.NotNull(outline.CanReorder);
            Assert.False(outline.CanReorder!(b0));
            TreeNode f0 = CategoryChild(outline, "Features", 0);
            Assert.True(outline.CanReorder!(f0));
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
        public void PaletteFilter_UnfocusesOnModeSwapAwayFromPlaceMode()
        {
            // Before this fix, a filter focused in the mode that shows it kept IsFocused stuck true forever once
            // the mode swapped away and its panel hid: TextInput.Unfocus only ever ran inside the filter's own
            // (mode-gated) Update call, which UpdateWidgets stops driving the instant KitPaletteVisible goes
            // false. AnyEditorFocused's own mode gate papered over the symptom (a hidden filter's stale focus no
            // longer blocked shortcuts in a DIFFERENT mode) without clearing the stuck bit itself.
            var scene = new DocScene(() => ValidDoc());
            scene.Init(null!, null!, null!, new MapEditorOptions());
            var m = new SceneManager { Input = InputState.Empty };
            m.Push(scene);

            scene.Controller.Mode = EditorToolMode.PlacePlacement;   // the mode that shows the kit palette filter
            m.Update(0.016f);   // establishes PlacePlacement as the chrome step's last-seen mode

            scene.PaletteFilter.Focus();
            Assert.True(scene.PaletteFilter.IsFocused);   // precondition: the filter owns focus

            scene.Controller.Mode = EditorToolMode.Select;   // swap away: the kit palette (and its filter) hides
            m.Update(0.016f);

            Assert.False(scene.PaletteFilter.IsFocused);
        }

        [Fact]
        public void SpawnFilter_UnfocusesOnModeSwapAwayFromSpawnMode()
        {
            // Same stuck-focus bug as PaletteFilter_UnfocusesOnModeSwapAwayFromPlaceMode, for the spawn filter.
            var scene = new DocScene(() => ValidDoc());
            scene.Init(null!, null!, null!, new MapEditorOptions());
            var m = new SceneManager { Input = InputState.Empty };
            m.Push(scene);

            scene.Controller.Mode = EditorToolMode.PlaceSpawn;   // the mode that shows the spawn filter
            m.Update(0.016f);   // establishes PlaceSpawn as the chrome step's last-seen mode

            scene.SpawnFilter.Focus();
            Assert.True(scene.SpawnFilter.IsFocused);   // precondition: the filter owns focus

            scene.Controller.Mode = EditorToolMode.Select;   // swap away: the spawn list (and its filter) hides
            m.Update(0.016f);

            Assert.False(scene.SpawnFilter.IsFocused);
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

        // Every terrain FloatRow's GestureEnded is wired (via AddFloatRow) to Document.SealGesture, so scrubbing
        // WaterLevel then BiomeBlend seals a barrier between them: EditTerrainCommand.TryMerge would otherwise
        // coalesce ANY two terrain edits into one undo step (that command-level merge is still correct WITHIN one
        // gesture - see EditorCommandsTests.EditTerrain_Merge_UnionOfDifferentFields_EachOldFromFirstSetter), but
        // two DIFFERENT inspector gestures back to back must land as two separate undo steps.
        [Fact]
        public void TerrainNode_ScrubTwoFields_SealsTwoSeparateUndoSteps()
        {
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Terrain.WaterLevel = 0f;
                doc.Terrain.BiomeBlend = 5f;
                return doc;
            });

            scene.Document.Selection.Set(SelectionKind.Terrain, "");
            FloatRow water = FloatRowByLabel(scene.Inspector, "WaterLevel");
            FloatRow blend = FloatRowByLabel(scene.Inspector, "BiomeBlend");
            var cell = new Rect(0f, 0f, 200f, 28f);

            // Scrub WaterLevel: press, drag (a real change lands the first undo step), release (seals the gesture).
            var uiWater = new InputManager();
            uiWater.Update(MouseFrame(new Vector2(100f, 10f), leftDown: false));
            water.Update(cell, uiWater, 0.016f);
            uiWater.Update(MouseFrame(new Vector2(100f, 10f), leftDown: true));
            water.Update(cell, uiWater, 0.016f);
            uiWater.Update(MouseFrame(new Vector2(200f, 10f), leftDown: true));
            water.Update(cell, uiWater, 0.016f);
            uiWater.Update(MouseFrame(new Vector2(200f, 10f), leftDown: false));
            water.Update(cell, uiWater, 0.016f);   // release: GestureEnded fires, seals the barrier

            Assert.Equal(1, scene.Document.History.UndoDepth);

            // Scrub BiomeBlend next: without the seal this would coalesce into the water step via
            // EditTerrainCommand.TryMerge (any two terrain edits merge within one gesture). The seal makes it a
            // second, separate step instead.
            var uiBlend = new InputManager();
            uiBlend.Update(MouseFrame(new Vector2(100f, 10f), leftDown: false));
            blend.Update(cell, uiBlend, 0.016f);
            uiBlend.Update(MouseFrame(new Vector2(100f, 10f), leftDown: true));
            blend.Update(cell, uiBlend, 0.016f);
            uiBlend.Update(MouseFrame(new Vector2(200f, 10f), leftDown: true));
            blend.Update(cell, uiBlend, 0.016f);

            Assert.Equal(2, scene.Document.History.UndoDepth);

            // Both edits are independently undoable: undoing twice restores both fields to their pre-scrub values.
            Assert.True(scene.Document.Undo());
            Near(5f, scene.Document.Doc.Terrain.BiomeBlend);
            Assert.True(scene.Document.Undo());
            Near(0f, scene.Document.Doc.Terrain.WaterLevel);
            Assert.False(scene.Document.History.CanUndo);
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

            // Selecting the band node builds its editable inspector (a "Range" group header, a read-only "Affects"
            // explainer, then the Biome choice + scalar rows, Task 5 grouping).
            scene.Document.Selection.Set(SelectionKind.BiomeBand, "0");
            Assert.IsType<HeaderRow>(scene.Inspector.Rows[0]);
            Assert.IsType<ReadOnlyRow>(scene.Inspector.Rows[1]);
            Assert.Equal("Affects", scene.Inspector.Rows[1].Label.Resolve());
            Assert.IsType<ChoiceRow>(scene.Inspector.Rows[2]);
            Assert.Equal("Meadow", ((ChoiceRow)scene.Inspector.Rows[2]).Selected);
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

            // A disc exclusion gets the editable shape surface: the "Kind" selector (under the "Shape" group
            // header, Task 5 grouping, renamed off "Shape" so it does not repeat the header) plus one FloatRow
            // per param.
            Assert.NotNull(ChoiceRowByLabel(scene.Inspector, "Kind"));
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
            int shapeRow = scene.Inspector.Rows.IndexOf(ChoiceRowByLabel(scene.Inspector, "Kind"));
            Assert.IsType<ChoiceRow>(scene.Inspector.Rows[shapeRow]);

            // The Kind row now sits after the "Identity" group header (Task 5 grouping put a header ahead of
            // it), so its editor cell is looked up by index rather than assumed at Rows[0] / y 0..28. With two
            // options the open list stacks directly below the trigger, one trigger-height row per option. Tap the
            // trigger, then pick "rect".
            var ui = new InputManager();
            OpenAndPickOption(scene.Inspector, ui, shapeRow, optionIndex: 1);   // "rect" is option 1

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
            shapeRow = scene.Inspector.Rows.IndexOf(ChoiceRowByLabel(scene.Inspector, "Kind"));
            OpenAndPickOption(scene.Inspector, ui, shapeRow, optionIndex: 0);   // "disc" is option 0
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
            // Rows[0] is the "Identity" group header (Task 5 grouping). The rename row (bound through
            // RenameRegionCommand) leads the rows underneath it.
            Assert.IsType<HeaderRow>(scene.Inspector.Rows[0]);
            Assert.IsType<TextRow>(scene.Inspector.Rows[1]);
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
            var row = Assert.IsType<TextRow>(scene.Inspector.Rows[1]);   // Rows[0] is the "Identity" group header

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
            // Rows[0] is the "Identity" group header (Task 5 grouping). The Name rename row is Rows[1].
            var row = Assert.IsType<TextRow>(scene.Inspector.Rows[1]);

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
            var row2 = Assert.IsType<TextRow>(scene.Inspector.Rows[1]);   // Rows[0] is the "Identity" group header
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
            // Rows[0] is the "Identity" group header (Task 5 grouping). The Name rename row is Rows[1].
            var row = Assert.IsType<TextRow>(scene.Inspector.Rows[1]);

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

        [Fact]
        public void MidRename_OutlineHighlightPersists()
        {
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Placements.Add(new MapPlacement { Id = "hut", Kind = "prop" });
                return doc;
            });

            scene.Document.Selection.Set(SelectionKind.Placement, "hut");
            // Rows[0] is the "Identity" group header (Task 5 grouping). The Name rename row is Rows[1].
            var row = Assert.IsType<TextRow>(scene.Inspector.Rows[1]);

            // Type a single character into the rename row: focus the field, replace its buffer with the OLD key
            // plus one appended character, then run one row Update so the TextChanged write-through fires the
            // setter (RenamePlacementCommand). That rebuilds the outline via OnDocumentChanged while the row is
            // still focused, i.e. before the deferred re-select (_pendingSelectId, see UpdateChrome) ever fires,
            // which is exactly the frame the outline highlight used to drop on.
            var ui = new InputManager();
            ui.Update(InputState.Empty);
            row.Input.IsFocused = true;
            row.Input.SetText("huts");
            row.Update(new Rect(0f, 0f, 200f, 28f), ui, 0.016f);

            Assert.Equal("huts", scene.Document.Doc.Placements[0].Id);
            Assert.True(row.Input.IsFocused);   // still typing: the deferred re-select has NOT fired yet

            TreeNode? selected = scene.Outline.Selected;
            Assert.NotNull(selected);
            Assert.Equal(new MapEditorScene.OutlineRef(SelectionKind.Placement, "huts"), selected!.Tag);
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

        // ---- outline selection sync -----------------------------------------------------------------------

        [Fact]
        public void ViewportPick_HighlightsAndScrollsOutline()
        {
            var scene = new FieldDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Placements.Add(new MapPlacement { Id = "hut", Kind = "prop", X = 0f, Z = 0f, Y = null });
                return doc;
            });
            scene.Init(null!, null!, null!, new MapEditorOptions());
            new SceneManager().Push(scene);

            TreeView outline = scene.Outline;
            outline.Bounds = new Rect(0f, 0f, 240f, 100f);   // only a handful of rows fit
            outline.ScrollOffset = 500f;                     // scrolled well past the content

            var down = new Vector3(0f, -1f, 0f);
            EditorFrameInput Press() => new EditorFrameInput(new Vector3(0f, 100f, 0f), down,
                pointerPressed: true, pointerDown: true, dt: 0.016f);
            scene.Controller.Update(Press());

            Assert.Equal(SelectionKind.Placement, scene.Document.Selection.Kind);
            Assert.Equal("hut", scene.Document.Selection.Id);

            TreeNode? selected = outline.Selected;
            Assert.NotNull(selected);
            Assert.Equal(new MapEditorScene.OutlineRef(SelectionKind.Placement, "hut"), selected!.Tag);
            Assert.True(outline.ScrollOffset < 500f, "the pick should scroll the newly selected row back into view");
        }

        [Fact]
        public void EditRebuild_ReselectsOutlineNode()
        {
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Placements.Add(new MapPlacement { Id = "hut", Kind = "prop", X = 0f, Z = 0f });
                return doc;
            });

            scene.Document.Selection.Set(SelectionKind.Placement, "hut");
            TreeNode? before = scene.Outline.Selected;
            Assert.NotNull(before);
            Assert.Equal(new MapEditorScene.OutlineRef(SelectionKind.Placement, "hut"), before!.Tag);

            // Any document command through the normal Execute path rebuilds the outline (RebuildOutline news up
            // every TreeNode), which used to orphan the highlight. The selection itself (kind/id) is unchanged.
            scene.Document.Execute(new MovePlacementCommand("hut", 5f, 5f, null));

            TreeNode? after = scene.Outline.Selected;
            Assert.NotNull(after);
            Assert.NotSame(before, after);   // proves the node really was replaced by the rebuild, not a survivor
            Assert.Equal(new MapEditorScene.OutlineRef(SelectionKind.Placement, "hut"), after!.Tag);
        }

        [Fact]
        public void SelectionClear_ClearsOutline()
        {
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Placements.Add(new MapPlacement { Id = "hut", Kind = "prop", X = 0f, Z = 0f });
                return doc;
            });

            scene.Document.Selection.Set(SelectionKind.Placement, "hut");
            Assert.NotNull(scene.Outline.Selected);

            scene.Document.Selection.Clear();
            Assert.Null(scene.Outline.Selected);
        }

        [Fact]
        public void OutlineTap_StillWorks_Idempotent()
        {
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Placements.Add(new MapPlacement { Id = "hut", Kind = "prop", X = 0f, Z = 0f });
                return doc;
            });

            TreeView outline = scene.Outline;
            outline.Bounds = new Rect(0f, 0f, 240f, 400f);
            TreeNode node = CategoryChild(outline, "Placements", 0);

            var input = new InputManager();
            TapTree(outline, input, RowCenter(outline, node));

            Assert.Equal(SelectionKind.Placement, scene.Document.Selection.Kind);
            Assert.Equal("hut", scene.Document.Selection.Id);
            Assert.Same(node, outline.Selected);   // the resync did not swap in a different node instance
            float scrollAfterTap = outline.ScrollOffset;

            // A few quiet frames afterward: the outline-originated resync must be idempotent, no feedback loop
            // bouncing the highlight or re-scrolling an already-visible row.
            for (int i = 0; i < 3; i++)
            {
                scene.OnUpdate(0.016f);
                Assert.Same(node, outline.Selected);
                Assert.Equal(scrollAfterTap, outline.ScrollOffset);
            }
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

            // Nothing is selected at startup, so the inspector is the Layers panel: one BoolRow per visibility group,
            // one Rendering toggle (Textured props), then one per named scatter layer.
            Assert.Equal(SelectionKind.None, scene.Document.Selection.Kind);
            List<BoolRow> bools = scene.Inspector.Rows.OfType<BoolRow>().ToList();
            List<string> labels = bools.Select(b => b.Label.Resolve()).ToList();

            Assert.Equal(8 + 1 + 2, bools.Count);   // eight groups + Textured props + two scatter layers
            Assert.Contains("Placements", labels);
            Assert.Contains("Spawns", labels);
            Assert.Contains("Water", labels);
            Assert.Contains("Exclusions", labels);
            Assert.Contains("Scatter overrides", labels);
            Assert.Contains("Regions", labels);
            Assert.Contains("Feature markers", labels);
            Assert.Contains("Player spawns", labels);
            Assert.Contains("Textured props", labels);
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

        // ---- Layers panel: Textured props toggle ---------------------------------------------------------

        static RebuildSpyDocScene PushRebuildSpyDocScene(Func<MapDocument> factory, MapEditorOptions options)
        {
            var scene = new RebuildSpyDocScene(factory);
            scene.Init(null!, null!, null!, options);
            new SceneManager().Push(scene);
            return scene;
        }

        [Fact]
        public void TexturedRow_InLayersPanel_HasDescription()
        {
            var scene = PushDocScene(ValidDoc);
            scene.Document.Selection.Clear();

            BoolRow textured = BoolRowByLabel(scene.Inspector, "Textured props");

            Assert.True(textured.Description.HasValue);
            Assert.False(string.IsNullOrWhiteSpace(textured.Description!.Value.Resolve()));
        }

        [Fact]
        public void TexturedToggle_Flip_TriggersRebuild()
        {
            var options = new MapEditorOptions();
            var scene = PushRebuildSpyDocScene(ValidDoc, options);
            scene.Document.Selection.Clear();

            BoolRow textured = BoolRowByLabel(scene.Inspector, "Textured props");
            Assert.True(options.TexturedProps);      // default true, matching gameplay
            Assert.Equal(0, scene.Rebuilds);

            // Flip off: the option updates and the Layers-panel toggle rebuilds the streamed world, mirroring how
            // a scatter-layer visibility toggle rebuilds (RebuildWorldForVisibility).
            Assert.True(TapBool(textured));
            Assert.False(options.TexturedProps);
            Assert.Equal(1, scene.Rebuilds);

            // Flip back on: rebuilds again.
            Assert.True(TapBool(textured));
            Assert.True(options.TexturedProps);
            Assert.Equal(2, scene.Rebuilds);
        }

        [Fact]
        public void TexturedToggle_Flip_InvalidatesKitMeshes_ThenRebuilds()
        {
            // The textured/flattened form a kit id loads is keyed on the toggle at load time (ViewportWorld's mesh
            // cache is keyed on entry id alone, not on which form was loaded), so the toggle must invalidate the
            // retained cache BEFORE it rebuilds, or the rebuild would serve the stale cached form.
            var options = new MapEditorOptions();
            var scene = PushRebuildSpyDocScene(ValidDoc, options);
            scene.Document.Selection.Clear();

            BoolRow textured = BoolRowByLabel(scene.Inspector, "Textured props");
            Assert.True(TapBool(textured));

            Assert.Equal(1, scene.KitMeshInvalidations);
            Assert.Equal(1, scene.Rebuilds);
            Assert.Equal(new[] { "invalidate", "rebuild" }, scene.Log);
        }

        [Fact]
        public void ScatterLayerVisibility_Flip_RebuildsWithoutInvalidatingKitMeshes()
        {
            // A scatter-layer visibility toggle only changes WHICH layers stream, never which mesh form an id
            // loads, so it must rebuild without touching the retained kit-mesh cache.
            var options = new MapEditorOptions();
            var scene = PushRebuildSpyDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.ScatterLayers.Add(new MapScatterLayer { Name = "trees" });
                return doc;
            }, options);
            scene.Document.Selection.Clear();

            BoolRow layerRow = BoolRowByLabel(scene.Inspector, "trees");
            Assert.True(TapBool(layerRow));

            Assert.Equal(0, scene.KitMeshInvalidations);
            Assert.Equal(1, scene.Rebuilds);
            Assert.Equal(new[] { "rebuild" }, scene.Log);
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
        public void Palette_WhitespaceOnlyFilterEdit_DoesNotRebuild()
        {
            var scene = PushPaletteScene(KitCategories());

            scene.PaletteFilter.SetText("oak");
            scene.RefreshPalettes();
            TreeNode nodeBefore = scene.PaletteTree.Roots[0];   // RebuildPaletteTree always mints fresh TreeNodes

            // Trims to the same "oak" the tree was already built for: RefreshPalettes must compare trimmed text
            // against the last-applied TRIMMED value, not the raw text, or a bare-space edit re-triggers
            // RebuildPaletteTree for no visible change.
            scene.PaletteFilter.SetText("oak ");
            scene.RefreshPalettes();

            Assert.Same(nodeBefore, scene.PaletteTree.Roots[0]);   // same instance: no rebuild ran
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

            // The pinned "player spawn" entry heads the list (never filtered), then the three archetypes.
            Assert.Equal(new[] { "player spawn", "wolf", "bear", "wolfpup" },
                scene.SpawnList.Roots.Select(r => r.Label.Resolve()).ToArray());
            Assert.All(scene.SpawnList.Roots, r => Assert.Empty(r.Children));   // no categories: every root is a leaf

            scene.SpawnFilter.SetText("WOLF");   // case-insensitive substring, narrows the archetypes below the pin
            scene.RefreshPalettes();
            Assert.Equal(new[] { "player spawn", "wolf", "wolfpup" },
                scene.SpawnList.Roots.Select(r => r.Label.Resolve()).ToArray());

            scene.SpawnFilter.SetText("");
            scene.RefreshPalettes();
            Assert.Equal(4, scene.SpawnList.Roots.Count);                 // clearing restores the pin plus all three
        }

        [Fact]
        public void SpawnList_WhitespaceOnlyFilterEdit_DoesNotRebuild()
        {
            var scene = PushPaletteScene(new Dictionary<string, string>(StringComparer.Ordinal), "wolf", "bear", "wolfpup");

            scene.SpawnFilter.SetText("wolf");
            scene.RefreshPalettes();
            TreeNode nodeBefore = scene.SpawnList.Roots[0];   // RebuildSpawnList always mints a fresh pinned root

            // Same underlying bug as the palette filter: a whitespace-only edit trims to the same "wolf" match, so
            // it must not re-trigger RebuildSpawnList.
            scene.SpawnFilter.SetText(" wolf");
            scene.RefreshPalettes();

            Assert.Same(nodeBefore, scene.SpawnList.Roots[0]);   // same instance: no rebuild ran
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

        [Fact]
        public void TruncateStatusLine_FitsWithinStrip_ReturnsUnchanged()
        {
            // A deterministic 10-units-per-char measure keeps the math exact and font-free (the GridCellText
            // idiom from PropertyGridTests, applied to the status strip's own truncation helper).
            static float Width(string s) => s.Length * 10f;

            Assert.Equal("Select   undo: -", MapEditorScene.TruncateStatusLine("Select   undo: -", 1000f, Width));
        }

        [Fact]
        public void TruncateStatusLine_TooWideForStrip_TruncatesWithEllipsis()
        {
            static float Width(string s) => s.Length * 10f;
            string longLine = new string('x', 40);

            string truncated = MapEditorScene.TruncateStatusLine(longLine, 100f, Width);

            // Fits inside the strip's own width (the truncation target is narrower still, once the left/right
            // insets are reserved), ends in the trailing ellipsis, and is strictly shorter than the source.
            Assert.True(Width(truncated) <= 100f);
            Assert.EndsWith("...", truncated);
            Assert.True(truncated.Length < longLine.Length);
        }

        [Fact]
        public void TruncateStatusLine_StripNarrowerThanInsets_ReturnsEllipsisOnly()
        {
            static float Width(string s) => s.Length * 10f;

            // A strip width at or below the reserved insets clamps the fit target to zero: not even one prefix
            // char plus the dots fits, so TruncateWithEllipsis's own floor case returns the bare ellipsis.
            Assert.Equal("...", MapEditorScene.TruncateStatusLine(new string('x', 40), 0f, Width));
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

        // ---- scatter + companion layer editing ----------------------------------------------------------

        static DocScene ScatterScene() => PushDocScene(() =>
        {
            MapDocument doc = ValidDoc();
            doc.ScatterLayers.Add(new MapScatterLayer
            {
                Name = "trees", Seed = 11, CellSize = 5f,
                Rules = { new MapBiomeScatterRule
                {
                    Biome = KhaozEngine.Terrain.BiomeId.Meadow, Density = 0.4f,
                    Kinds = { new MapPropKind { Id = "oak", Weight = 1f } },
                } },
            });
            return doc;
        });

        [Fact]
        public void ScatterLayersCategory_InOutline_SelectableEditable()
        {
            var scene = ScatterScene();

            // A Scatter Layers category sits in the outline, one node per layer (label = name) plus a trailing add.
            TreeNode layer0 = CategoryChild(scene.Outline, "Scatter Layers", 0);
            Assert.Equal("trees", layer0.Label.Resolve());
            TreeNode add = CategoryChild(scene.Outline, "Scatter Layers", 1);
            Assert.Equal("[+ add layer]", add.Label.Resolve());

            // Selecting the layer builds the editable inspector: Name, scalars, and the per-rule surface.
            scene.Document.Selection.Set(SelectionKind.ScatterLayer, "trees");
            Assert.NotNull(TextRowByLabel(scene.Inspector, "Name"));             // an inline-rename row is present
            Assert.NotNull(TextRowByLabel(scene.Inspector, "Rule 0 kinds"));     // the crude id:weight text row
            Assert.Equal(5f, FloatRowByLabel(scene.Inspector, "CellSize").Field.Value);   // scalars init from the layer
            Assert.Equal(0.4f, FloatRowByLabel(scene.Inspector, "Rule 0 density").Field.Value);
            var biome = Assert.IsType<ChoiceRow>(RowByLabel(scene.Inspector, "Rule 0 biome"));
            Assert.Equal("Meadow", biome.Selected);
        }

        [Fact]
        public void ScatterLayer_AddViaOutlineAction_AppendsAndSelects()
        {
            var scene = ScatterScene();
            TreeView outline = scene.Outline;
            outline.Bounds = new Rect(0f, 0f, 240f, 800f);
            TreeNode addNode = CategoryChild(outline, "Scatter Layers", 1);

            var input = new InputManager();
            TapTree(outline, input, RowCenter(outline, addNode));

            // A layer with a generated unique name was appended and selected straight into its inspector.
            Assert.Equal(2, scene.Document.Doc.ScatterLayers.Count);
            Assert.Equal("layer-1", scene.Document.Doc.ScatterLayers[1].Name);
            Assert.Equal(SelectionKind.ScatterLayer, scene.Document.Selection.Kind);
            Assert.Equal("layer-1", scene.Document.Selection.Id);
            Assert.True(scene.Document.WorldRebuildPending);
        }

        [Fact]
        public void CompanionLayer_AddViaOutlineAction_HostDefaultsToFirstScatter()
        {
            var scene = ScatterScene();
            TreeView outline = scene.Outline;
            outline.Bounds = new Rect(0f, 0f, 240f, 800f);
            TreeNode addNode = CategoryChild(outline, "Companion Layers", 0);   // no companions yet, so [+ add] is first
            Assert.Equal("[+ add companion]", addNode.Label.Resolve());

            var input = new InputManager();
            TapTree(outline, input, RowCenter(outline, addNode));

            Assert.Single(scene.Document.Doc.CompanionLayers);
            MapCompanionLayer added = scene.Document.Doc.CompanionLayers[0];
            Assert.Equal("companion-1", added.Name);
            Assert.Equal("trees", added.HostLayer);   // defaulted to the first scatter layer, so it validates on save
            Assert.Equal(SelectionKind.CompanionLayer, scene.Document.Selection.Kind);
        }

        // Bug #25: with zero scatter layers the "[+ add companion]" action node used to still appear, and
        // activating it crashed the editor (AddCompanionLayerCommand defaults HostLayer to "", which
        // BuildPropLayers then rejects mid-frame, out of OnUpdate). A companion rings a host scatter layer,
        // so with none the affordance is meaningless and unsafe: it must not be offered.
        [Fact]
        public void CompanionLayerAddAction_NoScatterLayers_NotOffered()
        {
            var scene = PushDocScene(ValidDoc);

            TreeNode companionRoot = scene.Outline.Roots.Single(r => r.Label.Resolve() == "Companion Layers");
            Assert.DoesNotContain(companionRoot.Children, n => n.Label.Resolve() == "[+ add companion]");
        }

        // The gate only withholds the action when there is nothing to host a companion: with >= 1 scatter
        // layer present, the affordance is still offered (the fix gates, it does not delete, the feature).
        [Fact]
        public void CompanionLayerAddAction_WithScatterLayer_Offered()
        {
            var scene = ScatterScene();

            TreeNode companionRoot = scene.Outline.Roots.Single(r => r.Label.Resolve() == "Companion Layers");
            Assert.Contains(companionRoot.Children, n => n.Label.Resolve() == "[+ add companion]");
        }

        // The gate targets only the trailing action node: an existing companion layer (e.g. left over after
        // its host scatter layer was removed) still gets a selectable outline row even with zero scatter
        // layers, it just cannot grow a new one via the outline until a host exists again.
        [Fact]
        public void CompanionLayerAddAction_NoScatterLayersButExistingCompanion_CompanionStillListed()
        {
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.CompanionLayers.Add(new MapCompanionLayer { Name = "ring", HostLayer = "trees" });
                return doc;
            });

            TreeNode companionRoot = scene.Outline.Roots.Single(r => r.Label.Resolve() == "Companion Layers");
            Assert.Contains(companionRoot.Children, n => n.Label.Resolve() == "ring (host trees)");
            Assert.DoesNotContain(companionRoot.Children, n => n.Label.Resolve() == "[+ add companion]");
        }

        [Fact]
        public void RuleAddRemove_RoundTrip()
        {
            var scene = ScatterScene();
            scene.Document.Selection.Set(SelectionKind.ScatterLayer, "trees");
            Assert.Single(scene.Document.Doc.ScatterLayers[0].Rules);

            // "[+ add rule]" is a button row (a BoolRow read as always-off): tapping it appends a rule.
            Assert.True(TapBool(BoolRowByLabel(scene.Inspector, "[+ add rule]")));
            Assert.Equal(2, scene.Document.Doc.ScatterLayers[0].Rules.Count);
            scene.OnUpdate(0.016f);   // the per-rule rows reflow through the deferred sync
            Assert.NotNull(RowByLabel(scene.Inspector, "Rule 1 density"));

            // "[- remove rule 0]" removes the first rule.
            Assert.True(TapBool(BoolRowByLabel(scene.Inspector, "[- remove rule 0]")));
            Assert.Single(scene.Document.Doc.ScatterLayers[0].Rules);
            scene.OnUpdate(0.016f);

            // Each button sealed its own gesture, so undo peels them back one at a time.
            Assert.True(scene.Document.Undo());
            Assert.Equal(2, scene.Document.Doc.ScatterLayers[0].Rules.Count);
            Assert.True(scene.Document.Undo());
            Assert.Single(scene.Document.Doc.ScatterLayers[0].Rules);
        }

        [Fact]
        public void KindsTextRow_ParsesIdWeight_RejectsGarbage()
        {
            var scene = ScatterScene();
            scene.Document.Selection.Set(SelectionKind.ScatterLayer, "trees");
            TextRow kinds = TextRowByLabel(scene.Inspector, "Rule 0 kinds");

            var ui = new InputManager();
            ui.Update(InputState.Empty);
            kinds.Input.IsFocused = true;

            // A valid "id" / "id:weight" list parses (unit weight from the bare id, an explicit weight from the pair).
            kinds.Input.SetText("oak:2, pine");
            kinds.Update(new Rect(0f, 0f, 200f, 28f), ui, 0.016f);
            List<MapPropKind> parsed = scene.Document.Doc.ScatterLayers[0].Rules[0].Kinds;
            Assert.Equal(2, parsed.Count);
            Assert.Equal("oak", parsed[0].Id);
            Assert.Equal(2f, parsed[0].Weight);
            Assert.Equal("pine", parsed[1].Id);
            Assert.Equal(1f, parsed[1].Weight);

            // Garbage (a non-numeric weight) is rejected: no command runs and the old value is kept.
            kinds.Input.SetText("oak:abc");
            kinds.Update(new Rect(0f, 0f, 200f, 28f), ui, 0.016f);
            Assert.Equal(2, scene.Document.Doc.ScatterLayers[0].Rules[0].Kinds.Count);   // unchanged
            Assert.Equal("oak", scene.Document.Doc.ScatterLayers[0].Rules[0].Kinds[0].Id);
        }

        [Fact]
        public void ScatterLayerInspector_DensityScrub_DeepCloneUndo()
        {
            // Proves the scene's whole-value edit DEEP-clones the layer: scrubbing a nested rule field then undoing
            // must restore the original value. A shallow clone would share the Rules list, so the captured old value
            // would be mutated too and undo would leave the scrubbed value behind.
            var scene = ScatterScene();
            scene.Document.Selection.Set(SelectionKind.ScatterLayer, "trees");
            FloatRow density = FloatRowByLabel(scene.Inspector, "Rule 0 density");

            var cell = new Rect(0f, 0f, 200f, 28f);
            var ui = new InputManager();
            ui.Update(MouseFrame(new Vector2(100f, 10f), leftDown: false)); density.Update(cell, ui, 0.016f);
            ui.Update(MouseFrame(new Vector2(100f, 10f), leftDown: true)); density.Update(cell, ui, 0.016f);   // grab origin
            ui.Update(MouseFrame(new Vector2(200f, 10f), leftDown: true)); density.Update(cell, ui, 0.016f);   // +100px * 0.01 = +1.0
            Near(1.4f, scene.Document.Doc.ScatterLayers[0].Rules[0].Density);

            Assert.True(scene.Document.Undo());
            Near(0.4f, scene.Document.Doc.ScatterLayers[0].Rules[0].Density);   // old value intact: the clone did not alias it
        }

        [Fact]
        public void LayerVisibilityToggle_FollowsRename()
        {
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.ScatterLayers.Add(new MapScatterLayer { Name = "trees" });
                return doc;
            });
            scene.Visibility.SetLayer("trees", false);   // hide the layer's props
            scene.Document.Selection.Set(SelectionKind.ScatterLayer, "trees");

            TextRow name = TextRowByLabel(scene.Inspector, "Name");
            var ui = new InputManager();
            ui.Update(InputState.Empty);
            name.Input.IsFocused = true;
            name.Input.SetText("forest");
            name.Update(new Rect(0f, 0f, 200f, 28f), ui, 0.016f);

            Assert.Equal("forest", scene.Document.Doc.ScatterLayers[0].Name);
            Assert.False(scene.Visibility.GetLayer("forest"));   // the hide followed the rename to the new key
            Assert.True(scene.Visibility.GetLayer("trees"));      // the old key defaults back to visible
        }

        [Fact]
        public void ScatterLayerRename_ViaNameRow_CascadesAndSelectionFollows()
        {
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.ScatterLayers.Add(new MapScatterLayer { Name = "trees" });
                doc.CompanionLayers.Add(new MapCompanionLayer { Name = "understory", HostLayer = "trees" });
                return doc;
            });
            scene.Document.Selection.Set(SelectionKind.ScatterLayer, "trees");

            TextRow name = TextRowByLabel(scene.Inspector, "Name");
            var ui = new InputManager();
            ui.Update(InputState.Empty);
            name.Input.IsFocused = true;
            name.Input.SetText("forest");
            name.Update(new Rect(0f, 0f, 200f, 28f), ui, 0.016f);

            Assert.Equal("forest", scene.Document.Doc.ScatterLayers[0].Name);
            Assert.Equal("forest", scene.Document.Doc.CompanionLayers[0].HostLayer);   // cascade retargeted the host

            // The name-keyed selection follows the rename once the row loses focus (the pending re-select).
            name.Input.IsFocused = false;
            scene.OnUpdate(0.016f);
            Assert.Equal(SelectionKind.ScatterLayer, scene.Document.Selection.Kind);
            Assert.Equal("forest", scene.Document.Selection.Id);
        }

        [Fact]
        public void ScatterLayerRename_MidEditFrame_KeepsSelectionAndRow()
        {
            // The interactive sequence the test above never exercises: TextRow's setter fires the rename on every
            // keystroke while the row is STILL focused (not just on blur), so a frame can land between a keystroke
            // and the blur with the document already renamed but the row still open and the selection still on the
            // OLD name. That frame must not tear the inspector down under the user.
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.ScatterLayers.Add(new MapScatterLayer { Name = "trees" });
                return doc;
            });
            scene.Document.Selection.Set(SelectionKind.ScatterLayer, "trees");

            TextRow name = TextRowByLabel(scene.Inspector, "Name");
            var ui = new InputManager();
            ui.Update(InputState.Empty);
            name.Input.IsFocused = true;
            name.Input.SetText("forest");
            name.Update(new Rect(0f, 0f, 200f, 28f), ui, 0.016f);   // one keystroke: setter fires, row stays focused

            Assert.Equal("forest", scene.Document.Doc.ScatterLayers[0].Name);   // renamed already
            Assert.True(name.Input.IsFocused);   // still focused, mid-edit

            // Pump a frame WITHOUT blurring. The old name is now dangling (renamed away), but the row is still
            // open: the vanished-selection guard must not clear the selection out from under the user.
            scene.OnUpdate(0.016f);

            Assert.Equal(SelectionKind.ScatterLayer, scene.Document.Selection.Kind);   // selection survived
            Assert.Equal("trees", scene.Document.Selection.Id);   // pending re-select has not landed yet (still focused)
            Assert.Same(name, TextRowByLabel(scene.Inspector, "Name"));   // same row instance: not torn down mid-edit
            Assert.True(name.Input.IsFocused);   // the live editor is still focused

            // Blur and pump again: the deferred re-select lands on the new name.
            name.Input.IsFocused = false;
            scene.OnUpdate(0.016f);
            Assert.Equal(SelectionKind.ScatterLayer, scene.Document.Selection.Kind);
            Assert.Equal("forest", scene.Document.Selection.Id);
        }

        [Fact]
        public void ExclusionLayerRows_FollowScatterLayerAddAndRename()
        {
            // The Task 2 review carry-forward: the exclusion inspector's layer-targeting rows capture the scatter
            // layer set at build time, so adding / renaming a scatter layer while the exclusion stays selected must
            // refresh those rows (never show a stale set).
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.ScatterLayers.Add(new MapScatterLayer { Name = "trees" });
                doc.Exclusions.Add(new MapExclusion
                {
                    Shape = new DiscShapeDoc { Radius = 5f },
                    Layers = new List<string> { "trees" },   // explicit, so the per-layer rows show
                });
                return doc;
            });
            scene.Document.Selection.Set(SelectionKind.Exclusion, "0");
            Assert.NotNull(BoolRowByLabel(scene.Inspector, "trees"));
            Assert.DoesNotContain(scene.Inspector.Rows.OfType<BoolRow>(), b => b.Label.Resolve() == "rocks");

            // Add a scatter layer while the exclusion stays selected: the new layer appears as a targeting row.
            scene.Document.Execute(new AddScatterLayerCommand(new MapScatterLayer { Name = "rocks" }));
            scene.OnUpdate(0.016f);
            Assert.NotNull(BoolRowByLabel(scene.Inspector, "rocks"));

            // Rename a scatter layer while the exclusion stays selected: the row relabels (and the cascade retargets
            // the exclusion's own filter, so it stays valid rather than dangling on the old name).
            scene.Document.Execute(new RenameScatterLayerCommand("trees", "forest"));
            scene.OnUpdate(0.016f);
            Assert.NotNull(BoolRowByLabel(scene.Inspector, "forest"));
            Assert.DoesNotContain(scene.Inspector.Rows.OfType<BoolRow>(), b => b.Label.Resolve() == "trees");
            Assert.Equal(new[] { "forest" }, scene.Document.Doc.Exclusions[0].Layers);
        }

        [Fact]
        public void RemoveReferencedScatterLayer_ViaInspector_SurfacesStatus()
        {
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.ScatterLayers.Add(new MapScatterLayer { Name = "trees" });
                doc.CompanionLayers.Add(new MapCompanionLayer { Name = "understory", HostLayer = "trees" });
                return doc;
            });
            scene.Document.Selection.Set(SelectionKind.ScatterLayer, "trees");

            // The inspector's remove button rejects a referenced removal: the layer stays and the message surfaces.
            Assert.True(TapBool(BoolRowByLabel(scene.Inspector, "[- remove layer]")));
            Assert.Single(scene.Document.Doc.ScatterLayers);
            Assert.Contains("understory", scene.StatusText);
            Assert.False(scene.Document.History.CanUndo);   // rejected before mutating: no undo step

            // An unreferenced companion removes cleanly from its own inspector button.
            scene.Document.Selection.Set(SelectionKind.CompanionLayer, "understory");
            Assert.True(TapBool(BoolRowByLabel(scene.Inspector, "[- remove companion]")));
            Assert.Empty(scene.Document.Doc.CompanionLayers);
        }

        [Fact]
        public void CompanionInspector_HostLayerChooser_OffersLiveScatterLayers()
        {
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.ScatterLayers.Add(new MapScatterLayer { Name = "trees" });
                doc.ScatterLayers.Add(new MapScatterLayer { Name = "rocks" });
                doc.CompanionLayers.Add(new MapCompanionLayer { Name = "understory", HostLayer = "trees" });
                return doc;
            });
            scene.Document.Selection.Set(SelectionKind.CompanionLayer, "understory");

            var host = Assert.IsType<ChoiceRow>(RowByLabel(scene.Inspector, "HostLayer"));
            Assert.Equal("trees", host.Selected);   // the chooser reflects the live host, drawn from the scatter set
        }

        // ---- player spawns (Task 4 Half A: the editor slice) ----------------------------------------------

        [Fact]
        public void PlayerSpawn_PlaceDragRenameDelete_FullGesturePath()
        {
            var scene = new FieldDocScene(ValidDoc);
            scene.Init(null!, null!, null!, new MapEditorOptions());
            new SceneManager().Push(scene);

            var down = new Vector3(0f, -1f, 0f);
            EditorFrameInput Press(Vector3 o, float travel = 0f) =>
                new EditorFrameInput(o, down, pointerPressed: true, pointerDown: true, pointerTravel: travel, dt: 0.016f);
            EditorFrameInput Hold(Vector3 o, float travel) =>
                new EditorFrameInput(o, down, pointerDown: true, pointerTravel: travel, dt: 0.016f);
            EditorFrameInput Release(Vector3 o) => new EditorFrameInput(o, down, pointerReleased: true, dt: 0.016f);

            // PLACE: the pinned "player spawn" entry drives the spawn tool to stamp a player start on a click.
            scene.Controller.Mode = EditorToolMode.PlaceSpawn;
            scene.Controller.PlacingPlayerSpawn = true;
            scene.Controller.Update(Press(new Vector3(0f, 100f, 0f)));
            scene.Controller.Update(Release(new Vector3(0f, 100f, 0f)));
            Assert.Single(scene.Document.Doc.PlayerSpawns);
            Assert.Empty(scene.Document.Doc.Spawns);   // an NPC spawn was NOT placed
            Assert.Equal("player-1", scene.Document.Doc.PlayerSpawns[0].Id);
            Assert.Equal(SelectionKind.PlayerSpawn, scene.Document.Selection.Kind);
            Assert.Equal("player-1", scene.Document.Selection.Id);

            // DRAG: body-drag the selected player spawn clear of the gizmo handles (Marker affordance, TranslateXZ).
            scene.Controller.Mode = EditorToolMode.Select;
            var bodyPoint = new Vector3(-0.4f, 100f, -0.4f);
            scene.Controller.Update(Press(bodyPoint));   // press the body: selection stays, no drag yet
            scene.Controller.Update(Hold(new Vector3(4.6f, 100f, -0.4f), EditorToolController.BodyDragThreshold + 1f));
            scene.Controller.Update(Release(new Vector3(4.6f, 100f, -0.4f)));
            Near(5f, scene.Document.Doc.PlayerSpawns[0].X);   // dragged +5 on X
            Near(0f, scene.Document.Doc.PlayerSpawns[0].Z);

            // RENAME: the inline Name row (Rows[1], after the "Identity" group header) routes through
            // RenamePlayerSpawnCommand.
            var name = Assert.IsType<TextRow>(scene.Inspector.Rows[1]);
            var ui = new InputManager();
            ui.Update(InputState.Empty);
            name.Input.IsFocused = true;
            name.Input.SetText("hero-start");
            name.Update(new Rect(0f, 0f, 200f, 28f), ui, 0.016f);
            Assert.Equal("hero-start", scene.Document.Doc.PlayerSpawns[0].Id);
            name.Input.IsFocused = false;
            scene.OnUpdate(0.016f);   // the deferred re-select lands the selection on the new id
            Assert.Equal("hero-start", scene.Document.Selection.Id);

            // DELETE: the standard Delete-key path removes the selected player spawn and clears the selection.
            scene.Controller.Update(new EditorFrameInput(default, default, deletePressed: true));
            Assert.Empty(scene.Document.Doc.PlayerSpawns);
            Assert.True(scene.Document.Selection.IsEmpty);
        }

        [Fact]
        public void PlayerSpawnPalette_EntryPinnedAboveArchetypes()
        {
            var scene = PushPaletteScene(new Dictionary<string, string>(StringComparer.Ordinal), "wolf", "bear");

            // The "player spawn" entry is pinned at the very top, above every archetype.
            Assert.Equal(new[] { "player spawn", "wolf", "bear" },
                scene.SpawnList.Roots.Select(r => r.Label.Resolve()).ToArray());

            scene.SpawnList.Bounds = new Rect(0f, 0f, 200f, 240f);
            scene.SpawnList.RowHeight = 22f;
            var input = new InputManager();

            // Tapping the pinned entry (row 0) flips the spawn tool to placing a player start.
            TapTree(scene.SpawnList, input, new Vector2(120f, 0 * 22f + 11f));
            Assert.True(scene.Controller.PlacingPlayerSpawn);

            // Tapping an archetype (row 1 = "wolf") flips it back to an NPC spawn of that archetype.
            TapTree(scene.SpawnList, input, new Vector2(120f, 1 * 22f + 11f));
            Assert.False(scene.Controller.PlacingPlayerSpawn);
            Assert.Equal("wolf", scene.Controller.SpawnArchetype);
        }

        [Fact]
        public void PlayerSpawnInspector_ShowsRenameXZYawEnabledVisible()
        {
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.PlayerSpawns.Add(new MapPlayerSpawn { Id = "player-1", X = 2f, Z = 3f, Yaw = 1.5f, Enabled = true });
                return doc;
            });
            scene.Document.Selection.Set(SelectionKind.PlayerSpawn, "player-1");

            Assert.IsType<HeaderRow>(scene.Inspector.Rows[0]);   // the "Identity" group header (Task 5 grouping)
            Assert.IsType<TextRow>(scene.Inspector.Rows[1]);     // inline rename row
            Assert.Equal(2f, FloatRowByLabel(scene.Inspector, "X").Field.Value);
            Assert.Equal(3f, FloatRowByLabel(scene.Inspector, "Z").Field.Value);
            Assert.Equal(1.5f, FloatRowByLabel(scene.Inspector, "Yaw").Field.Value);   // raw radians, like the placement Yaw row
            Assert.NotNull(BoolRowByLabel(scene.Inspector, "Enabled"));
            Assert.NotNull(BoolRowByLabel(scene.Inspector, "Visible"));
            // Player spawns carry no archetype, so the NPC Archetype row is absent.
            Assert.DoesNotContain(scene.Inspector.Rows.OfType<TextRow>(), t => t.Label.Resolve() == "Archetype");

            // Enabled toggles through SetPlayerSpawnEnabledCommand (undoable).
            Assert.True(TapBool(BoolRowByLabel(scene.Inspector, "Enabled")));
            Assert.False(scene.Document.Doc.PlayerSpawns[0].Enabled);
            Assert.True(scene.Document.History.CanUndo);
        }

        [Fact]
        public void PlayerSpawnYaw_EditIsUndoable_MarksDirty()
        {
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.PlayerSpawns.Add(new MapPlayerSpawn { Id = "player-1", X = 2f, Z = 3f, Yaw = 1.5f, Enabled = true });
                return doc;
            });
            scene.Document.Selection.Set(SelectionKind.PlayerSpawn, "player-1");
            FloatRow yaw = FloatRowByLabel(scene.Inspector, "Yaw");
            Assert.False(scene.Document.IsDirty);

            // Scrub the row's NumberField headless, same idiom as the X/Z and feature-row scrub tests: press
            // inside the editor cell, then drag +100 px. The scrub calls Field.SetValue and the row writes the
            // change through its setter, which must route through SetPlayerSpawnYawCommand (not a bare field set).
            var cell = new Rect(0f, 0f, 200f, 28f);
            var ui = new InputManager();
            ui.Update(MouseFrame(new Vector2(100f, 10f), leftDown: false));
            yaw.Update(cell, ui, 0.016f);
            ui.Update(MouseFrame(new Vector2(100f, 10f), leftDown: true));    // press inside (grab-gate origin)
            yaw.Update(cell, ui, 0.016f);
            ui.Update(MouseFrame(new Vector2(200f, 10f), leftDown: true));    // +100 px at DragScale 0.01 = +1.0
            bool changed = yaw.Update(cell, ui, 0.016f);

            Assert.True(changed);
            Near(2.5f, scene.Document.Doc.PlayerSpawns[0].Yaw);   // 1.5 + 1.0 scrub
            Assert.True(scene.Document.History.CanUndo);
            Assert.True(scene.Document.IsDirty);

            Assert.True(scene.Document.Undo());
            Near(1.5f, scene.Document.Doc.PlayerSpawns[0].Yaw);
        }

        [Fact]
        public void SpawnArchetype_EditIsUndoable_MarksDirty()
        {
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Spawns.Add(new MapSpawn { Id = "spawn-1", ArchetypeId = "wolf", X = 2f, Z = 3f });
                return doc;
            });
            scene.Document.Selection.Set(SelectionKind.Spawn, "spawn-1");
            TextRow archetype = TextRowByLabel(scene.Inspector, "Archetype");
            Assert.False(scene.Document.IsDirty);

            // Type into the row's TextInput headless, same idiom as the spawn/placement Name rename rows: focus,
            // set the buffer, then Update so the row sees TextChanged and writes through the setter, which must
            // route through SetSpawnArchetypeCommand (not a bare field set).
            var ui = new InputManager();
            ui.Update(InputState.Empty);
            archetype.Input.IsFocused = true;
            archetype.Input.SetText("worg");
            bool changed = archetype.Update(new Rect(0f, 0f, 200f, 28f), ui, 0.016f);

            Assert.True(changed);
            Assert.Equal("worg", scene.Document.Doc.Spawns[0].ArchetypeId);
            Assert.True(scene.Document.History.CanUndo);
            Assert.True(scene.Document.IsDirty);

            Assert.True(scene.Document.Undo());
            Assert.Equal("wolf", scene.Document.Doc.Spawns[0].ArchetypeId);
        }

        [Fact]
        public void PlayerSpawnOutline_ListsSpawns_DisabledSuffixed()
        {
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.PlayerSpawns.Add(new MapPlayerSpawn { Id = "player-1", Enabled = true });
                doc.PlayerSpawns.Add(new MapPlayerSpawn { Id = "player-2", Enabled = false });
                return doc;
            });

            TreeNode enabled = CategoryChild(scene.Outline, "Player Spawns", 0);
            TreeNode disabled = CategoryChild(scene.Outline, "Player Spawns", 1);
            Assert.Equal("player-1", enabled.Label.Resolve());
            Assert.Equal("player-2 (disabled)", disabled.Label.Resolve());
            Assert.Equal(new MapEditorScene.OutlineRef(SelectionKind.PlayerSpawn, "player-1"), enabled.Tag);
        }

        // ---- companion host swap (Task 4 Half B: locked decision 3) ---------------------------------------

        static DocScene CompanionScene(string companionHost, string[] hostKinds,
            params (string name, string[] kinds)[] scatters)
        {
            return PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                foreach ((string name, string[] kinds) in scatters)
                {
                    var rule = new MapBiomeScatterRule { Biome = KhaozEngine.Terrain.BiomeId.Meadow, Density = 1f };
                    foreach (string k in kinds) rule.Kinds.Add(new MapPropKind { Id = k, Weight = 1f });
                    doc.ScatterLayers.Add(new MapScatterLayer { Name = name, Rules = { rule } });
                }
                var companion = new MapCompanionLayer { Name = "understory", HostLayer = companionHost };
                companion.HostKinds.AddRange(hostKinds);
                doc.CompanionLayers.Add(companion);
                return doc;
            });
        }

        [Fact]
        public void HostSwap_ZeroIntersection_ClearsHostKinds_OneUndoStep()
        {
            var scene = CompanionScene("trees", new[] { "pine" },
                ("trees", new[] { "pine", "oak" }), ("rocks", new[] { "granite" }));
            scene.Document.Selection.Set(SelectionKind.CompanionLayer, "understory");

            // Swap the host to "rocks": [pine] matches none of {granite}, so the same edit clears HostKinds.
            scene.SetCompanionHostLayer("understory", "rocks");

            MapCompanionLayer live = scene.Document.Doc.CompanionLayers[0];
            Assert.Equal("rocks", live.HostLayer);
            Assert.Empty(live.HostKinds);   // cleared to match all hosts
            Assert.Contains("cleared", scene.StatusText);
            Assert.Equal(1, scene.Document.History.UndoDepth);   // ONE command, one undo step

            // One undo restores BOTH the host and the kinds.
            Assert.True(scene.Document.Undo());
            live = scene.Document.Doc.CompanionLayers[0];
            Assert.Equal("trees", live.HostLayer);
            Assert.Equal(new[] { "pine" }, live.HostKinds);
        }

        [Fact]
        public void HostSwap_Intersecting_KeepsHostKinds()
        {
            var scene = CompanionScene("trees", new[] { "oak" },
                ("trees", new[] { "pine", "oak" }), ("grove", new[] { "oak", "birch" }));
            scene.Document.Selection.Set(SelectionKind.CompanionLayer, "understory");

            // Swap to "grove": [oak] still matches {oak, birch}, so HostKinds are kept untouched.
            scene.SetCompanionHostLayer("understory", "grove");

            MapCompanionLayer live = scene.Document.Doc.CompanionLayers[0];
            Assert.Equal("grove", live.HostLayer);
            Assert.Equal(new[] { "oak" }, live.HostKinds);
        }

        [Fact]
        public void CompanionWarningRow_AppearsOnMismatch_HidesWhenEmpty()
        {
            var scene = CompanionScene("trees", new[] { "pine" }, ("trees", new[] { "pine", "oak" }));
            scene.Document.Selection.Set(SelectionKind.CompanionLayer, "understory");

            // Intersecting HostKinds ([pine] in {pine, oak}): no warning.
            Assert.False(HasRow(scene.Inspector, "Warning"));

            // Edit HostKinds to something the host layer cannot place ([maple]): the mismatch appears live via the
            // deferred SyncShapeInspector reflow (no reselect), exactly the _inspectorScatterNames-style trigger.
            DriveTextRow(scene, "HostKinds", "maple");
            Assert.Equal(new[] { "maple" }, scene.Document.Doc.CompanionLayers[0].HostKinds);
            scene.SyncShapeInspector();
            Assert.True(HasRow(scene.Inspector, "Warning"));

            // Empty HostKinds now means match-all, so the warning hides again.
            DriveTextRow(scene, "HostKinds", "");
            Assert.Empty(scene.Document.Doc.CompanionLayers[0].HostKinds);
            scene.SyncShapeInspector();
            Assert.False(HasRow(scene.Inspector, "Warning"));
        }

        // ---- Task 5: tooltips, grouping, layout, styling content ------------------------------------------

        // A document with at least one element of EVERY selection kind (Terrain is always present as the
        // singleton root), so EveryRow_HasDescription and Inspectors_AreGrouped can walk every inspector the
        // editor builds. The companion's HostKinds deliberately matches nothing in its host layer's rules
        // ("maple" against a layer that only places "oak"), so the mismatch Warning row is present too. The
        // region is rect-shaped (not disc, like the exclusion) so the walk below covers both AddShapeRows
        // branches, not just one of them repeated twice.
        static MapDocument EveryKindDoc()
        {
            MapDocument doc = ValidDoc();
            doc.Terrain.Biomes.Add(new MapBiomeBand { Start = 0f, End = 40f, Biome = KhaozEngine.Terrain.BiomeId.Meadow });
            doc.Terrain.Features.Add(new LakeFeatureDoc { CenterX = 0f, CenterZ = 0f, Radius = 5f, Depth = 2f });
            doc.Terrain.Features.Add(new FlattenFeatureDoc { CenterX = 1f, CenterZ = 1f, Radius = 4f, TargetHeight = 1f });
            doc.Terrain.Features.Add(new RimFeatureDoc { CenterX = 2f, CenterZ = 2f, InnerRadius = 3f, OuterRadius = 6f, WallHeight = 2f });
            doc.Terrain.Features.Add(new RidgeFeatureDoc { PointX = 3f, PointZ = 3f, DirectionX = 1f, Height = 2f, Width = 1f });
            doc.Placements.Add(new MapPlacement { Id = "placement-1", Kind = "prop" });
            doc.Spawns.Add(new MapSpawn { Id = "spawn-1", ArchetypeId = "wolf" });
            doc.PlayerSpawns.Add(new MapPlayerSpawn { Id = "player-1" });
            doc.ScatterLayers.Add(new MapScatterLayer
            {
                Name = "trees",
                Rules = { new MapBiomeScatterRule
                {
                    Biome = KhaozEngine.Terrain.BiomeId.Meadow, Density = 0.4f,
                    Kinds = { new MapPropKind { Id = "oak", Weight = 1f } },
                } },
            });
            doc.Exclusions.Add(new MapExclusion { Shape = new DiscShapeDoc { Radius = 5f }, Layers = new List<string> { "trees" } });
            // A rect-shaped override with an explicit layer list, so the walk covers the density / kinds rows plus
            // the per-layer targeting rows (an all-layers override would hide them), and both AddShapeRows branches.
            doc.ScatterOverrides.Add(new MapScatterOverrideDoc
            {
                Shape = new RectShapeDoc { MinX = -3f, MinZ = -3f, MaxX = 3f, MaxZ = 3f },
                DensityMultiplier = 0.5f,
                Kinds = new List<MapPropKind> { new MapPropKind { Id = "oak", Weight = 1f } },
                Layers = new List<string> { "trees" },
            });
            doc.Regions.Add(new MapRegion
            {
                Name = "region-1",
                Shape = new RectShapeDoc { MinX = -2f, MinZ = -2f, MaxX = 2f, MaxZ = 2f },
            });
            doc.CompanionLayers.Add(new MapCompanionLayer { Name = "understory", HostLayer = "trees", HostKinds = { "maple" } });
            return doc;
        }

        // A minimal document with a companion layer but NO scatter layers at all: BuildCompanionLayerInspector's
        // HostLayer chooser has nothing to offer (hostOptions empty, HostLayer defaults to ""), routing it to the
        // ReadOnlyRow fallback branch instead of the ChoiceRow branch EveryKindDoc's "understory" companion
        // already walks (it has a live "trees" scatter layer to choose from).
        static MapDocument CompanionNoScatterLayersDoc()
        {
            MapDocument doc = ValidDoc();
            doc.CompanionLayers.Add(new MapCompanionLayer { Name = "lonely" });
            return doc;
        }

        // Every non-header row in the CURRENT inspector must carry a non-null, non-empty Description: the guard
        // against a tooltip gap (a row an implementer forgot to describe would show no tooltip at all).
        static void AssertEveryRowDescribed(PropertyGrid grid, string context)
        {
            foreach (PropertyRow row in grid.Rows)
            {
                if (row is HeaderRow) continue;   // group dividers carry no editable value, no tooltip required
                LocalizedText? description = row.Description;
                Assert.True(description.HasValue, $"{context}: row '{row.Label.Resolve()}' has no Description");
                Assert.False(string.IsNullOrWhiteSpace(description!.Value.Resolve()),
                    $"{context}: row '{row.Label.Resolve()}' has an empty Description");
            }
        }

        [Fact]
        public void EveryRow_HasDescription()
        {
            var scene = PushDocScene(EveryKindDoc);

            // No selection: the Layers panel (group + per-scatter-layer visibility toggles).
            scene.Document.Selection.Clear();
            AssertEveryRowDescribed(scene.Inspector, "Layers panel (no selection)");

            scene.Document.Selection.Set(SelectionKind.Terrain, "");
            AssertEveryRowDescribed(scene.Inspector, "Terrain");

            scene.Document.Selection.Set(SelectionKind.Placement, "placement-1");
            AssertEveryRowDescribed(scene.Inspector, "Placement");

            scene.Document.Selection.Set(SelectionKind.Spawn, "spawn-1");
            AssertEveryRowDescribed(scene.Inspector, "Spawn");

            scene.Document.Selection.Set(SelectionKind.PlayerSpawn, "player-1");
            AssertEveryRowDescribed(scene.Inspector, "PlayerSpawn");

            // Every feature type (lake, flatten, rim, ridge) has its own AddFeatureRow call sites: walk all four.
            for (int i = 0; i < 4; i++)
            {
                scene.Document.Selection.Set(SelectionKind.Feature, $"{i}");
                AssertEveryRowDescribed(scene.Inspector, $"Feature[{i}]");
            }

            scene.Document.Selection.Set(SelectionKind.Exclusion, "0");
            AssertEveryRowDescribed(scene.Inspector, "Exclusion");

            scene.Document.Selection.Set(SelectionKind.ScatterOverride, "0");
            AssertEveryRowDescribed(scene.Inspector, "ScatterOverride");

            scene.Document.Selection.Set(SelectionKind.Region, "region-1");
            AssertEveryRowDescribed(scene.Inspector, "Region");

            scene.Document.Selection.Set(SelectionKind.BiomeBand, "0");
            AssertEveryRowDescribed(scene.Inspector, "BiomeBand");

            scene.Document.Selection.Set(SelectionKind.ScatterLayer, "trees");
            AssertEveryRowDescribed(scene.Inspector, "ScatterLayer");

            // The companion selection also exercises the Warning row (HostKinds mismatches the host layer).
            scene.Document.Selection.Set(SelectionKind.CompanionLayer, "understory");
            Assert.True(HasRow(scene.Inspector, "Warning"));
            AssertEveryRowDescribed(scene.Inspector, "CompanionLayer");

            // A second, separate walk: a companion with no scatter layers to choose from routes HostLayer to its
            // ReadOnlyRow fallback (see CompanionNoScatterLayersDoc), never covered by the "understory" companion
            // above since it always has a live "trees" layer.
            var noScatterScene = PushDocScene(CompanionNoScatterLayersDoc);
            noScatterScene.Document.Selection.Set(SelectionKind.CompanionLayer, "lonely");
            Assert.NotNull(ReadOnlyRowByLabel(noScatterScene.Inspector, "HostLayer"));
            AssertEveryRowDescribed(noScatterScene.Inspector, "CompanionLayer (no scatter layers)");
        }

        // A dedicated document for the PolygonShapeDoc branch of AddShapeRows: EveryKindDoc deliberately keeps its
        // exclusion/region/scatter-override shapes disc/rect (other tests depend on that exact fixture), so the
        // polygon branch (the read-only "Kind" + "Points" rows) has never been walked by EveryRow_HasDescription.
        [Fact]
        public void PolygonShapeRows_HaveDescriptions()
        {
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Exclusions.Add(new MapExclusion
                {
                    Shape = new PolygonShapeDoc { Points = { new[] { 0f, 0f }, new[] { 10f, 0f }, new[] { 5f, 8f } } },
                });
                return doc;
            });

            scene.Document.Selection.Set(SelectionKind.Exclusion, "0");

            ReadOnlyRow kind = ReadOnlyRowByLabel(scene.Inspector, "Kind");
            kind.Update(new Rect(0f, 0f, 200f, 28f), new InputManager(), 0f);   // polls the display getter
            Assert.Equal("polygon", kind.Display);

            AssertEveryRowDescribed(scene.Inspector, "Exclusion (polygon)");
        }

        // Asserts the CURRENT inspector carries at least the given group HeaderRow labels (order-independent,
        // extra headers allowed), the loose pin the plan calls for so a wording tweak does not break the test.
        static void AssertGrouped(PropertyGrid grid, string context, params string[] expectedHeaders)
        {
            List<string> headers = grid.Rows.OfType<HeaderRow>().Select(h => h.Label.Resolve()).ToList();
            Assert.True(headers.Count >= expectedHeaders.Length,
                $"{context}: expected at least {expectedHeaders.Length} group headers, found {headers.Count} " +
                $"({string.Join(", ", headers)})");
            foreach (string expected in expectedHeaders)
                Assert.Contains(expected, headers);
        }

        [Fact]
        public void Inspectors_AreGrouped()
        {
            var scene = PushDocScene(EveryKindDoc);

            scene.Document.Selection.Clear();
            AssertGrouped(scene.Inspector, "Layers panel", "Groups");

            scene.Document.Selection.Set(SelectionKind.Terrain, "");
            AssertGrouped(scene.Inspector, "Terrain", "Water", "World", "Noise");

            scene.Document.Selection.Set(SelectionKind.Placement, "placement-1");
            AssertGrouped(scene.Inspector, "Placement", "Identity", "Transform", "State");

            scene.Document.Selection.Set(SelectionKind.Spawn, "spawn-1");
            AssertGrouped(scene.Inspector, "Spawn", "Identity", "Transform", "State");

            scene.Document.Selection.Set(SelectionKind.PlayerSpawn, "player-1");
            AssertGrouped(scene.Inspector, "PlayerSpawn", "Identity", "Transform", "State");

            scene.Document.Selection.Set(SelectionKind.Feature, "0");
            AssertGrouped(scene.Inspector, "Feature", "Identity", "Shape", "State");

            scene.Document.Selection.Set(SelectionKind.Exclusion, "0");
            AssertGrouped(scene.Inspector, "Exclusion", "Identity", "Shape", "Targeting");

            scene.Document.Selection.Set(SelectionKind.ScatterOverride, "0");
            AssertGrouped(scene.Inspector, "ScatterOverride", "Identity", "State", "Shape", "Scatter", "Layers");

            scene.Document.Selection.Set(SelectionKind.Region, "region-1");
            AssertGrouped(scene.Inspector, "Region", "Identity", "Shape");

            scene.Document.Selection.Set(SelectionKind.BiomeBand, "0");
            AssertGrouped(scene.Inspector, "BiomeBand", "Range", "Shape");

            scene.Document.Selection.Set(SelectionKind.ScatterLayer, "trees");
            AssertGrouped(scene.Inspector, "ScatterLayer", "Identity", "Placement", "Scale", "Rules");

            scene.Document.Selection.Set(SelectionKind.CompanionLayer, "understory");
            AssertGrouped(scene.Inspector, "CompanionLayer", "Identity", "Host", "Output", "Shape");
        }

        [Fact]
        public void Layout_InspectorWidth340_Outline260()
        {
            var scene = PushDocScene(ValidDoc);

            Rect outline = scene.OutlineRect(1200f, 700f);
            Rect inspector = scene.InspectorRect(1200f, 700f);

            Near(260f, outline.Width);
            Near(0f, outline.X);                     // flush against the left edge
            Near(340f, inspector.Width);
            Near(1200f - 340f, inspector.X);          // flush against the right edge, independent of outline width
            Near(outline.Y, inspector.Y);             // both share the same body-top / body-bottom band
            Near(outline.Height, inspector.Height);
        }

        [Fact]
        public void Tooltip_ShowsOnRowHover_HidesOff()
        {
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Terrain.WaterLevel = 1f;
                return doc;
            });
            scene.Document.Selection.Set(SelectionKind.Terrain, "");
            scene.Inspector.Bounds = new Rect(0f, 0f, 300f, 400f);

            FloatRow water = FloatRowByLabel(scene.Inspector, "WaterLevel");
            int rowIndex = scene.Inspector.Rows.IndexOf(water);
            Rect label = scene.Inspector.RowLabelBounds(rowIndex);
            var overRow = new Vector2(label.X + 5f, label.Y + label.Height * 0.5f);

            var ui = new InputManager();
            ui.Update(MouseFrame(overRow, leftDown: false));
            scene.Inspector.Update(ui, 0.016f);

            // Hovering the row's label band tracks it as HoveredRow (PropertyGrid, decision 4) and the scene's
            // tooltip content decision (ComputeTooltipContent, decision 4's host-owned Tooltip) shows immediately
            // with that row's Description, anchored at its label rect. No live SpriteFont is needed for this: the
            // Tooltip instance itself is only built lazily in OnDrawUi to actually draw.
            Assert.Same(water, scene.Inspector.HoveredRow);
            (LocalizedText Text, Vector2 Anchor)? content = scene.ComputeTooltipContent();
            Assert.NotNull(content);
            Assert.Equal(water.Description!.Value.Resolve(), content!.Value.Text.Resolve());
            Assert.Equal(label.X + label.Width * 0.5f, content.Value.Anchor.X);
            Assert.Equal(label.Y, content.Value.Anchor.Y);

            // Moving the pointer off the grid entirely clears the hover and hides the tooltip content.
            ui.Update(MouseFrame(new Vector2(-100f, -100f), leftDown: false));
            scene.Inspector.Update(ui, 0.016f);

            Assert.Null(scene.Inspector.HoveredRow);
            Assert.Null(scene.ComputeTooltipContent());
        }

        [Fact]
        public void ComputeTooltipContent_SurvivesInspectorRebuildMidFrame()
        {
            // A same-frame RebuildInspector (SyncShapeInspector's "All layers" mismatch check, driven by the
            // deterministic ExclusionLayerRows_AllToggle_NullSemantics repro) clears and re-adds Rows wholesale
            // but PropertyGrid has no seam to reset HoveredRow along with it, so a row hovered just before the
            // rebuild is left dangling: still referenced by HoveredRow, but absent from the new Rows list.
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.ScatterLayers.Add(new MapScatterLayer { Name = "trees" });
                doc.Exclusions.Add(new MapExclusion { Shape = new DiscShapeDoc { Radius = 5f }, Layers = null });
                return doc;
            });
            scene.Document.Selection.Set(SelectionKind.Exclusion, "0");
            scene.Inspector.Bounds = new Rect(0f, 0f, 300f, 400f);

            BoolRow all = BoolRowByLabel(scene.Inspector, "All layers");
            int allIndex = scene.Inspector.Rows.IndexOf(all);
            Rect label = scene.Inspector.RowLabelBounds(allIndex);
            var overRow = new Vector2(label.X + 5f, label.Y + label.Height * 0.5f);

            var ui = new InputManager();
            ui.Update(MouseFrame(overRow, leftDown: false));
            scene.Inspector.Update(ui, 0.016f);
            Assert.Same(all, scene.Inspector.HoveredRow);   // hovering a live, described row

            // Flip All off: materializes the explicit layer list, which SyncShapeInspector notices next as an
            // "All layers" mismatch against the inspector's build snapshot and rebuilds.
            Assert.True(TapBool(all));
            scene.SyncShapeInspector();

            // The rebuild replaced Rows wholesale (fresh row instances) but never touched HoveredRow: it still
            // references the OLD "All layers" row, now orphaned from the live Rows list.
            Assert.DoesNotContain(all, scene.Inspector.Rows);
            Assert.Same(all, scene.Inspector.HoveredRow);

            // Before the fix this threw ArgumentOutOfRangeException: Rows.IndexOf(hovered) returned -1 for the
            // orphaned row, and RowLabelBounds(-1) indexes straight into Rows[-1]. The seam degrades gracefully
            // instead: no content for a row that no longer exists in the grid it is asked about.
            Assert.Null(scene.ComputeTooltipContent());
        }

        // Drives a companion/scatter TextRow the way a keystroke commit does. One unfocused Update first polls the
        // live value into the buffer (a freshly rebuilt row starts empty), so replacing it with `text` is a real
        // change even when the target is the empty string, then focus, replace, and Update to fire the write-through.
        static void DriveTextRow(MapEditorScene scene, string label, string text)
        {
            TextRow row = TextRowByLabel(scene.Inspector, label);
            var cell = new Rect(0f, 0f, 200f, 28f);
            var ui = new InputManager();
            ui.Update(InputState.Empty);
            row.Update(cell, ui, 0.016f);   // unfocused: load the live value into the buffer
            row.Input.IsFocused = true;
            row.Input.SetText(text);
            row.Update(cell, ui, 0.016f);
        }

        static bool HasRow(PropertyGrid grid, string label)
        {
            foreach (PropertyRow row in grid.Rows)
                if (row.Label.Resolve() == label) return true;
            return false;
        }

        // The first PropertyRow with the given label (any row type), or a failing assert.
        static PropertyRow RowByLabel(PropertyGrid grid, string label)
        {
            foreach (PropertyRow row in grid.Rows)
                if (row.Label.Resolve() == label) return row;
            Assert.Fail($"no row labeled '{label}' (rows: {grid.Rows.Count})");
            return null!;   // unreachable
        }

        // ---- duplicate chord (Cmd+D, decision 8) ----------------------------------------------------------

        [Fact]
        public void CmdD_TerrainSelected_NoOp()
        {
            var scene = new DocScene(() => ValidDoc());
            scene.Init(null!, null!, null!, new MapEditorOptions());
            var m = new SceneManager();
            m.Push(scene);

            scene.Document.Selection.Set(SelectionKind.Terrain, "");

            m.Input = CtrlKeyFrame(Key.D);
            m.Update(0.016f);

            Assert.False(scene.Document.History.CanUndo);
            Assert.Equal(SelectionKind.Terrain, scene.Document.Selection.Kind);
            Assert.Equal("Nothing to duplicate: Terrain is the document singleton.", scene.StatusText);
        }

        [Fact]
        public void CmdD_DuplicatesSelection_ThroughTheScene()
        {
            // A thin end-to-end check that the chord actually reaches EditorToolController.DuplicateSelection
            // (the exhaustive per-kind coverage lives in EditorToolTests against the controller directly).
            var scene = new DocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Placements.Add(new MapPlacement { Id = "hut", Kind = "prop", X = 1f, Z = 1f });
                return doc;
            });
            scene.Init(null!, null!, null!, new MapEditorOptions());
            var m = new SceneManager();
            m.Push(scene);

            scene.Document.Selection.Set(SelectionKind.Placement, "hut");

            m.Input = SuperKeyFrame(Key.D);   // Cmd+D via Super, proving IsCommandDown's other modifier fires it too
            m.Update(0.016f);

            Assert.Equal(2, scene.Document.Doc.Placements.Count);
            Assert.Equal(SelectionKind.Placement, scene.Document.Selection.Kind);
            Assert.NotEqual("hut", scene.Document.Selection.Id);
            Assert.Equal(1, scene.Document.History.UndoDepth);
        }

        // A custom feature type FeatureGeometry.Translated does not know how to offset (not one of the four
        // built-ins), the same "unknown type" idiom EditorToolTests.UnknownFeatureDoc covers at the controller
        // level. DuplicateSelection returns null for it, and the scene tells that apart from the ordinary
        // empty-selection no-op (selection kind is still Feature) to surface its own status note.
        sealed class UnknownFeatureDoc : MapFeature
        {
            public override string Type => "unknown";
        }

        [Fact]
        public void CmdD_CustomFeatureType_ShowsStatusNote()
        {
            var scene = new DocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Terrain.Features.Add(new UnknownFeatureDoc { Name = "mystery" });
                return doc;
            });
            scene.Init(null!, null!, null!, new MapEditorOptions());
            var m = new SceneManager();
            m.Push(scene);

            scene.Document.Selection.Set(SelectionKind.Feature, "0");

            m.Input = CtrlKeyFrame(Key.D);
            m.Update(0.016f);

            Assert.False(scene.Document.History.CanUndo);
            Assert.Single(scene.Document.Doc.Terrain.Features);
            Assert.Equal(SelectionKind.Feature, scene.Document.Selection.Kind);
            Assert.Equal("0", scene.Document.Selection.Id);
            Assert.Equal("Cannot duplicate this feature type.", scene.StatusText);
        }

        // ---- camera bookmarks (Shift+1..9 / 1..9, decision 9) ---------------------------------------------

        [Fact]
        public void Bookmark_StoreRecall_RestoresPositionYawPitch()
        {
            var scene = new SpyScene();   // real UpdateCamera, device work skipped
            scene.Init(null!, null!, null!, new MapEditorOptions());
            var m = new SceneManager();
            m.Push(scene);

            scene.Camera.Position = new Vector3(3f, 4f, 5f);
            scene.Camera.Yaw = 0.7f;
            scene.Camera.Pitch = 0.2f;

            m.Input = KeyFrame(shiftDown: true, Key.D3);   // Shift+3 stores
            m.Update(0.016f);
            Assert.Equal("Bookmark 3 stored", scene.StatusText);

            scene.Camera.Position = new Vector3(-9f, 1f, 2f);
            scene.Camera.Yaw = -1.1f;
            scene.Camera.Pitch = -0.3f;

            m.Input = KeyFrame(shiftDown: false, Key.D3);   // bare 3 recalls
            m.Update(0.016f);

            Assert.Equal(new Vector3(3f, 4f, 5f), scene.Camera.Position);
            Assert.Equal(0.7f, scene.Camera.Yaw);
            Assert.Equal(0.2f, scene.Camera.Pitch);
            Assert.Equal("Bookmark 3 recalled", scene.StatusText);
        }

        [Fact]
        public void Bookmark_EmptySlot_StatusNote_NoCameraChange()
        {
            var scene = new SpyScene();
            scene.Init(null!, null!, null!, new MapEditorOptions());
            var m = new SceneManager();
            m.Push(scene);

            Vector3 before = scene.Camera.Position;
            float yawBefore = scene.Camera.Yaw, pitchBefore = scene.Camera.Pitch;

            m.Input = KeyFrame(shiftDown: false, Key.D5);   // slot 5 was never stored this session
            m.Update(0.016f);

            Assert.Equal(before, scene.Camera.Position);
            Assert.Equal(yawBefore, scene.Camera.Yaw);
            Assert.Equal(pitchBefore, scene.Camera.Pitch);
            Assert.Equal("Bookmark 5 is empty", scene.StatusText);
        }

        [Fact]
        public void Bookmark_GatedWhileTyping()
        {
            var scene = new DocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Placements.Add(new MapPlacement { Id = "hut", Kind = "prop", X = 0f, Z = 0f });
                return doc;
            });
            scene.Init(null!, null!, null!, new MapEditorOptions());
            var m = new SceneManager();
            m.Push(scene);

            // Store bookmark 3 at a pose distinct from the camera's current one, so a wrongly-ungated recall
            // would be an observable camera jump rather than a no-op that happens to look like the gate working.
            scene.Camera.Position = new Vector3(10f, 5f, 10f);
            scene.Camera.Yaw = 1f;
            scene.Camera.Pitch = 0.1f;
            m.Input = KeyFrame(shiftDown: true, Key.D3);
            m.Update(0.016f);
            Assert.Equal("Bookmark 3 stored", scene.StatusText);

            scene.Camera.Position = new Vector3(0f, 24f, -32f);
            scene.Camera.Yaw = 0f;
            scene.Camera.Pitch = -0.5f;
            Vector3 before = scene.Camera.Position;
            float yawBefore = scene.Camera.Yaw, pitchBefore = scene.Camera.Pitch;

            scene.Document.Selection.Set(SelectionKind.Placement, "hut");
            TextRow name = TextRowByLabel(scene.Inspector, "Name");
            name.Input.IsFocused = true;

            m.Input = KeyFrame(shiftDown: false, Key.D3);   // bare 3 while the rename row owns the frame
            m.Update(0.016f);

            // AnyEditorFocused gates the bookmark chord exactly like every other bare-key chord: no recall runs.
            Assert.Equal(before, scene.Camera.Position);
            Assert.Equal(yawBefore, scene.Camera.Yaw);
            Assert.Equal(pitchBefore, scene.Camera.Pitch);

            // The same key reaches the row's own Update (the live UiViewport path this headless suite stands in
            // for by driving the row directly), so the digit types into the field instead of being swallowed.
            var ui = new InputManager();
            ui.Update(KeyFrame(shiftDown: false, Key.D3));
            name.Update(new Rect(0f, 0f, 200f, 28f), ui, 0.016f);
            Assert.Equal("3", name.Input.Text);
        }

        // ---- Task 4: scatter override scene surface (outline, reorder, inspector, selection sync) ---------

        // Seeds a document with one "trees" scatter layer and however many scatter overrides the caller adds, so the
        // override outline / inspector / reorder tests share one setup shape (mirrors the exclusion test docs).
        static MapDocument OverrideDoc(params MapScatterOverrideDoc[] overrides)
        {
            MapDocument doc = ValidDoc();
            doc.ScatterLayers.Add(new MapScatterLayer { Name = "trees" });
            doc.ScatterLayers.Add(new MapScatterLayer { Name = "rocks" });
            foreach (MapScatterOverrideDoc o in overrides) doc.ScatterOverrides.Add(o);
            return doc;
        }

        static MapScatterOverrideDoc DiscOverride(float radius) =>
            new MapScatterOverrideDoc { Shape = new DiscShapeDoc { Radius = radius } };

        [Fact]
        public void SelectScatterOverride_InspectorShowsShapeDensityKindsLayerRows()
        {
            var scene = PushDocScene(() => OverrideDoc(new MapScatterOverrideDoc
            {
                Shape = new DiscShapeDoc { CenterX = 1f, CenterZ = 2f, Radius = 5f },
                DensityMultiplier = 0.5f,
                Kinds = new List<MapPropKind> { new MapPropKind { Id = "oak", Weight = 2f } },
                Layers = new List<string> { "trees" },
            }));

            scene.Document.Selection.Set(SelectionKind.ScatterOverride, "0");

            // Identity + State + Shape + Scatter + Layers, mirroring the exclusion inspector plus the density / kinds pair.
            Assert.NotNull(TextRowByLabel(scene.Inspector, "Name"));
            Assert.NotNull(BoolRowByLabel(scene.Inspector, "Visible"));
            Assert.NotNull(ChoiceRowByLabel(scene.Inspector, "Kind"));        // the disc shape surface
            Assert.NotNull(FloatRowByLabel(scene.Inspector, "CenterX"));
            Assert.NotNull(FloatRowByLabel(scene.Inspector, "Radius"));
            Assert.Equal(0.5f, FloatRowByLabel(scene.Inspector, "DensityMultiplier").Field.Value);
            Assert.NotNull(TextRowByLabel(scene.Inspector, "Kinds"));
            // An explicit Layers list is in effect, so the per-layer targeting rows sit below the "All layers" toggle.
            Assert.NotNull(BoolRowByLabel(scene.Inspector, "All layers"));
            Assert.NotNull(BoolRowByLabel(scene.Inspector, "trees"));
            Assert.NotNull(BoolRowByLabel(scene.Inspector, "rocks"));
        }

        [Fact]
        public void ScatterOverrideInspector_EditsDensityAndKinds_NullOnEmpty()
        {
            var scene = PushDocScene(() => OverrideDoc(new MapScatterOverrideDoc
            {
                Shape = new DiscShapeDoc { Radius = 5f },
                DensityMultiplier = 1f,
                Kinds = new List<MapPropKind> { new MapPropKind { Id = "oak", Weight = 1f } },
            }));

            scene.Document.Selection.Set(SelectionKind.ScatterOverride, "0");

            // Density scrub routes through EditScatterOverrideValuesCommand (the whole-value path), affecting the world.
            FloatRow density = FloatRowByLabel(scene.Inspector, "DensityMultiplier");
            var cell = new Rect(0f, 0f, 200f, 28f);
            var ui = new InputManager();
            ui.Update(MouseFrame(new Vector2(100f, 10f), leftDown: false)); density.Update(cell, ui, 0.016f);
            ui.Update(MouseFrame(new Vector2(100f, 10f), leftDown: true)); density.Update(cell, ui, 0.016f);
            ui.Update(MouseFrame(new Vector2(150f, 10f), leftDown: true));   // +50 px at DragScale 0.01 = +0.5
            Assert.True(density.Update(cell, ui, 0.016f));
            Near(1.5f, scene.Document.Doc.ScatterOverrides[0].DensityMultiplier);
            Assert.True(scene.Document.WorldRebuildPending);

            // Kinds edits parse into a FRESH MapPropKind list (the same "id:weight" convention as the scatter rule row).
            DriveTextRow(scene, "Kinds", "pine:3, oak");
            List<MapPropKind>? kinds = scene.Document.Doc.ScatterOverrides[0].Kinds;
            Assert.NotNull(kinds);
            Assert.Equal(new[] { "pine", "oak" }, kinds!.Select(k => k.Id));
            Assert.Equal(3f, kinds[0].Weight);

            // Empty text means NULL kinds (keep each layer's own kinds, density-only override), not an empty list.
            DriveTextRow(scene, "Kinds", "");
            Assert.Null(scene.Document.Doc.ScatterOverrides[0].Kinds);
        }

        [Fact]
        public void ScatterOverrideRenameRow_ExecutesRenameCommand_SelectionStaysOnIndex()
        {
            var scene = PushDocScene(() => OverrideDoc(DiscOverride(5f), new MapScatterOverrideDoc
            {
                Name = "taken", Shape = new DiscShapeDoc { Radius = 3f },
            }));

            scene.Document.Selection.Set(SelectionKind.ScatterOverride, "0");
            TextRow name = TextRowByLabel(scene.Inspector, "Name");

            var ui = new InputManager();
            ui.Update(InputState.Empty);
            name.Input.IsFocused = true;
            name.Input.SetText("dense-zone");
            name.Update(new Rect(0f, 0f, 200f, 28f), ui, 0.016f);

            // Scatter overrides are index-addressed like exclusions: a rename never moves the selection off its index.
            Assert.Equal("dense-zone", scene.Document.Doc.ScatterOverrides[0].Name);
            Assert.Equal(SelectionKind.ScatterOverride, scene.Document.Selection.Kind);
            Assert.Equal("0", scene.Document.Selection.Id);
            Assert.True(scene.Document.History.CanUndo);

            // A collision with another override's live name is rejected before the command's own guard would throw.
            name.Input.SetText("taken");
            name.Update(new Rect(0f, 0f, 200f, 28f), ui, 0.016f);
            Assert.Equal("dense-zone", scene.Document.Doc.ScatterOverrides[0].Name);

            // Clearing to blank is a legal target (Name is optional): the override falls back to its index label.
            name.Input.SetText("");
            name.Update(new Rect(0f, 0f, 200f, 28f), ui, 0.016f);
            Assert.Null(scene.Document.Doc.ScatterOverrides[0].Name);
        }

        [Fact]
        public void ScatterOverrideNode_ShowsTargetingHint()
        {
            var scene = PushDocScene(() => OverrideDoc(
                new MapScatterOverrideDoc { Shape = new DiscShapeDoc { Radius = 5f }, Layers = null },
                new MapScatterOverrideDoc
                {
                    Name = "denser-trees",
                    Shape = new DiscShapeDoc { Radius = 3f },
                    Layers = new List<string> { "trees", "rocks" },
                }));

            TreeNode all = CategoryChild(scene.Outline, "Scatter Overrides", 0);
            TreeNode named = CategoryChild(scene.Outline, "Scatter Overrides", 1);

            // Unnamed falls back to the index label. Named or not, every override carries the targeting hint.
            Assert.Equal("override[0] (all)", all.Label.Resolve());
            Assert.Equal("denser-trees (trees, rocks)", named.Label.Resolve());
        }

        [Fact]
        public void ScatterOverrideLayerRows_AllToggle_NullSemantics()
        {
            var scene = PushDocScene(() => OverrideDoc(
                new MapScatterOverrideDoc { Shape = new DiscShapeDoc { Radius = 5f }, Layers = null }));

            scene.Document.Selection.Set(SelectionKind.ScatterOverride, "0");

            // All-on (Layers null): only the All toggle shows, the per-layer membership rows are hidden.
            Assert.Null(scene.Document.Doc.ScatterOverrides[0].Layers);
            Assert.DoesNotContain(scene.Inspector.Rows.OfType<BoolRow>(), b => b.Label.Resolve() == "trees");

            BoolRow all = BoolRowByLabel(scene.Inspector, "All layers");
            Assert.True(TapBool(all));   // flips All off
            Assert.Equal(new[] { "trees", "rocks" }, scene.Document.Doc.ScatterOverrides[0].Layers);   // materializes the full list
            Assert.True(scene.Document.WorldRebuildPending);

            // The per-layer rows reflow into view the next chrome step (the shape-kind-conversion reflow idiom).
            scene.OnUpdate(0.016f);
            BoolRow trees = BoolRowByLabel(scene.Inspector, "trees");

            Assert.True(TapBool(trees));   // uncheck trees: stays an explicit list, minus "trees"
            Assert.Equal(new[] { "rocks" }, scene.Document.Doc.ScatterOverrides[0].Layers);

            // Manually re-checking every layer stays an explicit list: only the All toggle itself produces null.
            Assert.True(TapBool(trees));
            Assert.NotNull(scene.Document.Doc.ScatterOverrides[0].Layers);
            Assert.Equal(new[] { "rocks", "trees" }, scene.Document.Doc.ScatterOverrides[0].Layers);
        }

        [Fact]
        public void ScatterOverrideLayerRow_TogglesMembership_WorldRebuildPending()
        {
            var scene = PushDocScene(() => OverrideDoc(new MapScatterOverrideDoc
            {
                Shape = new DiscShapeDoc { Radius = 5f },
                Layers = new List<string> { "trees", "rocks" },
            }));

            scene.Document.Selection.Set(SelectionKind.ScatterOverride, "0");
            Assert.False(scene.Document.WorldRebuildPending);

            BoolRow trees = BoolRowByLabel(scene.Inspector, "trees");
            Assert.True(TapBool(trees));

            Assert.Equal(new[] { "rocks" }, scene.Document.Doc.ScatterOverrides[0].Layers);
            Assert.True(scene.Document.WorldRebuildPending);   // override targeting changes the streamed scatter
            Assert.True(scene.Document.History.CanUndo);

            Assert.True(scene.Document.Undo());
            Assert.Equal(new[] { "trees", "rocks" }, scene.Document.Doc.ScatterOverrides[0].Layers);
        }

        [Fact]
        public void OutlineDrop_ReordersScatterOverride_SelectionFollows_HideRemaps_WorldRebuilds()
        {
            var scene = PushDocScene(() => OverrideDoc(DiscOverride(1f), DiscOverride(2f), DiscOverride(3f)));

            // Hide the override about to be moved, so the drop must carry its hide to the new index (not the slot).
            scene.Visibility.SetElementHidden(SelectionKind.ScatterOverride, "0", true);

            TreeView outline = scene.Outline;
            outline.Bounds = new Rect(0f, 0f, 240f, 400f);   // tall enough for every outline row
            TreeNode o0 = CategoryChild(outline, "Scatter Overrides", 0);
            TreeNode o1 = CategoryChild(outline, "Scatter Overrides", 1);

            var input = new InputManager();
            DragTreeRow(outline, input, RowOf(outline, o0), RowOf(outline, o1), afterTarget: true);   // 0 -> after 1

            // Order is significant for overrides (first match wins), so the reorder command lands and rebuilds the world.
            Assert.Equal(2f, ((DiscShapeDoc)scene.Document.Doc.ScatterOverrides[0].Shape!).Radius);   // index 0 is now the old o1 (radius 2)
            Assert.Equal(1f, ((DiscShapeDoc)scene.Document.Doc.ScatterOverrides[1].Shape!).Radius);   // the moved override
            Assert.Equal(3f, ((DiscShapeDoc)scene.Document.Doc.ScatterOverrides[2].Shape!).Radius);
            Assert.Equal(SelectionKind.ScatterOverride, scene.Document.Selection.Kind);
            Assert.Equal("1", scene.Document.Selection.Id);                                 // selection follows the moved override
            Assert.True(scene.Visibility.IsElementHidden(SelectionKind.ScatterOverride, "1"));   // the hide followed it
            Assert.False(scene.Visibility.IsElementHidden(SelectionKind.ScatterOverride, "0"));  // the vacated slot is not hidden
            Assert.True(scene.Document.WorldRebuildPending);
            Assert.True(scene.Document.History.CanUndo);
        }

        [Fact]
        public void CtrlDown_MovesSelectedScatterOverride_SelectionAndHideFollow()
        {
            var scene = PushDocScene(() => OverrideDoc(DiscOverride(1f), DiscOverride(2f)));
            var m = new SceneManager();
            m.Push(scene);

            scene.Visibility.SetElementHidden(SelectionKind.ScatterOverride, "0", true);
            scene.Document.Selection.Set(SelectionKind.ScatterOverride, "0");

            m.Input = CtrlKeyFrame(Key.Down);
            m.Update(0.016f);   // Ctrl+Down: the override moves 0 -> 1 (ReorderSelectedElement, not the outline drop)

            Assert.Equal(2f, ((DiscShapeDoc)scene.Document.Doc.ScatterOverrides[0].Shape!).Radius);   // old o1 now first
            Assert.Equal(1f, ((DiscShapeDoc)scene.Document.Doc.ScatterOverrides[1].Shape!).Radius);   // the moved override
            Assert.Equal("1", scene.Document.Selection.Id);                                 // selection followed it
            Assert.True(scene.Visibility.IsElementHidden(SelectionKind.ScatterOverride, "1"));    // the hide followed it
            Assert.False(scene.Visibility.IsElementHidden(SelectionKind.ScatterOverride, "0"));   // the vacated slot is not hidden
            Assert.True(scene.Document.WorldRebuildPending);
        }

        [Fact]
        public void CtrlUp_AtStart_ScatterOverrideIsNoOp()
        {
            var scene = PushDocScene(() => OverrideDoc(DiscOverride(1f), DiscOverride(2f)));
            var m = new SceneManager();
            m.Push(scene);

            scene.Document.Selection.Set(SelectionKind.ScatterOverride, "0");   // the first override: cannot move earlier

            m.Input = CtrlKeyFrame(Key.Up);
            m.Update(0.016f);

            // Clamped at the start: no reorder command lands and the order is untouched.
            Assert.Equal(1f, ((DiscShapeDoc)scene.Document.Doc.ScatterOverrides[0].Shape!).Radius);
            Assert.Equal(2f, ((DiscShapeDoc)scene.Document.Doc.ScatterOverrides[1].Shape!).Radius);
            Assert.False(scene.Document.History.CanUndo);
        }

        [Fact]
        public void HiddenScatterOverride_SurvivesDeleteOfEarlierIndex()
        {
            // Delete runs through EditorToolController.Update, which UpdateTools gates on a built Field, so this
            // needs FieldDocScene (not the plain DocScene the inspector tests use), mirroring the exclusion delete test.
            var scene = new FieldDocScene(() => OverrideDoc(DiscOverride(1f), DiscOverride(2f), DiscOverride(3f)));
            scene.Init(null!, null!, null!, new MapEditorOptions());
            var m = new SceneManager();
            m.Push(scene);

            scene.Visibility.SetElementHidden(SelectionKind.ScatterOverride, "2", true);
            scene.Document.Selection.Set(SelectionKind.ScatterOverride, "0");   // select the one about to be deleted

            m.Input = KeyFrame(shiftDown: false, Key.Delete);
            m.Update(0.016f);

            Assert.Equal(2, scene.Document.Doc.ScatterOverrides.Count);   // the earlier override was removed
            Assert.True(scene.Visibility.IsElementHidden(SelectionKind.ScatterOverride, "1"));    // the hidden one shifted down to index 1
            Assert.False(scene.Visibility.IsElementHidden(SelectionKind.ScatterOverride, "2"));   // nothing hidden at the old tail slot
        }

        // ---- event-driven hide maintenance across undo / redo / rename --------------------------------------

        [Fact]
        public void HiddenExclusion_ReorderThenUndoRedo_HideFollowsExactlyOnce()
        {
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Exclusions.Add(new MapExclusion { Shape = new DiscShapeDoc { Radius = 1f } });   // 0
                doc.Exclusions.Add(new MapExclusion { Shape = new DiscShapeDoc { Radius = 2f } });   // 1
                doc.Exclusions.Add(new MapExclusion { Shape = new DiscShapeDoc { Radius = 3f } });   // 2: hidden
                return doc;
            });
            scene.Visibility.SetElementHidden(SelectionKind.Exclusion, "2", true);

            TreeView outline = scene.Outline;
            outline.Bounds = new Rect(0f, 0f, 240f, 400f);
            TreeNode e2 = CategoryChild(outline, "Exclusions", 2);
            TreeNode e0 = CategoryChild(outline, "Exclusions", 0);

            var input = new InputManager();
            DragTreeRow(outline, input, RowOf(outline, e2), RowOf(outline, e0), afterTarget: false);   // 2 -> before 0

            // Single remap through the event path: 2 -> 0 exactly once. If the old inline RemapIndex call site still
            // ran alongside the event, the hide would double-shift to index 1, so asserting 0 (and NOT 1) is the
            // single-remap regression guard.
            Assert.True(scene.Visibility.IsElementHidden(SelectionKind.Exclusion, "0"));
            Assert.False(scene.Visibility.IsElementHidden(SelectionKind.Exclusion, "1"));
            Assert.False(scene.Visibility.IsElementHidden(SelectionKind.Exclusion, "2"));

            // Undo the reorder: the inverse remap on CommandUndone walks the hide back to index 2.
            Assert.True(scene.Document.Undo());
            Assert.True(scene.Visibility.IsElementHidden(SelectionKind.Exclusion, "2"));
            Assert.False(scene.Visibility.IsElementHidden(SelectionKind.Exclusion, "0"));

            // Redo repeats the forward remap.
            Assert.True(scene.Document.Redo());
            Assert.True(scene.Visibility.IsElementHidden(SelectionKind.Exclusion, "0"));
            Assert.False(scene.Visibility.IsElementHidden(SelectionKind.Exclusion, "2"));
        }

        [Fact]
        public void HiddenExclusion_DeleteThenUndo_HideShiftsDownAndBack()
        {
            // Delete runs through EditorToolController.Update (gated on a built Field), so this needs FieldDocScene.
            var scene = new FieldDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Exclusions.Add(new MapExclusion { Shape = new DiscShapeDoc { Radius = 1f } });   // 0: deleted
                doc.Exclusions.Add(new MapExclusion { Shape = new DiscShapeDoc { Radius = 2f } });   // 1: hidden
                return doc;
            });
            scene.Init(null!, null!, null!, new MapEditorOptions());
            var m = new SceneManager();
            m.Push(scene);

            scene.Visibility.SetElementHidden(SelectionKind.Exclusion, "1", true);
            scene.Document.Selection.Set(SelectionKind.Exclusion, "0");   // delete the earlier index

            m.Input = KeyFrame(shiftDown: false, Key.Delete);
            m.Update(0.016f);

            Assert.Single(scene.Document.Doc.Exclusions);
            Assert.True(scene.Visibility.IsElementHidden(SelectionKind.Exclusion, "0"));    // hide shifted down 1 -> 0
            Assert.False(scene.Visibility.IsElementHidden(SelectionKind.Exclusion, "1"));

            // Undo the delete: InsertIndex (the inverse) shifts the surviving hide back up to its original index 1.
            Assert.True(scene.Document.Undo());
            Assert.Equal(2, scene.Document.Doc.Exclusions.Count);
            Assert.True(scene.Visibility.IsElementHidden(SelectionKind.Exclusion, "1"));
            Assert.False(scene.Visibility.IsElementHidden(SelectionKind.Exclusion, "0"));
        }

        [Fact]
        public void HiddenPlacement_RenameThenUndoRedo_HideFollowsKey_NoOrphan()
        {
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Placements.Add(new MapPlacement { Id = "a", Kind = "prop", X = 1f, Z = 2f });
                return doc;
            });
            scene.Visibility.SetElementHidden(SelectionKind.Placement, "a", true);

            scene.Document.Execute(new RenamePlacementCommand("a", "b"));

            Assert.True(scene.Visibility.IsElementHidden(SelectionKind.Placement, "b"));    // the hide followed the rename
            Assert.False(scene.Visibility.IsElementHidden(SelectionKind.Placement, "a"));   // no orphan under the old key

            Assert.True(scene.Document.Undo());
            Assert.True(scene.Visibility.IsElementHidden(SelectionKind.Placement, "a"));    // back under the old key
            Assert.False(scene.Visibility.IsElementHidden(SelectionKind.Placement, "b"));   // no orphan under the new key

            Assert.True(scene.Document.Redo());
            Assert.True(scene.Visibility.IsElementHidden(SelectionKind.Placement, "b"));
            Assert.False(scene.Visibility.IsElementHidden(SelectionKind.Placement, "a"));
        }

        [Fact]
        public void HiddenRegion_MergedRenameChain_HideFollowsFinalName_ThenUndo()
        {
            var scene = PushDocScene(() =>
            {
                MapDocument doc = ValidDoc();
                doc.Regions.Add(new MapRegion { Name = "a", Shape = new DiscShapeDoc { Radius = 3f } });
                return doc;
            });
            scene.Visibility.SetElementHidden(SelectionKind.Region, "a", true);

            // A per-keystroke region rename a -> b -> c coalesces into ONE merged command (RenameRegionCommand.TryMerge)
            // whose live _newName is "c". CommandApplied fires on each execute, so the hide walks a -> b -> c.
            scene.Document.Execute(new RenameRegionCommand("a", "b"));
            scene.Document.Execute(new RenameRegionCommand("b", "c"));

            Assert.Equal(1, scene.Document.History.UndoDepth);   // merged into one undo step
            Assert.True(scene.Visibility.IsElementHidden(SelectionKind.Region, "c"));
            Assert.False(scene.Visibility.IsElementHidden(SelectionKind.Region, "a"));
            Assert.False(scene.Visibility.IsElementHidden(SelectionKind.Region, "b"));

            // Undo the merged rename: the command's live effect is Rename(a, c), so the inverse walks c -> a in one hop
            // (no orphan left under b or c).
            Assert.True(scene.Document.Undo());
            Assert.True(scene.Visibility.IsElementHidden(SelectionKind.Region, "a"));
            Assert.False(scene.Visibility.IsElementHidden(SelectionKind.Region, "b"));
            Assert.False(scene.Visibility.IsElementHidden(SelectionKind.Region, "c"));
        }
    }
}
