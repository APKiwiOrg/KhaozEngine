using System;
using System.Collections.Generic;

namespace KhaozEngine.TileWorld.Editing;

/// <summary>A rect of world tiles on one plane that an edit touched, the unit both collision rebaking and
/// renderer invalidation work in.</summary>
/// <param name="Rect">The world tile rect, far edges exclusive.</param>
/// <param name="Plane">The plane the rect applies to.</param>
public readonly record struct TileDirtyRect(TileRect Rect, int Plane);

/// <summary>One reversible edit to a <see cref="TileWorldDocument"/>. Commands are the ONLY mutation path the
/// MCP tool and, later, the GUI editor use, so undo is total by construction.</summary>
public interface ITileCommand
{
    /// <summary>A short human-readable label for the undo/redo menu (developer-facing tooling text).</summary>
    string Label { get; }

    /// <summary>Applies the edit to the document.</summary>
    void Apply(TileWorldDocument doc);

    /// <summary>Reverses the edit, restoring the document to its pre-<see cref="Apply"/> state.</summary>
    void Revert(TileWorldDocument doc);

    /// <summary>True when this command can absorb a newer one of the same gesture (drag coalescing): the pair
    /// collapses to one undo step.</summary>
    bool TryMerge(ITileCommand next);

    /// <summary>Every tile rect and plane this edit touched, in either direction. Applying and reverting reach
    /// the same tiles, so one set serves both: it must cover every tile whose layers, corners or collision
    /// moved, INCLUDING the full footprint of an object the command removes (measured with
    /// <see cref="TileFootprint.Of"/> BEFORE the removal, because a rebake cannot measure what is gone) and,
    /// for a corner height, the tiles on both sides of that corner.</summary>
    IEnumerable<TileDirtyRect> DirtyRects { get; }
}

/// <summary>Shared base for the concrete tile commands: carries the label, the accumulated dirty rects, and a
/// no-merge default, so a command only writes the mutation itself.</summary>
public abstract class TileCommandBase : ITileCommand
{
    /// <summary>Creates a command with the label its undo step shows.</summary>
    protected TileCommandBase(string label) => Label = label ?? throw new ArgumentNullException(nameof(label));

    /// <inheritdoc/>
    public string Label { get; }

    /// <summary>The rects this command touched, built by the subclass (in its constructor when the reach is
    /// known up front, otherwise while applying) and handed out through <see cref="DirtyRects"/>.</summary>
    protected List<TileDirtyRect> Dirty { get; } = new();

    /// <inheritdoc/>
    public IEnumerable<TileDirtyRect> DirtyRects => Dirty;

    /// <inheritdoc/>
    public abstract void Apply(TileWorldDocument doc);

    /// <inheritdoc/>
    public abstract void Revert(TileWorldDocument doc);

    /// <inheritdoc/>
    public virtual bool TryMerge(ITileCommand next) => false;

    /// <summary>Records a rect on a plane, dropping an empty one and an exact repeat (a re-apply after undo
    /// walks the same tiles, and a rebake of the same rect twice only costs time).</summary>
    protected void MarkDirty(TileRect rect, int plane)
    {
        if (rect.IsEmpty) return;
        var d = new TileDirtyRect(rect, plane);
        if (!Dirty.Contains(d)) Dirty.Add(d);
    }
}
