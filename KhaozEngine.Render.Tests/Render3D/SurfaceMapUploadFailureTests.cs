using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class SurfaceMapUploadFailureTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Later_texture_failure_retires_each_successful_map(int successfulCreates)
    {
        using var harness = new AttachmentAssetHarness();
        int from = harness.Factory.Textures.Count;
        var image = new DecodedImage([255, 255, 255, 255], 1, 1);
        harness.Factory.FailTextureCreationAfter(successfulCreates);

        Assert.Throws<InvalidOperationException>(() => harness.Scene.LoadSurfaceMaps(new(image, image, image)));
        List<FakeTexture> textures = harness.Factory.Textures.GetRange(from, harness.Factory.Textures.Count - from);
        harness.Factory.ThrowOnTextureCreate = 0;
        Assert.Equal(successfulCreates, textures.Count);
        Assert.All(textures, texture => Assert.False(texture.Disposed));
        harness.DrainRetired();

        Assert.All(textures, texture => Assert.True(texture.Disposed));
        Assert.Equal(0, harness.Scene.RetiredResourceCount);
    }

    [Fact]
    public void Unload_surface_maps_releases_a_shared_handle_once()
    {
        using var harness = new AttachmentAssetHarness();
        int from = harness.Factory.Textures.Count;
        Scene3D.TextureHandle texture = harness.Scene.LoadTexture([255, 255, 255, 255], 1, 1);
        var maps = new Scene3D.SurfaceMaps(texture, texture, texture);

        harness.Scene.UnloadSurfaceMaps(maps);
        Assert.Equal(1, harness.Scene.RetiredResourceCount);
        harness.DrainRetired();

        Assert.True(harness.Factory.Textures[from].Disposed);
    }

    [Fact]
    public void All_absent_maps_neither_create_nor_unload_scene_defaults()
    {
        using var harness = new AttachmentAssetHarness();
        int count = harness.Factory.Textures.Count;
        var defaults = harness.Factory.Textures.ToArray();

        Scene3D.SurfaceMaps maps = harness.Scene.LoadSurfaceMaps(default);
        harness.Scene.UnloadSurfaceMaps(maps);
        harness.DrainRetired();

        Assert.Equal(count, harness.Factory.Textures.Count);
        Assert.All(defaults, texture => Assert.False(texture.Disposed));
        Assert.Equal(0, harness.Scene.RetiredResourceCount);
    }
}
