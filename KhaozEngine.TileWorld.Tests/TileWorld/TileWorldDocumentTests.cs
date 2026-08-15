using System;
using System.Linq;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public class TileWorldDocumentTests
{
    [Fact]
    public void New_document_has_defaults_and_no_regions()
    {
        var doc = new TileWorldDocument { Id = "w", DisplayName = "W" };
        Assert.Equal(4, doc.PlaneCount);
        Assert.Equal(1f, doc.TileSize);
        Assert.Equal(3f, doc.PlaneHeight);
        Assert.Empty(doc.Regions);
        Assert.Equal(1, doc.NextObjectId);
    }

    [Fact]
    public void GetOrCreateRegion_allocates_planes_with_no_layers()
    {
        var doc = new TileWorldDocument { Id = "w", DisplayName = "W", PlaneCount = 2 };
        TileRegion r = doc.GetOrCreateRegion(new RegionCoord(0, 0));
        Assert.Same(r, doc.GetRegion(new RegionCoord(0, 0)));
        Assert.Equal(2, r.Planes.Length);
        Assert.True(r.Plane(0).IsEmpty);
        Assert.Null(r.Plane(0).Underlay);
        Assert.True(r.Dirty);
    }

    [Fact]
    public void Layer_writes_allocate_lazily_and_reads_default_outside_regions()
    {
        var doc = new TileWorldDocument { Id = "w", DisplayName = "W" };
        doc.GetOrCreateRegion(new RegionCoord(0, 0));
        Assert.Equal(0, doc.GetUnderlay(10, 10, 0));
        Assert.Equal(0, doc.GetUnderlay(-500, 10, 0));
        doc.SetUnderlay(10, 10, 0, 7);
        Assert.Equal(7, doc.GetUnderlay(10, 10, 0));
        Assert.NotNull(doc.GetRegion(new RegionCoord(0, 0))!.Plane(0).Underlay);
        Assert.Null(doc.GetRegion(new RegionCoord(0, 0))!.Plane(0).Overlay);
        doc.SetOverlayShape(10, 10, 0, TileOverlayShape.CornerQuarter);
        doc.SetOverlayRotation(10, 10, 0, 5);
        doc.SetSettings(10, 10, 0, TileSettings.Blocked | TileSettings.Indoors);
        Assert.Equal(TileOverlayShape.CornerQuarter, doc.GetOverlayShape(10, 10, 0));
        Assert.Equal(1, doc.GetOverlayRotation(10, 10, 0));
        Assert.Equal(TileSettings.Blocked | TileSettings.Indoors, doc.GetSettings(10, 10, 0));
    }

    [Fact]
    public void Layer_write_outside_any_region_throws_naming_the_region()
    {
        var doc = new TileWorldDocument { Id = "w", DisplayName = "W" };
        var ex = Assert.Throws<TileWorldException>(() => doc.SetUnderlay(70, -3, 0, 1));
        Assert.Contains("(1, -1)", ex.Message);
    }

    [Fact]
    public void Trim_nulls_all_default_layers()
    {
        var doc = new TileWorldDocument { Id = "w", DisplayName = "W" };
        doc.GetOrCreateRegion(new RegionCoord(0, 0));
        doc.SetUnderlay(1, 1, 0, 3);
        doc.SetUnderlay(1, 1, 0, 0);
        TilePlaneData p = doc.GetRegion(new RegionCoord(0, 0))!.Plane(0);
        Assert.NotNull(p.Underlay);
        p.Trim(0);
        Assert.Null(p.Underlay);
        Assert.True(p.IsEmpty);
    }

    [Fact]
    public void Trim_keeps_all_zero_heights_on_a_higher_plane()
    {
        var doc = new TileWorldDocument { Id = "w", DisplayName = "W" };
        TileRegion r = doc.GetOrCreateRegion(new RegionCoord(0, 0));
        r.Plane(1).HeightsOrAlloc();
        r.Plane(1).Trim(1);
        Assert.NotNull(r.Plane(1).Heights);
        r.Plane(0).HeightsOrAlloc();
        r.Plane(0).Trim(0);
        Assert.Null(r.Plane(0).Heights);
    }

    [Fact]
    public void Objects_get_unique_ids_live_in_the_anchor_region_and_are_indexed()
    {
        var doc = new TileWorldDocument { Id = "w", DisplayName = "W" };
        doc.GetOrCreateRegion(new RegionCoord(0, 0));
        doc.GetOrCreateRegion(new RegionCoord(1, 0));
        TileObject a = doc.AddObject("tree", 3, 4, 0, 1);
        TileObject b = doc.AddObject("tree", 70, 4, 0, 0, new[] { "forest" });
        Assert.Equal(1, a.Id);
        Assert.Equal(2, b.Id);
        Assert.Equal(3, doc.NextObjectId);
        Assert.Contains(a, doc.GetRegion(new RegionCoord(0, 0))!.Objects);
        Assert.Contains(b, doc.GetRegion(new RegionCoord(1, 0))!.Objects);
        Assert.Same(b, doc.FindObject(2));
        Assert.Equal(new[] { "forest" }, b.Tags);
        Assert.Equal(2, doc.AllObjects().Count());
        Assert.Single(doc.ObjectsIn(new TileRect(0, 0, 64, 64)));
        Assert.Empty(doc.ObjectsIn(new TileRect(0, 0, 64, 64), plane: 1));
    }

    [Fact]
    public void MoveObject_rehomes_across_regions_and_RemoveObject_drops_it()
    {
        var doc = new TileWorldDocument { Id = "w", DisplayName = "W" };
        doc.GetOrCreateRegion(new RegionCoord(0, 0));
        doc.GetOrCreateRegion(new RegionCoord(0, 1));
        TileObject a = doc.AddObject("tree", 3, 4, 0, 0);
        doc.MoveObject(a.Id, 3, 100, 2);
        Assert.Empty(doc.GetRegion(new RegionCoord(0, 0))!.Objects);
        Assert.Contains(a, doc.GetRegion(new RegionCoord(0, 1))!.Objects);
        Assert.Equal(100, a.Z);
        Assert.Equal(2, a.Plane);
        Assert.True(doc.RemoveObject(a.Id));
        Assert.Null(doc.FindObject(a.Id));
        Assert.False(doc.RemoveObject(a.Id));
    }

    [Fact]
    public void AddObject_outside_any_region_throws()
    {
        var doc = new TileWorldDocument { Id = "w", DisplayName = "W" };
        Assert.Throws<TileWorldException>(() => doc.AddObject("tree", 3, 4, 0, 0));
    }

    [Fact]
    public void Markers_are_unique_by_name_and_live_in_their_region()
    {
        var doc = new TileWorldDocument { Id = "w", DisplayName = "W" };
        doc.GetOrCreateRegion(new RegionCoord(0, 0));
        doc.SetMarker("spawn", 5, 5, 0);
        doc.SetMarker("spawn", 6, 6, 1, new[] { "player" });
        TileMarker m = Assert.Single(doc.AllMarkers());
        Assert.Equal(6, m.X);
        Assert.Equal(1, m.Plane);
        Assert.Same(m, doc.FindMarker("spawn"));
        Assert.True(doc.RemoveMarker("spawn"));
        Assert.Empty(doc.AllMarkers());
    }

    [Fact]
    public void Plane_out_of_range_throws_on_both_a_read_and_a_write()
    {
        var doc = new TileWorldDocument { Id = "w", DisplayName = "W", PlaneCount = 2 };
        doc.GetOrCreateRegion(new RegionCoord(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.GetUnderlay(1, 1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.SetUnderlay(1, 1, 2, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.GetSettings(1, 1, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.SetSettings(1, 1, -1, TileSettings.Blocked));
    }

    [Fact]
    public void Overlay_round_trips_without_touching_the_underlay_layer()
    {
        var doc = new TileWorldDocument { Id = "w", DisplayName = "W" };
        doc.GetOrCreateRegion(new RegionCoord(0, 0));
        Assert.Equal(0, doc.GetOverlay(8, 9, 0));
        doc.SetOverlay(8, 9, 0, 42);
        Assert.Equal(42, doc.GetOverlay(8, 9, 0));
        Assert.Equal(0, doc.GetOverlay(9, 9, 0));
        Assert.Equal(0, doc.GetUnderlay(8, 9, 0));
        Assert.Null(doc.GetRegion(new RegionCoord(0, 0))!.Plane(0).Underlay);
    }

    [Fact]
    public void RebuildObjectIndex_repairs_the_index_after_ids_change_underneath_it()
    {
        var doc = new TileWorldDocument { Id = "w", DisplayName = "W" };
        doc.GetOrCreateRegion(new RegionCoord(0, 0));
        doc.GetOrCreateRegion(new RegionCoord(1, 0));
        TileObject a = doc.AddObject("tree", 3, 4, 0, 0);
        TileObject b = doc.AddObject("rock", 70, 6, 0, 0);
        (a.Id, b.Id) = (b.Id, a.Id);
        // The stale index still sends each id to the other object's region.
        Assert.Null(doc.FindObject(a.Id));
        doc.RebuildObjectIndex();
        Assert.Same(a, doc.FindObject(a.Id));
        Assert.Same(b, doc.FindObject(b.Id));
    }

    [Fact]
    public void TileRegion_Trim_hands_each_plane_its_own_index()
    {
        var doc = new TileWorldDocument { Id = "w", DisplayName = "W" };
        TileRegion r = doc.GetOrCreateRegion(new RegionCoord(0, 0));
        r.Plane(0).HeightsOrAlloc();
        r.Plane(1).HeightsOrAlloc();
        r.Trim();
        Assert.Null(r.Plane(0).Heights);
        Assert.NotNull(r.Plane(1).Heights);
    }

    [Fact]
    public void An_unloaded_region_cannot_be_blanked_by_GetOrCreateRegion()
    {
        var doc = new TileWorldDocument { Id = "w", DisplayName = "W" };
        doc.UnloadedRegionHashes[new RegionCoord(2, 3)] = "abc123";
        var ex = Assert.Throws<TileWorldException>(() => doc.GetOrCreateRegion(new RegionCoord(2, 3)));
        Assert.Contains("(2, 3)", ex.Message);
        Assert.Contains("not loaded", ex.Message);
        Assert.Empty(doc.Regions);
        var write = Assert.Throws<TileWorldException>(() => doc.SetUnderlay(2 * 64, 3 * 64, 0, 1));
        Assert.Contains("not loaded", write.Message);
    }

    [Fact]
    public void SetMarker_to_a_missing_region_throws_and_keeps_the_old_marker()
    {
        var doc = new TileWorldDocument { Id = "w", DisplayName = "W" };
        doc.GetOrCreateRegion(new RegionCoord(0, 0));
        doc.SetMarker("spawn", 5, 5, 0);
        Assert.Throws<TileWorldException>(() => doc.SetMarker("spawn", 500, 500, 0));
        TileMarker? m = doc.FindMarker("spawn");
        Assert.NotNull(m);
        Assert.Equal(5, m!.X);
    }

    [Fact]
    public void RemoveRegion_drops_its_objects_from_the_index()
    {
        var doc = new TileWorldDocument { Id = "w", DisplayName = "W" };
        doc.GetOrCreateRegion(new RegionCoord(0, 0));
        TileObject a = doc.AddObject("tree", 3, 4, 0, 0);
        Assert.True(doc.RemoveRegion(new RegionCoord(0, 0)));
        Assert.Null(doc.FindObject(a.Id));
        Assert.Null(doc.GetRegion(new RegionCoord(0, 0)));
    }
}
