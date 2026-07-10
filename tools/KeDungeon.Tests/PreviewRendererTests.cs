using System.Collections.Generic;
using KeDungeon;
using KhaozEngine.Dungeon;
using Xunit;

namespace KeDungeon.Tests;

public class PreviewRendererTests
{
    static DungeonLayout SampleLayout()
    {
        return DungeonGenerator.Generate(new DungeonConfig(), 42UL);
    }

    [Fact]
    public void RenderFloor_DimensionsMatch()
    {
        DungeonLayout layout = SampleLayout();

        byte[] rgba = PreviewRenderer.RenderFloorRgba(layout, 0, out int width, out int height);

        Assert.Equal(layout.Width * 8, width);
        Assert.Equal(layout.Depth * 8, height);
        Assert.Equal(width * height * 4, rgba.Length);
    }

    [Fact]
    public void RenderFloor_NonEmpty()
    {
        DungeonLayout layout = SampleLayout();

        byte[] rgba = PreviewRenderer.RenderFloorRgba(layout, 0, out int width, out int height);

        var distinctPixels = new HashSet<(byte R, byte G, byte B, byte A)>();
        for (int i = 0; i < rgba.Length; i += 4)
        {
            distinctPixels.Add((rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]));
        }

        Assert.True(distinctPixels.Count >= 2, "expected at least two distinct pixel values in the rendered floor");
    }
}
