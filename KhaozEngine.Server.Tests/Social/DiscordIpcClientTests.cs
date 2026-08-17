using System.Linq;
using KhaozEngine.Social;
using KhaozEngine.Social.Discord.Internal;
using Xunit;

namespace KhaozEngine.Tests;

public class DiscordIpcClientTests
{
    private static DiscordIpcClient Connected(FakeDiscordIpcTransport transport)
    {
        var client = new DiscordIpcClient(transport);
        Assert.True(client.TryConnect("app-1"));
        return client;
    }

    [Fact]
    public void TryConnect_SendsHandshakeFrameFirst()
    {
        var transport = new FakeDiscordIpcTransport();
        using DiscordIpcClient client = Connected(transport);

        // The first frame written is the handshake carrying the client id.
        Assert.True(DiscordIpcCodec.TryDecodeFrame(
            transport.Written.ToArray(), out DiscordIpcOpcode first, out string firstJson, out _));
        Assert.Equal(DiscordIpcOpcode.Handshake, first);
        Assert.Contains("app-1", firstJson);
    }

    [Fact]
    public void TryConnect_ReturnsFalseWhenTransportCannotConnect()
    {
        var transport = new FakeDiscordIpcTransport { ConnectResult = false };
        var client = new DiscordIpcClient(transport);
        Assert.False(client.TryConnect("app-1"));
        Assert.False(client.IsConnected);
    }

    [Fact]
    public void Pump_ReadyDispatch_SetsLocalUser()
    {
        var transport = new FakeDiscordIpcTransport();
        using DiscordIpcClient client = Connected(transport);
        transport.EnqueueFrame(DiscordIpcOpcode.Frame,
            """{"cmd":"DISPATCH","evt":"READY","data":{"user":{"id":"5","username":"kiwi","global_name":"Kiwi"}}}""");

        client.Pump();

        Assert.NotNull(client.LocalUser);
        Assert.Equal("kiwi", client.LocalUser!.Value.Username);
    }

    [Fact]
    public void Pump_ActivityJoin_RaisesJoinSecret()
    {
        var transport = new FakeDiscordIpcTransport();
        using DiscordIpcClient client = Connected(transport);
        string? secret = null;
        client.JoinSecretReceived += s => secret = s;
        transport.EnqueueFrame(DiscordIpcOpcode.Frame,
            """{"cmd":"DISPATCH","evt":"ACTIVITY_JOIN","data":{"secret":"join-xyz"}}""");

        client.Pump();

        Assert.Equal("join-xyz", secret);
    }

    [Fact]
    public void SetActivity_WritesSetActivityFrame()
    {
        var transport = new FakeDiscordIpcTransport();
        using DiscordIpcClient client = Connected(transport);
        client.SetActivity(new RichPresence { Details = "In Game", State = "Solo" });

        Assert.True(transport.TryReadLastWrittenFrame(out DiscordIpcOpcode op, out string json));
        Assert.Equal(DiscordIpcOpcode.Frame, op);
        Assert.Contains("SET_ACTIVITY", json);
        Assert.Contains("In Game", json);
    }

    [Fact]
    public void WriteFailure_MarksDisconnected()
    {
        var transport = new FakeDiscordIpcTransport { ThrowOnWrite = true };
        var client = new DiscordIpcClient(transport);
        // handshake write throws -> connect fails cleanly
        Assert.False(client.TryConnect("app-1"));
        Assert.False(client.IsConnected);
    }

    [Fact]
    public void Pump_MalformedDispatch_StaysConnected()
    {
        var transport = new FakeDiscordIpcTransport();
        using DiscordIpcClient client = Connected(transport);
        // a wrong-typed READY (numeric id) must not throw or tear down the session
        transport.EnqueueFrame(DiscordIpcOpcode.Frame,
            """{"cmd":"DISPATCH","evt":"READY","data":{"user":{"id":12345,"username":"k"}}}""");

        client.Pump();

        Assert.True(client.IsConnected);
        Assert.Null(client.LocalUser);
    }

    [Fact]
    public void Pump_ASocketThatDiedQuietly_Disconnects_AndHandsTheTransportBackClean()
    {
        // #655: a player quitting Discord is the common shape of a drop, and the quiet one. No Close frame,
        // no throw from any read or write, just a socket that is not there. Nothing but the transport's own
        // IsConnected reports it, and before this the client sat "connected" to it for the whole session.
        var transport = new FakeDiscordIpcTransport();
        using DiscordIpcClient client = Connected(transport);
        transport.EnqueueFrame(DiscordIpcOpcode.Frame,
            """{"cmd":"DISPATCH","evt":"READY","data":{"user":{"id":"5","username":"kiwi","global_name":"Kiwi"}}}""");
        client.Pump();
        Assert.NotNull(client.LocalUser);

        transport.SimulateQuietDeath();
        client.Pump();

        Assert.False(client.IsConnected);
        Assert.Null(client.LocalUser);

        // And the socket plus its reader thread are released now, not left live for the whole reconnect
        // backoff the controller is about to sit through.
        Assert.Equal(1, transport.DisconnectCalls);
    }

    [Fact]
    public void Pump_CloseFrame_TearsTheTransportDown_Once()
    {
        var transport = new FakeDiscordIpcTransport();
        using DiscordIpcClient client = Connected(transport);
        transport.EnqueueFrame(DiscordIpcOpcode.Close, "{}");

        client.Pump();

        Assert.False(client.IsConnected);
        Assert.Equal(1, transport.DisconnectCalls);
    }

    [Fact]
    public void Pump_AReadThatThrows_TearsTheTransportDown()
    {
        var transport = new FakeDiscordIpcTransport { ThrowOnRead = true };
        using DiscordIpcClient client = Connected(transport);

        client.Pump();

        Assert.False(client.IsConnected);
        Assert.Equal(1, transport.DisconnectCalls);
    }

    [Fact]
    public void AfterADrop_ReconnectingOnTheSameClient_IsAFreshSession()
    {
        var transport = new FakeDiscordIpcTransport();
        using DiscordIpcClient client = Connected(transport);
        transport.SimulateQuietDeath();
        client.Pump();
        Assert.False(client.IsConnected);

        // Discord is still gone when the controller's first reconnect attempt lands, and a failed attempt
        // leaves the client down rather than half-up.
        transport.ConnectResult = false;
        Assert.False(client.TryConnect("app-1"));
        Assert.False(client.IsConnected);

        // Then it is back, on the same client and the same transport instance.
        transport.ConnectResult = true;
        Assert.True(client.TryConnect("app-1"));
        Assert.True(client.IsConnected);

        transport.EnqueueFrame(DiscordIpcOpcode.Frame,
            """{"cmd":"DISPATCH","evt":"READY","data":{"user":{"id":"9","username":"back","global_name":null}}}""");
        client.Pump();
        Assert.Equal("back", client.LocalUser!.Value.Username);
    }

    [Fact]
    public void ClearActivity_WritesNullActivityFrame()
    {
        var transport = new FakeDiscordIpcTransport();
        using DiscordIpcClient client = Connected(transport);
        client.ClearActivity();

        Assert.True(transport.TryReadLastWrittenFrame(out _, out string json));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("SET_ACTIVITY", doc.RootElement.GetProperty("cmd").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null,
            doc.RootElement.GetProperty("args").GetProperty("activity").ValueKind);
    }
}
