using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input.Touch;

namespace KhaozEngine.Screens;

/// <summary>
/// Base class for a screen in the <see cref="ScreenManager"/> stack. A screen is one UI surface
/// (gameplay, a menu, a modal, a HUD). Override <see cref="Update"/> and <see cref="Draw"/>.
/// </summary>
public abstract class GameScreen
{
    /// <summary>Draw/route priority. Higher values are on top (routed input first, drawn last).</summary>
    public int DrawOrder;

    /// <summary>If false, this screen is modal: screens below it neither update nor receive input.</summary>
    public bool PassUpdateThrough;

    /// <summary>If true, receives input even when a higher screen already consumed it (e.g. a persistent nav bar).</summary>
    public bool AlwaysReceivesInput;

    /// <summary>Declared consumption intent. Implement it via the bool returned from <see cref="Update"/>.</summary>
    public InputConsumption InputConsumption = InputConsumption.ConsumeWhenVisible;

    /// <summary>Current lifecycle state. Set to <see cref="ScreenState.Hidden"/> before adding to add it hidden.</summary>
    public ScreenState State = ScreenState.Active;

    /// <summary>Transition-in duration in seconds (0 = instant).</summary>
    public float TransitionOnDuration;

    /// <summary>Transition-out duration in seconds (0 = instant).</summary>
    public float TransitionOffDuration;

    /// <summary>Transition progress, 0 (hidden) to 1 (fully visible). Read in <see cref="Draw"/> to fade.</summary>
    public float TransitionAlpha = 1f;

    /// <summary>Optional owning player (for input scoping); null means any/unspecified.</summary>
    public PlayerIndex? ControllingPlayer;

    /// <summary>Touch gestures this screen wants enabled, if the game opts into gesture input.</summary>
    public GestureType EnabledGestures = GestureType.None;

    /// <summary>The owning manager, set by <see cref="ScreenManager.Add"/>. Reach input via <c>Manager.Input</c>.</summary>
    public ScreenManager Manager = null!;

    /// <summary>True once <see cref="ExitScreen"/> has begun an animated exit.</summary>
    public bool IsExiting;

    /// <summary>Called once when the screen is added. Load content here.</summary>
    public virtual void LoadContent() { }

    /// <summary>Called once when the screen is removed. Release resources here.</summary>
    public virtual void UnloadContent() { }

    /// <summary>
    /// Per-frame update. Return whether this screen consumed input this frame — true stops input
    /// reaching screens below it, false lets it fall through (see <see cref="InputConsumption"/>).
    /// </summary>
    /// <param name="gameTime">Timing snapshot.</param>
    /// <param name="receivesInput">True if this is the topmost input-receiving screen this frame.</param>
    public abstract bool Update(GameTime gameTime, bool receivesInput);

    /// <summary>Per-frame draw. Screens draw bottom-to-top.</summary>
    public abstract void Draw(GameTime gameTime, SpriteBatch spriteBatch);

    /// <summary>
    /// Requests removal. With a non-zero <see cref="TransitionOffDuration"/> this animates out first;
    /// otherwise it removes immediately.
    /// </summary>
    public void ExitScreen()
    {
        if (TransitionOffDuration <= 0f) { Manager.Remove(this); return; }
        IsExiting = true;
    }
}
