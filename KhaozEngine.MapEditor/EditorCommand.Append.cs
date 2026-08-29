using System;
using System.Collections.Generic;

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
}
