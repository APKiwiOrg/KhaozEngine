using System;
using System.Buffers.Binary;
using KhaozEngine.Simulation;
using Xunit;

namespace KhaozEngine.Tests.Simulation;

public class WorldClockAnchorTests
{
    [Fact]
    public void EncodeDecode_RoundTripsExactlyInTwentyBytes()
    {
        WorldClockAnchor anchor = new(1_753_000_000.125d, 0.6789f);

        byte[] encoded = anchor.Encode();
        WorldClockAnchor? decoded = WorldClockAnchor.Decode(encoded);

        Assert.Equal(20, encoded.Length);
        Assert.Equal(anchor, decoded);
    }

    [Fact]
    public void Decode_NullOrTruncatedPayloadReturnsNull()
    {
        byte[] encoded = new WorldClockAnchor(1_000d, 0.75f).Encode();

        Assert.Null(WorldClockAnchor.Decode(null));
        Assert.Null(WorldClockAnchor.Decode(encoded[..^1]));
    }

    [Fact]
    public void Decode_BadMagicReturnsNull()
    {
        byte[] encoded = new WorldClockAnchor(1_000d, 0.75f).Encode();
        BinaryPrimitives.WriteInt32LittleEndian(encoded.AsSpan(0, 4), 0);

        Assert.Null(WorldClockAnchor.Decode(encoded));
    }

    [Fact]
    public void Decode_UnknownVersionReturnsNull()
    {
        byte[] encoded = new WorldClockAnchor(1_000d, 0.75f).Encode();
        BinaryPrimitives.WriteInt32LittleEndian(encoded.AsSpan(4, 4), 2);

        Assert.Null(WorldClockAnchor.Decode(encoded));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Decode_NonFiniteFieldReturnsNull(bool corruptAnchorTime)
    {
        byte[] encoded = new WorldClockAnchor(1_000d, 0.75f).Encode();
        if (corruptAnchorTime)
            BinaryPrimitives.WriteDoubleLittleEndian(encoded.AsSpan(8, 8), double.NaN);
        else
            BinaryPrimitives.WriteSingleLittleEndian(encoded.AsSpan(16, 4), float.PositiveInfinity);

        Assert.Null(WorldClockAnchor.Decode(encoded));
    }

    [Fact]
    public void RecomputeTimeOfDay_AdvancesDowntimeAtScaleOne()
    {
        WorldClockAnchor anchor = new(1_000d, 0.75f);

        float result = anchor.RecomputeTimeOfDay(1_150d, 600f);

        Assert.Equal(0f, result, 4);
    }

    [Fact]
    public void RecomputeTimeOfDay_LongDowntimeWrapsBeforeNarrowingToFloat()
    {
        WorldClockAnchor anchor = new(0d, 0.125f);

        float result = anchor.RecomputeTimeOfDay(6_000_000_075d, 600f);

        Assert.Equal(0.25f, result, 4);
    }

    [Fact]
    public void RecomputeTimeOfDay_BackwardWallClockDoesNotRewind()
    {
        WorldClockAnchor anchor = new(1_000d, 0.75f);

        float result = anchor.RecomputeTimeOfDay(900d, 600f);

        Assert.Equal(0.75f, result, 4);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void RecomputeTimeOfDay_InvalidDayLengthFallsBackToWrappedAnchor(float invalidLength)
    {
        WorldClockAnchor anchor = new(1_000d, 1.75f);

        float result = anchor.RecomputeTimeOfDay(1_150d, invalidLength);

        Assert.Equal(0.75f, result, 4);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void RecomputeTimeOfDay_NonFiniteNowFallsBackToWrappedAnchor(double invalidNow)
    {
        WorldClockAnchor anchor = new(1_000d, 1.75f);

        float result = anchor.RecomputeTimeOfDay(invalidNow, 600f);

        Assert.Equal(0.75f, result, 4);
    }

    [Fact]
    public void RecomputeTimeOfDay_InvalidAnchorFallsBackSafely()
    {
        WorldClockAnchor invalidInstant = new(double.NaN, 0.75f);
        WorldClockAnchor invalidTime = new(1_000d, float.NaN);

        Assert.Equal(0.75f, invalidInstant.RecomputeTimeOfDay(1_150d, 600f), 4);
        Assert.Equal(0f, invalidTime.RecomputeTimeOfDay(1_150d, 600f), 4);
    }
}
