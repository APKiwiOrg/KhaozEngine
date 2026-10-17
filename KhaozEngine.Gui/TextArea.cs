using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using KhaozEngine.App;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// A multi-line text field over <see cref="Pointer"/> and <see cref="TextEntry"/>. A tap inside focuses it and
    /// places the caret, a release outside unfocuses it. While focused, committed text, Backspace and Ctrl+V or
    /// Cmd+V paste edit the text before the caret through <see cref="TextEntry.Apply(string, InputState, int, Func{string, char, bool}?, bool)"/>.
    /// Enter inserts a newline unless Ctrl or Cmd is held, so a host can read that chord as Send. Delete removes
    /// the next scalar. Left and Right step one scalar, Up and Down keep a remembered column across visual lines,
    /// and Home and End go to the ends of the visual line. Text greedy word wraps inside the padded area and
    /// scrolls vertically to keep the caret line visible. The wheel scrolls three lines. There is no selection.
    /// Line endings normalise to <c>'\n'</c> and other control characters are dropped. The caret is a UTF-16
    /// index that always sits on a scalar boundary. Layout measures with <see cref="Font"/> when set, otherwise
    /// with the measurer passed to the constructor, and is cached until the text, inner width, scale or font
    /// changes.
    /// </summary>
    public sealed class TextArea
    {
        public const int DefaultMaxLength = 4000;

        public Rect Bounds;
        /// <summary>Maximum length in UTF-16 units.</summary>
        public int MaxLength = DefaultMaxLength;
        public bool IsFocused;
        public SpriteFont? Font;

        /// <summary>The lazily resolved placeholder drawn when the text is empty. Defaults to empty.</summary>
        public LocalizedText PlaceholderContent;

        /// <summary>Uniform scale for the text and line height. Bounds, padding and the caret width are unscaled.</summary>
        public float TextScale = 1f;

        public Vector4 Background = new(0.12f, 0.12f, 0.16f, 1f);
        public Vector4 Border = new(0.31f, 0.31f, 0.39f, 1f);
        public Vector4 BorderFocused = new(0.47f, 0.55f, 0.78f, 1f);
        public Vector4 TextColor = new(0.90f, 0.90f, 0.94f, 1f);
        public Vector4 PlaceholderColor = new(0.39f, 0.39f, 0.47f, 1f);
        public Vector4 CursorColor = new(0.70f, 0.78f, 1f, 1f);

        const float BlinkRate = 0.5f;
        const float PadX = 8f;
        const float PadY = 6f;
        const float CaretWidth = 2f;
        const int WheelLines = 3;

        readonly ITextMeasurer _measurer;
        readonly GuiStyle _style = GuiStyle.Default with { BorderThickness = 1f };
        string _previousText = "";
        float _blink;
        float _targetX;
        bool _hasTargetX;
        bool _revealCaret;

        IReadOnlyList<TextAreaLine> _lines = Array.Empty<TextAreaLine>();
        string?[] _lineText = Array.Empty<string?>();
        string? _layoutText;
        float _layoutWidth = float.NaN;
        float _layoutScale = float.NaN;
        ITextMeasurer? _layoutMeasurer;
        int _layoutVersion;

        int _caretVersion = -1;
        int _caretIndex = -1;
        int _caretLine;
        float _caretX;

        /// <summary>The text, with <c>'\n'</c> line endings.</summary>
        public string Text { get; private set; } = "";

        /// <summary>The caret as a UTF-16 index into <see cref="Text"/>, always on a scalar boundary.</summary>
        public int Caret { get; private set; }

        /// <summary>The vertical scroll in pixels, from 0 to the content height minus the inner height.</summary>
        public float ScrollY { get; private set; }

        /// <summary>True on the frame <see cref="Text"/> changed, by typing or a <see cref="SetText"/> call.</summary>
        public bool TextChanged { get; private set; }

        /// <summary>Whether the caret is currently in its visible blink phase.</summary>
        public bool CursorVisible { get; private set; } = true;

        public TextArea(Rect bounds, ITextMeasurer measurer, SpriteFont? font = null)
        {
            ArgumentNullException.ThrowIfNull(measurer);
            Bounds = bounds;
            _measurer = measurer;
            Font = font;
        }

        /// <summary>The wrapped visual lines for the current text, inner width and scale.</summary>
        internal IReadOnlyList<TextAreaLine> Lines
        {
            get
            {
                EnsureLayout();
                return _lines;
            }
        }

        ITextMeasurer Measurer => Font ?? _measurer;

        float LineHeight => Measurer.LineHeight * TextScale;

        Rect Inner => new(Bounds.X + PadX, Bounds.Y + PadY,
            MathF.Max(0f, Bounds.Width - 2f * PadX), MathF.Max(0f, Bounds.Height - 2f * PadY));

        float MaxScroll => MathF.Max(0f, _lines.Count * LineHeight - Inner.Height);

        /// <summary>
        /// Replace the text programmatically. Line endings normalise to <c>'\n'</c>, other control characters are
        /// dropped, the result is clamped to <see cref="MaxLength"/> without splitting a surrogate pair, and the
        /// caret moves to the end. The next <see cref="Update"/> raises <see cref="TextChanged"/> and scrolls the
        /// caret into view.
        /// </summary>
        public void SetText(string value)
        {
            string next = Normalize(value ?? "");
            int max = Math.Max(0, MaxLength);
            if (next.Length > max) next = next[..ScalarFloor(next, max)];
            if (!string.Equals(next, Text, StringComparison.Ordinal)) Text = next;
            Caret = Text.Length;
            _hasTargetX = false;
            _revealCaret = true;
        }

        /// <summary>Update focus, editing, navigation, scroll and caret blink. Returns whether the area is focused,
        /// so the host knows it consumes the keyboard.</summary>
        public bool Update(Pointer pointer, InputState input, float dt)
        {
            pointer.BlockRegion(Bounds);
            if (pointer.IsTapIn(Bounds))
            {
                Focus();
                PlaceCaret(pointer.Position);
            }
            else if (pointer.IsReleasedOutside(Bounds))
            {
                Unfocus();
            }

            if (IsFocused)
            {
                if (HasKeyInput(input)) ApplyKeys(input);
                _blink += dt;
                while (_blink >= BlinkRate) { _blink -= BlinkRate; CursorVisible = !CursorVisible; }
            }

            TextChanged = !string.Equals(Text, _previousText, StringComparison.Ordinal);
            if (TextChanged) ResetBlink();
            _previousText = Text;

            EnsureLayout();
            if (_revealCaret)
            {
                RevealCaret();
                _revealCaret = false;
            }
            if (input.ScrollDelta != 0f && pointer.IsPointerIn(Bounds))
                ScrollY -= input.ScrollDelta * WheelLines * LineHeight;
            ScrollY = Math.Clamp(ScrollY, 0f, MaxScroll);
            return IsFocused;
        }

        /// <summary>Give the area keyboard focus (resets the caret blink). No-op if already focused.</summary>
        public void Focus() { if (!IsFocused) { IsFocused = true; ResetBlink(); } }

        /// <summary>Remove keyboard focus. No-op if not focused.</summary>
        public void Unfocus() { IsFocused = false; }

        /// <summary>Draw the box, the visible lines or the placeholder, and the caret, clipped to the padded inner
        /// area. <paramref name="white"/> is a 1x1 white texture. Does nothing without a <see cref="Font"/>.</summary>
        public void Draw(SpriteBatch batch, Texture2D white)
        {
            if (Font == null) return;
            GuiDraw.FillStyled(batch, white, Bounds, _style, Background, IsFocused ? BorderFocused : Border);
            batch.SetScissor(Inner);
            var sink = new SpriteBatchTextAreaSink(batch, Font, white, TextScale);
            DrawContent(ref sink);
            batch.ClearScissor();
        }

        /// <summary>Sends the lines that intersect the inner area, or the placeholder, then the caret. The caret's
        /// X is clamped inside the inner area, because trailing spaces can make a line wider than it.</summary>
        internal void DrawContent<TSink>(ref TSink sink) where TSink : ITextAreaSink
        {
            EnsureLayout();
            Rect inner = Inner;
            float lineHeight = LineHeight;
            float scroll = Math.Clamp(ScrollY, 0f, MaxScroll);
            if (Text.Length == 0)
            {
                string placeholder = PlaceholderContent.Resolve();
                if (placeholder.Length > 0)
                    sink.DrawText(placeholder, new Vector2(MathF.Floor(inner.X), MathF.Floor(inner.Y)), PlaceholderColor);
            }
            else if (lineHeight > 0f)
            {
                int first = Math.Max(0, (int)MathF.Floor(scroll / lineHeight));
                int last = Math.Min(_lines.Count - 1, (int)MathF.Ceiling((scroll + inner.Height) / lineHeight) - 1);
                for (int i = first; i <= last; i++)
                {
                    TextAreaLine line = _lines[i];
                    if (line.Length == 0) continue;
                    string text = _lineText[i] ??= Text.Substring(line.Start, line.Length);
                    var position = new Vector2(MathF.Floor(inner.X), MathF.Floor(inner.Y + i * lineHeight - scroll));
                    sink.DrawText(text, position, TextColor);
                }
            }

            if (IsFocused && CursorVisible)
            {
                (int caretLine, float caretX) = CaretPosition();
                float x = inner.X + MathF.Min(caretX + 1f, MathF.Max(0f, inner.Width - CaretWidth));
                sink.DrawCaret(new Rect(x, inner.Y + caretLine * lineHeight - scroll, CaretWidth, lineHeight), CursorColor);
            }
        }

        static bool HasKeyInput(InputState input) =>
            input.KeysPressed.Count > 0 || input.KeysRepeated.Count > 0
            || (input.TextInputAvailable && input.TextInput.Length > 0);

        void ApplyKeys(InputState input)
        {
            string textBefore = Text;
            int caretBefore = Caret;

            string head = Text[..Caret];
            string tail = Text[Caret..];
            string edited = TextEntry.Apply(head, input, MaxLength - tail.Length);
            if (!string.Equals(edited, head, StringComparison.Ordinal))
            {
                edited = Normalize(edited);
                Text = edited + tail;
                Caret = edited.Length;
            }
            if (input.WasTyped(Key.Enter) && !input.IsCommandDown && Text.Length < MaxLength)
            {
                Text = Text.Insert(Caret, "\n");
                Caret++;
            }
            if (input.WasTyped(Key.Delete) && Caret < Text.Length)
                Text = Text.Remove(Caret, NextBoundary(Text, Caret) - Caret);

            bool horizontal = input.WasTyped(Key.Left) || input.WasTyped(Key.Right)
                || input.WasTyped(Key.Home) || input.WasTyped(Key.End);
            if (horizontal || !ReferenceEquals(Text, textBefore)) _hasTargetX = false;

            if (input.WasTyped(Key.Left)) Caret = PreviousBoundary(Text, Caret);
            if (input.WasTyped(Key.Right)) Caret = NextBoundary(Text, Caret);
            if (input.WasTyped(Key.Home)) Caret = Lines[CaretPosition().Line].Start;
            if (input.WasTyped(Key.End))
                Caret = TextAreaLayout.IndexAt(Measurer, Text, Lines, CaretPosition().Line, float.MaxValue, TextScale);
            if (input.WasTyped(Key.Up)) MoveVertical(-1);
            if (input.WasTyped(Key.Down)) MoveVertical(1);

            if (Caret != caretBefore || !ReferenceEquals(Text, textBefore))
            {
                _revealCaret = true;
                ResetBlink();
            }
        }

        void MoveVertical(int direction)
        {
            (int line, float x) = CaretPosition();
            if (!_hasTargetX)
            {
                _targetX = x;
                _hasTargetX = true;
            }
            int target = line + direction;
            if (target < 0) Caret = 0;
            else if (target >= _lines.Count) Caret = Text.Length;
            else Caret = TextAreaLayout.IndexAt(Measurer, Text, _lines, target, _targetX, TextScale);
        }

        void PlaceCaret(Vector2 position)
        {
            EnsureLayout();
            Rect inner = Inner;
            float lineHeight = LineHeight;
            int line = lineHeight > 0f ? (int)MathF.Floor((position.Y - inner.Y + ScrollY) / lineHeight) : 0;
            line = Math.Clamp(line, 0, Math.Max(0, _lines.Count - 1));
            Caret = TextAreaLayout.IndexAt(Measurer, Text, _lines, line, position.X - inner.X, TextScale);
            _hasTargetX = false;
            _revealCaret = true;
            ResetBlink();
        }

        void RevealCaret()
        {
            float lineHeight = LineHeight;
            float top = CaretPosition().Line * lineHeight;
            float height = Inner.Height;
            if (top < ScrollY) ScrollY = top;
            else if (top + lineHeight > ScrollY + height) ScrollY = top + lineHeight - height;
        }

        /// <summary>The caret's visual line and X, cached until the caret or the layout changes.</summary>
        (int Line, float X) CaretPosition()
        {
            EnsureLayout();
            if (_caretVersion != _layoutVersion || _caretIndex != Caret)
            {
                (_caretLine, _caretX) = TextAreaLayout.CaretPosition(Measurer, Text, _lines, Caret, TextScale);
                _caretVersion = _layoutVersion;
                _caretIndex = Caret;
            }
            return (_caretLine, _caretX);
        }

        void EnsureLayout()
        {
            ITextMeasurer measurer = Measurer;
            float width = Inner.Width;
            if (ReferenceEquals(_layoutText, Text) && width == _layoutWidth && TextScale == _layoutScale
                && ReferenceEquals(measurer, _layoutMeasurer))
                return;
            _lines = TextAreaLayout.Wrap(measurer, Text, width, TextScale);
            _lineText = new string?[_lines.Count];
            _layoutText = Text;
            _layoutWidth = width;
            _layoutScale = TextScale;
            _layoutMeasurer = measurer;
            _layoutVersion++;
        }

        void ResetBlink() { CursorVisible = true; _blink = 0f; }

        /// <summary>Converts <c>"\r\n"</c> and <c>'\r'</c> to <c>'\n'</c> and drops every other control
        /// character. Returns <paramref name="text"/> itself when nothing changes.</summary>
        static string Normalize(string text)
        {
            int i = 0;
            while (i < text.Length && (text[i] == '\n' || !char.IsControl(text[i]))) i++;
            if (i == text.Length) return text;

            var result = new StringBuilder(text.Length);
            result.Append(text, 0, i);
            for (; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r')
                {
                    result.Append('\n');
                    if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                }
                else if (c == '\n' || !char.IsControl(c))
                {
                    result.Append(c);
                }
            }
            return result.ToString();
        }

        /// <summary><paramref name="index"/>, moved back one unit when it would split a surrogate pair.</summary>
        static int ScalarFloor(string text, int index) =>
            index > 0 && index < text.Length && char.IsLowSurrogate(text[index]) && char.IsHighSurrogate(text[index - 1])
                ? index - 1 : index;

        static int PreviousBoundary(string text, int index)
        {
            if (index <= 0) return 0;
            return index >= 2 && char.IsLowSurrogate(text[index - 1]) && char.IsHighSurrogate(text[index - 2])
                ? index - 2 : index - 1;
        }

        static int NextBoundary(string text, int index)
        {
            if (index >= text.Length) return text.Length;
            return index + 1 < text.Length && char.IsHighSurrogate(text[index]) && char.IsLowSurrogate(text[index + 1])
                ? index + 2 : index + 1;
        }
    }

    /// <summary>
    /// Where <see cref="TextArea"/> sends its text and caret. The frame draw passes
    /// <see cref="SpriteBatchTextAreaSink"/>. Tests pass a recording sink, because a <see cref="SpriteBatch"/>
    /// needs a GPU device and which lines a frame draws is the thing they prove.
    /// </summary>
    internal interface ITextAreaSink
    {
        /// <summary>Draw one line with its top-left at <paramref name="position"/>.</summary>
        void DrawText(string text, Vector2 position, Vector4 color);

        /// <summary>Draw the caret sliver.</summary>
        void DrawCaret(Rect caret, Vector4 color);
    }

    /// <summary>The sink the frame draw uses. A struct, so the generic draw runs without boxing it.</summary>
    internal readonly struct SpriteBatchTextAreaSink : ITextAreaSink
    {
        readonly SpriteBatch _batch;
        readonly SpriteFont _font;
        readonly Texture2D _white;
        readonly float _scale;

        internal SpriteBatchTextAreaSink(SpriteBatch batch, SpriteFont font, Texture2D white, float scale)
        {
            _batch = batch;
            _font = font;
            _white = white;
            _scale = scale;
        }

        public void DrawText(string text, Vector2 position, Vector4 color) =>
            _batch.DrawString(_font, text, position, (Color)color, _scale);

        public void DrawCaret(Rect caret, Vector4 color) => GuiDraw.Fill(_batch, _white, caret, color);
    }
}
