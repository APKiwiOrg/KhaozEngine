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

    [Fact]
    public void MeasuredSoftwareRecordVariationKeepsItsOwnBound()
    {
        // The precision-floor walk's worst pairs, in sparse populations below its measured differing shares.
        float[] a = new float[1000 * 4], b = new float[1000 * 4];
        for (int i = 0; i < 8; i++)
        {
            a[i * 4 + 2] = -0.00056409836f;
            b[i * 4 + 2] = 0.00024414062f;
        }
        for (int i = 0; i < 4; i++)
        {
            a[i * 4 + 3] = 0.0045204163f;
            b[i * 4 + 3] = 0.0059051514f;
        }
        var comparison = new TemporalEntryStateComparison(softwareVulkan: true);
        comparison.Compare(47, "measured llvmpipe witness", a, b);
        Assert.Equal(7327, comparison.Drift.WorstHalfSteps);
        Assert.Equal(363, comparison.Swing.WorstHalfSteps);
        Assert.Equal(0.008, comparison.Drift.Share, 12);
        Assert.Equal(0.004, comparison.Swing.Share, 12);
        comparison.AssertWithinBound("measured llvmpipe witness");
    }

    [Theory]
    [InlineData(true, 102, 9100)]
    [InlineData(false, 51, 500)]
    public void CalibratedRecordBoundsAcceptTheirLimitAndRejectTheNextSampleOrStep(bool drift, long differing,
        int steps)
    {
        int stepBound = drift ? TemporalSoftwareRecordDivergenceRule.DriftHalfStepBound
            : TemporalSoftwareRecordDivergenceRule.SwingHalfStepBound;
        double shareBound = drift ? TemporalSoftwareRecordDivergenceRule.DriftDifferingShareBound
            : TemporalSoftwareRecordDivergenceRule.SwingDifferingShareBound;
        Assert.True(TemporalSoftwareRecordDivergenceRule.Allows(steps, differing, 10_000, stepBound, shareBound));
        Assert.False(TemporalSoftwareRecordDivergenceRule.Allows(steps, differing + 1, 10_000, stepBound, shareBound));
        Assert.False(TemporalSoftwareRecordDivergenceRule.Allows(steps + 1, differing, 10_000, stepBound, shareBound));
    }

    static float Half(int bits) => (float)BitConverter.UInt16BitsToHalf((ushort)bits);
}
