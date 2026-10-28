using System;
using KhaozEngine.Tests.Gpu;
using Xunit;
using Xunit.Sdk;

namespace KhaozEngine.Tests.Render3D;

public sealed class TemporalSoftwareRecordDivergenceTests
{
    [Theory]
    [InlineData(0, 0, 10, true)]
    [InlineData(1, 1, 10, true)]
    [InlineData(2, 1, 10, true)]
    [InlineData(3, 1, 10, false)]
    [InlineData(1, 2, 10, false)]
    [InlineData(0, 0, 0, false)]
    [InlineData(int.MaxValue, 1, 10, false)]
    public void RecordBoundRequiresBothHalfStepsAndDifferingShare(int steps, long differing, long compared, bool allowed)
        => Assert.Equal(allowed, TemporalSoftwareRecordDivergenceRule.Allows(steps, differing, compared, 2, 0.1));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void SoftwareVulkanKeepsConfidenceAndStoredLockBitExact(int channel)
    {
        float[] a = [0f, 0f, 0.5f, 0.5f], b = (float[])a.Clone();
        b[channel] = -0f;
        var comparison = new TemporalEntryStateComparison(softwareVulkan: true);
        Assert.ThrowsAny<XunitException>(() => comparison.Compare(1, "probe", a, b));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void OtherDevicesKeepTheRecordBitExact(int channel)
    {
        float[] a = [0.5f, 0.875f, 0.5f, 0.5f], b = (float[])a.Clone();
        b[channel] = Half(0x3801);
        var comparison = new TemporalEntryStateComparison(softwareVulkan: false);
        Assert.ThrowsAny<XunitException>(() => comparison.Compare(1, "probe", a, b));
    }

    [Fact]
    public void SoftwareRecordMeasuresEachChannelInHalfStepsAndSamples()
    {
        var comparison = new TemporalEntryStateComparison(softwareVulkan: true);
        comparison.Compare(1, "probe", [0.5f, 0.875f, Half(0x3c00), Half(0x3800)],
            [0.5f, 0.875f, Half(0x3c02), Half(0x3801)]);
        comparison.Compare(2, "probe", [0.5f, 0.875f, 0f, 0f], [0.5f, 0.875f, 0f, 0f]);
        Assert.Equal(2, comparison.Drift.WorstHalfSteps);
        Assert.Equal(1, comparison.Swing.WorstHalfSteps);
        Assert.Equal(1, comparison.Drift.Differing);
        Assert.Equal(1, comparison.Swing.Differing);
        Assert.Equal(2, comparison.Drift.Compared);
        Assert.Equal(2, comparison.Swing.Compared);
    }

    static float Half(int bits) => (float)BitConverter.UInt16BitsToHalf((ushort)bits);
}
