using KhaozEngine.Render2D;

namespace KhaozEngine.Gui
{
    /// <summary>Lifecycle state of a <see cref="Screen"/>.</summary>
    public enum ScreenState { TransitionOn, Active, TransitionOff, Hidden }

    /// <summary>Declared input-consumption intent (implemented via the bool returned from <see cref="Screen.Update"/>).</summary>
    public enum InputConsumption { ConsumeWhenVisible, ConsumeWhenHandled }

    /// <summary>
    /// Base class for a screen in the <see cref="ScreenStack"/>. One UI surface (gameplay, menu, modal, HUD).
    /// Override <see cref="Update"/> (return whether it consumed input) and <see cref="Draw"/>. Read input via
    /// <c>Manager.Pointer</c> / <c>Manager.Input</c>. Ported from the MonoGame <c>GameScreen</c> (touch
    /// gestures / per-player scoping / pause hooks are follow-ups).
    /// </summary>
    public abstract class Screen
    {
        /// <summary>Draw/route priority. Higher is on top (routed input first, drawn last).</summary>
        public int DrawOrder;
        /// <summary>If false, this screen is modal: screens below it neither update nor receive input.</summary>
        public bool PassUpdateThrough;
        /// <summary>If true, receives input even when a higher screen already consumed it (e.g. a persistent nav bar).</summary>
        public bool AlwaysReceivesInput;
        public InputConsumption InputConsumption = InputConsumption.ConsumeWhenVisible;
        public ScreenState State = ScreenState.Active;
        public float TransitionOnDuration;
        public float TransitionOffDuration;
        /// <summary>Transition progress, 0 (hidden) to 1 (fully visible). Read in <see cref="Draw"/> to fade.</summary>
        public float TransitionAlpha = 1f;
        public bool IsExiting;

        /// <summary>The owning stack, set by <see cref="ScreenStack.Add"/>.</summary>
        public ScreenStack Manager = null!;

        public virtual void LoadContent() { }
        public virtual void UnloadContent() { }

        /// <summary>Per-frame update. Return whether this screen consumed input (true stops input reaching screens below).</summary>
        public abstract bool Update(float dt, bool receivesInput);

        /// <summary>Per-frame draw (screens draw bottom-to-top).</summary>
        public abstract void Draw(SpriteBatch batch);

        /// <summary>Request removal; animates out first if <see cref="TransitionOffDuration"/> &gt; 0.</summary>
        public void ExitScreen()
        {
            if (TransitionOffDuration <= 0f) { Manager.Remove(this); return; }
            IsExiting = true;
        }
    }
}
