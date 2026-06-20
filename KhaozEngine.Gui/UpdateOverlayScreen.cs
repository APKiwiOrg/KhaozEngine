using System;
using KhaozEngine.Render2D;
using KhaozEngine.Updates;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui;

/// <summary>
/// Drop-in <see cref="Screen"/> wrapping <see cref="UpdateOverlayView"/> for stack-based games. It reads
/// input from the owning <see cref="ScreenStack"/>, draws the overlay centred in the supplied design
/// viewport, and is modal only while a panel is showing (so the game below keeps updating when idle).
/// Re-exposes the view's <see cref="OnTrigger"/>/<see cref="Triggered"/> events.
/// </summary>
public sealed class UpdateOverlayScreen : Screen
{
    readonly IUpdateStatus _status;
    readonly SpriteFont _font;
    readonly Texture2D _white;
    readonly IDesignViewport _viewport;
    readonly UpdateOverlayView _view;

    /// <summary>Raised with the current state when the trigger fires (forwards the view's event).</summary>
    public event Action<UpdateState>? OnTrigger { add => _view.OnTrigger += value; remove => _view.OnTrigger -= value; }
    /// <summary>Paramless convenience (forwards the view's event).</summary>
    public event Action? Triggered { add => _view.Triggered += value; remove => _view.Triggered -= value; }

    /// <summary>The wrapped view (e.g. to retheme at runtime).</summary>
    public UpdateOverlayView View => _view;

    public UpdateOverlayScreen(IUpdateStatus status, SpriteFont font, Texture2D white,
        IDesignViewport viewport, UpdateOverlayTheme? theme = null)
    {
        _status = status;
        _font = font;
        _white = white;
        _viewport = viewport;
        _view = new UpdateOverlayView(theme);
        DrawOrder = 10_000;        // sits on top of game UI
        PassUpdateThrough = true;  // re-evaluated each frame from visibility
    }

    public override bool Update(float dt, bool receivesInput)
    {
        InputState input = receivesInput ? Manager.Input : InputState.Empty;
        bool visible = _view.Update(_status, input, dt);
        PassUpdateThrough = !visible; // modal only while a panel is shown
        return receivesInput && visible;
    }

    public override void Draw(SpriteBatch batch) =>
        _view.Draw(batch, _font, _white, new Rect(0, 0, _viewport.Width, _viewport.Height), _status);
}
