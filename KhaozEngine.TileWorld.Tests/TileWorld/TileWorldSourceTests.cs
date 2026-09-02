using System.IO;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public class TileWorldSourceTests
{
    static string SavedWorld(TempDir tmp)
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld(4, new RegionCoord(0, 0), new RegionCoord(1, 0), new RegionCoord(0, 1));
        doc.AddObject("tree", 70, 3, 0, 0);
        TileWorldFile.Save(doc, tmp.Sub("w"));
        return tmp.Sub("w");
    }

    [Fact]
    public void Open_materialises_nothing_until_asked()
    {
        using var tmp = new TempDir();
        TileWorldSource s = TileWorldSource.Open(SavedWorld(tmp));
        Assert.Equal(3, s.KnownRegions.Count);
        Assert.Empty(s.Document.Regions);
        Assert.True(s.IsKnown(new RegionCoord(1, 0)));
        Assert.False(s.IsKnown(new RegionCoord(9, 9)));
        Assert.False(s.IsLoaded(new RegionCoord(1, 0)));
        Assert.Null(s.EnsureLoaded(new RegionCoord(5, 5)));
        TileRegion r = s.EnsureLoaded(new RegionCoord(1, 0))!;
        Assert.True(s.IsLoaded(new RegionCoord(1, 0)));
        Assert.Single(r.Objects);
        Assert.NotNull(s.Document.FindObject(1));
        Assert.Same(r, s.EnsureLoaded(new RegionCoord(1, 0)));
    }

    [Fact]
    public void EnsureLoaded_rect_loads_the_touched_known_regions_only()
    {
        using var tmp = new TempDir();
        TileWorldSource s = TileWorldSource.Open(SavedWorld(tmp));
        var loaded = s.EnsureLoaded(new TileRect(60, 0, 10, 10));
        Assert.Equal(2, loaded.Count);
        Assert.False(s.IsLoaded(new RegionCoord(0, 1)));
    }

    [Fact]
    public void EnsureLoaded_rect_stops_at_the_region_edge_and_ignores_an_empty_rect()
    {
        using var tmp = new TempDir();
        TileWorldSource s = TileWorldSource.Open(SavedWorld(tmp));
        Assert.Single(s.EnsureLoaded(new TileRect(0, 0, 64, 64)));
        Assert.Empty(s.EnsureLoaded(new TileRect(0, 0, 0, 0)));
    }

    [Fact]
    public void Unload_drops_a_clean_region_and_a_save_still_carries_it()
    {
        using var tmp = new TempDir();
        string dir = SavedWorld(tmp);
        TileWorldSource s = TileWorldSource.Open(dir);
        s.EnsureLoaded(new RegionCoord(1, 0));
        Assert.False(s.Unload(new RegionCoord(2, 2)));
        Assert.True(s.Unload(new RegionCoord(1, 0)));
        Assert.False(s.IsLoaded(new RegionCoord(1, 0)));
        Assert.Null(s.Document.FindObject(1));
        s.EnsureLoaded(new RegionCoord(0, 0));
        s.Document.SetUnderlay(1, 1, 0, 2);
        TileWorldFile.Save(s.Document, dir);
        TileWorldDocument back = TileWorldFile.Load(dir);
        Assert.Equal(3, back.Regions.Count);
        Assert.NotNull(back.FindObject(1));
        Assert.Equal(2, back.GetUnderlay(1, 1, 0));
    }

    [Fact]
    public void Unload_refuses_a_dirty_region()
    {
        using var tmp = new TempDir();
        TileWorldSource s = TileWorldSource.Open(SavedWorld(tmp));
        s.EnsureLoaded(new RegionCoord(0, 0));
        s.Document.SetUnderlay(1, 1, 0, 2);
        Assert.Throws<TileWorldException>(() => s.Unload(new RegionCoord(0, 0)));
    }

    [Fact]
    public void Unload_after_a_save_carries_the_saved_bytes_hash_not_the_open_time_one()
    {
        using var tmp = new TempDir();
        string dir = SavedWorld(tmp);
        TileWorldSource s = TileWorldSource.Open(dir);
        s.EnsureLoaded(new RegionCoord(0, 0));
        s.Document.SetUnderlay(1, 1, 0, 2);
        TileWorldFile.Save(s.Document, dir);
        Assert.True(s.Unload(new RegionCoord(0, 0)));
        TileWorldFile.Save(s.Document, dir);
        TileWorldDocument back = TileWorldFile.Load(dir);
        Assert.Equal(2, back.GetUnderlay(1, 1, 0));
    }

    [Fact]
    public void A_region_saved_then_unloaded_loads_again_with_the_edit_on_it()
    {
        using var tmp = new TempDir();
        string dir = SavedWorld(tmp);
        TileWorldSource s = TileWorldSource.Open(dir);
        s.EnsureLoaded(new RegionCoord(0, 0));
        s.Document.SetUnderlay(1, 1, 0, 2);
        TileWorldFile.Save(s.Document, dir);
        Assert.True(s.Unload(new RegionCoord(0, 0)));
        Assert.NotNull(s.EnsureLoaded(new RegionCoord(0, 0)));
        Assert.Equal(2, s.Document.GetUnderlay(1, 1, 0));
    }

    [Fact]
    public void A_region_created_after_open_becomes_known_once_it_is_saved_and_unloaded()
    {
        using var tmp = new TempDir();
        string dir = SavedWorld(tmp);
        TileWorldSource s = TileWorldSource.Open(dir);
        var c = new RegionCoord(3, 3);
        s.Document.GetOrCreateRegion(c);
        s.Document.SetUnderlay(c.OriginX + 1, c.OriginZ + 1, 0, 5);
        TileWorldFile.Save(s.Document, dir);
        Assert.True(s.Unload(c));
        Assert.True(s.IsKnown(c));
        Assert.NotNull(s.EnsureLoaded(c));
        Assert.Equal(5, s.Document.GetUnderlay(c.OriginX + 1, c.OriginZ + 1, 0));
    }

    // The marker index: a client that opens a world and wants the spawn BEFORE it streams anything used to have to
    // materialise regions one at a time until one carried the marker, then unload the ones that did not.
    [Fact]
    public void The_spawn_marker_is_findable_with_no_region_loaded()
    {
        using var tmp = new TempDir();
        TileWorldDocument doc =
            TileWorldTestData.FlatWorld(4, new RegionCoord(0, 0), new RegionCoord(1, 0), new RegionCoord(0, 1));
        doc.SetMarker("spawn", 70, 3, 1, new[] { "start" });
        doc.SetMarker("exit", 5, 70, 0);
        TileWorldFile.Save(doc, tmp.Sub("m"));

        TileWorldSource s = TileWorldSource.Open(tmp.Sub("m"));
        Assert.Empty(s.Document.Regions);
        TileMarker spawn = s.FindMarker("spawn")!;
        Assert.NotNull(spawn);
        Assert.Equal(new TileCoord(70, 3, 1), spawn.Coord);
        Assert.Equal(new[] { "start" }, spawn.Tags);
        Assert.Equal(2, s.Markers.Count);
        Assert.Null(s.FindMarker("nowhere"));
        // Still nothing materialised: the whole point is that the lookup costs no region read.
        Assert.Empty(s.Document.Regions);
    }

    // A save that only has SOME regions loaded must not drop the markers of the ones it never saw. The index is
    // rebuilt for loaded regions and carried forward from the previous manifest for the rest.
    [Fact]
    public void A_partial_save_keeps_the_markers_of_regions_it_never_loaded()
    {
        using var tmp = new TempDir();
        string dir = tmp.Sub("p");
        TileWorldDocument doc = TileWorldTestData.FlatWorld(4, new RegionCoord(0, 0), new RegionCoord(1, 0));
        doc.SetMarker("spawn", 70, 3, 0);
        doc.SetMarker("gate", 5, 5, 0);
        TileWorldFile.Save(doc, dir);

        TileWorldSource s = TileWorldSource.Open(dir);
        TileRegion home = s.EnsureLoaded(new RegionCoord(0, 0))!;
        home.Markers.Clear();
        home.Dirty = true;
        TileWorldFile.Save(s.Document, dir);

        TileWorldSource after = TileWorldSource.Open(dir);
        // The loaded region's marker is gone because the region no longer carries it, and the unloaded region's
        // marker survived a save that never read its file.
        Assert.Null(after.FindMarker("gate"));
        Assert.Equal(new TileCoord(70, 3, 0), after.FindMarker("spawn")!.Coord);
    }

    // A marker the index hands back is a COPY. Editing it must not quietly rewrite what the region carries, or a
    // caller that nudged the spawn for its own use would be reading a world nobody saved.
    [Fact]
    public void A_marker_read_off_the_index_is_a_copy()
    {
        using var tmp = new TempDir();
        TileWorldDocument doc = TileWorldTestData.FlatWorld(4, new RegionCoord(0, 0));
        doc.SetMarker("spawn", 5, 5, 0);
        TileWorldFile.Save(doc, tmp.Sub("c"));

        TileWorldSource s = TileWorldSource.Open(tmp.Sub("c"));
        TileMarker first = s.FindMarker("spawn")!;
        first.X = 99;
        Assert.Equal(5, s.FindMarker("spawn")!.X);
    }

    [Fact]
    public void A_lazily_loaded_region_is_hash_checked()
    {
        using var tmp = new TempDir();
        string dir = SavedWorld(tmp);
        File.WriteAllBytes(Path.Combine(dir, "regions", "r_0_1.json"), new byte[] { (byte)'{', (byte)'}' });
        TileWorldSource s = TileWorldSource.Open(dir);
        Assert.Throws<TileWorldException>(() => s.EnsureLoaded(new RegionCoord(0, 1)));
    }
}
