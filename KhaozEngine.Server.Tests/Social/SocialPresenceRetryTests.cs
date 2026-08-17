using System;
using System.Collections.Generic;
using KhaozEngine.Social;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// The connect-retry contract (#158): a platform client that is not up when the game launches must not
/// cost the player presence for the whole session. Everything here runs on a fake clock, so the schedule
/// is asserted exactly rather than slept through.
/// </summary>
public class SocialPresenceRetryTests
{
    private static SocialPresenceOptions Options(int maxAttempts, double retrySeconds = 3) => new()
    {
        ConnectRetryDelay = TimeSpan.FromSeconds(retrySeconds),
        MaxConnectAttempts = maxAttempts,
    };

    [Fact]
    public void FailsTwiceThenConnects_AndPublishesThePresenceHeldWhileConnecting()
    {
        var clock = new StoppedClock();
        var fake = new FakeSocialProvider { FailInitializeCount = 2 };
        using var controller = new SocialPresenceController(fake, Options(maxAttempts: 8), clock.Now);

        controller.Initialize();
        Assert.Equal(SocialPresenceState.Connecting, controller.State);
        Assert.False(controller.IsEnabled);

        // The game sets presence long before the connect lands. It must not be lost.
        controller.SetPresence(new RichPresence { Details = "In Menu", State = "Idle" });
        Assert.Empty(fake.PresenceCalls);

        clock.Advance(TimeSpan.FromSeconds(3));
        controller.Update();
        Assert.Equal(2, fake.InitializedWith.Count);
        Assert.Equal(SocialPresenceState.Connecting, controller.State);

        clock.Advance(TimeSpan.FromSeconds(6));
        controller.Update();
        Assert.Equal(3, fake.InitializedWith.Count);
        Assert.Equal(SocialPresenceState.Connected, controller.State);
        Assert.True(controller.IsEnabled);

        RichPresence sent = Assert.Single(fake.PresenceCalls);
        Assert.Equal("In Menu", sent.Details);
        Assert.Equal("Idle", sent.State);

        // Applied ONCE, and the dedupe cache was primed by it, so re-sending the same content is quiet.
        controller.SetPresence(new RichPresence { Details = "In Menu", State = "Idle" });
        Assert.Single(fake.PresenceCalls);
    }

    [Fact]
    public void OnlyTheLatestHeldPresenceIsPublished_NotAQueueOfEveryOne()
    {
        var clock = new StoppedClock();
        var fake = new FakeSocialProvider { FailInitializeCount = 1 };
        using var controller = new SocialPresenceController(fake, Options(maxAttempts: 8), clock.Now);
        controller.Initialize();

        controller.SetPresence(new RichPresence { Details = "In Menu" });
        controller.SetPresence(new RichPresence { Details = "Loading" });
        controller.SetPresence(new RichPresence { Details = "In Game" });

        clock.Advance(TimeSpan.FromSeconds(3));
        controller.Update();

        RichPresence sent = Assert.Single(fake.PresenceCalls);
        Assert.Equal("In Game", sent.Details);
    }

    [Fact]
    public void HeldElapsedPresence_KeepsItsAbsoluteStart_AcrossTheWait()
    {
        var clock = new StoppedClock();
        var fake = new FakeSocialProvider { FailInitializeCount = 1 };
        using var controller = new SocialPresenceController(fake, Options(maxAttempts: 8), clock.Now);
        controller.Initialize();

        DateTime setAt = clock.UtcNow;
        controller.SetElapsedPresence(new RichPresence { Details = "In Game" }, TimeSpan.FromMinutes(5));

        clock.Advance(TimeSpan.FromSeconds(3));
        controller.Update();

        RichPresence sent = Assert.Single(fake.PresenceCalls);
        // The run started five minutes before the SetElapsedPresence call, not before the connect.
        Assert.Equal(setAt.AddMinutes(-5), sent.StartTimestampUtc);
    }

    [Fact]
    public void FailingForever_GivesUp_ThenStopsCallingTheProvider_AndRetryReArms()
    {
        var clock = new StoppedClock();
        var fake = new FakeSocialProvider { InitializeResult = false };
        using var controller = new SocialPresenceController(fake, Options(maxAttempts: 3, retrySeconds: 1), clock.Now);

        controller.Initialize();
        clock.Advance(TimeSpan.FromSeconds(1));
        controller.Update();
        clock.Advance(TimeSpan.FromSeconds(2));
        controller.Update();

        Assert.Equal(SocialPresenceState.GivenUp, controller.State);
        Assert.Equal(3, fake.InitializedWith.Count);

        // Given up means given up: no attempt, and no pump either, however long the game runs.
        for (int i = 0; i < 10; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            controller.Update();
        }

        Assert.Equal(3, fake.InitializedWith.Count);
        Assert.Equal(0, fake.UpdateCalls);

        // Retry() re-arms the whole schedule from scratch.
        controller.Retry();
        Assert.Equal(4, fake.InitializedWith.Count);
        Assert.Equal(SocialPresenceState.Connecting, controller.State);

        fake.InitializeResult = true;
        clock.Advance(TimeSpan.FromSeconds(1));
        controller.Update();
        Assert.Equal(SocialPresenceState.Connected, controller.State);
    }

