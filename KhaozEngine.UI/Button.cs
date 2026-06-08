using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using KhaozEngine.Input;

namespace KhaozEngine.UI;

/// <summary>
/// Visual state of a button.
/// </summary>
public enum ButtonState
{
    Normal,
    Hover,
    Pressed,
    Disabled
}

/// <summary>
/// Reusable button component with consistent theming and state management.
/// Tracks hover, pressed, and disabled states. Uses <see cref="ButtonStyle"/>
/// presets for visual consistency across the UI.
///
/// Usage:
/// 1. Create with style, font, renderer, input
/// 2. Call <see cref="Update"/> each frame with current bounds
/// 3. Check <see cref="WasClicked"/> for tap detection
/// 4. Call <see cref="Draw"/> to render
/// </summary>
public sealed class Button
{
    private readonly PrimitiveRenderer _renderer;
    private readonly InputManager _input;
    private readonly SpriteFont _font;

    /// <summary>The visual style applied to this button.</summary>
    public ButtonStyle Style { get; set; }

    /// <summary>Button label text.</summary>
    public string Text { get; set; } = "";

    /// <summary>Whether the button is interactive.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Current visual state (read-only, updated by <see cref="Update"/>).</summary>
    public ButtonState State { get; private set; } = ButtonState.Normal;

    /// <summary>True on the frame the button was successfully clicked (tap completed inside bounds).</summary>
    public bool WasClicked { get; private set; }

    /// <summary>
    /// Creates a new Button.
    /// </summary>
    public Button(ButtonStyle style, SpriteFont font, PrimitiveRenderer renderer, InputManager input)
    {
        Style = style;
        _font = font;
        _renderer = renderer;
        _input = input;
    }

    /// <summary>
    /// Updates button state (hover, pressed, clicked). Call each frame.
    /// </summary>
    /// <param name="bounds">The button's current screen bounds in virtual coordinates.</param>
    public void Update(Rectangle bounds)
    {
        WasClicked = false;

        if (!Enabled)
        {
            State = ButtonState.Disabled;
            return;
        }

        if (_input.IsPressingIn(bounds))
        {
            State = ButtonState.Pressed;
        }
        else if (_input.IsHoveringIn(bounds))
        {
            State = ButtonState.Hover;
        }
        else
        {
            State = ButtonState.Normal;
        }

        // Click = press started AND ended inside bounds
        if (_input.IsTapIn(bounds))
        {
            WasClicked = true;
        }
    }

    /// <summary>
    /// Draws the button at the given bounds. Must be inside an active SpriteBatch.
    /// </summary>
    /// <param name="spriteBatch">Active SpriteBatch.</param>
    /// <param name="bounds">Button rectangle in virtual coordinates.</param>
    public void Draw(SpriteBatch spriteBatch, Rectangle bounds)
    {
        Draw(spriteBatch, bounds, 1f);
    }

    /// <summary>
    /// Draws the button with alpha modulation.
    /// </summary>
    public void Draw(SpriteBatch spriteBatch, Rectangle bounds, float alpha)
    {
        Color bg = State switch
        {
            ButtonState.Hover => Style.BackgroundHover,
            ButtonState.Pressed => Style.BackgroundPressed,
            ButtonState.Disabled => Style.BackgroundDisabled,
            _ => Style.BackgroundNormal
        };

        Color border = State switch
        {
            ButtonState.Hover => Style.BorderHover,
            ButtonState.Pressed => Style.BorderPressed,
            ButtonState.Disabled => Style.BorderDisabled,
            _ => Style.BorderNormal
        };

        Color text = State switch
        {
            ButtonState.Hover => Style.TextHover,
            ButtonState.Pressed => Style.TextPressed,
            ButtonState.Disabled => Style.TextDisabled,
            _ => Style.TextNormal
        };

        _renderer.DrawFilledRect(spriteBatch, bounds, bg * alpha);
        _renderer.DrawRect(spriteBatch, bounds, border * alpha, Style.BorderThickness);
        TextHelper.DrawCenteredInRect(spriteBatch, _font, Text, bounds, text * alpha);
    }
}
