using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input.Touch;

namespace KhaozEngine.Screens;

public abstract class GameScreen
{
    public int DrawOrder;
    public bool PassUpdateThrough;        // false = modal: freezes lower screens (Nullwake) == !IsPopup (SpaceGame)
    public bool AlwaysReceivesInput;
    public InputConsumption InputConsumption = InputConsumption.ConsumeWhenVisible;
    public ScreenState State = ScreenState.Active;

    public float TransitionOnDuration;    // seconds; 0 = instant
    public float TransitionOffDuration;
    public float TransitionAlpha = 1f;

    public PlayerIndex? ControllingPlayer;
    public GestureType EnabledGestures = GestureType.None;

    public ScreenManager Manager = null!; // set by ScreenManager.Add
    public bool IsExiting;

    public virtual void LoadContent() { }
    public virtual void UnloadContent() { }

    // Returns whether this screen consumed input this frame. Under ConsumeWhenVisible
    // the base contract is "return receivesInput"; under ConsumeWhenHandled return the
    // real handled result.
    public abstract bool Update(GameTime gameTime, bool receivesInput);
    public abstract void Draw(GameTime gameTime, SpriteBatch spriteBatch);

    // Begin an animated exit; the manager drives TransitionOff then removes.
    public void ExitScreen()
    {
        if (TransitionOffDuration <= 0f) { Manager.Remove(this); return; }
        IsExiting = true;
    }
}
