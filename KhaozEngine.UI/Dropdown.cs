using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using KhaozEngine.Graphics;
using KhaozEngine.Input;

namespace KhaozEngine.UI;

/// <summary>
/// A single option in a <see cref="Dropdown"/>.
/// </summary>
/// <param name="Label">Display text shown in the dropdown.</param>
/// <param name="Value">The integer value associated with this option.</param>
public readonly record struct DropdownOption(string Label, int Value);

/// <summary>
/// Reusable dropdown selector component. Shows the currently selected option
/// in a trigger button; when tapped, expands a list of all options below.
/// Tapping an option selects it and closes the list; tapping outside dismisses.
///
/// Because the expanded option list may extend beyond a scissor-clipped panel,
/// rendering is split into two phases:
/// <list type="bullet">
///   <item><see cref="Draw"/>  -- renders the trigger button (call inside scissor clip)</item>
///   <item><see cref="DrawOverlay"/>  -- renders the expanded option list (call outside scissor clip)</item>
/// </list>
///
/// Usage:
/// 1. Create with options, renderer, input, font
/// 2. Call <see cref="Update"/> each frame with trigger bounds
/// 3. Call <see cref="Draw"/> inside panel content
/// 4. Call <see cref="DrawOverlay"/> after panel EndDraw
/// 5. Read <see cref="SelectedValue"/> or check <see cref="WasChanged"/>
/// </summary>
public sealed class Dropdown
{
    private readonly PrimitiveRenderer _renderer;
    private readonly InputManager _input;
    private readonly SpriteFont _font;
    private readonly List<DropdownOption> _options;

    private int _selectedIndex;
    private Rectangle _triggerBounds;

    /// <summary>Whether the dropdown list is currently expanded.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>True on the frame the selection changed.</summary>
    public bool WasChanged { get; private set; }

    /// <summary>The currently selected option's value.</summary>
    public int SelectedValue => _options[_selectedIndex].Value;

    /// <summary>The currently selected option's label.</summary>
    public string SelectedLabel => _options[_selectedIndex].Label;

    /// <summary>
    /// Creates a new Dropdown.
    /// </summary>
    /// <param name="options">The options to display. Must have at least one.</param>
    /// <param name="renderer">Primitive renderer for drawing.</param>
    /// <param name="input">Input manager for tap detection.</param>
    /// <param name="font">Font for option labels.</param>
    public Dropdown(List<DropdownOption> options, PrimitiveRenderer renderer, InputManager input, SpriteFont font)
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
    /// Updates dropdown state  -- open/close, option selection, dismiss.
    /// Call each frame.
    /// </summary>
    /// <param name="triggerBounds">Bounds of the trigger button in virtual coordinates.</param>
    public void Update(Rectangle triggerBounds)
    {
        WasChanged = false;
        _triggerBounds = triggerBounds;

        if (IsOpen)
        {
            // Tap on trigger -> close
            if (_input.IsTapIn(triggerBounds))
            {
                IsOpen = false;
                return;
            }

            // Tap on an option -> select + close
            for (int i = 0; i < _options.Count; i++)
            {
                Rectangle optBounds = GetOptionBounds(i);
                if (_input.IsTapIn(optBounds))
                {
                    if (_selectedIndex != i)
                    {
                        _selectedIndex = i;
                        WasChanged = true;
                    }
                    IsOpen = false;
                    return;
                }
            }

            // Tap outside dropdown area -> dismiss
            if (_input.IsReleasedOutside(GetFullBounds()))
            {
                IsOpen = false;
            }
        }
        else
        {
            // Tap on trigger -> open
            if (_input.IsTapIn(triggerBounds))
            {
                IsOpen = true;
            }
        }
    }

