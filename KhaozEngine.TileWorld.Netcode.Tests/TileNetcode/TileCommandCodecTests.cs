using System;
using System.Collections.Generic;
using System.Reflection;
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

    // Every field of the frame at its literal byte offset, the standard the repo already applies to its other
    // durable formats (TilePlayerRecordTests pins the JSON byte for byte, HandshakeTokenCompatibilityTests pins the
    // handshake bytes). The tag, the length, the kind at [5] and the plane at [14] were pinned above already. What
    // this adds is goalX at 6, goalZ at 10, mode at 15 and target at 16, which lived only inside the symmetric
    // round trip, where swapping two of them in BOTH halves in one commit stays green and breaks every client
    // built against the old layout. Harmless while R1 ships no client, load-bearing the moment R2 ships one.
    //
    // The multi-byte fields are written through BitConverter, so these are host-endian bytes and every supported
    // RID is little-endian. A big-endian one would move the wire itself, which is exactly what should go red here.
    [Fact]
    public void Every_command_frame_field_sits_at_its_literal_byte_offset()
    {
        var cmd = new TileCommand(TileCommandKind.Interact, new TileCoord(0x11223344, 0x55667788, 2),
            TileMoveMode.Run, 0x0102030405060708L);
        byte[] f = TileProtocol.EncodeCommand(seq: 0x1A2B3C4D, cmd);

        Assert.Equal(24, f.Length);
        Assert.Equal(TileProtocol.ClientFrameCommand, f[0]);
        Assert.Equal(new byte[] { 0x4D, 0x3C, 0x2B, 0x1A }, f[1..5]);                       // seq
        Assert.Equal((byte)TileCommandKind.Interact, f[5]);
        Assert.Equal(new byte[] { 0x44, 0x33, 0x22, 0x11 }, f[6..10]);                      // goalX
        Assert.Equal(new byte[] { 0x88, 0x77, 0x66, 0x55 }, f[10..14]);                     // goalZ
        Assert.Equal(2, f[14]);                                                             // plane
        Assert.Equal((byte)TileMoveMode.Run, f[15]);                                        // mode
        Assert.Equal(new byte[] { 8, 7, 6, 5, 4, 3, 2, 1 }, f[16..24]);                     // target
    }

    // A tile command is integer-only, so the float protocol's non-finite rejection (design section 12) has no
    // representation here. Named so nobody re-adds a NaN guard to a struct that cannot hold one.
    //
    // The walk RECURSES into the struct types the command composes, which the top-level version did not: a float
    // smuggled into TileCoord passed unnoticed, so the test documented an invariant it did not enforce. The probe
    // assertion at the end is what proves the recursion actually runs, since TileCoord is all ints today and a
    // walker that silently did nothing would look identical.
    [Fact]
    public void A_tile_command_has_no_floating_point_field_to_be_non_finite()
    {
        Assert.Null(FirstFloatingPointField(typeof(TileCommand), nameof(TileCommand)));
        Assert.Null(FirstFloatingPointField(typeof(TileCoord), nameof(TileCoord)));

        // One level down is exactly where the old walk was blind.
        Assert.Equal("Probe.Nested.Drift", FirstFloatingPointField(typeof(Probe), nameof(Probe)));
    }

    struct Probe { public NestedProbe Nested { get; set; } }
    struct NestedProbe { public int Steps { get; set; } public float Drift { get; set; } }

    // The dotted path to the first float or double reachable from this type, or null when there is none. Value
    // types only: a reference type here would be a wire format this protocol does not have, and following one
    // would walk out into the BCL.
    static string? FirstFloatingPointField(Type type, string path, HashSet<Type>? seen = null)
    {
        seen ??= new HashSet<Type>();
        foreach (PropertyInfo p in type.GetProperties())
        {
            Type t = p.PropertyType;
            string here = path + "." + p.Name;
            if (t == typeof(float) || t == typeof(double)) return here;
            if (!t.IsValueType || t.IsPrimitive || t.IsEnum || !seen.Add(t)) continue;
            if (FirstFloatingPointField(t, here, seen) is { } found) return found;
        }
        return null;
    }
}
