using System;
using KhaozEngine.Input;
using KhaozEngine.Screens;
using KhaozEngine.Time;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Xunit;

namespace KhaozEngine.Tests;

file sealed class LifecycleSpyScreen : GameScreen
{
    public int PauseCount;
    public int ResumeCount;

    public LifecycleSpyScreen(int order)
    {
        DrawOrder = order;
        PassUpdateThrough = true;
    }

    public override bool Update(GameTime gameTime, bool receivesInput) => false;
    public override void Draw(GameTime gameTime, SpriteBatch spriteBatch) { }
    protected override void OnPause() => PauseCount++;
    protected override void OnResume() => ResumeCount++;
}

public class ScreenManagerTimeTests
{
    private static GameTime Frame(double dt) =>
        new GameTime(TimeSpan.Zero, TimeSpan.FromSeconds(dt));

    [Fact]
    public void InjectedClockIsExposed()
    {
        var clock = new GameClock();
        var m = new ScreenManager(new InputManager(), clock);
        Assert.Same(clock, m.Clock);
    }

    [Fact]
    public void InjectedClockDispatchesPauseToScreens()
    {
        var clock = new GameClock();
        var m = new ScreenManager(new InputManager(), clock);
        var s = new LifecycleSpyScreen(0);
        m.Add(s);

        clock.Pause();
        Assert.Equal(1, s.PauseCount);

        clock.Resume();
        Assert.Equal(1, s.ResumeCount);
    }

    [Fact]
    public void DefaultConstructorCreatesAClock()
    {
        var m = new ScreenManager(new InputManager());
        Assert.NotNull(m.Clock);
        Assert.False(m.IsPaused);
    }

    [Fact]
    public void ScaledDeltaReflectsTimeScaleAfterUpdate()
    {
        var m = new ScreenManager(new InputManager()) { TimeScale = 2f };
        m.Update(Frame(0.5));
        Assert.Equal(0.5f, m.RealDeltaSeconds);
        Assert.Equal(1.0f, m.ScaledDeltaSeconds);
    }

    [Fact]
    public void TransitionsAdvanceWhilePaused()
    {
        var m = new ScreenManager(new InputManager());
        var s = new LifecycleSpyScreen(0) { TransitionOnDuration = 1f };
        m.Add(s);
        Assert.Equal(ScreenState.TransitionOn, s.State);
        Assert.Equal(0f, s.TransitionAlpha);

        m.Clock.Pause();
        m.Update(Frame(0.5));   // real dt still flows to transitions
        Assert.Equal(0.5f, s.TransitionAlpha);
    }

    [Fact]
    public void TimeScaleZeroDispatchesOnPauseAndResumeReversesIt()
    {
        var m = new ScreenManager(new InputManager());
        var s = new LifecycleSpyScreen(0);
        m.Add(s);

        m.TimeScale = 0f;          // pause via time-scale, not Pause()
        Assert.Equal(1, s.PauseCount);

        m.TimeScale = 1f;          // back to normal speed dispatches OnResume once
        Assert.Equal(1, s.ResumeCount);
        Assert.Equal(1, s.PauseCount);
    }

    [Fact]
    public void PauseDispatchesOnPauseToAllScreens()
    {
        var m = new ScreenManager(new InputManager());
        var a = new LifecycleSpyScreen(0);
        var b = new LifecycleSpyScreen(10);
        m.Add(a); m.Add(b);

        m.Clock.Pause();
        Assert.Equal(1, a.PauseCount);
        Assert.Equal(1, b.PauseCount);

        m.Update(Frame(0.5));   // does not refire
        Assert.Equal(1, a.PauseCount);

        m.Clock.Resume();
        Assert.Equal(1, a.ResumeCount);
        Assert.Equal(1, b.ResumeCount);
    }
}
