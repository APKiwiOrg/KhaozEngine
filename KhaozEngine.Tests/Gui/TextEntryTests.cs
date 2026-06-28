using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
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
            // Ctrl+V is a paste shortcut, not a 'v' keystroke.
            Assert.Equal("abc", TextEntry.Apply("abc", Frame(new[] { Key.V }, held: new[] { Key.LeftControl })));
            Assert.Equal("abc", TextEntry.Apply("abc", Frame(new[] { Key.V }, held: new[] { Key.RightControl })));
        }

        [Fact]
        public void Super_held_does_not_type_the_letter()
        {
            // Cmd+V (macOS paste) must not append 'v' either.
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
    }
}
