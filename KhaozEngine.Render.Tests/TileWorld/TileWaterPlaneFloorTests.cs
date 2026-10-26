using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
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
        // This deeper corner is outside both rectangles even after outward snapping to the coarsest cell grid.
        doc.SetCornerHeightCm(18, 13, 0, -900);
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

    [Fact]
    public void AuthoredBedLookChangesPreserveEachFloorWithoutRebuildingOptics()
    {
        var scene = new RecordingTileWorldScene();
        var options = new TileWorldViewOptions { WaterDepthMode = TileWaterDepthMode.AuthoredBed };
        using var view = new TileWorldView(scene, SlopedBody(), TileRenderTestData.Catalogs, new GreyboxMeshResolver(),
            options);
        view.LoadRegion(Origin);
        view.DrawWaterPlanes();
        WaterPlane[] before = scene.WaterDraws.ToArray();
        Assert.Equal(2, before.Length);
        Assert.All(before, plane => Assert.NotNull(plane.FloorY));
        int uploads = scene.TriangleLoads.Count;

        options.WaterLook = new WaterLook { Opacity = 0.4f, SwellAmplitude = 0f };
        view.Flush();
        scene.ClearWater();
        view.DrawWaterPlanes();

        Assert.Equal(before.Length, scene.WaterDraws.Count);
        Assert.Equal(uploads, scene.TriangleLoads.Count);
        for (int i = 0; i < before.Length; i++)
        {
            WaterPlane after = scene.WaterDraws[i];
            Assert.Same(options.WaterLook, after.Look);
            Assert.Equal(before[i].FloorY, after.FloorY);
            Assert.Equal(before[i].Optics, after.Optics);
        }
    }

    public static TheoryData<TileGroundLod, int> GroundLodsAndRegions
    {
        get
        {
            var data = new TheoryData<TileGroundLod, int>();
            foreach (TileGroundLod lod in Enum.GetValues<TileGroundLod>())
            {
                data.Add(lod, 0);
                data.Add(lod, -1);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(GroundLodsAndRegions))]
    public void EveryEmittedLodStaysAboveTheFloorAcrossRectangleBoundaries(TileGroundLod lod, int regionIndex)
    {
        var region = new RegionCoord(regionIndex, regionIndex);
        var doc = new TileWorldDocument { Id = "coarse-floor", DisplayName = "Coarse floor" };
        doc.GetOrCreateRegion(region);
        for (int z = 0; z < 20; z++)
            for (int x = 0; x < 20; x++)
            {
                bool water = x >= 4 && z >= 4 && (z < 10 ? x < 16 : z < 16 && x < 12);
                doc.SetUnderlay(region.OriginX + x, region.OriginZ + z, 0,
                    water ? TileRenderTestData.Water : TileRenderTestData.Grass);
            }
        for (int z = 10; z <= 16; z++)
            for (int x = 4; x <= 12; x++)
                doc.SetCornerHeightCm(region.OriginX + x, region.OriginZ + z, 0, -100);
        doc.SetCornerHeightCm(region.OriginX + 8, region.OriginZ + 8, 0, -400);

        var planes = TileWaterPlanes.Collect(doc, TileRenderTestData.Catalogs, region, 0);
        Assert.Equal(2, planes.Count);
        GltfMesh mesh = Assert.IsType<GltfMesh>(TileGroundMesher.Build(doc, TileRenderTestData.Catalogs, region, 0, lod));
        Matrix4x4 world = TileGroundMesher.WorldMatrix(doc, region);
        foreach (WaterPlane plane in planes) AssertMeshAboveFloor(mesh, plane, world);

        // The Coarse4 transition fan crosses z = 10. Its point (8.5, 11) is below every authored corner in
        // the second water rectangle, while the full-detail mesh there stays on its flat -1 m bed.
        float? witness = null;
        for (int i = 0; i < mesh.Indices32.Length; i += 3)
        {
            Vector3 a = mesh.Vertices[mesh.Indices32[i]].Position;
            Vector3 b = mesh.Vertices[mesh.Indices32[i + 1]].Position;
            Vector3 c = mesh.Vertices[mesh.Indices32[i + 2]].Position;
            for (int u = 0; u <= 8; u++)
                for (int v = 0; v <= 8 - u; v++)
                {
                    Vector3 point = (u * a + v * b + (8 - u - v) * c) / 8f;
                    if (point.X == 8.5f && point.Z == -11f) witness = point.Y;
                }
        }
        if (lod == TileGroundLod.Coarse4) Assert.Equal(-1.375f, witness);
        // Both LODs use the coarsest possible footprint, even when Full currently draws a shallower bed.
        Assert.All(planes, plane => Assert.Equal(-4.0001f, plane.FloorY));
    }

    static void AssertMeshAboveFloor(GltfMesh mesh, WaterPlane plane, Matrix4x4 world)
    {
        float floor = Assert.IsType<float>(plane.FloorY);
        int vertices = 0, fragments = 0;
        foreach (var vertex in mesh.Vertices)
        {
            Vector3 point = Vector3.Transform(vertex.Position, world);
            if (Inside(plane, point))
            {
                vertices++;
                Assert.True(point.Y >= floor, $"vertex {point} below floor {floor}");
            }
        }
        for (int i = 0; i < mesh.Indices32.Length; i += 3)
        {
            Vector3 a = Vector3.Transform(mesh.Vertices[mesh.Indices32[i]].Position, world);
            Vector3 b = Vector3.Transform(mesh.Vertices[mesh.Indices32[i + 1]].Position, world);
            Vector3 c = Vector3.Transform(mesh.Vertices[mesh.Indices32[i + 2]].Position, world);
            for (int u = 0; u <= 8; u++)
                for (int v = 0; v <= 8 - u; v++)
                {
                    Vector3 point = (u * a + v * b + (8 - u - v) * c) / 8f;
                    if (!Inside(plane, point)) continue;
                    fragments++;
                    Assert.True(point.Y >= floor, $"fragment {point} below floor {floor}");
                }
        }
        Assert.True(vertices > 0);
        Assert.True(fragments > 0);
    }

    static bool Inside(WaterPlane plane, Vector3 point) =>
        point.X >= plane.CenterX - plane.HalfExtentX && point.X <= plane.CenterX + plane.HalfExtentX &&
        point.Z >= plane.CenterZ - plane.HalfExtentZ && point.Z <= plane.CenterZ + plane.HalfExtentZ;

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
