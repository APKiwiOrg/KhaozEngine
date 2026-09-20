using System;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.MapDoc;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;

namespace KhaozEngine.MapEditor;

// The terrain-sculpt tool's editor chrome: the toolbar tab label (kept here alongside the whole ToolLabels set so
// the sculpt tab lives with the sculpt code) and the inspector brush panel. The stroke behaviour is on the
// controller (EditorToolSculpt.cs); this is the GUI seam that drives the brush parameters and shows them.
public partial class MapEditorScene
{
    static readonly Color SculptRaiseColor = new(1f, 0.84f, 0.2f, 1f);
    static readonly Color SculptLowerColor = new(0.25f, 0.78f, 1f, 1f);
    static readonly Color SculptSmoothColor = new(0.75f, 0.45f, 1f, 1f);
    static readonly Color SculptFlattenColor = new(0.35f, 0.95f, 0.52f, 1f);
    static readonly Color SculptSetHeightColor = new(1f, 0.55f, 0.22f, 1f);
    static readonly Color SculptInvalidColor = new(1f, 0.35f, 0.3f, 1f);
    const float SculptLabelPadX = 7f;
    const float SculptLabelPadY = 4f;
    const float SculptLabelOffset = 14f;

    readonly SculptOverlayLine[] _sculptOverlayLines = new SculptOverlayLine[SculptBrushOverlay.MaxLines];
    SculptOverlayFrame _sculptOverlayFrame;

    // Order-locked to the EditorToolMode enum: the toolbar reads back through (EditorToolMode)ActiveIndex, so a new
    // label appends LAST alongside the enum's own last member, never inserts. Sculpt is the last tab.
    static readonly LocalizedText[] ToolLabels =
    {
        LocalizedText.Raw("Select"), LocalizedText.Raw("Prop"), LocalizedText.Raw("Spawn"),
        LocalizedText.Raw("Exclude"), LocalizedText.Raw("Region"), LocalizedText.Raw("Feature"),
        LocalizedText.Raw("Bake"), LocalizedText.Raw("Override"), LocalizedText.Raw("Sculpt"),
    };

    // The brush selector's labels, in SculptBrush order (index == (int)brush), so the dropdown maps to the enum by
    // position. Raw dev-tool text (the editor is not player-facing), no em / en dashes or semicolons.
    static readonly string[] SculptBrushLabels = { "Raise", "Lower", "Smooth", "Flatten", "Set height" };

    // True while the sculpt tool is active, so RebuildInspector shows the brush panel instead of a selection panel.
    bool SculptMode => _controller is not null && _controller.Mode == EditorToolMode.SculptTerrain;

    void DrawSculptCursor(Scene3D scene)
    {
        _sculptOverlayFrame = SculptOverlayFrame.Hidden;
        if (!SculptMode || Manager is null) return;
        bool overViewport = Manager.Input.Width > 0 && Manager.Input.Height > 0
            && !IsOverChrome(Manager.Input.MousePosition);
        float cellSize = _document.Doc.TerrainOverrides?.CellSize ?? MapTerrainOverrides.DefaultCellSize;
        MapBounds docBounds = _document.Doc.Bounds;
        SculptBounds bounds = SculptBounds.FromBounds(
            docBounds.MinX, docBounds.MinZ, docBounds.MaxX, docBounds.MaxZ, cellSize);
        int count = SculptCursor.Build(_controller, BuildFrameInput(0f), bounds, cellSize,
            overViewport, NavigationOwnsPointer, _exitDialog is not null || _settingsDialog is not null,
            _viewport.IsTerrainLoaded, SculptMarkerHalfSizeFor,
            _viewport.IsTerrainSegmentLoaded,
            _sculptOverlayLines, out _sculptOverlayFrame);
        SculptBrushOverlay.Draw(scene, _sculptOverlayLines, count,
            SculptOperationColor(_controller.Brush, _sculptOverlayFrame.State));
    }

