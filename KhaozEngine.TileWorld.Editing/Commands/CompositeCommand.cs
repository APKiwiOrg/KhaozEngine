using System;
using System.Collections.Generic;
using System.Linq;

namespace KhaozEngine.TileWorld.Editing;

/// <summary>A labelled list of commands that lands as ONE undo step: applied front to back, reverted back to
/// front, and reporting every child's dirty rects as its own. Built by the <see cref="TileEditOps"/> factories
/// for the edits that are naturally many small commands (a line of fence posts, a scatter of trees).
///
/// It does not derive from <see cref="TileCommandBase"/> on purpose: a child can only work out the tiles it
/// touched while it applies (a move learns where the object came from then, a region command learns the plane
/// count then), so <see cref="DirtyRects"/> reads THROUGH to the children on every call instead of copying a
/// snapshot of them that would be empty at construction and stale afterwards.</summary>
public sealed class CompositeCommand : ITileCommand
{
    readonly IReadOnlyList<ITileCommand> _commands;

    /// <summary>Creates the composite from the children, in the order they should apply.</summary>
    public CompositeCommand(string label, IReadOnlyList<ITileCommand> commands)
    {
        Label = label ?? throw new ArgumentNullException(nameof(label));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
    }

    /// <inheritdoc/>
    public string Label { get; }

    /// <summary>The children, in apply order. A revert walks them the other way.</summary>
    public IReadOnlyList<ITileCommand> Commands => _commands;

    /// <summary>Every rect every child reports, read live rather than copied.</summary>
    public IEnumerable<TileDirtyRect> DirtyRects => _commands.SelectMany(c => c.DirtyRects);

    /// <summary>Applies the children in order. A child that throws part way takes the whole composite with it:
    /// the ones that already applied are reverted, back to front, before the exception carries on. A composite
    /// that half landed would sit in the undo stack claiming to describe an edit the document never made, and
    /// the caller's retry would then double the half that did.</summary>
    public void Apply(TileWorldDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        for (int i = 0; i < _commands.Count; i++)
        {
            try
            {
                _commands[i].Apply(doc);
            }
            catch
            {
                for (int j = i - 1; j >= 0; j--) _commands[j].Revert(doc);
                throw;
            }
        }
    }

    /// <summary>Reverts the children in reverse order, so a later child's undo runs before the earlier one it
    /// was built on top of.</summary>
    public void Revert(TileWorldDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        for (int i = _commands.Count - 1; i >= 0; i--) _commands[i].Revert(doc);
    }

    /// <summary>Never merges. A composite is already a whole authored operation, so coalescing two of them
    /// would collapse two deliberate edits into one undo step.</summary>
    public bool TryMerge(ITileCommand next) => false;
}
