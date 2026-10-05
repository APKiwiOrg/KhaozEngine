using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Gui;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Primitives;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.MapDoc;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.MapEditor;

public partial class MapEditorSceneTests
{
    sealed class NativeEditScene(MapDocument doc) : MapEditorScene
    {
        public EditorFrameInput Frame;
        public Exception? ToolFailure;
        protected override MapDocument CreateDocument(MapDocRegistry registry) => doc;
        protected override void BuildWorld() => Controller.Field = new TerrainField(new TerrainConfig { GentleAmplitude = 0 });
        protected override void TeardownWorld() { }
        protected override void UpdateTools(float dt)
        {
            if (ToolFailure is not null) throw ToolFailure;
            Controller.Update(Frame);
        }
    }

    sealed class NativeSceneHarness : IDisposable
    {
        public NativePlacementHistoryFixture Fixture { get; } = new();
        public NativeEditScene Scene { get; }
        public SceneManager Manager { get; } = new();
        public NativeSceneHarness()
        {
            Scene = new NativeEditScene(Fixture.Document);
            Scene.Init(null!, null!, null!, new MapEditorOptions());
            Manager.Push(Scene);
            Scene.Document.Selection.Set(SelectionKind.Placement, "existing");
        }
        public void Step(EditorFrameInput frame = default, InputState? keys = null)
        {
            Scene.Frame = frame;
            Manager.Input = keys ?? InputState.Empty;
            Manager.Update(0.016f);
        }
        public void Dispose() { Manager.Pop(); Fixture.Dispose(); }
    }

    static EditorFrameInput NativePress(float x = 30) => new(new Vector3(x, 100, 30), -Vector3.UnitY,
        pointerPressed: true, pointerDown: true, dt: 0.016f);
    static EditorFrameInput NativeDrag(float x = 35) => new(new Vector3(x, 100, 30), -Vector3.UnitY,
        pointerDown: true, dt: 0.016f);

    static void NativeRejected(NativeSceneHarness h, Action action)
    {
        var ed = h.Scene.Document;
        string before = MapDocumentFile.SaveText(ed.Doc);
        string? undo = ed.History.UndoLabel, redo = ed.History.RedoLabel;
        int undoDepth = ed.History.UndoDepth, redoDepth = ed.History.RedoDepth;
        bool dirty = ed.IsDirty, rebuild = ed.WorldRebuildPending;
        string selected = ed.Selection.Id;
        SelectionKind kind = ed.Selection.Kind;
        Vector3 camera = h.Scene.Camera.Position;
        int events = 0;
        void Changed() => events++;
        void Command(IEditorCommand _) => events++;
        ed.DocumentChanged += Changed;
        ed.CommandApplied += Command;
        ed.CommandUndone += Command;
        ed.CommandRedone += Command;
        try { Assert.Null(Record.Exception(action)); }
        finally
        {
            ed.DocumentChanged -= Changed;
            ed.CommandApplied -= Command;
            ed.CommandUndone -= Command;
            ed.CommandRedone -= Command;
        }
        Assert.Contains("rejected", h.Scene.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, MapDocumentFile.SaveText(ed.Doc));
        Assert.Equal((undo, redo, undoDepth, redoDepth, dirty, rebuild),
            (ed.History.UndoLabel, ed.History.RedoLabel, ed.History.UndoDepth, ed.History.RedoDepth, ed.IsDirty, ed.WorldRebuildPending));
        Assert.Equal((kind, selected), (ed.Selection.Kind, ed.Selection.Id));
        Assert.Equal(camera, h.Scene.Camera.Position);
        Assert.Equal(0, events);
        Assert.False(h.Scene.Controller.IsDragging);
        Assert.False(h.Scene.Controller.IsDrawing);
        Assert.False(h.Scene.Controller.IsSculpting);
        h.Step();
    }

