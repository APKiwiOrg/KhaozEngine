using System;
using System.Collections.Generic;
using KhaozEngine.Social;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// The mid-session drop contract (#655): a player who quits Discord after the game connected must not leave
/// the controller sitting in <see cref="SocialPresenceState.Connected"/> publishing into a client that is
/// gone. #158 covers the other half, the client that was never up. Everything here runs on a fake clock, so
/// the reconnect schedule is asserted exactly rather than slept through.
/// </summary>
public class SocialPresenceReconnectTests
{
    private static SocialPresenceOptions Options(int maxAttempts, double retrySeconds = 3) => new()
    {
        ConnectRetryDelay = TimeSpan.FromSeconds(retrySeconds),
        MaxConnectAttempts = maxAttempts,
    };

    [Fact]
    public void ADroppedConnection_ReEntersTheBackoff_AndRepublishesTheLastPresenceOnTheWayBack()
    {
        var clock = new StoppedClock();
        var fake = new FakeSocialProvider();
        var seen = new List<SocialPresenceState>();
        using var controller = new SocialPresenceController(fake, Options(maxAttempts: 8), clock.Now);
        controller.StateChanged += s => seen.Add(s);

        controller.Initialize();
        controller.SetPresence(new RichPresence { Details = "In Game", State = "Boss Rush" });
        Assert.Equal(SocialPresenceState.Connected, controller.State);
        Assert.Single(fake.PresenceCalls);

        // The player quits Discord. The provider reports it the way the seam says to: false from IsConnected.
        fake.ConnectedResult = false;
        controller.Update();

        Assert.Equal(SocialPresenceState.Reconnecting, controller.State);
        Assert.False(controller.IsEnabled);
        Assert.Equal(new[] { SocialPresenceState.Connected, SocialPresenceState.Reconnecting }, seen);

        // The provider is kept, not disposed: disposing it is what made the old terminal path unrecoverable.
        Assert.Equal(0, fake.DisposeCalls);

        // Nothing is published into the dead client, and the provider is not even pumped while it is down.
        int pumpsAtDrop = fake.UpdateCalls;
        controller.Update();
        controller.Update();
        Assert.Equal(pumpsAtDrop, fake.UpdateCalls);
        Assert.Single(fake.PresenceCalls);

        // No reconnect attempt before the initial delay is up, exactly like a cold start.
        clock.Advance(TimeSpan.FromSeconds(2.999));
        controller.Update();
        Assert.Single(fake.InitializedWith);

        // Discord is back.
        fake.ConnectedResult = true;
        clock.Advance(TimeSpan.FromSeconds(0.001));
        controller.Update();

        Assert.Equal(2, fake.InitializedWith.Count);
        Assert.Equal(SocialPresenceState.Connected, controller.State);
        Assert.Equal(
            new[] { SocialPresenceState.Connected, SocialPresenceState.Reconnecting, SocialPresenceState.Connected },
            seen);

        // The line that was live at the drop is back on screen, published once.
        Assert.Equal(2, fake.PresenceCalls.Count);
        Assert.Equal("In Game", fake.PresenceCalls[1].Details);
        Assert.Equal("Boss Rush", fake.PresenceCalls[1].State);

        // And the dedupe cache believes that line is what is published, so the game re-sending it is quiet.
        controller.SetPresence(new RichPresence { Details = "In Game", State = "Boss Rush" });
        Assert.Equal(2, fake.PresenceCalls.Count);
    }

    [Fact]
    public void APresenceSetDuringTheOutage_IsWhatComesBack_NotTheOneLiveAtTheDrop()
    {
        var clock = new StoppedClock();
        var fake = new FakeSocialProvider();
        using var controller = new SocialPresenceController(fake, Options(maxAttempts: 8), clock.Now);

        controller.Initialize();
        controller.SetPresence(new RichPresence { Details = "In Menu" });
        fake.ConnectedResult = false;
        controller.Update();
        Assert.Equal(SocialPresenceState.Reconnecting, controller.State);

        // The game keeps playing while Discord is gone, so what it wants shown has moved on.
        controller.SetPresence(new RichPresence { Details = "In Game" });
        controller.SetPresence(new RichPresence { Details = "Boss Fight" });
        Assert.Single(fake.PresenceCalls);

        fake.ConnectedResult = true;
        clock.Advance(TimeSpan.FromSeconds(3));
        controller.Update();

        Assert.Equal(SocialPresenceState.Connected, controller.State);
        Assert.Equal(2, fake.PresenceCalls.Count);
        Assert.Equal("Boss Fight", fake.PresenceCalls[1].Details);
    }

