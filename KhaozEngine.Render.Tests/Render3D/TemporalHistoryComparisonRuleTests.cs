using System;
using System.Collections.Generic;
using KhaozEngine.Tests.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Render3D;

// States are one texel of the four-channel history state: confidence, stored lock, drift and swing.
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

    // Hardware NVIDIA Direct3D 11 reports finite colour and confidence differences against D and never asserts them.
    [Fact]
    public void FiniteColourAndConfidenceDifferencesAreReportedNotAsserted()
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
            float[] a = [0.5f, 0.5f, 0.5f, 0.5f], b = (float[])a.Clone(), state = [0.5f, 1f, 0f, 0f];
            b[channel] = Half(0x3900);
            Assert.True(TemporalNvidiaComparisonRule.Allows(TemporalHistoryDifference.Measure(a, state, b, state)));
        }
        Assert.True(TemporalNvidiaComparisonRule.ClassesMatch(far));
        Assert.Equal(0, far.Confidence.ClassMismatches);
    }

    // Every colour and confidence value keeps D's non-finite class: NaN, positive infinity, negative infinity or
    // finite. A class that differs fails both the A/B rule and entry identity's class rule.
    [Theory]
    [InlineData(float.NaN, 0.5f)]
    [InlineData(0.5f, float.NaN)]
    [InlineData(float.PositiveInfinity, 0.5f)]
    [InlineData(float.NegativeInfinity, 0.5f)]
    [InlineData(float.PositiveInfinity, float.NegativeInfinity)]
    [InlineData(float.NaN, float.PositiveInfinity)]
    public void NonFiniteClassDifferencesFail(float production, float d)
    {
        foreach (TemporalHistoryDifference difference in new[]
        {
            Difference(red: production, redD: d), Difference(green: production, greenD: d),
            Difference(confidence: production, confidenceD: d),
        })
        {
            Assert.False(TemporalNvidiaComparisonRule.ClassesMatch(difference));
            Assert.False(TemporalNvidiaComparisonRule.Allows(difference));
            Assert.Contains("non-finite class mismatches", difference.ToString(), StringComparison.Ordinal);
        }
        float[] colour = [0.5f, 0.5f, 0.5f, 0.5f], state = [0.5f, 1f, 0f, 0f];
        foreach (int channel in new[] { 2, 3 })
        {
            float[] a = (float[])colour.Clone(), b = (float[])colour.Clone();
            a[channel] = production;
            b[channel] = d;
            Assert.False(TemporalNvidiaComparisonRule.ClassesMatch(TemporalHistoryDifference.Measure(a, state, b, state)));
        }
    }

    // A NaN where D is finite fails the oracle's entry identity comparison too, and a finite difference only prints.
    [Fact]
    public void EntryIdentityAssertsTheClassAndReportsFiniteDifferences()
    {
        var lines = new List<string>();
        var oracle = new TemporalNvidiaHistoryOracle(new Lines(lines));
        TemporalHistorySample d = Sample(0.5f, 0.5f), finite = Sample(Half(0x3820), Half(0x3820));
        oracle.Report(0, "finite", finite, d);
        Assert.Contains(lines, l => l.Contains("32 half steps", StringComparison.Ordinal));
        Assert.ThrowsAny<Exception>(() => oracle.Report(1, "NaN colour", Sample(float.NaN, 0.5f), d));
        Assert.ThrowsAny<Exception>(() => oracle.Report(2, "infinite confidence", Sample(0.5f, float.PositiveInfinity), d));
        Assert.ThrowsAny<Exception>(() => oracle.Compare(3, "NaN colour", Sample(float.NaN, 0.5f), d));
        oracle.Compare(4, "finite", finite, d);
    }

    // Matching non-finite classes are not a class failure, whatever their payload or sign bits.
    [Fact]
    public void MatchingNonFiniteClassesPass()
    {
        float otherNaN = BitConverter.Int32BitsToSingle(unchecked((int)0xFFC00001));
        TemporalHistoryDifference nan = Difference(red: otherNaN, redD: float.NaN, confidence: float.PositiveInfinity,
            confidenceD: float.PositiveInfinity);
        Assert.True(TemporalNvidiaComparisonRule.Allows(nan));
        Assert.Equal(1, nan.Colour[0].Count);
        Assert.Contains("non-finite difference", nan.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("class mismatches", nan.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void StoredLockBitsAreExactEvenWhenHalfStepDistanceIsZero()
    {
        Assert.False(TemporalNvidiaComparisonRule.Allows(Difference(locked: Half(0x3c01))));
        Assert.False(TemporalNvidiaComparisonRule.Allows(Difference(locked: -1f)));
        float[] colour = [0f, 0f, 0f, 1f];
        Assert.False(TemporalNvidiaComparisonRule.Allows(TemporalHistoryDifference.Measure(colour, [0.5f, 0f, 0f, 0f],
            colour, [0.5f, -0f, 0f, 0f])));
    }

    // A software Vulkan device holds the entries' colour within amendment 23's bound, inclusive, and its share. A
    // non-finite worst or an empty comparison never passes.
    [Fact]
    public void SoftwareVulkanColourHoldsTheBoundAndTheShare()
    {
        const float bound = TemporalSoftwareCompileDivergenceRule.AbsoluteBound;
        Assert.Equal(0.0625f, bound);
        Assert.Equal(0.0025f, TemporalSoftwareCompileDivergenceRule.DifferingShareBound);
        Assert.True(TemporalSoftwareCompileDivergenceRule.Allows(0f, 0, 1));
        Assert.True(TemporalSoftwareCompileDivergenceRule.Allows(0.0439f, 118, 100_000));
        Assert.True(TemporalSoftwareCompileDivergenceRule.Allows(bound, 24, 10_000));
        Assert.False(TemporalSoftwareCompileDivergenceRule.Allows(BitConverter.Int32BitsToSingle(
            BitConverter.SingleToInt32Bits(bound) + 1), 1, 10_000));
        Assert.False(TemporalSoftwareCompileDivergenceRule.Allows(0.001f, 26, 10_000));
        Assert.False(TemporalSoftwareCompileDivergenceRule.Allows(float.NaN, 1, 10_000));
        Assert.False(TemporalSoftwareCompileDivergenceRule.Allows(float.PositiveInfinity, 1, 10_000));
        Assert.False(TemporalSoftwareCompileDivergenceRule.Allows(0f, 0, 0));
        Assert.Equal(0.0025, TemporalSoftwareCompileDivergenceRule.ShareOf(25, 10_000), 12);
    }

    static float Half(int bits) => (float)BitConverter.Int16BitsToHalf((short)bits);

    static TemporalHistoryDifference Difference(float red = 0.5f, float green = 0.5f,
        float confidence = 0.5f, float locked = 1f, float redD = 0.5f, float greenD = 0.5f, float confidenceD = 0.5f)
        => TemporalHistoryDifference.Measure([redD, greenD, 0.5f, 1f], [confidenceD, 1f, 0f, 0f],
            [red, green, 0.5f, 1f], [confidence, locked, 0f, 0f]);

    static TemporalHistorySample Sample(float red, float confidence)
        => new([red, 0.5f, 0.5f, 1f], [confidence, 1f, 0f, 0f], [7], [0.25f]);

    sealed class Lines(List<string> lines) : ITestOutputHelper
    {
        public void WriteLine(string message) => lines.Add(message);
        public void WriteLine(string format, params object[] args) => lines.Add(string.Format(format, args));
    }
}
