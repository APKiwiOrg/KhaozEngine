using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.WorldMap;
using KhaozEngine.WorldMap;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.WorldMap;

/// <summary>TileWorld terrain, water and authored placements adapted into map metres.</summary>
public sealed class TileWorldMapSourceTests
{
    /// <summary>Loaded regions west of zero contribute their full bounds.</summary>
    [Fact]
    public void Bounds_UnionOfRegions_IncludesNegativeRegions()
    {
        var source = new TileWorldMapSource(FlatWorld(1f, new(-1, 0), new(0, 0)), Catalogs());
        Assert.Equal(new MapRect(-64, 0, 128, 64), source.Bounds);
        Assert.Equal(1f, source.SurfaceCellSize);
    }

    /// <summary>Resolved overlays replace whole tiles, while missing underlays stay void.</summary>
    [Fact]
    public void Surface_UnderlayColour_OverlayReplaces_VoidOutsideRegions()
    {
        TileWorldDocument doc = FlatWorld();
        doc.SetOverlay(1, 0, 0, 2);
        doc.SetOverlayShape(1, 0, 0, TileOverlayShape.CornerQuarter);
        doc.SetOverlay(2, 0, 0, 65535);
        doc.SetUnderlay(3, 0, 0, 0);
        doc.SetOverlay(3, 0, 0, 2);
        doc.SetUnderlay(4, 0, 0, 65535);
        var source = new TileWorldMapSource(doc, Catalogs());
        WorldMapSurfaceGrid grid = source.SampleSurface(new MapRect(-1, 0, 6, 1), source.SurfaceCellSize);

        Assert.Equal(new Vector2(-1, 0), grid.Origin);
        Assert.Equal(6, grid.Width);
        Assert.Equal(1, grid.Height);
        Assert.Equal(Color.Transparent, grid.Ground[0]);
        Assert.Equal(Color.FromHex("#4d8a3a"), grid.Ground[1]);
        Assert.Equal(Color.FromHex("#8a6a3a"), grid.Ground[2]);
        Assert.Equal(Color.FromHex("#4d8a3a"), grid.Ground[3]);
        foreach (int index in new[] { 0, 4, 5 })
        {
            Assert.Equal(Color.Transparent, grid.Ground[index]);
            Assert.True(float.IsNaN(grid.SurfaceHeight[index]));
            Assert.True(float.IsNaN(grid.WaterSurface[index]));
        }
    }

    /// <summary>Surface samples use all four corner heights in metres.</summary>
    [Fact]
    public void Surface_HeightIsMeanOfFourCorners()
    {
        TileWorldDocument doc = FlatWorld();
        doc.SetCornerHeightCm(10, 20, 0, 100);
        doc.SetCornerHeightCm(11, 20, 0, 200);
        doc.SetCornerHeightCm(10, 21, 0, 300);
        doc.SetCornerHeightCm(11, 21, 0, 600);
        var source = new TileWorldMapSource(doc, Catalogs());
        WorldMapSurfaceGrid grid = source.SampleSurface(new MapRect(10, 20, 1, 1), source.SurfaceCellSize);
        Assert.Equal(3f, grid.SurfaceHeight[0]);
        Assert.True(float.IsNaN(grid.WaterSurface[0]));
    }

    /// <summary>Each water tile reads its body's rim surface, not its bed or tile scale.</summary>
    [Fact]
    public void Surface_WaterTileReportsBodySurface_DryTileIsNaN()
    {
        TileWorldDocument doc = FlatWorld(2f);
        doc.SetUnderlay(10, 20, 0, 4);
        doc.SetUnderlay(11, 20, 0, 4);
        doc.SetCornerHeightCm(12, 21, 0, 250);
        doc.SetOverlay(12, 20, 0, 4);
        var source = new TileWorldMapSource(doc, Catalogs());
        WorldMapSurfaceGrid grid = source.SampleSurface(new MapRect(20, 40, 6, 2), source.SurfaceCellSize);
        Assert.Equal(2.48f, grid.WaterSurface[0], 5);
        Assert.Equal(2.48f, grid.WaterSurface[1], 5);
        Assert.True(float.IsNaN(grid.WaterSurface[2]));
    }

