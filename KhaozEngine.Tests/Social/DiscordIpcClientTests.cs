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
