using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// Headless coverage for the text scales added to the four widgets that still drew through the no-scale
    /// <c>DrawString</c> (#244): <see cref="Dropdown.TextScale"/>, <see cref="TextInput.TextScale"/>,
    /// <see cref="TreeView.TextScale"/> and <see cref="ProgressBar.OverlayTextScale"/>. Each defaults to <c>1f</c>,
    /// so every existing caller renders byte-identically.
    /// <para>
    /// The layout math is here rather than on-device: <see cref="GuiDraw.CenteredTextY"/> is the one vertical
    /// centring term all four share, and <see cref="TextInput.DrawLayout"/> is the field's three width terms (the
    /// caret x, the overflow-clip test, and the drawn text). TextInput is the trap of this set: it carries THREE
    /// width terms rather than one, and missing the clip test is silent (it under-clips or over-flushes instead of
    /// throwing). The forwards from each Draw into the scaled DrawString have their own on-device net
    /// (<c>WidgetTextScaleForwardGpuTests</c>), since no pure test can see a dropped argument.
    /// </para>
    /// </summary>
    public class WidgetTextScaleTests
    {
        // 10px/char, 20px line height.
        sealed class FixedFont : ITextMeasurer
        {
            public float LineHeight => 20f;
            public Vector2 Measure(string text) => new(text.Length * 10f, 20f);
        }

        static readonly FixedFont Font = new();

        // --- the field defaults ------------------------------------------------

        [Fact]
        public void Every_new_text_scale_defaults_to_one()
        {
            var dropdown = new Dropdown(new[] { new DropdownOption("a", 0) }, new Rect(0, 0, 120, 24));
            Assert.Equal(1f, dropdown.TextScale);
            Assert.Equal(1f, new TextInput(new Rect(0, 0, 200, 30)).TextScale);
            Assert.Equal(1f, new TreeView(new Rect(0, 0, 200, 120)).TextScale);
            Assert.Equal(1f, new ProgressBar(new Rect(0, 0, 200, 20)).OverlayTextScale);
        }

        // --- the shared vertical centring term ---------------------------------

        [Fact]
        public void CenteredTextY_at_scale_one_reproduces_the_unscaled_expression()
        {
            // The exact pre-scale expression the three row-centred widgets used inline.
            const float rowY = 50f, rowHeight = 24f, lineHeight = 20f;
            Assert.Equal(rowY + (rowHeight - lineHeight) * 0.5f, GuiDraw.CenteredTextY(rowY, rowHeight, lineHeight));
            Assert.Equal(GuiDraw.CenteredTextY(rowY, rowHeight, lineHeight),
                GuiDraw.CenteredTextY(rowY, rowHeight, lineHeight, 1f));
        }

        [Fact]
        public void CenteredTextY_keeps_the_scaled_line_centred()
        {
            // A half-height line in a 24px row: 50 + (24 - 10) / 2 = 57, still centred rather than pinned to the top.
            Assert.Equal(57f, GuiDraw.CenteredTextY(50f, 24f, 20f, 0.5f), 3);
        }

        // --- TextInput: the three width terms ----------------------------------

        // A 200-wide field at x=100, text starting 8px in (the fixed pad, which no scale touches).
        static readonly Rect Field = new(100, 50, 200, 30);
        const float TextX = 108f;

        [Fact]
        public void TextInput_DrawLayout_at_scale_one_reproduces_todays_exact_terms()
        {
            const string text = "abcdef";   // 60px at 10px/char
            var l = TextInput.DrawLayout(Font, Field, TextX, text, text, 1f);

            Assert.Equal(TextX, l.TextX, 3);
            Assert.Equal(Field.Y + (Field.Height - Font.LineHeight) * 0.5f, l.TextY, 3);
            Assert.Equal(TextX + 60f + 1f, l.CaretX, 3);
            Assert.False(l.Clip);           // 108 + 60 + 2 + 1 = 171 < 300
        }

        [Fact]
        public void TextInput_caret_trails_the_drawn_width_not_the_unscaled_one()
        {
            const string text = "abcdef";
            float full = TextInput.DrawLayout(Font, Field, TextX, text, text, 1f).CaretX;
            float half = TextInput.DrawLayout(Font, Field, TextX, text, text, 0.5f).CaretX;

            Assert.Equal(TextX + 60f + 1f, full, 3);
            Assert.Equal(TextX + 30f + 1f, half, 3);   // the caret follows the glyphs, not the measure
        }

        [Fact]
        public void TextInput_overflow_clip_measures_the_drawn_width_not_the_unscaled_one()
        {
            // 24 chars = 240px unscaled: 108 + 240 + 2 + 1 = 351 > the field's right edge at 300, so it clips.
            string wide = new('x', 24);
            Assert.True(TextInput.DrawLayout(Font, Field, TextX, wide, wide, 1f).Clip);

            // The same text drawn at half scale is 120px wide: 108 + 120 + 3 = 231 < 300, so it fits and must NOT
            // clip. This is the silent term: a scale that reaches the DrawString but not this test flushes the
            // batch twice per frame to scissor text that never leaves the box.
            Assert.False(TextInput.DrawLayout(Font, Field, TextX, wide, wide, 0.5f).Clip);

            // And the converse: text that fits unscaled overflows once it is drawn larger.
            string mid = new('x', 16);      // 160px unscaled fits, 320px at 2x does not
            Assert.False(TextInput.DrawLayout(Font, Field, TextX, mid, mid, 1f).Clip);
            Assert.True(TextInput.DrawLayout(Font, Field, TextX, mid, mid, 2f).Clip);
        }

        [Fact]
        public void TextInput_empty_field_puts_the_caret_at_the_text_origin_at_any_scale()
        {
            // An empty field shows the placeholder, but the caret belongs to the (empty) VALUE, so it must not
            // trail the placeholder's width at any scale.
            const string placeholder = "type here";
            Assert.Equal(TextX + 1f, TextInput.DrawLayout(Font, Field, TextX, placeholder, "", 1f).CaretX, 3);
            Assert.Equal(TextX + 1f, TextInput.DrawLayout(Font, Field, TextX, placeholder, "", 0.5f).CaretX, 3);
        }

        [Fact]
        public void TextInput_overflow_clip_measures_what_is_shown_and_the_caret_measures_the_value()
        {
            // A long placeholder over an empty value: the clip test is about the placeholder (what is drawn),
            // the caret is about the value (what is edited). The two terms must not be collapsed into one.
            string placeholder = new('x', 24);
            var l = TextInput.DrawLayout(Font, Field, TextX, placeholder, "", 1f);
            Assert.True(l.Clip);
            Assert.Equal(TextX + 1f, l.CaretX, 3);
        }

        [Fact]
        public void TextInput_vertical_centring_follows_the_scale()
        {
            Assert.Equal(GuiDraw.CenteredTextY(Field.Y, Field.Height, Font.LineHeight, 0.5f),
                TextInput.DrawLayout(Font, Field, TextX, "abc", "abc", 0.5f).TextY, 3);
        }
    }
}
