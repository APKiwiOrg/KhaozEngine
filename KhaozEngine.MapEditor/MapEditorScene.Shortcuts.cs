using KhaozEngine.Windowing;

namespace KhaozEngine.MapEditor;

public partial class MapEditorScene
{
    void HandleShortcuts()
    {
        InputState s = Manager!.Input;
        bool shift = s.IsDown(Key.LeftShift) || s.IsDown(Key.RightShift);
        // Shift+Escape opens the modal exit dialog (OpenExitDialog). It deliberately stays global here, even while
        // an inspector field or a filter is focused: it is the one chord a user needs to be able to reach FROM
        // inside a field (leave the editor). NumberField's own bare-Escape cancel never watches the Shift-modified
        // form, and TextInput (both the inspector's TextRow and the palette/spawn filters) has no Escape handling
        // of its own at all, so neither can ever compete with this chord for the same keypress. HandleShortcuts
        // never runs while the dialog is already open (OnUpdate gates the whole editor step off _exitDialog), so
        // this only ever opens a fresh dialog, never re-opens one.
        if (shift && s.WasPressed(Key.Escape)) { OpenExitDialog(); return; }

        // Every other chord below, plus the bare R hotkey, belongs to a focused editor over the document:
        // Ctrl+Z inside a focused NumberField should undo the field's own typed digit (TextEntry already blocks
        // the literal keystroke), not pop a document command, and R should type into a focused name field or
        // filter instead of snapping the selection to the ground. This aggregate query replaces the old ad hoc
        // `_nameRow`-only guard, so every row type (Float/Text/Choice) AND the kit-palette/spawn filters block
        // chords, not just the rename row.
        if (AnyEditorFocused) return;

        bool ctrl = s.IsCommandDown;
        if (!ctrl)
        {
            if (s.WasPressed(Key.R)) SnapSelectedPlacementToGround();
            else HandleBookmarkChord(s, shift);   // bare / Shift+1..9 (decision 9)
            return;
        }
        if (s.WasPressed(Key.Z)) { if (shift) _document.Redo(); else _document.Undo(); }
        else if (s.WasPressed(Key.Y)) _document.Redo();
        else if (s.WasPressed(Key.S)) SaveDocument();
        else if (s.WasPressed(Key.D)) DuplicateSelectionChord();       // Cmd+D (decision 8)
        else if (shift && s.WasPressed(Key.F)) FreezeZoneChord();      // Cmd+Shift+F (whole-zone scatter freeze)
        else if (s.WasPressed(Key.Up)) ReorderSelectedElement(-1);     // earlier in the fold / match order
        else if (s.WasPressed(Key.Down)) ReorderSelectedElement(+1);   // later in the fold / match order (toward winning)
    }

    // Cmd+Shift+F: freeze the whole zone's procedural scatter into authored placements and strip every scatter
    // layer, companion layer, exclusion, and override, as one undoable command (EditorToolController.FreezeZone).
    // A document with no scatter or companion layers has nothing to freeze, so the controller returns null and this
    // lands a status note instead of a phantom undo entry (the DuplicateSelectionChord no-op idiom). The frozen
    // document is placements-only, so a redo of the world stream draws the baked props with no re-scatter.
    void FreezeZoneChord()
    {
        if (_controller.FreezeZone() is not { } r)
        {
            _statusText = "Nothing to freeze: the zone has no scatter or companion layers.";
            return;
        }
        _statusText = $"Froze zone: {r.PlacementCount} placements, removed {r.ScatterLayersRemoved} scatter and "
            + $"{r.CompanionLayersRemoved} companion layers, {r.ExclusionsRemoved} exclusions, "
            + $"{r.ScatterOverridesRemoved} overrides.";
    }
}
