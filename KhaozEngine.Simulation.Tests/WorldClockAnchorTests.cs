using System;
using System.Buffers.Binary;
using KhaozEngine.Simulation;
using Xunit;

namespace KhaozEngine.Tests.Simulation;

public class WorldClockAnchorTests
{
    [Fact]
    public void EncodeDecode_MatchesVersionOneGoldenVector()
    {
        WorldClockAnchor anchor = new(1d, 0.75f);
        byte[] expected =
        [
            0x4b, 0x57, 0x43, 0x41,
            0x01, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xf0, 0x3f,
            0x00, 0x00, 0x40, 0x3f,
        ];

        byte[] encoded = anchor.Encode();
        WorldClockAnchor? decoded = WorldClockAnchor.Decode(expected);

        Assert.Equal(expected, encoded);
        Assert.Equal(anchor, decoded);
    }

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

    [Theory]
    [InlineData(0, 0)] // invalid magic
    [InlineData(4, 2)] // unknown version
    public void Decode_UnknownHeaderReturnsNull(int offset, int value)
    {
        byte[] encoded = new WorldClockAnchor(1_000d, 0.75f).Encode();
        BinaryPrimitives.WriteInt32LittleEndian(encoded.AsSpan(offset, 4), value);

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

    [Theory]
    [InlineData(-0.25f)]
    [InlineData(1f)]
    [InlineData(1.25f)]
    public void Decode_OutOfRangeTimeOfDayReturnsNull(float corruptTimeOfDay)
    {
        byte[] encoded = new WorldClockAnchor(1_000d, corruptTimeOfDay).Encode();

        Assert.Null(WorldClockAnchor.Decode(encoded));
    }

    [Theory]
    [InlineData(1_000d, 0.75f, 1_150d, 0f)]
    [InlineData(0d, 0.125f, 6_000_000_075d, 0.25f)] // wrap before narrowing to float
    [InlineData(1_000d, 0.75f, 900d, 0.75f)] // a backward wall clock cannot rewind
    public void RecomputeTimeOfDay_HandlesElapsedWallTime(double anchorTime, float timeOfDay, double now, float expected)
    {
        WorldClockAnchor anchor = new(anchorTime, timeOfDay);

        float result = anchor.RecomputeTimeOfDay(now, 600f);

        Assert.Equal(expected, result, 4);
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
