using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using StbTrueTypeSharp;
using Veldrid;

namespace KhaozEngine.Render2D
{
    internal sealed class GlyphInfo
    {
        public int Ax, Ay, W, H, XOff, YOff;
        public float Advance;
    }

    /// <summary>
    /// A bitmap font rasterized at load time from a TrueType file (stb_truetype) into a single atlas.
    /// Draw with <see cref="SpriteBatch.DrawString"/>.
    /// </summary>
    public sealed class SpriteFont : IDisposable
    {
        internal readonly Texture2D Atlas;
        internal readonly int AtlasW, AtlasH;
        internal readonly float Ascent;
        internal readonly Dictionary<char, GlyphInfo> Glyphs = new();

        /// <summary>Recommended line advance (pixels) at the baked size.</summary>
        public float LineHeight { get; }

        SpriteFont(Texture2D atlas, int aw, int ah, float ascent, float lineHeight)
        {
            Atlas = atlas; AtlasW = aw; AtlasH = ah; Ascent = ascent; LineHeight = lineHeight;
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

        internal static unsafe SpriteFont Build(GraphicsDevice gd, byte[] ttf, float pixelHeight)
        {
            var handle = GCHandle.Alloc(ttf, GCHandleType.Pinned);
            try
            {
                byte* p = (byte*)handle.AddrOfPinnedObject();
                var info = new StbTrueType.stbtt_fontinfo();
                StbTrueType.stbtt_InitFont(info, p, StbTrueType.stbtt_GetFontOffsetForIndex(p, 0));
                float scale = StbTrueType.stbtt_ScaleForPixelHeight(info, pixelHeight);
                int ascent, descent, lineGap;
                StbTrueType.stbtt_GetFontVMetrics(info, &ascent, &descent, &lineGap);
                float lineHeight = (ascent - descent + lineGap) * scale;

                int aw = 512, ah = 256;
                byte[] atlas = new byte[aw * ah * 4];
                int penX = 2, penY = 2, rowH = 0;
                var glyphs = new Dictionary<char, GlyphInfo>();
                for (int cp = 32; cp < 127; cp++)
                {
                    int x0, y0, x1, y1;
                    StbTrueType.stbtt_GetCodepointBitmapBox(info, cp, scale, scale, &x0, &y0, &x1, &y1);
                    int gw = x1 - x0, gh = y1 - y0;
                    if (penX + gw + 2 > aw) { penX = 2; penY += rowH + 2; rowH = 0; }
                    if (gw > 0 && gh > 0)
                    {
                        byte[] tmp = new byte[gw * gh];
                        fixed (byte* op = tmp)
                            StbTrueType.stbtt_MakeCodepointBitmap(info, op, gw, gh, gw, scale, scale, cp);
                        for (int yy = 0; yy < gh; yy++)
                            for (int xx = 0; xx < gw; xx++)
                            {
                                byte cov = tmp[yy * gw + xx];
                                int ai = ((penY + yy) * aw + (penX + xx)) * 4;
                                atlas[ai] = 255; atlas[ai + 1] = 255; atlas[ai + 2] = 255; atlas[ai + 3] = cov;
                            }
                    }
                    int adv, lsb;
                    StbTrueType.stbtt_GetCodepointHMetrics(info, cp, &adv, &lsb);
                    glyphs[(char)cp] = new GlyphInfo { Ax = penX, Ay = penY, W = gw, H = gh, XOff = x0, YOff = y0, Advance = adv * scale };
                    penX += gw + 2; rowH = Math.Max(rowH, gh);
                }

                var tex = gd.ResourceFactory.CreateTexture(TextureDescription.Texture2D(
                    (uint)aw, (uint)ah, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Sampled));
                gd.UpdateTexture(tex, atlas, 0, 0, 0, (uint)aw, (uint)ah, 1, 0, 0);

                var font = new SpriteFont(new Texture2D(tex, aw, ah), aw, ah, ascent * scale, lineHeight);
                foreach (var kv in glyphs) font.Glyphs[kv.Key] = kv.Value;
                return font;
            }
            finally { handle.Free(); }
        }
    }
}
