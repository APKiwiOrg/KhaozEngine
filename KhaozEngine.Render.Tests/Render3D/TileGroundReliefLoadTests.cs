using System;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// <c>Scene3D.LoadTileGroundMaterial</c> with relief layers, headless on <see cref="FakeGpuDevice"/>, which records
/// every texture and buffer the scene asks for: what the surface array holds, the surface refusals, and that a
/// refused relief layer creates nothing.
/// </summary>
public sealed class TileGroundReliefLoadTests
{
    static (FakeGpuResourceFactory Factory, Scene3D Scene) NewScene()
    {
        var fake = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)fake.Factory;
        IGpuTexture tex = factory.CreateTexture(GpuTextureDescription.Texture2D(
            16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        IGpuFramebuffer fb = factory.CreateFramebuffer(null, tex);
        return (factory, new Scene3D(fake, fb.Outputs));
    }

    static TileGroundLayerImage Mapped(int surfaceTexels) => new()
    {
        AlbedoRgba = new byte[4 * 4 * 4],
        SurfaceRgba = new byte[surfaceTexels * 4],
        NormalMapped = true,
    };

    static TileGroundLayerImage Plain() => new() { AlbedoRgba = new byte[4 * 4 * 4] };

    [Fact]
    public void The_surface_array_holds_one_layer_per_mapped_layer_at_the_surface_size()
    {
        (FakeGpuResourceFactory factory, Scene3D scene) = NewScene();
        using (scene)
        {
            int before = factory.Textures.Count;

            Assert.True(scene.LoadTileGroundMaterial(4, 4, new[] { Mapped(8 * 8), Plain(), Mapped(8 * 8) }, 8, 8).IsValid);

            // The albedo array, then the surface array of the two mapped layers at their own size.
            FakeTexture[] made = factory.Textures.Skip(before).ToArray();
            Assert.Equal(2, made.Length);
            Assert.Equal((4u, 4u, 3u), (made[0].Width, made[0].Height, made[0].ArrayLayers));
            Assert.Equal((8u, 8u, 2u), (made[1].Width, made[1].Height, made[1].ArrayLayers));
            Assert.Equal(TileGroundMaterialConfig.MipLevelCount(8, 8), made[1].MipLevels);
        }
    }

    [Fact]
    public void A_relief_set_without_surface_texels_binds_one_flat_texel_and_a_plain_set_binds_none()
    {
        (FakeGpuResourceFactory factory, Scene3D scene) = NewScene();
        using (scene)
        {
            int before = factory.Textures.Count;
            Assert.True(scene.LoadTileGroundMaterial(4, 4, new[] { Plain(), Plain() }).IsValid);
            Assert.Equal(before + 1, factory.Textures.Count);

            before = factory.Textures.Count;
            TileGroundLayerImage height = Plain();
            height.HeightMapped = true;
            Assert.True(scene.LoadTileGroundMaterial(4, 4, new[] { height, Plain() }).IsValid);
            FakeTexture[] made = factory.Textures.Skip(before).ToArray();
            Assert.Equal(2, made.Length);
            Assert.Equal((1u, 1u, 1u, 1u), (made[1].Width, made[1].Height, made[1].ArrayLayers, made[1].MipLevels));
        }
    }

    [Fact]
    public void Surface_texels_of_the_wrong_length_are_refused_naming_the_layer()
    {
        (FakeGpuResourceFactory factory, Scene3D scene) = NewScene();
        using (scene)
        {
            var layers = new[] { Mapped(8 * 8), Mapped(4 * 4) };
            int textures = factory.Textures.Count;

            var error = Assert.Throws<TileGroundUploadArgumentException>(() =>
                scene.LoadTileGroundMaterial(4, 4, layers, 8, 8));

            Assert.Equal("layers", error.ParamName);
            Assert.Equal(TileGroundUploadRefusal.SurfaceByteLengthMismatch, error.Reason);
            Assert.Equal(1, error.LayerIndex);
            Assert.Equal(4 * 4 * 4, error.ActualByteLength);
            Assert.Equal(8 * 8 * 4, error.ExpectedByteLength);
            Assert.Equal(8, error.Width);
            Assert.Equal(8, error.Height);
            Assert.Equal(textures, factory.Textures.Count);

            layers[1].SurfaceRgba = new byte[8 * 8 * 4];
            Assert.True(scene.LoadTileGroundMaterial(4, 4, layers, 8, 8).IsValid);
        }
    }

    [Theory]
    [InlineData(0, 8, "surfaceWidth")]
    [InlineData(8, 0, "surfaceHeight")]
    public void Surface_texels_need_a_positive_surface_size(int width, int height, string parameter)
    {
        (_, Scene3D scene) = NewScene();
        using (scene)
        {
            var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
                scene.LoadTileGroundMaterial(4, 4, new[] { Mapped(8 * 8) }, width, height));
            Assert.Equal(parameter, error.ParamName);

            // A set with no surface texels never reads the surface size.
            TileGroundLayerImage breakup = Plain();
            breakup.FeatherBreakup = 0.5f;
            Assert.True(scene.LoadTileGroundMaterial(4, 4, new[] { breakup }, width, height).IsValid);
        }
    }

    [Fact]
    public void A_refused_relief_layer_throws_before_any_texture_or_buffer_is_created()
    {
        (FakeGpuResourceFactory factory, Scene3D scene) = NewScene();
        using (scene)
        {
            TileGroundLayerImage layer = Mapped(8 * 8);
            layer.FeatherBreakup = 2f;
            int textures = factory.Textures.Count;
            int buffers = factory.Buffers.Count;

            var ex = Assert.Throws<TileGroundReliefArgumentException>(() =>
                scene.LoadTileGroundMaterial(4, 4, new[] { Plain(), layer }, 8, 8));

            Assert.Equal(TileGroundReliefRefusal.FeatherBreakupOutsideRange, ex.Reason);
            Assert.Equal(1, ex.LayerIndex);
            Assert.Equal(textures, factory.Textures.Count);
            Assert.Equal(buffers, factory.Buffers.Count);
        }
    }
}
