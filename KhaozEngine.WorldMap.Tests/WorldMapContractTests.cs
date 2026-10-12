using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.WorldMap;
using Xunit;

namespace KhaozEngine.Tests.WorldMap;

/// <summary>Proves rectangle, surface-grid and style contracts shared by later map layers.</summary>
public sealed class WorldMapContractTests
{
    /// <summary>Rectangle ownership includes south and west and excludes north and east.</summary>
    [Fact]
    public void MapRect_HalfOpenContainsAndIntersects()
    {
        var r = new MapRect(0f, 0f, 10f, 10f);
        Assert.True(r.Contains(new Vector2(0f, 0f)));
        Assert.False(r.Contains(new Vector2(10f, 5f)));
        Assert.False(r.Contains(new Vector2(5f, 10f)));
        Assert.False(r.Intersects(new MapRect(10f, 0f, 5f, 5f)));
        Assert.False(r.Intersects(new MapRect(0f, 10f, 5f, 5f)));
        Assert.True(r.Intersects(new MapRect(9.9f, 9.9f, 5f, 5f)));
        Assert.Equal(new MapRect(-1f, -1f, 12f, 12f), r.Expand(1f));
    }

    /// <summary>Empty rectangles never intersect, including when wholly inside an occupied rectangle.</summary>
    [Theory]
    [InlineData(0f, 3f)]
    [InlineData(3f, 0f)]
    [InlineData(-1f, 3f)]
    public void MapRect_EmptyArea_IntersectsNothing(float width, float height)
    {
        var empty = new MapRect(2f, 2f, width, height);
        var occupied = new MapRect(0f, 0f, 10f, 10f);
        Assert.False(empty.Contains(new Vector2(2f, 2f)));
        Assert.False(empty.Intersects(occupied));
        Assert.False(occupied.Intersects(empty));
    }

    /// <summary>Derived bounds and union cover both rectangles across the map origin.</summary>
    [Fact]
    public void MapRect_DerivedBoundsAndUnion_CoverBothRectangles()
    {
        var r = new MapRect(-4f, 2f, 6f, 8f);
        Assert.Equal(2f, r.Right);
        Assert.Equal(10f, r.Top);
        Assert.Equal(new Vector2(-1f, 6f), r.Center);
        var other = new MapRect(1f, -3f, 5f, 7f);
        Assert.Equal(new MapRect(-4f, -3f, 10f, 13f), r.Union(other));
        Assert.Equal(new MapRect(-4f, -3f, 10f, 13f), other.Union(r));
    }

    /// <summary>Allocated cells begin void and row zero is the south row.</summary>
    [Fact]
    public void SurfaceGrid_StartsVoid_IndexIsRowMajorSouthFirst()
    {
        var g = new WorldMapSurfaceGrid(new Vector2(-2f, 3f), 1f, 4, 3);
        Assert.Equal(new Vector2(-2f, 3f), g.Origin);
        Assert.Equal(1f, g.CellSize);
        Assert.Equal(4, g.Width);
        Assert.Equal(3, g.Height);
        Assert.Equal(12, g.Ground.Length);
        Assert.Equal(12, g.SurfaceHeight.Length);
        Assert.Equal(12, g.WaterSurface.Length);
        Assert.Equal(1 * 4 + 2, g.Index(2, 1));
        Assert.All(g.Ground, c => Assert.Equal(Color.Transparent, c));
        Assert.All(g.SurfaceHeight, h => Assert.True(float.IsNaN(h)));
        Assert.All(g.WaterSurface, w => Assert.True(float.IsNaN(w)));
        g.Ground[g.Index(2, 1)] = Color.White;
        g.SurfaceHeight[g.Index(2, 1)] = 7f;
        g.WaterSurface[g.Index(2, 1)] = 8f;
        Assert.Equal(Color.White, g.Ground[6]);
        Assert.Equal(7f, g.SurfaceHeight[6]);
        Assert.Equal(8f, g.WaterSurface[6]);
        Assert.Equal(Color.Transparent, g.Ground[5]);
    }