    /// <summary>Water lookups keep independent body surfaces on both sides of a region border.</summary>
    [Fact]
    public void Surface_WaterBodiesInNegativeAndPositiveRegions_KeepTheirOwnSurfaces()
    {
        TileWorldDocument doc = FlatWorld(1f, new(-1, 0), new(0, 0));
        doc.SetUnderlay(-1, 0, 0, 4);
        doc.SetUnderlay(0, 0, 0, 4);
        doc.SetCornerHeightCm(-1, 0, 0, 100);
        doc.SetCornerHeightCm(1, 1, 0, 300);
        var source = new TileWorldMapSource(doc, Catalogs());
        WorldMapSurfaceGrid grid = source.SampleSurface(new MapRect(-1, 0, 2, 1), source.SurfaceCellSize);
        Assert.Equal(0.98f, grid.WaterSurface[0], 5);
        Assert.Equal(2.98f, grid.WaterSurface[1], 5);
    }

    /// <summary>Fractional negative areas round outward on the tile lattice, south row first.</summary>
    [Fact]
    public void Surface_NegativeFractionalArea_AlignsToScaledLattice_SouthRowFirst()
    {
        TileWorldDocument doc = FlatWorld(2f, new(-1, -1), new(-1, 0));
        doc.SetOverlay(-1, 0, 0, 2);
        var source = new TileWorldMapSource(doc, Catalogs());
        WorldMapSurfaceGrid grid = source.SampleSurface(new MapRect(-1.5f, -0.5f, 1f, 1f), source.SurfaceCellSize);
        Assert.Equal(new Vector2(-2, -2), grid.Origin);
        Assert.Equal(2f, grid.CellSize);
        Assert.Equal(1, grid.Width);
        Assert.Equal(2, grid.Height);
        Assert.Equal(Color.FromHex("#4d8a3a"), grid.Ground[grid.Index(0, 0)]);
        Assert.Equal(Color.FromHex("#8a6a3a"), grid.Ground[grid.Index(0, 1)]);
    }

    /// <summary>Roofs on upper planes remain visible without including other upper objects.</summary>
    [Fact]
    public void Footprints_RoofOnUpperPlaneIncluded_OtherUpperPlaneObjectsExcluded()
    {
        TileWorldDocument doc = FlatWorld();
        TileObject ground = doc.AddObject("tree", 10, 10, 0, 0);
        TileObject roof = doc.AddObject("roof_flat", 12, 10, 2, 0);
        doc.AddObject("tree", 14, 10, 2, 0);
        doc.AddObject("undefined", 16, 10, 0, 0);
        var source = new TileWorldMapSource(doc, Catalogs());
        var footprints = new List<WorldMapFootprint>();
        source.CollectFootprints(new MapRect(0, 0, 64, 64), 0f, footprints);
        Assert.Collection(footprints,
            f => { Assert.Equal(ground.Id, f.Id); Assert.False(f.IsRoof); },
            f => { Assert.Equal(roof.Id, f.Id); Assert.True(f.IsRoof); });
    }

    /// <summary>A roof anchored west of the chunk still contributes its overlapping footprint.</summary>
    [Fact]
    public void Footprints_LargeObjectAnchoredOutsideArea_IsIncluded()
    {
        TileWorldDocument doc = FlatWorld();
        TileObject roof = doc.AddObject("roof_large", 60, 0, 2, 0);
        doc.AddObject("roof_large", 58, 12, 2, 0);
        var source = new TileWorldMapSource(doc, Catalogs());
        var footprints = new List<WorldMapFootprint>();
        source.CollectFootprints(new MapRect(64, 0, 64, 64), 0f, footprints);
        WorldMapFootprint footprint = Assert.Single(footprints);
        Assert.Equal(roof.Id, footprint.Id);
        Assert.Equal(new Vector2(63, 3), footprint.Center);
        Assert.Equal(new Vector2(3, 3), footprint.HalfExtents);
    }

