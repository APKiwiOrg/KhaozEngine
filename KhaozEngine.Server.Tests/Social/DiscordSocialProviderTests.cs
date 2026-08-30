using System.Text.Json;
using KhaozEngine.Social;
using KhaozEngine.Social.Discord;
using KhaozEngine.Social.Discord.Internal;
using Xunit;

namespace KhaozEngine.Tests;

public class DiscordSocialProviderTests
{
    private const string JoinRequestDispatch =
        """{"cmd":"DISPATCH","evt":"ACTIVITY_JOIN_REQUEST","data":{"user":{"id":"9","username":"ally","global_name":null}}}""";

    private static JoinRequest RaiseJoinRequest(DiscordSocialProvider provider, FakeDiscordIpcTransport transport)
    {
        JoinRequest? received = null;
        provider.JoinRequestReceived += r => received = r;
        transport.EnqueueFrame(DiscordIpcOpcode.Frame, JoinRequestDispatch);
        provider.Update();
        Assert.NotNull(received);
        return received!;
    }

    [Fact]
    public void Initialize_ConnectsAndReportsConnected()
    {
        var transport = new FakeDiscordIpcTransport();
        using var provider = new DiscordSocialProvider(transport);
        Assert.True(provider.TryInitialize("app-1"));
        Assert.True(provider.IsConnected);
    }

    [Fact]
    public void SetPresence_WritesActivity()
    {
        var transport = new FakeDiscordIpcTransport();
        using var provider = new DiscordSocialProvider(transport);
        provider.TryInitialize("app-1");
        provider.SetPresence(new RichPresence { Details = "In Game" });

        Assert.True(transport.TryReadLastWrittenFrame(out _, out string json));
        Assert.Contains("SET_ACTIVITY", json);
        Assert.Contains("In Game", json);
    }

    [Fact]
    public void Update_ReadyDispatch_ExposesLocalUser()
    {
        var transport = new FakeDiscordIpcTransport();
        using var provider = new DiscordSocialProvider(transport);
        provider.TryInitialize("app-1");
        transport.EnqueueFrame(DiscordIpcOpcode.Frame,
            """{"cmd":"DISPATCH","evt":"READY","data":{"user":{"id":"5","username":"kiwi","global_name":null}}}""");

        provider.Update();

        Assert.True(provider.TryGetLocalUser(out SocialUser user));
        Assert.Equal("kiwi", user.Username);
    }

    [Fact]
    public void Update_ActivityJoin_RaisesJoinRequested()
    {
        var transport = new FakeDiscordIpcTransport();
        using var provider = new DiscordSocialProvider(transport);
        provider.TryInitialize("app-1");
        string? secret = null;
        provider.JoinRequested += s => secret = s;
        transport.EnqueueFrame(DiscordIpcOpcode.Frame,
            """{"cmd":"DISPATCH","evt":"ACTIVITY_JOIN","data":{"secret":"j-1"}}""");

        provider.Update();

        Assert.Equal("j-1", secret);
    }

