using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Editing;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Editing;

/// <summary>Headless tests for the tile world's undo/redo stack: apply and push, undo/redo ordering, redo
/// invalidation on a fresh edit, gesture coalescing through <see cref="ITileCommand.TryMerge"/>, and the merge
/// barrier that <see cref="TileEditHistory.SealGesture"/>, <see cref="TileEditHistory.Undo"/> and
/// <see cref="TileEditHistory.Redo"/> raise.</summary>
public class TileEditHistoryTests
{
    // The minimal reversible edit: one tile's underlay. It merges with a later write to the SAME tile, which is
    // the paint-drag gesture coalescing exists for, and refuses a write to any other tile.
    sealed class SetUnderlayCommand : TileCommandBase
    {
        readonly int _x;
        readonly int _z;
        readonly int _plane;
        ushort _value;
        ushort _old;
        bool _captured;

        public SetUnderlayCommand(int x, int z, int plane, ushort value, string label = "Paint tile") : base(label)
        {
            _x = x;
            _z = z;
            _plane = plane;
            _value = value;
            Dirty.Add(new TileDirtyRect(new TileRect(x, z, 1, 1), plane));
        }

        public override void Apply(TileWorldDocument doc)
        {
            // Captured once, so a re-apply after undo still reverts to the value the gesture started from.
            if (!_captured)
            {
                _old = doc.GetUnderlay(_x, _z, _plane);
                _captured = true;
            }
            doc.SetUnderlay(_x, _z, _plane, _value);
        }

        public override void Revert(TileWorldDocument doc) => doc.SetUnderlay(_x, _z, _plane, _old);

        public override bool TryMerge(ITileCommand next)
        {
            if (next is not SetUnderlayCommand c || c._x != _x || c._z != _z || c._plane != _plane) return false;
            _value = c._value;
            return true;
        }
    }

    static TileWorldDocument Flat() => TileWorldTestData.FlatWorld();

    [Fact]
    public void Execute_applies_the_command_and_enables_undo()
    {
        TileWorldDocument doc = Flat();
        var h = new TileEditHistory();
        Assert.False(h.CanUndo);

        h.Execute(doc, new SetUnderlayCommand(5, 5, 0, 3));

        Assert.Equal((ushort)3, doc.GetUnderlay(5, 5, 0));
        Assert.True(h.CanUndo);
        Assert.False(h.CanRedo);
        Assert.Equal(1, h.UndoDepth);
        Assert.Equal(0, h.RedoDepth);
    }

    [Fact]
    public void Undo_reverts_the_top_command_and_enables_redo()
    {
        TileWorldDocument doc = Flat();
        var h = new TileEditHistory();
        h.Execute(doc, new SetUnderlayCommand(5, 5, 0, 3));

        Assert.True(h.Undo(doc));

        Assert.Equal((ushort)1, doc.GetUnderlay(5, 5, 0));
        Assert.False(h.CanUndo);
        Assert.True(h.CanRedo);
        Assert.Equal(0, h.UndoDepth);
        Assert.Equal(1, h.RedoDepth);
    }

    [Fact]
    public void Redo_reapplies_the_undone_command()
    {
        TileWorldDocument doc = Flat();
        var h = new TileEditHistory();
        h.Execute(doc, new SetUnderlayCommand(5, 5, 0, 3));
        h.Undo(doc);

        Assert.True(h.Redo(doc));

        Assert.Equal((ushort)3, doc.GetUnderlay(5, 5, 0));
        Assert.Equal(1, h.UndoDepth);
        Assert.Equal(0, h.RedoDepth);
    }

    [Fact]
    public void Undo_and_redo_report_false_on_an_empty_stack()
    {
        TileWorldDocument doc = Flat();
        var h = new TileEditHistory();

        Assert.False(h.Undo(doc));
        Assert.False(h.Redo(doc));
    }

    [Fact]
    public void Labels_follow_the_stack_tops()
    {
        TileWorldDocument doc = Flat();
        var h = new TileEditHistory();
        Assert.Null(h.UndoLabel);
        Assert.Null(h.RedoLabel);

        h.Execute(doc, new SetUnderlayCommand(5, 5, 0, 3, "Paint grass"));
        Assert.Equal("Paint grass", h.UndoLabel);
        Assert.Null(h.RedoLabel);

        h.Undo(doc);
        Assert.Null(h.UndoLabel);
        Assert.Equal("Paint grass", h.RedoLabel);
    }

