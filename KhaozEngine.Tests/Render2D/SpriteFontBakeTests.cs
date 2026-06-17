using System;
using System.IO;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Render2D
{
    /// <summary>
    /// Headless coverage for <see cref="SpriteFont.BakeCpu"/> (the device-free rasterization path): oversampling
    /// must raise atlas texel density without changing the logical layout metrics, and the default (oversample 1)
    /// must keep the original 512x256 bake so existing GPU goldens stay byte-identical.
    /// </summary>
    public sealed class SpriteFontBakeTests
    {
        static readonly string FontPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Roboto-Regular.ttf");
        static byte[] Ttf() => File.ReadAllBytes(FontPath);

        [Fact]
        public void Default_oversample_keeps_the_original_512x256_atlas()
        {
            BakedFont baked = SpriteFont.BakeCpu(Ttf(), 20f, 1);

            Assert.Equal(512, baked.AtlasW);
            Assert.Equal(256, baked.AtlasH);
            Assert.Equal(1f, baked.RenderScale);
        }

        [Fact]
        public void Oversample_raises_texel_density_via_a_taller_atlas()
        {
            BakedFont one = SpriteFont.BakeCpu(Ttf(), 20f, 1);
            BakedFont three = SpriteFont.BakeCpu(Ttf(), 20f, 3);

            Assert.Equal(512, three.AtlasW);               // width stays fixed
            Assert.True(three.AtlasH > one.AtlasH);         // 3x raster needs more rows
            Assert.Equal(1f / 3f, three.RenderScale, 5);    // atlas -> logical downscale
        }

        [Fact]
        public void Logical_layout_metrics_are_invariant_under_oversample()
        {
            BakedFont one = SpriteFont.BakeCpu(Ttf(), 20f, 1);
            BakedFont three = SpriteFont.BakeCpu(Ttf(), 20f, 3);

            // Line height + ascent are reported in logical pixels, so they match regardless of oversample (the
            // tiny residual is float error from rasterizing at 3x then dividing back down).
            Assert.True(Math.Abs(one.LineHeight - three.LineHeight) < 0.01f);
            Assert.True(Math.Abs(one.Ascent - three.Ascent) < 0.01f);

            // Every printable glyph keeps the same logical advance (this is what keeps text layout identical).
            for (char c = (char)32; c < (char)127; c++)
            {
                Assert.True(one.Glyphs.ContainsKey(c) && three.Glyphs.ContainsKey(c));
                Assert.True(Math.Abs(one.Glyphs[c].Advance - three.Glyphs[c].Advance) < 0.01f);
            }
        }

        [Fact]
        public void Oversampled_glyph_quads_are_drawn_at_the_same_logical_size()
        {
            BakedFont one = SpriteFont.BakeCpu(Ttf(), 20f, 1);
            BakedFont three = SpriteFont.BakeCpu(Ttf(), 20f, 3);

            // Glyph bitmaps are bigger in the oversampled atlas, but W * RenderScale (the on-screen logical width)
            // is the same to within a pixel (stb rounds the bitmap bounding box independently at each scale, so
            // the inked extent only matches approximately - the advance, tested above, is what's exact).
            GlyphInfo m1 = one.Glyphs['M'], m3 = three.Glyphs['M'];
            Assert.True(m3.W > m1.W && m3.H > m1.H);
            Assert.True(Math.Abs(m1.W * one.RenderScale - m3.W * three.RenderScale) < 1.5f);
            Assert.True(Math.Abs(m1.H * one.RenderScale - m3.H * three.RenderScale) < 1.5f);
        }

        [Fact]
        public void Oversampled_atlas_has_real_glyph_coverage()
        {
            BakedFont three = SpriteFont.BakeCpu(Ttf(), 20f, 3);

            // Alpha channel carries glyph coverage; a baked atlas must have some non-zero alpha.
            int nonZero = 0;
            for (int i = 3; i < three.Atlas.Length; i += 4)
                if (three.Atlas[i] != 0) nonZero++;
            Assert.True(nonZero > 0);
        }
    }
}
