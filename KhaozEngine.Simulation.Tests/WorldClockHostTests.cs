using System;
using System.Collections.Generic;
using KhaozEngine.Simulation;
using Xunit;

namespace KhaozEngine.Tests.Simulation;

public class WorldClockHostTests
{
    private const float LongBroadcastIntervalSeconds = 100000f;

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(0f)]
    [InlineData(-1f)]
    public void Constructor_InvalidBroadcastIntervalThrows(float invalidValue)
    {
        WorldClockHostOptions options = new() { BroadcastIntervalSeconds = invalidValue };

        Assert.Throws<ArgumentOutOfRangeException>(() => CreateHost(new WorldClock(600f), options));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(0f)]
    [InlineData(-1f)]
    public void Constructor_InvalidMinimumDayLengthThrows(float invalidValue)
    {
        WorldClockHostOptions options = new() { MinDayLengthSeconds = invalidValue };

        Assert.Throws<ArgumentOutOfRangeException>(() => CreateHost(new WorldClock(600f), options));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(-1f)]
    public void Constructor_InvalidMaximumTimeScaleThrows(float invalidValue)
    {
        WorldClockHostOptions options = new() { MaxTimeScale = invalidValue };

        Assert.Throws<ArgumentOutOfRangeException>(() => CreateHost(new WorldClock(600f), options));
    }

    [Theory]
    [InlineData(float.Epsilon, float.Epsilon, 0f)]
    [InlineData(float.MaxValue, float.MaxValue, float.MaxValue)]
    public void Constructor_ValidOptionBoundariesAreAccepted(
        float broadcastIntervalSeconds,
        float minimumDayLengthSeconds,
        float maximumTimeScale)
    {
        WorldClockHostOptions options = new()
        {
            BroadcastIntervalSeconds = broadcastIntervalSeconds,
            MinDayLengthSeconds = minimumDayLengthSeconds,
            MaxTimeScale = maximumTimeScale,
        };

        WorldClockHost host = CreateHost(new WorldClock(600f, 0.25f), options);

        Assert.Equal(new WorldClockState(0.25f, 600f, 1f), host.Snapshot);
    }

    [Fact]
    public void Tick_AdvancesClockAndPublishesSnapshot()
    {
        WorldClockHost host = CreateHost(new WorldClock(600f, 0.25f));

        host.Tick(150f);

        Assert.Equal(0.50f, host.Clock.TimeOfDay, 5);
        Assert.Equal(new WorldClockState(0.50f, 600f, 1f), host.Snapshot);
    }

    [Fact]
    public void Tick_BroadcastsWhenDefaultFiveSecondIntervalExpires()
    {
        List<byte[]> broadcasts = [];
        WorldClock clock = new(600f, 0.25f) { TimeScale = 0f };
        WorldClockHost host = new(clock, payload => broadcasts.Add(payload), (_, _) => { });

        host.Tick(2f);
        host.Tick(2f);

        Assert.Empty(broadcasts);

        host.Tick(1f);

        byte[] payload = Assert.Single(broadcasts);
        Assert.True(WorldClockCodec.TryDecodeState(payload, out WorldClockState state));
        Assert.Equal(new WorldClockState(0.25f, 600f, 0f), state);
    }

    [Fact]
    public void Tick_BroadcastsAfterEachAcceptedCommandAndAppliesDefaultBounds()
    {
        List<byte[]> broadcasts = [];
        WorldClockHost host = new(
            new WorldClock(600f, 0.10f),
            payload => broadcasts.Add(payload),
            (_, _) => { },
            LongIntervalOptions());

        host.EnqueueCommand(new WorldClockCommand(WorldClockCommandKind.SetTimeOfDay, 1.25f));
        host.EnqueueCommand(new WorldClockCommand(WorldClockCommandKind.SetTimeScale, 2000f));
        host.EnqueueCommand(new WorldClockCommand(WorldClockCommandKind.SetDayLength, 30f));

        host.Tick(0f);

        Assert.Equal(3, broadcasts.Count);
        AssertState(broadcasts[0], new WorldClockState(0.25f, 600f, 1f));
        AssertState(broadcasts[1], new WorldClockState(0.25f, 600f, 1000f));
        AssertState(broadcasts[2], new WorldClockState(0.25f, 60f, 1000f));
        Assert.Equal(new WorldClockState(0.25f, 60f, 1000f), host.Snapshot);
    }

    [Fact]
    public void Tick_AppliesCustomCommandBounds()
    {
        List<byte[]> broadcasts = [];
        WorldClockHostOptions options = new()
        {
            BroadcastIntervalSeconds = LongBroadcastIntervalSeconds,
            MaxTimeScale = 4f,
            MinDayLengthSeconds = 120f,
        };
        WorldClockHost host = new(
            new WorldClock(600f, 0.10f),
            payload => broadcasts.Add(payload),
            (_, _) => { },
            options);

        host.EnqueueCommand(new WorldClockCommand(WorldClockCommandKind.SetTimeScale, 5f));
        host.EnqueueCommand(new WorldClockCommand(WorldClockCommandKind.SetDayLength, 90f));

        host.Tick(0f);

        Assert.Equal(new WorldClockState(0.10f, 120f, 4f), host.Snapshot);
        Assert.Equal(2, broadcasts.Count);
    }

    [Fact]
    public void Tick_RejectsInvalidCommandsWithoutBroadcastingOrMutatingState()
    {
        List<byte[]> broadcasts = [];
        WorldClockHost host = new(
            new WorldClock(600f, 0.35f),
            payload => broadcasts.Add(payload),
            (_, _) => { },
            LongIntervalOptions());

        host.EnqueueCommand(new WorldClockCommand(WorldClockCommandKind.SetTimeOfDay, float.NaN));
        host.EnqueueCommand(new WorldClockCommand(WorldClockCommandKind.SetTimeScale, float.PositiveInfinity));
        host.EnqueueCommand(new WorldClockCommand(WorldClockCommandKind.SetDayLength, float.NegativeInfinity));
        host.EnqueueCommand(new WorldClockCommand((WorldClockCommandKind)99, 0.75f));

        host.Tick(0f);

        Assert.Empty(broadcasts);
        Assert.Equal(new WorldClockState(0.35f, 600f, 1f), host.Snapshot);
    }

    [Fact]
    public void TryEnqueueCommand_RejectsMalformedPayloadWithoutChangingClock()
    {
        List<byte[]> broadcasts = [];
        WorldClockHost host = new(
            new WorldClock(600f, 0.35f),
            payload => broadcasts.Add(payload),
            (_, _) => { },
            LongIntervalOptions());

        bool accepted = host.TryEnqueueCommand(new byte[4]);
        host.Tick(0f);

        Assert.False(accepted);
        Assert.Empty(broadcasts);
        Assert.Equal(new WorldClockState(0.35f, 600f, 1f), host.Snapshot);
    }

    [Fact]
    public void TryEnqueueCommand_AcceptsValidPayloadAndAppliesItOnTick()
    {
        List<byte[]> broadcasts = [];
        WorldClockHost host = new(
            new WorldClock(600f, 0.35f),
            payload => broadcasts.Add(payload),
            (_, _) => { },
            LongIntervalOptions());
        byte[] command = WorldClockCodec.EncodeCommand(
            new WorldClockCommand(WorldClockCommandKind.SetTimeOfDay, -0.25f));

        bool accepted = host.TryEnqueueCommand(command);

        Assert.True(accepted);
        Assert.Empty(broadcasts);

        host.Tick(0f);

        Assert.Single(broadcasts);
        Assert.Equal(0.75f, host.Snapshot.TimeOfDay, 5);
    }

    [Fact]
    public void PushTo_DefersSendUntilTickAndUsesPostAdvanceState()
    {
        List<(int Slot, byte[] Payload)> sends = [];
        WorldClockHost host = new(
            new WorldClock(600f, 0.25f),
            _ => { },
            (slot, payload) => sends.Add((slot, payload)),
            LongIntervalOptions());

        host.PushTo(7);

        Assert.Empty(sends);

        host.Tick(150f);

        (int slot, byte[] payload) = Assert.Single(sends);
        Assert.Equal(7, slot);
        AssertState(payload, new WorldClockState(0.50f, 600f, 1f));

        host.Tick(0f);
        Assert.Single(sends);
    }

    [Fact]
    public void PushTo_UsesStateAfterCommandsAppliedInSameTick()
    {
        List<(int Slot, byte[] Payload)> sends = [];
        WorldClockHost host = new(
            new WorldClock(600f, 0.25f),
            _ => { },
            (slot, payload) => sends.Add((slot, payload)),
            LongIntervalOptions());

        host.PushTo(4);
        host.EnqueueCommand(new WorldClockCommand(WorldClockCommandKind.SetTimeScale, 3f));
        host.Tick(0f);

        (int slot, byte[] payload) = Assert.Single(sends);
        Assert.Equal(4, slot);
        AssertState(payload, new WorldClockState(0.25f, 600f, 3f));
    }

    private static WorldClockHost CreateHost(WorldClock clock) =>
        new(clock, _ => { }, (_, _) => { }, LongIntervalOptions());

    private static WorldClockHost CreateHost(WorldClock clock, WorldClockHostOptions options) =>
        new(clock, _ => { }, (_, _) => { }, options);

    private static WorldClockHostOptions LongIntervalOptions() => new()
    {
        BroadcastIntervalSeconds = LongBroadcastIntervalSeconds,
    };

    private static void AssertState(byte[] payload, WorldClockState expected)
    {
        Assert.True(WorldClockCodec.TryDecodeState(payload, out WorldClockState state));
        Assert.Equal(expected, state);
    }
}
