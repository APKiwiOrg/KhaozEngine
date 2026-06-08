using System;
using System.Collections.Generic;
using KhaozEngine.Input;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace KhaozEngine.Screens;

/// <summary>
/// Owns the screen stack and routes input top-to-bottom. The first visible, non-passthrough screen
/// that reports consuming input blocks the screens below it; a non-passthrough (modal) screen also
/// stops them updating. Drawing runs bottom-to-top. Also drives screen transitions.
/// </summary>
public sealed class ScreenManager
{
    private readonly List<GameScreen> _screens = new();

    /// <summary>The shared input manager; screens reach it via <c>Manager.Input</c>.</summary>
    public InputManager Input { get; }

    /// <summary>Optional graphics device, for screens that need it.</summary>
    public GraphicsDevice? GraphicsDevice { get; set; }

    /// <summary>Optional shared sprite batch.</summary>
    public SpriteBatch? SpriteBatch { get; set; }

    /// <summary>Optional service provider for games that resolve screen dependencies from a container.</summary>
    public IServiceProvider? Services { get; set; }

    /// <summary>Invoked by <see cref="RequestExit"/>; wire this to <c>Game.Exit</c>.</summary>
    public Action? ExitRequested;

    /// <summary>Creates the manager around an <see cref="InputManager"/>.</summary>
    public ScreenManager(InputManager input) => Input = input;

    /// <summary>The current screens, sorted by <see cref="GameScreen.DrawOrder"/> ascending.</summary>
    public IReadOnlyList<GameScreen> Screens => _screens;

    /// <summary>Adds a screen, applies its entry transition state, calls LoadContent, and re-sorts by draw order.</summary>
    public void Add(GameScreen screen)
    {
        screen.Manager = this;
        // Respect a screen deliberately added Hidden; otherwise apply the entry state.
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
    public void Remove(GameScreen screen)
    {
        screen.UnloadContent();
        _screens.Remove(screen);
    }

    /// <summary>Raises <see cref="ExitRequested"/>.</summary>
    public void RequestExit() => ExitRequested?.Invoke();

    /// <summary>Advances transitions and routes input/update top-to-bottom. Call once per frame after <see cref="InputManager.Update"/>.</summary>
    public void Update(GameTime gameTime)
    {
        float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        GameScreen[] snapshot = _screens.ToArray();   // screens may add/remove during Update
        bool inputHandled = false;

        for (int i = snapshot.Length - 1; i >= 0; i--)
        {
            GameScreen screen = snapshot[i];
            AdvanceTransition(screen, dt);
            if (screen.State == ScreenState.Hidden) continue;

            bool receivesInput = !inputHandled || screen.AlwaysReceivesInput;
            bool consumed = screen.Update(gameTime, receivesInput);

            if (receivesInput && consumed && !screen.AlwaysReceivesInput)
                inputHandled = true;

            if (!screen.PassUpdateThrough) break;
        }
    }

    private void AdvanceTransition(GameScreen screen, float dt)
    {
        if (screen.IsExiting)
        {
            screen.State = ScreenState.TransitionOff;
            if (Step(screen, screen.TransitionOffDuration, -1, dt)) { Remove(screen); }
            return;
        }
        if (screen.State == ScreenState.TransitionOn)
        {
            if (Step(screen, screen.TransitionOnDuration, +1, dt)) screen.State = ScreenState.Active;
        }
    }

    // Advances TransitionAlpha; returns true when the transition completes.
    private static bool Step(GameScreen screen, float duration, int dir, float dt)
    {
        float delta = duration > 0f ? dt / duration : 1f;
        screen.TransitionAlpha = MathHelper.Clamp(screen.TransitionAlpha + delta * dir, 0f, 1f);
        return (dir > 0 && screen.TransitionAlpha >= 1f) || (dir < 0 && screen.TransitionAlpha <= 0f);
    }

    /// <summary>Draws all non-hidden screens bottom-to-top.</summary>
    public void Draw(GameTime gameTime, SpriteBatch spriteBatch)
    {
        for (int i = 0; i < _screens.Count; i++)
            if (_screens[i].State != ScreenState.Hidden)
                _screens[i].Draw(gameTime, spriteBatch);
    }
}
