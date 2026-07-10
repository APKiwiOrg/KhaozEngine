using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Render2D
{
    /// <summary>
    /// Headless coverage for the default baked glyph range and the fallback-glyph semantics of
    /// <see cref="SpriteFont"/>. The default bake must cover printable ASCII plus Latin-1 Supplement and
    /// Latin Extended-A (U+0020..U+017F) so accented Western/Central European text renders out of the box,
    /// and any codepoint outside the baked coverage must measure and draw as the visible
    /// <see cref="SpriteFont.FallbackChar"/> glyph instead of silently dropping (control characters stay
    /// zero-width). Measurement goes through the same resolver as drawing, so metrics and rendering agree.
    /// </summary>
    public sealed class SpriteFontCoverageTests
    {
        static readonly string FontPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Roboto-Regular.ttf");
        static BakedFont Bake() => SpriteFont.BakeCpu(File.ReadAllBytes(FontPath), 20f, 1);

        static float Width(BakedFont baked, string text) => SpriteFont.MeasureWidth(baked.Glyphs, text);

        [Fact]
        public void Latin1_and_ExtendedA_letters_bake_real_glyphs()
        {
            BakedFont baked = Bake();

            // A sample across Latin-1 Supplement (U+00C0..U+00FF) and Latin Extended-A (U+0100..U+017F),
            // all present in the bundled Roboto face.
            foreach (char c in "ÀÉÎÕÜßàçéñöûÿĀăĆčĐēĞğİıŁłŃňŒœŘšŤůŸŽž")
            {
                Assert.True(baked.Glyphs.TryGetValue(c, out GlyphInfo? g), $"glyph U+{(int)c:X4} '{c}' not baked");
                Assert.True(g!.W > 0 && g.H > 0, $"glyph U+{(int)c:X4} '{c}' baked empty ({g.W}x{g.H})");
                Assert.True(g.Advance > 0, $"glyph U+{(int)c:X4} '{c}' has no advance");
            }
        }

        [Fact]
        public void Accented_glyphs_are_distinct_from_their_base_letters()
        {
            BakedFont baked = Bake();

            GlyphInfo a = baked.Glyphs['A'], aGrave = baked.Glyphs['À'];
            Assert.True(aGrave.Ax != a.Ax || aGrave.Ay != a.Ay); // its own atlas cell, not an alias
            Assert.True(aGrave.H > a.H);                          // the accent adds inked height
        }

        [Fact]
        public void Measure_counts_accented_chars()
        {
            BakedFont baked = Bake();

            Assert.True(Width(baked, "À") > 0);
            Assert.True(Width(baked, "AÀ") > Width(baked, "A"));
        }

        [Fact]
        public void Measure_of_a_localized_string_equals_its_glyph_by_glyph_width()
        {
            BakedFont baked = Bake();
            const string text = "NOTES DE MISE À JOUR";

            float sum = 0;
            foreach (char c in text)
            {
                float w = Width(baked, c.ToString());
                Assert.True(w > 0, $"char U+{(int)c:X4} '{c}' measured zero-width (dropped)");
                sum += w;
            }
            Assert.Equal(sum, Width(baked, text), 3);
        }

        [Fact]
        public void Unbaked_codepoint_resolves_to_the_visible_fallback_glyph()
        {
            BakedFont baked = Bake();

            // U+4E00 (CJK) is far outside the baked coverage: it must measure as the fallback glyph,
            // not zero-width, in both the measure and draw paths (they share ResolveGlyph).
            float fallback = baked.Glyphs[SpriteFont.FallbackChar].Advance;
            Assert.True(fallback > 0);
            Assert.Equal(fallback, Width(baked, "一"), 3);
            Assert.Equal(Width(baked, "A") * 2 + fallback, Width(baked, "A一A"), 3);

            int i = 0;
            GlyphInfo? g = SpriteFont.ResolveGlyph(baked.Glyphs, "一", ref i);
            Assert.NotNull(g);
            Assert.Equal(baked.Glyphs[SpriteFont.FallbackChar], g);
        }

        [Fact]
        public void Astral_codepoint_resolves_to_one_fallback_glyph_not_two()
        {
            BakedFont baked = Bake();

            // U+1F600 is a surrogate pair in UTF-16; it must resolve to a single fallback glyph.
            Assert.Equal(baked.Glyphs[SpriteFont.FallbackChar].Advance, Width(baked, "\U0001F600"), 3);
        }

        [Fact]
        public void Control_chars_stay_zero_width_and_are_never_substituted()
        {
            BakedFont baked = Bake();

            Assert.Equal(0f, Width(baked, "\n"));
            Assert.Equal(0f, Width(baked, "\r\t"));
            Assert.Equal(Width(baked, "AB"), Width(baked, "A\nB"), 3);
        }

        [Fact]
        public void Codepoints_the_face_lacks_are_not_baked_as_empty_boxes()
        {
            BakedFont baked = Bake();

            // Every baked glyph must be a real outline the face contains: either inked (W/H > 0) or a
            // legitimate blank with an advance (space, NBSP). A .notdef bake would show up as an empty
            // box with no ink for a letter codepoint.
            foreach (KeyValuePair<char, GlyphInfo> kv in baked.Glyphs)
            {
                bool inked = kv.Value.W > 0 && kv.Value.H > 0;
                bool blankWithAdvance = kv.Value.W <= 0 && kv.Value.H <= 0 && kv.Value.Advance > 0;
                Assert.True(inked || blankWithAdvance, $"glyph U+{(int)kv.Key:X4} baked as an empty box");
            }
        }

        [Fact]
        public void Ascii_metrics_are_unchanged_from_the_legacy_bake()
        {
            // Captured from the pre-widening bake (ASCII 32..126 only) at pixelHeight 20, density 1:
            // widening the coverage must not move any existing ASCII metric.
            BakedFont baked = Bake();

            Assert.Equal(20.000002f, baked.LineHeight, 4);
            Assert.Equal(15.833334f, baked.Ascent, 4);
            Assert.Equal(11.133334f, baked.Glyphs['A'].Advance, 4);
            Assert.Equal(87.86667f, Width(baked, "Hello World"), 3);
        }

        [Fact]
        public void Widened_default_bake_keeps_the_fixed_atlas_width()
        {
            BakedFont baked = Bake();

            Assert.Equal(512, baked.AtlasW);
            Assert.True(baked.Glyphs.Count > 300, $"expected the widened coverage, got {baked.Glyphs.Count} glyphs");
        }
    }
}