    [Fact]
    public void Undo_and_redo_run_last_in_first_out()
    {
        TileWorldDocument doc = Flat();
        var h = new TileEditHistory();
        h.Execute(doc, new SetUnderlayCommand(1, 1, 0, 3));
        h.Execute(doc, new SetUnderlayCommand(2, 2, 0, 4));

        Assert.True(h.Undo(doc));
        Assert.Equal((ushort)1, doc.GetUnderlay(2, 2, 0));
        Assert.Equal((ushort)3, doc.GetUnderlay(1, 1, 0));

        Assert.True(h.Undo(doc));
        Assert.Equal((ushort)1, doc.GetUnderlay(1, 1, 0));
    }

    [Fact]
    public void Execute_clears_the_redo_stack()
    {
        TileWorldDocument doc = Flat();
        var h = new TileEditHistory();
        h.Execute(doc, new SetUnderlayCommand(1, 1, 0, 3));
        h.Execute(doc, new SetUnderlayCommand(2, 2, 0, 4));
        h.Undo(doc);
        Assert.True(h.CanRedo);

        h.Execute(doc, new SetUnderlayCommand(3, 3, 0, 5));

        Assert.False(h.CanRedo);
        Assert.Equal(0, h.RedoDepth);
    }

    [Fact]
    public void Repeated_edits_to_one_tile_coalesce_into_a_single_step()
    {
        TileWorldDocument doc = Flat();
        var h = new TileEditHistory();
        h.Execute(doc, new SetUnderlayCommand(5, 5, 0, 2));
        h.Execute(doc, new SetUnderlayCommand(5, 5, 0, 3));
        h.Execute(doc, new SetUnderlayCommand(5, 5, 0, 4));

        Assert.Equal((ushort)4, doc.GetUnderlay(5, 5, 0));
        Assert.Equal(1, h.UndoDepth);

        Assert.True(h.Undo(doc));
        Assert.Equal((ushort)1, doc.GetUnderlay(5, 5, 0));
        Assert.False(h.CanUndo);
    }

    [Fact]
    public void Edits_to_different_tiles_do_not_coalesce()
    {
        TileWorldDocument doc = Flat();
        var h = new TileEditHistory();
        h.Execute(doc, new SetUnderlayCommand(1, 1, 0, 3));
        h.Execute(doc, new SetUnderlayCommand(2, 2, 0, 4));

        Assert.Equal(2, h.UndoDepth);
    }

    [Fact]
    public void SealGesture_stops_the_next_edit_merging_into_the_current_step()
    {
        TileWorldDocument doc = Flat();
        var h = new TileEditHistory();
        h.Execute(doc, new SetUnderlayCommand(5, 5, 0, 2));
        h.SealGesture();
        h.SealGesture();                                   // idempotent
        h.Execute(doc, new SetUnderlayCommand(5, 5, 0, 3));

        Assert.Equal(2, h.UndoDepth);
        Assert.True(h.Undo(doc));
        Assert.Equal((ushort)2, doc.GetUnderlay(5, 5, 0));  // back to the seal point, not to the start
        Assert.True(h.CanUndo);
    }

    [Fact]
    public void Undo_raises_the_merge_barrier_so_the_next_edit_starts_a_step()
    {
        TileWorldDocument doc = Flat();
        var h = new TileEditHistory();
        h.Execute(doc, new SetUnderlayCommand(1, 1, 0, 2));
        h.Execute(doc, new SetUnderlayCommand(2, 2, 0, 3));
        h.Undo(doc);                                       // the top is the (1, 1) command again

        // Without the barrier this same-tile edit would be absorbed by the reactivated step below it.
        h.Execute(doc, new SetUnderlayCommand(1, 1, 0, 4));

        Assert.Equal(2, h.UndoDepth);
        Assert.False(h.CanRedo);
        Assert.True(h.Undo(doc));
        Assert.Equal((ushort)2, doc.GetUnderlay(1, 1, 0));
    }

    [Fact]
    public void Redo_raises_the_merge_barrier_too()
    {
        TileWorldDocument doc = Flat();
        var h = new TileEditHistory();
        h.Execute(doc, new SetUnderlayCommand(1, 1, 0, 2));
        h.Execute(doc, new SetUnderlayCommand(2, 2, 0, 3));
        h.Undo(doc);
        h.Redo(doc);                                       // the top is the (2, 2) command again

        h.Execute(doc, new SetUnderlayCommand(2, 2, 0, 4));

        Assert.Equal(3, h.UndoDepth);
        Assert.True(h.Undo(doc));
        Assert.Equal((ushort)3, doc.GetUnderlay(2, 2, 0));
    }
}
