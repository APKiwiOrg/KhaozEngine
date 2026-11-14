using System;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.Tests.TileWorld;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class Scene3DReleaseLifetimeTests
{
    static readonly byte[] Pixel = [255, 255, 255, 255];

    static Scene3D NewScene(FakeGpuDevice device) =>
        new(device, new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm));

    [Fact]
    public void TileWorldViewWithWaterCanBeDisposedAfterItsScene()
    {
        // #1778: snapshot capture disposes its scene before the caller can dispose the view built in setup.
        using var device = new FakeGpuDevice();
        using var scene = NewScene(device);
        TileWorldDocument document = TileWorldViewWaterOpticsTests.World();
        document.AddObject("tree", 20, 20, 0, 0);
        using var view = new TileWorldView(new Scene3DTileWorldScene(scene), document,
            TileRenderTestData.Catalogs, new GreyboxMeshResolver(),
            new TileWorldViewOptions { WaterDepthMode = TileWaterDepthMode.AuthoredBed },
            null, new ManualTileBuildDispatcher());
        view.LoadRegion(TileRenderTestData.Region);
        Assert.True(scene.WaterOpticsStats.LiveFields > 0);
        Assert.True(scene.LiveMeshCount > 0);

        scene.Dispose();

        Assert.Null(Record.Exception(view.Dispose));
        Assert.Equal(0, scene.RetiredResourceCount);
    }

    [Theory]
    [InlineData(nameof(Scene3D.UnloadMesh))]
    [InlineData(nameof(Scene3D.UnloadProp))]
    [InlineData(nameof(Scene3D.UnloadSkinnedMesh))]
    [InlineData(nameof(Scene3D.UnloadTexture))]
    [InlineData(nameof(Scene3D.UnloadSurfaceMaps))]
    [InlineData(nameof(Scene3D.UnloadSplatMaterial))]
    [InlineData(nameof(Scene3D.UnloadTileGroundMaterial))]
    public void UnloadAfterSceneDisposalDoesNotRetireAlreadyDestroyedResources(string operation)
    {
        // #1778: all release paths must be inert after scene disposal, including mesh-owned textures.
        using var device = new FakeGpuDevice();
        using var scene = NewScene(device);
        var maps = new GltfMaterialMaps(new DecodedImage(Pixel, 1, 1), null, null);
        Action unload = operation switch
        {
            nameof(Scene3D.UnloadMesh) => Release(scene.LoadMesh(MeshPrimitives.Box(), maps), scene.UnloadMesh),
            nameof(Scene3D.UnloadProp) => Release(scene.LoadProp([new(MeshPrimitives.Box(), maps)]), scene.UnloadProp),
            nameof(Scene3D.UnloadSkinnedMesh) => Release(
                scene.LoadSkinnedMesh(SkinnedMeshBuilder.BuildTube(.5f, 4f, 6, 6, 4, Axis.Z), maps), scene.UnloadSkinnedMesh),
            nameof(Scene3D.UnloadTexture) => Release(scene.LoadTexture(Pixel, 1, 1), scene.UnloadTexture),
            nameof(Scene3D.UnloadSurfaceMaps) => Release(scene.LoadSurfaceMaps(maps), h => scene.UnloadSurfaceMaps(h)),
            nameof(Scene3D.UnloadSplatMaterial) => Release(scene.LoadSplatMaterial(1, 1,
                Enumerable.Range(0, SplatMaterialConfig.LayerCount)
                    .Select(_ => new SplatLayerImage { AlbedoRgba = Pixel, NormalRgba = Pixel }).ToList()),
                scene.UnloadSplatMaterial),
            nameof(Scene3D.UnloadTileGroundMaterial) => Release(scene.LoadTileGroundMaterial(1, 1,
                [new TileGroundLayerImage { AlbedoRgba = Pixel }]), scene.UnloadTileGroundMaterial),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        scene.Dispose();
        Assert.Equal(0, scene.RetiredResourceCount);

        unload();
        unload();

        Assert.Equal(0, scene.RetiredResourceCount);
    }

    static Action Release<T>(T handle, Action<T> unload) => () => unload(handle);
}
