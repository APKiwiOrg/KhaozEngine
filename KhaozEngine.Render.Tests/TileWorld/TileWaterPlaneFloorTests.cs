using KhaozEngine.Render3D;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public sealed class TileWaterPlaneFloorTests
{
    static readonly RegionCoord Origin = new(0, 0);

    static TileWorldDocument World()
    {
        var doc = new TileWorldDocument { Id = "water-floors", DisplayName = "Water floors", PlaneHeight = 4.5f };
        doc.GetOrCreateRegion(Origin);
        for (int z = 6; z < 20; z++)
            for (int x = 6; x < 20; x++) doc.SetUnderlay(x, z, 0, TileRenderTestData.Grass);
        return doc;
    }

    static void Paint(TileWorldDocument doc, int plane, int width = 3, int height = 3)
    {
        for (int z = 10; z < 10 + height; z++)
            for (int x = 10; x < 10 + width; x++) doc.SetUnderlay(x, z, plane, TileRenderTestData.Water);
    }

    static TileWorldDocument SlopedBody()
    {
        TileWorldDocument doc = World();
        // One L shaped body splits into a 5x2 cap and a 3x3 leg, each with a different bed minimum.
        Paint(doc, 0, width: 5, height: 2);
        Paint(doc, 0, height: 5);
        doc.SetCornerHeightCm(11, 11, 0, -150);
        doc.SetCornerHeightCm(11, 14, 0, -75);
        // A deeper corner outside both rectangles must not extend either water volume.
        doc.SetCornerHeightCm(16, 13, 0, -900);
        return doc;
    }

    [Fact]
    public void FloorIsTheLowestBedCornerUnderTheRectangle()
    {
        var planes = TileWaterPlanes.Collect(SlopedBody(), TileRenderTestData.Catalogs, Origin, 0);

        Assert.Equal(2, planes.Count);
        Assert.Equal(-1.5001f, planes[0].FloorY);
        Assert.Equal(-0.7501f, planes[1].FloorY);
    }

    [Theory]
    [InlineData(false, 2.9999f)]
    [InlineData(true, 1.2499f)]
    public void StackedPlanesGetTheirOwnFloors(bool authoredUpperBed, float expectedUpperFloor)
    {
        TileWorldDocument doc = World();
        Paint(doc, 0);
        Paint(doc, 1);
        doc.SetCornerHeightCm(11, 11, 0, -150);
        if (authoredUpperBed) doc.SetCornerHeightCm(11, 11, 1, 125);

        WaterPlane lower = Assert.Single(TileWaterPlanes.Collect(doc, TileRenderTestData.Catalogs, Origin, 0));
        WaterPlane upper = Assert.Single(TileWaterPlanes.Collect(doc, TileRenderTestData.Catalogs, Origin, 1));

        Assert.Equal(-1.5001f, lower.FloorY);
        Assert.Equal(expectedUpperFloor, upper.FloorY);
        Assert.True(upper.FloorY > lower.SurfaceY);
    }

    [Fact]
    public void DefaultViewsCarryTheFloorToo()
    {
        var scene = new RecordingTileWorldScene();
        using var view = new TileWorldView(scene, SlopedBody(), TileRenderTestData.Catalogs, new GreyboxMeshResolver());
        view.LoadRegion(Origin);
        view.DrawWaterPlanes();

        Assert.Equal(2, scene.WaterDraws.Count);
        Assert.Equal(-1.5001f, scene.WaterDraws[0].FloorY);
        Assert.Equal(-0.7501f, scene.WaterDraws[1].FloorY);
        Assert.All(scene.WaterDraws, plane => Assert.False(plane.Optics.IsBound));
    }

    [Fact]
    public void AuthoredBedViewsPreserveEachFloorWhenBindingOptics()
    {
        var scene = new RecordingTileWorldScene();
        using var view = new TileWorldView(scene, SlopedBody(), TileRenderTestData.Catalogs, new GreyboxMeshResolver(),
            new TileWorldViewOptions { WaterDepthMode = TileWaterDepthMode.AuthoredBed });
        view.LoadRegion(Origin);
        view.DrawWaterPlanes();

        Assert.Empty(view.WaterOpticsFailures);
        Assert.Equal(2, scene.WaterDraws.Count);
        Assert.Equal(-1.5001f, scene.WaterDraws[0].FloorY);
        Assert.Equal(-0.7501f, scene.WaterDraws[1].FloorY);
        Assert.All(scene.WaterDraws, plane => Assert.True(plane.Optics.IsBound));
    }

    [Theory]
    [InlineData(false, -1.5001f)]
    [InlineData(true, -2.0001f)]
    public void FarEdgeCornersUseTheGroundMeshOwnerAndFallback(bool eastOwnerPresent, float expectedFloor)
    {
        TileWorldDocument doc = World();
        doc.SetUnderlay(63, 10, 0, TileRenderTestData.Water);
        doc.SetCornerHeightCm(63, 10, 0, -150);
        if (eastOwnerPresent)
        {
            doc.GetOrCreateRegion(new RegionCoord(1, 0));
            doc.SetCornerHeightCm(64, 10, 0, -200);
        }

        WaterPlane plane = Assert.Single(TileWaterPlanes.Collect(doc, TileRenderTestData.Catalogs, Origin, 0));

        Assert.Equal(expectedFloor, plane.FloorY);
    }

    [Fact]
    public void DryRectanglesNeverPutTheFloorAboveTheSurface()
    {
        TileWorldDocument doc = World();
        Paint(doc, 0);

        WaterPlane plane = Assert.Single(TileWaterPlanes.Collect(doc, TileRenderTestData.Catalogs, Origin, 0));

        Assert.NotNull(plane.FloorY);
        Assert.Equal(plane.SurfaceY, plane.FloorY);
    }
}