    [Fact]
    public void ADropThatNeverComesBack_GivesUp_AndRetryReArmsIntoReconnecting()
    {
        var clock = new StoppedClock();
        var fake = new FakeSocialProvider();
        using var controller = new SocialPresenceController(fake, Options(maxAttempts: 3, retrySeconds: 1), clock.Now);

        controller.Initialize();
        Assert.Equal(SocialPresenceState.Connected, controller.State);

        // A session that actually ran before Discord went away, so the drop that ends it is the real thing
        // rather than a flap, and is entitled to a budget of its own.
        clock.Advance(TimeSpan.FromSeconds(31));

        // Discord is gone for good: the socket is dead and nothing will connect to it again.
        fake.ConnectedResult = false;
        fake.InitializeResult = false;
        controller.Update();
        Assert.Equal(SocialPresenceState.Reconnecting, controller.State);

        // A fresh budget, so the drop gets the full three attempts rather than whatever the cold start left.
        clock.Advance(TimeSpan.FromSeconds(1));
        controller.Update();
        Assert.Equal(2, fake.InitializedWith.Count);
        Assert.Equal(SocialPresenceState.Reconnecting, controller.State);

        clock.Advance(TimeSpan.FromSeconds(2));
        controller.Update();
        Assert.Equal(3, fake.InitializedWith.Count);

        clock.Advance(TimeSpan.FromSeconds(4));
        controller.Update();
        Assert.Equal(4, fake.InitializedWith.Count);
        Assert.Equal(SocialPresenceState.GivenUp, controller.State);

        // Given up after a drop costs the same as given up from a cold start: nothing at all, per frame.
        int readsBefore = clock.Reads;
        for (int i = 0; i < 100; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            controller.Update();
        }

        Assert.Equal(readsBefore, clock.Reads);
        Assert.Equal(4, fake.InitializedWith.Count);

        // Retry() re-arms it, and it comes back as Reconnecting rather than Connecting: this session HAD
        // presence, so "lost it" is still the true thing to say about it.
        controller.Retry();
        Assert.Equal(5, fake.InitializedWith.Count);
        Assert.Equal(SocialPresenceState.Reconnecting, controller.State);

        fake.InitializeResult = true;
        fake.ConnectedResult = true;
        clock.Advance(TimeSpan.FromSeconds(1));
        controller.Update();
        Assert.Equal(SocialPresenceState.Connected, controller.State);
    }

    [Fact]
    public void AConnectionThatKeepsDroppingTheInstantItLands_RunsOutOfBudget_InsteadOfFlappingForever()
    {
        var clock = new StoppedClock();

        // TryInitialize keeps saying yes and the connection is dead again by the next probe. A real Discord
        // mid-restart does this: its handshake succeeds the moment the bytes are written, and the socket it
        // was written to is already going away.
        var fake = new FakeSocialProvider { ConnectedResult = false };
        using var controller = new SocialPresenceController(fake, Options(maxAttempts: 4, retrySeconds: 1), clock.Now);

        controller.Initialize();
        controller.SetPresence(new RichPresence { Details = "In Game" });

        // Ten simulated minutes of it. Every cycle connects, republishes and drops on one frame, so before
        // the flap guard this ran a status line flipping every few seconds and a SET_ACTIVITY per cycle for
        // as long as the game was open.
        for (int i = 0; i < 200; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(3));
            controller.Update();
        }

