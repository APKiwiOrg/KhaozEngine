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

        // Bare Escape, with no exit dialog and no settings menu open (OnUpdate gates the whole editor step off
        // each), no editor field focused (the guard above), and nothing for the tool layer to cancel: open the
        // settings menu. _toolOwnsEscape is sampled in OnUpdate BEFORE the tool step, because the tool step has
        // already cancelled and reset to Select by the time this runs, so reading the controller here would let a
        // gesture-cancelling Escape also pop the menu open. Returns either way, so an Escape that cancelled a
        // gesture is consumed and never falls through to a bookmark chord.
        // A held command modifier keeps Escape out of this branch, so the menu is strictly the BARE chord.
        if (!s.IsCommandDown && s.WasPressed(Key.Escape))
        {
            if (!_toolOwnsEscape) OpenSettingsDialog();
            return;
        }

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
