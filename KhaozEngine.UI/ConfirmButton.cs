using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using KhaozEngine.Graphics;
using KhaozEngine.Input;

namespace KhaozEngine.UI;

/// <summary>
/// A button that requires two taps to activate: the first tap switches to a
/// confirmation state (text/style change), the second tap within a timeout
/// fires the confirmed action. Resets to the initial state on timeout.
/// Wraps <see cref="Button"/> for rendering and input handling.
/// </summary>
public sealed class ConfirmButton
{
    private readonly Button _button;
    private readonly string _normalText;
    private readonly string _confirmText;
    private readonly ButtonStyle _normalStyle;
    private readonly ButtonStyle _confirmStyle;
    private readonly double _confirmTimeout;

    private bool _confirming;
    private double _confirmTimer;

    /// <summary>True on the frame the user completed the two-tap confirmation.</summary>
    public bool WasConfirmed { get; private set; }

    /// <summary>Whether the button is interactive.</summary>
    public bool Enabled
    {
        get => _button.Enabled;
        set => _button.Enabled = value;
    }

    /// <summary>
    /// Creates a new ConfirmButton.
    /// </summary>
    /// <param name="normalText">Label shown in the default state.</param>
    /// <param name="confirmText">Label shown after the first tap (confirmation pending).</param>
    /// <param name="normalStyle">Style in the default state.</param>
    /// <param name="confirmStyle">Style during confirmation (typically <see cref="ButtonStyle.Danger"/>).</param>
    /// <param name="font">Font for the button label.</param>
    /// <param name="renderer">Primitive renderer.</param>
    /// <param name="input">Input manager.</param>
    /// <param name="confirmTimeout">Seconds before the confirmation state resets. Default 3.0.</param>
    public ConfirmButton(
        string normalText, string confirmText,
        ButtonStyle normalStyle, ButtonStyle confirmStyle,
        SpriteFont font, PrimitiveRenderer renderer, InputManager input,
        double confirmTimeout = 3.0)
    {
        _normalText = normalText;
        _confirmText = confirmText;
        _normalStyle = normalStyle;
        _confirmStyle = confirmStyle;
        _confirmTimeout = confirmTimeout;

        _button = new Button(normalStyle, font, renderer, input)
        {
            Text = normalText
        };
    }

    /// <summary>
    /// Updates button state and confirmation logic. Call each frame.
    /// </summary>
    /// <param name="bounds">The button's current screen bounds in virtual coordinates.</param>
    /// <param name="deltaSeconds">Real elapsed seconds this frame.</param>
    public void Update(Rectangle bounds, double deltaSeconds)
    {
        WasConfirmed = false;
        _button.Update(bounds);

        if (_button.WasClicked && _button.Enabled)
        {
            if (_confirming)
            {
                WasConfirmed = true;
                Reset();
            }
            else
            {
                _confirming = true;
                _confirmTimer = _confirmTimeout;
                _button.Text = _confirmText;
                _button.Style = _confirmStyle;
            }
        }

        if (_confirming)
        {
            _confirmTimer -= deltaSeconds;
            if (_confirmTimer <= 0)
                Reset();
        }
    }

    /// <summary>
    /// Draws the button at the given bounds. Must be inside an active SpriteBatch.
    /// </summary>
    public void Draw(SpriteBatch spriteBatch, Rectangle bounds)
    {
        _button.Draw(spriteBatch, bounds);
    }

    /// <summary>
    /// Draws the button with alpha modulation.
    /// </summary>
    public void Draw(SpriteBatch spriteBatch, Rectangle bounds, float alpha)
    {
        _button.Draw(spriteBatch, bounds, alpha);
    }

    /// <summary>
    /// Resets to the initial (non-confirming) state.
    /// </summary>
    public void Reset()
    {
        _confirming = false;
        _confirmTimer = 0;
        _button.Text = _normalText;
        _button.Style = _normalStyle;
    }
}