    /// <summary>Size budgets reject small archetypes, retain the threshold and use map metres.</summary>
    [Theory]
    [InlineData(1f, 0, 1)]
    [InlineData(2f, 1, 1)]
    public void Footprints_MinExtentFiltersBeforeMaterialization(float tileSize, int expectedTrees, int expectedHalls)
    {
        TileWorldDocument doc = FlatWorld(tileSize);
        doc.AddObject("tree", 10, 10, 0, 0);
        doc.AddObject("hall", 20, 20, 0, 1);
        var source = new TileWorldMapSource(doc, Catalogs());
        var footprints = new List<WorldMapFootprint>();

        source.CollectFootprints(new MapRect(0, 0, 64 * tileSize, 64 * tileSize), 2f, footprints);

        Assert.Equal(expectedTrees + expectedHalls, footprints.Count);
        Assert.Equal(expectedTrees, footprints.FindAll(f => f.ArchetypeKey == "tree").Count);
        Assert.Equal(expectedHalls, footprints.FindAll(f => f.ArchetypeKey == "hall").Count);
    }

    /// <summary>Quarter turns preserve local extents and use the rotated tile rectangle's centre.</summary>
    [Fact]
    public void Footprints_QuarterTurn_CenterAndExtentsAgreeWithTileFootprint()
    {
        TileWorldDocument doc = FlatWorld(2f);
        for (int rotation = 0; rotation < 4; rotation++)
            doc.AddObject("hall", 10, 20, 0, rotation);
        var source = new TileWorldMapSource(doc, Catalogs());
        var footprints = new List<WorldMapFootprint>();
        source.CollectFootprints(new MapRect(20, 40, 6, 6), 0f, footprints);
        Assert.Equal(4, footprints.Count);
        Vector2[] centres = { new(22, 43), new(23, 42), new(22, 43), new(23, 42) };
        for (int rotation = 0; rotation < 4; rotation++)
        {
            Assert.Equal(centres[rotation], footprints[rotation].Center);
            Assert.Equal(new Vector2(2, 3), footprints[rotation].HalfExtents);
            Assert.Equal(rotation * MathF.PI / 2f, footprints[rotation].Rotation);
        }
    }

    /// <summary>TileWorld's three wall kinds share the map wall role.</summary>
    [Fact]
    public void Footprints_CollisionKindsMap()
    {
        TileWorldDocument doc = FlatWorld();
        string[] archetypes = { "doorway", "tree", "wall", "wall_corner", "diag_wall" };
        for (int i = 0; i < archetypes.Length; i++) doc.AddObject(archetypes[i], i, 0, 0, 0);
        var source = new TileWorldMapSource(doc, Catalogs());
        var footprints = new List<WorldMapFootprint>();
        source.CollectFootprints(new MapRect(0, 0, 5, 1), 0f, footprints);
        Assert.Equal(5, footprints.Count);
        WorldMapCollision[] expected = { WorldMapCollision.None, WorldMapCollision.Solid,
            WorldMapCollision.Wall, WorldMapCollision.Wall, WorldMapCollision.Wall };
        for (int i = 0; i < footprints.Count; i++)
        {
            Assert.Equal(expected[i], footprints[i].Collision);
            Assert.Equal(i != 0, footprints[i].Blocking);
        }
    }

    /// <summary>Tags retain authored order and remove ordinal duplicates across both lists.</summary>
    [Fact]
    public void Footprints_TagsCombineArchetypeThenObject()
    {
        TileWorldDocument doc = FlatWorld();
        doc.AddObject("hall", 0, 0, 0, 0, new[] { "building", "bank", "bank", "Building" });
        var source = new TileWorldMapSource(doc, Catalogs());
        var footprints = new List<WorldMapFootprint>();
        source.CollectFootprints(new MapRect(0, 0, 2, 3), 0f, footprints);
        WorldMapFootprint footprint = Assert.Single(footprints);
        Assert.Equal("hall", footprint.ArchetypeKey);
        Assert.Equal(new[] { "building", "stone", "bank", "Building" }, footprint.Tags);
    }

