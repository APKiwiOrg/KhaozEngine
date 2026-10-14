using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests;

public sealed class NameplateStackerTests
{
    [Fact]
    public void EmptyAndSingleFramesAreNoOps()
    {
        var s = new NameplateStacker();
        s.Begin();
        s.Resolve(0f);
        Assert.True(s.DrawOrder.IsEmpty);
        Assert.Throws<KeyNotFoundException>(() => s.RectOf(1));

        s.Begin();
        s.Add(1, new Vector4(10f, 100f, 40f, 10f), 3f, false);
        s.Resolve(0f);
        Assert.Equal(new Vector4(10f, 100f, 40f, 10f), s.RectOf(1));
        Assert.Equal(new long[] { 1 }, s.DrawOrder.ToArray());
    }

    [Fact]
    public void DuplicateIdsKeepTheLastValidSubmission()
    {
        var s = new NameplateStacker();
        s.Begin();
        s.Add(1, new Vector4(10f, 100f, 40f, 10f), 3f, false);
        s.Add(1, new Vector4(20f, 120f, 50f, 12f), 1f, true);
        s.Add(1, new Vector4(float.NaN, 0f, 1f, 1f), 0f, false);
        s.Resolve(0f);
        Assert.Equal(new Vector4(20f, 120f, 50f, 12f), s.RectOf(1));
        Assert.Equal(new long[] { 1 }, s.DrawOrder.ToArray());
    }

    [Fact]
    public void BeginInvalidatesOutputAndResolveDropsMissingIds()
    {
        var s = new NameplateStacker();
        s.Begin();
        s.Add(1, new Vector4(10f, 100f, 40f, 10f), 3f, false);
        s.Resolve(0f);

        s.Begin();
        Assert.True(s.DrawOrder.IsEmpty);
        Assert.Throws<KeyNotFoundException>(() => s.RectOf(1));
        s.Resolve(0f);
        Assert.Throws<KeyNotFoundException>(() => s.RectOf(1));
    }

    [Theory]
    [InlineData(float.NaN, 100f, 40f, 10f, 1f)]
    [InlineData(10f, float.PositiveInfinity, 40f, 10f, 1f)]
    [InlineData(10f, 100f, 0f, 10f, 1f)]
    [InlineData(10f, 100f, 40f, 0f, 1f)]
    [InlineData(10f, 100f, -1f, 10f, 1f)]
    [InlineData(float.MaxValue, 100f, float.MaxValue, 10f, 1f)]
    [InlineData(10f, float.MaxValue, 40f, float.MaxValue, 1f)]
    [InlineData(10f, 100f, 40f, 10f, float.NaN)]
    [InlineData(10f, 100f, 40f, 10f, float.PositiveInfinity)]
    [InlineData(10f, 100f, 40f, 10f, float.NegativeInfinity)]
    public void InvalidInputsNeverEnterTheFrame(float x, float y, float width, float height, float rank)
    {
        var s = new NameplateStacker();
        s.Begin();
        s.Add(1, new Vector4(x, y, width, height), rank, false);
        s.Add(2, new Vector4(10f, 100f, 40f, 10f), 2f, false);
        s.Resolve(0f);
        Assert.Equal(new long[] { 2 }, s.DrawOrder.ToArray());
        Assert.Throws<KeyNotFoundException>(() => s.RectOf(1));
    }

    [Theory]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void InvalidTopMarginIsRejected(float topMargin)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NameplateStacker(topMargin));
    }

    [Fact]
    public void ResolveCompactsMissingEntriesWithoutLosingSurvivors()
    {
        var s = new NameplateStacker();
        s.Begin();
        s.Add(1, new Vector4(10f, 100f, 40f, 10f), 3f, false);
        s.Add(2, new Vector4(100f, 100f, 40f, 10f), 2f, false);
        s.Add(3, new Vector4(200f, 100f, 40f, 10f), 1f, false);
        s.Resolve(0f);

        s.Begin();
        s.Add(2, new Vector4(110f, 120f, 50f, 12f), 2f, false);
        s.Resolve(0f);
        Assert.Equal(new long[] { 2 }, s.DrawOrder.ToArray());
        Assert.Equal(new Vector4(110f, 120f, 50f, 12f), s.RectOf(2));
        Assert.Throws<KeyNotFoundException>(() => s.RectOf(1));
        Assert.Throws<KeyNotFoundException>(() => s.RectOf(3));

        s.Begin();
        s.Add(3, new Vector4(210f, 140f, 40f, 10f), 1f, false);
        s.Add(2, new Vector4(120f, 140f, 50f, 12f), 2f, false);
        s.Resolve(0f);
        Assert.Equal(2, s.DrawOrder.Length);
        Assert.Equal(new Vector4(210f, 140f, 40f, 10f), s.RectOf(3));
        Assert.Equal(new Vector4(120f, 140f, 50f, 12f), s.RectOf(2));
    }

    [Fact]
    public void FramesCanGrowPastInitialCapacityAndReuseIds()
    {
        var s = new NameplateStacker();
        s.Begin();
        for (int id = 0; id < 40; id++)
            s.Add(id, new Vector4(id * 100f, 100f, 40f, 10f), id, false);
        s.Resolve(0f);
        Assert.Equal(40, s.DrawOrder.Length);
        for (int id = 0; id < 40; id++)
            Assert.Equal(new Vector4(id * 100f, 100f, 40f, 10f), s.RectOf(id));

        s.Begin();
        for (int id = 39; id >= 0; id--)
            s.Add(id, new Vector4(id * 100f, 120f, 50f, 12f), id, false);
        s.Resolve(0f);
        Assert.Equal(40, s.DrawOrder.Length);
        for (int id = 0; id < 40; id++)
            Assert.Equal(new Vector4(id * 100f, 120f, 50f, 12f), s.RectOf(id));
    }
}
