using System;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TileCommandCodecTests
{
    const int Planes = 4;

    [Theory]
    [InlineData(TileCommandKind.None, 0, 0, 0, TileMoveMode.Walk, 0L)]
    [InlineData(TileCommandKind.WalkTo, 12, -3400, 2, TileMoveMode.Run, 0L)]
    [InlineData(TileCommandKind.Interact, -7, 9, 0, TileMoveMode.Walk, 4242L)]
    public void Every_command_round_trips(TileCommandKind kind, int x, int z, int plane, TileMoveMode mode, long target)
    {
        var cmd = new TileCommand(kind, new TileCoord(x, z, plane), mode, target);
        byte[] frame = TileProtocol.EncodeCommand(seq: 17, cmd);

        Assert.True(TileProtocol.TryDecodeCommand(frame, Planes, out int seq, out TileCommand back));
        Assert.Equal(17, seq);
        Assert.Equal(cmd, back);
    }

    [Fact]
    public void A_command_frame_carries_the_client_frame_tag()
    {
        byte[] frame = TileProtocol.EncodeCommand(0, TileCommand.None);
        Assert.Equal(TileProtocol.ClientFrameCommand, frame[0]);
        // The tag is 0 and the buffer starts zero-filled, so the byte check alone would survive deleting the write.
        // Pinning the frame size is what this test can actually hold on its own.
        Assert.Equal(24, frame.Length);
    }

    [Fact]
    public void An_unknown_kind_is_rejected()
    {
        byte[] frame = TileProtocol.EncodeCommand(1, TileCommand.WalkTo(new TileCoord(1, 1, 0), TileMoveMode.Walk));
        frame[5] = 99;
        Assert.False(TileProtocol.TryDecodeCommand(frame, Planes, out _, out _));
    }

    [Fact]
    public void A_plane_outside_the_worlds_plane_count_is_rejected()
    {
        var cmd = new TileCommand(TileCommandKind.WalkTo, new TileCoord(1, 1, 9), TileMoveMode.Walk, 0);
        byte[] frame = TileProtocol.EncodeCommand(1, cmd);
        Assert.False(TileProtocol.TryDecodeCommand(frame, Planes, out _, out _));
        Assert.True(TileProtocol.TryDecodeCommand(frame, 10, out _, out _));
    }

    [Fact]
    public void A_negative_sequence_is_rejected()
    {
        byte[] frame = TileProtocol.EncodeCommand(-1, TileCommand.None);
        Assert.False(TileProtocol.TryDecodeCommand(frame, Planes, out _, out _));
    }

    [Fact]
    public void Every_truncation_and_a_wrong_tag_is_rejected_and_never_throws()
    {
        byte[] frame = TileProtocol.EncodeCommand(3, TileCommand.Interact(7, TileMoveMode.Run));
        for (int len = 0; len < frame.Length; len++)
            Assert.False(TileProtocol.TryDecodeCommand(frame.AsSpan(0, len), Planes, out _, out _));

        byte[] wrongTag = (byte[])frame.Clone();
        wrongTag[0] = 0xEE;
        Assert.False(TileProtocol.TryDecodeCommand(wrongTag, Planes, out _, out _));
    }

    // A tile command is integer-only, so the float protocol's non-finite rejection (design section 12) has no
    // representation here. Named so nobody re-adds a NaN guard to a struct that cannot hold one.
    [Fact]
    public void A_tile_command_has_no_floating_point_field_to_be_non_finite()
    {
        foreach (System.Reflection.PropertyInfo p in typeof(TileCommand).GetProperties())
            Assert.True(p.PropertyType != typeof(float) && p.PropertyType != typeof(double), p.Name);
    }
}
