using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Render2D;
using KhaozEngine.Tests.App;
using KhaozEngine.Windowing;
using Xunit;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.Gui
{
    [Collection("AmbientLocalization")]
    public class TextInputTests
    {
        static readonly Rect Field = new(100, 100, 200, 30);
        static readonly Vector2 Inside = new(150, 115);
        static readonly Vector2 Outside = new(10, 10);

        // One per test-class instance (xUnit builds a fresh instance per fact), so the mouse press and
        // release edges derive from this test's own frame sequence and nothing crosses between tests.
        readonly MouseFrames _mouse = new();

        InputState Frame(Vector2 pos, bool leftDown, IEnumerable<Key>? pressed = null)
        {
            var down = new HashSet<MouseButton>();
            if (leftDown) down.Add(MouseButton.Left);
            var keys = new HashSet<Key>(pressed ?? System.Array.Empty<Key>());
            var (edgePressed, edgeReleased) = _mouse.Advance(down);
            return new InputState(
                keys, keys, new HashSet<Key>(),
                down, edgePressed, pos, Vector2.Zero, 0, 960, 540, mouseReleased: edgeReleased);
        }

        // Tap = press + release at the same point.
        void Tap(TextInput field, Pointer p, Vector2 at)
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
        public void Prefix_is_localized_lazily_and_does_not_enter_the_editable_buffer()
        {
            IStringCatalog? previous = LocalizationContext.Catalog;
            try
            {
                var field = new TextInput(Field)
                {
                    PrefixContent = new StringId("Chat.PlayerPrefix"),
                    MaxLength = 2,
                };
                LocalizationContext.Catalog = new DictionaryCatalog().Add("Chat.PlayerPrefix", "Alice: ");
                Assert.Equal("Alice: ", field.PrefixContent.Resolve());

                LocalizationContext.Catalog = new DictionaryCatalog().Add("Chat.PlayerPrefix", "Alicia: ");
                Assert.Equal("Alicia: ", field.PrefixContent.Resolve());

                var pointer = new Pointer();
                Tap(field, pointer, Inside);
                foreach (Key key in new[] { Key.A, Key.B, Key.C })
                    field.Update(pointer, Frame(Inside, false, pressed: new[] { key }), 0f);

                Assert.Equal("ab", field.Text);
            }
            finally
            {
                LocalizationContext.Catalog = previous;
            }
        }

        [Fact]
        public void Draw_layout_places_prefix_before_placeholder_or_typed_text()
        {
            var font = new FixedMeasurer();
            var style = new GuiStyle();

            TextInput.TextInputLayout empty = TextInput.DrawLayout(
                font, Field, style, "Me: ", "type here", "", 1f);
            TextInput.TextInputLayout typed = TextInput.DrawLayout(
                font, Field, style, "Me: ", "type here", "abc", 1f);

            Assert.Equal("type here", empty.VisibleContent);
            Assert.True(empty.ShowingPlaceholder);
            Assert.Equal(108f, empty.TextX, 3);
            Assert.Equal(148f, empty.ContentX, 3);
            Assert.Equal(149f, empty.CaretX, 3);

            Assert.Equal("abc", typed.VisibleContent);
            Assert.False(typed.ShowingPlaceholder);
            Assert.Equal(148f, typed.ContentX, 3);
            Assert.Equal(179f, typed.CaretX, 3);
        }

        [Fact]
        public void Draw_layout_applies_text_scale_and_skin_inset_to_prefix_content_and_caret()
        {
            var font = new FixedMeasurer();
            var style = new GuiStyle
            {
                Skin = new GuiSkin
                {
                    InsetLeft = 18f,
                    InsetTop = 4f,
                    InsetRight = 6f,
                    InsetBottom = 4f,
                },
            };

            TextInput.TextInputLayout layout = TextInput.DrawLayout(
                font, Field, style, "Me: ", "type here", "abc", 0.5f);

            Assert.Equal(118f, layout.TextX, 3);
            Assert.Equal(138f, layout.ContentX, 3);
            Assert.Equal(154f, layout.CaretX, 3);
        }

        [Fact]
        public void Draw_layout_clips_when_prefix_plus_visible_content_crosses_the_right_edge()
        {
            var font = new FixedMeasurer();

            TextInput.TextInputLayout layout = TextInput.DrawLayout(
                font, Field, new GuiStyle(), "0123456789", "", "abcdefghij", 1f);

            Assert.True(layout.Clip);
        }

        [Fact]
        public void Empty_prefix_preserves_the_existing_layout_exactly()
        {
            var font = new FixedMeasurer();
            TextInput.TextInputLayout before = TextInput.DrawLayout(
                font, Field, 108f, "type here", "", 0.5f);
            TextInput.TextInputLayout after = TextInput.DrawLayout(
                font, Field, new GuiStyle(), "", "type here", "", 0.5f);

            Assert.Equal(before, after);
            Assert.Equal("", new TextInput(Field).PrefixContent.Resolve());
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

        sealed class FixedMeasurer : ITextMeasurer
        {
            public float LineHeight => 20f;

            public Vector2 Measure(string text) => new(text.Length * 10f, LineHeight);
        }
    }
}
