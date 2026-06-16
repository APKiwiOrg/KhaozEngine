using System;
using System.Collections.Generic;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// Owns the screen stack and routes input top-to-bottom: the first visible, non-passthrough screen that
    /// reports consuming input blocks the screens below it; a non-passthrough (modal) screen also stops them
    /// updating. Drawing runs bottom-to-top. Also drives transitions. Ported from the MonoGame
    /// <c>ScreenManager</c> (uses dt + the engine-native <see cref="Pointer"/>/<see cref="InputState"/>).
    /// </summary>
    public sealed class ScreenStack
    {
        readonly List<Screen> _screens = new();
        readonly List<Screen> _updateScratch = new(); // reused per-frame copy so screens can add/remove during Update

        /// <summary>The shared bounds-aware pointer; screens hit-test via <c>Manager.Pointer</c>.</summary>
        public Pointer Pointer { get; } = new();
        /// <summary>This frame's raw input snapshot (keyboard etc.).</summary>
        public InputState Input { get; private set; } = InputState.Empty;
        /// <summary>The current screens, sorted by <see cref="Screen.DrawOrder"/> ascending.</summary>
        public IReadOnlyList<Screen> Screens => _screens;

        /// <summary>Optional service container shared with the screens (a DI container or a service locator).
        /// Screens read it via <see cref="Screen.Services"/>. Set once after constructing the stack; null when unused.</summary>
        public System.IServiceProvider? Services { get; set; }

        /// <summary>Adds a screen, applies its entry transition, calls LoadContent, and re-sorts by draw order.</summary>
        public void Add(Screen screen)
        {
            screen.Manager = this;
            if (screen.State != ScreenState.Hidden)
            {
                screen.State = screen.TransitionOnDuration > 0f ? ScreenState.TransitionOn : ScreenState.Active;
                if (screen.TransitionOnDuration > 0f) screen.TransitionAlpha = 0f;
            }
            screen.LoadContent();
            _screens.Add(screen);
            _screens.Sort((a, b) => a.DrawOrder.CompareTo(b.DrawOrder));
        }

        /// <summary>Removes a screen and calls its UnloadContent.</summary>
        public void Remove(Screen screen)
        {
            screen.UnloadContent();
            _screens.Remove(screen);
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
            Pointer.Update(input, viewport);

            _updateScratch.Clear();                  // screens may add/remove during Update; iterate a copy
            _updateScratch.AddRange(_screens);
            bool inputHandled = false;
            for (int i = _updateScratch.Count - 1; i >= 0; i--)
            {
                Screen screen = _updateScratch[i];
                AdvanceTransition(screen, dt);
                if (screen.State == ScreenState.Hidden) continue;

                bool receivesInput = !inputHandled || screen.AlwaysReceivesInput;
                bool consumed = screen.Update(dt, receivesInput);
                if (receivesInput && consumed && !screen.AlwaysReceivesInput) inputHandled = true;

                if (!screen.PassUpdateThrough) break;
            }
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
