using System;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.MapDoc;

namespace KhaozEngine.MapEditor;

public partial class MapEditorScene
{
    bool _rejectedInspectorRefresh;

    // Catch at the whole action, not Execute: callbacks may select a new ID or report success after Execute.
    // Native validation errors and exhausted numeric allocation are expected refusals. Other failures escape.
    bool RunNativeEditAction(Action action)
    {
        SelectionKind kind = _document.Selection.Kind;
        string id = _document.Selection.Id;
        string? pending = _pendingSelectId;
        SelectionKind pendingKind = _pendingSelectKind;
        EditorToolMode mode = _controller.Mode;
        try
        {
            action();
            return true;
        }
        catch (Exception ex) when (_document.Doc.ResolverIdentity is not null &&
            (ex is MapDocumentException || (ex is OverflowException && _document.Doc.NumericIdHighWaterMark == long.MaxValue)))
        {
            _controller.CancelRejectedGesture(mode);
            _pendingSelectId = pending;
            _pendingSelectKind = pendingKind;
            if (_document.Selection.Kind != kind || _document.Selection.Id != id)
                _document.Selection.Set(kind, id);
            _statusText = "Edit rejected: " + ex.Message;
            _rejectedInspectorRefresh = true;
            return false;
        }
    }

    // Run after widget iteration has unwound. Recreate rows from committed values, discarding rejected drafts.
    void RefreshRejectedInspector()
    {
        if (!_rejectedInspectorRefresh) return;
        _rejectedInspectorRefresh = false;
        RebuildInspector();
    }

    // The single spot every FloatRow the inspector builds funnels through, directly or via a domain wrapper
    // (AddFeatureRow, AddBandFloatRow, AddScatterFloatRow, AddCompanionFloatRow, AddShapeRow): wires
    // FloatRow.GestureEnded to SealGesture so a scrub or edit commit on this row seals the undo gesture the
    // moment it ends. Without this, scrubbing two different fields back to back (e.g. WaterLevel then
    // BiomeBlend) can coalesce into ONE undo step through the underlying command's same-gesture TryMerge
    // (EditTerrainCommand merges ANY two terrain edits, by design, within one gesture) - sealing here draws the
    // gesture boundary at the widget level so each field's scrub becomes its own undo step. Same signature as
    // the FloatRow constructor, so every existing call site converts by dropping "_inspector.Rows.Add(new
    // FloatRow(" down to "AddFloatRow(".
    FloatRow AddFloatRow(LocalizedText label, Func<float> get, Action<float> set,
        float min = float.MinValue, float max = float.MaxValue, float dragScale = 0.01f, int decimals = 2,
        LocalizedText? description = null)
    {
        var row = new FloatRow(label, get, value => RunNativeEditAction(() => set(value)), min, max, dragScale, decimals, description);
        row.GestureEnded += _document.SealGesture;
        _inspector.Rows.Add(row);
        return row;
    }
}
