using System;
using System.Text;
using KhaozEngine.Social.Discord.Internal;
using Xunit;

namespace KhaozEngine.Tests;

public class DiscordIpcCodecTests
{
    [Fact]
    public void EncodeFrame_WritesLittleEndianOpcodeLengthThenUtf8Body()
    {
        byte[] frame = DiscordIpcCodec.EncodeFrame(DiscordIpcOpcode.Handshake, "{\"v\":1}");

        // header: opcode (0) then length (7) as little-endian int32
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, frame[0..4]);
        Assert.Equal(new byte[] { 7, 0, 0, 0 }, frame[4..8]);
        Assert.Equal("{\"v\":1}", Encoding.UTF8.GetString(frame, 8, 7));
        Assert.Equal(15, frame.Length);
    }

    [Fact]
    public void DecodeFrame_RoundTripsEncode()
    {
        byte[] frame = DiscordIpcCodec.EncodeFrame(DiscordIpcOpcode.Frame, "{\"cmd\":\"SET_ACTIVITY\"}");

        bool ok = DiscordIpcCodec.TryDecodeFrame(frame, out DiscordIpcOpcode op, out string json, out int consumed);

        Assert.True(ok);
        Assert.Equal(DiscordIpcOpcode.Frame, op);
        Assert.Equal("{\"cmd\":\"SET_ACTIVITY\"}", json);
        Assert.Equal(frame.Length, consumed);
    }

    [Fact]
    public void DecodeFrame_ReturnsFalseWhenHeaderIncomplete()
    {
        bool ok = DiscordIpcCodec.TryDecodeFrame(new byte[] { 1, 0, 0 }, out _, out _, out int consumed);
        Assert.False(ok);
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void DecodeFrame_ReturnsFalseWhenBodyIncomplete()
    {
        // header says 10 bytes of body but only 2 present
        byte[] buf = new byte[8 + 2];
        buf[0] = 1;              // opcode
        buf[4] = 10;             // length low byte
        bool ok = DiscordIpcCodec.TryDecodeFrame(buf, out _, out _, out int consumed);
        Assert.False(ok);
        Assert.Equal(0, consumed);
    }
}
