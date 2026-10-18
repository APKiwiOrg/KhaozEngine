using System;
using System.Numerics;
using KhaozEngine.Primitives;
using Xunit;
using static KhaozEngine.Tests.InteriorCut.InteriorHeightFieldTests;

namespace KhaozEngine.Tests.InteriorCut;

public class InteriorFieldRayTests
{
    [Fact]
    public void FirstUncutMatchesDenseOracle()
    {
        var field = Build([Rect(-2f, -2f, 0f, 2f, 0f, 3f), Rect(0f, -1f, 2f, 1f, 1f, 2f)], new InteriorWindow(new Vector2(-2f), 0.5f, 8));
        var random = new Random(1470);
        for (int ray = 0; ray < 100; ray++)
        {
            Vector3 origin = new(Next(-2.5f, 2.5f), Next(-1f, 4f), Next(-2.5f, 2.5f));
            Vector3 direction = new(Next(-2f, 2f), Next(-2f, 2f), Next(-2f, 2f));
            bool found = field.FirstUncut(origin, direction, 0f, 3f, out float first);
            if (found)
            {
                Assert.InRange(first, 0f, 3f);
                Assert.False(field.Contains(origin + direction * first));
                if (first > 0f)
                    Assert.True(field.Contains(origin + direction * MathF.BitDecrement(first)));
            }
            for (int sample = 0; sample <= 1000; sample++)
            {
                float time = 3f * sample / 1000f;
                if (!field.Contains(origin + direction * time))
                {
                    Assert.True(found);
                    Assert.True(first <= time);
                    break;
                }
            }
            float Next(float min, float max) => min + (float)random.NextDouble() * (max - min);
        }
    }

    [Fact]
    public void ExactIntervalEndpointsRespectHalfOpenSpans()
    {
        var field = Build([Rect(0f, 0f, 2f, 2f, 0f, 3f)], new InteriorWindow(Vector2.Zero, 0.5f, 4));
        Vector3 inside = new(0.5f, 1f, 0.5f);
        const float whollyCutEnd = 1f;
        Assert.False(field.FirstUncut(inside, Vector3.UnitX, 0f, whollyCutEnd, out _));
        Vector3 onFloor = new(0.5f, 0f, 0.5f);
        Assert.False(field.FirstUncut(onFloor, Vector3.UnitY, 0.25f, MathF.BitDecrement(3f), out _));
        Assert.True(field.FirstUncut(onFloor, Vector3.UnitY, 0.25f, 3f, out float top));
        Assert.Equal(3f, top);
        Assert.False(field.FirstUncut(inside, -Vector3.UnitY, 0f, 1f, out _));
        Assert.True(field.FirstUncut(inside, -Vector3.UnitY, 0f, MathF.BitIncrement(1f), out float belowFloor));
        Assert.Equal(MathF.BitIncrement(1f), belowFloor);
        Assert.True(field.FirstUncut(inside, Vector3.UnitX, 0f, 1.5f, out float edge));
        Assert.Equal(1.5f, edge);
        Vector3 outside = new(-1f, 1f, 0f);
        Assert.True(field.FirstUncut(outside, Vector3.UnitY, 0f, 1f, out float first));
        Assert.Equal(0f, first);
        Assert.True(field.FirstUncut(outside, Vector3.UnitX, 0.5f, 2f, out float start));
        Assert.Equal(0.5f, start);
    }

    [Theory]
    [InlineData(1f, 0f)]
    [InlineData(-1f, 0f)]
    [InlineData(0f, 1f)]
    [InlineData(0f, -1f)]
    [InlineData(1f, 1f)]
    [InlineData(-1f, -1f)]
    [InlineData(1f, -1f)]
    [InlineData(-1f, 1f)]
    public void BoundaryAndVerticalRaysTerminate(float dx, float dz)
    {
        var field = Build([Rect(-2f, -2f, 2f, 2f)], new InteriorWindow(new Vector2(-2f), 0.5f, 8));
        Assert.True(field.FirstUncut(new Vector3(0f, 1f, 0f), new Vector3(dx, 0f, dz), 0f, 3f, out float edge));
        // Subtracting the negative window minimum rounds the positive edge to cell 8 one t early.
        Assert.Equal(dx > 0f || dz > 0f ? MathF.BitDecrement(2f) : MathF.BitIncrement(2f), edge);
        Assert.False(field.Contains(new Vector3(dx * edge, 1f, dz * edge)));
        float previous = MathF.BitDecrement(edge);
        Assert.True(field.Contains(new Vector3(dx * previous, 1f, dz * previous)));
        Assert.False(field.FirstUncut(new Vector3(0f, 1f, 0f), Vector3.Zero, 0f, 100f, out _));
        Assert.False(field.FirstUncut(new Vector3(0f, 1f, 0f), Vector3.UnitY, 0f, 0f, out _));
        Assert.True(field.FirstUncut(new Vector3(-3f, 1f, -3f), Vector3.Zero, 2f, 2f, out float point));
        Assert.Equal(2f, point);
    }

    [Fact]
    public void NegativeBoundaryBelongsToCellOnItsPositiveSide()
    {
        var field = Build([Rect(-1f, -1f, 0f, 0f)], new InteriorWindow(new Vector2(-1f), 0.5f, 4));
        Vector3 inside = new(-0.5f, 1f, -0.5f);
        Assert.False(field.FirstUncut(inside, -Vector3.UnitX, 0f, 0.5f, out _));
        Assert.True(field.FirstUncut(inside, -Vector3.UnitX, 0f, 1f, out float left));
        Assert.True(left > 0.5f);
        Assert.False(field.Contains(inside - Vector3.UnitX * left));
        Assert.True(field.Contains(inside - Vector3.UnitX * MathF.BitDecrement(left)));
        Assert.True(field.FirstUncut(inside, Vector3.UnitX, 0f, 1f, out float right));
        Assert.Equal(MathF.BitDecrement(0.5f), right);
        Assert.False(field.Contains(inside + Vector3.UnitX * right));
        Assert.True(field.Contains(inside + Vector3.UnitX * MathF.BitDecrement(right)));
    }

    [Fact]
    public void NonFiniteOrReversedRayIntervalsAreRejected()
    {
        var field = Build([], new InteriorWindow(Vector2.Zero, 1f, 1));
        Assert.ThrowsAny<ArgumentException>(() => field.FirstUncut(Vector3.Zero, Vector3.One, 2f, 1f, out _));
        Assert.ThrowsAny<ArgumentException>(() => field.FirstUncut(Vector3.Zero, Vector3.One, 0f, float.PositiveInfinity, out _));
        Assert.ThrowsAny<ArgumentException>(() => field.FirstUncut(new Vector3(float.NaN), Vector3.One, 0f, 1f, out _));
        Assert.ThrowsAny<ArgumentException>(() => field.FirstUncut(Vector3.Zero, new Vector3(float.NaN), 0f, 1f, out _));
    }
}
