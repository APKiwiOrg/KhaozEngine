using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using KhaozEngine.Input;

namespace KhaozEngine.UI;

/// <summary>
/// A single selectable quantity option (e.g., "1x", "10x", "Max").
/// </summary>
/// <param name="Label">Display text shown on the button.</param>
/// <param name="Value">
/// The quantity value. Use <see cref="int.MaxValue"/> for "Max" (buy as many as affordable).
/// </param>
public readonly record struct QuantityOption(string Label, int Value);

/// <summary>
/// Reusable quantity selector component. Displays a horizontal row of toggle buttons
/// (e.g., 1x, 10x, 100x, Max) and tracks which is selected.
///
/// The set of visible options is configurable per instance. Each caller decides which
/// options to show by passing a list of <see cref="QuantityOption"/> values.
///
/// Usage:
/// 1. Create with options list, fonts, and renderer
/// 2. Call <see cref="Update"/> each frame with the bounds where the selector is drawn
/// 3. Call <see cref="Draw"/> to render the buttons
/// 4. Read <see cref="SelectedValue"/> to get the current quantity
/// </summary>
public sealed class QuantitySelector
{
    private readonly PrimitiveRenderer _renderer;
    private readonly InputManager _input;
    private readonly SpriteFont _font;
    private readonly List<QuantityOption> _options;

    private int _selectedIndex;

    /// <summary>
    /// The currently selected quantity value.
    /// </summary>
    public int SelectedValue => _options[_selectedIndex].Value;

    /// <summary>
    /// The currently selected option label.
    /// </summary>
    public string SelectedLabel => _options[_selectedIndex].Label;

    /// <summary>
    /// Creates a new QuantitySelector with the given options.
    /// The first option is selected by default.
    /// </summary>
    /// <param name="options">The quantity options to display. Must have at least one.</param>
    /// <param name="renderer">Primitive renderer for drawing button backgrounds.</param>
    /// <param name="input">Input manager for tap detection.</param>
    /// <param name="font">Font for button labels.</param>
    public QuantitySelector(List<QuantityOption> options, PrimitiveRenderer renderer, InputManager input, SpriteFont font)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Count == 0) throw new ArgumentException("At least one option is required.", nameof(options));

        _options = options;
        _renderer = renderer;
        _input = input;
        _font = font;
    }

    /// <summary>
    /// Selects the option with the given value. No-op if value is not found.
    /// </summary>
    public void SelectByValue(int value)
    {
        for (int i = 0; i < _options.Count; i++)
        {
            if (_options[i].Value == value)
            {
                _selectedIndex = i;
                return;
            }
        }
    }

    /// <summary>
    /// Checks for taps on option buttons within the given bounds.
    /// Call each frame during the owning screen's Update.
    /// </summary>
    /// <param name="bounds">The rectangle where the selector is drawn, in virtual coordinates.</param>
    public void Update(Rectangle bounds)
    {
        for (int i = 0; i < _options.Count; i++)
        {
            Rectangle buttonBounds = GetButtonBounds(i, bounds);
            if (_input.IsTapIn(buttonBounds))
            {
                _selectedIndex = i;
                break;
            }
        }
    }

    /// <summary>
    /// Draws the selector buttons right-aligned within the given bounds.
    /// Buttons are sized to fit their label text, not stretched to fill.
    /// Must be called inside an active SpriteBatch.Begin/End.
    /// </summary>
    /// <param name="spriteBatch">The active SpriteBatch.</param>
    /// <param name="bounds">The rectangle to draw in, in virtual coordinates.</param>
    public void Draw(SpriteBatch spriteBatch, Rectangle bounds)
    {
        int hPad = 5;
        int gap = 2;
        int bw = GetNormalizedButtonWidth(hPad);
        int totalWidth = _options.Count * bw + (_options.Count - 1) * gap;

        int x = bounds.Right - totalWidth;

        for (int i = 0; i < _options.Count; i++)
        {
            bool isSelected = i == _selectedIndex;
            var buttonRect = new Rectangle(x, bounds.Y, bw, bounds.Height);

            Color bgColor = isSelected ? new Color(40, 60, 90) : new Color(25, 25, 35);
            _renderer.DrawFilledRect(spriteBatch, buttonRect, bgColor);

            Color borderColor = isSelected ? new Color(80, 140, 220) : new Color(45, 45, 55);
            _renderer.DrawRect(spriteBatch, buttonRect, borderColor, 1);

            string label = _options[i].Label;
            Color labelColor = isSelected ? new Color(140, 200, 255) : new Color(120, 120, 130);
            Rectangle buttonRect2 = new(x, bounds.Y, bw, bounds.Height);
            TextHelper.DrawCenteredInRect(spriteBatch, _font, label, buttonRect2, labelColor);

            x += bw + gap;
        }
    }

    /// <summary>
    /// Returns the bounds of a specific button for hit-testing. Matches Draw layout.
    /// </summary>
    private Rectangle GetButtonBounds(int index, Rectangle bounds)
    {
        int hPad = 5;
        int gap = 2;
        int bw = GetNormalizedButtonWidth(hPad);
        int totalWidth = _options.Count * bw + (_options.Count - 1) * gap;
        int x = bounds.Right - totalWidth + index * (bw + gap);
        return new Rectangle(x, bounds.Y, bw, bounds.Height);
    }

    /// <summary>
    /// Returns the total rendered width of all buttons (including gaps).
    /// Use this to calculate tight exclusion zones for overlapping input regions.
    /// </summary>
    public int GetTotalWidth()
    {
        int hPad = 5;
        int gap = 2;
        int bw = GetNormalizedButtonWidth(hPad);
        return _options.Count * bw + (_options.Count - 1) * gap;
    }

    /// <summary>
    /// Finds the widest label and returns a uniform button width for all options.
    /// </summary>
    private int GetNormalizedButtonWidth(int hPad)
    {
        int maxLabelWidth = 0;
        for (int i = 0; i < _options.Count; i++)
        {
            int w = (int)_font.MeasureString(_options[i].Label).X;
            if (w > maxLabelWidth) maxLabelWidth = w;
        }
        return maxLabelWidth + hPad * 2;
    }
}
