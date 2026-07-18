using System;
using KhaozEngine.Render2D;
using KhaozEngine.Updates;
using KhaozEngine.Windowing;
using KhaozEngine.Primitives;

namespace KhaozEngine.Gui;

/// <summary>
/// Drop-in <see cref="Screen"/> wrapping <see cref="UpdateOverlayView"/> for stack-based games. It reads
/// input from the owning <see cref="ScreenStack"/>, draws the overlay centred in the supplied design
/// viewport, and is modal only for a required update or the apply step. An optional prompt stays non-modal,
/// so the game below keeps simulating and keeps its own input, and the overlay consumes only the frame its
/// trigger fires. Re-exposes the view's <see cref="OnTrigger"/>/<see cref="Triggered"/> events.
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
        PassUpdateThrough = true;  // re-evaluated each frame from modality
    }

    public override bool Update(float dt, bool receivesInput)
    {
        InputState input = receivesInput ? Manager.Input : InputState.Empty;
        bool visible = _view.Update(_status, input, dt);
        // Modal only when the update demands attention: a required update, or the apply step (the process is
        // about to relaunch). An optional prompt stays non-modal so the game below keeps simulating.
        bool modal = visible && (_status.IsRequired || _status.State == UpdateState.Applying);
        PassUpdateThrough = !modal;
        // While modal, consume all input; while non-modal, consume only the frame the trigger fires so the game
        // keeps receiving its own input. Never a bare true, and false whenever !receivesInput.
        return modal ? receivesInput : _view.TriggeredThisFrame;
    }

    public override void Draw(SpriteBatch batch) =>
        // WindowBounds, not (0,0,Width,Height): under a letterbox scale the dim scrim must cover the whole
        // window (bars included), otherwise the game shows through at the edges. The panel stays centred (the
        // letterbox is symmetric, so WindowBounds shares the design centre).
        _view.Draw(batch, _font, _white, _viewport.WindowBounds, _status);
}
