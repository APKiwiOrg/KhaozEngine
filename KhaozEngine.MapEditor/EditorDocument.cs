using System;
using KhaozEngine.MapDoc;

namespace KhaozEngine.MapEditor;

/// <summary>The open document plus editor state: dirty tracking, selection, world-rebuild signalling, and the
/// mutation choke point (<see cref="Execute"/> routes through <see cref="EditorHistory"/>). Undo/redo are
/// mediated here too, so <see cref="DocumentChanged"/> and <see cref="WorldRebuildPending"/> stay correct for
/// every mutation path. Selection is by stable id string plus a kind discriminator, resilient to list
/// reordering.</summary>
public sealed class EditorDocument
{
    /// <summary>Sentinel saved-point marker meaning "the saved state is no longer reachable" (its history
    /// branch was discarded by a later edit). Undo depth is always non-negative, so this never matches.</summary>
    const int Unreachable = -1;

    int _savedMarker;

    /// <summary>Creates an editor document over <paramref name="doc"/>, defaulting the feature registry to
    /// <see cref="MapDocRegistry.CreateDefault"/> when none is supplied.</summary>
    public EditorDocument(MapDocument doc, MapDocRegistry? registry = null)
    {
        Doc = doc ?? throw new ArgumentNullException(nameof(doc));
        Registry = registry ?? MapDocRegistry.CreateDefault();
    }

    /// <summary>The document being edited.</summary>
    public MapDocument Doc { get; }

    /// <summary>The feature registry used to interpret and serialize the document.</summary>
    public MapDocRegistry Registry { get; }

    /// <summary>The undo/redo command stack backing this document.</summary>
    public EditorHistory History { get; } = new();

    /// <summary>The current selection.</summary>
    public EditorSelection Selection { get; } = new();

    /// <summary>Fired after every committed mutation (<see cref="Execute"/>, <see cref="Undo"/>,
    /// <see cref="Redo"/>).</summary>
    public event Action? DocumentChanged;

    /// <summary>True after any un-undone command since the last <see cref="MarkSaved"/>. Tracked by history
    /// position: undoing back to the saved point clears it. If a fresh edit discards the history branch that
    /// held the saved point, the saved state becomes unreachable and this stays true until the next save.</summary>
    public bool IsDirty => History.UndoDepth != _savedMarker;

    /// <summary>True when the last committed command changed terrain shape or scatter inputs (feature, scatter
    /// layer, exclusion, override, bounds), meaning the viewport must rebuild its streamed world.
    /// Placement/spawn/region edits leave it false. Cleared by <see cref="AcknowledgeWorldRebuild"/>.</summary>
    public bool WorldRebuildPending { get; private set; }

    /// <summary>Clears the pending world-rebuild flag once the viewport has rebuilt.</summary>
    public void AcknowledgeWorldRebuild() => WorldRebuildPending = false;

    /// <summary>Applies a command through the history stack, then raises the change signals. This is the only
    /// mutation entry point the editor uses.</summary>
    public void Execute(IEditorCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        int depthBefore = History.UndoDepth;
        History.Execute(Doc, command);
        // If this edit discarded a redo branch that held the saved point, the saved state is gone for good.
        if (_savedMarker != Unreachable && _savedMarker > depthBefore)
            _savedMarker = Unreachable;
        if (AffectsWorld(command)) WorldRebuildPending = true;
        DocumentChanged?.Invoke();
    }

    /// <summary>Undoes the top command. Returns false when there is nothing to undo.</summary>
    public bool Undo()
    {
        IEditorCommand? command = History.PeekUndo();
        if (!History.Undo(Doc)) return false;
        if (command is not null && AffectsWorld(command)) WorldRebuildPending = true;
        DocumentChanged?.Invoke();
        return true;
    }

    /// <summary>Redoes the last undone command. Returns false when there is nothing to redo.</summary>
    public bool Redo()
    {
        IEditorCommand? command = History.PeekRedo();
        if (!History.Redo(Doc)) return false;
        if (command is not null && AffectsWorld(command)) WorldRebuildPending = true;
        DocumentChanged?.Invoke();
        return true;
    }

    /// <summary>Marks the current history position as the on-disk saved state, clearing <see cref="IsDirty"/>.</summary>
    public void MarkSaved() => _savedMarker = History.UndoDepth;

    static bool AffectsWorld(IEditorCommand command) => command is EditorCommand ec && ec.AffectsWorld;
}
