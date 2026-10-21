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

    // Hardware NVIDIA Direct3D 11 reports colour and confidence differences against D and never asserts them.
    [Fact]
    public void ColourAndConfidenceDifferencesAreReportedNotAsserted()
    {
        Assert.True(TemporalNvidiaComparisonRule.Allows(Difference()));
        TemporalHistoryDifference far = Difference(red: Half(0x3820), green: Half(0x37e0), confidence: Half(0x3820));
        Assert.True(TemporalNvidiaComparisonRule.Allows(far));
        Assert.Equal(1, far.Colour[0].Count);
        Assert.Equal(32, far.Colour[0].MaxSteps);
        Assert.Equal(32, far.Colour[1].MaxSteps);
        Assert.Equal(32, far.Confidence.MaxSteps);
        foreach (int channel in new[] { 2, 3 })
        {
            float[] a = [0.5f, 0.5f, 0.5f, 0.5f], b = (float[])a.Clone(), state = [0.5f, 1f];
            b[channel] = Half(0x3900);
            Assert.True(TemporalNvidiaComparisonRule.Allows(TemporalHistoryDifference.Measure(a, state, b, state)));
        }
        TemporalHistoryDifference nonFinite = Difference(red: float.NaN, confidence: float.PositiveInfinity);
        Assert.True(TemporalNvidiaComparisonRule.Allows(nonFinite));
        Assert.Contains("non-finite difference", nonFinite.ToString(), StringComparison.Ordinal);
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

    static float Half(int bits) => (float)BitConverter.Int16BitsToHalf((short)bits);

    static TemporalHistoryDifference Difference(float red = 0.5f, float green = 0.5f,
        float confidence = 0.5f, float locked = 1f)
        => TemporalHistoryDifference.Measure([0.5f, 0.5f, 0.5f, 1f], [0.5f, 1f],
            [red, green, 0.5f, 1f], [confidence, locked]);
}
