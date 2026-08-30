using System;
using System.Buffers.Binary;
using System.IO;
using KhaozEngine.Social.Discord.Internal;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// Issue #159: one bad length in the byte stream used to wedge the Discord IPC client for the rest of the
/// session. TryDecodeFrame bounded only the negative case, so an out-of-range declared body read as "not
/// enough bytes yet, keep waiting" for a body that was never coming. The socket itself stays healthy in that
/// scenario, so the transport-death check from #655 never fires either: IsConnected stayed true, presence
/// silently stopped updating, and nothing threw anywhere. The declared length is now bounded, and a hopeless
/// desync tears the connection down instead.
/// </summary>
public class DiscordIpcDesyncTests
{
    private static byte[] Header(DiscordIpcOpcode opcode, int declaredLength, int bodyBytesPresent = 0)
    {
        byte[] buf = new byte[DiscordIpcCodec.HeaderSize + bodyBytesPresent];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), (int)opcode);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), declaredLength);
        return buf;
    }

    [Theory]
    [InlineData(100 * 1024 * 1024)]                   // the issue's 100 MB body
    [InlineData(DiscordIpcCodec.MaxBodyLength + 1)]   // one byte over the bound
    [InlineData(int.MaxValue)]
    [InlineData(-1)]                                  // negative: also a wedge before, not just a false
    [InlineData(int.MinValue)]
    public void DecodeFrame_ThrowsOnALengthNoStreamCouldSatisfy(int declaredLength)
    {
        InvalidDataException ex = Assert.Throws<InvalidDataException>(
            () => DiscordIpcCodec.TryDecodeFrame(Header(DiscordIpcOpcode.Frame, declaredLength), out _, out _, out _));

        Assert.Contains(declaredLength.ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Message);
    }

    [Fact]
    public void DecodeFrame_StillWaitsForABodyThatIsMerelyLate()
    {
        // At the bound exactly, and under it: a partial frame is ordinary backpressure and must not throw.
        Assert.False(DiscordIpcCodec.TryDecodeFrame(
            Header(DiscordIpcOpcode.Frame, DiscordIpcCodec.MaxBodyLength, bodyBytesPresent: 16),
            out _, out _, out int consumed));
        Assert.Equal(0, consumed);

        Assert.False(DiscordIpcCodec.TryDecodeFrame(
            Header(DiscordIpcOpcode.Frame, 4096, bodyBytesPresent: 100), out _, out _, out _));
    }

    [Fact]
    public void Pump_TearsDownOnADesyncedFrame_InsteadOfWedgingForever()
    {
        var transport = new FakeDiscordIpcTransport();
        var client = new DiscordIpcClient(transport);
        Assert.True(client.TryConnect("app-1"));

        // A healthy socket carrying one bogus length: nothing here breaks the pipe, which is exactly why the
        // transport-death check cannot see it.
        transport.EnqueueRaw(Header(DiscordIpcOpcode.Frame, 100 * 1024 * 1024));

        client.Pump();

        Assert.False(client.IsConnected);              // torn down, so the controller can reconnect
        Assert.True(transport.DisconnectCalls > 0);    // and the socket went back cleanly with it
        Assert.False(transport.IsConnected);

        // A later Pump on the dead client is a no-op rather than a re-throw, and a reconnect starts clean.
        client.Pump();
        Assert.False(client.IsConnected);
        Assert.True(client.TryConnect("app-1"));
        Assert.True(client.IsConnected);
    }

    [Fact]
    public void Pump_KeepsWorkingWhenTheStreamIsMerelySlow()
    {
        // Not vacuous: the same client, given a frame split across two reads, decodes it and stays connected.
        var transport = new FakeDiscordIpcTransport();
        var client = new DiscordIpcClient(transport);
        Assert.True(client.TryConnect("app-1"));

        byte[] frame = DiscordIpcCodec.EncodeFrame(DiscordIpcOpcode.Frame,
            """{"cmd":"DISPATCH","evt":"READY","data":{"user":{"id":"5","username":"kiwi","global_name":"Kiwi"}}}""");
        transport.EnqueueRaw(frame[..10]);

        client.Pump();          // the head only: still waiting, still connected
        Assert.True(client.IsConnected);
        Assert.Null(client.LocalUser);

        transport.EnqueueRaw(frame[10..]);
        client.Pump();          // the rest arrives and the frame decodes
        Assert.True(client.IsConnected);
        Assert.NotNull(client.LocalUser);
        Assert.Equal("kiwi", client.LocalUser!.Value.Username);
    }
}
