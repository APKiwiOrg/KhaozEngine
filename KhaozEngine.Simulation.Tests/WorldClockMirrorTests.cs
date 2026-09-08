using KhaozEngine.Simulation;
using Xunit;

namespace KhaozEngine.Tests.Simulation;

public class WorldClockMirrorTests
{
    [Fact]
    public void Advance_BeforeFirstStateLeavesMirrorAbsent()
    {
        WorldClockMirror mirror = new();

        mirror.Advance(60f);

        Assert.False(mirror.HasState);
        Assert.Equal(default, mirror.State);
    }

    [Fact]
    public void Apply_ValidPayloadInitializesAllState()
    {
        WorldClockMirror mirror = new();
        byte[] payload = WorldClockCodec.EncodeState(new WorldClockState(0.25f, 600f, 2f));

        bool applied = mirror.Apply(payload);

        Assert.True(applied);
        Assert.True(mirror.HasState);
        Assert.Equal(new WorldClockState(0.25f, 600f, 2f), mirror.State);
    }

    [Fact]
    public void Advance_AfterStateMovesMirrorBetweenMessages()
    {
        WorldClockMirror mirror = new();
        mirror.Apply(WorldClockCodec.EncodeState(new WorldClockState(0.25f, 100f, 2f)));

        mirror.Advance(12.5f);

        Assert.Equal(0.50f, mirror.State.TimeOfDay, 5);
        Assert.Equal(100f, mirror.State.DayLengthSeconds);
        Assert.Equal(2f, mirror.State.TimeScale);
    }

    [Fact]
    public void Apply_LaterStateCorrectsTimeAndLiveClockSettings()
    {
        WorldClockMirror mirror = new();
        mirror.Apply(WorldClockCodec.EncodeState(new WorldClockState(0.25f, 100f, 2f)));
        mirror.Advance(12.5f);

        bool applied = mirror.Apply(
            WorldClockCodec.EncodeState(new WorldClockState(0.50f, 400f, 4f)));
        mirror.Advance(25f);

        Assert.True(applied);
        Assert.Equal(new WorldClockState(0.75f, 400f, 4f), mirror.State);
    }

    [Fact]
    public void Apply_MalformedPayloadRetainsPriorState()
    {
        WorldClockMirror mirror = new();
        mirror.Apply(WorldClockCodec.EncodeState(new WorldClockState(0.25f, 600f, 2f)));

        bool applied = mirror.Apply(new byte[11]);

        Assert.False(applied);
        Assert.True(mirror.HasState);
        Assert.Equal(new WorldClockState(0.25f, 600f, 2f), mirror.State);
    }

    [Fact]
    public void Apply_InvalidStateBeforeInitializationLeavesMirrorAbsent()
    {
        WorldClockMirror mirror = new();
        byte[] invalid = WorldClockCodec.EncodeState(new WorldClockState(1f, 600f, 2f));

        bool applied = mirror.Apply(invalid);

        Assert.False(applied);
        Assert.False(mirror.HasState);
        Assert.Equal(default, mirror.State);
    }
}
