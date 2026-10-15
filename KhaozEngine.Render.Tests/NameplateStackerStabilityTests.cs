using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests;

[Collection("AllocSensitive")]
public sealed class NameplateStackerStabilityTests
{
    [Fact]
    public void NearEqualRanksKeepOrderUntilTheStrictBandIsCrossed()
    {
        var s = new NameplateStacker();
        OrderFrame(s, 1f, 2f);
        OrderFrame(s, 2f, 1.5f);
        Assert.Equal(new long[] { 2, 1 }, s.DrawOrder.ToArray());
        OrderFrame(s, 2f, 1.499f);
        Assert.Equal(new long[] { 1, 2 }, s.DrawOrder.ToArray());
        OrderFrame(s, 1.4f, 1.499f);
        Assert.Equal(new long[] { 1, 2 }, s.DrawOrder.ToArray());
    }

    [Fact]
    public void EqualRankNewcomersUseIdAndDoNotResetSurvivorOrder()
    {
        var s = new NameplateStacker();
        OrderFrame(s, 2f, 1f);
        s.Begin();
        s.Add(1, new Vector4(0f, 100f, 10f, 10f), 1f, false);
        s.Add(2, new Vector4(100f, 100f, 10f, 10f), 1f, false);
        s.Add(3, new Vector4(200f, 100f, 10f, 10f), 1f, false);
        s.Resolve(0f);
        Assert.Equal(new long[] { 3, 1, 2 }, s.DrawOrder.ToArray());
    }

    [Fact]
    public void ThreeRanksDoNotUseANonTransitiveSortComparer()
    {
        var s = new NameplateStacker();
        s.Begin();
        s.Add(1, new Vector4(0f, 100f, 10f, 10f), 1f, false);
        s.Add(2, new Vector4(100f, 100f, 10f, 10f), 2f, false);
        s.Add(3, new Vector4(200f, 100f, 10f, 10f), 3f, false);
        s.Resolve(0f);
        s.Begin();
        s.Add(1, new Vector4(0f, 100f, 10f, 10f), 3f, false);
        s.Add(2, new Vector4(100f, 100f, 10f, 10f), 1f, false);
        s.Add(3, new Vector4(200f, 100f, 10f, 10f), 2f, false);
        s.Resolve(0f);
        Assert.Equal(new long[] { 1, 3, 2 }, s.DrawOrder.ToArray());
    }

    [Fact]
    public void OffsetsEaseWithTheSpecifiedTimeConstantAndConverge()
    {
        var s = PrepareRelease();
        ReleaseFrame(s, 0.12f);
        Assert.Equal(100f - 12f * MathF.Exp(-1f), s.RectOf(2).Y, 4);
        ReleaseFrame(s, 1.08f);
        Assert.InRange(100f - s.RectOf(2).Y, 0f, 0.001f);
    }

    [Fact]
    public void EasingIsFrameRateIndependent()
    {
        var one = PrepareRelease();
        var two = PrepareRelease();
        ReleaseFrame(one, 0.12f);
        ReleaseFrame(two, 0.06f);
        ReleaseFrame(two, 0.06f);
        Assert.Equal(one.RectOf(2).Y, two.RectOf(2).Y, 4);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void InvalidDtDoesNotAdvanceOffsets(float dt)
    {
        var s = PrepareRelease();
        ReleaseFrame(s, dt);
        Assert.Equal(88f, s.RectOf(2).Y);
        s.Begin();
        s.Add(2, new Vector4(10f, 100f, 40f, 10f), 2f, true);
        s.Resolve(dt);
        Assert.Equal(100f, s.RectOf(2).Y);
        s = new NameplateStacker();
        s.Begin();
        s.Add(1, new Vector4(10f, 100f, 40f, 10f), 1f, true);
        s.Add(2, new Vector4(10f, 100f, 40f, 10f), 2f, false);
        s.Resolve(dt);
        Assert.Equal(88f, s.RectOf(2).Y);
    }

    [Fact]
    public void ReturningIdsStartSolvedAndPinningResetsAnOldOffsetImmediately()
    {
        var s = PrepareRelease();
        s.Begin();
        s.Resolve(0f);
        s.Begin();
        s.Add(2, new Vector4(10f, 100f, 40f, 10f), 2f, false);
        s.Resolve(0f);
        Assert.Equal(100f, s.RectOf(2).Y);
        s = PrepareRelease();
        s.Begin();
        s.Add(2, new Vector4(10f, 100f, 40f, 10f), 2f, true);
        s.Resolve(0f);
        Assert.Equal(100f, s.RectOf(2).Y);
    }

    [Fact]
    public void AWarmedFrameWithChurnAllocatesNothing()
    {
        var s = new NameplateStacker();
        s.Begin();
        for (int id = 0; id < 64; id++)
            s.Add(id, new Vector4(id * 20f, 100f, 10f, 10f), id, false);
        s.Resolve(0.016f);
        s.Begin();
        for (int id = 64; id < 128; id++)
            s.Add(id, new Vector4(id * 20f, 100f, 10f, 10f), id, false);
        s.Resolve(0.016f);
        s.Begin();
        for (int id = 64; id < 128; id++)
            s.Add(id, new Vector4(id * 20f, 100f, 10f, 10f), id, false);
        s.Resolve(0.016f);
        GC.GetAllocatedBytesForCurrentThread();

        int count = 0;
        long widthSum = 0;
        long before, after;
        using (AllocationRegion.Start("a warm nameplate stacker frame with churn"))
        {
            before = GC.GetAllocatedBytesForCurrentThread();
            s.Begin();
            for (int id = 96; id < 160; id++)
                s.Add(id, new Vector4(id * 20f, 100f, 10f, 10f), id, false);
            s.Resolve(0.016f);
            for (int i = 0; i < s.DrawOrder.Length; i++)
            {
                long id = s.DrawOrder[i];
                count++;
                widthSum += (long)s.RectOf(id).Z;
            }

            after = GC.GetAllocatedBytesForCurrentThread();
        }

        Assert.Equal(64, count);
        Assert.Equal(64L * 10L, widthSum);
        Assert.Equal(before, after);
    }

    private static void OrderFrame(NameplateStacker s, float firstRank, float secondRank)
    {
        s.Begin();
        s.Add(1, new Vector4(0f, 100f, 10f, 10f), firstRank, false);
        s.Add(2, new Vector4(100f, 100f, 10f, 10f), secondRank, false);
        s.Resolve(0f);
    }

    private static NameplateStacker PrepareRelease()
    {
        var s = new NameplateStacker();
        s.Begin();
        s.Add(1, new Vector4(10f, 100f, 40f, 10f), 1f, true);
        s.Add(2, new Vector4(10f, 100f, 40f, 10f), 2f, false);
        s.Resolve(0f);
        Assert.Equal(88f, s.RectOf(2).Y);
        return s;
    }

    private static void ReleaseFrame(NameplateStacker s, float dt)
    {
        s.Begin();
        s.Add(2, new Vector4(10f, 100f, 40f, 10f), 2f, false);
        s.Resolve(dt);
    }
}
