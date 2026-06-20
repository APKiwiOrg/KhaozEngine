using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render2D;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// A tintable UI icon set drawn through the shared batched-quad path. The core set is CPU-baked into one
    /// alpha-mask atlas (white RGB, per-icon alpha) following the <c>VfxTextures</c> pattern - no shipped asset,
    /// headless-testable. Games register their own icons (which may point at their own textures) into the same
    /// string-keyed registry, drawn the same way via <see cref="GuiSurface.Icon"/>.
    /// </summary>
    public sealed class IconAtlas
    {
        readonly Dictionary<string, (Texture2D Tex, Vector4 SrcUV)> _reg = new();

        /// <summary>Register (or replace) an icon id with a texture + source UV sub-rect (u0,v0,u1,v1 in 0..1).</summary>
        public void Register(string id, Texture2D tex, Vector4 srcUV) => _reg[id] = (tex, srcUV);

        /// <summary>Look up an icon's texture + source UV. Returns false when the id is unknown.</summary>
        public bool TryGet(string id, out Texture2D tex, out Vector4 srcUV)
        {
            if (_reg.TryGetValue(id, out var e)) { tex = e.Tex; srcUV = e.SrcUV; return true; }
            tex = null!; srcUV = default; return false;
        }

        /// <summary>True when <paramref name="id"/> is registered.</summary>
        public bool Has(string id) => _reg.ContainsKey(id);

        // ---- Core atlas bake -------------------------------------------------------------------------------

        /// <summary>
        /// Bake the core icon set into one RGBA8 atlas (white RGB, per-icon alpha). Returns the pixel buffer,
        /// its width/height, and each core id's source UV sub-rect. Pure / headless. <paramref name="cell"/> is the
        /// per-icon cell size in texels (clamped to at least 8).
        /// </summary>
        public static (byte[] Pixels, int Width, int Height, IReadOnlyDictionary<string, Vector4> Uvs)
            BakeAtlasPixels(int cell = 64)
        {
            cell = Math.Max(8, cell);
            int count = Icons.All.Count;
            int cols = 4;
            int rows = (count + cols - 1) / cols;
            int w = cols * cell, h = rows * cell;
            var px = new byte[w * h * 4];
            for (int i = 0; i < px.Length; i += 4) { px[i] = 255; px[i + 1] = 255; px[i + 2] = 255; px[i + 3] = 0; }

            var uvs = new Dictionary<string, Vector4>(count);
            for (int idx = 0; idx < count; idx++)
            {
                int cx = (idx % cols) * cell, cy = (idx / cols) * cell;
                DrawIcon(Icons.All[idx], px, w, cx, cy, cell);
                uvs[Icons.All[idx]] = new Vector4((float)cx / w, (float)cy / h, (float)(cx + cell) / w, (float)(cy + cell) / h);
            }
            return (px, w, h, uvs);
        }

        /// <summary>Bake the core atlas and upload it to a sampleable texture on <paramref name="surface"/>'s device, returning a populated registry.</summary>
        public static IconAtlas Bake(Render2DSurface surface, int cell = 64)
        {
            ArgumentNullException.ThrowIfNull(surface);
            var (px, w, h, uvs) = BakeAtlasPixels(cell);
            Texture2D tex = surface.CreateTexture(px, w, h);
            return FromCore(tex, uvs);
        }

        /// <summary>Bake the core atlas and upload it on the snapshot <paramref name="context"/>'s device (for goldens).</summary>
        public static IconAtlas Bake(Render2DContext context, int cell = 64)
        {
            ArgumentNullException.ThrowIfNull(context);
            var (px, w, h, uvs) = BakeAtlasPixels(cell);
            Texture2D tex = context.CreateTexture(px, w, h);
            return FromCore(tex, uvs);
        }

        static IconAtlas FromCore(Texture2D tex, IReadOnlyDictionary<string, Vector4> uvs)
        {
            var a = new IconAtlas();
            foreach (var kv in uvs) a.Register(kv.Key, tex, kv.Value);
            return a;
        }

        // ---- Per-icon rasterisation into a cell's alpha ----------------------------------------------------

        static void DrawIcon(string id, byte[] px, int w, int cx, int cy, int n)
        {
            // Work in a normalised cell: centre (0.5,0.5), unit = n. Stroke ~ 8% of the cell.
            float s = MathF.Max(1.5f, n * 0.08f);
            float c = n * 0.5f;
            switch (id)
            {
                case Icons.Coin:
                    Ring(px, w, cx, cy, n, c, c, n * 0.36f, s);
                    Ring(px, w, cx, cy, n, c, c, n * 0.20f, s * 0.7f);
                    break;
                case Icons.Heart:
                    DiscMask(px, w, cx, cy, n, c - n * 0.16f, c - n * 0.12f, n * 0.18f);
                    DiscMask(px, w, cx, cy, n, c + n * 0.16f, c - n * 0.12f, n * 0.18f);
                    FillTri(px, w, cx, cy, n,
                        new Vector2(c - n * 0.32f, c - n * 0.04f),
                        new Vector2(c + n * 0.32f, c - n * 0.04f),
                        new Vector2(c, c + n * 0.34f));
                    break;
                case Icons.Skull:
                    DiscMask(px, w, cx, cy, n, c, c - n * 0.06f, n * 0.30f);
                    FillRect(px, w, cx, cy, n, c - n * 0.22f, c - n * 0.06f, c + n * 0.22f, c + n * 0.22f);
                    Punch(px, w, cx, cy, n, c - n * 0.12f, c - n * 0.06f, n * 0.09f);   // left eye
                    Punch(px, w, cx, cy, n, c + n * 0.12f, c - n * 0.06f, n * 0.09f);   // right eye
                    Punch(px, w, cx, cy, n, c, c + n * 0.08f, n * 0.05f);               // nose
                    break;
                case Icons.Crosshair:
                    Ring(px, w, cx, cy, n, c, c, n * 0.30f, s);
                    Line(px, w, cx, cy, n, c, c - n * 0.42f, c, c - n * 0.18f, s);
                    Line(px, w, cx, cy, n, c, c + n * 0.18f, c, c + n * 0.42f, s);
                    Line(px, w, cx, cy, n, c - n * 0.42f, c, c - n * 0.18f, c, s);
                    Line(px, w, cx, cy, n, c + n * 0.18f, c, c + n * 0.42f, c, s);
                    break;
                case Icons.Gear:
                    int teeth = 8;
                    for (int t = 0; t < teeth; t++)
                    {
                        float a = t * (MathF.PI * 2f / teeth);
                        float tx = c + MathF.Cos(a) * n * 0.40f, ty = c + MathF.Sin(a) * n * 0.40f;
                        DiscMask(px, w, cx, cy, n, tx, ty, n * 0.10f);
                    }
                    Ring(px, w, cx, cy, n, c, c, n * 0.28f, s * 1.3f);
                    Punch(px, w, cx, cy, n, c, c, n * 0.14f);
                    break;
                case Icons.Play:
                    FillTri(px, w, cx, cy, n,
                        new Vector2(c - n * 0.18f, c - n * 0.26f),
                        new Vector2(c - n * 0.18f, c + n * 0.26f),
                        new Vector2(c + n * 0.28f, c));
                    break;
                case Icons.Pause:
                    FillRect(px, w, cx, cy, n, c - n * 0.22f, c - n * 0.26f, c - n * 0.06f, c + n * 0.26f);
                    FillRect(px, w, cx, cy, n, c + n * 0.06f, c - n * 0.26f, c + n * 0.22f, c + n * 0.26f);
                    break;
                case Icons.Close:
                    Line(px, w, cx, cy, n, c - n * 0.24f, c - n * 0.24f, c + n * 0.24f, c + n * 0.24f, s);
                    Line(px, w, cx, cy, n, c + n * 0.24f, c - n * 0.24f, c - n * 0.24f, c + n * 0.24f, s);
                    break;
                case Icons.Check:
                    Line(px, w, cx, cy, n, c - n * 0.26f, c, c - n * 0.06f, c + n * 0.22f, s);
                    Line(px, w, cx, cy, n, c - n * 0.06f, c + n * 0.22f, c + n * 0.28f, c - n * 0.24f, s);
                    break;
                case Icons.Plus:
                    Line(px, w, cx, cy, n, c, c - n * 0.28f, c, c + n * 0.28f, s);
                    Line(px, w, cx, cy, n, c - n * 0.28f, c, c + n * 0.28f, c, s);
                    break;
                case Icons.Minus:
                    Line(px, w, cx, cy, n, c - n * 0.28f, c, c + n * 0.28f, c, s);
                    break;
                case Icons.ChevronLeft:
                    Line(px, w, cx, cy, n, c + n * 0.14f, c - n * 0.26f, c - n * 0.14f, c, s);
                    Line(px, w, cx, cy, n, c - n * 0.14f, c, c + n * 0.14f, c + n * 0.26f, s);
                    break;
                case Icons.ChevronRight:
                    Line(px, w, cx, cy, n, c - n * 0.14f, c - n * 0.26f, c + n * 0.14f, c, s);
                    Line(px, w, cx, cy, n, c + n * 0.14f, c, c - n * 0.14f, c + n * 0.26f, s);
                    break;
                case Icons.ChevronUp:
                    Line(px, w, cx, cy, n, c - n * 0.26f, c + n * 0.14f, c, c - n * 0.14f, s);
                    Line(px, w, cx, cy, n, c, c - n * 0.14f, c + n * 0.26f, c + n * 0.14f, s);
                    break;
                case Icons.ChevronDown:
                    Line(px, w, cx, cy, n, c - n * 0.26f, c - n * 0.14f, c, c + n * 0.14f, s);
                    Line(px, w, cx, cy, n, c, c + n * 0.14f, c + n * 0.26f, c - n * 0.14f, s);
                    break;
            }
        }

        // alpha = max(existing, value) so overlapping strokes union cleanly.
        static void Plot(byte[] px, int w, int cx, int cy, int n, int lx, int ly, float a)
        {
            if (lx < 0 || ly < 0 || lx >= n || ly >= n) return;
            int gx = cx + lx, gy = cy + ly;
            int i = (gy * w + gx) * 4 + 3;
            byte v = (byte)Math.Clamp((int)(a * 255f + 0.5f), 0, 255);
            if (v > px[i]) px[i] = v;
        }

        // Hard-clear alpha to 0 (eye/nose holes), localised to a disc.
        static void Punch(byte[] px, int w, int cx, int cy, int n, float ox, float oy, float r)
        {
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = x + 0.5f - ox, dy = y + 0.5f - oy;
                    if (dx * dx + dy * dy <= r * r)
                    {
                        int gx = cx + x, gy = cy + y;
                        px[(gy * w + gx) * 4 + 3] = 0;
                    }
                }
        }

        static void DiscMask(byte[] px, int w, int cx, int cy, int n, float ox, float oy, float r)
        {
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = x + 0.5f - ox, dy = y + 0.5f - oy;
                    float d = MathF.Sqrt(dx * dx + dy * dy);
                    float a = Math.Clamp(r - d + 0.5f, 0f, 1f);   // 1px AA edge
                    if (a > 0f) Plot(px, w, cx, cy, n, x, y, a);
                }
        }

        static void Ring(byte[] px, int w, int cx, int cy, int n, float ox, float oy, float r, float thick)
        {
            float half = thick * 0.5f;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = x + 0.5f - ox, dy = y + 0.5f - oy;
                    float d = MathF.Sqrt(dx * dx + dy * dy);
                    float a = Math.Clamp(half - MathF.Abs(d - r) + 0.5f, 0f, 1f);
                    if (a > 0f) Plot(px, w, cx, cy, n, x, y, a);
                }
        }

        static void Line(byte[] px, int w, int cx, int cy, int n, float x0, float y0, float x1, float y1, float thick)
        {
            float half = thick * 0.5f;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float a = Math.Clamp(half - DistToSegment(x + 0.5f, y + 0.5f, x0, y0, x1, y1) + 0.5f, 0f, 1f);
                    if (a > 0f) Plot(px, w, cx, cy, n, x, y, a);
                }
        }

        static void FillRect(byte[] px, int w, int cx, int cy, int n, float x0, float y0, float x1, float y1)
        {
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float fx = x + 0.5f, fy = y + 0.5f;
                    if (fx >= x0 && fx <= x1 && fy >= y0 && fy <= y1) Plot(px, w, cx, cy, n, x, y, 1f);
                }
        }

        static void FillTri(byte[] px, int w, int cx, int cy, int n, Vector2 a, Vector2 b, Vector2 cc)
        {
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    if (InTriangle(p, a, b, cc)) Plot(px, w, cx, cy, n, x, y, 1f);
                }
        }

        static float DistToSegment(float px_, float py, float x0, float y0, float x1, float y1)
        {
            float vx = x1 - x0, vy = y1 - y0;
            float wx = px_ - x0, wy = py - y0;
            float len2 = vx * vx + vy * vy;
            float t = len2 <= 1e-6f ? 0f : Math.Clamp((wx * vx + wy * vy) / len2, 0f, 1f);
            float dx = px_ - (x0 + t * vx), dy = py - (y0 + t * vy);
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        static bool InTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Sign(p, a, b), d2 = Sign(p, b, c), d3 = Sign(p, c, a);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0;
            bool pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }

        static float Sign(Vector2 p, Vector2 a, Vector2 b) => (p.X - b.X) * (a.Y - b.Y) - (a.X - b.X) * (p.Y - b.Y);
    }
}
