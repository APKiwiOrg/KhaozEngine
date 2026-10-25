using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Render3D;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>Authored-bed refusals that name their cause before or instead of publishing a field.</summary>
public sealed class TileWorldViewWaterOpticsRefusalTests
{
    static readonly RegionCoord Origin = new(0, 0);
    static readonly RegionCoord East = new(1, 0);

    static TileWorldViewOptions Authored(WaterLook? look = null) =>
        new() { WaterDepthMode = TileWaterDepthMode.AuthoredBed, WaterLook = look };

    static TileWorldView View(ITileWorldScene scene, TileWorldDocument doc, TileWorldViewOptions options) =>
        new(scene, doc, TileRenderTestData.Catalogs, new GreyboxMeshResolver(), options);

    static void Fill(TileWorldDocument doc, ushort material, int x0, int z0, int x1, int z1)
    {
        for (int z = z0; z < z1; z++)
            for (int x = x0; x < x1; x++) doc.SetUnderlay(x, z, 0, material);
    }

    static WaterPlane[] Water(TileWorldView view, RecordingTileWorldScene scene)
    {
        scene.ClearWater();
        view.DrawWaterPlanes();
        return scene.WaterDraws.ToArray();
    }

    static bool Covers(TileRect domain, WaterPlane plane, float tileSize) =>
        domain.Contains((int)MathF.Floor(plane.CenterX / tileSize), (int)MathF.Floor(-plane.CenterZ / tileSize));

    [Fact]
    public void UploadRefusalNamesTheFailingFieldAndRefusesOnlyItsMember()
    {
        // East owns interior water. Origin has two reference levels at its east border, both supported by East.
        var doc = new TileWorldDocument { Id = "upload-refusal", DisplayName = "Upload refusal", PlaneCount = 1 };
        doc.GetOrCreateRegion(Origin);
        doc.GetOrCreateRegion(East);
        Fill(doc, TileRenderTestData.Grass, 48, 4, 100, 40);
        Fill(doc, TileRenderTestData.Water, 88, 12, 90, 15);
        Fill(doc, TileRenderTestData.Water, 62, 12, 64, 15);
        Fill(doc, TileRenderTestData.Water, 62, 30, 64, 33);
        for (int z = 28; z <= 35; z++)
            for (int x = 60; x <= 66; x++) doc.SetCornerHeightCm(x, z, 0, 250);
        var scene = new RecordingTileWorldScene { OpticalPadding = 2f };
        using var view = View(scene, doc, Authored());
        view.LoadRegion(East);
        view.LoadRegion(Origin);
        view.Flush();
        Assert.Empty(view.WaterOpticsFailures);
        Assert.Equal(3, scene.TriangleFields.Count);
        WaterPlane eastWater = Assert.Single(Water(view, scene), p => p.CenterX > 64f);
        TileGroundSurfaceSnapshot east = view.AcceptedGroundSurface(East, 0)!;

        // Origin's fields upload in level order after East's own field. The backend refuses Origin's second level.
        float[] levels = TileWaterPlanes.Collect(doc, TileRenderTestData.Catalogs, Origin, 0, null)
            .Select(p => p.SurfaceY).Distinct().ToArray();
        Assert.Equal(2, levels.Length);
        WaterPlane[] origin = Water(view, scene).Where(p => p.CenterX < 64f).ToArray();
        WaterPlane refusedLevel = Assert.Single(origin, p => p.SurfaceY == levels[1]);
        WaterPlane otherLevel = Assert.Single(origin, p => p.SurfaceY == levels[0]);
        int meshes = scene.MeshLoads.Count;
        scene.RefuseSurfaceOnOpticalLoad = scene.TriangleLoads.Count + 3;
        view.MarkDirty(new TileRect(96, 64, 1, 1), 0);
        view.Flush();

        TileWaterOpticsFailure failure = Assert.Single(view.WaterOpticsFailures);
        Assert.Equal(Origin, failure.Region);
        Assert.Equal(TileWaterOpticsFailureKind.UnrepresentableSurface, failure.Kind);
        Assert.Equal(levels[1], failure.ReferenceSurfaceY);
        Assert.NotEqual(levels[0], failure.ReferenceSurfaceY);
        TileRect domain = Assert.IsType<TileRect>(failure.Domain);
        Assert.True(Covers(domain, refusedLevel, doc.TileSize), $"domain {domain}");
        Assert.False(Covers(domain, otherLevel, doc.TileSize), $"domain {domain}");

        // The group still published East's replacement surface, mesh and own field.
        Assert.NotSame(east, view.AcceptedGroundSurface(East, 0));
        Assert.Equal(meshes + 1, scene.MeshLoads.Count);
        WaterPlane published = Assert.Single(Water(view, scene));
        Assert.True(published.CenterX > 64f);
        Assert.NotEqual(eastWater.Optics.Field, published.Optics.Field);
        Assert.Equal(published.Optics.Field, Assert.Single(scene.TriangleFields).Key);
    }

