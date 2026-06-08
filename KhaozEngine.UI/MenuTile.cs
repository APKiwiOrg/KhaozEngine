using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using KhaozEngine.Input;

namespace KhaozEngine.UI;

/// <summary>
/// Large tile-style button for menu grids. Displays a colored icon placeholder,
/// label, and optional subtitle. Supports hover, pressed, and disabled states
/// with consistent theming.
///
/// Usage:
/// 1. Create with fonts, renderer, input
/// 2. Set Label, Subtitle, IconGlyph, AccentColor
/// 3. Call <see cref="Update"/> each frame with bounds
/// 4. Check <see cref="WasClicked"/> for tap detection
/// 5. Call <see cref="Draw"/> to render
/// </summary>
public sealed class MenuTile
{
    private readonly PrimitiveRenderer _renderer;
    private readonly InputManager _input;
    private readonly SpriteFont _labelFont;
    private readonly SpriteFont _subtitleFont;

    /// <summary>Tile label text.</summary>
    public string Label { get; set; } = "";

    /// <summary>Optional subtitle text below the label.</summary>
    public string Subtitle { get; set; } = "";

    /// <summary>Icon placeholder letter/symbol rendered inside the icon area.</summary>
    public string IconGlyph { get; set; } = "";

    /// <summary>Accent color for the icon area border and fill.</summary>
    public Color AccentColor { get; set; } = new(100, 200, 255);

    /// <summary>Whether the tile is interactive.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Current visual state (read-only, updated by <see cref="Update"/>).</summary>
    public ButtonState State { get; private set; } = ButtonState.Normal;

    /// <summary>True on the frame the tile was successfully tapped.</summary>
    public bool WasClicked { get; private set; }

    /// <summary>
    /// Creates a new MenuTile.
    /// </summary>
    public MenuTile(SpriteFont labelFont, SpriteFont subtitleFont, PrimitiveRenderer renderer, InputManager input)
    {
        _labelFont = labelFont;
        _subtitleFont = subtitleFont;
        _renderer = renderer;
        _input = input;
    }

    /// <summary>
    /// Updates tile state (hover, pressed, clicked). Call each frame.
    /// </summary>
    public void Update(Rectangle bounds)
    {
        WasClicked = false;

        if (!Enabled)
        {
            State = ButtonState.Disabled;
            return;
        }

        if (_input.IsPressingIn(bounds))
            State = ButtonState.Pressed;
        else if (_input.IsHoveringIn(bounds))
            State = ButtonState.Hover;
        else
            State = ButtonState.Normal;

        if (_input.IsTapIn(bounds))
            WasClicked = true;
    }

    /// <summary>
    /// Draws the tile at the given bounds with optional alpha modulation.
    /// Must be inside an active SpriteBatch.
    /// </summary>
    public void Draw(SpriteBatch spriteBatch, Rectangle bounds, float alpha = 1f)
    {
        Color bg = State switch
        {
            ButtonState.Hover => new Color(30, 35, 50),
            ButtonState.Pressed => new Color(15, 18, 28),
            ButtonState.Disabled => new Color(18, 18, 22),
            _ => new Color(22, 25, 38)
        };

        Color border = State switch
        {
            ButtonState.Hover => new Color(60, 70, 90),
            ButtonState.Pressed => new Color(40, 45, 60),
            ButtonState.Disabled => new Color(30, 30, 38),
            _ => new Color(40, 45, 58)
        };

        _renderer.DrawFilledRect(spriteBatch, bounds, bg * alpha);
        _renderer.DrawRect(spriteBatch, bounds, border * alpha, 1);

        // Icon placeholder  -- centered square with accent tint
        int iconSize = Math.Min(bounds.Width, bounds.Height) / 3;
        int iconX = bounds.X + bounds.Width / 2 - iconSize / 2;
        int iconY = bounds.Y + bounds.Height / 3 - iconSize / 2;

        Color iconColor = Enabled ? AccentColor * alpha : new Color(50, 50, 60) * alpha;
        _renderer.DrawFilledRect(spriteBatch, new Rectangle(iconX, iconY, iconSize, iconSize), iconColor * 0.3f);
        _renderer.DrawRect(spriteBatch, new Rectangle(iconX, iconY, iconSize, iconSize), iconColor, 1);

        if (!string.IsNullOrEmpty(IconGlyph))
        {
            TextHelper.DrawCenteredInRect(spriteBatch, _labelFont, IconGlyph,
                new Rectangle(iconX, iconY, iconSize, iconSize), iconColor);
        }

        // Label below icon
        Color labelColor = State switch
        {
            ButtonState.Disabled => new Color(60, 60, 70) * alpha,
            ButtonState.Hover => new Color(220, 230, 245) * alpha,
            _ => Color.White * alpha
        };

        int labelY = iconY + iconSize + 10;
        TextHelper.DrawCentered(spriteBatch, _labelFont, Label,
            bounds.X + bounds.Width / 2, labelY, labelColor);

        // Subtitle
        if (!string.IsNullOrEmpty(Subtitle))
        {
            Color subColor = Enabled
                ? new Color(120, 125, 140) * alpha
                : new Color(45, 45, 55) * alpha;
            int subY = labelY + _labelFont.LineSpacing + 2;
            TextHelper.DrawCentered(spriteBatch, _subtitleFont, Subtitle,
                bounds.X + bounds.Width / 2, subY, subColor);
        }
    }
}
