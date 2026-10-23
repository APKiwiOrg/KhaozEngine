using System.Numerics;
using KhaozEngine.WorldMap.Gui;
using Xunit;

namespace KhaozEngine.Tests.WorldMap.Gui;

/// <summary>Checks draw coordinates against chunk-interior expectations on a one-pixel gutter upload.</summary>
internal static class ChunkUv
{
    internal static void AssertInterior(Vector4 interior, WorldMapDrawItem<FakeTexture> item)
    {
        FakeTexture texture = Assert.IsType<FakeTexture>(item.Texture);
        Assert.Equal(texture.Width, texture.Height);
        Vector4 expected = (Vector4.One + interior * (texture.Width - 2)) / texture.Width;
        Assert.Equal(expected.X, item.SourceUv.X, 6);
        Assert.Equal(expected.Y, item.SourceUv.Y, 6);
        Assert.Equal(expected.Z, item.SourceUv.Z, 6);
        Assert.Equal(expected.W, item.SourceUv.W, 6);
    }
}
