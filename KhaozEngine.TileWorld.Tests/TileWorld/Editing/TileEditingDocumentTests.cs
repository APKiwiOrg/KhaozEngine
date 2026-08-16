using System;
using System.Collections.Generic;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Editing;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Editing;

/// <summary>Headless tests for the editing document: the saved-point dirty flag (including the saved state a
/// later edit makes unreachable), the collision map kept in step with every command through its dirty rects, the
/// accumulated pending rebuilds, and the per-command events.</summary>
public class TileEditingDocumentTests
{
    static readonly TileWorldCatalogs Cat = TileWorldCatalogs.Greybox();

    // Places one Solid archetype, so the derived collision map has something to flip. Task 3 and 4 ship the real
    // object commands, this is only enough to drive the document.
    sealed class PlaceTreeCommand : TileCommandBase
    {
        readonly int _x;
        readonly int _z;
        readonly int _plane;
        long _id;

        public PlaceTreeCommand(int x, int z, int plane) : base("Place tree")
        {
            _x = x;
            _z = z;
            _plane = plane;
            Dirty.Add(new TileDirtyRect(new TileRect(x, z, 1, 1), plane));
        }

        public override void Apply(TileWorldDocument doc) => _id = doc.AddObject("tree", _x, _z, _plane, 0).Id;

        public override void Revert(TileWorldDocument doc) => doc.RemoveObject(_id);
    }

    // Mergeable per tile, so the tests can drive a coalescing gesture through the document.
    sealed class SetUnderlayCommand : TileCommandBase
    {
        readonly int _x;
        readonly int _z;
        readonly int _plane;
        ushort _value;
        ushort _old;
        bool _captured;

        public SetUnderlayCommand(int x, int z, int plane, ushort value) : base("Paint tile")
        {
            _x = x;
            _z = z;
            _plane = plane;
            _value = value;
            Dirty.Add(new TileDirtyRect(new TileRect(x, z, 1, 1), plane));
        }

        public override void Apply(TileWorldDocument doc)
        {
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

    static TileEditingDocument Editing(out TileWorldDocument doc)
    {
        doc = TileWorldTestData.FlatWorld();
        return new TileEditingDocument(doc, Cat);
    }

    [Fact]
    public void Construction_bakes_collision_and_starts_clean()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.SetUnderlay(3, 3, 0, 0);                       // void ground reads blocked

        var ed = new TileEditingDocument(doc, Cat);

        Assert.Same(doc, ed.Document);
        Assert.Same(Cat, ed.Catalogs);
        Assert.Equal(TileCollisionFlags.Blocked, ed.Collision.Get(3, 3, 0));
        Assert.Equal(TileCollisionFlags.None, ed.Collision.Get(4, 4, 0));
        Assert.False(ed.IsDirty);
        Assert.Empty(ed.PendingRebuilds);
        Assert.False(ed.History.CanUndo);
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new TileEditingDocument(null!, Cat));
        Assert.Throws<ArgumentNullException>(() => new TileEditingDocument(TileWorldTestData.FlatWorld(), null!));
        TileEditingDocument ed = Editing(out _);
        Assert.Throws<ArgumentNullException>(() => ed.Execute(null!));
    }