    [Fact]
    public void ThrowingTryInitialize_IsAFailedAttempt_AndNeverEscapesUpdate()
    {
        var clock = new StoppedClock();
        var fake = new FakeSocialProvider { ThrowOnInitialize = true };
        using var controller = new SocialPresenceController(fake, Options(maxAttempts: 8), clock.Now);

        Assert.Null(Record.Exception(() => controller.Initialize()));
        Assert.Equal(SocialPresenceState.Connecting, controller.State);

        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Null(Record.Exception(() => controller.Update()));
        Assert.Equal(2, fake.InitializedWith.Count);
        Assert.Equal(SocialPresenceState.Connecting, controller.State);

        // A throwing attempt is not terminal: the provider survives it and can still connect later.
        Assert.Equal(0, fake.DisposeCalls);
        fake.ThrowOnInitialize = false;
        clock.Advance(TimeSpan.FromSeconds(6));
        controller.Update();
        Assert.Equal(SocialPresenceState.Connected, controller.State);
    }

    [Fact]
    public void NoAttemptBeforeTheScheduledInterval_AndTheWaitDoubles()
    {
        var clock = new StoppedClock();
        var fake = new FakeSocialProvider { InitializeResult = false };
        using var controller = new SocialPresenceController(fake, Options(maxAttempts: 8), clock.Now);

        controller.Initialize();
        Assert.Single(fake.InitializedWith);

        // Pumping without any time passing must not attempt anything.
        for (int i = 0; i < 5; i++)
        {
            controller.Update();
        }

        Assert.Single(fake.InitializedWith);

        clock.Advance(TimeSpan.FromSeconds(2.999));
        controller.Update();
        Assert.Single(fake.InitializedWith);

        clock.Advance(TimeSpan.FromSeconds(0.001));
        controller.Update();
        Assert.Equal(2, fake.InitializedWith.Count);

        // The second wait is the doubled one: 6s, not 3s.
        clock.Advance(TimeSpan.FromSeconds(5.999));
        controller.Update();
        Assert.Equal(2, fake.InitializedWith.Count);

        clock.Advance(TimeSpan.FromSeconds(0.001));
        controller.Update();
        Assert.Equal(3, fake.InitializedWith.Count);
    }

    [Fact]
    public void TheWaitStopsGrowingAtTheCap()
    {
        var clock = new StoppedClock();
        var fake = new FakeSocialProvider { InitializeResult = false };
        var options = new SocialPresenceOptions
        {
            ConnectRetryDelay = TimeSpan.FromSeconds(4),
            MaxConnectRetryDelay = TimeSpan.FromSeconds(10),
            MaxConnectAttempts = 8,
        };
        using var controller = new SocialPresenceController(fake, options, clock.Now);

        controller.Initialize();                                    // waits 4s
        clock.Advance(TimeSpan.FromSeconds(4));
        controller.Update();                                        // waits 8s
        clock.Advance(TimeSpan.FromSeconds(8));
        controller.Update();                                        // would be 16s, capped to 10s

        Assert.Equal(3, fake.InitializedWith.Count);
        clock.Advance(TimeSpan.FromSeconds(9.999));
        controller.Update();
        Assert.Equal(3, fake.InitializedWith.Count);

        clock.Advance(TimeSpan.FromSeconds(0.001));
        controller.Update();
        Assert.Equal(4, fake.InitializedWith.Count);
    }

    [Fact]
    public void DisposeMidRetry_StopsEverything()
    {
        var clock = new StoppedClock();
        var fake = new FakeSocialProvider { InitializeResult = false };
        var controller = new SocialPresenceController(fake, Options(maxAttempts: 8), clock.Now);

        controller.Initialize();
        Assert.Equal(SocialPresenceState.Connecting, controller.State);

        controller.Dispose();
        Assert.Equal(SocialPresenceState.Disposed, controller.State);
        Assert.Equal(1, fake.DisposeCalls);

        clock.Advance(TimeSpan.FromMinutes(10));
        controller.Update();
        controller.Retry();
        controller.SetPresence(new RichPresence { Details = "In Game" });
        controller.Update();

        Assert.Single(fake.InitializedWith);
        Assert.Equal(0, fake.UpdateCalls);
        Assert.Empty(fake.PresenceCalls);
        Assert.Equal(1, fake.DisposeCalls);

        controller.Dispose();
        Assert.Equal(1, fake.DisposeCalls);
    }