    /// <summary>Indexing cannot alias a neighbour row or escape the allocated grid.</summary>
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(4, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 3)]
    public void SurfaceGrid_IndexOutsideGrid_IsRejected(int column, int row)
    {
        var grid = new WorldMapSurfaceGrid(Vector2.Zero, 1f, 4, 3);
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.Index(column, row));
    }

    /// <summary>Cell spacing must be finite and positive.</summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void SurfaceGrid_InvalidCellSize_IsRejected(float cellSize) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorldMapSurfaceGrid(Vector2.Zero, cellSize, 4, 3));

    /// <summary>Negative dimensions cannot allocate a valid grid.</summary>
    [Theory]
    [InlineData(-1, 3)]
    [InlineData(4, -1)]
    public void SurfaceGrid_NegativeDimensions_AreRejected(int width, int height) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorldMapSurfaceGrid(Vector2.Zero, 1f, width, height));

    /// <summary>A source can return an empty grid, which has no addressable cells.</summary>
    [Fact]
    public void SurfaceGrid_ZeroWidth_HasNoCells()
    {
        var grid = new WorldMapSurfaceGrid(Vector2.Zero, 1f, 0, 3);
        Assert.Empty(grid.Ground);
        Assert.Empty(grid.SurfaceHeight);
        Assert.Empty(grid.WaterSurface);
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.Index(0, 0));
    }

    /// <summary>Neutral raster, relief, palette and feature defaults match the shared specification.</summary>
    [Fact]
    public void Style_Defaults_MatchSpec()
    {
        var s = new WorldMapStyle();
        Assert.Equal(4f, s.PixelsPerMetre);
        Assert.Equal(64f, s.ChunkMetres);
        Assert.Equal(256, s.ChunkPixels);
        Assert.Equal(Color.FromHex("#d9cba6"), s.VoidColor);
        Assert.Equal(315f, s.Relief.AzimuthDegrees);
        Assert.Equal(45f, s.Relief.AltitudeDegrees);
        Assert.Equal(0.5f, s.Relief.Strength);
        Assert.Equal(1f, s.Relief.SteepSlope);
        Assert.Equal(0.75f, s.Relief.SteepDarken);
        Assert.Equal(Color.FromHex("#6f9cc0"), s.Water.Shallow);
        Assert.Equal(Color.FromHex("#2a5a9a"), s.Water.Deep);
        Assert.Equal(Color.FromHex("#1f3f66"), s.Water.Shoreline);
        Assert.Equal(4f, s.Water.DeepDepthMetres);
        Assert.Equal(1, s.Water.ShorelinePixels);
        Assert.Equal(WorldMapSymbolKind.Obstacle, s.FallbackBlocking.Kind);
        Assert.Equal(Color.FromHex("#6f6a60"), s.FallbackBlocking.Fill);
        Assert.Equal(Color.FromHex("#3d3a34"), s.FallbackBlocking.Outline);
        Assert.Equal(new Color(0f, 0f, 0f, 0.35f), s.BuildingShadow);
        Assert.Equal(new Vector2(0.6f, -0.6f), s.BuildingShadowOffsetMetres);
        Assert.Equal(0.4f, s.WallWidthMetres);
        Assert.Equal(1f, s.OutlinePixels);
        Assert.Empty(s.SymbolRules);
        Assert.Empty(s.IconRules);
        Assert.Null(s.Labels);
        Assert.Equal(WorldMapSymbolKind.Hidden, WorldMapSymbol.Hidden.Kind);
        Assert.Equal(Color.Transparent, WorldMapSymbol.Hidden.Fill);
        Assert.Equal(Color.Transparent, WorldMapSymbol.Hidden.Outline);
    }

    /// <summary>Changing raster scale derives a rounded pixel size instead of retaining the default.</summary>
    [Fact]
    public void Style_ChunkPixels_RoundsTheConfiguredScale()
    {
        var style = new WorldMapStyle { ChunkMetres = 3f, PixelsPerMetre = 1.5f };
        Assert.Equal(4, style.ChunkPixels);
        Assert.Equal(10, (style with { PixelsPerMetre = 3.25f }).ChunkPixels);
    }

    /// <summary>Both collision forms block while an uncollidable footprint does not.</summary>
    [Fact]
    public void Footprint_BlockingFollowsCollision()
    {
        var f = new WorldMapFootprint(1, "rock", Vector2.Zero, Vector2.One, 0f, [], false, WorldMapCollision.None);
        Assert.False(f.Blocking);
        Assert.True((f with { Collision = WorldMapCollision.Wall }).Blocking);
        Assert.True((f with { Collision = WorldMapCollision.Solid }).Blocking);
    }
}
