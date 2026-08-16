using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Editing;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Editing;

/// <summary>Headless tests for the object commands: the exact apply/undo/redo round trip of each one, the
/// object id that survives a redo without rolling the document's allocator back, the dirty rects that cover a
/// removed object's whole footprint and BOTH footprints of a move or a rotate, and the per-id coalescing that
/// collapses a drag into one undo step without losing the tiles the middle of the drag passed through.</summary>
public class ObjectCommandTests
{
    static readonly TileWorldCatalogs Cat = TileWorldTestData.EditingCatalogs();

    static TileEditingDocument Editing(out TileWorldDocument doc)
    {
        doc = TileWorldTestData.FlatWorld();
        return new TileEditingDocument(doc, Cat);
    }

    static bool Blocked(TileEditingDocument ed, int x, int z, int plane = 0) =>
        (ed.Collision.Get(x, z, plane) & TileCollisionFlags.Blocked) != 0;

    static long Place(TileEditingDocument ed, string archetype, int x, int z, int rotation = 0, string[]? tags = null)
    {
        var place = new PlaceObjectCommand(Cat, archetype, x, z, 0, rotation, tags);
        ed.Execute(place);
        ed.SealGesture();
        return place.ObjectId!.Value;
    }

    [Fact]
    public void Place_round_trips_and_keeps_the_same_id_without_rolling_the_allocator_back()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);
        var place = new PlaceObjectCommand(Cat, "tree", 5, 6, 0, 1, new[] { "forest" });

        Assert.Equal("Place object", place.Label);
        Assert.Null(place.ObjectId);

        ed.Execute(place);

        long id = place.ObjectId!.Value;
        Assert.Equal(1, id);
        Assert.Equal(2, doc.NextObjectId);
        TileObject o = doc.FindObject(id)!;
        Assert.Equal("tree", o.ArchetypeId);
        Assert.Equal(5, o.X);
        Assert.Equal(6, o.Z);
        Assert.Equal(0, o.Plane);
        Assert.Equal(1, o.Rotation);
        Assert.Equal(new[] { "forest" }, o.Tags);
        Assert.True(Blocked(ed, 5, 6));

        Assert.True(ed.Undo());
        Assert.Null(doc.FindObject(id));
        Assert.False(Blocked(ed, 5, 6));
        // The allocator is deliberately NOT rewound: ids are never reused, so a document saved between the undo
        // and the redo cannot hand this id to something else and then collide with the redo.
        Assert.Equal(2, doc.NextObjectId);

        Assert.True(ed.Redo());
        TileObject again = doc.FindObject(id)!;
        Assert.Equal(id, again.Id);
        Assert.Equal("tree", again.ArchetypeId);
        Assert.Equal(5, again.X);
        Assert.Equal(6, again.Z);
        Assert.Equal(1, again.Rotation);
        Assert.Equal(new[] { "forest" }, again.Tags);
        Assert.Equal(2, doc.NextObjectId);
        Assert.True(Blocked(ed, 5, 6));

        Assert.True(ed.Undo());
        Assert.Null(doc.FindObject(id));
        Assert.True(ed.Redo());
        Assert.Equal(id, doc.FindObject(id)!.Id);
        Assert.Equal(2, doc.NextObjectId);
    }

    [Fact]
    public void Place_reports_the_rotated_footprint_as_its_dirty_rect()
    {
        var flat = new PlaceObjectCommand(Cat, "hall", 10, 10, 0, 0, null);
        var turned = new PlaceObjectCommand(Cat, "hall", 10, 10, 2, 1, null);

        Assert.Equal(new TileDirtyRect(new TileRect(10, 10, 2, 3), 0), Assert.Single(flat.DirtyRects));
        Assert.Equal(new TileDirtyRect(new TileRect(10, 10, 3, 2), 2), Assert.Single(turned.DirtyRects));
    }

    [Fact]
    public void Place_of_an_archetype_the_catalogs_do_not_define_throws()
    {
        TileWorldException ex = Assert.Throws<TileWorldException>(
            () => new PlaceObjectCommand(Cat, "not_a_thing", 5, 5, 0, 0, null));
        Assert.Contains("not_a_thing", ex.Message);
    }

    [Fact]
    public void Place_into_a_missing_region_throws_and_leaves_the_world_alone()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);

        Assert.Throws<TileWorldException>(() => ed.Execute(new PlaceObjectCommand(Cat, "tree", 500, 500, 0, 0, null)));

        Assert.Empty(doc.AllObjects());
        Assert.Equal(1, doc.NextObjectId);
        Assert.Equal(0, ed.History.UndoDepth);
        Assert.False(ed.IsDirty);
    }

    [Fact]
    public void Move_round_trips_and_its_dirty_rects_cover_the_old_and_new_footprints()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        // The flat world only grounds plane 0, and void ground reads blocked whatever stands on it, so the
        // destination needs real ground before a collision assertion up there means anything.
        for (int z = 39; z <= 43; z++)
            for (int x = 29; x <= 32; x++) doc.SetUnderlay(x, z, 2, 1);
        var ed = new TileEditingDocument(doc, Cat);
        long id = Place(ed, "hall", 10, 10);

        var move = new MoveObjectCommand(Cat, id, 30, 40, 2);
        ed.Execute(move);

        Assert.Equal("Move object", move.Label);
        TileObject o = doc.FindObject(id)!;
        Assert.Equal(30, o.X);
        Assert.Equal(40, o.Z);
        Assert.Equal(2, o.Plane);
        Assert.Contains(new TileDirtyRect(new TileRect(10, 10, 2, 3), 0), move.DirtyRects);
        Assert.Contains(new TileDirtyRect(new TileRect(30, 40, 2, 3), 2), move.DirtyRects);
        Assert.False(Blocked(ed, 11, 12));
        Assert.True(Blocked(ed, 31, 42, 2));

        Assert.True(ed.Undo());
        TileObject back = doc.FindObject(id)!;
        Assert.Equal(10, back.X);
        Assert.Equal(10, back.Z);
        Assert.Equal(0, back.Plane);
        Assert.True(Blocked(ed, 11, 12));
        Assert.False(Blocked(ed, 31, 42, 2));

        Assert.True(ed.Redo());
        Assert.Equal(40, doc.FindObject(id)!.Z);
    }

    [Fact]
    public void Two_moves_of_one_object_coalesce_and_the_undo_returns_it_to_the_origin()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);
        long id = Place(ed, "tree", 5, 5);
        int depthAfterPlace = ed.History.UndoDepth;

        var first = new MoveObjectCommand(Cat, id, 6, 5, 0);
        ed.Execute(first);
        ed.Execute(new MoveObjectCommand(Cat, id, 7, 5, 0));

        // One gesture, one undo step, whatever the drag passed through on the way.
        Assert.Equal(depthAfterPlace + 1, ed.History.UndoDepth);
        Assert.Equal(7, doc.FindObject(id)!.X);
        Assert.True(Blocked(ed, 7, 5));
        Assert.False(Blocked(ed, 5, 5));
        Assert.False(Blocked(ed, 6, 5));

        // The surviving command is the only one left to revert, so it has to carry A, B AND C or the middle of
        // the gesture keeps its stale collision after the undo.
        Assert.Contains(new TileDirtyRect(new TileRect(5, 5, 1, 1), 0), first.DirtyRects);
        Assert.Contains(new TileDirtyRect(new TileRect(6, 5, 1, 1), 0), first.DirtyRects);
        Assert.Contains(new TileDirtyRect(new TileRect(7, 5, 1, 1), 0), first.DirtyRects);

        Assert.True(ed.Undo());
        Assert.Equal(5, doc.FindObject(id)!.X);
        Assert.True(Blocked(ed, 5, 5));
        Assert.False(Blocked(ed, 6, 5));
        Assert.False(Blocked(ed, 7, 5));
    }

    [Fact]
    public void A_move_of_another_object_does_not_coalesce()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);
        long a = Place(ed, "tree", 5, 5);
        long b = Place(ed, "tree", 20, 20);
        int depth = ed.History.UndoDepth;

        ed.Execute(new MoveObjectCommand(Cat, a, 6, 5, 0));
        ed.Execute(new MoveObjectCommand(Cat, b, 21, 20, 0));

        Assert.Equal(depth + 2, ed.History.UndoDepth);
        Assert.True(ed.Undo());
        Assert.Equal(20, doc.FindObject(b)!.X);
        Assert.Equal(6, doc.FindObject(a)!.X);
    }

    [Fact]
    public void Move_of_an_unknown_id_or_into_a_missing_region_throws_and_changes_nothing()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);
        long id = Place(ed, "tree", 5, 5);

        Assert.Throws<TileWorldException>(() => ed.Execute(new MoveObjectCommand(Cat, 9999, 6, 5, 0)));
        Assert.Throws<TileWorldException>(() => ed.Execute(new MoveObjectCommand(Cat, id, 500, 500, 0)));

        TileObject o = doc.FindObject(id)!;
        Assert.Equal(5, o.X);
        Assert.Equal(5, o.Z);
    }

    [Fact]
    public void Rotate_round_trips_and_its_dirty_rects_cover_both_footprints()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);
        long id = Place(ed, "hall", 10, 10);

        var rotate = new RotateObjectCommand(Cat, id, 1);
        ed.Execute(rotate);

        Assert.Equal("Rotate object", rotate.Label);
        Assert.Equal(1, doc.FindObject(id)!.Rotation);
        Assert.Contains(new TileDirtyRect(new TileRect(10, 10, 2, 3), 0), rotate.DirtyRects);
        Assert.Contains(new TileDirtyRect(new TileRect(10, 10, 3, 2), 0), rotate.DirtyRects);
        // The turn takes the footprint from 2 wide by 3 deep to 3 wide by 2 deep, so one tile leaves it and
        // another joins it. A dirty rect covering only one of the two shapes would strand one of them.
        Assert.True(Blocked(ed, 12, 10));
        Assert.False(Blocked(ed, 11, 12));

        Assert.True(ed.Undo());
        Assert.Equal(0, doc.FindObject(id)!.Rotation);
        Assert.False(Blocked(ed, 12, 10));
        Assert.True(Blocked(ed, 11, 12));

        Assert.True(ed.Redo());
        Assert.Equal(1, doc.FindObject(id)!.Rotation);
        Assert.True(Blocked(ed, 12, 10));
    }

    [Fact]
    public void Remove_captures_the_whole_object_and_its_dirty_rect_covers_the_2_by_3_footprint()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);
        long id = Place(ed, "hall", 10, 10, rotation: 2, tags: new[] { "keep", "me" });

        var remove = new RemoveObjectCommand(Cat, id);
        ed.Execute(remove);

        Assert.Equal("Remove object", remove.Label);
        // Measured from the catalog BEFORE the removal: a rebake cannot re-derive the footprint of an object
        // that is no longer in the document.
        Assert.Equal(new TileDirtyRect(new TileRect(10, 10, 2, 3), 0), Assert.Single(remove.DirtyRects));
        Assert.Null(doc.FindObject(id));
        Assert.False(Blocked(ed, 11, 12));

        Assert.True(ed.Undo());
        TileObject back = doc.FindObject(id)!;
        Assert.Equal(id, back.Id);
        Assert.Equal("hall", back.ArchetypeId);
        Assert.Equal(10, back.X);
        Assert.Equal(10, back.Z);
        Assert.Equal(0, back.Plane);
        Assert.Equal(2, back.Rotation);
        Assert.Equal(new[] { "keep", "me" }, back.Tags);
        Assert.True(Blocked(ed, 11, 12));

        Assert.True(ed.Redo());
        Assert.Null(doc.FindObject(id));
        Assert.True(ed.Undo());
        Assert.Equal(id, doc.FindObject(id)!.Id);
    }

    [Fact]
    public void Remove_of_an_unknown_id_throws()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);
        Assert.Throws<TileWorldException>(() => ed.Execute(new RemoveObjectCommand(Cat, 4242)));
        Assert.Empty(doc.AllObjects());
    }

    [Fact]
    public void Set_tags_round_trips_and_reports_no_dirty_rects_at_all()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);
        long id = Place(ed, "tree", 5, 5);

        var tags = new SetObjectTagsCommand(id, new[] { "quest", "hidden" });
        ed.Execute(tags);

        Assert.Equal("Set object tags", tags.Label);
        // Tags are authoring metadata: nothing in the collision baker, the pathfinder or the renderer reads
        // them, so the command touches no tile and claims none.
        Assert.Empty(tags.DirtyRects);
        Assert.Equal(new[] { "quest", "hidden" }, doc.FindObject(id)!.Tags);

        Assert.True(ed.Undo());
        Assert.Null(doc.FindObject(id)!.Tags);

        Assert.True(ed.Redo());
        Assert.Equal(new[] { "quest", "hidden" }, doc.FindObject(id)!.Tags);
    }

    [Fact]
    public void Set_tags_restores_the_previous_list_rather_than_clearing_it()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);
        long id = Place(ed, "tree", 5, 5, tags: new[] { "original" });

        ed.Execute(new SetObjectTagsCommand(id, new[] { "replacement" }));
        Assert.Equal(new[] { "replacement" }, doc.FindObject(id)!.Tags);

        Assert.True(ed.Undo());
        Assert.Equal(new[] { "original" }, doc.FindObject(id)!.Tags);
    }

    [Fact]
    public void Set_tags_of_an_unknown_id_throws()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);
        Assert.Throws<TileWorldException>(() => ed.Execute(new SetObjectTagsCommand(77, new[] { "x" })));
    }
}
