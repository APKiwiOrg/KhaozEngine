using KhaozEngine.Input;
using KhaozEngine.Screens;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Xunit;

namespace KhaozEngine.Tests;

file sealed class SpyScreen : GameScreen
{
    public int UpdateCount;
    public bool LastReceivedInput;
    public bool ConsumeResult = true;   // what this screen reports consuming

    public SpyScreen(int order, bool passUpdateThrough, bool always = false,
                     InputConsumption policy = InputConsumption.ConsumeWhenVisible)
    {
        DrawOrder = order;
        PassUpdateThrough = passUpdateThrough;
        AlwaysReceivesInput = always;
        InputConsumption = policy;
    }

    public override bool Update(GameTime gameTime, bool receivesInput)
    {
        UpdateCount++;
        LastReceivedInput = receivesInput;
        return receivesInput && ConsumeResult;
    }

    public override void Draw(GameTime gameTime, SpriteBatch spriteBatch) { }
}

public class ScreenManagerTests
{
    private static readonly GameTime Zero = new();
    private static ScreenManager NewManager() => new(new InputManager());

    [Fact]
    public void TopScreenConsumesInputAndBlocksLower()
    {
        var m = NewManager();
        var low = new SpyScreen(0, passUpdateThrough: true);
        var high = new SpyScreen(10, passUpdateThrough: true);
        m.Add(low); m.Add(high);
        m.Update(Zero);
        Assert.True(high.LastReceivedInput);
        Assert.False(low.LastReceivedInput);
        Assert.Equal(1, low.UpdateCount);
    }

    [Fact]
    public void ConsumeWhenHandledLetsInputFallThroughWhenNotHandled()
    {
        var m = NewManager();
        var low = new SpyScreen(0, passUpdateThrough: true);
        var high = new SpyScreen(10, passUpdateThrough: true,
            policy: InputConsumption.ConsumeWhenHandled) { ConsumeResult = false };
        m.Add(low); m.Add(high);
        m.Update(Zero);
        Assert.True(high.LastReceivedInput);
        Assert.True(low.LastReceivedInput);   // high did not handle, so low still gets input
    }

    [Fact]
    public void AlwaysReceivesInputGetsInputEvenWhenHigherConsumed()
    {
        var m = NewManager();
        var normal = new SpyScreen(0, passUpdateThrough: true);
        var always = new SpyScreen(5, passUpdateThrough: true, always: true);
        var consumer = new SpyScreen(10, passUpdateThrough: true);
        m.Add(normal); m.Add(always); m.Add(consumer);
        m.Update(Zero);
        Assert.True(consumer.LastReceivedInput);
        Assert.True(always.LastReceivedInput);
        Assert.False(normal.LastReceivedInput);
    }

    [Fact]
    public void NonPassThroughScreenFreezesLowerScreens()
    {
        var m = NewManager();
        var low = new SpyScreen(0, passUpdateThrough: true);
        var modal = new SpyScreen(10, passUpdateThrough: false);
        m.Add(low); m.Add(modal);
        m.Update(Zero);
        Assert.Equal(1, modal.UpdateCount);
        Assert.Equal(0, low.UpdateCount);
    }

    [Fact]
    public void HiddenScreenNeitherConsumesNorBlocks()
    {
        var m = NewManager();
        var low = new SpyScreen(0, passUpdateThrough: true);
        var hidden = new SpyScreen(10, passUpdateThrough: false) { State = ScreenState.Hidden };
        m.Add(low); m.Add(hidden);
        m.Update(Zero);
        Assert.Equal(0, hidden.UpdateCount);
        Assert.Equal(1, low.UpdateCount);
        Assert.True(low.LastReceivedInput);
    }

    [Fact]
    public void AddRemoveAndRequestExit()
    {
        var m = NewManager();
        var s = new SpyScreen(0, passUpdateThrough: true);
        m.Add(s);
        Assert.Single(m.Screens);
        Assert.Same(m, s.Manager);
        m.Remove(s);
        Assert.Empty(m.Screens);

        bool exited = false;
        m.ExitRequested = () => exited = true;
        m.RequestExit();
        Assert.True(exited);
    }

    [Fact]
    public void TransitionDurationZeroSnapsToActive()
    {
        var m = NewManager();
        var s = new SpyScreen(0, passUpdateThrough: true);   // default durations 0
        m.Add(s);
        m.Update(Zero);
        Assert.Equal(ScreenState.Active, s.State);
    }
}