    [Fact]
    public void NativeRejection_PlaceFrameShowsReasonAndViewOnlyStillWorks()
    {
        using var h = new NativeSceneHarness();
        h.Scene.Controller.Mode = EditorToolMode.PlacePlacement;
        h.Scene.Controller.PlaceKind = "prop";
        NativeRejected(h, () => h.Step(NativePress()));
        Assert.Contains("binding", h.Scene.StatusText);
        Assert.True(TapBool(BoolRowByLabel(h.Scene.Inspector, "Visible")));
        Assert.True(h.Scene.Visibility.IsElementHidden(SelectionKind.Placement, "existing"));
        Assert.False(h.Scene.Document.IsDirty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeRejection_DeleteAndDuplicateDoNotLoseSelectionOrShowSuccess(bool duplicate)
    {
        using var h = new NativeSceneHarness();
        NativeRejected(h, () => h.Step(new EditorFrameInput(default, default, deletePressed: !duplicate),
            duplicate ? CtrlKeyFrame(Key.D) : InputState.Empty));
        Assert.DoesNotContain("Duplicated", h.Scene.StatusText);
    }

    [Fact]
    public void NativeRejection_LabelWidgetShowsReasonAndValidBoundRetryWorks()
    {
        using var h = new NativeSceneHarness();
        var row = TextRowByLabel(h.Scene.Inspector, "Label");
        var input = new InputManager();
        input.Update(InputState.Empty);
        void Edit(string value)
        {
            row.Input.IsFocused = true;
            row.Input.SetText(value);
            row.Update(new Rect(0, 0, 200, 28), input, 0.016f);
        }
        NativeRejected(h, () => Edit("rejected-label"));
        row = TextRowByLabel(h.Scene.Inspector, "Label");
        Assert.Equal("", row.Input.Text);
        h.Scene.Document.BindNativeAssets(h.Fixture.Assets);
        Edit("accepted-label");
        Assert.Equal("accepted-label", h.Fixture.Document.Placements.Single().DisplayName);
        Assert.Equal(10, h.Fixture.Document.Placements.Single().NumericId);
        Assert.Equal("existing", h.Scene.Document.Selection.Id);
    }

    [Fact]
    public void NativeRejection_TransformWidgetAndUnsupportedTerrainWidgetRemainAtomic()
    {
        using var h = new NativeSceneHarness();
        void Scrub(FloatRow row)
        {
            var ui = new InputManager();
            var cell = new Rect(0, 0, 200, 28);
            ui.Update(MouseFrame(new Vector2(100, 10), false)); row.Update(cell, ui, 0.016f);
            ui.Update(MouseFrame(new Vector2(100, 10), true)); row.Update(cell, ui, 0.016f);
            ui.Update(MouseFrame(new Vector2(200, 10), true)); row.Update(cell, ui, 0.016f);
        }
        NativeRejected(h, () => Scrub(FloatRowByLabel(h.Scene.Inspector, "X")));
        Assert.Equal(0, FloatRowByLabel(h.Scene.Inspector, "X").Field.Value);
        h.Scene.Document.BindNativeAssets(h.Fixture.Assets);
        h.Scene.Document.Selection.Set(SelectionKind.Terrain, "");
        NativeRejected(h, () => Scrub(FloatRowByLabel(h.Scene.Inspector, "WaterLevel")));
        Assert.Contains("does not support", h.Scene.StatusText);
    }

    [Fact]
    public void NativeRejection_UnsupportedDrawCancelsGesture()
    {
        using var h = new NativeSceneHarness();
        h.Scene.Document.BindNativeAssets(h.Fixture.Assets);
        h.Scene.Controller.Mode = EditorToolMode.DrawExclusion;
        h.Step(NativePress());
        Assert.True(h.Scene.Controller.IsDrawing);
        NativeRejected(h, () => h.Step(new EditorFrameInput(new Vector3(40, 100, 40), -Vector3.UnitY, pointerReleased: true)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeRejection_UndoAndRedoCollisionRetainHistory(bool redo)
    {
        using var h = new NativeSceneHarness();
        var ed = h.Scene.Document;
        ed.BindNativeAssets(h.Fixture.Assets);
        if (redo)
        {
            ed.Execute(new AddPlacementCommand(h.Fixture.NewProp("a")));
            ed.Undo();
            ed.Doc.Placements.Add(h.Fixture.NewProp("a"));
        }
        else
        {
            ed.Execute(new RemovePlacementCommand("existing"));
            ed.Doc.Placements.Add(h.Fixture.NewProp("existing"));
        }
        NativeRejected(h, () => h.Step(keys: CtrlKeyFrame(redo ? Key.Y : Key.Z)));
    }

    [Fact]
    public void NativeRejection_ExhaustionAndMidPlaceFailureDoNotLeaveHeldGesture()
    {
        using var h = new NativeSceneHarness();
        var ed = h.Scene.Document;
        ed.BindNativeAssets(h.Fixture.Assets);
        h.Scene.Controller.Mode = EditorToolMode.PlacePlacement;
        h.Scene.Controller.PlaceKind = "prop";
        h.Step(NativePress());
        var root = ed.Doc.NativeAssets[0];
        ed.Doc.NativeAssets.Clear();
        NativeRejected(h, () => h.Step(NativeDrag()));
        ed.Doc.NativeAssets.Add(root);
        string before = MapDocumentFile.SaveText(ed.Doc);
        h.Step(NativeDrag(50));
        Assert.Equal(before, MapDocumentFile.SaveText(ed.Doc));
        ed.Doc.NumericIdHighWaterMark = long.MaxValue;
        NativeRejected(h, () => h.Step(NativePress(60)));
    }

    [Fact]
    public void NativeRejection_UnexpectedFailureStillEscapes()
    {
        using var h = new NativeSceneHarness();
        h.Scene.ToolFailure = new InvalidOperationException("unexpected tool bug");
        Assert.Throws<InvalidOperationException>(() => h.Step());
    }
    [Fact]
    public void NativeRejection_RealToolbarAndWidgetDispatchKeepViewActionsAvailable()
    {
        using var h = new NativeSceneHarness();
        h.Manager.UiViewport = new UiViewport(960, 540, 960, 540);
        h.Step();
        void Tap(Rect rect)
        {
            var at = new Vector2(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
            h.Step(keys: MouseFrame(at, false));
            h.Step(keys: MouseFrame(at, true));
            h.Step(keys: MouseFrame(at, false));
        }
        Tap(h.Scene.Toolbar.TabRect((int)EditorToolMode.PlacePlacement));
        Assert.Equal(EditorToolMode.PlacePlacement, h.Scene.Controller.Mode);
        h.Scene.Controller.PlaceKind = "prop";
        NativeRejected(h, () => h.Step(NativePress()));
        Tap(h.Scene.Toolbar.TabRect((int)EditorToolMode.Select));
        Assert.Equal(EditorToolMode.Select, h.Scene.Controller.Mode);
        h.Scene.Document.Doc.Spawns.Add(new MapSpawn { Id = "spawn", ArchetypeId = "wolf" });
        h.Scene.Document.Selection.Set(SelectionKind.Spawn, "spawn");
        h.Step();
        var grid = h.Scene.Inspector;
        int enabled = grid.Rows.IndexOf(BoolRowByLabel(grid, "Enabled"));
        Rect cell = grid.RowEditorBounds(enabled);
        var click = new Vector2(cell.X + cell.Width / 2, cell.Y + cell.Height / 2);
        h.Step(keys: MouseFrame(click, false));
        h.Step(keys: MouseFrame(click, true));
        NativeRejected(h, () => h.Step(keys: MouseFrame(click, false)));
        Assert.True(h.Scene.Document.Doc.Spawns[0].Enabled);
        int visible = grid.Rows.IndexOf(BoolRowByLabel(grid, "Visible"));
        Tap(grid.RowEditorBounds(visible));
        Assert.True(h.Scene.Visibility.IsElementHidden(SelectionKind.Spawn, "spawn"));
        Assert.False(h.Scene.Document.IsDirty);
    }

}
