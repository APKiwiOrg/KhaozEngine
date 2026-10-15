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

    [Fact]
    public void RankOrderStacksUpWithExactlyTwoPixelsBetweenPanels()
    {
        var r = new Vector4(10f, 100f, 40f, 10f);
        var s = Frame((3, r, 3f, false), (1, r, 1f, false), (2, r, 2f, false));
        Assert.Equal(r, s.RectOf(1));
        Assert.Equal(new Vector4(10f, 88f, 40f, 10f), s.RectOf(2));
        Assert.Equal(new Vector4(10f, 76f, 40f, 10f), s.RectOf(3));
        Assert.Equal(new long[] { 3, 2, 1 }, s.DrawOrder.ToArray());
    }

    [Fact]
    public void MovingAboveAnOverlapRechecksEarlierObstacles()
    {
        var r = new Vector4(10f, 100f, 40f, 10f);
        var s = Frame((1, new Vector4(10f, 100f, 40f, 10f), 1f, true),
            (2, new Vector4(10f, 80f, 40f, 10f), 2f, true), (3, r, 3f, false));
        Assert.Equal(new Vector4(10f, 68f, 40f, 10f), s.RectOf(3));
        Assert.Equal(new long[] { 3, 2, 1 }, s.DrawOrder.ToArray());
    }

    [Fact]
    public void OverlappingPinnedPlatesRemainUnchanged()
    {
        var r = new Vector4(10f, 100f, 40f, 10f);
        var s = Frame((1, r, 1f, true), (2, r, 2f, true), (3, r, 0f, false));
        Assert.Equal(r, s.RectOf(1));
        Assert.Equal(r, s.RectOf(2));
        Assert.Equal(new Vector4(10f, 88f, 40f, 10f), s.RectOf(3));
        Assert.Equal(new long[] { 3, 2, 1 }, s.DrawOrder.ToArray());
    }

    [Fact]
    public void EqualRanksUseIdToBreakTheInitialTie()
    {
        var r = new Vector4(10f, 100f, 40f, 10f);
        var s = Frame((2, r, 1f, false), (1, r, 1f, false));
        Assert.Equal(r, s.RectOf(1));
        Assert.Equal(new Vector4(10f, 88f, 40f, 10f), s.RectOf(2));
        Assert.Equal(new long[] { 2, 1 }, s.DrawOrder.ToArray());
    }

    [Theory]
    [InlineData(52f, 100f)]
    [InlineData(10f, 112f)]
    public void ExactlyTwoPixelsOfHorizontalOrVerticalClearanceNeedsNoMove(float x, float y)
    {
        var r = new Vector4(10f, 100f, 40f, 10f);
        var second = new Vector4(x, y, 40f, 10f);
        var s = Frame((1, r, 1f, true), (2, second, 2f, false));
        Assert.Equal(second, s.RectOf(2));
    }

    [Fact]
    public void TopSaturationTerminatesAndAnAlreadyOverflowingNaturalRectNeverMovesDown()
    {
        var s = Frame((1, new Vector4(10f, 10f, 40f, 10f), 1f, true),
            (2, new Vector4(10f, 10f, 40f, 10f), 2f, false));
        Assert.Equal(new Vector4(10f, 4f, 40f, 10f), s.RectOf(2));
        s = Frame((1, new Vector4(10f, -5f, 40f, 10f), 1f, false));
        Assert.Equal(-5f, s.RectOf(1).Y);
    }

    [Fact]
    public void OversizePanelsNeverShiftSideways()
    {
        var template = new NameplateTemplate(new NameplateComponent[]
        {
            new NameplateTextComponent("name", NameplateSlot.Name),
        })
        {
            EdgeBehavior = NameplateEdgeBehavior.Clamp,
            EdgeMargin = 4f,
        };
        NameplatePlacementState state = default;
        Vector4 wide = NameplatePlacement.Place(new Vector2(50f, 100f), new Vector2(200f, 10f),
            100, 100, template, ref state);
        Assert.Equal(new Vector4(4f, 86f, 200f, 10f), wide);
        var s = Frame((1, wide, 1f, true), (2, wide, 2f, false));
        Assert.Equal(new Vector4(4f, 74f, 200f, 10f), s.RectOf(2));
    }

    [Fact]
    public void DuplicateSubmissionReplacesRankAndPin()
    {
        var r = new Vector4(10f, 100f, 40f, 10f);
        var s = new NameplateStacker();
        s.Begin();
        s.Add(1, r, 1f, false);
        s.Add(1, r, 100f, true);
        s.Add(2, r, 0f, false);
        s.Resolve(0f);
        Assert.Equal(r, s.RectOf(1));
        Assert.Equal(new Vector4(10f, 88f, 40f, 10f), s.RectOf(2));
        Assert.Equal(new long[] { 2, 1 }, s.DrawOrder.ToArray());
    }

    [Fact]
    public void ACustomTopMarginOverridesTheDefault()
    {
        var s = new NameplateStacker(10f);
        var r = new Vector4(10f, 16f, 40f, 10f);
        s.Begin();
        s.Add(1, r, 1f, true);
        s.Add(2, r, 2f, false);
        s.Resolve(0f);
        Assert.Equal(new Vector4(10f, 10f, 40f, 10f), s.RectOf(2));
        Assert.Equal(2f, NameplateStacker.Gap);
        Assert.Equal(0.5f, NameplateStacker.RankBand);
        Assert.Equal(0.12f, NameplateStacker.TimeConstant);
    }

    [Fact]
    public void GeneratedSetsWithRoomHaveNoOverlap()
    {
        for (int seed = 0; seed < 8; seed++)
        {
            var random = new Random(seed);
            var naturalRects = new Vector4[16];
            var s = new NameplateStacker();
            s.Begin();
            for (int id = 0; id < naturalRects.Length; id++)
            {
                var natural = new Vector4(random.Next(0, 5) * 20, 600 + random.Next(0, 80),
                    20 + random.Next(0, 40), 8 + random.Next(0, 8));
                naturalRects[id] = natural;
                s.Add(id, natural, id, false);
            }

            s.Resolve(0f);
            for (int id = 0; id < naturalRects.Length; id++)
            {
                Vector4 natural = naturalRects[id];
                Vector4 actual = s.RectOf(id);
                Assert.Equal(natural.X, actual.X);
                Assert.Equal(natural.Z, actual.Z);
                Assert.Equal(natural.W, actual.W);
                Assert.InRange(actual.Y, 4f, natural.Y);
                for (int otherId = 0; otherId < id; otherId++)
                {
                    Vector4 a = actual;
                    Vector4 b = s.RectOf(otherId);
                    Assert.True(a.X + a.Z + 2f <= b.X || b.X + b.Z + 2f <= a.X ||
                        a.Y + a.W + 2f <= b.Y || b.Y + b.W + 2f <= a.Y);
                }
            }
        }
    }

    private static NameplateStacker Frame(params (long Id, Vector4 Rect, float Rank, bool Pinned)[] plates)
    {
        var s = new NameplateStacker();
        s.Begin();
        foreach (var plate in plates)
            s.Add(plate.Id, plate.Rect, plate.Rank, plate.Pinned);
        s.Resolve(0f);
        return s;
    }
}
