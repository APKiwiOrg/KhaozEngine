using System;
using System.Linq;
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FftLookIsRefusedBeforeAnyFieldUpload(bool explicitLook)
    {
        // Real scene padding: FFT water has no certified displacement envelope, through the look or its default.
        using var scene = new Scene3D(new FakeGpuDevice(), new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm));
        WaterLook? look = explicitLook ? new WaterLook { WaveSource = WaterWaveSource.FftOcean, SwellAmplitude = 0f } : null;
        if (!explicitLook) scene.Post.Water.WaveSource = WaterWaveSource.FftOcean;
        var doc = new TileWorldDocument { Id = "fft-refusal", DisplayName = "FFT refusal", PlaneCount = 1 };
        doc.GetOrCreateRegion(Origin);
        Fill(doc, TileRenderTestData.Grass, 4, 4, 24, 24);
        Fill(doc, TileRenderTestData.Water, 10, 10, 13, 13);
        using var view = View(new Scene3DTileWorldScene(scene), doc, Authored(look));

        NotSupportedException refused = Assert.Throws<NotSupportedException>(() => view.LoadRegion(Origin));
        Assert.Contains("FFT", refused.Message);
        Assert.Throws<NotSupportedException>(() => view.Flush());
        Assert.Equal(0L, scene.WaterOpticsStats.UploadCount);
        Assert.Equal(0, scene.WaterOpticsStats.LiveFields);
        Assert.Empty(view.WaterOpticsFailures);
    }
}