    /// <summary>Markers use tile centres in metres and half-open surface-plane filtering.</summary>
    [Fact]
    public void Markers_AtTileCentre_WithTags()
    {
        TileWorldDocument doc = FlatWorld(2f, new RegionCoord(-1, 0));
        doc.SetMarker("bank", -1, 2, 0, new[] { "map-icon", "bank" });
        doc.SetMarker("upper", -1, 2, 1);
        var source = new TileWorldMapSource(doc, Catalogs());
        var markers = new List<WorldMapMarker>();
        source.CollectMarkers(new MapRect(-1, 5, 1, 1), markers);
        WorldMapMarker marker = Assert.Single(markers);
        Assert.Equal("bank", marker.Name);
        Assert.Equal(new Vector2(-1, 5), marker.Position);
        Assert.Equal(new[] { "map-icon", "bank" }, marker.Tags);
        markers.Clear();
        source.CollectMarkers(new MapRect(-2, 4, 1, 1), markers);
        Assert.Empty(markers);
    }

    /// <summary>Explicit surfaces sample and filter their chosen plane while retaining all roofs.</summary>
    [Fact]
    public void SurfacePlane_SelectsTerrainObjectsAndMarkers()
    {
        TileWorldDocument doc = FlatWorld();
        doc.SetUnderlay(0, 0, 1, 2);
        doc.AddObject("tree", 0, 0, 0, 0);
        TileObject upper = doc.AddObject("tree", 0, 0, 1, 0);
        doc.SetMarker("ground", 0, 0, 0);
        doc.SetMarker("upper", 0, 0, 1);
        var source = new TileWorldMapSource(doc, Catalogs(), 1);
        WorldMapSurfaceGrid grid = source.SampleSurface(new MapRect(0, 0, 1, 1), source.SurfaceCellSize);
        Assert.Equal(Color.FromHex("#8a6a3a"), grid.Ground[0]);
        Assert.Equal(3f, grid.SurfaceHeight[0]);
        var footprints = new List<WorldMapFootprint>();
        source.CollectFootprints(new MapRect(0, 0, 1, 1), 0f, footprints);
        Assert.Equal(upper.Id, Assert.Single(footprints).Id);
        var markers = new List<WorldMapMarker>();
        source.CollectMarkers(new MapRect(0, 0, 1, 1), markers);
        Assert.Equal("upper", Assert.Single(markers).Name);
    }

    /// <summary>An empty document and empty queries produce no authored surface or placements.</summary>
    [Fact]
    public void EmptyDocumentAndAreas_ReturnEmptyResults()
    {
        var source = new TileWorldMapSource(new TileWorldDocument { Id = "w", DisplayName = "W" }, Catalogs());
        Assert.Equal(default, source.Bounds);
        Assert.Equal(0, source.Revision);
        WorldMapSurfaceGrid grid = source.SampleSurface(new MapRect(1, 1, 0, 5), source.SurfaceCellSize);
        Assert.Empty(grid.Ground);
        var footprints = new List<WorldMapFootprint>();
        source.CollectFootprints(new MapRect(1, 1, 0, 5), 0f, footprints);
        Assert.Empty(footprints);
        var markers = new List<WorldMapMarker>();
        source.CollectMarkers(new MapRect(1, 1, 0, 5), markers);
        Assert.Empty(markers);
    }

    internal static TileWorldDocument FlatWorld(float tileSize = 1f, params RegionCoord[] regions)
    {
        var doc = new TileWorldDocument { Id = "w", DisplayName = "W", TileSize = tileSize };
        if (regions.Length == 0) regions = new[] { new RegionCoord(0, 0) };
        foreach (RegionCoord coord in regions)
        {
            var underlay = new ushort[TileRegion.TileCount];
            Array.Fill(underlay, (ushort)1);
            doc.GetOrCreateRegion(coord).Plane(0).Underlay = underlay;
        }
        return doc;
    }

    internal static TileWorldCatalogs Catalogs() => TileWorldCatalogs.Merge(
        TileWorldCatalogs.Greybox(), TileWorldCatalogs.LoadJson(
            """
            {
              "archetypes": [
                { "id": "hall", "name": "hall", "meshRef": "test/hall.glb", "sizeX": 2, "sizeZ": 3,
                  "collisionKind": "Solid", "tags": ["building", "stone", "building"] },
                { "id": "roof_large", "name": "roof", "meshRef": "test/roof.glb", "sizeX": 6, "sizeZ": 6,
                  "isRoof": true, "collisionKind": "None" }
              ]
            }
            """, "worldmap-tests"));
}