    void DrawSculptOverlayLabel(SpriteBatch batch, SpriteFont font, IDesignViewport viewport)
    {
        if (!_sculptOverlayFrame.Visible || Manager is null) return;
        Vector2 anchor;
        if (_sculptOverlayFrame.HasWorldAnchor)
        {
            if (!((IIsoCamera3D)_camera).WorldToScreen(_sculptOverlayFrame.Center, viewport, out anchor)) return;
        }
        else
        {
            anchor = viewport.ScreenToDesign(Manager.Input.MousePosition);
        }

        string text = MapEditorStrings.Resolve(_sculptOverlayFrame.OperationLabel) + ": "
            + MapEditorStrings.Resolve(_sculptOverlayFrame.StateLabel);
        Vector2 measured = font.Measure(text);
        var box = new Rect(
            MathF.Floor(anchor.X - measured.X * 0.5f - SculptLabelPadX),
            MathF.Floor(anchor.Y + SculptLabelOffset),
            measured.X + SculptLabelPadX * 2f,
            font.LineHeight + SculptLabelPadY * 2f);
        batch.DrawRounded(_white, new Vector4(box.X, box.Y, box.Width, box.Height),
            new Color(0.04f, 0.045f, 0.065f, 0.92f), 4f);
        batch.DrawString(font, text,
            new Vector2(box.X + SculptLabelPadX, box.Y + SculptLabelPadY),
            SculptOperationColor(_controller.Brush, _sculptOverlayFrame.State));
    }

    internal static Color SculptOperationColor(SculptBrush brush, SculptOverlayState state)
    {
        if (state == SculptOverlayState.Invalid) return SculptInvalidColor;
        Color color = brush switch
        {
            SculptBrush.Raise => SculptRaiseColor,
            SculptBrush.Lower => SculptLowerColor,
            SculptBrush.Smooth => SculptSmoothColor,
            SculptBrush.Flatten => SculptFlattenColor,
            SculptBrush.SetHeight => SculptSetHeightColor,
            _ => SculptRaiseColor,
        };
        return state == SculptOverlayState.Active ? color.ScaleRgbClamped(1.25f) : color;
    }

    float SculptMarkerHalfSizeFor(Vector3 center) =>
        SculptBrushOverlay.ScreenMarkerHalfSize(Vector3.Distance(_camera.Position, center));

    // The sculpt-mode inspector: the brush op, radius, strength, and the set-height target. These edit the tool's
    // brush parameters directly (not the document), so they are plain rows with no undo gesture. The stroke itself
    // is the undoable edit.
    void BuildSculptInspector()
    {
        _inspector.Rows.Add(new HeaderRow(LocalizedText.Raw("Terrain Sculpt"), LocalizedText.Raw(
            "Drag on the terrain to sculpt authored height deltas over the procedural base. A press-drag-release " +
            "stroke is one undo step. The footprint is clamped to the document bounds.")));

        _inspector.Rows.Add(new ChoiceRow(LocalizedText.Raw("Brush"), SculptBrushLabels,
            () => SculptBrushLabels[(int)_controller.Brush],
            label => { int i = Array.IndexOf(SculptBrushLabels, label); if (i >= 0) _controller.Brush = (SculptBrush)i; },
            LocalizedText.Raw("Raise and lower add or remove height. Smooth blends toward the neighbourhood mean. " +
                "Flatten blends toward the height under the first press. Set height blends toward the Set height " +
                "value below.")));

        _inspector.Rows.Add(new FloatRow(LocalizedText.Raw("Radius"),
            () => _controller.BrushRadius, v => _controller.BrushRadius = v,
            min: EditorToolController.MinBrushRadius, max: 256f, dragScale: 0.25f, decimals: 1,
            description: LocalizedText.Raw("Brush footprint radius in world units.")));

        _inspector.Rows.Add(new FloatRow(LocalizedText.Raw("Strength"),
            () => _controller.BrushStrength, v => _controller.BrushStrength = v,
            min: 0f, max: 100f, dragScale: 0.1f, decimals: 2,
            description: LocalizedText.Raw("Meters per stroke-second for raise and lower, and a per-second blend " +
                "rate toward the target for smooth, flatten, and set height. Hold the stroke to build up.")));

        _inspector.Rows.Add(new FloatRow(LocalizedText.Raw("Set height"),
            () => _controller.SetHeight, v => _controller.SetHeight = v,
            dragScale: 0.1f, decimals: 2,
            description: LocalizedText.Raw("The absolute world height the Set height brush blends the surface toward.")));
    }
}
