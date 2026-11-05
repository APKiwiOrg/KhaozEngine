using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// A tile-ground set whose steep companion is refused must fail before <c>Scene3D.LoadTileGroundMaterial</c>
/// creates any GPU resource. The params are packed, and so validated, ahead of the albedo upload, and a throw after
/// the upload would leak the texture array it had just created. Headless, on <see cref="FakeGpuDevice"/>, which
/// records every texture and buffer the scene asks for.
/// </summary>
public sealed class TileGroundSteepLoadOrderTests
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

    [Fact]
    public void A_refused_companion_throws_before_any_texture_or_buffer_is_created()
    {
        (FakeGpuResourceFactory factory, Scene3D scene) = NewScene();
        using (scene)
        {
            var layers = new[]
            {
                new TileGroundLayerImage
                {
                    AlbedoRgba = new byte[4],
                    Steep = new TileGroundSteepLayer { CompanionSlot = 0, FromDegrees = 40f, ToDegrees = 55f },
                },
                new TileGroundLayerImage { AlbedoRgba = new byte[4] },
            };
            int textures = factory.Textures.Count;
            int buffers = factory.Buffers.Count;

            var ex = Assert.Throws<TileGroundSteepArgumentException>(() => scene.LoadTileGroundMaterial(1, 1, layers));

            Assert.Equal("layers", ex.ParamName);
            Assert.Equal(TileGroundSteepRefusal.CompanionIsSelf, ex.Reason);
            Assert.Equal(0, ex.LayerIndex);
            Assert.Equal(0, ex.CompanionSlot);
            Assert.Equal(textures, factory.Textures.Count);
            Assert.Equal(buffers, factory.Buffers.Count);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(TileGroundMaterialConfig.MaxMaterials + 1)]
    public void Upload_refuses_the_same_layer_counts_as_params_construction(int count)
    {
        var layers = new TileGroundLayerImage[count];
        for (int i = 0; i < count; i++) layers[i] = new() { AlbedoRgba = new byte[4] };
        var (factory, scene) = NewScene();
        using (scene)
        {
            var error = Assert.Throws<TileGroundUploadArgumentException>(() =>
                scene.LoadTileGroundMaterial(1, 1, layers));

            Assert.Equal("layers", error.ParamName);
            Assert.Equal(TileGroundUploadRefusal.LayerCountOutsideRange, error.Reason);
            Assert.Equal(count, error.LayerCount);
            Assert.Equal(1, error.MinimumLayerCount);
            Assert.Equal(TileGroundMaterialConfig.MaxMaterials, error.MaximumLayerCount);
        }
    }

    [Fact]
    public void Upload_refuses_a_slice_with_the_wrong_byte_length()
    {
        var layers = new[]
        {
            new TileGroundLayerImage { AlbedoRgba = new byte[16] },
            new TileGroundLayerImage { AlbedoRgba = new byte[4] },
        };
        var (factory, scene) = NewScene();
        using (scene)
        {
            var error = Assert.Throws<TileGroundUploadArgumentException>(() =>
                scene.LoadTileGroundMaterial(2, 2, layers));

            Assert.Equal("layers", error.ParamName);
            Assert.Equal(TileGroundUploadRefusal.LayerByteLengthMismatch, error.Reason);
            Assert.Equal(1, error.LayerIndex);
            Assert.Equal(4, error.ActualByteLength);
            Assert.Equal(16, error.ExpectedByteLength);
            Assert.Equal(2, error.Width);
            Assert.Equal(2, error.Height);
            layers[1].AlbedoRgba = new byte[16];
            Assert.True(scene.LoadTileGroundMaterial(2, 2, layers).IsValid);
        }
    }

    [Fact]
    public void A_sound_companion_still_loads()
    {
        (FakeGpuResourceFactory factory, Scene3D scene) = NewScene();
        using (scene)
        {
            var layers = new[]
            {
                new TileGroundLayerImage
                {
                    AlbedoRgba = new byte[4],
                    Steep = new TileGroundSteepLayer { CompanionSlot = 1, FromDegrees = 40f, ToDegrees = 55f },
                },
                new TileGroundLayerImage { AlbedoRgba = new byte[4] },
            };
            int textures = factory.Textures.Count;

            Assert.True(scene.LoadTileGroundMaterial(1, 1, layers).IsValid);
            Assert.Equal(textures + 1, factory.Textures.Count);
        }
    }
}