    [Fact]
    public void Update_ActivityJoinRequest_AcceptSendsTheJoinInviteNamingTheRequester()
    {
        // The headline of #162: Accept used to reach a null respond callback, so the asking friend's
        // Discord client sat waiting for an answer that was never sent.
        var transport = new FakeDiscordIpcTransport();
        using var provider = new DiscordSocialProvider(transport);
        provider.TryInitialize("app-1");
        JoinRequest request = RaiseJoinRequest(provider, transport);

        request.Accept();

        Assert.True(transport.TryReadLastWrittenFrame(out DiscordIpcOpcode op, out string json));
        Assert.Equal(DiscordIpcOpcode.Frame, op);
        JsonElement root = JsonDocument.Parse(json).RootElement;
        Assert.Equal("SEND_ACTIVITY_JOIN_INVITE", root.GetProperty("cmd").GetString());
        Assert.Equal("9", root.GetProperty("args").GetProperty("user_id").GetString());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("nonce").GetString()));
    }

    [Fact]
    public void Update_ActivityJoinRequest_RejectSendsTheCloseRequestNamingTheRequester()
    {
        var transport = new FakeDiscordIpcTransport();
        using var provider = new DiscordSocialProvider(transport);
        provider.TryInitialize("app-1");
        JoinRequest request = RaiseJoinRequest(provider, transport);

        request.Reject();

        Assert.True(transport.TryReadLastWrittenFrame(out _, out string json));
        JsonElement root = JsonDocument.Parse(json).RootElement;
        Assert.Equal("CLOSE_ACTIVITY_REQUEST", root.GetProperty("cmd").GetString());
        Assert.Equal("9", root.GetProperty("args").GetProperty("user_id").GetString());
    }

    [Fact]
    public void JoinRequest_AnsweredAfterTheConnectionDropped_IsASilentNoOp()
    {
        // A game answers from its own UI flow, an unbounded time after the request arrived, so Discord
        // going away in between is the normal case rather than an error to throw into game code.
        var transport = new FakeDiscordIpcTransport();
        using var provider = new DiscordSocialProvider(transport);
        provider.TryInitialize("app-1");
        JoinRequest request = RaiseJoinRequest(provider, transport);

        transport.SimulateQuietDeath();
        provider.Update();
        Assert.False(provider.IsConnected);

        int writtenBefore = transport.Written.Count;
        Assert.Null(Record.Exception(request.Accept));
        Assert.Equal(writtenBefore, transport.Written.Count);
    }

    [Fact]
    public void JoinRequest_AnsweredAfterTheProviderIsDisposed_IsASilentNoOp()
    {
        var transport = new FakeDiscordIpcTransport();
        var provider = new DiscordSocialProvider(transport);
        provider.TryInitialize("app-1");
        JoinRequest request = RaiseJoinRequest(provider, transport);

        provider.Dispose();

        int writtenBefore = transport.Written.Count;
        Assert.Null(Record.Exception(request.Reject));
        Assert.Equal(writtenBefore, transport.Written.Count);
    }

    [Fact]
    public void FailedConnect_IsNotConnected_AndNeverThrows()
    {
        var transport = new FakeDiscordIpcTransport { ConnectResult = false };
        using var provider = new DiscordSocialProvider(transport);
        Assert.False(provider.TryInitialize("app-1"));
        provider.SetPresence(new RichPresence { Details = "x" });
        provider.Update();
        Assert.False(provider.IsConnected);
    }

    [Fact]
    public void EmptyAppId_WithNoOptions_DoesNotConnect()
    {
        var transport = new FakeDiscordIpcTransport();
        using var provider = new DiscordSocialProvider(transport);
        Assert.False(provider.TryInitialize(string.Empty));
        Assert.False(provider.IsConnected);
    }

    [Fact]
    public void TryInitialize_IsReAttemptable_OnTheSameInstance_AfterAFailedConnect()
    {
        // SocialPresenceController retries a failed connect on the instance it already has (#158), so a
        // provider that could only ever be initialized once would strand the whole retry path.
        var transport = new FakeDiscordIpcTransport { ConnectResult = false };
        using var provider = new DiscordSocialProvider(transport);
        Assert.False(provider.TryInitialize("app-1"));

        transport.ConnectResult = true;
        Assert.True(provider.TryInitialize("app-1"));
        Assert.True(provider.IsConnected);

        provider.SetPresence(new RichPresence { Details = "In Game" });
        Assert.True(transport.TryReadLastWrittenFrame(out _, out string json));
        Assert.Contains("SET_ACTIVITY", json);
    }

    [Fact]
    public void Reconnect_DropsTheIdentityFromThePreviousConnection()
    {
        var transport = new FakeDiscordIpcTransport();
        using var provider = new DiscordSocialProvider(transport);
        provider.TryInitialize("app-1");
        transport.EnqueueFrame(DiscordIpcOpcode.Frame,
            """{"cmd":"DISPATCH","evt":"READY","data":{"user":{"id":"5","username":"kiwi","global_name":null}}}""");
        provider.Update();
        Assert.True(provider.TryGetLocalUser(out _));

        // A second connect is a new session: the old READY's user is not this connection's user.
        Assert.True(provider.TryInitialize("app-1"));
        Assert.False(provider.TryGetLocalUser(out _));
    }
}
