using System;
using KhaozEngine.Social;
using Xunit;

namespace KhaozEngine.Tests;

public class SocialPresenceControllerTests
{
    private static SocialPresenceController Make(FakeSocialProvider fake, out FakeSocialProvider provider)
    {
        provider = fake;
        var options = new SocialPresenceOptions { RepublishInterval = TimeSpan.FromSeconds(15) };
        var controller = new SocialPresenceController(fake, options);
        controller.Initialize();
        return controller;
    }

    [Fact]
    public void Initialize_ForwardsToProvider()
    {
        var fake = new FakeSocialProvider();
        using var controller = Make(fake, out _);
        Assert.True(controller.IsEnabled);
        Assert.Single(fake.InitializedWith);
    }

    [Fact]
    public void FailedInitialize_DisablesController()
    {
        var fake = new FakeSocialProvider { InitializeResult = false };
        using var controller = new SocialPresenceController(fake);
        controller.Initialize();
        Assert.False(controller.IsEnabled);
        controller.SetPresence(new RichPresence { Details = "a" });
        Assert.Empty(fake.PresenceCalls);
    }

    [Fact]
    public void SetPresence_DedupesIdenticalContent()
    {
        var fake = new FakeSocialProvider();
        using var controller = Make(fake, out _);
        var p = new RichPresence { Details = "In Menu", State = "Idle" };
        controller.SetPresence(p);
        controller.SetPresence(p);
        Assert.Single(fake.PresenceCalls);
    }

    [Fact]
    public void SetPresence_ResendsWhenContentChanges()
    {
        var fake = new FakeSocialProvider();
        using var controller = Make(fake, out _);
        controller.SetPresence(new RichPresence { Details = "In Menu", State = "Idle" });
        controller.SetPresence(new RichPresence { Details = "In Game", State = "Fighting" });
        Assert.Equal(2, fake.PresenceCalls.Count);
    }

    [Fact]
    public void Force_ResendsIdenticalContent()
    {
        var fake = new FakeSocialProvider();
        using var controller = Make(fake, out _);
        var p = new RichPresence { Details = "In Game" };
        controller.SetPresence(p);
        controller.SetPresence(p, force: true);
        Assert.Equal(2, fake.PresenceCalls.Count);
    }

    [Fact]
    public void SetElapsedPresence_SetsStartTimestampFromElapsed()
    {
        var fake = new FakeSocialProvider();
        using var controller = Make(fake, out _);
        DateTime before = DateTime.UtcNow;
        controller.SetElapsedPresence(new RichPresence { Details = "In Game" }, TimeSpan.FromSeconds(60));
        DateTime after = DateTime.UtcNow;

        RichPresence sent = Assert.Single(fake.PresenceCalls);
        Assert.NotNull(sent.StartTimestampUtc);
        // start ~= now - 60s, within the wall-clock window of the call.
        Assert.InRange(sent.StartTimestampUtc!.Value,
            before.AddSeconds(-61), after.AddSeconds(-59));
    }

    [Fact]
    public void ProviderThrow_DisablesSessionAndDisposesProvider()
    {
        var fake = new FakeSocialProvider { ThrowOnSetPresence = true };
        using var controller = Make(fake, out _);
        controller.SetPresence(new RichPresence { Details = "boom" });
        Assert.False(controller.IsEnabled);
        Assert.Equal(1, fake.DisposeCalls);
        // Subsequent calls are silent no-ops.
        controller.SetPresence(new RichPresence { Details = "again" });
        controller.Update();
    }

    [Fact]
    public void JoinRequested_ForwardsThroughController()
    {
        var fake = new FakeSocialProvider();
        using var controller = Make(fake, out _);
        string? got = null;
        controller.JoinRequested += s => got = s;
        fake.RaiseJoinRequested("secret-123");
        Assert.Equal("secret-123", got);
    }

    [Fact]
    public void TryGetLocalUser_PassesThrough()
    {
        var fake = new FakeSocialProvider { LocalUser = new SocialUser("1", "kiwi", null) };
        using var controller = Make(fake, out _);
        Assert.True(controller.TryGetLocalUser(out SocialUser user));
        Assert.Equal("kiwi", user.Username);
    }

    [Fact]
    public void SetPresence_DedupesEqualButtonLists_FromDifferentInstances()
    {
        var fake = new FakeSocialProvider();
        using var controller = Make(fake, out _);
        var p1 = new RichPresence { Details = "In Game", Buttons = new[] { new PresenceButton("Site", "https://x") } };
        var p2 = new RichPresence { Details = "In Game", Buttons = new[] { new PresenceButton("Site", "https://x") } };

        Assert.Equal(p1, p2); // structural equality despite distinct list instances
        controller.SetPresence(p1);
        controller.SetPresence(p2);
        Assert.Single(fake.PresenceCalls);
    }

    [Fact]
    public void ThrowingJoinHandler_IsContained_AndSessionStaysEnabled()
    {
        var fake = new FakeSocialProvider();
        using var controller = Make(fake, out _);
        controller.JoinRequested += _ => throw new InvalidOperationException("bad game handler");

        Exception? escaped = Record.Exception(() => fake.RaiseJoinRequested("secret"));

        Assert.Null(escaped);            // the throw never escapes the controller
        Assert.True(controller.IsEnabled); // a bad game callback does not disable the session
    }
}
