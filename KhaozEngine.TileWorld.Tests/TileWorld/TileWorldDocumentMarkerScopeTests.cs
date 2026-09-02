using System.Linq;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>Marker name uniqueness across a world only PART of which is in memory (#788). A streaming session
/// used to be able to author a second marker with a name an unloaded region already carried, and the two only
/// collided once both regions were loaded.</summary>
public class TileWorldDocumentMarkerScopeTests
{
    // Two regions, one marker each: "camp" in region (0, 0) and "spawn" in region (1, 0).
    static string SavedWorld(TempDir tmp)
    {
        string dir = tmp.Sub("world");
        TileWorldDocument doc = TileWorldTestData.FlatWorld(4, new RegionCoord(0, 0), new RegionCoord(1, 0));
        doc.SetMarker("camp", 5, 5, 0);
        doc.SetMarker("spawn", 70, 5, 0);
        TileWorldFile.Save(doc, dir);
        return dir;
    }

    [Fact]
    public void A_name_an_unloaded_region_carries_is_refused()
    {
        using var tmp = new TempDir();
        TileWorldSource src = TileWorldSource.Open(SavedWorld(tmp));
        src.EnsureLoaded(new RegionCoord(0, 0));
        TileWorldDocument doc = src.Document;

        TileWorldException ex = Assert.Throws<TileWorldException>(() => doc.SetMarker("spawn", 6, 6, 0));
        Assert.Contains("spawn", ex.Message);
        Assert.Contains("1, 0", ex.Message);   // names the region holding it, so the fix is obvious

        TileRegion loaded = doc.GetRegion(new RegionCoord(0, 0))!;
        Assert.Equal(new[] { "camp" }, loaded.Markers.Select(m => m.Name).ToArray());
        Assert.False(loaded.Dirty);            // nothing mutated on the way to the refusal
    }

    [Fact]
    public void A_name_the_loaded_region_carries_still_re_homes()
    {
        using var tmp = new TempDir();
        TileWorldSource src = TileWorldSource.Open(SavedWorld(tmp));
        src.EnsureLoaded(new RegionCoord(0, 0));
        TileWorldDocument doc = src.Document;

        // The manifest index carries "camp" too, homed in the region that IS loaded, so the live region wins
        // and re-homing the marker inside it stays legal.
        TileMarker moved = doc.SetMarker("camp", 7, 7, 0);
        Assert.Equal(7, moved.X);
        Assert.Single(doc.GetRegion(new RegionCoord(0, 0))!.Markers, m => m.Name == "camp");
    }

    [Fact]
    public void A_fresh_name_is_still_accepted_while_a_region_is_unloaded()
    {
        using var tmp = new TempDir();
        TileWorldSource src = TileWorldSource.Open(SavedWorld(tmp));
        src.EnsureLoaded(new RegionCoord(0, 0));

        TileMarker m = src.Document.SetMarker("dock", 6, 6, 0);
        Assert.Equal(6, m.Z);
    }

    [Fact]
    public void A_document_with_no_source_keeps_the_loaded_set_check()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld(4, new RegionCoord(0, 0));
        Assert.Null(doc.Source);
        doc.SetMarker("camp", 5, 5, 0);
        doc.SetMarker("camp", 6, 6, 0);   // re-home, the pre-existing behaviour
        Assert.Single(doc.GetRegion(new RegionCoord(0, 0))!.Markers);
        Assert.Equal(6, doc.FindMarker("camp")!.X);
    }

    [Fact]
    public void A_region_that_lost_the_name_before_it_was_unloaded_does_not_refuse()
    {
        using var tmp = new TempDir();
        string dir = SavedWorld(tmp);
        TileWorldSource src = TileWorldSource.Open(dir);
        src.EnsureLoaded(new RegionCoord(0, 0));
        src.EnsureLoaded(new RegionCoord(1, 0));
        TileWorldDocument doc = src.Document;

        Assert.True(doc.RemoveMarker("spawn"));
        TileWorldFile.Save(doc, dir);
        Assert.True(src.Unload(new RegionCoord(1, 0)));

        // The manifest row was the Open-time truth and is not any more. Unload refreshes the index from the
        // region it is dropping, so the name really is free.
        Assert.DoesNotContain("spawn", src.Markers);
        TileMarker m = doc.SetMarker("spawn", 6, 6, 0);
        Assert.Equal(6, m.X);
    }
}
