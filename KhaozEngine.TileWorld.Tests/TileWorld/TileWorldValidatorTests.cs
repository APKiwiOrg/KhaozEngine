using System;
using System.Linq;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public class TileWorldValidatorTests
{
    [Fact]
    public void A_clean_greybox_world_has_no_issues()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.AddObject("tree", 5, 5, 0, 0);
        doc.SetMarker("spawn", 6, 6, 0);
        Assert.Empty(TileWorldValidator.Validate(doc, TileWorldCatalogs.Greybox()));
    }

    [Fact]
    public void Dangling_ids_bad_planes_and_footprints_are_reported_with_codes()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.SetUnderlay(1, 1, 0, 99);
        doc.SetOverlay(2, 2, 0, 98);
        doc.AddObject("nope", 3, 3, 0, 0);
        doc.AddObject("rock_large", 63, 63, 0, 0);
        TileObject bad = doc.AddObject("tree", 4, 4, 0, 0);
        bad.Plane = 7;
        TileRegion r = doc.GetRegion(new RegionCoord(0, 0))!;
        r.Objects.Add(new TileObject { Id = bad.Id, ArchetypeId = "tree", X = 5, Z = 5 });
        r.Markers.Add(new TileMarker { Name = "m", X = 200, Z = 200 });
        r.Markers.Add(new TileMarker { Name = "m", X = 1, Z = 1 });

        var codes = TileWorldValidator.Validate(doc, TileWorldCatalogs.Greybox()).Select(i => i.Code).ToList();
        Assert.Equal(2, codes.Count(c => c == "material.missing"));
        Assert.Contains("archetype.missing", codes);
        Assert.Contains("object.footprint", codes);
        Assert.Contains("object.plane", codes);
        Assert.Contains("object.duplicateId", codes);
        Assert.Contains("marker.region", codes);
        Assert.Contains("marker.duplicateName", codes);
        var ex = Assert.Throws<TileWorldException>(() => TileWorldValidator.ValidateOrThrow(doc, TileWorldCatalogs.Greybox()));
        Assert.Contains("issues", ex.Message);
    }

    [Fact]
    public void Footprint_across_an_existing_neighbour_region_is_fine()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld(4, new RegionCoord(0, 0), new RegionCoord(1, 0), new RegionCoord(0, 1), new RegionCoord(1, 1));
        doc.AddObject("rock_large", 63, 63, 0, 0);
        Assert.Empty(TileWorldValidator.Validate(doc, TileWorldCatalogs.Greybox()));
    }

    [Fact]
    public void A_region_left_behind_by_a_plane_count_change_is_reported()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.PlaneCount = 5;

        TileWorldIssue issue = Assert.Single(TileWorldValidator.Validate(doc, TileWorldCatalogs.Greybox()));
        Assert.Equal("region.planeCount", issue.Code);
        Assert.Equal(new RegionCoord(0, 0), issue.Region);
        Assert.Contains("has 4 planes", issue.Message);
        Assert.Contains("5", issue.Message);
    }

    [Fact]
    public void Header_fields_out_of_range_are_reported_and_the_throw_names_the_count()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.PlaneCount = 0;
        doc.TileSize = 0f;
        doc.PlaneHeight = -1f;

        var codes = TileWorldValidator.Validate(doc, TileWorldCatalogs.Greybox()).Select(i => i.Code).ToList();
        Assert.Contains("header.planeCount", codes);
        Assert.Contains("header.tileSize", codes);
        Assert.Contains("header.planeHeight", codes);

        var ex = Assert.Throws<TileWorldException>(() => TileWorldValidator.ValidateOrThrow(doc, TileWorldCatalogs.Greybox()));
        Assert.Contains($"has {codes.Count} validation issues", ex.Message);
        Assert.Contains("[header.planeCount]", ex.Message);
        Assert.Equal(4, ex.Message.Split(" | ").Length);
    }

    [Fact]
    public void An_object_stored_in_the_wrong_region_and_an_unknown_overlay_shape_are_reported()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        TileRegion r = doc.GetRegion(new RegionCoord(0, 0))!;
        r.Objects.Add(new TileObject { Id = 500, ArchetypeId = "tree", X = -5, Z = -5 });
        r.Plane(0).OverlayShapeOrAlloc()[TilePlaneData.Index(3, 3)] = 9;

        var issues = TileWorldValidator.Validate(doc, TileWorldCatalogs.Greybox());
        var codes = issues.Select(i => i.Code).ToList();
        Assert.Contains("object.region", codes);
        Assert.Contains("overlay.shape", codes);
        Assert.Equal(1, codes.Count(c => c == "overlay.shape"));
    }

    [Fact]
    public void One_missing_material_id_is_reported_once_per_region_plane()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        for (int i = 0; i < 8; i++) doc.SetUnderlay(i, 0, 0, 99);
        doc.SetOverlay(0, 1, 0, 99);
        doc.SetUnderlay(0, 2, 1, 99);

        var issues = TileWorldValidator.Validate(doc, TileWorldCatalogs.Greybox())
            .Where(i => i.Code == "material.missing").ToList();
        Assert.Equal(2, issues.Count);
        Assert.All(issues, i => Assert.Equal(new RegionCoord(0, 0), i.Region));
        Assert.Equal(new[] { 0, 1 }, issues.Select(i => i.Tile!.Value.Plane).ToArray());
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        Assert.Throws<ArgumentNullException>(() => TileWorldValidator.Validate(null!, TileWorldCatalogs.Greybox()));
        Assert.Throws<ArgumentNullException>(() => TileWorldValidator.Validate(doc, null!));
    }
}
