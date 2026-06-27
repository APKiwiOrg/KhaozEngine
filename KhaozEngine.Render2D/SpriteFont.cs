using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using StbTrueTypeSharp;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render2D
{
    internal sealed class GlyphInfo
    {
        // Ax/Ay/W/H/XOff/YOff are in ATLAS texels (the supersampled raster); the dest quad is scaled down by
        // SpriteFont.RenderScale at draw time. Advance is in LOGICAL pixels (already divided by the oversample
        // factor), so layout/measurement is identical regardless of oversample.
        public int Ax, Ay, W, H, XOff, YOff;
        public float Advance;
    }

    /// <summary>The CPU-side result of rasterizing a font: a device-free atlas + glyph table + metrics.</summary>
    internal sealed class BakedFont
    {
        public byte[] Atlas = Array.Empty<byte>();
        public int AtlasW, AtlasH;
        public float Ascent;       // logical pixels
        public float LineHeight;   // logical pixels
        public float RenderScale;  // 1 / oversample (atlas texels -> logical pixels)
        public Dictionary<char, GlyphInfo> Glyphs = new();
    }

    /// <summary>
    /// A bitmap font rasterized at load time from a TrueType file (stb_truetype) into a single atlas.
    /// Draw with <see cref="SpriteBatch.DrawString(KhaozEngine.Render2D.SpriteFont, string, System.Numerics.Vector2, KhaozEngine.Primitives.Color)"/>.
    /// <para>
    /// An <c>oversample</c> factor rasterizes the atlas at <c>pixelHeight * oversample</c> while reporting all
    /// layout metrics (<see cref="Measure"/>, <see cref="LineHeight"/>, glyph advances) at the logical
    /// <c>pixelHeight</c>. The extra texel density stays crisp when a design-space viewport upscales the text to a
    /// higher-resolution framebuffer; pass <c>oversample &gt; 1</c> (2-3 covers typical HiDPI / design-viewport
    /// upscales). <c>oversample == 1</c> is the original pixel-for-pixel bake.
    /// </para>
    /// </summary>
    public sealed class SpriteFont : IDisposable, ITextMeasurer
    {
        internal readonly Texture2D Atlas;
        internal readonly int AtlasW, AtlasH;
        internal readonly float Ascent;
        /// <summary>Atlas-texel -> logical-pixel scale (1 / oversample); applied to the glyph dest quad.</summary>
        internal readonly float RenderScale;
        internal readonly Dictionary<char, GlyphInfo> Glyphs = new();

        /// <summary>Recommended line advance, in logical pixels (independent of the oversample factor).</summary>
        public float LineHeight { get; }

        SpriteFont(Texture2D atlas, int aw, int ah, float ascent, float lineHeight, float renderScale)
        {
            Atlas = atlas; AtlasW = aw; AtlasH = ah; Ascent = ascent; LineHeight = lineHeight; RenderScale = renderScale;
        }

        /// <summary>Width/height (pixels) the string occupies at the baked size.</summary>
        public Vector2 Measure(string text)
        {
            float w = 0;
            foreach (char c in text)
                if (Glyphs.TryGetValue(c, out var g)) w += g.Advance;
            return new Vector2(w, LineHeight);
        }

        public void Dispose() => Atlas.Dispose();

        internal static SpriteFont Build(IGpuDevice gd, byte[] ttf, float pixelHeight, int oversample = 1)
        {
            BakedFont baked = BakeCpu(ttf, pixelHeight, oversample);
            var tex = gd.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                (uint)baked.AtlasW, (uint)baked.AtlasH, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
            gd.UpdateTexture(tex, baked.Atlas, 0, 0, (uint)baked.AtlasW, (uint)baked.AtlasH);

            var font = new SpriteFont(new Texture2D(tex, baked.AtlasW, baked.AtlasH),
                baked.AtlasW, baked.AtlasH, baked.Ascent, baked.LineHeight, baked.RenderScale);
            foreach (var kv in baked.Glyphs) font.Glyphs[kv.Key] = kv.Value;
            return font;
        }

        /// <summary>
        /// Rasterizes the printable ASCII range (32-126) into a single device-free RGBA atlas (white with the
        /// coverage in alpha) and reports logical-pixel metrics. The atlas is baked at <paramref name="pixelHeight"/>
        /// * <paramref name="oversample"/>; advances/line height/ascent are divided back down so layout is identical
        /// at any oversample. The atlas width is fixed at 512; its height grows past the 256 floor only when a larger
        /// raster needs the room (so <c>oversample == 1</c> stays byte-identical to the original 512x256 bake).
        /// No GPU device required, so it is unit-testable headless.
        /// </summary>
        internal static unsafe BakedFont BakeCpu(byte[] ttf, float pixelHeight, int oversample)
        {
            if (oversample < 1) oversample = 1;
            var handle = GCHandle.Alloc(ttf, GCHandleType.Pinned);
            try
            {
                byte* p = (byte*)handle.AddrOfPinnedObject();
                var info = new StbTrueType.stbtt_fontinfo();
                StbTrueType.stbtt_InitFont(info, p, StbTrueType.stbtt_GetFontOffsetForIndex(p, 0));
                float scale = StbTrueType.stbtt_ScaleForPixelHeight(info, pixelHeight * oversample);
                float k = 1f / oversample; // atlas (supersampled) -> logical pixels
                int ascent, descent, lineGap;
                StbTrueType.stbtt_GetFontVMetrics(info, &ascent, &descent, &lineGap);
                float lineHeight = (ascent - descent + lineGap) * scale * k;

                // Pass 1: pack glyph boxes (atlas texels) and record metrics; learn the needed atlas height.
                const int aw = 512;
                int penX = 2, penY = 2, rowH = 0;
                var glyphs = new Dictionary<char, GlyphInfo>();
                for (int cp = 32; cp < 127; cp++)
                {
                    int x0, y0, x1, y1;
                    StbTrueType.stbtt_GetCodepointBitmapBox(info, cp, scale, scale, &x0, &y0, &x1, &y1);
                    int gw = x1 - x0, gh = y1 - y0;
                    if (penX + gw + 2 > aw) { penX = 2; penY += rowH + 2; rowH = 0; }
                    int adv, lsb;
                    StbTrueType.stbtt_GetCodepointHMetrics(info, cp, &adv, &lsb);
                    glyphs[(char)cp] = new GlyphInfo { Ax = penX, Ay = penY, W = gw, H = gh, XOff = x0, YOff = y0, Advance = adv * scale * k };
                    penX += gw + 2; rowH = Math.Max(rowH, gh);
                }
                int ah = Math.Max(256, penY + rowH + 2);

                // Pass 2: rasterize each glyph into the allocated atlas at its packed position.
                byte[] atlas = new byte[aw * ah * 4];
                foreach (var kv in glyphs)
                {
                    GlyphInfo g = kv.Value;
                    if (g.W <= 0 || g.H <= 0) continue;
                    byte[] tmp = new byte[g.W * g.H];
                    fixed (byte* op = tmp)
                        StbTrueType.stbtt_MakeCodepointBitmap(info, op, g.W, g.H, g.W, scale, scale, kv.Key);
                    for (int yy = 0; yy < g.H; yy++)
                        for (int xx = 0; xx < g.W; xx++)
                        {
                            byte cov = tmp[yy * g.W + xx];
                            int ai = ((g.Ay + yy) * aw + (g.Ax + xx)) * 4;
                            atlas[ai] = 255; atlas[ai + 1] = 255; atlas[ai + 2] = 255; atlas[ai + 3] = cov;
                        }
                }

                return new BakedFont
                {
                    Atlas = atlas, AtlasW = aw, AtlasH = ah,
                    Ascent = ascent * scale * k, LineHeight = lineHeight, RenderScale = k,
                    Glyphs = glyphs,
                };
            }
            finally { handle.Free(); }
        }
    }
}
