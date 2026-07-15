using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui
{
    /// <summary>Lifecycle state of a <see cref="Screen"/>.</summary>
    public enum ScreenState { TransitionOn, Active, TransitionOff, Hidden }

    /// <summary>
    /// Base class for a screen in the <see cref="ScreenStack"/>. One UI surface (gameplay, menu, modal, HUD).
    /// Override <see cref="Update"/> (return whether it consumed input) and <see cref="Draw"/>. Read input via
    /// <c>Manager.Pointer</c> / <c>Manager.Input</c>. Touch gestures / per-player scoping / pause hooks are
    /// follow-ups.
    /// </summary>
    public abstract class Screen
    {
        /// <summary>Draw/route priority. Higher is on top (routed input first, drawn last).</summary>
        public int DrawOrder;
        /// <summary>If false, this screen is modal: screens below it neither update nor receive input.</summary>
        public bool PassUpdateThrough;
        /// <summary>If true, receives input even when a higher screen already consumed it (e.g. a persistent nav bar).</summary>
        public bool AlwaysReceivesInput;
        public ScreenState State = ScreenState.Active;
        public float TransitionOnDuration;
        public float TransitionOffDuration;
        /// <summary>Transition progress, 0 (hidden) to 1 (fully visible). Read in <see cref="Draw"/> to fade.</summary>
        public float TransitionAlpha = 1f;
        public bool IsExiting;

        /// <summary>
        /// Opaque fill for a full (non-modal) screen. When set, call <see cref="DrawBackground"/> first in
        /// <see cref="Draw"/> so the screen below does not show through the gaps. Leave null for a modal/overlay
        /// that should let the screen below show (draw your own scrim instead).
        /// </summary>
        public Vector4? BackgroundColor;

        /// <summary>The owning stack, set by <see cref="ScreenStack.Add"/>.</summary>
        public ScreenStack Manager = null!;

        /// <summary>The owning stack's <see cref="ScreenStack.Services"/> container, or null when none is set
        /// (or before this screen has been added to a stack). Read it to resolve shared services.</summary>
        public System.IServiceProvider? Services => Manager?.Services;

        public virtual void LoadContent() { }
        public virtual void UnloadContent() { }

        /// <summary>
        /// Per-frame update. <paramref name="receivesInput"/> is whether a screen above already consumed input this
        /// frame (or this screen has <see cref="AlwaysReceivesInput"/> set); read it before touching input, since a
        /// screen still updates every frame it is not blocked by <see cref="PassUpdateThrough"/> regardless of
        /// whether it receives input.
        /// </summary>
        /// <returns>
        /// Whether THIS screen consumed input THIS frame - true stops input reaching screens below (see
        /// <see cref="ScreenStack"/>). "Received" and "consumed" are different questions: a screen can receive
        /// input and still return false (it looked and had nothing to do with it), and must return false whenever
        /// <paramref name="receivesInput"/> is false (it never got a chance to act).
        /// <para>
        /// A screen that stays in the stack permanently but is only sometimes showing something (an always-mounted
        /// overlay, a toast, a hotkey-triggered panel) MUST return false while dormant/hidden, or it silently
        /// starves every screen below it of input for as long as it sits in the stack - the screen still LOOKS
        /// empty, but nothing beneath it can be clicked or typed into. Pair this with keeping
        /// <see cref="PassUpdateThrough"/> true while dormant and flipping it false only while something is
        /// actually visible/interactive, so a modal moment blocks the screens below and an idle moment does not.
        /// <see cref="UpdateOverlayScreen"/> is the reference implementation: it recomputes
        /// <see cref="PassUpdateThrough"/> from its own visibility every frame and returns
        /// <c>receivesInput &amp;&amp; visible</c>, never a bare `true`.
        /// </para>
        /// </returns>
        public abstract bool Update(float dt, bool receivesInput);

        /// <summary>Per-frame draw (screens draw bottom-to-top).</summary>
        public abstract void Draw(SpriteBatch batch);

        /// <summary>
        /// Fill the whole design viewport with <see cref="BackgroundColor"/> if set (no-op otherwise). Call
        /// first in <see cref="Draw"/> for an opaque full screen. <paramref name="white"/> is a 1x1 white texture.
        /// </summary>
        public void DrawBackground(SpriteBatch batch, Texture2D white, IDesignViewport viewport)
        {
            // WindowBounds, not (0,0,Width,Height): under a letterbox scale the design rect stops short of the
            // window edges, so an opaque full-screen fill sized from the design would leave the bars showing the
            // screen below. WindowBounds covers the whole window and reduces to the design rect when unletterboxed.
            if (BackgroundColor is { } c)
                batch.Draw(white, viewport.WindowBounds, (Color)c);
        }

        /// <summary>Request removal; animates out first if <see cref="TransitionOffDuration"/> &gt; 0.</summary>
        public void ExitScreen()
        {
            if (TransitionOffDuration <= 0f) { Manager.Remove(this); return; }
            IsExiting = true;
        }
    }
}
