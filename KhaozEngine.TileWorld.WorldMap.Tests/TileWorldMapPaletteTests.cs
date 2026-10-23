using System.Collections.Generic;
using KhaozEngine.Primitives;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.WorldMap;
using KhaozEngine.WorldMap;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.WorldMap;

/// <summary>Map palettes select resolved materials without changing the authored catalog.</summary>
public sealed class TileWorldMapPaletteTests
{
    /// <summary>Already compiled callers retain the original three-parameter constructor signature.</summary>
    [Fact]
    public void Palette_LegacyConstructor_RemainsInvocable()
    {
        var constructor = typeof(TileWorldMapSource).GetConstructor(
            [typeof(TileWorldDocument), typeof(TileWorldCatalogs), typeof(int)]);
        Assert.NotNull(constructor);
        TileWorldDocument doc = TileWorldMapSourceTests.FlatWorld();
        doc.SetUnderlay(0, 0, 1, 2);

        var source = (TileWorldMapSource)constructor.Invoke([doc, TileWorldMapSourceTests.Catalogs(), 1]);

        Assert.Equal(Color.FromHex("#8a6a3a"), source.SampleSurface(new(0, 0, 1, 1), 1f).Ground[0]);
    }

    /// <summary>The selected underlay uses its map colour, including a non-default plane.</summary>
    [Fact]
    public void Palette_SelectedUnderlay_UsesMapColour()
    {
        TileWorldDocument doc = TileWorldMapSourceTests.FlatWorld();
        doc.SetUnderlay(0, 0, 1, 2);
        var source = Create(doc, TileWorldMapSourceTests.Catalogs(), 1,
            new Dictionary<ushort, Color> { [2] = Color.White });

        Assert.Equal(Color.White, source.SampleSurface(new(0, 0, 1, 1), 1f).Ground[0]);
    }

    /// <summary>A resolved overlay wins before palette lookup and a missing key falls back.</summary>
    [Fact]
    public void Palette_OverlayPrecedenceAndMissingEntries_PreserveMaterialSelection()
    {
        TileWorldDocument doc = TileWorldMapSourceTests.FlatWorld();
        doc.SetOverlay(1, 0, 0, 2);
        doc.SetOverlay(2, 0, 0, 65535);
        doc.SetUnderlay(3, 0, 0, 65535);
        var source = Create(doc, TileWorldMapSourceTests.Catalogs(), 0,
            new Dictionary<ushort, Color> { [1] = Color.White, [65535] = Color.Black });

        WorldMapSurfaceGrid grid = source.SampleSurface(new(0, 0, 4, 1), 1f);

        Assert.Equal(Color.White, grid.Ground[0]);
        Assert.Equal(Color.FromHex("#8a6a3a"), grid.Ground[1]);
        Assert.Equal(Color.White, grid.Ground[2]);
        Assert.Equal(Color.Transparent, grid.Ground[3]);
        var overlay = Create(doc, TileWorldMapSourceTests.Catalogs(), 0,
            new Dictionary<ushort, Color> { [1] = Color.White, [2] = Color.Black });
        Assert.Equal(Color.Black, overlay.SampleSurface(new(1, 0, 1, 1), 1f).Ground[0]);
    }

    /// <summary>Caller mutation before the first worker sample cannot alter the palette snapshot.</summary>
    [Fact]
    public void Palette_CallerMutationBeforeSampling_DoesNotChangeSource()
    {
        TileWorldDocument doc = TileWorldMapSourceTests.FlatWorld();
        doc.SetOverlay(1, 0, 0, 2);
        var palette = new Dictionary<ushort, Color> { [1] = Color.White };
        var source = Create(doc, TileWorldMapSourceTests.Catalogs(), 0, palette);
        palette[1] = Color.Black;
        palette[2] = Color.Black;

        WorldMapSurfaceGrid grid = source.SampleSurface(new(0, 0, 2, 1), 1f);

        Assert.Equal(Color.White, grid.Ground[0]);
        Assert.Equal(Color.FromHex("#8a6a3a"), grid.Ground[1]);
    }

    /// <summary>Catalog colours, water identity and metre heights survive palette overrides.</summary>
    [Fact]
    public void Palette_DoesNotMutateCatalogOrTerrainSemantics()
    {
        TileWorldDocument doc = TileWorldMapSourceTests.FlatWorld();
        doc.SetUnderlay(0, 0, 0, 4);
        doc.SetCornerHeightCm(1, 1, 0, 200);
        TileWorldCatalogs catalogs = TileWorldMapSourceTests.Catalogs();
        string original = catalogs.Material(4)!.Color;
        var source = Create(doc, catalogs, 0, new Dictionary<ushort, Color> { [4] = Color.White });
        WorldMapSurfaceGrid mapped = source.SampleSurface(new(0, 0, 1, 1), 1f);
        WorldMapSurfaceGrid legacy = new TileWorldMapSource(doc, catalogs).SampleSurface(new(0, 0, 1, 1), 1f);

        Assert.Equal(Color.White, mapped.Ground[0]);
        Assert.Equal(original, catalogs.Material(4)!.Color);
        Assert.Equal(Color.FromHex(original), legacy.Ground[0]);
        Assert.Equal(0.5f, mapped.SurfaceHeight[0]);
        Assert.Equal(legacy.WaterSurface, mapped.WaterSurface);
        Assert.True(float.IsFinite(mapped.WaterSurface[0]));
    }

    static TileWorldMapSource Create(TileWorldDocument doc, TileWorldCatalogs catalogs, int plane,
        IReadOnlyDictionary<ushort, Color> palette)
    {
        return new TileWorldMapSource(doc, catalogs, plane, palette);
    }
}
