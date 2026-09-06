using System.Linq;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Editing;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Editing;

/// <summary>Headless tests for the marker and region commands: the marker round trip over a fresh name and
/// over one that was already taken, the fact that neither marker command claims a dirty rect, and the region
/// pair, whose undo has to put a deleted region's layers, objects, markers AND derived collision back exactly
/// as they were.</summary>
public class MarkerAndRegionCommandTests
{
    static readonly TileWorldCatalogs Cat = TileWorldTestData.EditingCatalogs();

    static TileEditingDocument Editing(out TileWorldDocument doc, params RegionCoord[] regions)
    {
        doc = TileWorldTestData.FlatWorld(4, regions);
        return new TileEditingDocument(doc, Cat);
    }

    static bool Blocked(TileEditingDocument ed, int x, int z, int plane = 0) =>
        (ed.Collision.Get(x, z, plane) & TileCollisionFlags.Blocked) != 0;

    [Fact]
    public void Setting_a_fresh_marker_reverts_by_removing_it_again()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);
        var set = new SetMarkerCommand("spawn", 5, 6, 1, new[] { "player" });

        ed.Execute(set);

        Assert.Equal("Set marker", set.Label);
        TileMarker m = doc.FindMarker("spawn")!;
        Assert.Equal(5, m.X);
        Assert.Equal(6, m.Z);
        Assert.Equal(1, m.Plane);
        Assert.Equal(new[] { "player" }, m.Tags);

        Assert.True(ed.Undo());
        Assert.Null(doc.FindMarker("spawn"));
        Assert.Empty(doc.AllMarkers());

        Assert.True(ed.Redo());
        TileMarker again = doc.FindMarker("spawn")!;
        Assert.Equal(5, again.X);
        Assert.Equal(1, again.Plane);
        Assert.Equal(new[] { "player" }, again.Tags);
    }

    [Fact]
    public void Setting_a_marker_over_one_that_exists_restores_its_old_position_and_tags()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);
        doc.SetMarker("bank", 10, 11, 0, new[] { "west" });

        ed.Execute(new SetMarkerCommand("bank", 40, 41, 2, new[] { "east", "new" }));

        TileMarker moved = doc.FindMarker("bank")!;
        Assert.Equal(40, moved.X);
        Assert.Equal(2, moved.Plane);
        Assert.Equal(new[] { "east", "new" }, moved.Tags);
        Assert.Single(doc.AllMarkers());

        Assert.True(ed.Undo());
        TileMarker back = doc.FindMarker("bank")!;
        Assert.Equal(10, back.X);
        Assert.Equal(11, back.Z);
        Assert.Equal(0, back.Plane);
        Assert.Equal(new[] { "west" }, back.Tags);
        Assert.Single(doc.AllMarkers());
    }

    [Fact]
    public void Marker_commands_claim_no_dirty_rects()
    {
        // Markers carry no collision and no mesh: the baker never reads them and neither does the renderer, so
        // moving one touches no tile.
        Assert.Empty(new SetMarkerCommand("spawn", 5, 6, 0, null).DirtyRects);
        Assert.Empty(new RemoveMarkerCommand("spawn").DirtyRects);
    }

    [Fact]
    public void Removing_a_marker_round_trips_its_name_position_plane_and_tags()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);
        doc.SetMarker("altar", 12, 13, 3, new[] { "holy" });

        var remove = new RemoveMarkerCommand("altar");
        ed.Execute(remove);

        Assert.Equal("Remove marker", remove.Label);
        Assert.Null(doc.FindMarker("altar"));

        Assert.True(ed.Undo());
        TileMarker back = doc.FindMarker("altar")!;
        Assert.Equal(12, back.X);
        Assert.Equal(13, back.Z);
        Assert.Equal(3, back.Plane);
        Assert.Equal(new[] { "holy" }, back.Tags);

        Assert.True(ed.Redo());
        Assert.Null(doc.FindMarker("altar"));
    }

    [Fact]
    public void Removing_a_marker_that_is_not_there_throws()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);
        TileWorldException ex = Assert.Throws<TileWorldException>(() => ed.Execute(new RemoveMarkerCommand("ghost")));
        Assert.Contains("ghost", ex.Message);
        Assert.Empty(doc.AllMarkers());
    }

    [Fact]
    public void Creating_a_region_gives_it_collision_storage_and_the_undo_takes_it_away()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);   // region (0, 0) only
        var coord = new RegionCoord(1, 0);
        Assert.False(ed.Collision.HasRegion(coord));

        var create = new CreateRegionCommand(coord);
        ed.Execute(create);

        Assert.Equal("Create region", create.Label);
        Assert.True(create.Created);
        Assert.NotNull(doc.GetRegion(coord));
        Assert.True(ed.Collision.HasRegion(coord));
        // A fresh region is void ground, so it still reads blocked. What changed is that the map now HAS the
        // region, which is what lets a later paint make it walkable at all.
        Assert.True(Blocked(ed, 70, 5));
        Assert.Equal(doc.PlaneCount, create.DirtyRects.Count());
        Assert.All(create.DirtyRects, d => Assert.Equal(coord.Rect, d.Rect));
        Assert.Equal(new[] { 0, 1, 2, 3 }, create.DirtyRects.Select(d => d.Plane).ToArray());

        Assert.True(ed.Undo());
        Assert.Null(doc.GetRegion(coord));
        Assert.False(ed.Collision.HasRegion(coord));

        Assert.True(ed.Redo());
        Assert.NotNull(doc.GetRegion(coord));
        Assert.True(ed.Collision.HasRegion(coord));
    }

    [Fact]
    public void Creating_a_region_that_already_exists_is_a_no_op_and_the_undo_leaves_it_alone()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);
        var coord = new RegionCoord(0, 0);
        TileRegion existing = doc.GetRegion(coord)!;

        var create = new CreateRegionCommand(coord);
        ed.Execute(create);

        Assert.False(create.Created);
        Assert.Same(existing, doc.GetRegion(coord));

        Assert.True(ed.Undo());
        Assert.Same(existing, doc.GetRegion(coord));
        Assert.Equal(1, doc.GetUnderlay(5, 5, 0));
    }

    [Fact]
    public void Creating_a_region_can_be_undone_on_a_source_backed_document()
    {
        using var tmp = new TempDir();
        var existing = new RegionCoord(0, 0);
        var created = new RegionCoord(1, 0);
        string dir = tmp.Sub("create-undo");
        TileWorldFile.Save(TileWorldTestData.FlatWorld(4, existing), dir);
        TileWorldDocument doc = TileWorldFile.Load(dir);
        var ed = new TileEditingDocument(doc, Cat);

        ed.Execute(new CreateRegionCommand(created));
        Assert.NotNull(doc.GetRegion(created));

        Assert.True(ed.Undo());
        Assert.Null(doc.GetRegion(created));
        Assert.False(doc.Source!.IsKnown(created));

        TileWorldFile.Save(doc, dir);
        Assert.Null(TileWorldFile.Load(dir).GetRegion(created));
    }

    [Fact]
    public void Deleting_a_region_and_undoing_restores_its_layers_objects_markers_and_collision()
    {
        var coord = new RegionCoord(1, 0);
        TileWorldDocument doc = TileWorldTestData.FlatWorld(4, new RegionCoord(0, 0), coord);
        doc.SetUnderlay(70, 5, 0, 6);
        doc.SetOverlay(70, 5, 0, 4);
        doc.SetSettings(71, 5, 0, TileSettings.Indoors);
        doc.SetCornerHeightCm(70, 5, 0, 275);
        TileObject tree = doc.AddObject("tree", 72, 5, 0, 1, new[] { "old" });
        doc.SetMarker("east_gate", 73, 5, 0, new[] { "gate" });
        TileRegion instance = doc.GetRegion(coord)!;
        // The editing document bakes collision at construction, so the content above has to be in place first
        // or the bake never sees the tree and the assertions below read a map that was never right.
        var ed = new TileEditingDocument(doc, Cat);

        Assert.True(Blocked(ed, 72, 5));
        Assert.False(Blocked(ed, 71, 5));

        var delete = new DeleteRegionCommand(coord);
        ed.Execute(delete);

        Assert.Equal("Delete region", delete.Label);
        Assert.Null(doc.GetRegion(coord));
        Assert.Null(doc.FindObject(tree.Id));
        Assert.Null(doc.FindMarker("east_gate"));
        Assert.False(ed.Collision.HasRegion(coord));
        Assert.True(Blocked(ed, 71, 5));
        Assert.Equal(doc.PlaneCount, delete.DirtyRects.Count());
        Assert.All(delete.DirtyRects, d => Assert.Equal(coord.Rect, d.Rect));

        Assert.True(ed.Undo());

        Assert.Same(instance, doc.GetRegion(coord));
        Assert.Equal(6, doc.GetUnderlay(70, 5, 0));
        Assert.Equal(4, doc.GetOverlay(70, 5, 0));
        Assert.Equal(TileSettings.Indoors, doc.GetSettings(71, 5, 0));
        Assert.Equal(275, doc.CornerHeightCm(70, 5, 0));
        TileObject back = doc.FindObject(tree.Id)!;
        Assert.Equal(72, back.X);
        Assert.Equal(1, back.Rotation);
        Assert.Equal(new[] { "old" }, back.Tags);
        TileMarker marker = doc.FindMarker("east_gate")!;
        Assert.Equal(73, marker.X);
        Assert.Equal(new[] { "gate" }, marker.Tags);
        Assert.True(instance.Dirty);
        Assert.True(ed.Collision.HasRegion(coord));
        Assert.True(Blocked(ed, 72, 5));
        Assert.False(Blocked(ed, 71, 5));

        Assert.True(ed.Redo());
        Assert.Null(doc.GetRegion(coord));
        Assert.Null(doc.FindObject(tree.Id));

        Assert.True(ed.Undo());
        Assert.Same(instance, doc.GetRegion(coord));
        Assert.Equal(6, doc.GetUnderlay(70, 5, 0));
        Assert.Equal(72, doc.FindObject(tree.Id)!.X);
    }

    [Fact]
    public void Source_backed_delete_undo_redo_allows_the_deleted_marker_name_to_be_reused()
    {
        using var tmp = new TempDir();
        var west = new RegionCoord(0, 0);
        var east = new RegionCoord(1, 0);
        TileWorldDocument original = TileWorldTestData.FlatWorld(4, west, east);
        original.SetMarker("gate", 70, 5, 0);
        string dir = tmp.Sub("delete-undo-redo");
        TileWorldFile.Save(original, dir);
        TileWorldDocument doc = TileWorldFile.Load(dir);
        var ed = new TileEditingDocument(doc, Cat);

        ed.Execute(new DeleteRegionCommand(east));
        Assert.False(doc.Source!.IsKnown(east));
        Assert.Null(doc.Source.FindMarker("gate"));

        ed.Execute(new SetMarkerCommand("gate", 5, 5, 0, null));
        Assert.Equal(new TileCoord(5, 5, 0), doc.FindMarker("gate")!.Coord);

        Assert.True(ed.Undo());
        Assert.Null(doc.FindMarker("gate"));
        Assert.True(ed.Undo());
        Assert.Equal(new TileCoord(70, 5, 0), doc.FindMarker("gate")!.Coord);

        Assert.True(ed.Redo());
        Assert.Null(doc.FindMarker("gate"));
        Assert.True(ed.Redo());
        Assert.Equal(new TileCoord(5, 5, 0), doc.FindMarker("gate")!.Coord);
    }

    [Fact]
    public void Deleting_a_dirty_source_backed_region_does_not_take_the_unload_refusal_path()
    {
        using var tmp = new TempDir();
        var west = new RegionCoord(0, 0);
        var east = new RegionCoord(1, 0);
        string dir = tmp.Sub("delete-dirty-command");
        TileWorldFile.Save(TileWorldTestData.FlatWorld(4, west, east), dir);
        TileWorldDocument doc = TileWorldFile.Load(dir);
        doc.SetUnderlay(70, 5, 0, 6);
        var ed = new TileEditingDocument(doc, Cat);

        ed.Execute(new DeleteRegionCommand(east));

        Assert.Null(doc.GetRegion(east));
        Assert.False(doc.Source!.IsKnown(east));
    }

    [Fact]
    public void Deleting_a_region_that_is_not_there_throws()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);
        TileWorldException ex = Assert.Throws<TileWorldException>(() => ed.Execute(new DeleteRegionCommand(new RegionCoord(3, 3))));
        Assert.Contains("(3, 3)", ex.Message);
        Assert.Single(doc.Regions);
    }
}
