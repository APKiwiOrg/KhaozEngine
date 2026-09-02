using System;
using System.Text;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TileProtocolFrameTests
{
    const int Planes = 4;

    static int CommandFrameSize => TileProtocol.EncodeCommand(0, TileCommand.None).Length;

    [Fact]
    public void A_snapshot_frame_round_trips_its_header_and_body()
    {
        byte[] body = { 9, 8, 7 };
        byte[] frame = TileProtocol.EncodeSnapshotFrame(0x0001_0000_0000_002AL, 41, 1234, body);
        Assert.Equal(TileProtocol.ServerFrameSnapshot, TileProtocol.ServerFrameTag(frame));
        Assert.True(TileProtocol.TryDecodeSnapshotFrame(frame, out long id, out int ack, out long tick, out byte[] back));
        Assert.Equal(0x0001_0000_0000_002AL, id);
        Assert.Equal(41, ack);
        Assert.Equal(1234, tick);
        Assert.Equal(body, back);
    }

    [Fact]
    public void A_snapshot_whose_natural_length_is_a_command_frame_is_padded_and_still_round_trips()
    {
        // The one body length that lands on the command frame's size, and the reason the snapshot frame carries a
        // flags byte at all. The pad has to survive the round trip invisibly: a padded two-byte body and a genuine
        // three-byte body are both 25 bytes on the wire, and only the flag tells them apart.
        int commandSize = CommandFrameSize;
        byte[] padded = TileProtocol.EncodeSnapshotFrame(1, 2, 3, new byte[] { 7, 7 });
        Assert.Equal(commandSize + 1, padded.Length);
        Assert.True(TileProtocol.TryDecodeSnapshotFrame(padded, out _, out _, out _, out byte[] two));
        Assert.Equal(new byte[] { 7, 7 }, two);

        byte[] plain = TileProtocol.EncodeSnapshotFrame(1, 2, 3, new byte[] { 7, 7, 7 });
        Assert.Equal(padded.Length, plain.Length);
        Assert.True(TileProtocol.TryDecodeSnapshotFrame(plain, out _, out _, out _, out byte[] three));
        Assert.Equal(new byte[] { 7, 7, 7 }, three);

        // And a frame that IS the command size in this direction is not a snapshot this encoder ever emitted.
        Assert.False(TileProtocol.TryDecodeSnapshotFrame(new byte[commandSize], out _, out _, out _, out _));
    }

    [Fact]
    public void No_snapshot_of_any_body_length_lands_on_the_command_frame_size()
    {
        // The property the cross-family test used to assert by fixture. Held for EVERY body length rather than for
        // the one the fixture happened to pick: a 3-byte body was the length that broke it before the snapshot
        // frame joined the pad rule (21 header bytes plus 3 is exactly a command frame).
        int commandSize = CommandFrameSize;
        for (int len = 0; len <= commandSize + 16; len++)
        {
            var body = new byte[len];
            for (int i = 0; i < len; i++) body[i] = (byte)(i + 1);
            byte[] frame = TileProtocol.EncodeSnapshotFrame(1, 2, 3, body);

            Assert.NotEqual(commandSize, frame.Length);
            Assert.False(TileProtocol.TryDecodeCommand(frame, Planes, out _, out _));
            Assert.True(TileProtocol.TryDecodeSnapshotFrame(frame, out _, out _, out _, out byte[] back));
            Assert.Equal(body, back);
        }
    }

    [Fact]
    public void An_empty_frame_answers_the_no_tag_sentinel_in_both_directions()
    {
        Assert.Equal((byte)0xFF, TileProtocol.ClientFrameTag(ReadOnlySpan<byte>.Empty));
        Assert.Equal((byte)0xFF, TileProtocol.ServerFrameTag(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void A_game_message_round_trips_in_both_directions_and_is_capped()
    {
        byte[] payload = Encoding.UTF8.GetBytes("hello");
        foreach (byte tag in new[] { TileProtocol.ClientFrameGameMessage, TileProtocol.ServerFrameGameMessage })
        {
            byte[] frame = TileProtocol.EncodeGameMessage(tag, 7, payload);
            Assert.True(TileProtocol.TryDecodeGameMessage(frame, tag, out ushort kind, out ReadOnlySpan<byte> back));
            Assert.Equal(7, kind);
            Assert.Equal(payload, back.ToArray());
        }
        Assert.Throws<ArgumentException>(() =>
            TileProtocol.EncodeGameMessage(TileProtocol.ClientFrameGameMessage, 1, new byte[TileProtocol.MaxGameMessageBytes + 1]));
    }

    [Fact]
    public void A_game_message_over_the_payload_cap_is_refused_by_the_decoder_too()
    {
        var oversized = new byte[TileProtocol.MaxGameMessageBytes + 8];
        oversized[0] = TileProtocol.ServerFrameGameMessage;
        Assert.False(TileProtocol.TryDecodeGameMessage(oversized, TileProtocol.ServerFrameGameMessage, out _, out _));
    }

    [Fact]
    public void A_notice_round_trips_its_reason_token()
    {
        byte[] frame = TileProtocol.EncodeNotice("ke:draining");
        Assert.True(TileProtocol.TryDecodeNotice(frame, out string reason));
        Assert.Equal("ke:draining", reason);
    }

    [Fact]
    public void A_notice_over_the_token_cap_is_refused_by_the_encoder()
    {
        Assert.Throws<ArgumentException>(() => TileProtocol.EncodeNotice(new string('x', TileProtocol.MaxNoticeBytes + 1)));
    }

    [Fact]
    public void A_game_message_on_a_tag_this_wire_does_not_own_is_refused_by_the_encoder()
    {
        // Encoding one produces a frame the matching decoder refuses, so the message would be dropped in silence.
        // The command tag is the case that matters: it builds something command SHAPED that is not a command.
        Assert.Throws<ArgumentException>(() =>
            TileProtocol.EncodeGameMessage(TileProtocol.ClientFrameCommand, 1, new byte[] { 1 }));
        Assert.Throws<ArgumentException>(() =>
            TileProtocol.EncodeGameMessage(TileProtocol.ServerFrameNotice, 1, new byte[] { 1 }));
    }

    [Fact]
    public void An_invalid_utf8_reason_token_substitutes_rather_than_throwing()
    {
        // The lenient decoder is the RIGHT one here: a strict Encoding.UTF8 would throw inside the receive loop on
        // a byte string a remote peer chose. This pins that, so a later switch to a throwing decoder goes red here
        // rather than on the wire.
        byte[] frame = { TileProtocol.ServerFrameNotice, 3, 0xFF, 0xFE, 0xFD };
        Assert.True(TileProtocol.TryDecodeNotice(frame, out string token));
        Assert.Equal("\uFFFD\uFFFD\uFFFD", token);
    }

    [Fact]
    public void A_notice_whose_declared_length_disagrees_with_its_frame_is_refused()
    {
        byte[] frame = TileProtocol.EncodeNotice("ke:draining");
        byte[] lying = (byte[])frame.Clone();
        lying[1] = (byte)(frame[1] + 1);
        Assert.False(TileProtocol.TryDecodeNotice(lying, out _));
        Assert.False(TileProtocol.TryDecodeNotice(frame.AsSpan(0, frame.Length - 1), out _));
    }

    [Fact]
    public void A_connect_token_peels_back_to_the_version_the_world_and_the_auth_token()
    {
        byte[] auth = Encoding.UTF8.GetBytes("acct");
        byte[] token = TileProtocol.BuildConnectToken("tile-1", "hash-1", auth);
        Assert.True(KhaozEngine.Netcode.HandshakeToken.TryUnwrap(token, out string version, out byte[] inner));
        Assert.Equal("tile-1", version);
        Assert.True(KhaozEngine.Netcode.HandshakeToken.TryUnwrap(inner, out string world, out byte[] innermost));
        Assert.Equal("hash-1", world);
        Assert.Equal(auth, innermost);
    }

    [Fact]
    public void No_envelope_of_any_payload_length_ever_lands_on_the_command_frame_size()
    {
        int commandSize = CommandFrameSize;
        for (int len = 0; len <= commandSize + 16; len++)
        {
            byte[] message = TileProtocol.EncodeGameMessage(TileProtocol.ClientFrameGameMessage, 3, new byte[len]);
            byte[] notice = TileProtocol.EncodeNotice(new string('a', len));

            Assert.NotEqual(commandSize, message.Length);
            Assert.NotEqual(commandSize, notice.Length);
            Assert.False(TileProtocol.TryDecodeCommand(message, Planes, out _, out _));
            Assert.False(TileProtocol.TryDecodeCommand(notice, Planes, out _, out _));

            Assert.True(TileProtocol.TryDecodeGameMessage(message, TileProtocol.ClientFrameGameMessage,
                out ushort kind, out ReadOnlySpan<byte> payload));
            Assert.Equal(3, kind);
            Assert.Equal(len, payload.Length);
            Assert.True(TileProtocol.TryDecodeNotice(notice, out string token));
            Assert.Equal(new string('a', len), token);
        }
    }

    [Fact]
    public void A_frame_of_one_family_never_decodes_as_a_frame_of_another()
    {
        byte[] command = TileProtocol.EncodeCommand(3, TileCommand.WalkTo(new TileCoord(1, 1, 0), TileMoveMode.Run));
        byte[] snapshot = TileProtocol.EncodeSnapshotFrame(1, 2, 3, new byte[] { 4, 5 });
        byte[] message = TileProtocol.EncodeGameMessage(TileProtocol.ServerFrameGameMessage, 1, new byte[] { 6 });
        byte[] notice = TileProtocol.EncodeNotice("ke:draining");

        Assert.False(TileProtocol.TryDecodeSnapshotFrame(message, out _, out _, out _, out _));
        Assert.False(TileProtocol.TryDecodeSnapshotFrame(notice, out _, out _, out _, out _));
        Assert.False(TileProtocol.TryDecodeGameMessage(snapshot, TileProtocol.ServerFrameGameMessage, out _, out _));
        Assert.False(TileProtocol.TryDecodeGameMessage(notice, TileProtocol.ServerFrameGameMessage, out _, out _));
        Assert.False(TileProtocol.TryDecodeGameMessage(command, TileProtocol.ClientFrameGameMessage, out _, out _));
        Assert.False(TileProtocol.TryDecodeNotice(snapshot, out _));
        Assert.False(TileProtocol.TryDecodeNotice(message, out _));
        Assert.False(TileProtocol.TryDecodeNotice(command, out _));

        // The command and the snapshot share tag 0, one per direction, so this pair is the one the pad rule has to
        // separate rather than the tag. It now holds in BOTH directions and for every snapshot body length, see
        // No_snapshot_of_any_body_length_lands_on_the_command_frame_size.
        Assert.False(TileProtocol.TryDecodeCommand(snapshot, Planes, out _, out _));
        Assert.False(TileProtocol.TryDecodeSnapshotFrame(command, out _, out _, out _, out _));
    }

    [Fact]
    public void Every_truncation_of_every_frame_is_refused_and_never_throws()
    {
        int commandSize = CommandFrameSize;
        byte[] command = TileProtocol.EncodeCommand(1, TileCommand.None);
        byte[] snapshot = TileProtocol.EncodeSnapshotFrame(1, 2, 3, new byte[] { 4, 5 });
        byte[] clientMessage = TileProtocol.EncodeGameMessage(TileProtocol.ClientFrameGameMessage, 1, new byte[] { 6 });
        byte[] serverMessage = TileProtocol.EncodeGameMessage(TileProtocol.ServerFrameGameMessage, 1, new byte[] { 6 });
        byte[] notice = TileProtocol.EncodeNotice("ke:draining");

        // Totality: every decoder answers for every prefix of every frame, and none of them throws. The command
        // decoder is the one that can be asserted across all of them, because its frame size is fixed. A CUT of
        // exactly the command size is the documented exception: the pad rule bounds whole frames, and any tag-0
        // frame long enough to be cut at 24 bytes is command shaped by construction. A transport that hands a
        // decoder a cut frame has already failed, which is why the rule is written about frames and not prefixes.
        foreach (byte[] f in new[] { command, snapshot, clientMessage, serverMessage, notice })
            for (int len = 0; len < f.Length; len++)
            {
                ReadOnlySpan<byte> cut = f.AsSpan(0, len);
                if (len != commandSize) Assert.False(TileProtocol.TryDecodeCommand(cut, Planes, out _, out _));
                TileProtocol.TryDecodeSnapshotFrame(cut, out _, out _, out _, out _);
                TileProtocol.TryDecodeGameMessage(cut, TileProtocol.ClientFrameGameMessage, out _, out _);
                TileProtocol.TryDecodeGameMessage(cut, TileProtocol.ServerFrameGameMessage, out _, out _);
                TileProtocol.TryDecodeNotice(cut, out _);
            }

        // And each frame's OWN decoder refuses every truncation of it. The notice declares its length, so every
        // short read of one is a refusal. The snapshot does not (its body is "the rest"), so what is pinned there
        // is that a cut below the header is refused and a cut above it reports exactly the body it still holds.
        for (int len = 0; len < notice.Length; len++)
            Assert.False(TileProtocol.TryDecodeNotice(notice.AsSpan(0, len), out _));

        int snapshotHeader = TileProtocol.EncodeSnapshotFrame(0, 0, 0, Array.Empty<byte>()).Length;
        byte[] unpadded = TileProtocol.EncodeSnapshotFrame(1, 2, 3, new byte[] { 4, 5, 6, 7, 8, 9 });
        for (int len = 0; len <= unpadded.Length; len++)
        {
            bool ok = TileProtocol.TryDecodeSnapshotFrame(unpadded.AsSpan(0, len), out _, out _, out _, out byte[] body);
            Assert.Equal(len >= snapshotHeader && len != commandSize, ok);
            if (ok) Assert.Equal(len - snapshotHeader, body.Length);
        }
    }

    [Fact]
    public void A_truncated_game_message_is_a_shorter_message_rather_than_a_refusal()
    {
        // The one frame family whose length is declared nowhere, so a cut of it is well formed by construction.
        // What is pinned instead: a cut below the header is refused, and a cut that decodes never claims more
        // payload than it holds. Anything stronger would be asserting a length field this envelope does not have.
        int header = TileProtocol.EncodeGameMessage(TileProtocol.ServerFrameGameMessage, 0, ReadOnlySpan<byte>.Empty).Length;
        byte[] frame = TileProtocol.EncodeGameMessage(TileProtocol.ServerFrameGameMessage, 1, new byte[] { 6, 6, 6 });
        for (int len = 0; len <= frame.Length; len++)
        {
            bool ok = TileProtocol.TryDecodeGameMessage(frame.AsSpan(0, len), TileProtocol.ServerFrameGameMessage,
                out _, out ReadOnlySpan<byte> payload);
            Assert.Equal(len >= header, ok);
            if (ok) Assert.Equal(len - header, payload.Length);
        }
    }

    [Fact]
    public void A_pad_flagged_frame_with_no_room_for_a_payload_is_refused()
    {
        // The minimal denial of service on this wire: an all-header frame claiming a trailing pad byte. Slicing
        // it would compute a negative payload length and throw out of the receive loop.
        var hostile = new byte[TileProtocol.EncodeGameMessage(TileProtocol.ServerFrameGameMessage, 0, ReadOnlySpan<byte>.Empty).Length];
        hostile[0] = TileProtocol.ServerFrameGameMessage;
        hostile[hostile.Length - 1] = 0x01;
        Assert.False(TileProtocol.TryDecodeGameMessage(hostile, TileProtocol.ServerFrameGameMessage, out _, out _));
    }

    [Fact]
    public void A_pad_flagged_frame_of_any_other_length_is_refused()
    {
        // The encoder pads exactly ONE length: the natural frame that lands on the command size, which goes out at
        // command size plus one. A five byte frame with the flag set used to decode as a well formed EMPTY message
        // with its fifth byte silently dropped, which is a second wire form for a message that has only one.
        var hostile = new byte[5];
        hostile[0] = TileProtocol.ServerFrameGameMessage;
        hostile[3] = 0x01;
        Assert.False(TileProtocol.TryDecodeGameMessage(hostile, TileProtocol.ServerFrameGameMessage, out _, out _));

        // Every other padded length is refused the same way, and the one the encoder emits still decodes. The padded
        // form carries CommandFrameSize minus the header bytes of payload, so it is the one length that keeps its
        // payload across the round trip.
        int header = TileProtocol.EncodeGameMessage(TileProtocol.ServerFrameGameMessage, 0, ReadOnlySpan<byte>.Empty).Length;
        var payload = new byte[CommandFrameSize - header];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i + 1);
        byte[] padded = TileProtocol.EncodeGameMessage(TileProtocol.ServerFrameGameMessage, 7, payload);
        Assert.Equal(CommandFrameSize + 1, padded.Length);
        Assert.True(TileProtocol.TryDecodeGameMessage(padded, TileProtocol.ServerFrameGameMessage, out ushort kind,
            out ReadOnlySpan<byte> back));
        Assert.Equal(7, kind);
        Assert.Equal(payload, back.ToArray());

        for (int len = header; len <= CommandFrameSize + 4; len++)
        {
            if (len == CommandFrameSize + 1) continue;
            var frame = new byte[len];
            frame[0] = TileProtocol.ServerFrameGameMessage;
            frame[3] = 0x01;
            Assert.False(TileProtocol.TryDecodeGameMessage(frame, TileProtocol.ServerFrameGameMessage, out _, out _));
        }
    }

    [Fact]
    public void Random_bytes_never_throw_in_any_decoder()
    {
        var rng = new Random(20260822);
        for (int i = 0; i < 5000; i++)
        {
            var junk = new byte[rng.Next(0, 64)];
            rng.NextBytes(junk);
            TileProtocol.TryDecodeSnapshotFrame(junk, out _, out _, out _, out _);
            TileProtocol.TryDecodeGameMessage(junk, TileProtocol.ServerFrameGameMessage, out _, out _);
            TileProtocol.TryDecodeNotice(junk, out _);
            TileProtocol.TryDecodeCommand(junk, Planes, out _, out _);
        }
    }
}
