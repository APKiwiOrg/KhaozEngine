using KhaozEngine.Social;
using KhaozEngine.Social.Discord;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// Exercises the real Discord socket. Requires a running Discord client and a valid Application id, so
/// it is tagged LiveSocket and excluded from CI (`--filter "Category!=LiveSocket"`). Run manually with
/// a real app id to smoke-test presence on a dev machine.
/// </summary>
[Trait("Category", "LiveSocket")]
public class DiscordLiveSocketTests
{
    [Fact]
    public void ConnectsToLocalDiscordAndSetsPresence()
    {
        using var provider = new DiscordSocialProvider(new DiscordSocialOptions { ApplicationId = "1478493292369936527" });
        bool connected = provider.TryInitialize(string.Empty);
        if (!connected)
        {
            return; // No Discord running: treat as inconclusive, not a failure.
        }

        provider.SetPresence(new RichPresence { Details = "KhaozEngine live test", State = "Running" });
        for (int i = 0; i < 20; i++)
        {
            provider.Update();
            System.Threading.Thread.Sleep(50);
        }

        Assert.True(provider.IsConnected);
    }
}