    static TileWorldDocument WaterAndGrassOnly(RegionCoord grassOnly)
    {
        var doc = new TileWorldDocument { Id = "uncertified", DisplayName = "Uncertified displacement", PlaneCount = 1 };
        doc.GetOrCreateRegion(Origin);
        doc.GetOrCreateRegion(grassOnly);
        Fill(doc, TileRenderTestData.Grass, 4, 4, 24, 24);
        Fill(doc, TileRenderTestData.Water, 10, 10, 13, 13);
        Fill(doc, TileRenderTestData.Grass, grassOnly.OriginX + 4, grassOnly.OriginZ + 4, grassOnly.OriginX + 24,
            grassOnly.OriginZ + 24);
        return doc;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UncertifiedDisplacementWithdrawsWaterAndKeepsTerrain(bool explicitLook)
    {
        // Real scene padding: FFT water has no certified displacement envelope, through the look or its default.
        using var scene = new Scene3D(new FakeGpuDevice(), new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm));
        WaterLook? look = explicitLook ? new WaterLook { WaveSource = WaterWaveSource.FftOcean, SwellAmplitude = 0f } : null;
        if (!explicitLook) scene.Post.Water.WaveSource = WaterWaveSource.FftOcean;
        var grassOnly = new RegionCoord(3, 0);
        using var view = View(new Scene3DTileWorldScene(scene), WaterAndGrassOnly(grassOnly), Authored(look));

        view.LoadRegion(Origin);
        view.LoadRegion(grassOnly);
        view.Flush();
        Assert.Equal(2, view.LoadedRegionCount);
        Assert.NotNull(view.AcceptedGroundSurface(Origin, 0));
        Assert.NotNull(view.AcceptedGroundSurface(grassOnly, 0));
        Assert.Equal(0L, scene.WaterOpticsStats.UploadCount);
        TileWaterOpticsFailure failure = Assert.Single(view.WaterOpticsFailures);
        Assert.Equal((Origin, 0, TileWaterOpticsFailureKind.UncertifiedDisplacement),
            (failure.Region, failure.Plane, failure.Kind));
        scene.Begin();
        view.Draw(new Vector3(12f, 0f, -12f));
        Assert.Equal(0, scene.WaterPlaneCount);
        Assert.True(scene.QueuedInstancesForTests.Count >= 2);

        // A certifiable look republishes the water against the unchanged terrain.
        if (explicitLook) look!.WaveSource = WaterWaveSource.Procedural;
        else scene.Post.Water.WaveSource = WaterWaveSource.Procedural;
        view.Flush();
        Assert.Empty(view.WaterOpticsFailures);
        Assert.Equal(1L, scene.WaterOpticsStats.UploadCount);
        Assert.Equal(1, scene.WaterOpticsStats.LiveFields);
        scene.Begin();
        view.Draw(new Vector3(12f, 0f, -12f));
        Assert.Equal(1, scene.WaterPlaneCount);
    }

    [Fact]
    public void BecomingUncertifiedWithdrawsResidentWaterWithoutRebuildingTerrain()
    {
        var grassOnly = new RegionCoord(3, 0);
        var scene = new RecordingTileWorldScene();
        TileWorldViewOptions options = Authored();
        using var view = View(scene, WaterAndGrassOnly(grassOnly), options);
        view.LoadRegion(Origin);
        view.LoadRegion(grassOnly);
        WaterOpticsHandle first = Assert.Single(Water(view, scene)).Optics.Field;
        int meshes = scene.MeshLoads.Count;

        options.WaterLook = new WaterLook { WaveSource = WaterWaveSource.FftOcean };
        view.Flush();
        Assert.Empty(Water(view, scene));
        Assert.Empty(scene.TriangleFields);
        Assert.Equal(new[] { first }, scene.OpticalUnloads);
        Assert.Equal(meshes, scene.MeshLoads.Count);
        TileWaterOpticsFailure failure = Assert.Single(view.WaterOpticsFailures);
        Assert.Equal((Origin, TileWaterOpticsFailureKind.UncertifiedDisplacement), (failure.Region, failure.Kind));
        // The refused state is latched. Further flushes and edits elsewhere neither upload nor throw.
        view.MarkDirty(grassOnly, 0);
        view.Flush();
        view.Flush();
        Assert.Single(scene.TriangleLoads);
        Assert.Single(view.WaterOpticsFailures);
        Assert.Equal(meshes + 1, scene.MeshLoads.Count);

        options.WaterLook = new WaterLook { WaveSource = WaterWaveSource.Procedural };
        view.Flush();
        Assert.Empty(view.WaterOpticsFailures);
        WaterPlane plane = Assert.Single(Water(view, scene));
        Assert.NotEqual(first, plane.Optics.Field);
        Assert.Equal(2, scene.TriangleLoads.Count);
    }
}