        // Four connects, four publishes, then nothing at all: the drop after the last one has no budget left
        // to reset, so it ends the cycle the way an unreachable platform client ends a cold start.
        Assert.Equal(SocialPresenceState.GivenUp, controller.State);
        Assert.Equal(4, fake.InitializedWith.Count);
        Assert.Equal(4, fake.PresenceCalls.Count);
    }

    [Fact]
    public void ASessionThatHeldBeforeItDropped_GetsAFreshBudget_NotTheColdStartsLeftovers()
    {
        var clock = new StoppedClock();
        var fake = new FakeSocialProvider();
        using var controller = new SocialPresenceController(fake, Options(maxAttempts: 2, retrySeconds: 1), clock.Now);

        controller.Initialize();
        Assert.Equal(SocialPresenceState.Connected, controller.State);

        // Past StableConnectionSpan, so this is a session ending rather than a flap.
        clock.Advance(TimeSpan.FromSeconds(31));
        fake.ConnectedResult = false;
        fake.InitializeResult = false;
        controller.Update();
        Assert.Equal(SocialPresenceState.Reconnecting, controller.State);

        // Both attempts of the budget are there to spend. Carrying the cold start's one forward would have
        // given up on this first one instead.
        clock.Advance(TimeSpan.FromSeconds(1));
        controller.Update();
        Assert.Equal(2, fake.InitializedWith.Count);
        Assert.Equal(SocialPresenceState.Reconnecting, controller.State);

        clock.Advance(TimeSpan.FromSeconds(2));
        controller.Update();
        Assert.Equal(3, fake.InitializedWith.Count);
        Assert.Equal(SocialPresenceState.GivenUp, controller.State);
    }

    [Fact]
    public void AClearDuringTheOutage_LeavesThePreDropLinePublishable_RatherThanDedupedAway()
    {
        var clock = new StoppedClock();
        var fake = new FakeSocialProvider();

        // The republish throttle is well past the whole test, so it cannot be what publishes the last line:
        // the only thing that decides it is what the dedupe cache believes is currently on the platform.
        var options = new SocialPresenceOptions
        {
            ConnectRetryDelay = TimeSpan.FromSeconds(3),
            MaxConnectAttempts = 8,
            RepublishInterval = TimeSpan.FromMinutes(5),
        };
        using var controller = new SocialPresenceController(fake, options, clock.Now);

        var line = new RichPresence { Details = "In Game", State = "Boss Rush" };
        controller.Initialize();
        controller.SetPresence(line);
        Assert.Single(fake.PresenceCalls);

        clock.Advance(TimeSpan.FromSeconds(31));
        fake.ConnectedResult = false;
        controller.Update();
        Assert.Equal(SocialPresenceState.Reconnecting, controller.State);

        // The game clears while the platform is down, so the reconnect deliberately comes back blank.
        controller.ClearPresence();

        fake.ConnectedResult = true;
        clock.Advance(TimeSpan.FromSeconds(3));
        controller.Update();
        Assert.Equal(SocialPresenceState.Connected, controller.State);
        Assert.Single(fake.PresenceCalls);

        // And the line the session was showing before the drop can go back up. The cache described a client
        // that no longer exists, so it is not allowed to swallow this: a change-driven publisher would
        // otherwise stay blank until it happened to set some different line.
        controller.SetPresence(line);
        Assert.Equal(2, fake.PresenceCalls.Count);
        Assert.Equal("Boss Rush", fake.PresenceCalls[1].State);
    }

    [Fact]
    public void ALiveConnection_CostsOneBoolPerFrame_AndStillNotOneClockRead()
    {
        var clock = new StoppedClock();
        var fake = new FakeSocialProvider();
        using var controller = new SocialPresenceController(fake, Options(maxAttempts: 8), clock.Now);

        controller.Initialize();
        Assert.Equal(SocialPresenceState.Connected, controller.State);

        int readsBefore = clock.Reads;
        int probesBefore = fake.IsConnectedReads;
        for (int i = 0; i < 1000; i++)
        {
            controller.Update();
        }

        // The drop probe runs every single frame (it is the whole detection mechanism), and costs exactly
        // one bool read on the seam. The clock is touched only once a drop is actually found.
        Assert.Equal(1000, fake.IsConnectedReads - probesBefore);
        Assert.Equal(readsBefore, clock.Reads);
        Assert.Equal(1000, fake.UpdateCalls);
    }

    [Fact]
    public void AThrowingConnectionProbe_IsStillTerminal_LikeEveryOtherThrow()
    {
        var clock = new StoppedClock();
        var fake = new FakeSocialProvider();
        using var controller = new SocialPresenceController(fake, Options(maxAttempts: 8), clock.Now);

        controller.Initialize();
        fake.ThrowOnIsConnected = true;

        // A drop is a clean false. A throw says the provider is in a state the seam cannot promise anything
        // about, so it ends the session as every other throwing member does, and never escapes Update().
        Assert.Null(Record.Exception(() => controller.Update()));
        Assert.Equal(SocialPresenceState.Disabled, controller.State);
        Assert.Equal(1, fake.DisposeCalls);
    }

    [Fact]
    public void DisposeWhileReconnecting_StopsEverything()
    {
        var clock = new StoppedClock();
        var fake = new FakeSocialProvider();
        var controller = new SocialPresenceController(fake, Options(maxAttempts: 8), clock.Now);

        controller.Initialize();
        fake.ConnectedResult = false;
        controller.Update();
        Assert.Equal(SocialPresenceState.Reconnecting, controller.State);
        Assert.Equal(0, fake.DisposeCalls);

        controller.Dispose();
        Assert.Equal(SocialPresenceState.Disposed, controller.State);
        Assert.Equal(1, fake.DisposeCalls);

        int pumps = fake.UpdateCalls;
        fake.ConnectedResult = true;
        clock.Advance(TimeSpan.FromMinutes(10));
        controller.Update();
        controller.Retry();
        controller.SetPresence(new RichPresence { Details = "In Game" });
        controller.Update();

        Assert.Single(fake.InitializedWith);
        Assert.Equal(pumps, fake.UpdateCalls);
        Assert.Empty(fake.PresenceCalls);
        Assert.Equal(1, fake.DisposeCalls);
    }
}
