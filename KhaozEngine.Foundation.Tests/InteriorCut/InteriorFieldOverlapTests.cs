using System;
using System.Numerics;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.InteriorCut;

public sealed class InteriorFieldOverlapTests
{
    [Theory]
    [InlineData(1f, false)]
    [InlineData(1.2f, false)]
    [InlineData(1f, true)]
    [InlineData(1.2f, true)]
    public void MayIntersectNeverMissesACutPoint(float tileSize, bool far)
    {
        float cell = tileSize == 1f ? .25f : .24f;
        Vector2 offset = far ? new(4096f, -4096f) : Vector2.Zero;
        var frame = far ? new InteriorCutFrame(4096d, 128d, -4096d) : default;
        var window = new InteriorWindow(offset - new Vector2(8f * cell), cell, 64);
        var field = InteriorHeightFieldBuilder.Build(
            [Room(offset, -1f, -1f, 2f, 3f, -.15f, .4f), Room(offset, 4f, 2f, 7f, 5f, 2.1f, 5.4f)],
            window, InteriorEncoding.For(-.15f, 5.4f));
        var random = new Random(107019);
        int cutBoxes = 0;
        for (int i = 0; i < 400; i++)
        {
            var min = new Vector3(offset.X - 2f + (float)random.NextDouble() * 10f,
                -1f + (float)random.NextDouble() * 7f, offset.Y - 2f + (float)random.NextDouble() * 9f);
            Vector3 max = min + new Vector3((float)random.NextDouble() * 3f,
                (float)random.NextDouble() * 2f, (float)random.NextDouble() * 3f);
            bool cut = false;
            for (int z = 0; z <= 8 && !cut; z++)
                for (int y = 0; y <= 8 && !cut; y++)
                    for (int x = 0; x <= 8 && !cut; x++)
                        cut = ShaderContains(field, frame, new Vector3(
                            float.Lerp(min.X, max.X, x / 8f), float.Lerp(min.Y, max.Y, y / 8f),
                            float.Lerp(min.Z, max.Z, z / 8f)));
            if (!cut) continue;
            cutBoxes++;
            Assert.True(field.MayIntersect(min, max, frame), $"Missed cut box {min} .. {max}, frame {frame}");
            if (!far) Assert.True(field.MayIntersect(min, max));
        }
        Assert.True(cutBoxes > 20);
    }

    [Fact]
    public void MayIntersectRejectsFarBoxes()
    {
        var field = InteriorHeightFieldBuilder.Build([Room(Vector2.Zero, 0, 0, 2, 2, 0, 3)],
            new InteriorWindow(new Vector2(-16), .25f, 128), InteriorEncoding.For(0, 3));
        Assert.False(field.MayIntersect(new(8, 1, 8), new(10, 2, 10)));
        Assert.False(field.MayIntersect(new(-30, 0, -30), new(-20, 1, -20)));
        Assert.False(field.MayIntersect(new(.5f, 5, .5f), new(1, 6, 1)));
        Assert.False(field.MayIntersect(new(.5f, -5, .5f), new(1, -4, 1)));
        var empty = InteriorHeightFieldBuilder.Build([], field.Window, InteriorEncoding.For(0, 3));
        Assert.False(empty.MayIntersect(new(-100), new(100)));
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(128d)]
    [InlineData(100000d)]
    public void AdjacentRepresentedBoundariesRemainConservative(double originY)
    {
        var field = new InteriorHeightField(new(Vector2.Zero, .24f, 2), new(0f, .01f),
            [0, 15, 0, 40, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], 0, 0);
        var frame = new InteriorCutFrame(0d, originY, 0d);
        foreach (float x in new[] { MathF.BitDecrement(.24f), .24f, MathF.BitIncrement(.24f), .12f })
            foreach (float y in new[] { MathF.BitDecrement(.15f), .15f, MathF.BitIncrement(.15f),
                MathF.BitDecrement(.4f), .4f, MathF.BitIncrement(.4f), .25f })
            {
                var point = new Vector3(x, y, .12f);
                if (ShaderContains(field, frame, point)) Assert.True(field.MayIntersect(point, point, frame));
            }
    }

    static bool ShaderContains(InteriorHeightField field, InteriorCutFrame frame, Vector3 point)
    {
        var (window, heights) = frame.Uniforms(field);
        Vector3 p = frame.ToRender(point);
        float x = (p.X - window.X) * window.Z, z = (p.Z - window.Y) * window.Z;
        if (!(x >= 0 && x < window.W && z >= 0 && z < window.W)) return false;
        int at = ((int)MathF.Floor(z) * field.Window.Cells + (int)MathF.Floor(x)) * 4;
        ReadOnlySpan<byte> texels = field.Texels.Span;
        int lo = (texels[at] << 8) | texels[at + 1], hi = (texels[at + 2] << 8) | texels[at + 3];
        float q = (p.Y - heights.X) * heights.Y;
        return hi > lo && q >= lo && q < hi;
    }

    static InteriorVolume Room(Vector2 offset, float x0, float z0, float x1, float z1, float floor, float top) => new()
    {
        Outline = [offset + new Vector2(x0, z0), offset + new Vector2(x1, z0),
            offset + new Vector2(x1, z1), offset + new Vector2(x0, z1)],
        FloorY = floor,
        CeilingY = top,
    };
}
