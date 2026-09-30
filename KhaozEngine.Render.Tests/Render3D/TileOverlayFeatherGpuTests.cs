using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.TileWorld;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Render3D;

public sealed class TileOverlayFeatherGpuTests(ITestOutputHelper output)
{
    [GpuFact]
    public void The_authored_mesh_blends_at_the_edge_but_retains_its_opaque_center()
    {
        var doc = new TileWorldDocument();
        doc.GetOrCreateRegion(new RegionCoord(0, 0));
        for (int z = 9; z <= 11; z++)
            for (int x = 9; x <= 11; x++) doc.SetUnderlay(x, z, 0, 1);
        doc.SetOverlay(10, 10, 0, 6);
        doc.SetSettings(10, 10, 0, TileSettings.FeatherOverlay);
        GltfMesh mesh = TileGroundMesher.Build(doc, TileWorldCatalogs.Greybox(), new RegionCoord(0, 0), 0,
            new TileGroundMesherOptions { Slots = new Slots(), JitterAmplitude = 0 })!;
        MeshHandle handle = default;
        const int size = 240;
        byte[] rgba = Render3DSnapshot.Capture(size, size, setup: scene =>
        {
            var material = scene.LoadTileGroundMaterial(1, 1, new[]
            {
                new TileGroundLayerImage { AlbedoRgba = new byte[] { 0, 255, 0, 255 } },
                new TileGroundLayerImage { AlbedoRgba = new byte[] { 255, 0, 0, 255 } },
            }, baseSpecStrength: 0);
            handle = scene.LoadMesh(mesh, material);
            scene.Camera.Azimuth = 0;
            scene.Camera.Elevation = 1.5f;
            scene.Camera.Target = new Vector3(10.5f, 0, -10.5f);
            scene.Camera.AspectRatio = 1;
            scene.Camera.Zoom = 1;
            scene.Camera.OrthoSize = 1.5f;
        }, drawFrame: scene => scene.Draw(handle, Matrix4x4.Identity));
        (int r, int g) Pixel(int x) => (rgba[(120 * size + x) * 4], rgba[(120 * size + x) * 4 + 1]);
        (int outerR, int outerG) = Pixel(36);
        (int fadeR, int fadeG) = Pixel(56);
        (int coreR, int coreG) = Pixel(120);
        output.WriteLine($"outside=({outerR},{outerG}), feather=({fadeR},{fadeG}), core=({coreR},{coreG})");
        Assert.True(outerG > outerR + 60);
        Assert.True(fadeR > 30 && fadeG > 30);
        Assert.True(coreR > coreG + 60);
        string? evidence = Environment.GetEnvironmentVariable("KE_TILE_FEATHER_EVIDENCE");
        if (!string.IsNullOrEmpty(evidence)) KhaozEngine.Imaging.PngWriter.Save(evidence, rgba, size, size);
    }

    sealed class Slots : ITileGroundSlotMap
    {
        public int MissingSlot => 0;
        public int SlotOf(ushort materialId) => materialId == 6 ? 1 : 0;
    }
}
