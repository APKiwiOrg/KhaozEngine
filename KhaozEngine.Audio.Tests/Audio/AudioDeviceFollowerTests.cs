using System;
using System.Collections.Generic;
using KhaozEngine.Audio;
using KhaozEngine.Diagnostics;
using Silk.NET.OpenAL;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// The poll and reopen rules behind following the output device (#770), driven through a scripted device and a
/// hand-advanced clock. Before this the OpenAL device was opened once and never looked at again, so switching
/// speakers to headphones left the game on the speakers and a lost device stayed silent until restart.
/// </summary>
public sealed class AudioDeviceFollowerTests
{
    [Fact]
    public void NothingIsCheckedUntilAnIntervalHasPassed()
    {
        var device = new FakeAlcOutputDevice();
        var (follower, _) = NewFollower(device);

        follower.Poll(Seconds(0.5));
        Assert.Equal(0, device.ProbeCount);

        follower.Poll(Seconds(1));
        follower.Poll(Seconds(1.5));
        Assert.Equal(1, device.ProbeCount);

        follower.Poll(Seconds(2));
        Assert.Equal(2, device.ProbeCount);
    }

    [Fact]
    public void AConnectedDeviceOnTheSameDefaultIsLeftAlone()
    {
        var device = new FakeAlcOutputDevice();
        var (follower, sink) = NewFollower(device);

        // Ten seconds of frames at 60 Hz: one probe per second, nothing reopened, nothing logged.
        for (int frame = 1; frame <= 600; frame++)
            follower.Poll(Seconds(frame / 60.0));

        Assert.Equal(10, device.ProbeCount);
        Assert.Equal(0, device.ReopenCalls);
        Assert.Empty(sink.Entries);
    }

    [Fact]
    public void ADefaultChangeReopensOnceAndThenFollowsTheNewDefault()
    {
        var device = new FakeAlcOutputDevice();
        var (follower, sink) = NewFollower(device);
        follower.Poll(Seconds(1));

        device.DefaultDeviceName = "Headphones";
        follower.Poll(Seconds(2));

        Assert.Equal(1, device.ReopenCalls);
        Assert.Equal(1, follower.ReopenCount);
        var entry = Assert.Single(sink.Entries);
        Assert.Equal(LogLevel.Info, entry.Level);
        Assert.Contains("default output changed", entry.Message);
        Assert.Contains("Headphones", entry.Message);

        // Headphones are the known default now, so the next checks do not reopen again.
        follower.Poll(Seconds(3));
        follower.Poll(Seconds(4));
        Assert.Equal(1, device.ReopenCalls);
    }

    [Fact]
    public void ALostDeviceIsReopenedWhenTheDefaultDidNotMove()
    {
        var device = new FakeAlcOutputDevice();
        var (follower, sink) = NewFollower(device);

        device.IsConnected = false;
        follower.Poll(Seconds(1));

        Assert.Equal(1, follower.ReopenCount);
        Assert.Contains("was lost", Assert.Single(sink.Entries).Message);
    }

    [Fact]
    public void ADeviceOpenedByNameIgnoresTheDefaultButStillRecoversFromALoss()
    {
        var device = new FakeAlcOutputDevice();
        var (follower, _) = NewFollower(device, followsDefault: false);

        device.DefaultDeviceName = "Headphones";
        follower.Poll(Seconds(1));
        follower.Poll(Seconds(2));
        Assert.Equal(0, device.ProbeCount);
        Assert.Equal(0, device.ReopenCalls);

        device.IsConnected = false;
        follower.Poll(Seconds(3));
        Assert.Equal(1, follower.ReopenCount);
    }

    [Fact]
    public void AFailedReopenOfALostDeviceIsLoggedOnceAndRetriedEverySecond()
    {
        var device = new FakeAlcOutputDevice { IsConnected = false };
        for (int i = 0; i < 3; i++) device.ReopenFailures.Enqueue(ContextError.InvalidValue);
        var (follower, sink) = NewFollower(device);

        for (int second = 1; second <= 3; second++)
            follower.Poll(Seconds(second));
        Assert.Equal(3, device.ReopenCalls);
        Assert.Equal(3, follower.FailedReopenCount);
        var error = Assert.Single(sink.Entries);
        Assert.Equal(LogLevel.Error, error.Level);
        Assert.Contains("device reopen", error.Message);
        Assert.Contains("InvalidValue", error.Message);

        follower.Poll(Seconds(4));
        Assert.Equal(1, follower.ReopenCount);
        Assert.True(device.IsConnected);
    }

