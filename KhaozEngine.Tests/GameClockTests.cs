using System;
using KhaozEngine.Time;
using Microsoft.Xna.Framework;
using Xunit;

namespace KhaozEngine.Tests;

public class GameClockTests
{
    private static GameTime Frame(double dt) =>
        new GameTime(TimeSpan.Zero, TimeSpan.FromSeconds(dt));

    [Fact]
    public void DefaultsAreNormalSpeedNotPausedZeroDeltas()
    {
        var c = new GameClock();
        Assert.Equal(1f, c.TimeScale);
        Assert.False(c.IsPaused);
        Assert.Equal(0f, c.RealDeltaSeconds);
        Assert.Equal(0f, c.ScaledDeltaSeconds);
    }

    [Fact]
    public void UpdateAtNormalSpeedScaledEqualsReal()
    {
        var c = new GameClock();
        c.Update(Frame(0.5));
        Assert.Equal(0.5f, c.RealDeltaSeconds);
        Assert.Equal(0.5f, c.ScaledDeltaSeconds);
    }

    [Fact]
    public void FastForwardScalesSimButNotReal()
    {
        var c = new GameClock { TimeScale = 2f };
        c.Update(Frame(0.5));
        Assert.Equal(0.5f, c.RealDeltaSeconds);
        Assert.Equal(1.0f, c.ScaledDeltaSeconds);
    }

    [Fact]
    public void SlowMoScalesSimDown()
    {
        var c = new GameClock { TimeScale = 0.5f };
        c.Update(Frame(0.5));
        Assert.Equal(0.25f, c.ScaledDeltaSeconds);
        Assert.Equal(0.5f, c.RealDeltaSeconds);
    }

    [Fact]
    public void NegativeTimeScaleClampsToZero()
    {
        var c = new GameClock { TimeScale = -3f };
        Assert.Equal(0f, c.TimeScale);
        Assert.True(c.IsPaused);   // clamped to zero -> sim frozen
    }

    [Fact]
    public void PauseZeroesScaledButNotRealDelta()
    {
        var c = new GameClock();
        c.Pause();
        c.Update(Frame(0.5));
        Assert.True(c.IsPaused);
        Assert.Equal(0f, c.ScaledDeltaSeconds);
        Assert.Equal(0.5f, c.RealDeltaSeconds);
    }

    [Fact]
    public void ResumeRestoresPriorTimeScale()
    {
        var c = new GameClock { TimeScale = 2f };
        c.Pause();
        c.Resume();
        c.Update(Frame(0.5));
        Assert.False(c.IsPaused);
        Assert.Equal(1.0f, c.ScaledDeltaSeconds);   // back to 2x, not 1x
    }

    [Fact]
    public void TimeScaleZeroReportsPaused()
    {
        var c = new GameClock { TimeScale = 0f };
        Assert.True(c.IsPaused);
    }

    [Fact]
    public void PausedEventFiresOnceOnTransitionNotPerFrame()
    {
        var c = new GameClock();
        int paused = 0, resumed = 0;
        c.Paused += () => paused++;
        c.Resumed += () => resumed++;

        c.Pause();
        c.Update(Frame(0.5));
        c.Update(Frame(0.5));
        Assert.Equal(1, paused);
        Assert.Equal(0, resumed);

        c.Resume();
        c.Update(Frame(0.5));
        Assert.Equal(1, paused);
        Assert.Equal(1, resumed);
    }

    [Fact]
    public void SettingTimeScaleToZeroFiresPausedEvent()
    {
        var c = new GameClock();
        int paused = 0;
        c.Paused += () => paused++;
        c.TimeScale = 0f;
        Assert.Equal(1, paused);
    }
}
