using System.Collections.Generic;
using KhaozEngine.MapDoc;

namespace KhaozEngine.MapEditor;

/// <summary>Undo/redo stack with gesture coalescing via <see cref="IEditorCommand.TryMerge"/>. The engine's
/// first command stack: <see cref="Execute"/> applies a command and pushes it (clearing redo), and a newer
/// command of the same gesture is absorbed by the one on top instead of pushing a second step. A merge barrier
/// after any undo or redo guarantees the next edit starts a fresh step rather than coalescing into a
/// reactivated one, and <see cref="SealGesture"/> raises the same barrier at explicit gesture boundaries
/// (drag release, focus loss, save).</summary>
public sealed class EditorHistory
{
    readonly List<IEditorCommand> _undo = new();
    readonly List<IEditorCommand> _redo = new();
    bool _mergeBarrier;

    /// <summary>True when there is a command to undo.</summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>True when there is a command to redo.</summary>
    public bool CanRedo => _redo.Count > 0;

    /// <summary>The label of the command that would be undone next, or null when the undo stack is empty.</summary>
    public string? UndoLabel => _undo.Count > 0 ? _undo[^1].Label : null;

    /// <summary>The label of the command that would be redone next, or null when the redo stack is empty.</summary>
    public string? RedoLabel => _redo.Count > 0 ? _redo[^1].Label : null;

    /// <summary>Number of committed undo steps. Used by <see cref="EditorDocument"/> as the saved-point marker.</summary>
    internal int UndoDepth => _undo.Count;

    /// <summary>Number of steps available to redo.</summary>
    internal int RedoDepth => _redo.Count;

    /// <summary>The command that <see cref="Undo"/> would revert next, or null when empty.</summary>
    internal IEditorCommand? PeekUndo() => _undo.Count > 0 ? _undo[^1] : null;

    /// <summary>The command that <see cref="Redo"/> would reapply next, or null when empty.</summary>
    internal IEditorCommand? PeekRedo() => _redo.Count > 0 ? _redo[^1] : null;

    /// <summary>Marks the end of the current gesture: the NEXT <see cref="Execute"/> never merges into the
    /// current top command (the same barrier mechanism Undo/Redo already use). Call on gesture end (drag
    /// release, focus loss). Idempotent.</summary>
    public void SealGesture() => _mergeBarrier = true;

    /// <summary>Applies <paramref name="command"/> to the document and records it, clearing the redo stack.
    /// When the command coalesces into the current top (same gesture, no barrier), no new step is pushed.</summary>
    public void Execute(MapDocument doc, IEditorCommand command)
    {
        command.Apply(doc);
        if (_mergeBarrier || _undo.Count == 0 || !_undo[^1].TryMerge(command))
            _undo.Add(command);
        _mergeBarrier = false;
        _redo.Clear();
    }

    /// <summary>Reverts the top command and moves it to the redo stack. Returns false when nothing to undo.</summary>
    public bool Undo(MapDocument doc)
    {
        if (_undo.Count == 0) return false;
        IEditorCommand c = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        c.Revert(doc);
        _redo.Add(c);
        _mergeBarrier = true;
        return true;
    }

    /// <summary>Reapplies the top redo command and moves it back to the undo stack. Returns false when
    /// nothing to redo.</summary>
    public bool Redo(MapDocument doc)
    {
        if (_redo.Count == 0) return false;
        IEditorCommand c = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        c.Apply(doc);
        _undo.Add(c);
        _mergeBarrier = true;
        return true;
    }
}
