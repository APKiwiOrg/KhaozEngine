using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class MoveProtocolTests
{
    [Fact]
    public void Move_round_trips()
    {
        var cmd = new MoveCommand(new Vector2(0.5f, -1f), run: true, cameraYaw: 1.23f, jump: true);
        byte[] wire = MoveProtocol.EncodeMove(seq: 42, cmd);
        Assert.True(MoveProtocol.TryDecodeMove(wire, out int seq, out MoveCommand back));
        Assert.Equal(42, seq);
        Assert.Equal(cmd.Move, back.Move);
        Assert.Equal(cmd.Run, back.Run);
        Assert.Equal(cmd.CameraYaw, back.CameraYaw, 5);
        Assert.True(back.Jump);
    }

    [Fact]
    public void Move_round_trips_without_jump()
    {
        var cmd = new MoveCommand(new Vector2(0.5f, -1f), run: false, cameraYaw: 0f, jump: false);
        byte[] wire = MoveProtocol.EncodeMove(seq: 1, cmd);
        Assert.True(MoveProtocol.TryDecodeMove(wire, out _, out MoveCommand back));
        Assert.False(back.Jump);
    }

    [Fact]
    public void Move_decode_rejects_short_payload()
    {
        Assert.False(MoveProtocol.TryDecodeMove(new byte[] { 1, 2, 3 }, out _, out _));
    }

    [Fact]
    public void Snapshot_frame_round_trips()
    {
        byte[] snap = { 9, 8, 7, 6, 5 };
        byte[] frame = MoveProtocol.EncodeSnapshotFrame(localNetId: 3, ackSeq: 11, snap);
        Assert.True(MoveProtocol.TryDecodeSnapshotFrame(frame, out int id, out int ack, out byte[] back));
        Assert.Equal(3, id);
        Assert.Equal(11, ack);
        Assert.Equal(snap, back);
    }

    [Fact]
    public void Snapshot_frame_rejects_short_payload()
    {
        Assert.False(MoveProtocol.TryDecodeSnapshotFrame(new byte[] { 1, 2 }, out _, out _, out _));
    }
}
