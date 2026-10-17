using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    [Collection("AmbientLocalization")]
    public class TextAreaTests
    {
        // 8 px horizontal and 6 px vertical padding leave a 200 x 100 inner area: 20 characters by 5 lines.
        static readonly Rect Area = new(100, 100, 216, 112);
        const float InnerX = 108f;
        const float InnerY = 106f;
        const float InnerRight = 308f;
        static readonly Vector2 Inside = new(150, 150);
        static readonly Vector2 Outside = new(10, 10);
        const string Emoji = "\U0001F600";

        // One per test-class instance (xUnit builds a fresh instance per fact), so the mouse press and
        // release edges derive from this test's own frame sequence and nothing crosses between tests.
        readonly MouseFrames _mouse = new();
        readonly Pointer _pointer = new();

        InputState Frame(Vector2 pos, bool leftDown = false, IEnumerable<Key>? pressed = null,
            string? committedText = null, float scroll = 0f)
        {
            var down = new HashSet<MouseButton>();
            if (leftDown) down.Add(MouseButton.Left);
            var keys = new HashSet<Key>(pressed ?? Array.Empty<Key>());
            var (edgePressed, edgeReleased) = _mouse.Advance(down);
            return new InputState(
                keys, keys, new HashSet<Key>(),
                down, edgePressed, pos, Vector2.Zero, scroll, 960, 540, mouseReleased: edgeReleased,
                textInput: committedText ?? "", textInputAvailable: committedText != null);
        }

        void Step(TextArea area, InputState frame)
        {
            _pointer.Update(frame);
            area.Update(_pointer, frame, 0f);
        }

        void Press(TextArea area, params Key[] keys) => Step(area, Frame(Inside, pressed: keys));

        void Type(TextArea area, string text) => Step(area, Frame(Inside, committedText: text));

        // Tap = press + release at the same point.
        void Tap(TextArea area, Vector2 at)
        {
            Step(area, Frame(at));
            Step(area, Frame(at, leftDown: true));
            Step(area, Frame(at));
        }

        static TextArea Focused(string text, ITextMeasurer? measurer = null)
        {
            var area = new TextArea(Area, measurer ?? new FixedMeasurer());
            area.SetText(text);
            area.Focus();
            return area;
        }

        static string Lines(int count)
        {
            var lines = new string[count];
            for (int i = 0; i < count; i++) lines[i] = $"line {i}";
            return string.Join("\n", lines);
        }

        [Fact]
        public void Tap_focuses_and_release_outside_unfocuses()
        {
            var area = new TextArea(Area, new FixedMeasurer());
            Tap(area, Inside);
            Assert.True(area.IsFocused);
            Tap(area, Outside);
            Assert.False(area.IsFocused);
        }

        [Fact]
        public void Committed_text_inserts_at_the_caret()
        {
            TextArea area = Focused("ac");
            Press(area, Key.Left);
            Type(area, "b");
            Assert.Equal("abc", area.Text);
            Assert.Equal(2, area.Caret);
        }

        [Fact]
        public void Enter_inserts_a_newline_but_ctrl_enter_does_not()
        {
            TextArea area = Focused("ab");
            Press(area, Key.Enter);
            Assert.Equal("ab\n", area.Text);
            Assert.Equal(3, area.Caret);

            Press(area, Key.LeftControl, Key.Enter);
            Press(area, Key.LeftSuper, Key.Enter);
            Assert.Equal("ab\n", area.Text);
        }

        [Fact]
        public void Backspace_and_delete_remove_one_scalar_each_side()
        {
            TextArea area = Focused("a" + Emoji + Emoji + "b");
            Press(area, Key.Left);
            Press(area, Key.Left);
            Assert.Equal(3, area.Caret);

            Press(area, Key.Backspace);
            Assert.Equal("a" + Emoji + "b", area.Text);
            Assert.Equal(1, area.Caret);

            Press(area, Key.Delete);
            Assert.Equal("ab", area.Text);
            Assert.Equal(1, area.Caret);
        }

        [Fact]
        public void SetText_normalises_line_endings()
        {
            var area = new TextArea(Area, new FixedMeasurer());
            area.SetText("a\r\nb\rc\td");
            Assert.Equal("a\nb\ncd", area.Text);
            Assert.Equal(6, area.Caret);
        }

        [Fact]
        public void Long_unbroken_paste_wraps_and_caps()
        {
            TextArea area = Focused(new string('x', 5000));
            Assert.Equal(TextArea.DefaultMaxLength, area.Text.Length);
            Assert.Equal(TextArea.DefaultMaxLength, area.Caret);

            Type(area, "y");
            Press(area, Key.Enter);
            Assert.Equal(new string('x', TextArea.DefaultMaxLength), area.Text);
            Assert.Equal(200, area.Lines.Count);

            area.SetText(new string('x', TextArea.DefaultMaxLength - 1) + Emoji);
            Assert.Equal(TextArea.DefaultMaxLength - 1, area.Text.Length);
        }

        [Fact]
        public void Up_and_down_keep_the_column()
        {
            TextArea area = Focused("abcdef\nab\nabcdef");
            var caret = new List<int>();
            foreach (Key key in new[] { Key.Up, Key.Up, Key.Down, Key.Down })
            {
                Press(area, key);
                caret.Add(area.Caret);
            }
            Assert.Equal(new[] { 9, 6, 9, 16 }, caret);

            // A horizontal move forgets the remembered column, so the next climb starts from X 50, not 60.
            Press(area, Key.Left);
            Press(area, Key.Up);
            Assert.Equal(9, area.Caret);
            Press(area, Key.Up);
            Assert.Equal(5, area.Caret);
        }

        [Fact]
        public void Home_and_end_use_the_visual_line()
        {
            // Twenty characters fill the first visual line, so "eeee ffff" soft wraps onto the second.
            TextArea area = Focused("aaaa bbbb cccc dddd eeee ffff");
            Press(area, Key.Home);
            Assert.Equal(20, area.Caret);
            Press(area, Key.End);
            Assert.Equal(29, area.Caret);

            Press(area, Key.Up);
            Assert.Equal(9, area.Caret);
            Press(area, Key.End);
            Assert.Equal(19, area.Caret);
            Press(area, Key.Home);
            Assert.Equal(0, area.Caret);
        }

        [Fact]
        public void Tap_places_the_caret()
        {
            TextArea area = Focused("hello\nworld");
            Tap(area, new Vector2(InnerX + 32f, InnerY + 25f));
            Assert.Equal(9, area.Caret);
        }

        [Fact]
        public void Caret_line_scrolls_into_view_and_wheel_scrolls_three_lines()
        {
            var area = new TextArea(Area, new FixedMeasurer());
            area.SetText(Lines(30));
            Step(area, Frame(Inside));
            Assert.Equal(30 * 20f - 100f, area.ScrollY);

            Step(area, Frame(Inside, scroll: 1f));
            Assert.Equal(440f, area.ScrollY);

            Step(area, Frame(Inside, scroll: 100f));
            Assert.Equal(0f, area.ScrollY);
            Step(area, Frame(Inside, scroll: -100f));
            Assert.Equal(500f, area.ScrollY);
        }

        [Fact]
        public void Unfocused_area_ignores_typing()
        {
            var area = new TextArea(Area, new FixedMeasurer());
            Step(area, Frame(Inside, pressed: new[] { Key.H }, committedText: "x"));
            Press(area, Key.Enter);
            Assert.Equal("", area.Text);
            Assert.False(area.TextChanged);
        }

        [Fact]
        public void TextChanged_is_true_only_on_the_changing_frame()
        {
            TextArea area = Focused("");
            Step(area, Frame(Inside));
            Assert.False(area.TextChanged);

            Type(area, "a");
            Assert.True(area.TextChanged);
            Step(area, Frame(Inside));
            Assert.False(area.TextChanged);

            Press(area, Key.Left);
            Assert.False(area.TextChanged);

            area.SetText("b");
            Step(area, Frame(Inside));
            Assert.True(area.TextChanged);
            Step(area, Frame(Inside));
            Assert.False(area.TextChanged);
        }

        [Fact]
        public void Layout_is_rebuilt_only_when_its_inputs_change()
        {
            var measurer = new CountingMeasurer();
            var words = new System.Text.StringBuilder();
            while (words.Length < TextArea.DefaultMaxLength) words.Append("word ");
            TextArea area = Focused(words.ToString(), measurer);
            Assert.Equal(TextArea.DefaultMaxLength, area.Text.Length);

            Step(area, Frame(Inside));
            Assert.True(measurer.Calls > 0);
            int afterFirst = measurer.Calls;
            for (int i = 0; i < 9; i++) Step(area, Frame(Inside));
            Assert.Equal(afterFirst, measurer.Calls);

            area.Bounds = Area with { Y = 50f, Height = 300f };
            Step(area, Frame(Inside));
            Assert.Equal(afterFirst, measurer.Calls);

            area.Bounds = Area with { Width = 300f };
            Step(area, Frame(Inside));
            int afterWidth = measurer.Calls;
            Assert.True(afterWidth > afterFirst);

            area.TextScale = 0.5f;
            Step(area, Frame(Inside));
            int afterScale = measurer.Calls;
            Assert.True(afterScale > afterWidth);
            Step(area, Frame(Inside));
            Assert.Equal(afterScale, measurer.Calls);
        }

        [Fact]
        public void Draw_sends_only_the_visible_lines()
        {
            var area = new TextArea(Area, new FixedMeasurer());
            area.SetText(Lines(30));
            Step(area, Frame(Inside));

            var sink = new RecordingSink();
            area.DrawContent(ref sink);
            Assert.InRange(sink.Texts.Count, 1, 6);
            Assert.Equal("line 25", sink.Texts[0]);
            Assert.Equal("line 29", sink.Texts[^1]);

            Step(area, Frame(Inside, scroll: 1f));
            sink = new RecordingSink();
            area.DrawContent(ref sink);
            Assert.InRange(sink.Texts.Count, 1, 6);
            Assert.Equal("line 22", sink.Texts[0]);
        }

        [Fact]
        public void Caret_after_many_spaces_is_drawn_inside_the_area()
        {
            TextArea area = Focused(new string(' ', 60));
            Step(area, Frame(Inside));
            Assert.Single(area.Lines);

            var sink = new RecordingSink();
            area.DrawContent(ref sink);
            Rect caret = Assert.Single(sink.Carets);
            Assert.True(caret.X >= InnerX, $"caret X {caret.X}");
            Assert.True(caret.Right <= InnerRight, $"caret right {caret.Right}");
        }

        sealed class FixedMeasurer : ITextMeasurer
        {
            public float LineHeight => 20f;

            public Vector2 Measure(string text) => new(text.Length * 10f, LineHeight);

            public Vector2 Measure(ReadOnlySpan<char> text) => new(text.Length * 10f, LineHeight);
        }

        sealed class CountingMeasurer : ITextMeasurer
        {
            public int Calls;

            public float LineHeight => 20f;

            public Vector2 Measure(string text) => Measure(text.AsSpan());

            public Vector2 Measure(ReadOnlySpan<char> text)
            {
                Calls++;
                return new Vector2(text.Length * 10f, LineHeight);
            }
        }

        struct RecordingSink : ITextAreaSink
        {
            public List<string> Texts;
            public List<Rect> Carets;

            public RecordingSink()
            {
                Texts = new List<string>();
                Carets = new List<Rect>();
            }

            public void DrawText(string text, Vector2 position, Vector4 color) => Texts.Add(text);

            public void DrawCaret(Rect caret, Vector4 color) => Carets.Add(caret);
        }
    }
}
