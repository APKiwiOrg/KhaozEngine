using System;
using System.Collections.Generic;
using System.IO;
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

        static FloatRow FloatRowByLabel(PropertyGrid grid, string label)
        {
            foreach (PropertyRow row in grid.Rows)
                if (row is FloatRow f && f.Label.Resolve() == label) return f;
            Assert.Fail($"no FloatRow labeled '{label}' (rows: {grid.Rows.Count})");
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
    }
}