    [Fact]
    public void AFollowThatKeepsFailingBacksOffWhileTheOldOutputStillPlays()
    {
        var device = new FakeAlcOutputDevice { DefaultDeviceName = "Bluetooth" };
        for (int i = 0; i < 100; i++) device.ReopenFailures.Enqueue(ContextError.InvalidValue);
        var (follower, _) = NewFollower(device);

        var attempts = new List<int>();
        for (int second = 1; second <= 100; second++)
        {
            int before = device.ReopenCalls;
            follower.Poll(Seconds(second));
            if (device.ReopenCalls > before) attempts.Add(second);
        }

        // Each failure stops and restarts the output that still works, so the gap doubles up to 30 seconds.
        Assert.Equal(new[] { 1, 3, 7, 15, 31, 61, 91 }, attempts);
        Assert.Equal(100, device.ProbeCount);
    }

    [Fact]
    public void ALossDuringAFollowBackoffIsRetriedStraightAway()
    {
        var device = new FakeAlcOutputDevice { DefaultDeviceName = "Bluetooth" };
        device.ReopenFailures.Enqueue(ContextError.InvalidValue);
        var (follower, _) = NewFollower(device);

        follower.Poll(Seconds(1));   // fails, next follow attempt not before 3 s
        device.IsConnected = false;
        follower.Poll(Seconds(2));

        Assert.Equal(2, device.ReopenCalls);
        Assert.Equal(1, follower.ReopenCount);
    }

    [Fact]
    public void ADefaultThatMovesBackEndsTheRetriesAndResetsTheBackoff()
    {
        var device = new FakeAlcOutputDevice { DefaultDeviceName = "Bluetooth" };
        for (int i = 0; i < 5; i++) device.ReopenFailures.Enqueue(ContextError.InvalidValue);
        var (follower, _) = NewFollower(device);

        follower.Poll(Seconds(1));
        follower.Poll(Seconds(3));
        Assert.Equal(2, device.ReopenCalls);

        device.DefaultDeviceName = "Speakers";
        for (int second = 4; second <= 40; second++)
            follower.Poll(Seconds(second));
        Assert.Equal(2, device.ReopenCalls);

        // A fresh change is tried on the next check, not after the backoff the old one had built up.
        device.DefaultDeviceName = "Bluetooth";
        follower.Poll(Seconds(41));
        Assert.Equal(3, device.ReopenCalls);
    }

    [Fact]
    public void AProbeThatSeesNoDefaultIsNotAChange()
    {
        var device = new FakeAlcOutputDevice { DefaultDeviceName = null };
        var (follower, _) = NewFollower(device);

        for (int second = 1; second <= 5; second++)
            follower.Poll(Seconds(second));

        Assert.Equal(0, device.ReopenCalls);
    }

    [Fact]
    public void WithoutReopenSupportTheDeviceIsNeverPolled()
    {
        var device = new FakeAlcOutputDevice { CanReopen = false, IsConnected = false, DefaultDeviceName = "Headphones" };
        var (follower, sink) = NewFollower(device);

        for (int second = 1; second <= 5; second++)
            follower.Poll(Seconds(second));

        Assert.Equal(0, device.ProbeCount);
        Assert.Equal(0, device.ReopenCalls);
        var entry = Assert.Single(sink.Entries);
        Assert.Equal(LogLevel.Info, entry.Level);
        Assert.Contains("ALC_SOFT_reopen_device", entry.Message);
    }

    [Fact]
    public void AChangeRacingTheOpenCostsOneReopenRatherThanGoingUnnoticed()
    {
        // The default was probed as Speakers just before the device opened, and moved before the first check.
        var device = new FakeAlcOutputDevice { DefaultDeviceName = "Headphones" };
        var (follower, _) = NewFollower(device, defaultAtOpen: "Speakers");

        follower.Poll(Seconds(1));

        Assert.Equal(1, follower.ReopenCount);
        Assert.Equal("Headphones", device.CurrentDeviceName);
    }

    static TimeSpan Seconds(double seconds) => TimeSpan.FromSeconds(seconds);

    // Error and info lines land in one synchronous InMemorySink. A private LogManager touches no process-global
    // logging state, so this class needs no serial collection.
    static (AudioDeviceFollower Follower, InMemorySink Sink) NewFollower(
        FakeAlcOutputDevice device,
        bool followsDefault = true,
        string? defaultAtOpen = "Speakers")
    {
        var sink = new InMemorySink();
        var options = new LoggerOptions { Synchronous = true, MinimumLevel = LogLevel.Trace };
        options.Sinks.Add(sink);
        ILogger logger = new LogManager(options).GetLogger("test");
        var follower = new AudioDeviceFollower(
            device, followsDefault, defaultAtOpen, new AlErrorLog(logger), logger, TimeSpan.Zero);
        return (follower, sink);
    }
}