    [Fact]
    public void NoBackend_GoesStraightToDisabled_AndNeverTicksTheBackoff()
    {
        var clock = new StoppedClock();
        using var controller = new SocialPresenceController(clock: clock.Now);

        controller.Initialize();
        Assert.Equal(SocialPresenceState.Disabled, controller.State);

        // An opted-out game pays nothing per frame: not one clock read, let alone a connect attempt.
        int readsBefore = clock.Reads;
        for (int i = 0; i < 100; i++)
        {
            controller.Update();
            controller.SetPresence(new RichPresence { Details = "In Game" });
            controller.SetElapsedPresence(new RichPresence { Details = "In Game" }, TimeSpan.FromMinutes(1));
            controller.ClearPresence();
        }

        Assert.Equal(readsBefore, clock.Reads);
        Assert.Equal(SocialPresenceState.Disabled, controller.State);
    }

    [Fact]
    public void MaxConnectAttemptsOfOne_IsTheOldOneShotBehaviour()
    {
        var clock = new StoppedClock();
        var fake = new FakeSocialProvider { InitializeResult = false };
        using var controller = new SocialPresenceController(fake, Options(maxAttempts: 1), clock.Now);

        controller.Initialize();
        Assert.Equal(SocialPresenceState.GivenUp, controller.State);
        Assert.False(controller.IsEnabled);

        clock.Advance(TimeSpan.FromHours(1));
        controller.Update();
        Assert.Single(fake.InitializedWith);
    }

    [Fact]
    public void StateChanged_ReportsEveryTransition()
    {
        var clock = new StoppedClock();
        var fake = new FakeSocialProvider { FailInitializeCount = 2 };
        var seen = new List<SocialPresenceState>();
        var controller = new SocialPresenceController(fake, Options(maxAttempts: 8), clock.Now);
        controller.StateChanged += s => seen.Add(s);

        controller.Initialize();
        clock.Advance(TimeSpan.FromSeconds(3));
        controller.Update();                    // still Connecting: a repeat is not a transition
        clock.Advance(TimeSpan.FromSeconds(6));
        controller.Update();                    // Connected
        controller.Dispose();

        Assert.Equal(
            new[] { SocialPresenceState.Connecting, SocialPresenceState.Connected, SocialPresenceState.Disposed },
            seen);
    }

    [Fact]
    public void ThrowingStateChangedHandler_IsContained()
    {
        var clock = new StoppedClock();
        var fake = new FakeSocialProvider { FailInitializeCount = 1 };
        using var controller = new SocialPresenceController(fake, Options(maxAttempts: 8), clock.Now);
        controller.StateChanged += _ => throw new InvalidOperationException("bad game handler");

        Assert.Null(Record.Exception(() => controller.Initialize()));
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Null(Record.Exception(() => controller.Update()));
        Assert.Equal(SocialPresenceState.Connected, controller.State);
    }

    [Fact]
    public void ClearPresence_WhileConnecting_CancelsTheHeldPresence()
    {
        var clock = new StoppedClock();
        var fake = new FakeSocialProvider { FailInitializeCount = 1 };
        using var controller = new SocialPresenceController(fake, Options(maxAttempts: 8), clock.Now);
        controller.Initialize();

        controller.SetPresence(new RichPresence { Details = "In Menu" });
        controller.ClearPresence();

        clock.Advance(TimeSpan.FromSeconds(3));
        controller.Update();

        Assert.Equal(SocialPresenceState.Connected, controller.State);
        Assert.Empty(fake.PresenceCalls);
        Assert.Equal(0, fake.ClearCalls);   // nothing was ever published, so nothing needed clearing
    }

    [Fact]
    public void MidSessionProviderFailure_StaysTerminal_AndRetryDoesNotRevive()
    {
        var clock = new StoppedClock();
        var fake = new FakeSocialProvider { ThrowOnUpdate = true };
        using var controller = new SocialPresenceController(fake, Options(maxAttempts: 8), clock.Now);
        controller.Initialize();
        Assert.Equal(SocialPresenceState.Connected, controller.State);

        controller.Update();
        Assert.Equal(SocialPresenceState.Disabled, controller.State);
        Assert.Equal(1, fake.DisposeCalls);

        // A dead transport is not a cold start: the provider is gone, so nothing re-attempts it.
        controller.Retry();
        clock.Advance(TimeSpan.FromMinutes(5));
        controller.Update();
        Assert.Single(fake.InitializedWith);
        Assert.Equal(SocialPresenceState.Disabled, controller.State);
    }

    /// <summary>A clock that only moves when the test says so, and counts how often it was read.</summary>
    private sealed class StoppedClock
    {
        private DateTimeOffset now = new(2026, 8, 17, 9, 0, 0, TimeSpan.Zero);

        public int Reads { get; private set; }

        public DateTime UtcNow => now.UtcDateTime;

        public Func<DateTimeOffset> Now => () =>
        {
            Reads++;
            return now;
        };

        public void Advance(TimeSpan by) => now += by;
    }
}