    /// <summary>
    /// Draws the trigger button (closed state display).
    /// Call inside the panel's scissor-clipped draw region.
    /// </summary>
    public void Draw(SpriteBatch spriteBatch, Rectangle bounds, float alpha = 1f)
    {
        _triggerBounds = bounds;

        Color bg = new Color(25, 25, 35) * alpha;
        Color border = IsOpen ? new Color(80, 140, 220) * alpha : new Color(45, 45, 55) * alpha;

        _renderer.DrawFilledRect(spriteBatch, bounds, bg);
        _renderer.DrawRect(spriteBatch, bounds, border, 1);

        // Selected label
        Color textColor = new Color(200, 205, 215) * alpha;
        int textY = bounds.Y + (bounds.Height - _font.LineSpacing) / 2;
        TextHelper.Draw(spriteBatch, _font, SelectedLabel, bounds.X + 6, textY, textColor);

        // Chevron indicator
        Color chevronColor = new Color(120, 125, 140) * alpha;
        int cx = bounds.Right - 14;
        int cy = bounds.Y + bounds.Height / 2;
        if (IsOpen)
        {
            // Upward chevron
            _renderer.DrawLine(spriteBatch, new Vector2(cx - 4, cy + 2), new Vector2(cx, cy - 2), chevronColor, 1);
            _renderer.DrawLine(spriteBatch, new Vector2(cx, cy - 2), new Vector2(cx + 4, cy + 2), chevronColor, 1);
        }
        else
        {
            // Downward chevron
            _renderer.DrawLine(spriteBatch, new Vector2(cx - 4, cy - 2), new Vector2(cx, cy + 2), chevronColor, 1);
            _renderer.DrawLine(spriteBatch, new Vector2(cx, cy + 2), new Vector2(cx + 4, cy - 2), chevronColor, 1);
        }
    }

    /// <summary>
    /// Draws the expanded option list when open. Call AFTER the panel's EndDraw
    /// (outside scissor clip) so the list renders on top of other content.
    /// </summary>
    public void DrawOverlay(SpriteBatch spriteBatch, float alpha = 1f)
    {
        if (!IsOpen) return;

        int optionHeight = _triggerBounds.Height;
        int listHeight = _options.Count * optionHeight;
        var listBounds = new Rectangle(
            _triggerBounds.X, _triggerBounds.Bottom,
            _triggerBounds.Width, listHeight);

        // List background
        _renderer.DrawFilledRect(spriteBatch, listBounds, new Color(18, 18, 28) * alpha);
        _renderer.DrawRect(spriteBatch, listBounds, new Color(50, 55, 65) * alpha, 1);

        // Options
        for (int i = 0; i < _options.Count; i++)
        {
            Rectangle optBounds = GetOptionBounds(i);
            bool isSelected = i == _selectedIndex;
            bool isHovered = _input.IsPointerIn(optBounds);

            if (isSelected)
                _renderer.DrawFilledRect(spriteBatch, optBounds, new Color(35, 50, 75) * alpha);
            else if (isHovered)
                _renderer.DrawFilledRect(spriteBatch, optBounds, new Color(28, 32, 45) * alpha);

            Color optColor = isSelected
                ? new Color(140, 200, 255) * alpha
                : new Color(200, 205, 215) * alpha;
            int textY = optBounds.Y + (optBounds.Height - _font.LineSpacing) / 2;
            TextHelper.Draw(spriteBatch, _font, _options[i].Label, optBounds.X + 6, textY, optColor);

            // Separator between options (except last)
            if (i < _options.Count - 1)
            {
                int sepY = optBounds.Bottom - 1;
                _renderer.DrawFilledRect(spriteBatch,
                    new Rectangle(optBounds.X + 4, sepY, optBounds.Width - 8, 1),
                    new Color(35, 38, 48) * alpha);
            }
        }
    }

    private Rectangle GetOptionBounds(int index)
    {
        int optionHeight = _triggerBounds.Height;
        return new Rectangle(
            _triggerBounds.X,
            _triggerBounds.Bottom + index * optionHeight,
            _triggerBounds.Width,
            optionHeight);
    }

    private Rectangle GetFullBounds()
    {
        int optionHeight = _triggerBounds.Height;
        return new Rectangle(
            _triggerBounds.X,
            _triggerBounds.Y,
            _triggerBounds.Width,
            _triggerBounds.Height + _options.Count * optionHeight);
    }
}
