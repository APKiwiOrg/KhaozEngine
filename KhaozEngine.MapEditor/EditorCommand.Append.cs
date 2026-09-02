using System;
using System.Collections.Generic;
using KhaozEngine.MapDoc;

namespace KhaozEngine.MapEditor;

// The append slot shared by the whole Add*Command family. It lives here rather than as a field repeated in
// every Add command so the family has ONE idiom and ONE explanation, and so a new Add command joins it by
// calling the pair below instead of by remembering to declare an index field of its own (#76).
public abstract partial class EditorCommand
{
    // Why the slot exists at all, and why Revert never removes by reference.
    //
    // The MapDoc element types carry no Equals override, so List.Remove compares by reference. That makes a
    // reference-based Revert wrong in two ways:
    //
    //   1. It removes the FIRST reference-equal match, not the slot Apply appended. Re-adding an element the
    //      document already holds earlier in the list therefore strips the wrong occurrence and leaves the
    //      list reordered rather than restored.
    //   2. It silently no-ops once the appended instance is no longer the one sitting in the slot.
    //      EditScatterOverrideValuesCommand deep-clones both the new and the old value it is given, so
    //      undoing an edit puts a CLONE into the slot. The paired Add's Remove then found nothing and
    //      stranded that clone in the document while History.UndoDepth == 0 and IsDirty == false both still
    //      passed over it (#24). Only that one Edit command clones internally today, which is why #24 was the
    //      only live case, but the reference shape re-arms the same corruption for any Add whose paired Edit
    //      later adopts the clone-internally pattern.
    //
    // Add always appends, so the slot is the list's length at Apply time. Under LIFO undo everything added
    // after this command is already reverted by the time its Revert runs, so the captured slot is still the
    // command's own element then. Redo re-runs Apply and recaptures it.
    int _appendSlot = -1;

    /// <summary>Appends <paramref name="item"/> to <paramref name="list"/> and captures the slot it landed in
    /// for <see cref="RevertAppend"/>. The Add*Command <see cref="Apply"/> idiom.</summary>
    private protected void ApplyAppend<T>(List<T> list, T item)
    {
        _appendSlot = list.Count;
        list.Add(item);
    }

    /// <summary>Removes the slot <see cref="ApplyAppend"/> captured, by index rather than by reference. The
    /// Add*Command <see cref="Revert"/> idiom.</summary>
    private protected void RevertAppend<T>(List<T> list)
    {
        if (_appendSlot < 0) throw new InvalidOperationException("Revert called before Apply.");
        list.RemoveAt(_appendSlot);
    }

    // The guarded half of the same idiom, for the three id-keyed Add commands (#766, the shape #75 closed for
    // regions). Each guard already existed with exactly one caller, the matching rename command, so an add could
    // land a duplicate id that only save-time validation ever reported, long after the gesture that caused it.
    //
    // The pairs live HERE rather than as two lines inside each Apply for the same reason the append slot does:
    // one idiom, one explanation, and a new Add command joins it by calling the pair. It also keeps
    // EditorCommands.cs, which sits at its file-size ratchet, at one line per Apply.
    //
    // Guard BEFORE the append, never after: History.Execute applies before it pushes, so a throwing Apply lands
    // no undo step and leaves the document exactly as it was. A redo re-applies against a document its own undo
    // has already emptied of the element, so the guard cannot see the command's own id.

    /// <summary>Rejects a duplicate placement id, then appends. The guarded <see cref="ApplyAppend"/>.</summary>
    private protected void ApplyAppendUnique(MapDocument doc, MapPlacement placement)
    {
        GuardNoPlacement(doc, placement.Id);
        ApplyAppend(doc.Placements, placement);
    }

    /// <summary>Rejects a duplicate spawn id, then appends. The guarded <see cref="ApplyAppend"/>.</summary>
    private protected void ApplyAppendUnique(MapDocument doc, MapSpawn spawn)
    {
        GuardNoSpawn(doc, spawn.Id);
        ApplyAppend(doc.Spawns, spawn);
    }

    /// <summary>Rejects a duplicate player spawn id, then appends. The guarded <see cref="ApplyAppend"/>.</summary>
    private protected void ApplyAppendUnique(MapDocument doc, MapPlayerSpawn spawn)
    {
        GuardNoPlayerSpawn(doc, spawn.Id);
        ApplyAppend(doc.PlayerSpawns, spawn);
    }
}
