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
        Assert.Equal(TileWorldRefusal.RegionMissing, ex.Reason);
        Assert.Equal(new RegionCoord(1, -1), ex.Region);
        Assert.Equal(70, ex.TileX);
        Assert.Equal(-3, ex.TileZ);
        doc.GetOrCreateRegion(new RegionCoord(1, -1));
        doc.SetUnderlay(70, -3, 0, 1);
        Assert.Equal(1, doc.GetUnderlay(70, -3, 0));
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

        TileWorldException missing = Assert.Throws<TileWorldException>(() => doc.MoveObject(a.Id, 3, 4, 0));
        Assert.Equal(TileWorldRefusal.ObjectMissing, missing.Reason);
        Assert.Equal(a.Id, missing.ObjectId);
    }

    [Fact]
    public void MoveObject_checks_the_destination_before_refusing_a_stale_index()
    {
        var doc = new TileWorldDocument { Id = "w", DisplayName = "W" };
        TileRegion from = doc.GetOrCreateRegion(new RegionCoord(0, 0));
        TileObject o = doc.AddObject("tree", 3, 4, 0, 0);
        Assert.True(from.Objects.Remove(o));
        from.Dirty = false;

        TileWorldException destination = Assert.Throws<TileWorldException>(() => doc.MoveObject(o.Id, 70, 4, 2));
        Assert.Equal(TileWorldRefusal.RegionMissing, destination.Reason);
        Assert.Equal(new RegionCoord(1, 0), destination.Region);
        Assert.Equal(70, destination.TileX);
        Assert.Equal(4, destination.TileZ);
        Assert.False(from.Dirty);

        TileRegion to = doc.GetOrCreateRegion(new RegionCoord(1, 0));
        to.Dirty = false;
        TileWorldException stale = Assert.Throws<TileWorldException>(() => doc.MoveObject(o.Id, 70, 4, 2));
        Assert.Equal(TileWorldRefusal.ObjectIndexStale, stale.Reason);
        Assert.Equal(o.Id, stale.ObjectId);
        Assert.Equal(from.Coord, stale.Region);
        Assert.Equal(new TileCoord(3, 4, 0), o.Coord);
        Assert.Empty(from.Objects);
        Assert.Empty(to.Objects);
        Assert.False(from.Dirty);
        Assert.False(to.Dirty);

        // Restoring the indexed object fixes the sole defect in the second refusal.
        from.Objects.Add(o);
        doc.MoveObject(o.Id, 70, 4, 2);
        Assert.Same(o, doc.FindObject(o.Id));
        Assert.Equal(new TileCoord(70, 4, 2), o.Coord);
        Assert.Empty(from.Objects);
        Assert.Same(o, Assert.Single(to.Objects));
    }

    [Fact]
    public void AddObject_outside_any_region_throws()
    {
        var doc = new TileWorldDocument { Id = "w", DisplayName = "W" };
        var ex = Assert.Throws<TileWorldException>(() => doc.AddObject("tree", 3, 4, 0, 0));
        Assert.Equal(TileWorldRefusal.RegionMissing, ex.Reason);
        Assert.Equal(new RegionCoord(0, 0), ex.Region);
        Assert.Equal(3, ex.TileX);
        Assert.Equal(4, ex.TileZ);
        Assert.Equal(1, doc.NextObjectId);
        Assert.Empty(doc.AllObjects());
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
        Assert.Equal(TileWorldRefusal.RegionNotLoaded, ex.Reason);
        Assert.Equal(new RegionCoord(2, 3), ex.Region);
        Assert.Null(ex.TileX);
        Assert.Null(ex.TileZ);
        Assert.Empty(doc.Regions);
        var write = Assert.Throws<TileWorldException>(() => doc.SetUnderlay(2 * 64, 3 * 64, 0, 1));
        Assert.Equal(TileWorldRefusal.RegionNotLoaded, write.Reason);
        Assert.Equal(new RegionCoord(2, 3), write.Region);
        Assert.Equal(128, write.TileX);
        Assert.Equal(192, write.TileZ);

        var loaded = new TileRegion(new RegionCoord(2, 3), doc.PlaneCount);
        doc.AttachRegion(loaded);
        Assert.Same(loaded, doc.GetOrCreateRegion(new RegionCoord(2, 3)));
        doc.SetUnderlay(128, 192, 0, 1);
        Assert.Equal(1, doc.GetUnderlay(128, 192, 0));
    }

    [Fact]
    public void SetMarker_to_a_missing_region_throws_and_keeps_the_old_marker()
    {
        var doc = new TileWorldDocument { Id = "w", DisplayName = "W" };
        doc.GetOrCreateRegion(new RegionCoord(0, 0));
        doc.SetMarker("spawn", 5, 5, 0);
        var ex = Assert.Throws<TileWorldException>(() => doc.SetMarker("spawn", 500, 500, 0));
        Assert.Equal(TileWorldRefusal.RegionMissing, ex.Reason);
        Assert.Equal(new RegionCoord(7, 7), ex.Region);
        Assert.Equal(500, ex.TileX);
        Assert.Equal(500, ex.TileZ);
        TileMarker? m = doc.FindMarker("spawn");
        Assert.NotNull(m);
        Assert.Equal(5, m!.X);
    }

    [Fact]
    public void AddObjectWithId_inserts_at_the_given_id_and_never_lets_the_allocator_fall_behind_it()
    {
        var doc = new TileWorldDocument { Id = "w", DisplayName = "W" };
        doc.GetOrCreateRegion(new RegionCoord(0, 0));

        TileObject high = doc.AddObjectWithId(50, "tree", 3, 4, 0, 5, new[] { "old" });

        Assert.Equal(50, high.Id);
        Assert.Equal(1, high.Rotation);   // masked into 0..3, the same as AddObject
        Assert.Equal(new[] { "old" }, high.Tags);
        Assert.Same(high, doc.FindObject(50));
        Assert.Contains(high, doc.GetRegion(new RegionCoord(0, 0))!.Objects);
        // The allocator jumps past a re-inserted id so the next fresh one cannot collide with it.
        Assert.Equal(51, doc.NextObjectId);

        doc.AddObjectWithId(10, "tree", 5, 6, 0, 0, null);
        Assert.Equal(51, doc.NextObjectId);
        Assert.Equal(51, doc.AddObject("tree", 7, 8, 0, 0).Id);
    }

    [Fact]
    public void AddObjectWithId_refuses_an_id_that_is_already_in_use()
    {
        var doc = new TileWorldDocument { Id = "w", DisplayName = "W" };
        doc.GetOrCreateRegion(new RegionCoord(0, 0));
        TileObject first = doc.AddObject("tree", 3, 4, 0, 0);

        var ex = Assert.Throws<TileWorldException>(() => doc.AddObjectWithId(first.Id, "tree", 9, 9, 0, 0, null));

        Assert.Equal(TileWorldRefusal.ObjectIdDuplicate, ex.Reason);
        Assert.Equal(first.Id, ex.ObjectId);
        Assert.Single(doc.AllObjects());
        Assert.Same(first, doc.FindObject(first.Id));

        TileObject other = doc.AddObjectWithId(doc.NextObjectId, "tree", 9, 9, 0, 0, null);
        Assert.Same(other, doc.FindObject(other.Id));
    }

    [Fact]
    public void RestoreRegion_reattaches_a_detached_region_and_reindexes_its_objects()
    {
        var doc = new TileWorldDocument { Id = "w", DisplayName = "W" };
        var coord = new RegionCoord(1, 2);
        TileRegion region = doc.GetOrCreateRegion(coord);
        TileObject o = doc.AddObject("tree", coord.OriginX + 3, coord.OriginZ + 4, 0, 0);
        Assert.True(doc.RemoveRegion(coord));
        Assert.Null(doc.FindObject(o.Id));

        doc.RestoreRegion(region);

        Assert.Same(region, doc.GetRegion(coord));
        Assert.Same(o, doc.FindObject(o.Id));
        // The coordinate is taken now, so a second restore is the same refusal AttachRegion gives the loader.
        Assert.Throws<ArgumentException>(() => doc.RestoreRegion(region));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Attach_and_restore_refuse_a_region_whose_plane_count_does_not_match(bool restore)
    {
        var doc = new TileWorldDocument { Id = "w", DisplayName = "W", PlaneCount = 4 };

        var coord = new RegionCoord(0, 0);
        var ex = Assert.Throws<TileWorldException>(() =>
        {
            var region = new TileRegion(coord, 2);
            if (restore) doc.RestoreRegion(region);
            else doc.AttachRegion(region);
        });

        Assert.Equal(TileWorldRefusal.RegionPlaneCountMismatch, ex.Reason);
        Assert.Equal(coord, ex.Region);
        Assert.Equal(2, ex.ActualPlaneCount);
        Assert.Equal(4, ex.ExpectedPlaneCount);
        Assert.Empty(doc.Regions);

        var matching = new TileRegion(coord, 4);
        if (restore) doc.RestoreRegion(matching);
        else doc.AttachRegion(matching);
        Assert.Same(matching, doc.GetRegion(coord));
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