    [Fact]
    public void Executing_a_command_makes_the_document_dirty()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);

        ed.Execute(new SetUnderlayCommand(5, 5, 0, 3));

        Assert.True(ed.IsDirty);
        Assert.Equal((ushort)3, doc.GetUnderlay(5, 5, 0));
    }

    [Fact]
    public void IsDirty_tracks_the_saved_point_across_execute_undo_and_redo()
    {
        TileEditingDocument ed = Editing(out _);
        Assert.False(ed.IsDirty);

        ed.Execute(new PlaceTreeCommand(10, 10, 0));
        Assert.True(ed.IsDirty);

        ed.MarkSaved();
        Assert.False(ed.IsDirty);

        ed.Execute(new PlaceTreeCommand(12, 12, 0));
        Assert.True(ed.IsDirty);

        Assert.True(ed.Undo());                            // back to the saved point
        Assert.False(ed.IsDirty);

        Assert.True(ed.Undo());                            // below the saved point
        Assert.True(ed.IsDirty);

        Assert.True(ed.Redo());                            // forward to the saved point again
        Assert.False(ed.IsDirty);
    }

    [Fact]
    public void An_edit_after_undo_makes_the_saved_point_unreachable_for_good()
    {
        TileEditingDocument ed = Editing(out _);
        ed.Execute(new PlaceTreeCommand(10, 10, 0));
        ed.MarkSaved();
        Assert.True(ed.Undo());
        Assert.True(ed.IsDirty);                           // one step below the saved point

        // This edit discards the redo branch holding the saved point, so no amount of undo gets back to it.
        ed.Execute(new PlaceTreeCommand(12, 12, 0));
        Assert.True(ed.IsDirty);
        Assert.True(ed.Undo());
        Assert.True(ed.IsDirty);
        Assert.True(ed.Redo());
        Assert.True(ed.IsDirty);
    }

    [Fact]
    public void MarkSaved_seals_the_gesture_so_a_later_edit_cannot_hide_inside_the_saved_step()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);
        ed.Execute(new SetUnderlayCommand(5, 5, 0, 2));
        ed.MarkSaved();
        Assert.False(ed.IsDirty);

        // Same tile, so without the seal it would merge into the saved command and leave the depth marker
        // matching while the document had changed under it.
        ed.Execute(new SetUnderlayCommand(5, 5, 0, 3));
        Assert.True(ed.IsDirty);

        Assert.True(ed.Undo());
        Assert.False(ed.IsDirty);
        Assert.Equal((ushort)2, doc.GetUnderlay(5, 5, 0));
    }

    [Fact]
    public void SealGesture_splits_a_coalescing_gesture_into_two_steps()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);
        ed.Execute(new SetUnderlayCommand(5, 5, 0, 2));
        ed.Execute(new SetUnderlayCommand(5, 5, 0, 3));    // merges, one gesture
        ed.SealGesture();
        ed.Execute(new SetUnderlayCommand(5, 5, 0, 4));

        Assert.True(ed.Undo());
        Assert.Equal((ushort)3, doc.GetUnderlay(5, 5, 0));
        Assert.True(ed.Undo());
        Assert.Equal((ushort)1, doc.GetUnderlay(5, 5, 0));
        Assert.False(ed.History.CanUndo);
    }

    [Fact]
    public void Undo_and_redo_report_false_when_there_is_nothing_to_do()
    {
        TileEditingDocument ed = Editing(out _);

        Assert.False(ed.Undo());
        Assert.False(ed.Redo());
        Assert.Empty(ed.PendingRebuilds);
    }

    [Fact]
    public void Collision_flips_on_execute_and_back_on_undo()
    {
        TileEditingDocument ed = Editing(out _);
        Assert.Equal(TileCollisionFlags.None, ed.Collision.Get(10, 10, 0));

        ed.Execute(new PlaceTreeCommand(10, 10, 0));
        Assert.Equal(TileCollisionFlags.Blocked, ed.Collision.Get(10, 10, 0));

        Assert.True(ed.Undo());
        Assert.Equal(TileCollisionFlags.None, ed.Collision.Get(10, 10, 0));

        Assert.True(ed.Redo());
        Assert.Equal(TileCollisionFlags.Blocked, ed.Collision.Get(10, 10, 0));
    }

    [Fact]
    public void Collision_follows_a_ground_edit_too()
    {
        TileEditingDocument ed = Editing(out _);

        ed.Execute(new SetUnderlayCommand(7, 7, 0, 0));    // void ground blocks the tile
        Assert.Equal(TileCollisionFlags.Blocked, ed.Collision.Get(7, 7, 0));

        Assert.True(ed.Undo());
        Assert.Equal(TileCollisionFlags.None, ed.Collision.Get(7, 7, 0));
    }

    [Fact]
    public void PendingRebuilds_accumulate_and_acknowledge_clears_them()
    {
        TileEditingDocument ed = Editing(out _);

        ed.Execute(new SetUnderlayCommand(5, 5, 0, 2));
        ed.Execute(new SetUnderlayCommand(6, 6, 0, 2));

        Assert.Equal(2, ed.PendingRebuilds.Count);
        Assert.Contains(new TileDirtyRect(new TileRect(5, 5, 1, 1), 0), ed.PendingRebuilds);
        Assert.Contains(new TileDirtyRect(new TileRect(6, 6, 1, 1), 0), ed.PendingRebuilds);

        ed.AcknowledgeRebuilds();
        Assert.Empty(ed.PendingRebuilds);

        Assert.True(ed.Undo());                            // an undo dirties the same rect again
        Assert.Equal(new TileDirtyRect(new TileRect(6, 6, 1, 1), 0), Assert.Single(ed.PendingRebuilds));

        Assert.True(ed.Redo());
        Assert.Equal(2, ed.PendingRebuilds.Count);
    }

    [Fact]
    public void Events_carry_the_command_and_fire_once_collision_is_up_to_date()
    {
        TileEditingDocument ed = Editing(out _);
        var applied = new List<ITileCommand>();
        var undone = new List<ITileCommand>();
        var redone = new List<ITileCommand>();
        var seen = new List<TileCollisionFlags>();
        ed.CommandApplied += c => { applied.Add(c); seen.Add(ed.Collision.Get(10, 10, 0)); };
        ed.CommandUndone += c => { undone.Add(c); seen.Add(ed.Collision.Get(10, 10, 0)); };
        ed.CommandRedone += c => { redone.Add(c); seen.Add(ed.Collision.Get(10, 10, 0)); };

        var command = new PlaceTreeCommand(10, 10, 0);
        ed.Execute(command);
        Assert.True(ed.Undo());
        Assert.True(ed.Redo());

        Assert.Same(command, Assert.Single(applied));
        Assert.Same(command, Assert.Single(undone));
        Assert.Same(command, Assert.Single(redone));
        Assert.Equal(
            new[] { TileCollisionFlags.Blocked, TileCollisionFlags.None, TileCollisionFlags.Blocked },
            seen);
    }

    [Fact]
    public void No_event_fires_when_there_is_nothing_to_undo_or_redo()
    {
        TileEditingDocument ed = Editing(out _);
        int fired = 0;
        ed.CommandUndone += _ => fired++;
        ed.CommandRedone += _ => fired++;

        Assert.False(ed.Undo());
        Assert.False(ed.Redo());

        Assert.Equal(0, fired);
    }
}
