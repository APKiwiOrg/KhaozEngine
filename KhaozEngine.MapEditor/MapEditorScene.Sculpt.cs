using System;
using KhaozEngine.App;
using KhaozEngine.Gui;

namespace KhaozEngine.MapEditor;

// The terrain-sculpt tool's editor chrome: the toolbar tab label (kept here alongside the whole ToolLabels set so
// the sculpt tab lives with the sculpt code) and the inspector brush panel. The stroke behaviour is on the
// controller (EditorToolSculpt.cs); this is the GUI seam that drives the brush parameters and shows them.
public partial class MapEditorScene
{
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
