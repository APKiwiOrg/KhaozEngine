using System;
using System.Numerics;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.InteriorCut;

public class InteriorCutFrameTests
{
    [Fact]
    public void AdjacentNonBinaryCellPositionsUseTheShaderReciprocal()
    {
        var field = new InteriorHeightField(new(Vector2.Zero, .24f, 2), new(0f, .01f),
            [0, 0, 1, 44, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], 0, 0);
        float[] values = [MathF.BitDecrement(.24f), .24f, MathF.BitIncrement(.24f)];
        foreach (float edge in values)
        {
            float shaderCell = edge * (1f / .24f);
            Assert.True(shaderCell >= 1f);
            var point = new Vector3(edge, 1f, .12f);
            Assert.False(field.Contains(point));
            Assert.True(field.FirstUncut(point, Vector3.Zero, 0f, 1f, out float t));
            Assert.Equal(0f, t);
        }
    }

    [Fact]
    public void NonzeroRenderHeightUsesRoundedRelativePointAndBase()
    {
        var field = new InteriorHeightField(new(Vector2.Zero, .24f, 1), new(0f, .01f), [0, 15, 0, 40], 0, 0);
        float[] heights = [MathF.BitDecrement(.15f), .15f, MathF.BitIncrement(.15f),
            MathF.BitDecrement(.4f), .4f, MathF.BitIncrement(.4f)];
        foreach (float height in heights)
        {
            float relative = (float)((double)height - 128d);
            float q = (relative - -128f) * 100f;
            bool shaderInside = q >= 15f && q < 40f;
            Assert.Equal(height < .2f, shaderInside);
            var world = new Vector3(.12f, height, .12f);
            var frame = new InteriorCutFrame(0d, 128d, 0d);
            Assert.Equal(shaderInside, field.Contains(world, frame));
            Assert.Equal(!shaderInside, field.FirstUncut(world, Vector3.Zero, 0f, 1f, frame, out _));
        }
    }

    [Theory]
    [InlineData(0, -1f)]
    [InlineData(0, 1f)]
    [InlineData(1, -1f)]
    [InlineData(1, 1f)]
    [InlineData(2, -1f)]
    [InlineData(2, 1f)]
    public void DdaConfirmsTheFirstRepresentedExitWithTheFramePredicate(int axis, float sign)
    {
        var frame = new InteriorCutFrame(4096d, 128d, -4096d);
        var field = new InteriorHeightField(new(new Vector2(4096f, -4096f), .24f, 2), new(0f, .01f),
            [0, 15, 0, 40, 0, 15, 0, 40, 0, 15, 0, 40, 0, 15, 0, 40], 0, 0);
        var origin = new Vector3(4096.12f, .25f, -4095.88f);
        Vector3 direction = (axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ) * sign;

        Assert.True(field.FirstUncut(origin, direction, 0f, 1f, frame, out float t));
        Assert.InRange(t, .09f, .37f);
        Assert.False(field.Contains(origin + direction * t, frame));
        float previous = MathF.BitDecrement(t);
        Assert.True(field.Contains(origin + direction * previous, frame));
        Assert.False(field.FirstUncut(origin, direction, 0f, previous, frame, out _));

        var (window, heights) = frame.Uniforms(field);
        Vector3 p = frame.ToRender(origin + direction * t);
        float x = (p.X - window.X) * window.Z, z = (p.Z - window.Y) * window.Z;
        float q = (p.Y - heights.X) * heights.Y;
        Assert.False(x >= 0f && x < 2f && z >= 0f && z < 2f && q >= 15f && q < 40f);
    }
}
