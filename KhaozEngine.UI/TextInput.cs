using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using KhaozEngine.Input;

namespace KhaozEngine.UI;

/// <summary>
/// Reusable single-line text input component.
/// Hooks <see cref="GameWindow.TextInput"/> for keyboard input (desktop and mobile).
/// Renders a bordered field with the current text, a blinking cursor, and
/// placeholder text when empty.
///
/// Usage:
/// 1. Create with a <see cref="GameWindow"/>, font, renderer, input manager
/// 2. Call <see cref="Update"/> each frame with current bounds
/// 3. Call <see cref="Draw"/> to render
/// 4. Read <see cref="Text"/> for the current value
/// 5. Call <see cref="Dispose"/> when done to unhook events
/// </summary>
public sealed class TextInput : IDisposable
{
    private readonly GameWindow _window;
    private readonly SpriteFont _font;
    private readonly PrimitiveRenderer _renderer;
    private readonly InputManager _input;

    private double _cursorBlinkTimer;
    private bool _cursorVisible = true;
    private bool _hooked;
    private string _previousText = "";

    // Visual constants
    private static readonly Color FieldBackground = new(30, 30, 40);
    private static readonly Color FieldBorder = new(80, 80, 100);
    private static readonly Color FieldBorderFocused = new(120, 140, 200);
    private static readonly Color TextColor = new(230, 230, 240);
    private static readonly Color PlaceholderColor = new(100, 100, 120);
    private static readonly Color CursorColor = new(180, 200, 255);
    private const float CursorBlinkRate = 0.5f;
    private const int FieldPaddingX = 8;
    private const int CursorWidth = 2;

    /// <summary>Current text value.</summary>
    public string Text { get; private set; } = "";

    /// <summary>Maximum number of characters allowed.</summary>
    public int MaxLength { get; set; } = 20;

    /// <summary>Placeholder text shown when the field is empty.</summary>
    public string Placeholder { get; set; } = "";

    /// <summary>Whether the field currently has input focus.</summary>
    public bool IsFocused { get; private set; }

    /// <summary>
    /// Optional character filter. Return true to accept the character.
    /// When null, all printable characters are accepted.
    /// </summary>
    public Func<char, bool>? CharFilter { get; set; }

    /// <summary>True on the frame when <see cref="Text"/> changed.</summary>
    public bool TextChanged { get; private set; }

    /// <summary>
    /// Creates a new TextInput.
    /// </summary>
    /// <param name="window">The game window (for TextInput event subscription).</param>
    /// <param name="font">Font used to render text.</param>
    /// <param name="renderer">Primitive renderer for field background and border.</param>
    /// <param name="input">Input manager for tap detection.</param>
    public TextInput(GameWindow window, SpriteFont font, PrimitiveRenderer renderer, InputManager input)
    {
        _window = window;
        _font = font;
        _renderer = renderer;
        _input = input;
    }

    /// <summary>
    /// Updates input focus and cursor blink. Call each frame.
    /// </summary>
    /// <param name="bounds">The field's current screen bounds in virtual coordinates.</param>
    /// <param name="deltaSeconds">Real-time delta in seconds.</param>
    public void Update(Rectangle bounds, double deltaSeconds)
    {
        // Detect changes from OnTextInput events that fired between frames
        TextChanged = Text != _previousText;
        _previousText = Text;

        // Tap inside field = focus; tap outside = unfocus
        if (_input.IsTapIn(bounds))
        {
            if (!IsFocused)
                Focus();
        }
        else if (_input.IsTapIn(new Rectangle(0, 0, 9999, 9999)) && !_input.IsTapIn(bounds))
        {
            // Tapped somewhere outside the field
            if (IsFocused)
                Unfocus();
        }

        // Cursor blink
        if (IsFocused)
        {
            _cursorBlinkTimer += deltaSeconds;
            if (_cursorBlinkTimer >= CursorBlinkRate)
            {
                _cursorBlinkTimer -= CursorBlinkRate;
                _cursorVisible = !_cursorVisible;
            }
        }
    }

    /// <summary>
    /// Draws the text input field. Must be inside an active SpriteBatch.
    /// </summary>
    /// <param name="spriteBatch">Active SpriteBatch.</param>
    /// <param name="bounds">Field rectangle in virtual coordinates.</param>
    /// <param name="alpha">Opacity multiplier (0-1).</param>
    public void Draw(SpriteBatch spriteBatch, Rectangle bounds, float alpha)
    {
        // Background
        _renderer.DrawFilledRect(spriteBatch, bounds, FieldBackground * alpha);

        // Border
        Color borderColor = IsFocused ? FieldBorderFocused : FieldBorder;
        _renderer.DrawRect(spriteBatch, bounds, borderColor * alpha, 1);

        // Text area (inset by padding)
        int textX = bounds.X + FieldPaddingX;
        int textY = bounds.Y + (bounds.Height - _font.LineSpacing) / 2;

        if (Text.Length > 0)
        {
            TextHelper.Draw(spriteBatch, _font, Text, textX, textY, TextColor, alpha);

            // Cursor after text
            if (IsFocused && _cursorVisible)
            {
                Vector2 textSize = _font.MeasureString(Text);
                int cursorX = textX + (int)textSize.X + 1;
                Rectangle cursorRect = new(cursorX, bounds.Y + 4, CursorWidth, bounds.Height - 8);
                _renderer.DrawFilledRect(spriteBatch, cursorRect, CursorColor * alpha);
            }
        }
        else
        {
            // Placeholder
            TextHelper.Draw(spriteBatch, _font, Placeholder, textX, textY, PlaceholderColor, alpha);

            // Cursor at start
            if (IsFocused && _cursorVisible)
            {
                Rectangle cursorRect = new(textX, bounds.Y + 4, CursorWidth, bounds.Height - 8);
                _renderer.DrawFilledRect(spriteBatch, cursorRect, CursorColor * alpha);
            }
        }
    }

    /// <summary>
    /// Programmatically sets the focus state and hooks keyboard events.
    /// </summary>
    public void Focus()
    {
        if (IsFocused) return;
        IsFocused = true;
        _cursorVisible = true;
        _cursorBlinkTimer = 0;
        HookTextInput();
    }

    /// <summary>
    /// Programmatically removes focus and unhooks keyboard events.
    /// </summary>
    public void Unfocus()
    {
        if (!IsFocused) return;
        IsFocused = false;
        UnhookTextInput();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        UnhookTextInput();
    }

    private void HookTextInput()
    {
        if (_hooked) return;
        _window.TextInput += OnTextInput;
        _hooked = true;
    }

    private void UnhookTextInput()
    {
        if (!_hooked) return;
        _window.TextInput -= OnTextInput;
        _hooked = false;
    }

    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        char c = e.Character;

        // Backspace
        if (c == '\b')
        {
            if (Text.Length > 0)
            {
                Text = Text[..^1];
                ResetCursorBlink();
            }
            return;
        }

        // Ignore non-printable
        if (char.IsControl(c))
            return;

        // Max length
        if (Text.Length >= MaxLength)
            return;

        // Character filter
        if (CharFilter != null && !CharFilter(c))
            return;

        Text += c;
        ResetCursorBlink();
    }

    private void ResetCursorBlink()
    {
        _cursorVisible = true;
        _cursorBlinkTimer = 0;
    }
}
