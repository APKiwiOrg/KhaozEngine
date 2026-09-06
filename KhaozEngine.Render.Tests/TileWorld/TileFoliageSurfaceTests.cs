using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Terrain;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public class TileFoliageSurfaceTests
{
    static TileFoliageLayer Layer(float tileSize, float edgeFade = 0f, bool excludeIndoors = true,
        float doorClearance = 1.5f) => new(
        "grass", 0, -200f, -200f, tileSize, 401, 401,
        Enumerable.Repeat((byte)255, 401 * 401).ToArray(), 3, 0.5f, 0.8f, 1.2f, -0.04f,
        [new TileFoliageArchetype("bush", 1f)], [TileRenderTestData.Grass], excludeIndoors, true,
        doorClearance, edgeFade);

    static GroundCoverSample At(TileFoliageSurface surface, TileWorldDocument doc, float tileX, float tileZ) =>
        surface.Sample(TileWorldSpace.WorldX(tileX, doc.TileSize), TileWorldSpace.WorldZ(tileZ, doc.TileSize));

    [Theory]
    [InlineData(1f)]
    [InlineData(2.5f)]
    public void Sample_UsesTileWorldCoordinatesAndAlignsToSlopedGround(float tileSize)
    {
        TileWorldDocument doc = TileRenderTestData.HillWorld();
        doc.TileSize = tileSize;
        var surface = new TileFoliageSurface(doc, TileRenderTestData.Catalogs, Layer(tileSize));

        GroundCoverSample sample = At(surface, doc, 20.5f, 19.75f);

        Assert.Equal(doc.HeightAt(TileWorldSpace.WorldX(20.5f, tileSize), TileWorldSpace.WorldZ(19.75f, tileSize), 0), sample.Height, 4);
        Assert.True(sample.Normal.Y > 0f);
        Assert.NotEqual(Vector3.UnitY, sample.Normal);
        Assert.Equal(1f, sample.Density, 4);
    }

    [Fact]
    public void Sample_RejectsRoadWaterInteriorSolidDoorAndUpperPlaneRoof()
    {
        TileWorldDocument doc = TileRenderTestData.GreyboxWorld();
        TileWorldCatalogs catalogs = TileRenderTestData.Catalogs;
        doc.SetUnderlay(2, 2, 0, TileRenderTestData.Road);
        doc.SetUnderlay(3, 2, 0, TileRenderTestData.Water);
        doc.SetSettings(4, 2, 0, TileSettings.Indoors);
        doc.AddObject("tree", 5, 2, 0, 0);
        doc.AddObject("bush", 15, 2, 0, 0);
        doc.AddObject("doorway", 7, 2, 0, 0, ["door"]);
        doc.AddObject("roof_flat", 8, 2, 1, 0);
        var surface = new TileFoliageSurface(doc, catalogs, Layer(doc.TileSize));

        Assert.Equal(0f, At(surface, doc, 2.5f, 2.5f).Density);
        Assert.Equal(0f, At(surface, doc, 3.5f, 2.5f).Density);
        Assert.Equal(0f, At(surface, doc, 4.5f, 2.5f).Density);
        Assert.Equal(0f, At(surface, doc, 5.5f, 2.5f).Density);
        Assert.True(At(surface, doc, 15.5f, 2.5f).Density > 0f);
        Assert.Equal(0f, At(surface, doc, 7.9f, 2.5f).Density);
        Assert.Equal(0f, At(surface, doc, 8.5f, 2.5f).Density);
    }

    [Fact]
    public void Sample_FadesAtEligibleGroundEdges()
    {
        TileWorldDocument doc = TileRenderTestData.GreyboxWorld();
        doc.SetUnderlay(1, 0, 0, TileRenderTestData.Road);
        var surface = new TileFoliageSurface(doc, TileRenderTestData.Catalogs, Layer(doc.TileSize, edgeFade: 0.5f));

        GroundCoverSample edge = At(surface, doc, 0.9f, 0.5f);
        GroundCoverSample centre = At(surface, doc, 0.5f, 0.5f);

        Assert.InRange(edge.Density, 0.15f, 0.25f);
        Assert.Equal(1f, centre.Density, 4);
    }

    [Fact]
    public void Sample_UsesTheVisibleOverlayShapeWhenApplyingAllowedMaterials()
    {
        TileWorldDocument doc = TileRenderTestData.GreyboxWorld();
        doc.SetOverlay(10, 4, 0, TileRenderTestData.Road);
        doc.SetOverlayShape(10, 4, 0, TileOverlayShape.DiagonalHalf);
        doc.SetOverlayRotation(10, 4, 0, 0);
        var surface = new TileFoliageSurface(doc, TileRenderTestData.Catalogs, Layer(doc.TileSize));

        Assert.Equal(0f, At(surface, doc, 10.2f, 4.8f).Density);
        Assert.True(At(surface, doc, 10.8f, 4.2f).Density > 0f);
    }

    [Fact]
    public void Sample_ExcludeIndoorsControlsBothIndoorTilesAndUpperRoofs()
    {
        TileWorldDocument doc = TileRenderTestData.GreyboxWorld();
        doc.SetSettings(3, 3, 0, TileSettings.Indoors);
        doc.AddObject("roof_flat", 4, 3, 1, 0);
        var surface = new TileFoliageSurface(doc, TileRenderTestData.Catalogs,
            Layer(doc.TileSize, excludeIndoors: false));

        Assert.True(At(surface, doc, 3.5f, 3.5f).Density > 0f);
        Assert.True(At(surface, doc, 4.5f, 3.5f).Density > 0f);
    }

    [Fact]
    public void Sample_DoorClearanceOnlyUsesDoorsOnTheLayerPlane()
    {
        TileWorldDocument doc = TileRenderTestData.GreyboxWorld();
        doc.AddObject("doorway", 6, 6, 1, 0, ["door"]);
        var surface = new TileFoliageSurface(doc, TileRenderTestData.Catalogs, Layer(doc.TileSize));

        Assert.True(At(surface, doc, 6.5f, 6.5f).Density > 0f);
    }

    [Fact]
    public void Sample_RejectsAbsentAndUnloadedRegions()
    {
        using var tmp = new TempDir();
        string dir = TileRenderTestData.SaveGrid(tmp, 2, 1);
        TileWorldSource source = TileWorldSource.Open(dir);
        source.Document.SetFoliageLayer(Layer(source.Document.TileSize));
        var surface = new TileFoliageSurface(source.Document, TileRenderTestData.Catalogs,
            source.Document.GetFoliageLayer("grass")!);

        Assert.Equal(0f, At(surface, source.Document, 1.5f, 1.5f).Density);
        source.EnsureLoaded(new RegionCoord(0, 0));
        Assert.True(At(surface, source.Document, 1.5f, 1.5f).Density > 0f);
        Assert.Equal(0f, At(surface, source.Document, TileRegion.Size + 1.5f, 1.5f).Density);
        Assert.Equal(0f, At(surface, source.Document, -1.5f, 1.5f).Density);
    }
}
