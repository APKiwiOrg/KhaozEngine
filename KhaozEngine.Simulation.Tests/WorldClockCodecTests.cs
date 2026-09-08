using System;
using System.Buffers.Binary;
using KhaozEngine.Simulation;
using Xunit;

namespace KhaozEngine.Tests.Simulation;

public class WorldClockCodecTests
{
    [Fact]
    public void EncodeState_WritesExactLittleEndianPayload()
    {
        WorldClockState state = new(0.5f, 1800f, 10f);

        byte[] encoded = WorldClockCodec.EncodeState(state);

        Assert.Equal(new byte[]
        {
            0x00, 0x00, 0x00, 0x3f,
            0x00, 0x00, 0xe1, 0x44,
            0x00, 0x00, 0x20, 0x41,
        }, encoded);
    }

    [Fact]
    public void TryDecodeState_ValidPayloadRoundTripsAllFields()
    {
        WorldClockState state = new(0.6f, 1800f, 10f);
        byte[] encoded = WorldClockCodec.EncodeState(state);

        bool decodedSuccessfully = WorldClockCodec.TryDecodeState(encoded, out WorldClockState decoded);

        Assert.True(decodedSuccessfully);
        Assert.Equal(state, decoded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(13)]
    public void TryDecodeState_NonExactPayloadLengthReturnsFalseAndDefault(int length)
    {
        byte[] encoded = new byte[length];
        WorldClockState decoded = new(0.5f, 300f, 2f);

        bool decodedSuccessfully = WorldClockCodec.TryDecodeState(encoded, out decoded);

        Assert.False(decodedSuccessfully);
        Assert.Equal(default, decoded);
    }

    [Theory]
    [InlineData(0, float.NaN)]
    [InlineData(0, float.PositiveInfinity)]
    [InlineData(0, float.NegativeInfinity)]
    [InlineData(4, float.NaN)]
    [InlineData(4, float.PositiveInfinity)]
    [InlineData(4, float.NegativeInfinity)]
    [InlineData(8, float.NaN)]
    [InlineData(8, float.PositiveInfinity)]
    [InlineData(8, float.NegativeInfinity)]
    public void TryDecodeState_NonFiniteFieldReturnsFalseAndDefault(int offset, float invalidValue)
    {
        byte[] encoded = WorldClockCodec.EncodeState(new WorldClockState(0.5f, 300f, 2f));
        BinaryPrimitives.WriteSingleLittleEndian(encoded.AsSpan(offset, sizeof(float)), invalidValue);
        WorldClockState decoded = new(0.5f, 300f, 2f);

        bool decodedSuccessfully = WorldClockCodec.TryDecodeState(encoded, out decoded);

        Assert.False(decodedSuccessfully);
        Assert.Equal(default, decoded);
    }

    [Theory]
    [InlineData(-0.25f)]
    [InlineData(1f)]
    [InlineData(1.25f)]
    public void TryDecodeState_TimeOfDayOutsideNormalizedRangeReturnsFalseAndDefault(float invalidTimeOfDay)
    {
        byte[] encoded = WorldClockCodec.EncodeState(new WorldClockState(invalidTimeOfDay, 300f, 2f));
        WorldClockState decoded = new(0.5f, 300f, 2f);

        bool decodedSuccessfully = WorldClockCodec.TryDecodeState(encoded, out decoded);

        Assert.False(decodedSuccessfully);
        Assert.Equal(default, decoded);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    public void TryDecodeState_NonPositiveDayLengthReturnsFalseAndDefault(float invalidDayLength)
    {
        byte[] encoded = WorldClockCodec.EncodeState(new WorldClockState(0.5f, invalidDayLength, 2f));
        WorldClockState decoded = new(0.5f, 300f, 2f);

        bool decodedSuccessfully = WorldClockCodec.TryDecodeState(encoded, out decoded);

        Assert.False(decodedSuccessfully);
        Assert.Equal(default, decoded);
    }

    [Fact]
    public void TryDecodeState_NegativeTimeScaleReturnsFalseAndDefault()
    {
        byte[] encoded = WorldClockCodec.EncodeState(new WorldClockState(0.5f, 300f, -1f));
        WorldClockState decoded = new(0.5f, 300f, 2f);

        bool decodedSuccessfully = WorldClockCodec.TryDecodeState(encoded, out decoded);

        Assert.False(decodedSuccessfully);
        Assert.Equal(default, decoded);
    }

    [Fact]
    public void EncodeCommand_WritesExactLittleEndianPayload()
    {
        WorldClockCommand command = new(WorldClockCommandKind.SetDayLength, 900f);

        byte[] encoded = WorldClockCodec.EncodeCommand(command);

        Assert.Equal(new byte[] { 0x02, 0x00, 0x00, 0x61, 0x44 }, encoded);
    }

    [Theory]
    [InlineData(WorldClockCommandKind.SetTimeOfDay, 0.75f)]
    [InlineData(WorldClockCommandKind.SetTimeScale, 0f)]
    [InlineData(WorldClockCommandKind.SetTimeScale, -1f)]
    [InlineData(WorldClockCommandKind.SetDayLength, 900f)]
    [InlineData(WorldClockCommandKind.SetDayLength, 0f)]
    [InlineData(WorldClockCommandKind.SetDayLength, -1f)]
    public void TryDecodeCommand_ValidPayloadRoundTripsAllFields(WorldClockCommandKind kind, float value)
    {
        WorldClockCommand command = new(kind, value);
        byte[] encoded = WorldClockCodec.EncodeCommand(command);

        bool decodedSuccessfully = WorldClockCodec.TryDecodeCommand(encoded, out WorldClockCommand decoded);

        Assert.True(decodedSuccessfully);
        Assert.Equal(command, decoded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(6)]
    public void TryDecodeCommand_NonExactPayloadLengthReturnsFalseAndDefault(int length)
    {
        byte[] encoded = new byte[length];
        WorldClockCommand decoded = new(WorldClockCommandKind.SetTimeScale, 2f);

        bool decodedSuccessfully = WorldClockCodec.TryDecodeCommand(encoded, out decoded);

        Assert.False(decodedSuccessfully);
        Assert.Equal(default, decoded);
    }

    [Fact]
    public void TryDecodeCommand_UnknownKindReturnsFalseAndDefault()
    {
        byte[] encoded = WorldClockCodec.EncodeCommand(new WorldClockCommand(WorldClockCommandKind.SetTimeOfDay, 0.5f));
        encoded[0] = 3;
        WorldClockCommand decoded = new(WorldClockCommandKind.SetTimeScale, 2f);

        bool decodedSuccessfully = WorldClockCodec.TryDecodeCommand(encoded, out decoded);

        Assert.False(decodedSuccessfully);
        Assert.Equal(default, decoded);
    }

    [Theory]
    [InlineData(WorldClockCommandKind.SetTimeOfDay, float.NaN)]
    [InlineData(WorldClockCommandKind.SetTimeScale, float.PositiveInfinity)]
    [InlineData(WorldClockCommandKind.SetDayLength, float.NegativeInfinity)]
    public void TryDecodeCommand_NonFiniteValueReturnsFalseAndDefault(WorldClockCommandKind kind, float invalidValue)
    {
        byte[] encoded = WorldClockCodec.EncodeCommand(new WorldClockCommand(kind, invalidValue));
        WorldClockCommand decoded = new(WorldClockCommandKind.SetTimeScale, 2f);

        bool decodedSuccessfully = WorldClockCodec.TryDecodeCommand(encoded, out decoded);

        Assert.False(decodedSuccessfully);
        Assert.Equal(default, decoded);
    }

}
