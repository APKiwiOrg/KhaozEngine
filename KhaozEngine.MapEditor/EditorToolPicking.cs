using System;

namespace KhaozEngine.MapEditor;

public sealed partial class EditorToolController
{
    // ---- viewport pick filters ---------------------------------------------------------------------------

    /// <summary>Whether an element (kind, id) is pickable from the viewport, consulted by the Select-mode pick so a
    /// hidden element cannot be clicked (it is still selectable from the outline, which does not go through here).
    /// Defaults to everything pickable, and the scene points it at its <see cref="EditorVisibility.IsElementVisible"/>.
    /// The pick calls it once per element, so it must be constant-time: resolve nothing from the document here.</summary>
    public Func<SelectionKind, string, bool> IsVisible { get; set; } = static (_, _) => true;

    /// <summary>Whether placements of a kit id are pickable, the category half of the pick filter (a hidden Trees
    /// or Rocks category). Keyed on the kit so the pick reads it from the placement it already holds. Defaults to
    /// every kit pickable. Constant-time for the same reason as <see cref="IsVisible"/>.</summary>
    public Func<string, bool> PlacementKindVisible { get; set; } = static _ => true;

    // A press that grabbed no gizmo handle: pick a placement / spawn first, else fall through to the overlay shapes
    // under the ground point (exclusions, regions, feature markers), so those otherwise-invisible authoring shapes
    // are selectable with the mouse. A pick that finds nothing at all clears the selection.
    void PickSelection(in EditorFrameInput input)
    {
        if (!EditorPicking.Pick(_document.Doc, Field!, input.RayOrigin, input.RayDirection, PickDistance, HeightOf,
                out EditorPicking.PickResult r, IsVisible, PlacementKindVisible))
        {
            _document.Selection.Clear();
            return;
        }

        if (r.Kind != SelectionKind.None)
            _document.Selection.Set(r.Kind, r.Id);
        else if (OverlayPicking.Pick(_document.Doc, r.Point.X, r.Point.Z, out OverlayPicking.OverlayPickResult o, IsVisible))
            _document.Selection.Set(o.Kind, o.Id);
        else
            _document.Selection.Clear();
    }

    // The current selection passes both pick filters. One element, so the id lookup for a placement's kit is fine
    // here, unlike inside the pick loop.
    bool SelectionVisible(EditorSelection selection)
    {
        if (!IsVisible(selection.Kind, selection.Id)) return false;
        return selection.Kind != SelectionKind.Placement
            || FindPlacement(selection.Id) is not { } placement
            || PlacementKindVisible(placement.Kind);
    }
}
