using System;
using System.Numerics;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.InteriorCut;

public sealed class InteriorCodeSpaceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonzeroCodesMatchShaderAtBothRepresentedBoundaries(bool largeRange)
    {
        InteriorHeightField field = Field(largeRange);
        Assert.True(field.TryGetSpan(0, 0, out float floor, out float top));
        float[] heights = [MathF.BitDecrement(floor), floor, MathF.BitIncrement(floor),
            MathF.BitDecrement(top), top, MathF.BitIncrement(top)];
        bool[] expected = [false, false, true, true, true, false];
        for (int i = 0; i < heights.Length; i++)
        {
            float height = heights[i];
            bool shaderInside = ShaderInside(field, height);
            Assert.Equal(expected[i], shaderInside);
            var point = new Vector3(.5f, height, .5f);
            Assert.Equal(shaderInside, field.Contains(point));
            Assert.Equal(!shaderInside, field.FirstUncut(point, Vector3.Zero, 0f, 1f, out float t));
            if (!shaderInside) Assert.Equal(0f, t);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FirstUncutFindsTheFirstCodeSpaceExit(bool largeRange, bool descending)
    {
        InteriorHeightField field = Field(largeRange);
        Assert.True(field.TryGetSpan(0, 0, out float floor, out float top));
        Vector3 origin = new(.5f, 0f, .5f);
        Vector3 direction = descending ? -Vector3.UnitY : Vector3.UnitY;
        float start = (descending ? -25f : 25f) * field.StepY;
        float end = descending ? -MathF.BitDecrement(floor) : MathF.BitIncrement(top);
        float expected = descending ? -floor : MathF.BitIncrement(top);
        Assert.True(ShaderInside(field, (origin + direction * start).Y));

        Assert.True(field.FirstUncut(origin, direction, start, end, out float t));

        Assert.Equal(expected, t);
        Assert.False(ShaderInside(field, (origin + direction * t).Y));
        Assert.False(field.Contains(origin + direction * t));
        float previous = MathF.BitDecrement(t);
        Assert.True(ShaderInside(field, (origin + direction * previous).Y));
        Assert.True(field.Contains(origin + direction * previous));
        Assert.False(field.FirstUncut(origin, direction, start, previous, out _));
    }

    static InteriorHeightField Field(bool largeRange)
    {
        InteriorEncoding encoding = largeRange ? InteriorEncoding.For(0f, 1000f) : new(0f, .01f);
        if (largeRange) Assert.True(encoding.StepY > .01f);
        return new InteriorHeightField(new InteriorWindow(Vector2.Zero, 1f, 1), encoding, [0, 15, 0, 40], 0, 0);
    }

    // Emulate the shader's float32 expression directly, independently of decoded world-height endpoints.
    static bool ShaderInside(InteriorHeightField field, float height)
    {
        ReadOnlySpan<byte> bytes = field.Texels.Span;
        float lo = bytes[0] * 256 + bytes[1], hi = bytes[2] * 256 + bytes[3];
        float q = (height - field.BaseY) * (1f / field.StepY);
        return hi > lo && q >= lo && q < hi;
    }
}
