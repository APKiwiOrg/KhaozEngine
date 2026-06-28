using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    public class TextEntryTests
    {
        // Build a frame where `pressed` keys went down this frame and `held` keys are held (for shift).
        static InputState Frame(IEnumerable<Key> pressed, IEnumerable<Key>? held = null)
        {
            var down = new HashSet<Key>(held ?? System.Array.Empty<Key>());
            var press = new HashSet<Key>(pressed);
            foreach (var k in press) down.Add(k);
            return new InputState(
                down, press, new HashSet<Key>(),
                new HashSet<MouseButton>(), new HashSet<MouseButton>(),
                Vector2.Zero, Vector2.Zero, 0, 960, 540);
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
    }
}
