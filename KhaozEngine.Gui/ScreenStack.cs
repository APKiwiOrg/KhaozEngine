using System;
using System.Collections.Generic;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using KhaozEngine.Primitives;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// Owns the screen stack and routes input top-to-bottom: the first visible, non-passthrough screen that
    /// reports consuming input blocks the screens below it; a non-passthrough (modal) screen also stops them
    /// updating. Drawing runs bottom-to-top. Also drives transitions. Uses dt + the engine-native
    /// <see cref="Pointer"/>/<see cref="InputState"/>.
    /// </summary>
    public sealed class ScreenStack
    {
        readonly List<Screen> _screens = new();
        readonly List<Screen> _updateScratch = new(); // reused per-frame copy so screens can add/remove during Update
        readonly InputManager _input = new();

        /// <summary>
        /// The shared high-level input manager driving this stack: menu navigation (<see cref="InputManager.IsMenuUp"/>/
        /// <c>Down</c>/<c>Select</c>/<c>Cancel</c>, <c>IsSelectNext</c>/<c>Previous</c>), action mapping, and the
        /// composed <see cref="Windowing.Pointer"/>. Screens read it as <c>Manager.InputManager</c> to drive a
        /// <see cref="FocusNavigator"/> and the keyboard/gamepad widget overloads
        /// (<c>Toggle</c>/<c>Slider</c>/<c>Dropdown</c> <c>Update(InputManager, focused)</c>). Updated every frame
        /// from the routed <see cref="Input"/> snapshot (and viewport). Its <see cref="Windowing.InputManager.Pointer"/>
        /// IS this stack's <see cref="Pointer"/>, so a screen can freely mix pointer-only and manager-driven widget
        /// updates and the click-through blocking still composes across them.
        /// </summary>
        public InputManager InputManager => _input;
        /// <summary>The shared bounds-aware pointer; screens hit-test via <c>Manager.Pointer</c>. The same instance
        /// as <see cref="InputManager"/>'s pointer.</summary>
        public Pointer Pointer => _input.Pointer;
        /// <summary>This frame's raw input snapshot (keyboard etc.). Also reachable as <c>InputManager.State</c>.</summary>
        public InputState Input { get; private set; } = InputState.Empty;

        /// <summary>
        /// Whether the CURRENT pointer gesture began over a region the screens had reserved, latched at the press
        /// and held until the next fresh press. This is the press-origin invariant carried across the UI boundary.
        /// <para>The stack drives its own composed <see cref="Pointer"/>, so a <see cref="Pointer.BlockRegion"/> a
        /// screen makes here is invisible to the game's world-picking pointer, and there is no rect a game can hand
        /// <see cref="Pointer.IsTapIn"/> that means "the whole screen stack" (its rect is the window, and the window
        /// contains every tap). A game with a modal screen over a 3D world gates its world pick on this instead:
        /// <code>if (!stack.PressBeganOverUi &amp;&amp; worldPointer.IsTapIn(bounds)) Pick();</code>
        /// Without it, the release that dismisses a login screen is still live on the frame the world appears, so
        /// the click that signs the player in also walks them.</para>
        /// <para>Evaluated inside <see cref="Update(float, InputState, IDesignViewport?)"/>, AFTER the screens have
        /// reserved their regions for the frame, so it sees this frame's real occlusion. It deliberately answers
        /// from the PRESS frame rather than recomputing on the release, by which point the screen that was in the
        /// way has usually closed. Left button only, which is the gesture the world pick rides.</para>
        /// </summary>
        public bool PressBeganOverUi { get; private set; }
        /// <summary>The current screens, ordered by <see cref="Screen.DrawOrder"/> ascending, with insertion order
        /// breaking ties. So the last element is the visually-topmost screen (highest draw order, and last added
        /// among equals).</summary>
        public IReadOnlyList<Screen> Screens => _screens;

        /// <summary>Optional service container shared with the screens (a DI container or a service locator).
        /// Screens read it via <see cref="Screen.Services"/>. Set once after constructing the stack; null when unused.</summary>
        public System.IServiceProvider? Services { get; set; }

        /// <summary>Adds a screen, applies its entry transition, calls LoadContent, and inserts it in draw order
        /// (a stable insert, so screens sharing a <see cref="Screen.DrawOrder"/> keep insertion order).</summary>
        public void Add(Screen screen)
        {
            // A screen that has been mounted before arrives back in the terminal Hidden state Remove left it in,
            // so a re-add has to re-run the entry transition instead of silently mounting it invisible-and-never-
            // updated. A screen the CALLER pre-set to Hidden without ever having been mounted anywhere (Manager
            // still unset) is honoured as a dormant add, which is what the Hidden check was there for.
            bool remount = screen.Manager is not null;
            screen.Manager = this;
            if (remount || screen.State != ScreenState.Hidden)
            {
                screen.State = screen.TransitionOnDuration > 0f ? ScreenState.TransitionOn : ScreenState.Active;
                if (screen.TransitionOnDuration > 0f) screen.TransitionAlpha = 0f;
            }
            screen.LoadContent();

            // Stable insert: place after every existing screen with DrawOrder <= this one, so equal-DrawOrder
            // screens keep insertion order and the last added is the topmost (Screens[^1]). A plain List.Sort is
            // not stable, so it could reorder equal-DrawOrder screens and break code relying on that ordering.
            int idx = _screens.Count;
            for (int i = 0; i < _screens.Count; i++)
                if (_screens[i].DrawOrder > screen.DrawOrder) { idx = i; break; }
            _screens.Insert(idx, screen);
        }

        /// <summary>
        /// Removes a screen, calls its <see cref="Screen.UnloadContent"/> (before the removal, so a screen tearing
        /// itself down still sees the stack it is leaving), and leaves it in the terminal
        /// <see cref="ScreenState.Hidden"/> state with <see cref="Screen.IsExiting"/> cleared.
        /// <para>That terminal state is load-bearing rather than cosmetic. <see cref="Update(float, InputState,
        /// IDesignViewport?)"/> iterates a scratch copy taken before the frame's removals, so a screen pulled out
        /// mid-frame - by its own transition-off completing, or by another screen's <see cref="Screen.Update"/> -
        /// is still sitting in that copy. Hidden is the one state the loop skips, so setting it here is what stops
        /// the screen running one more <see cref="Screen.Update"/> after its content is already gone.</para>
        /// <para>Removal is terminal without being one-way: re-adding the same instance through
        /// <see cref="Add"/> re-runs its entry transition.</para>
        /// </summary>
        public void Remove(Screen screen)
        {
            screen.UnloadContent();
            _screens.Remove(screen);
            screen.State = ScreenState.Hidden;
            screen.IsExiting = false;   // the exit finished; leaving it set re-enters AdvanceTransition on a re-add
        }

        /// <summary>Advance transitions and route input/update top-to-bottom. Call once per frame.</summary>
        public void Update(float dt, InputState input) => Update(dt, input, null);

        /// <summary>
        /// As <see cref="Update(float, InputState)"/>, but maps the pointer into design space through
        /// <paramref name="viewport"/> so screens hit-test in the same coordinates they draw with under
        /// <c>SpriteBatch.Begin(IDesignViewport)</c>. Pass null for raw window-pixel coordinates.
        /// </summary>
        public void Update(float dt, InputState input, IDesignViewport? viewport)
        {
            Input = input;
            _input.Update(input, viewport);   // drives the composed pointer (== Pointer) and menu-nav edges

            _updateScratch.Clear();                  // screens may add/remove during Update; iterate a copy
            _updateScratch.AddRange(_screens);
            bool inputHandled = false;
            for (int i = _updateScratch.Count - 1; i >= 0; i--)
            {
                Screen screen = _updateScratch[i];
                AdvanceTransition(screen, dt);
                // Hidden covers both a dormant screen and one already removed this frame (Remove leaves it Hidden),
                // so a screen whose UnloadContent has just run never gets one more Update out of the scratch copy.
                if (screen.State == ScreenState.Hidden) continue;

                bool receivesInput = !inputHandled || screen.AlwaysReceivesInput;
                bool consumed = screen.Update(dt, receivesInput);
                if (receivesInput && consumed && !screen.AlwaysReceivesInput) inputHandled = true;

                if (!screen.PassUpdateThrough) break;
            }

            // After the screens have reserved this frame's regions, so the query sees the real occlusion. Only on
            // a fresh press origin: on any other frame the answer is already latched, and recomputing it would read
            // the CURRENT layout for a press made when the layout was something else.
            Pointer pointer = _input.Pointer;
            if (pointer.IsPressOriginFresh) PressBeganOverUi = pointer.IsBlocked(pointer.PressOrigin);
        }

        /// <summary>Draws all non-hidden screens bottom-to-top.</summary>
        public void Draw(SpriteBatch batch)
        {
            for (int i = 0; i < _screens.Count; i++)
                if (_screens[i].State != ScreenState.Hidden)
                    _screens[i].Draw(batch);
        }

        void AdvanceTransition(Screen screen, float dt)
        {
            if (screen.IsExiting)
            {
                screen.State = ScreenState.TransitionOff;
                if (Step(screen, screen.TransitionOffDuration, -1, dt)) Remove(screen);
                return;
            }
            if (screen.State == ScreenState.TransitionOn && Step(screen, screen.TransitionOnDuration, +1, dt))
                screen.State = ScreenState.Active;
        }

        static bool Step(Screen screen, float duration, int dir, float dt)
        {
            float delta = duration > 0f ? dt / duration : 1f;
            screen.TransitionAlpha = Math.Clamp(screen.TransitionAlpha + delta * dir, 0f, 1f);
            return (dir > 0 && screen.TransitionAlpha >= 1f) || (dir < 0 && screen.TransitionAlpha <= 0f);
        }
    }
}
