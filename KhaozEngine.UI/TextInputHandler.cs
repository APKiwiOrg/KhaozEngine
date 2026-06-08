using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using KhaozEngine.Input;

namespace KhaozEngine.UI;

/// <summary>
/// Reusable text input state and keyboard handling logic. Holds the mutable text buffer,
/// caret blink timer, and processes keyboard input each frame via the shared InputManager.
/// </summary>
public sealed class TextInputHandler
{
    private readonly int maxLength;
    private readonly Func<char, bool> charValidator;

    /// <summary>The current text value. Callers may read or replace this directly.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Elapsed seconds driving the caret blink animation.</summary>
    public float CaretBlinkTimer { get; private set; }

    /// <summary>True when the user pressed Ctrl/Cmd+V this frame. Cleared each ProcessInput.</summary>
    public bool PasteRequested { get; private set; }

    /// <summary>True when a Backspace or Delete was handled this frame. Cleared each ProcessInput.</summary>
    public bool TextDeleted { get; private set; }

    public TextInputHandler(int maxLength, Func<char, bool> charValidator)
    {
        ArgumentNullException.ThrowIfNull(charValidator);
        this.maxLength = maxLength;
        this.charValidator = charValidator;
    }

    /// <summary>Advances the caret blink timer. Call once per frame from Update().</summary>
    public void UpdateBlinkTimer(GameTime gameTime)
    {
        CaretBlinkTimer += (float)gameTime.ElapsedGameTime.TotalSeconds;
    }

    /// <summary>
    /// Processes keyboard input for the text field. Handles Backspace, Delete,
    /// A-Z, 0-9, NumPad0-9, and Ctrl/Cmd+V paste detection.
    /// </summary>
    /// <returns>True if any input was consumed.</returns>
    public bool ProcessInput(InputManager input, PlayerIndex? controllingPlayer)
    {
        PasteRequested = false;
        TextDeleted = false;
        bool consumed = false;

        if (input.IsNewKeyPress(Keys.Back, controllingPlayer, out _))
        {
            if (Text.Length > 0)
            {
                Text = Text[..^1];
                TextDeleted = true;
            }
            consumed = true;
        }

        if (input.IsNewKeyPress(Keys.Delete, controllingPlayer, out _))
        {
            Text = string.Empty;
            TextDeleted = true;
            consumed = true;
        }

        if (IsPasteShortcutPressed(input, controllingPlayer))
        {
            PasteRequested = true;
            return true;
        }

        for (Keys key = Keys.A; key <= Keys.Z; key++)
        {
            if (!input.IsNewKeyPress(key, controllingPlayer, out _)) continue;
            char ch = (char)('a' + (key - Keys.A));
            if (charValidator(ch) && Text.Length < maxLength)
            {
                Text += ch;
                consumed = true;
            }
        }

        for (Keys key = Keys.D0; key <= Keys.D9; key++)
        {
            if (!input.IsNewKeyPress(key, controllingPlayer, out _)) continue;
            char ch = (char)('0' + (key - Keys.D0));
            if (charValidator(ch) && Text.Length < maxLength)
            {
                Text += ch;
                consumed = true;
            }
        }

        for (Keys key = Keys.NumPad0; key <= Keys.NumPad9; key++)
        {
            if (!input.IsNewKeyPress(key, controllingPlayer, out _)) continue;
            char ch = (char)('0' + (key - Keys.NumPad0));
            if (charValidator(ch) && Text.Length < maxLength)
            {
                Text += ch;
                consumed = true;
            }
        }

        return consumed;
    }

    // Detects Ctrl+V or Cmd+V (macOS).
    private static bool IsPasteShortcutPressed(InputManager input, PlayerIndex? controllingPlayer)
    {
        if (!input.IsNewKeyPress(Keys.V, controllingPlayer, out _)) return false;
        bool controlPressed = input.IsKeyDown(Keys.LeftControl) || input.IsKeyDown(Keys.RightControl);
        bool commandPressed = input.IsKeyDown(Keys.LeftWindows) || input.IsKeyDown(Keys.RightWindows);
        return controlPressed || commandPressed;
    }
}
