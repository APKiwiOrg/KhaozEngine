using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui;

/// <summary>
/// Drop-in <see cref="Screen"/> wrapping <see cref="PatchNotesView"/> for stack-based games: mirrors
/// <see cref="UpdateOverlayScreen"/>'s thin wrapper shape (construct the view, read input from the owning
/// <see cref="ScreenStack"/>, forward Update/Draw) but is always modal
/// (<see cref="Screen.PassUpdateThrough"/> stays false) with <c>SettingsScreen</c>-style 0.18s in/out
/// transitions, because a patch-notes panel is only ever pushed to be shown front and center, never as a
/// passive, sometimes-passthrough overlay like the update notice. Exits itself
/// (<see cref="Screen.ExitScreen"/>) the frame <see cref="PatchNotesView.CloseRequested"/> latches (close
/// button tap or Escape).
/// </summary>
public sealed class PatchNotesScreen : Screen
{
    readonly SpriteFont _font;
    readonly Texture2D _white;
    readonly IDesignViewport _viewport;
    readonly PatchNotesView _view;

    /// <summary>The wrapped view (e.g. to retheme at runtime or inspect scroll/expand state).</summary>
    public PatchNotesView View => _view;

    /// <summary>
    /// Wraps <paramref name="document"/> in a modal patch-notes panel. <paramref name="font"/> both measures
    /// (drives <see cref="PatchNotesView"/>'s scroll clamp) and renders the panel text; <paramref name="white"/>
    /// is a 1x1 white texture; <paramref name="viewport"/> supplies the design-to-window mapping the panel
    /// centers within (its <see cref="IDesignViewport.WindowBounds"/> is what the panel and scrim are drawn
    /// into, so the scrim covers the whole window under a letterbox, same as
    /// <see cref="UpdateOverlayScreen"/>). <paramref name="theme"/> defaults to
    /// <see cref="PatchNotesTheme.Default"/>.
    /// </summary>
    public PatchNotesScreen(PatchNotesDocument document, SpriteFont font, Texture2D white,
        IDesignViewport viewport, PatchNotesTheme? theme = null)
    {
        _font = font;
        _white = white;
        _viewport = viewport;
        _view = new PatchNotesView(document, theme);
        DrawOrder = 10;                 // draws above a room's root menu screen, mirrors SettingsScreen
        PassUpdateThrough = false;      // modal: the screen below neither updates nor receives input
        TransitionOnDuration = 0.18f;   // SettingsScreen-style modal transition
        TransitionOffDuration = 0.18f;
    }

    public override bool Update(float dt, bool receivesInput)
    {
        if (receivesInput)
        {
            bool open = _view.Update(Manager.Pointer, Manager.Input, dt, _viewport.WindowBounds, _font);
            if (!open) ExitScreen();
        }
        return true;
    }

    public override void Draw(SpriteBatch batch) =>
        _view.Draw(batch, _font, _white, _viewport.WindowBounds);
}
