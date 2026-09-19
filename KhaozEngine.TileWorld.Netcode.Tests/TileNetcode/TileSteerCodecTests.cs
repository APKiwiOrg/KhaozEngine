using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TileSteerCodecTests
{
    const int Planes = 4;

    [Theory]
    [InlineData(TileDirection.W)] [InlineData(TileDirection.E)]
    [InlineData(TileDirection.S)] [InlineData(TileDirection.N)]
    [InlineData(TileDirection.SW)] [InlineData(TileDirection.SE)]
    [InlineData(TileDirection.NW)] [InlineData(TileDirection.NE)]
    public void Every_direction_round_trips(TileDirection direction)
    {
        TileCommand cmd = TileCommand.Steer(direction, TileMoveMode.Run);
        byte[] frame = TileProtocol.EncodeCommand(seq: 3, cmd);

        Assert.Equal(24, frame.Length);
        Assert.Equal(5, frame[5]);
        Assert.True(TileProtocol.TryDecodeCommand(frame, Planes, out int seq, out TileCommand back));
        Assert.Equal(3, seq);
        Assert.Equal(cmd, back);
        Assert.Equal(direction, back.SteerDirection);
        Assert.Equal(TileMoveMode.Run, back.Mode);
    }

    [Theory]
    [InlineData(8L)] [InlineData(-1L)] [InlineData(256L)] [InlineData(long.MaxValue)]
    public void An_undefined_direction_is_rejected_whole(long target)
    {
        byte[] frame = TileProtocol.EncodeCommand(0,
            new TileCommand(TileCommandKind.Steer, default, TileMoveMode.Walk, target));
        Assert.False(TileProtocol.TryDecodeCommand(frame, Planes, out _, out _));
    }

    [Fact]
    public void The_kind_after_steer_is_still_unknown()
    {
        byte[] frame = TileProtocol.EncodeCommand(0, TileCommand.Steer(TileDirection.N, TileMoveMode.Walk));
        frame[5] = 6;
        Assert.False(TileProtocol.TryDecodeCommand(frame, Planes, out _, out _));
    }
}
