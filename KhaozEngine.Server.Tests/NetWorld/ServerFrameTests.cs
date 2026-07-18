using System;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class ServerFrameTests
{
    [Fact]
    public void Snapshot_frame_round_trips_kind_and_payload()
    {
        byte[] payload = { 1, 2, 3, 4, 5 };
        byte[] framed = MoveProtocol.EncodeServerFrame(MoveProtocol.ServerFrameKind.Snapshot, payload);

        Assert.True(MoveProtocol.TryDecodeServerFrame(framed, out MoveProtocol.ServerFrameKind kind, out byte[] body));
        Assert.Equal(MoveProtocol.ServerFrameKind.Snapshot, kind);
        Assert.Equal(payload, body);
    }

    [Fact]
    public void Notice_frame_round_trips_kind()
    {
        byte[] framed = MoveProtocol.EncodeServerFrame(MoveProtocol.ServerFrameKind.Notice, Array.Empty<byte>());
        Assert.True(MoveProtocol.TryDecodeServerFrame(framed, out MoveProtocol.ServerFrameKind kind, out byte[] body));
        Assert.Equal(MoveProtocol.ServerFrameKind.Notice, kind);
        Assert.Empty(body);
    }

    [Fact]
    public void Empty_input_is_rejected()
    {
        Assert.False(MoveProtocol.TryDecodeServerFrame(Array.Empty<byte>(), out _, out _));
    }
}
