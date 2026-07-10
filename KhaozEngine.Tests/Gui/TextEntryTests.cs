using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Platform;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    [Collection("ClipboardSerial")]   // paste tests mutate the static Clipboard provider; serialize with ClipboardTests
    public class TextEntryTests
    {
        // Build a frame where `pressed` keys went down this frame, `held` keys are held (for shift / chords),
        // and `repeated` keys fired an OS auto-repeat tick this frame (held past the repeat delay).
        static InputState Frame(IEnumerable<Key> pressed, IEnumerable<Key>? held = null, IEnumerable<Key>? repeated = null)
        {
            var down = new HashSet<Key>(held ?? System.Array.Empty<Key>());
            var press = new HashSet<Key>(pressed);
            foreach (var k in press) down.Add(k);
            var rep = new HashSet<Key>(repeated ?? System.Array.Empty<Key>());
            foreach (var k in rep) down.Add(k);   // a repeating key is, by definition, still held down
            return new InputState(
                down, press, new HashSet<Key>(),
                new HashSet<MouseButton>(), new HashSet<MouseButton>(),
                Vector2.Zero, Vector2.Zero, 0, 960, 540, repeated: rep);
        }

        // A frame where `repeated` keys fired an OS auto-repeat tick (held, past the delay) with NO fresh press edge.
        static InputState Repeat(IEnumerable<Key> repeated, IEnumerable<Key>? held = null)
            => Frame(System.Array.Empty<Key>(), held, repeated);

        // Pins the system clipboard to a known text for the scope of a test, so the paste path is deterministic on
        // every host (a produced provider value wins before the OS NSPasteboard/GDI backends). Disposes back to none.
        sealed class FakeClipboard : IDisposable
        {
            public FakeClipboard(string text) => Clipboard.RegisterTextProvider(() => text, _ => true);
            public void Dispose() => Clipboard.ClearTextProvider();
        }

        [Fact]
        public void Typing_a_letter_appends_it_lowercase()
        {
            Assert.Equal("hi", TextEntry.Apply("h", Frame(new[] { Key.I })));
        }

        [Fact]
        public void Shift_makes_the_letter_uppercase()
        {
            Assert.Equal("A", TextEntry.Apply("", Frame(new[] { Key.A }, held: new[] { Key.LeftShift })));
        }

        [Fact]
        public void Backspace_removes_the_last_character()
        {
            Assert.Equal("ab", TextEntry.Apply("abc", Frame(new[] { Key.Backspace })));
        }

        [Fact]
        public void Backspace_on_empty_is_a_noop()
        {
            Assert.Equal("", TextEntry.Apply("", Frame(new[] { Key.Backspace })));
        }

        [Fact]
        public void Digits_append_and_shift_gives_us_layout_symbol()
        {
            Assert.Equal("7", TextEntry.Apply("", Frame(new[] { Key.D7 })));
            Assert.Equal("&", TextEntry.Apply("", Frame(new[] { Key.D7 }, held: new[] { Key.LeftShift })));
        }

        [Fact]
        public void Space_appends_a_space()
        {
            string s = TextEntry.Apply("a", Frame(new[] { Key.Space }));   // "a "
            Assert.Equal("a ", s);
            Assert.Equal("a b", TextEntry.Apply(s, Frame(new[] { Key.B }))); // next frame
        }

        [Fact]
        public void MaxLength_blocks_further_characters()
        {
            Assert.Equal("ab", TextEntry.Apply("ab", Frame(new[] { Key.C }, held: null), maxLength: 2));
        }

        [Fact]
        public void Filter_rejects_disallowed_characters()
        {
            bool DigitsOnly(char c) => char.IsDigit(c);
            Assert.Equal("12", TextEntry.Apply("12", Frame(new[] { Key.A }), filter: DigitsOnly));
            Assert.Equal("123", TextEntry.Apply("12", Frame(new[] { Key.D3 }), filter: DigitsOnly));
        }

        [Fact]
        public void Ctrl_held_does_not_type_the_letter()
        {
            // Ctrl+V is a paste shortcut, not a 'v' keystroke. With an empty clipboard, paste appends nothing,
            // so the literal 'v' must never reach the field. (Paste-with-content is covered separately below.)
            using var _ = new FakeClipboard("");
            Assert.Equal("abc", TextEntry.Apply("abc", Frame(new[] { Key.V }, held: new[] { Key.LeftControl })));
            Assert.Equal("abc", TextEntry.Apply("abc", Frame(new[] { Key.V }, held: new[] { Key.RightControl })));
        }

        [Fact]
        public void Super_held_does_not_type_the_letter()
        {
            // Cmd+V (macOS paste) must not append 'v' either; empty clipboard -> no change.
            using var _ = new FakeClipboard("");
            Assert.Equal("abc", TextEntry.Apply("abc", Frame(new[] { Key.V }, held: new[] { Key.LeftSuper })));
            Assert.Equal("abc", TextEntry.Apply("abc", Frame(new[] { Key.V }, held: new[] { Key.RightSuper })));
        }

        [Fact]
        public void Bare_V_still_appends()
        {
            Assert.Equal("v", TextEntry.Apply("", Frame(new[] { Key.V })));
        }

        [Fact]
        public void Shift_still_types_with_no_modifier_block()
        {
            Assert.Equal("A", TextEntry.Apply("", Frame(new[] { Key.A }, held: new[] { Key.LeftShift })));
        }

        // ---- Numpad: keypad keys type digits, dot, minus (shift-independent; no symbol row on a keypad). ----

        [Fact]
        public void NumpadKeys_TypeDigitsIntoTextEntry()
        {
            // Each keypad digit types its digit, one per frame, in order.
            string s = "";
            for (Key k = Key.Keypad0; k <= Key.Keypad9; k++)
                s = TextEntry.Apply(s, Frame(new[] { k }));
            Assert.Equal("0123456789", s);

            // Shift does not change a keypad key: still the digit, never a US-layout symbol.
            Assert.Equal("7", TextEntry.Apply("", Frame(new[] { Key.Keypad7 }, held: new[] { Key.LeftShift })));

            // The keypad's dot and minus type through too (what a numeric field needs).
            Assert.Equal(".", TextEntry.Apply("", Frame(new[] { Key.KeypadDecimal })));
            Assert.Equal("-", TextEntry.Apply("", Frame(new[] { Key.KeypadSubtract })));

            // The keypad operator keys are printable as well.
            Assert.Equal("+", TextEntry.Apply("", Frame(new[] { Key.KeypadAdd })));
            Assert.Equal("*", TextEntry.Apply("", Frame(new[] { Key.KeypadMultiply })));
            Assert.Equal("/", TextEntry.Apply("", Frame(new[] { Key.KeypadDivide })));
            Assert.Equal("=", TextEntry.Apply("", Frame(new[] { Key.KeypadEqual })));
        }

        [Fact]
        public void Numpad_repeat_types_on_the_repeat_frame()
        {
            // Holding a keypad digit auto-repeats like any printable key.
            Assert.Equal("55", TextEntry.Apply("5", Repeat(new[] { Key.Keypad5 })));
        }

        // ---- Hold-to-repeat: a held key auto-repeats off the OS repeat signal (InputState.WasRepeated). ----

        [Fact]
        public void Backspace_repeat_deletes_on_the_repeat_frame()
        {
            // Holding Backspace: the OS fires a REPEAT tick with no fresh press edge; it should still delete.
            Assert.Equal("ab", TextEntry.Apply("abc", Repeat(new[] { Key.Backspace })));
        }

        [Fact]
        public void Held_key_with_neither_press_nor_repeat_does_nothing()
        {
            // Backspace held down but no press edge and no repeat tick this frame: must NOT delete (edge/tick only).
            Assert.Equal("abc", TextEntry.Apply("abc", Frame(System.Array.Empty<Key>(), held: new[] { Key.Backspace })));
        }

        [Fact]
        public void Printable_repeat_appends_on_the_repeat_frame()
        {
            // Hold-to-type: a repeat tick on 'a' appends another 'a'.
            Assert.Equal("aa", TextEntry.Apply("a", Repeat(new[] { Key.A })));
        }

        [Fact]
        public void Repeat_respects_maxLength()
        {
            Assert.Equal("abc", TextEntry.Apply("ab", Repeat(new[] { Key.C }), maxLength: 5)); // under limit -> appends
            Assert.Equal("ab", TextEntry.Apply("ab", Repeat(new[] { Key.C }), maxLength: 2));  // at limit -> blocked
        }

        [Fact]
        public void Repeat_respects_filter()
        {
            bool DigitsOnly(char c) => char.IsDigit(c);
            Assert.Equal("12", TextEntry.Apply("12", Repeat(new[] { Key.A }), filter: DigitsOnly));   // rejected
            Assert.Equal("123", TextEntry.Apply("12", Repeat(new[] { Key.D3 }), filter: DigitsOnly)); // allowed
        }

        [Fact]
        public void Shift_held_repeat_types_uppercase()
        {
            Assert.Equal("AA", TextEntry.Apply("A", Repeat(new[] { Key.A }, held: new[] { Key.LeftShift })));
        }

        [Fact]
        public void Ctrl_chord_blocks_repeated_character_entry()
        {
            // Holding Ctrl+V must not machine-gun 'v' into the field on repeat ticks.
            Assert.Equal("abc", TextEntry.Apply("abc", Repeat(new[] { Key.V }, held: new[] { Key.LeftControl })));
        }

        [Fact]
        public void Super_chord_blocks_repeated_character_entry()
        {
            // Holding Cmd+V (macOS) must not machine-gun 'v' either.
            Assert.Equal("abc", TextEntry.Apply("abc", Repeat(new[] { Key.V }, held: new[] { Key.LeftSuper })));
        }

        [Fact]
        public void Backspace_repeat_still_works_under_a_held_modifier()
        {
            // Backspace is not a chord-typed character, so its repeat must survive even while Ctrl/Cmd is held.
            Assert.Equal("ab", TextEntry.Apply("abc", Repeat(new[] { Key.Backspace }, held: new[] { Key.LeftControl })));
            Assert.Equal("ab", TextEntry.Apply("abc", Repeat(new[] { Key.Backspace }, held: new[] { Key.LeftSuper })));
        }

        // ---- Clipboard paste: Ctrl/Cmd+V appends clipboard text, filtered + length-capped like typed chars. ----

        [Fact]
        public void Ctrl_V_pastes_clipboard_text()
        {
            using var _ = new FakeClipboard("XY");
            Assert.Equal("abXY", TextEntry.Apply("ab", Frame(new[] { Key.V }, held: new[] { Key.LeftControl })));
        }

        [Fact]
        public void Cmd_V_pastes_clipboard_text()
        {
            using var _ = new FakeClipboard("XY");
            Assert.Equal("abXY", TextEntry.Apply("ab", Frame(new[] { Key.V }, held: new[] { Key.LeftSuper })));
        }

        [Fact]
        public void Paste_does_not_also_type_a_v()
        {
            // The paste chord consumes the V press: only the clipboard text lands, never a literal 'v'.
            using var _ = new FakeClipboard("X");
            Assert.Equal("X", TextEntry.Apply("", Frame(new[] { Key.V }, held: new[] { Key.LeftControl })));
        }

        [Fact]
        public void Paste_runs_clipboard_text_through_the_filter()
        {
            bool DigitsOnly(char c) => char.IsDigit(c);
            using var _ = new FakeClipboard("a1b2c3");
            Assert.Equal("123", TextEntry.Apply("", Frame(new[] { Key.V }, held: new[] { Key.LeftControl }), filter: DigitsOnly));
        }

        [Fact]
        public void Paste_honors_maxLength()
        {
            using var _ = new FakeClipboard("XYZW");
            Assert.Equal("abXY", TextEntry.Apply("ab", Frame(new[] { Key.V }, held: new[] { Key.LeftControl }), maxLength: 4));
        }

        [Fact]
        public void Paste_into_a_full_field_appends_nothing()
        {
            using var _ = new FakeClipboard("XYZ");
            Assert.Equal("ab", TextEntry.Apply("ab", Frame(new[] { Key.V }, held: new[] { Key.LeftControl }), maxLength: 2));
        }

        [Fact]
        public void Empty_clipboard_paste_is_a_noop()
        {
            using var _ = new FakeClipboard("");
            Assert.Equal("ab", TextEntry.Apply("ab", Frame(new[] { Key.V }, held: new[] { Key.LeftControl })));
        }

        [Fact]
        public void Paste_is_suppressed_when_allowPaste_is_false()
        {
            using var _ = new FakeClipboard("XY");
            Assert.Equal("ab", TextEntry.Apply("ab", Frame(new[] { Key.V }, held: new[] { Key.LeftControl }), allowPaste: false));
        }

        [Fact]
        public void Other_modifier_chords_do_not_paste()
        {
            // Ctrl+C (or any non-V chord) is not a paste; the clipboard is left untouched.
            using var _ = new FakeClipboard("XY");
            Assert.Equal("ab", TextEntry.Apply("ab", Frame(new[] { Key.C }, held: new[] { Key.LeftControl })));
        }

        [Fact]
        public void Paste_fires_on_the_press_edge_not_on_auto_repeat()
        {
            // Holding Ctrl+V pastes once (on the press edge); the OS repeat ticks for V must not re-paste.
            using var _ = new FakeClipboard("XY");
            Assert.Equal("ab", TextEntry.Apply("ab", Repeat(new[] { Key.V }, held: new[] { Key.LeftControl })));
        }

        // ---- Feeding TextEntry from an InputManager (the retained-widget path). ----

        [Fact]
        public void InputManager_State_exposes_the_polled_snapshot()
        {
            var mgr = new InputManager();
            var frame = Frame(new[] { Key.A });
            mgr.Update(frame);
            Assert.Same(frame, mgr.State);
        }

        [Fact]
        public void Apply_via_InputManager_round_trips_a_typed_character()
        {
            var mgr = new InputManager();
            mgr.Update(Frame(new[] { Key.I }));
            Assert.Equal("hi", TextEntry.Apply("h", mgr));
        }

        [Fact]
        public void Apply_via_InputManager_pastes_when_allowed()
        {
            using var _ = new FakeClipboard("XY");
            var mgr = new InputManager();
            mgr.Update(Frame(new[] { Key.V }, held: new[] { Key.LeftControl }));
            Assert.Equal("abXY", TextEntry.Apply("ab", mgr, maxLength: 32, filter: null, allowPaste: true));
        }
    }
}
