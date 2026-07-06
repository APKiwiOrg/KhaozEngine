using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// Wire additions for delta replication: the <see cref="MoveProtocol.ServerFrameKind.Delta"/> server frame, the
/// <see cref="MoveProtocol.ClientControlKind.DeltaCapable"/> capability hello (old-server-harmless), and the
/// client-to-server replication ack (a distinct length so it never aliases a move or a control).
/// </summary>
public class DeltaProtocolTests
{
    [Fact]
    public void Delta_server_frame_round_trips_kind_and_payload()
    {
        byte[] payload = { 9, 8, 7, 6 };
        byte[] framed = MoveProtocol.EncodeServerFrame(MoveProtocol.ServerFrameKind.Delta, payload);

        Assert.True(MoveProtocol.TryDecodeServerFrame(framed, out MoveProtocol.ServerFrameKind kind, out byte[] body));
        Assert.Equal(MoveProtocol.ServerFrameKind.Delta, kind);
        Assert.Equal(payload, body);
    }

    [Fact]
    public void DeltaCapable_hello_decodes_as_a_control_and_is_harmless_to_an_older_server()
    {
        // The hello is a 2-byte ClientControl: an older server decodes it as a control with an unknown kind and
        // (its switch only acts on SelfRescue) harmlessly ignores it: no malformed-packet flag, no protocol break.
        byte[] hello = MoveProtocol.EncodeClientControl(MoveProtocol.ClientControlKind.DeltaCapable);

        Assert.True(MoveProtocol.TryDecodeClientControl(hello, out MoveProtocol.ClientControlKind kind));
        Assert.Equal(MoveProtocol.ClientControlKind.DeltaCapable, kind);
        Assert.NotEqual(MoveProtocol.ClientControlKind.SelfRescue, kind);
    }

    [Fact]
    public void Replication_ack_round_trips_the_seq()
    {
        byte[] ack = MoveProtocol.EncodeReplicationAck(4242);
        Assert.True(MoveProtocol.TryDecodeReplicationAck(ack, out int seq));
        Assert.Equal(4242, seq);
    }

    [Fact]
    public void Replication_ack_does_not_alias_a_control_or_a_move()
    {
        byte[] ack = MoveProtocol.EncodeReplicationAck(1);
        Assert.False(MoveProtocol.TryDecodeClientControl(ack, out _));       // distinct length from a 2-byte control
        Assert.False(MoveProtocol.TryDecodeMove(ack, out _, out _));         // shorter than an 18-byte move

        byte[] move = MoveProtocol.EncodeMove(7, new MoveCommand(new Vector2(1, 0), false, 0f));
        byte[] control = MoveProtocol.EncodeClientControl(MoveProtocol.ClientControlKind.SelfRescue);
        Assert.False(MoveProtocol.TryDecodeReplicationAck(move, out _));
        Assert.False(MoveProtocol.TryDecodeReplicationAck(control, out _));
    }
}
