using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class MoveProtocolSpeedFractionTests
{
    [Theory]
    [InlineData(false, false, false, "2A000000000000000000803F000000000000")]
    [InlineData(true, true, true, "2A000000000000000000803F030000000001")]
    public void UnflaggedCommandsKeepTheirLiteralBytes(bool run, bool faceCamera, bool jump, string hex)
    {
        var legacy = new MoveCommand(Vector2.UnitY, run, 0f, jump, faceCamera);
        var explicitFalse = new MoveCommand(Vector2.UnitY, run, 0f, jump, faceCamera, false);

        Assert.Equal(Convert.FromHexString(hex), MoveProtocol.EncodeMove(42, legacy));
        Assert.Equal(Convert.FromHexString(hex), MoveProtocol.EncodeMove(42, explicitFalse));
    }

    [Theory]
    [InlineData(false, false, false, 0x00)]
    [InlineData(true, false, false, 0x01)]
    [InlineData(false, true, false, 0x02)]
    [InlineData(true, true, false, 0x03)]
    [InlineData(false, false, true, 0x04)]
    [InlineData(true, false, true, 0x05)]
    [InlineData(false, true, true, 0x06)]
    [InlineData(true, true, true, 0x07)]
    public void SpeedFractionRoundTripsWithIndependentCommandFlags(bool run, bool faceCamera, bool precise, byte flags)
    {
        var command = new MoveCommand(new Vector2(0.125f, -0.25f), run, 1.25f, true, faceCamera, precise);

        byte[] wire = MoveProtocol.EncodeMove(42, command);

        Assert.Equal(18, wire.Length);
        Assert.Equal(flags, wire[12]);
        Assert.Equal(1, wire[17]);
        Assert.True(MoveProtocol.TryDecodeMove(wire, out int seq, out MoveCommand decoded));
        Assert.Equal(42, seq);
        Assert.Equal(command.Move, decoded.Move);
        Assert.Equal(1.25f, decoded.CameraYaw);
        Assert.Equal(run, decoded.Run);
        Assert.Equal(faceCamera, decoded.FaceCamera);
        Assert.Equal(precise, decoded.ScaleSpeedByAxis);
        Assert.True(decoded.Jump);
    }

    [Theory]
    [InlineData(0x08, false)]
    [InlineData(0x10, false)]
    [InlineData(0x20, false)]
    [InlineData(0x40, false)]
    [InlineData(0x80, false)]
    [InlineData(0xF8, false)]
    [InlineData(0xFC, true)]
    [InlineData(0xFF, true)]
    public void UnknownFlagsAreIgnoredWithoutLosingPreciseIntent(byte flags, bool precise)
    {
        byte[] wire = Convert.FromHexString("2A000000000000000000803F000000000000");
        wire[12] = flags;

        Assert.True(MoveProtocol.TryDecodeMove(wire, out int seq, out MoveCommand decoded));
        Assert.Equal(42, seq);
        Assert.Equal(Vector2.UnitY, decoded.Move);
        Assert.Equal(0f, decoded.CameraYaw);
        Assert.Equal(flags == 0xFF, decoded.Run);
        Assert.Equal(flags == 0xFF, decoded.FaceCamera);
        Assert.Equal(precise, decoded.ScaleSpeedByAxis);
        Assert.False(decoded.Jump);
    }

    [Theory]
    [InlineData(float.NaN, 0f, 0f)]
    [InlineData(float.PositiveInfinity, 0f, 0f)]
    [InlineData(float.NegativeInfinity, 0f, 0f)]
    [InlineData(0f, float.NaN, 0f)]
    [InlineData(0f, float.PositiveInfinity, 0f)]
    [InlineData(0f, float.NegativeInfinity, 0f)]
    [InlineData(0f, 0f, float.NaN)]
    [InlineData(0f, 0f, float.PositiveInfinity)]
    [InlineData(0f, 0f, float.NegativeInfinity)]
    public void PreciseCommandsWithNonfiniteAxesOrYawAreRejected(float x, float y, float yaw)
    {
        byte[] wire = MoveProtocol.EncodeMove(42, new MoveCommand(new Vector2(x, y), true, yaw, true, true, true));
        wire[12] |= 0x04;

        Assert.False(MoveProtocol.TryDecodeMove(wire, out int seq, out MoveCommand decoded));
        Assert.Equal(-1, seq);
        Assert.Equal(default, decoded);
    }

    [Theory]
    [InlineData(0x000001C5)]
    [InlineData(0x0000A0C5)]
    [InlineData(0x0000B0C5)]
    public void PreciseMoveLengthPreventsControlAckAndGameMessageAliasing(int seq)
    {
        byte[] wire = MoveProtocol.EncodeMove(seq, new MoveCommand(Vector2.UnitY, true, 0f, true, true, true));

        Assert.Equal(18, wire.Length);
        Assert.False(MoveProtocol.TryDecodeClientControl(wire, out _));
        Assert.False(MoveProtocol.TryDecodeReplicationAck(wire, out _));
        Assert.False(MoveProtocol.TryDecodeGameMessage(wire, out _, out _));
        Assert.True(MoveProtocol.TryDecodeMove(wire, out int decodedSeq, out MoveCommand decoded));
        Assert.Equal(seq, decodedSeq);
        Assert.True(decoded.ScaleSpeedByAxis);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(17)]
    public void ShortPreciseMoveIsRejected(int length)
    {
        byte[] wire = Convert.FromHexString("2A000000000000000000803F040000000000");

        Assert.False(MoveProtocol.TryDecodeMove(wire.AsSpan(0, length), out int seq, out MoveCommand decoded));
        Assert.Equal(-1, seq);
        Assert.Equal(default, decoded);
    }
}
