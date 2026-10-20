using System;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class TemporalHistoryEnvelopeTests
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
    public void OnlyTheMeasuredChannelEnvelopeIsAllowed()
    {
        var envelope = new TemporalHistoryEnvelope();
        envelope.Include(Difference(red: Half(0x3802), confidence: Half(0x3801)));
        Assert.True(envelope.Allows(Difference(red: Half(0x3802), confidence: Half(0x3801))));
        Assert.False(envelope.Allows(Difference(red: Half(0x3803))));
        Assert.False(envelope.Allows(Difference(green: Half(0x3801))));
        Assert.False(envelope.Allows(Difference(confidence: Half(0x3802))));
    }

    [Fact]
    public void AStoredLockDifferenceIsNeverAllowed()
    {
        var envelope = new TemporalHistoryEnvelope();
        envelope.Include(Difference(red: 1f, confidence: 1f));
        Assert.False(envelope.Allows(Difference(locked: Half(0x3c01))));
        Assert.False(envelope.Allows(Difference(locked: -1f)));
    }

    [Fact]
    public void ZeroEnvelopeRequiresExactBitsIncludingSignedZero()
    {
        var envelope = new TemporalHistoryEnvelope();
        Assert.True(envelope.Allows(Difference()));
        Assert.False(envelope.Allows(Difference(red: Half(0x3801))));
        float[] a = [0f, 0f, 0f, 1f], b = [-0f, 0f, 0f, 1f], state = [0.5f, 1f];
        Assert.False(envelope.Allows(TemporalHistoryDifference.Measure(a, state, b, state)));
    }

    [Fact]
    public void TheEnvelopeUsesTheMaximumAcrossFramesWithoutAllowingNonFiniteDifferences()
    {
        var envelope = new TemporalHistoryEnvelope();
        envelope.Include(Difference(red: Half(0x3801)));
        envelope.Include(Difference(red: Half(0x3803)));
        envelope.Include(Difference());
        Assert.True(envelope.Allows(Difference(red: Half(0x3803))));
        Assert.False(envelope.Allows(Difference(red: Half(0x3804))));
        Assert.False(envelope.Allows(Difference(red: float.NaN)));
        Assert.False(envelope.Allows(Difference(confidence: float.PositiveInfinity)));
    }

    static float Half(int bits) => (float)BitConverter.Int16BitsToHalf((short)bits);

    static TemporalHistoryDifference Difference(float red = 0.5f, float green = 0.5f,
        float confidence = 0.5f, float locked = 1f)
        => TemporalHistoryDifference.Measure([0.5f, 0.5f, 0.5f, 1f], [0.5f, 1f],
            [red, green, 0.5f, 1f], [confidence, locked]);
}
