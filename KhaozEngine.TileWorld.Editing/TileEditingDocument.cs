using System;
using System.Collections.Generic;

namespace KhaozEngine.TileWorld.Editing;

/// <summary>The open tile world plus editing state: the command history, the saved-point dirty flag, the
/// derived collision map kept in step with every edit, and the rects a renderer still has to rebuild.
/// <see cref="Execute"/>, <see cref="Undo"/> and <see cref="Redo"/> are the only mutation paths the tool and
/// the editor use, so collision upkeep and the change events stay correct for every edit by construction.
/// </summary>
public sealed class TileEditingDocument
{
    /// <summary>Sentinel saved-point marker meaning "the saved state is no longer reachable" (its history
    /// branch was discarded by a later edit). Undo depth is always non-negative, so this never matches.</summary>
    const int Unreachable = -1;

    int _savedMarker;
    readonly List<TileDirtyRect> _pending = new();

    /// <summary>Opens a document for editing against the catalogs its content is authored with, baking the
    /// collision map once up front.</summary>
    public TileEditingDocument(TileWorldDocument doc, TileWorldCatalogs catalogs)
    {
        Document = doc ?? throw new ArgumentNullException(nameof(doc));
        Catalogs = catalogs ?? throw new ArgumentNullException(nameof(catalogs));
        Collision = TileCollisionBaker.Bake(doc, catalogs);
    }

    /// <summary>The world being edited.</summary>
    public TileWorldDocument Document { get; }

    /// <summary>The materials and archetypes the world's content resolves against.</summary>
    public TileWorldCatalogs Catalogs { get; }

    /// <summary>The undo/redo command stack backing this document.</summary>
    public TileEditHistory History { get; } = new();

    /// <summary>The derived collision map, baked at construction and rebaked over each command's dirty rects as
    /// it applies, reverts or reapplies. Never authored and never saved.</summary>
    public TileCollisionMap Collision { get; }

    /// <summary>True after any un-undone command since the last <see cref="MarkSaved"/>. Tracked by history
    /// position, so undoing back to the saved point clears it. If a fresh edit discards the history branch that
    /// held the saved point, the saved state becomes unreachable and this stays true until the next save.</summary>
    public bool IsDirty => History.UndoDepth != _savedMarker;

    /// <summary>The tile rects, per plane, that changed since the last <see cref="AcknowledgeRebuilds"/>, in the
    /// order the edits touched them. A renderer rebuilds the chunks they cover (its own seam margin is its
    /// business). Repeats are kept rather than merged, because a rebuild is idempotent and folding rects from
    /// unrelated edits into one bounding rect would cover the whole world after two edits at opposite corners.
    /// </summary>
    public IReadOnlyList<TileDirtyRect> PendingRebuilds => _pending;

    /// <summary>Fired after a command applies through <see cref="Execute"/>, carrying that command. Fires on
    /// EVERY execute, including one that coalesced into the current undo step (a merged command still applied
    /// its mutation), and only once the collision map is up to date.</summary>
    public event Action<ITileCommand>? CommandApplied;

    /// <summary>Fired after <see cref="Undo"/> reverts a command, carrying the reverted command, once the
    /// collision map is up to date.</summary>
    public event Action<ITileCommand>? CommandUndone;

    /// <summary>Fired after <see cref="Redo"/> reapplies a command, carrying that command, once the collision
    /// map is up to date.</summary>
    public event Action<ITileCommand>? CommandRedone;

    /// <summary>Applies a command through the history stack, rebakes collision over its dirty rects, then
    /// raises <see cref="CommandApplied"/>. This is the one mutation entry point.</summary>
    public void Execute(ITileCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        int depthBefore = History.UndoDepth;
        History.Execute(Document, command);
        // If this edit discarded a redo branch that held the saved point, the saved state is gone for good.
        if (_savedMarker != Unreachable && _savedMarker > depthBefore)
            _savedMarker = Unreachable;
        RefreshCollision(command);
        CommandApplied?.Invoke(command);
    }

    /// <summary>Undoes the top command, rebaking collision over the same rects (what an apply touches, a revert
    /// touches). Returns false when there is nothing to undo.</summary>
    public bool Undo()
    {
        ITileCommand? command = History.PeekUndo();
        if (!History.Undo(Document)) return false;
        if (command is not null)
        {
            RefreshCollision(command);
            CommandUndone?.Invoke(command);
        }
        return true;
    }

    /// <summary>Redoes the last undone command, rebaking collision over its rects. Returns false when there is
    /// nothing to redo.</summary>
    public bool Redo()
    {
        ITileCommand? command = History.PeekRedo();
        if (!History.Redo(Document)) return false;
        if (command is not null)
        {
            RefreshCollision(command);
            CommandRedone?.Invoke(command);
        }
        return true;
    }

    /// <summary>Marks the end of the current input gesture (drag release, focus loss): the next
    /// <see cref="Execute"/> starts a new undo step instead of coalescing into the current one. Idempotent.</summary>
    public void SealGesture() => History.SealGesture();

    /// <summary>Marks the current history position as the on-disk saved state, clearing <see cref="IsDirty"/>.
    /// A save is always a gesture boundary, so it also seals the current gesture: a later same-gesture edit can
    /// never merge into the saved command and hide itself from <see cref="IsDirty"/>.</summary>
    public void MarkSaved()
    {
        History.SealGesture();
        _savedMarker = History.UndoDepth;
    }

    /// <summary>Clears the accumulated <see cref="PendingRebuilds"/> once the renderer has consumed them.</summary>
    public void AcknowledgeRebuilds() => _pending.Clear();

    // Rebakes one rect per plane the command reports and records the same rects for the renderer. Called AFTER
    // the history call, so the document already holds the post-edit state the rebake re-derives from.
    void RefreshCollision(ITileCommand command)
    {
        foreach (TileDirtyRect d in command.DirtyRects)
        {
            if (d.Rect.IsEmpty) continue;
            TileCollisionBaker.Rebake(Collision, Document, Catalogs, d.Rect, d.Plane);
            _pending.Add(d);
        }
    }
}
