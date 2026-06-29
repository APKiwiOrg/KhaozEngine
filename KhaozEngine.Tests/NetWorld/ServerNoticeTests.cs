using System;
using System.Text;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class ServerNoticeTests
{
    [Fact]
    public void Round_trips_kind_message_and_seconds()
    {
        var n = new ServerNotice(ServerNoticeKind.Maintenance, "Restarting soon", secondsUntil: 30f);
        ServerNotice back = MoveProtocol.DecodeNotice(MoveProtocol.EncodeNotice(n));
        Assert.Equal(ServerNoticeKind.Maintenance, back.Kind);
        Assert.Equal("Restarting soon", back.Message);
        Assert.True(back.SecondsUntil.HasValue);
        Assert.Equal(30f, back.SecondsUntil!.Value, 3);
        Assert.Empty(back.Payload);
    }

    [Fact]
    public void Round_trips_absent_seconds_and_custom_payload()
    {
        byte[] payload = { 9, 8, 7 };
        var n = new ServerNotice(ServerNoticeKind.Custom, "evt", secondsUntil: null, payload: payload);
        ServerNotice back = MoveProtocol.DecodeNotice(MoveProtocol.EncodeNotice(n));
        Assert.Equal(ServerNoticeKind.Custom, back.Kind);
        Assert.False(back.SecondsUntil.HasValue);
        Assert.Equal(payload, back.Payload);
    }

    [Fact]
    public void Oversize_message_is_truncated_on_the_wire()
    {
        string huge = new string('x', 5000);
        var n = new ServerNotice(ServerNoticeKind.Custom, huge);
        byte[] wire = MoveProtocol.EncodeNotice(n);
        Assert.True(wire.Length < 1000, $"oversize message not capped: {wire.Length} bytes");
        ServerNotice back = MoveProtocol.DecodeNotice(wire);
        Assert.True(Encoding.UTF8.GetByteCount(back.Message) <= MoveProtocol.MaxNoticeMessageBytes);
    }

    [Fact]
    public void Corrupt_short_buffer_decodes_to_a_safe_default_without_throwing()
    {
        ServerNotice back = MoveProtocol.DecodeNotice(Array.Empty<byte>());
        Assert.Equal(string.Empty, back.Message);
    }
}
