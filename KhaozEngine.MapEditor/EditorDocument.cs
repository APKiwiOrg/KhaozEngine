using System;
using KhaozEngine.MapDoc;
using KhaozEngine.Terrain;

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

    // The dirty region accumulated across the commands that set WorldRebuildPending since the last acknowledge.
    // Three states, distinguished cleanly: nothing pending (both null/false), pending-with-rect (_pendingRegion set,
    // _pendingRegionIsFull false), pending-full (_pendingRegionIsFull true). Once any command with a null DirtyRegion
    // lands, the region is full-sticky for the rest of the batch (a later bounded rect cannot narrow it back).
    RectArea? _pendingRegion;
    bool _pendingRegionIsFull;

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

    /// <summary>Fired after a command applies through <see cref="Execute"/>, carrying that command (BEFORE
    /// <see cref="DocumentChanged"/>). Fires on EVERY execute, including one that coalesced into the current undo
    /// step (a merged command still applied its mutation). The editor subscribes to run a command's view-only
    /// visibility maintenance (<see cref="IVisibilityEffect"/>) so a per-element hide follows the element the
    /// command moved, removed, or renamed.</summary>
    public event Action<IEditorCommand>? CommandApplied;

    /// <summary>Fired after <see cref="Undo"/> reverts a command, carrying the reverted command (BEFORE
    /// <see cref="DocumentChanged"/>). Its subscriber applies the INVERSE of the command's visibility maintenance,
    /// so a hide follows the element back to its pre-command identity.</summary>
    public event Action<IEditorCommand>? CommandUndone;

    /// <summary>Fired after <see cref="Redo"/> re-applies a command, carrying that command (BEFORE
    /// <see cref="DocumentChanged"/>). Its subscriber re-applies the FORWARD visibility maintenance, matching
    /// <see cref="CommandApplied"/>.</summary>
    public event Action<IEditorCommand>? CommandRedone;

    /// <summary>True after any un-undone command since the last <see cref="MarkSaved"/>. Tracked by history
    /// position: undoing back to the saved point clears it. If a fresh edit discards the history branch that
    /// held the saved point, the saved state becomes unreachable and this stays true until the next save.</summary>
    public bool IsDirty => History.UndoDepth != _savedMarker;

    /// <summary>True when the last committed command changed terrain shape or scatter inputs (feature, scatter
    /// layer, exclusion, override, bounds), meaning the viewport must rebuild its streamed world.
    /// Placement/spawn/region edits leave it false. Cleared by <see cref="AcknowledgeWorldRebuild"/>.</summary>
    public bool WorldRebuildPending { get; private set; }

    /// <summary>The accumulated world-space region the pending rebuild must cover, meaningful ONLY while
    /// <see cref="WorldRebuildPending"/> is true. Null carries TWO meanings, so a caller MUST consult
    /// <see cref="WorldRebuildPending"/> first to tell them apart: with a rebuild pending, null means a FULL rebuild
    /// is required (some accumulated command had no bounded region), while a non-null rect means only the chunks it
    /// overlaps need rebuilding. With no rebuild pending it is simply null (nothing accumulated). Reset by
    /// <see cref="AcknowledgeWorldRebuild"/>.</summary>
    public RectArea? PendingRebuildRegion => _pendingRegionIsFull ? null : _pendingRegion;

    /// <summary>Clears the pending world-rebuild flag and its accumulated region once the viewport has rebuilt.</summary>
    public void AcknowledgeWorldRebuild()
    {
        WorldRebuildPending = false;
        _pendingRegion = null;
        _pendingRegionIsFull = false;
    }

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
        MarkWorldRebuild(command);
        CommandApplied?.Invoke(command);
        DocumentChanged?.Invoke();
    }

    /// <summary>Undoes the top command. Returns false when there is nothing to undo.</summary>
    public bool Undo()
    {
        IEditorCommand? command = History.PeekUndo();
        if (!History.Undo(Doc)) return false;
        if (command is not null)
        {
            MarkWorldRebuild(command);
            CommandUndone?.Invoke(command);
        }
        DocumentChanged?.Invoke();
        return true;
    }

    /// <summary>Redoes the last undone command. Returns false when there is nothing to redo.</summary>
    public bool Redo()
    {
        IEditorCommand? command = History.PeekRedo();
        if (!History.Redo(Doc)) return false;
        if (command is not null)
        {
            MarkWorldRebuild(command);
            CommandRedone?.Invoke(command);
        }
        DocumentChanged?.Invoke();
        return true;
    }

    /// <summary>Marks the end of the current input gesture (drag release, focus loss): the next
    /// <see cref="Execute"/> starts a new undo step instead of coalescing into the current one. Delegates to
    /// <see cref="EditorHistory.SealGesture"/>. Idempotent.</summary>
    public void SealGesture() => History.SealGesture();

    /// <summary>Marks the current history position as the on-disk saved state, clearing <see cref="IsDirty"/>.
    /// A save is always a gesture boundary, so it also seals the current gesture: a later same-gesture edit
    /// can never merge into the saved command and hide itself from <see cref="IsDirty"/>.</summary>
    public void MarkSaved()
    {
        History.SealGesture();
        _savedMarker = History.UndoDepth;
    }

    // Sets WorldRebuildPending and folds the command's dirty region into the pending accumulation, when the command
    // is an engine command that affects the world. A command with a null DirtyRegion (its reach is not a bounded
    // rect) makes the pending region full-sticky for the rest of the batch. A bounded rect unions into the running
    // region. For a merged drag, Undo/Redo pass the peeked (merged) command, whose endpoints-union region is correct
    // (chunks the feature crossed mid-drag were already rebuilt when the footprint left them).
    void MarkWorldRebuild(IEditorCommand command)
    {
        if (command is not EditorCommand ec || !ec.AffectsWorld) return;
        WorldRebuildPending = true;
        if (_pendingRegionIsFull) return;
        if (ec.DirtyRegion is RectArea region)
            _pendingRegion = _pendingRegion is RectArea acc ? FeatureGeometry.Union(acc, region) : region;
        else
            _pendingRegionIsFull = true;
    }
}
