using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// Pins the single-level rule on the tile-ground albedo array: a 1x1 flat-colour set (the untextured world's
/// shape) must neither declare the GenerateMipmaps usage nor run a generation pass, because the native Vulkan
/// backend refuses generation on a texture with nothing to generate (found by the vulkan-native guest leg on
/// the first untextured tile-world golden), while a multi-level set keeps both.
/// </summary>
public sealed class TileGroundMaterialMipTests
{
    sealed class Harness
    {
        internal required FakeGpuResourceFactory Factory { get; init; }
        internal required Scene3D Scene { get; init; }
    }

    static Harness NewHarness()
    {
        var fake = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)fake.Factory;
        IGpuTexture tex = factory.CreateTexture(GpuTextureDescription.Texture2D(
            16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        IGpuFramebuffer fb = factory.CreateFramebuffer(null, tex);
        return new Harness { Factory = factory, Scene = new Scene3D(fake, fb.Outputs) };
    }

    static List<TileGroundLayerImage> Layers(int w, int h) =>
        new() { new TileGroundLayerImage { AlbedoRgba = new byte[w * h * 4] } };

    static FakeTexture ArrayOf(Harness h, int texturesFrom) =>
        Assert.Single(h.Factory.Textures.Skip(texturesFrom).Where(t => t.MipLevels >= 1).Cast<FakeTexture>());

    [Fact]
    public void A_single_level_array_neither_declares_nor_runs_mip_generation()
    {
        Harness h = NewHarness();
        int from = h.Factory.Textures.Count;

        h.Scene.LoadTileGroundMaterial(1, 1, Layers(1, 1));

        FakeTexture array = ArrayOf(h, from);
        Assert.Equal(1u, array.MipLevels);
        Assert.False(array.Usage.HasFlag(GpuTextureUsage.GenerateMipmaps));
        Assert.DoesNotContain(h.Factory.CommandLists, cl => cl.MipGenerations.Contains(array));
    }

    [Fact]
    public void A_one_layer_set_is_padded_to_two_so_the_texture_stays_an_array()
    {
        // Every backend derives array-ness from the layer count, so a one-layer texture would bind as plain 2D
        // under a fragment that declares texture2DArray, which Metal validation kills at the first draw.
        Harness h = NewHarness();
        int from = h.Factory.Textures.Count;

        h.Scene.LoadTileGroundMaterial(1, 1, Layers(1, 1));

        FakeTexture array = ArrayOf(h, from);
        Assert.Equal(2u, array.ArrayLayers);
    }

    [Fact]
    public void A_multi_level_array_declares_the_usage_and_generates_once()
    {
        Harness h = NewHarness();
        int from = h.Factory.Textures.Count;

        h.Scene.LoadTileGroundMaterial(8, 8, Layers(8, 8));

        FakeTexture array = ArrayOf(h, from);
        Assert.True(array.MipLevels > 1);
        Assert.True(array.Usage.HasFlag(GpuTextureUsage.GenerateMipmaps));
        Assert.Equal(1, h.Factory.CommandLists.Sum(cl => cl.MipGenerations.Count(t => ReferenceEquals(t, array))));
    }
}
