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
    public void APresenceSetFromTheConnectedHandler_BeatsTheOneHeldWhileConnecting()
    {
        var clock = new StoppedClock();
        var fake = new FakeSocialProvider { FailInitializeCount = 1 };
        using var controller = new SocialPresenceController(fake, Options(maxAttempts: 8), clock.Now);

        // The documented pattern: publish the real line the moment the platform is up.
        controller.StateChanged += s =>
        {
            if (s == SocialPresenceState.Connected)
            {
                controller.SetPresence(new RichPresence { Details = "In Game" });
            }
        };

        controller.Initialize();
        controller.SetPresence(new RichPresence { Details = "In Menu" });    // held for the connect

        clock.Advance(TimeSpan.FromSeconds(3));
        controller.Update();

        // The hold publishes first, so what the platform is left showing is the handler's line, not the
        // menu line the game had already moved past.
        Assert.Equal(2, fake.PresenceCalls.Count);
        Assert.Equal("In Menu", fake.PresenceCalls[0].Details);
        Assert.Equal("In Game", fake.PresenceCalls[1].Details);

        // Nothing republishes the stale line for the rest of the session either.
        for (int i = 0; i < 200; i++)
        {
            controller.Update();
        }

        Assert.Equal(2, fake.PresenceCalls.Count);

        // And the dedupe cache was primed with the handler's line, not the held one: re-sending it is quiet,
        // which is only true if "In Game" is what the controller believes is currently published.
        controller.SetPresence(new RichPresence { Details = "In Game" });
        Assert.Equal(2, fake.PresenceCalls.Count);
    }

    [Fact]
    public void AConnectRetryDelayOfMaxValue_IsClamped_NotThrownOutOfInitialize()
    {
        var clock = new StoppedClock();
        var fake = new FakeSocialProvider { InitializeResult = false };
        var options = new SocialPresenceOptions
        {
            ConnectRetryDelay = TimeSpan.MaxValue,       // the natural spelling of "wait forever"
            MaxConnectAttempts = 8,
        };
        using var controller = new SocialPresenceController(fake, options, clock.Now);

        Assert.Null(Record.Exception(() => controller.Initialize()));
        Assert.Equal(SocialPresenceState.Connecting, controller.State);

        // Clamped to the one-day ceiling, so the schedule still runs instead of parking past the end of time.
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Null(Record.Exception(() => controller.Update()));
        Assert.Equal(2, fake.InitializedWith.Count);
    }

    [Fact]
    public void AConnectRetryDelayOfMillionsOfDays_IsClamped_NotThrownOutOfInitialize()
    {
        var clock = new StoppedClock();
        var fake = new FakeSocialProvider { InitializeResult = false };
        var options = new SocialPresenceOptions
        {
            ConnectRetryDelay = TimeSpan.FromDays(3_000_000),   // finite, and still past the end of DateTime
            MaxConnectAttempts = 8,
        };
        using var controller = new SocialPresenceController(fake, options, clock.Now);

        Assert.Null(Record.Exception(() => controller.Initialize()));
        Assert.Equal(SocialPresenceState.Connecting, controller.State);

        clock.Advance(TimeSpan.FromDays(1));
        Assert.Null(Record.Exception(() => controller.Update()));
        Assert.Equal(2, fake.InitializedWith.Count);
    }

    [Fact]
    public void AnAbsurdElapsedSpan_DoesNotThrow_OnEitherPath()
    {
        // #657: the same underflow the retry delay had, one member over. Both the connected path and the held
        // (still connecting) path derive a start instant from UtcNow - elapsed, so both saturate instead of throw.
        var clock = new StoppedClock();
        var connecting = new FakeSocialProvider { FailInitializeCount = 1 };
        using var held = new SocialPresenceController(connecting, Options(maxAttempts: 8), clock.Now);
        held.Initialize();
        Assert.Equal(SocialPresenceState.Connecting, held.State);
        Assert.Null(Record.Exception(() =>
            held.SetElapsedPresence(new RichPresence { Details = "In Game" }, TimeSpan.MaxValue)));

        var connectedFake = new FakeSocialProvider();
        using var live = new SocialPresenceController(connectedFake, Options(maxAttempts: 8), clock.Now);
        live.Initialize();
        Assert.Equal(SocialPresenceState.Connected, live.State);
        Assert.Null(Record.Exception(() =>
            live.SetElapsedPresence(new RichPresence { Details = "In Game" }, TimeSpan.MaxValue)));
        RichPresence sent = Assert.Single(connectedFake.PresenceCalls);
        Assert.Equal(DateTime.MinValue, sent.StartTimestampUtc);

        // A negative span still starts now, as before.
        live.SetElapsedPresence(new RichPresence { Details = "Lobby" }, TimeSpan.FromMinutes(-5));
        Assert.Equal(clock.UtcNow, connectedFake.PresenceCalls[^1].StartTimestampUtc);
    }

    [Fact]
    public void AnUncappedMaxRetryDelay_DoesNotOverflowTheSecondAttempt()
    {
        var clock = new StoppedClock();
        var fake = new FakeSocialProvider { InitializeResult = false };
        var options = new SocialPresenceOptions
        {
            ConnectRetryDelay = TimeSpan.FromDays(2_000_000),   // fits the first schedule, doubles past the end
            MaxConnectRetryDelay = TimeSpan.MaxValue,           // the natural spelling of "no cap"
            MaxConnectAttempts = 8,
        };
        using var controller = new SocialPresenceController(fake, options, clock.Now);

        Assert.Null(Record.Exception(() => controller.Initialize()));

        // The unclamped growth overflowed here, out of Update() and into the game loop.
        clock.Advance(TimeSpan.FromDays(2_000_000));
        Assert.Null(Record.Exception(() => controller.Update()));
        Assert.Equal(2, fake.InitializedWith.Count);
        Assert.Equal(SocialPresenceState.Connecting, controller.State);
    }

    [Fact]
    public void ConnectedUpdate_NeverReadsTheClock()
    {
        var clock = new StoppedClock();
        var fake = new FakeSocialProvider();
        using var controller = new SocialPresenceController(fake, Options(maxAttempts: 8), clock.Now);

        controller.Initialize();
        Assert.Equal(SocialPresenceState.Connected, controller.State);

        // A connected session pumps the provider and nothing else: the backoff schedule is gone, so the
        // per-frame cost of the controller is one virtual call, not a clock read.
        int readsBefore = clock.Reads;
        for (int i = 0; i < 1000; i++)
        {
            controller.Update();
        }

        Assert.Equal(readsBefore, clock.Reads);
        Assert.Equal(1000, fake.UpdateCalls);
    }

    [Fact]
    public void GivenUpUpdate_NeverReadsTheClock()
    {
        var clock = new StoppedClock();
        var fake = new FakeSocialProvider { InitializeResult = false };
        using var controller = new SocialPresenceController(fake, Options(maxAttempts: 1), clock.Now);

        controller.Initialize();
        Assert.Equal(SocialPresenceState.GivenUp, controller.State);

        // Given up costs nothing per frame either, so a machine with no platform client is not paying for
        // one for the rest of the session.
        int readsBefore = clock.Reads;
        for (int i = 0; i < 1000; i++)
        {
            controller.Update();
        }

        Assert.Equal(readsBefore, clock.Reads);
        Assert.Equal(0, fake.UpdateCalls);
        Assert.Single(fake.InitializedWith);
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
    public void AutoRetryFromTheGivenUpHandler_GetsOneMoreAttempt_AndNoSecondGivenUp()
    {
        var clock = new StoppedClock();
        var fake = new FakeSocialProvider { InitializeResult = false };
        var seen = new List<SocialPresenceState>();
        using var controller = new SocialPresenceController(fake, Options(maxAttempts: 1), clock.Now);
        controller.StateChanged += s =>
        {
            seen.Add(s);
            if (s == SocialPresenceState.GivenUp)
            {
                controller.Retry();
            }
        };

        controller.Initialize();

        // Retry() runs its attempt synchronously inside the event, while the state is still GivenUp, so the
        // repeat transition is deduped and the handler is never re-entered. One extra attempt, no loop.
        Assert.Equal(2, fake.InitializedWith.Count);
        Assert.Equal(new[] { SocialPresenceState.GivenUp }, seen);
        Assert.Equal(SocialPresenceState.GivenUp, controller.State);
    }

    [Fact]
    public void AutoRetryFromTheGivenUpHandler_ReArmsTheWholeScheduleWhenThereIsABudgetToReArm()
    {
        var clock = new StoppedClock();
        var fake = new FakeSocialProvider { InitializeResult = false };
        var seen = new List<SocialPresenceState>();
        using var controller = new SocialPresenceController(fake, Options(maxAttempts: 3, retrySeconds: 1), clock.Now);
        controller.StateChanged += s =>
        {
            seen.Add(s);
            if (s == SocialPresenceState.GivenUp)
            {
                controller.Retry();
            }
        };

        controller.Initialize();
        clock.Advance(TimeSpan.FromSeconds(1));
        controller.Update();
        clock.Advance(TimeSpan.FromSeconds(2));
        controller.Update();                    // third attempt gives up, and the handler re-arms from there

        // The forced attempt has a fresh budget, so it lands in Connecting rather than back in GivenUp: an
        // auto-retry handler is a reconnect loop, not a single extra try.
        Assert.Equal(4, fake.InitializedWith.Count);
        Assert.Equal(SocialPresenceState.Connecting, controller.State);
        Assert.Equal(
            new[]
            {
                SocialPresenceState.Connecting,
                SocialPresenceState.GivenUp,
                SocialPresenceState.Connecting,
            },
            seen);
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
}
