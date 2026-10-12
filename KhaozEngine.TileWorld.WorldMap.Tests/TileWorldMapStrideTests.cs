using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Primitives;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.WorldMap;
using KhaozEngine.WorldMap;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.WorldMap;

/// <summary>Proves native fidelity, centre point sampling and negative coarse-grid alignment.</summary>
public sealed class TileWorldMapStrideTests
{
    /// <summary>Hints below and at native spacing yield byte-identical colour and height arrays.</summary>
    [Fact]
    public void Stride1_EqualsNativeSampling()
    {
        TileWorldDocument doc = TileWorldMapSourceTests.FlatWorld();
        doc.SetOverlay(1, 1, 0, 2);
        doc.SetUnderlay(2, 2, 0, 4);
        doc.SetCornerHeightCm(3, 3, 0, 250);
        var source = new TileWorldMapSource(doc, TileWorldMapSourceTests.Catalogs());
        WorldMapSurfaceGrid below = source.SampleSurface(new(-0.5f, -0.5f, 5f, 5f), 0.5f);
        WorldMapSurfaceGrid native = source.SampleSurface(new(-0.5f, -0.5f, 5f, 5f), 1f);
        Assert.Equal(new Vector2(-1f, -1f), native.Origin);
        Assert.Equal(1f, native.CellSize);
        Assert.Equal(native.Origin, below.Origin);
        Assert.Equal(native.Width, below.Width);
        Assert.Equal(native.Height, below.Height);
        Assert.Equal(MemoryMarshal.AsBytes(native.Ground.AsSpan()).ToArray(), MemoryMarshal.AsBytes(below.Ground.AsSpan()).ToArray());
        Assert.Equal(MemoryMarshal.AsBytes(native.SurfaceHeight.AsSpan()).ToArray(), MemoryMarshal.AsBytes(below.SurfaceHeight.AsSpan()).ToArray());
        Assert.Equal(MemoryMarshal.AsBytes(native.WaterSurface.AsSpan()).ToArray(), MemoryMarshal.AsBytes(below.WaterSurface.AsSpan()).ToArray());
    }

    /// <summary>Four-tile blocks take colour, corner mean and water from the tile at start plus two.</summary>
    [Fact]
    public void Stride4_PointSamplesBlockCentre()
    {
        TileWorldDocument doc = TileWorldMapSourceTests.FlatWorld();
        doc.SetOverlay(2, 2, 0, 2);
        doc.SetCornerHeightCm(2, 2, 0, 100);
        doc.SetCornerHeightCm(3, 2, 0, 200);
        doc.SetCornerHeightCm(2, 3, 0, 300);
        doc.SetCornerHeightCm(3, 3, 0, 600);
        doc.SetUnderlay(6, 2, 0, 4);
        doc.SetCornerHeightCm(7, 3, 0, 250);
        var source = new TileWorldMapSource(doc, TileWorldMapSourceTests.Catalogs());
        WorldMapSurfaceGrid grid = source.SampleSurface(new(0f, 0f, 8f, 4f), 4.9f);
        Assert.Equal(Vector2.Zero, grid.Origin);
        Assert.Equal(4f, grid.CellSize);
        Assert.Equal(2, grid.Width);
        Assert.Equal(1, grid.Height);
        Assert.Equal(Color.FromHex("#8a6a3a"), grid.Ground[0]);
        Assert.Equal(3f, grid.SurfaceHeight[0]);
        Assert.True(float.IsNaN(grid.WaterSurface[0]));
        Assert.Equal(2.48f, grid.WaterSurface[1], 5);
    }

    /// <summary>Blocks west and south of zero align to multiples of the stride in tiles.</summary>
    [Fact]
    public void Stride_NegativeRegionsAlignToMultiples()
    {
        TileWorldDocument doc = TileWorldMapSourceTests.FlatWorld(2f, new RegionCoord(-1, -1));
        doc.SetOverlay(-2, -2, 0, 2);
        var source = new TileWorldMapSource(doc, TileWorldMapSourceTests.Catalogs());
        WorldMapSurfaceGrid grid = source.SampleSurface(new(-7f, -7f, 6f, 6f), 8f);
        Assert.Equal(new Vector2(-8f, -8f), grid.Origin);
        Assert.Equal(8f, grid.CellSize);
        Assert.Equal(1, grid.Width);
        Assert.Equal(1, grid.Height);
        Assert.Equal(Color.FromHex("#8a6a3a"), grid.Ground[0]);
    }
}
