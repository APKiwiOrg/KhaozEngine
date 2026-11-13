using System.IO;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.Tests.Render3D;
using KhaozEngine.TileWorld;
using SharpGLTF.Materials;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>A tile-world kit whose pieces share one tileable texture uploads it once (issue #1776): the view uploads
/// every archetype through the scene's material residency, and disposing the view releases every texture it took.
/// The same check works headlessly for a game's own kit: resolve it, build the view, read
/// <see cref="Scene3D.ResidentMaterialTextureCount"/>.</summary>
public sealed class TileWorldMaterialResidencyTests
{
    [Fact]
    public void Kit_pieces_sharing_one_texture_file_upload_it_once_and_release_it_with_the_view()
    {
        using var dir = new TempDir();
        string kit = Path.Combine(dir.Path, "greybox");
        Directory.CreateDirectory(kit);
        byte[] oak = MaterialGlbFixture.UniquePng();
        // Three greybox archetypes get a real piece, each referencing the one oak.png beside it. The other nine fall
        // back to untextured boxes.
        foreach (string piece in new[] { "wall", "fence", "tree" })
            MaterialGlbFixture.Write(kit, piece + ".glb",
                new MaterialBuilder("oak").WithBaseColor(new SharpGLTF.Memory.MemoryImage(oak)), satelliteNames: ["oak.png"]);
        using var device = new FakeGpuDevice();
        using var scene = new Scene3D(device, new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm));
        int textures = scene.LiveTextureCount;

        var view = new TileWorldView(new Scene3DTileWorldScene(scene), TileRenderTestData.GreyboxWorld(),
            TileRenderTestData.Catalogs, new GltfMeshResolver(dir.Path, new GreyboxMeshResolver()));
        Assert.Equal(1, scene.ResidentMaterialTextureCount);

        view.Dispose();
        Assert.Equal(0, scene.ResidentMaterialTextureCount);
        Assert.Equal(textures, scene.LiveTextureCount);
    }
}
