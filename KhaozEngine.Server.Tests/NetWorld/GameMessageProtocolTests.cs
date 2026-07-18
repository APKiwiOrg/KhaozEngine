using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// Wire-level tests for the generic game-message seam (<see cref="MoveProtocol.EncodeGameMessage"/> and the
/// <see cref="MoveProtocol.ServerFrameKind.GameMessage"/> envelope). The load-bearing property is the aliasing
/// contract: a client-to-server game message shares the 0xC5 marker family with the move/control/ack frames yet can
/// never be mistaken for any of the 2 / 6 / 18 byte shapes the server demuxes on.
/// </summary>
public class GameMessageProtocolTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(12)]   // natural length 17
    [InlineData(13)]   // natural length 18 -> must be padded to 19
    [InlineData(14)]   // natural length 19 (no pad)
    [InlineData(64)]
    [InlineData(1024)]
    public void Client_game_message_round_trips_any_payload_size(int size)
    {
        var payload = new byte[size];
        for (int i = 0; i < size; i++) payload[i] = (byte)(i * 7 + 1);
        byte[] wire = MoveProtocol.EncodeGameMessage(kind: 0xBEEF, payload);

        Assert.True(MoveProtocol.TryDecodeGameMessage(wire, out ushort kind, out ReadOnlySpan<byte> back));
        Assert.Equal(0xBEEF, kind);
        Assert.Equal(payload, back.ToArray());
    }

    [Fact]
    public void Client_game_message_never_encodes_to_18_bytes()
    {
        // 18 is the move length the server demuxes on. The one payload size whose natural frame length is 18 (13 bytes:
        // 5-byte header + 13) must be padded to 19 so it can never alias a move; every other size is left as-is.
        for (int size = 0; size <= 64; size++)
        {
            byte[] wire = MoveProtocol.EncodeGameMessage(kind: 1, new byte[size]);
            Assert.NotEqual(18, wire.Length);
        }
        Assert.Equal(19, MoveProtocol.EncodeGameMessage(kind: 1, new byte[13]).Length);   // padded away from 18
        Assert.Equal(17, MoveProtocol.EncodeGameMessage(kind: 1, new byte[12]).Length);   // unaffected
        Assert.Equal(19, MoveProtocol.EncodeGameMessage(kind: 1, new byte[14]).Length);   // natural 19, no pad
    }

    [Fact]
    public void Game_message_decode_rejects_an_18_byte_move()
    {
        // A real move is exactly 18 bytes of arbitrary content. The game-message decode must reject length 18 outright
        // so a move is never stolen, even one whose leading bytes coincidentally match the game-message marker pair.
        byte[] move = MoveProtocol.EncodeMove(seq: 5, new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: 0f));
        Assert.Equal(18, move.Length);
        Assert.False(MoveProtocol.TryDecodeGameMessage(move, out _, out _));
    }

    [Fact]
    public void A_move_whose_first_bytes_match_the_game_marker_is_still_not_a_game_message()
    {
        // seq 0x0000B0C5 puts 0xC5 at [0] and 0xB0 at [1] - exactly the game-message marker pair. The length-18 guard
        // is what keeps this legitimate move from being decoded as a game message.
        byte[] move = MoveProtocol.EncodeMove(seq: 0x0000B0C5, new MoveCommand(new Vector2(0f, 0f), run: false, cameraYaw: 0f));
        Assert.Equal(0xC5, move[0]);
        Assert.Equal(0xB0, move[1]);
        Assert.False(MoveProtocol.TryDecodeGameMessage(move, out _, out _));
        // ...and the move still decodes as a move.
        Assert.True(MoveProtocol.TryDecodeMove(move, out int seq, out _));
        Assert.Equal(0x0000B0C5, seq);
    }

    [Fact]
    public void Game_message_decode_rejects_control_and_ack_frames()
    {
        byte[] control = MoveProtocol.EncodeClientControl(MoveProtocol.ClientControlKind.SelfRescue);   // 2 bytes
        byte[] ack = MoveProtocol.EncodeReplicationAck(appliedSeq: 42);                                  // 6 bytes, [1]==0xA0
        Assert.False(MoveProtocol.TryDecodeGameMessage(control, out _, out _));
        Assert.False(MoveProtocol.TryDecodeGameMessage(ack, out _, out _));
    }

    [Fact]
    public void Ack_and_control_decodes_reject_a_game_message_frame()
    {
        // The reverse direction: the ack/control decodes (tried before the game-message decode) must not steal a game
        // message. A 4-byte-payload game message is 9 bytes (!= 2, != 6), and a game message carries [1]==0xB0 != 0xA0.
        byte[] gm = MoveProtocol.EncodeGameMessage(kind: 7, new byte[] { 1, 2, 3, 4 });
        Assert.False(MoveProtocol.TryDecodeClientControl(gm, out _));
        Assert.False(MoveProtocol.TryDecodeReplicationAck(gm, out _));
        Assert.False(MoveProtocol.TryDecodeMove(gm, out _, out _));   // 9 bytes < 18
    }

    [Fact]
    public void Empty_game_message_is_five_bytes_and_round_trips()
    {
        byte[] wire = MoveProtocol.EncodeGameMessage(kind: 3, ReadOnlySpan<byte>.Empty);
        Assert.Equal(5, wire.Length);
        Assert.True(MoveProtocol.TryDecodeGameMessage(wire, out ushort kind, out ReadOnlySpan<byte> back));
        Assert.Equal(3, kind);
        Assert.True(back.IsEmpty);
    }

    [Fact]
    public void Server_game_message_frame_round_trips_through_the_envelope()
    {
        byte[] payload = { 9, 8, 7 };
        byte[] envelope = MoveProtocol.EncodeServerFrame(
            MoveProtocol.ServerFrameKind.GameMessage, MoveProtocol.EncodeGameMessageBody(kind: 0xABCD, payload));

        Assert.True(MoveProtocol.TryDecodeServerFrame(envelope, out MoveProtocol.ServerFrameKind kind, out byte[] body));
        Assert.Equal(MoveProtocol.ServerFrameKind.GameMessage, kind);
        Assert.True(MoveProtocol.TryDecodeGameMessageBody(body, out ushort gameKind, out ReadOnlySpan<byte> back));
        Assert.Equal(0xABCD, gameKind);
        Assert.Equal(payload, back.ToArray());
    }

    [Fact]
    public void Server_game_message_body_rejects_a_short_frame()
    {
        Assert.False(MoveProtocol.TryDecodeGameMessageBody(new byte[] { 1 }, out _, out _));   // < 2-byte kind
    }
}
