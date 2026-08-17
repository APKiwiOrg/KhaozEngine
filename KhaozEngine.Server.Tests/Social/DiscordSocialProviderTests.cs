using KhaozEngine.Social;
using KhaozEngine.Social.Discord;
using KhaozEngine.Social.Discord.Internal;
using Xunit;

namespace KhaozEngine.Tests;

public class DiscordSocialProviderTests
{
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
