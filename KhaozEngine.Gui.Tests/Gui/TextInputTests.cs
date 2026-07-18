using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Windowing;
using Xunit;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.Gui
{
    public class TextInputTests
    {
        static readonly Rect Field = new(100, 100, 200, 30);
        static readonly Vector2 Inside = new(150, 115);
        static readonly Vector2 Outside = new(10, 10);

        static InputState Frame(Vector2 pos, bool leftDown, IEnumerable<Key>? pressed = null)
        {
            var down = new HashSet<MouseButton>();
            if (leftDown) down.Add(MouseButton.Left);
            var keys = new HashSet<Key>(pressed ?? System.Array.Empty<Key>());
            return new InputState(
                keys, keys, new HashSet<Key>(),
                down, new HashSet<MouseButton>(), pos, Vector2.Zero, 0, 960, 540);
        }

        // Tap = press + release at the same point.
        static void Tap(TextInput field, Pointer p, Vector2 at)
        {
            p.Update(Frame(at, false)); field.Update(p, InputState.Empty, 0f);
            p.Update(Frame(at, true)); field.Update(p, InputState.Empty, 0f);
            p.Update(Frame(at, false)); field.Update(p, InputState.Empty, 0f);
        }

        [Fact]
        public void Tap_inside_focuses_the_field()
        {
            var field = new TextInput(Field);
            var p = new Pointer();
            Tap(field, p, Inside);
            Assert.True(field.IsFocused);
        }

        [Fact]
        public void Tap_outside_unfocuses()
        {
            var field = new TextInput(Field);
            var p = new Pointer();
            Tap(field, p, Inside);
            Assert.True(field.IsFocused);
            Tap(field, p, Outside);
            Assert.False(field.IsFocused);
        }

        [Fact]
        public void Typing_while_focused_appends_to_text()
        {
            var field = new TextInput(Field);
            var p = new Pointer();
            Tap(field, p, Inside);
            p.Update(Frame(Inside, false, pressed: new[] { Key.H }));
            field.Update(p, Frame(Inside, false, pressed: new[] { Key.H }), 0f);
            Assert.Equal("h", field.Text);
            Assert.True(field.TextChanged);
        }

        [Fact]
        public void Typing_while_unfocused_is_ignored()
        {
            var field = new TextInput(Field);
            var p = new Pointer();
            p.Update(Frame(Inside, false));
            field.Update(p, Frame(Inside, false, pressed: new[] { Key.H }), 0f);
            Assert.Equal("", field.Text);
        }

        [Fact]
        public void MaxLength_is_enforced()
        {
            var field = new TextInput(Field) { MaxLength = 2 };
            var p = new Pointer();
            Tap(field, p, Inside);
            foreach (var k in new[] { Key.A, Key.B, Key.C })
                field.Update(p, Frame(Inside, false, pressed: new[] { k }), 0f);
            Assert.Equal("ab", field.Text);
        }

        [Fact]
        public void Cursor_blinks_off_after_the_blink_interval()
        {
            var field = new TextInput(Field);
            var p = new Pointer();
            Tap(field, p, Inside);
            Assert.True(field.CursorVisible);                       // visible right after focus
            field.Update(p, InputState.Empty, 0.6f);               // past the 0.5s blink interval
            Assert.False(field.CursorVisible);
        }
    }
}
