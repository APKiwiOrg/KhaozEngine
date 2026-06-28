using System;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// Headless text-entry helper: maps a frame's <see cref="InputState"/> key presses to typed characters and
    /// applies them to a string buffer (append printable, Backspace deletes). Works off the engine-native
    /// <see cref="Key"/> enum + shift state, so it needs no SDL text-input plumbing and is fully unit-testable.
    /// Holding Ctrl or Super (Cmd) suppresses character entry so shortcut chords like Ctrl+V / Cmd+V don't type
    /// the letter into the field; Shift is a text modifier and still applies.
    /// US keyboard layout for shifted symbols. Used by the <see cref="TextInput"/> widget.
    /// Hold-to-repeat works because it acts on <see cref="InputState.WasTyped"/> (press edge OR an OS auto-repeat
    /// tick), so a held Backspace or character key deletes / types at the OS repeat rate.
    /// Limitations vs a real IME: no locale layouts, dead keys, or composition.
    /// </summary>
    public static class TextEntry
    {
        /// <summary>
        /// Returns <paramref name="current"/> after applying this frame's typed keys: Backspace removes the
        /// last char; printable keys append (subject to <paramref name="maxLength"/> and <paramref name="filter"/>).
        /// Acts on the press-or-repeat signal (<see cref="InputState.WasTyped"/>), so holding a key auto-repeats.
        /// </summary>
        public static string Apply(string current, InputState input, int maxLength = int.MaxValue, Func<char, bool>? filter = null)
        {
            if (input.WasTyped(Key.Backspace) && current.Length > 0)
                current = current[..^1];

            // Ctrl/Super held = a shortcut chord (Ctrl+V / Cmd+V paste, etc.), not text entry.
            // Don't type the printable key (Backspace above still works); Shift is a text modifier, not a chord.
            // This gate runs before the printable loop, so it also blocks repeat ticks (no machine-gunning a chord key).
            if (input.IsDown(Key.LeftControl) || input.IsDown(Key.RightControl)
                || input.IsDown(Key.LeftSuper) || input.IsDown(Key.RightSuper))
                return current;

            bool shift = input.IsDown(Key.LeftShift) || input.IsDown(Key.RightShift);

            // Iterate the printable range in enum order for deterministic multi-key frames.
            for (Key k = Key.A; k <= Key.Grave; k++)
            {
                if (!input.WasTyped(k)) continue;
                if (!TryMapChar(k, shift, out char c)) continue;
                if (current.Length >= maxLength) continue;
                if (filter != null && !filter(c)) continue;
                current += c;
            }
            return current;
        }

        static bool TryMapChar(Key k, bool shift, out char c)
        {
            if (k >= Key.A && k <= Key.Z)
            {
                c = (char)((shift ? 'A' : 'a') + (k - Key.A));
                return true;
            }
            if (k >= Key.D0 && k <= Key.D9)
            {
                c = shift ? ")!@#$%^&*("[k - Key.D0] : (char)('0' + (k - Key.D0));
                return true;
            }
            switch (k)
            {
                case Key.Space: c = ' '; return true;
                case Key.Minus: c = shift ? '_' : '-'; return true;
                case Key.Equals: c = shift ? '+' : '='; return true;
                case Key.LeftBracket: c = shift ? '{' : '['; return true;
                case Key.RightBracket: c = shift ? '}' : ']'; return true;
                case Key.Backslash: c = shift ? '|' : '\\'; return true;
                case Key.Semicolon: c = shift ? ':' : ';'; return true;
                case Key.Apostrophe: c = shift ? '"' : '\''; return true;
                case Key.Comma: c = shift ? '<' : ','; return true;
                case Key.Period: c = shift ? '>' : '.'; return true;
                case Key.Slash: c = shift ? '?' : '/'; return true;
                case Key.Grave: c = shift ? '~' : '`'; return true;
                default: c = '\0'; return false;
            }
        }
    }
}
