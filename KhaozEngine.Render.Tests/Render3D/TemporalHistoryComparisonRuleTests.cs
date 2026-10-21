using System;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class TemporalHistoryComparisonRuleTests
{
    [Theory]
    [InlineData(0x0000, 0x8000, 0)]
    [InlineData(0x0000, 0x0001, 1)]
    [InlineData(0x8001, 0x0001, 2)]
    [InlineData(0x3bff, 0x3c01, 2)]
    [InlineData(0xbc01, 0xbbff, 2)]
    public void HalfStepDistanceCrossesZeroAndBinades(int a, int b, int steps)
        => Assert.Equal(steps, TemporalHistoryDifference.HalfSteps(Half(a), Half(b)));

    [Fact]
    public void EveryArithmeticChannelHasTheSameFixedTwoStepBound()
    {
        Assert.True(TemporalNvidiaComparisonRule.Allows(Difference()));
        Assert.True(TemporalNvidiaComparisonRule.Allows(Difference(red: Half(0x3802), green: Half(0x37fe),
            confidence: Half(0x3802))));
        Assert.False(TemporalNvidiaComparisonRule.Allows(Difference(red: Half(0x3803))));
        Assert.False(TemporalNvidiaComparisonRule.Allows(Difference(green: Half(0x37fd))));
        Assert.False(TemporalNvidiaComparisonRule.Allows(Difference(confidence: Half(0x3803))));
        foreach (int channel in new[] { 2, 3 })
        {
            float[] a = [0.5f, 0.5f, 0.5f, 0.5f], b = (float[])a.Clone(), state = [0.5f, 1f];
            b[channel] = Half(0x3802);
            Assert.True(TemporalNvidiaComparisonRule.Allows(TemporalHistoryDifference.Measure(a, state, b, state)));
            b[channel] = Half(0x3803);
            Assert.False(TemporalNvidiaComparisonRule.Allows(TemporalHistoryDifference.Measure(a, state, b, state)));
        }
    }

    [Fact]
    public void StoredLockBitsAreExactEvenWhenHalfStepDistanceIsZero()
    {
        Assert.False(TemporalNvidiaComparisonRule.Allows(Difference(locked: Half(0x3c01))));
        Assert.False(TemporalNvidiaComparisonRule.Allows(Difference(locked: -1f)));
        float[] colour = [0f, 0f, 0f, 1f];
        Assert.False(TemporalNvidiaComparisonRule.Allows(TemporalHistoryDifference.Measure(colour, [0.5f, 0f],
            colour, [0.5f, -0f])));
    }

    [Fact]
    public void NonFiniteArithmeticDifferencesAreNeverAllowed()
    {
        Assert.False(TemporalNvidiaComparisonRule.Allows(Difference(red: float.NaN)));
        Assert.False(TemporalNvidiaComparisonRule.Allows(Difference(confidence: float.PositiveInfinity)));
    }

    static float Half(int bits) => (float)BitConverter.Int16BitsToHalf((short)bits);

    static TemporalHistoryDifference Difference(float red = 0.5f, float green = 0.5f,
        float confidence = 0.5f, float locked = 1f)
        => TemporalHistoryDifference.Measure([0.5f, 0.5f, 0.5f, 1f], [0.5f, 1f],
            [red, green, 0.5f, 1f], [confidence, locked]);
}
