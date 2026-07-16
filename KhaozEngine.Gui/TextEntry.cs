using System;
using System.Text;
using KhaozEngine.Platform;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// Headless text-entry helper: maps a frame's <see cref="InputState"/> key presses to typed characters and
    /// applies them to a string buffer (append printable, Backspace deletes). Works off the engine-native
    /// <see cref="Key"/> enum + shift state, so it needs no SDL text-input plumbing and is fully unit-testable.
    /// Holding Ctrl or Super (Cmd) suppresses character entry so shortcut chords don't type the letter into the
    /// field; Shift is a text modifier and still applies. The one chord that is acted on is Ctrl+V / Cmd+V, which
    /// pastes the system clipboard (filtered + length-capped like typed text; opt out with <c>allowPaste: false</c>).
    /// US keyboard layout for shifted symbols. Keypad (numpad) keys type their digit, dot, and operator characters
    /// shift-independently. Used by the <see cref="TextInput"/> widget, and consumable by a
    /// retained custom widget via the <see cref="InputManager"/> overload.
    /// Hold-to-repeat works because it acts on <see cref="InputState.WasTyped"/> (press edge OR an OS auto-repeat
    /// tick), so a held Backspace or character key deletes / types at the OS repeat rate.
    /// A <c>filter</c> is validated against the buffer as it accumulates THIS call, not a snapshot taken before the
    /// call started: a multi-key frame (several keys typed the same tick) and a paste both feed the filter each
    /// already-admitted char first, so a stateful filter (e.g. <see cref="NumberField"/>'s "at most one dot") sees
    /// every earlier admission within the same call, not a stale pre-call buffer.
    /// Limitations vs a real IME: no locale layouts, dead keys, or composition.
    /// </summary>
    public static class TextEntry
    {
        /// <summary>
        /// Returns <paramref name="current"/> after applying this frame's typed keys: Backspace removes the
        /// last char; printable keys append (subject to <paramref name="maxLength"/> and <paramref name="filter"/>).
        /// Acts on the press-or-repeat signal (<see cref="InputState.WasTyped"/>), so holding a key auto-repeats.
        /// When <paramref name="allowPaste"/> is set (the default), a Ctrl+V / Cmd+V chord appends the system
        /// clipboard text (<see cref="Clipboard.TryGetClipboardText"/>) through the same <paramref name="filter"/>
        /// and <paramref name="maxLength"/> path as typed characters; pass <c>false</c> to suppress paste.
        /// <paramref name="filter"/> receives the buffer accumulated so far THIS call (not the pre-call
        /// <paramref name="current"/>) alongside the candidate char, so it can reject e.g. a second dot admitted
        /// earlier in the same multi-key frame or paste.
        /// </summary>
        public static string Apply(string current, InputState input, int maxLength = int.MaxValue, Func<string, char, bool>? filter = null, bool allowPaste = true)
        {
            if (input.WasTyped(Key.Backspace) && current.Length > 0)
                current = current[..^1];

            // Ctrl/Super held = a shortcut chord (Ctrl+V / Cmd+V paste, etc.), not text entry. IsCommandDown is the
            // shared cross-platform gate (also used by MapEditorScene's shortcuts), so this and every other chord
            // agree on which keys count. Don't type the printable key (Backspace above still works). Shift is a
            // text modifier, not a chord. This gate runs before the printable loop, so it also blocks repeat ticks
            // (no machine-gunning a chord key).
            if (input.IsCommandDown)
            {
                // Ctrl/Cmd+V pastes the clipboard (filtered + length-capped, same path as typed chars). Fires on the
                // V press edge only, so holding the chord doesn't paste again on every OS auto-repeat tick.
                if (allowPaste && input.WasPressed(Key.V))
                    current = AppendClipboard(current, maxLength, filter);
                return current;
            }

            bool shift = input.IsDown(Key.LeftShift) || input.IsDown(Key.RightShift);

            // Iterate the printable range in enum order for deterministic multi-key frames. KeypadEqual is the
            // last printable member (the keypad block sits after Grave); unmapped members in between no-op.
            for (Key k = Key.A; k <= Key.KeypadEqual; k++)
            {
                if (!input.WasTyped(k)) continue;
                if (!TryMapChar(k, shift, out char c)) continue;
                if (current.Length >= maxLength) continue;
                // `current` is the buffer as accumulated by this same loop so far this call (not a pre-call
                // snapshot), so a filter sees every char this call already admitted ahead of this one.
                if (filter != null && !filter(current, c)) continue;
                current += c;
            }
            return current;
        }

        /// <summary>
        /// Convenience overload that reads the manager's current frame snapshot (<see cref="InputManager.State"/>).
        /// For a retained, custom-rendered widget that holds an <see cref="InputManager"/> rather than the immediate
        /// <c>GuiSurface</c>. Identical behaviour to the <see cref="InputState"/> overload.
        /// </summary>
        public static string Apply(string current, InputManager input, int maxLength = int.MaxValue, Func<string, char, bool>? filter = null, bool allowPaste = true)
            => Apply(current, input.State, maxLength, filter, allowPaste);

        // Appends the system clipboard text to the buffer, each char gated by the same filter + maxLength as typed
        // input, against the buffer as accumulated so far in this paste (not a pre-paste snapshot), so a second dot
        // later in the same clipboard text is rejected the same way a second dot in one typed frame is. StringBuilder
        // keeps the append itself from being O(n^2) on the string. maxLength bounds the buffer, so re-stringifying
        // it once per admitted char stays cheap in every real caller (single-line fields with a small maxLength).
        static string AppendClipboard(string current, int maxLength, Func<string, char, bool>? filter)
        {
            string clip = Clipboard.TryGetClipboardText();
            if (string.IsNullOrEmpty(clip)) return current;

            var sb = new StringBuilder(current);
            foreach (char c in clip)
            {
                if (sb.Length >= maxLength) break;
                if (filter != null && !filter(sb.ToString(), c)) continue;
                sb.Append(c);
            }
            return sb.ToString();
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
            if (k >= Key.Keypad0 && k <= Key.Keypad9)
            {
                // Keypad keys are shift-independent: a numpad has no symbol row.
                c = (char)('0' + (k - Key.Keypad0));
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
                // Keypad punctuation/operators, all shift-independent like the keypad digits above.
                case Key.KeypadDecimal: c = '.'; return true;
                case Key.KeypadAdd: c = '+'; return true;
                case Key.KeypadSubtract: c = '-'; return true;
                case Key.KeypadMultiply: c = '*'; return true;
                case Key.KeypadDivide: c = '/'; return true;
                case Key.KeypadEqual: c = '='; return true;
                default: c = '\0'; return false;
            }
        }
    }
}
