using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;

namespace KhaozEngine.Render2D
{
    /// <summary>
    /// Draws 2D primitives (rectangles, lines, circles, rings, gradients, progress bars) through a
    /// <see cref="SpriteBatch"/> using a 1x1 white texture. The MonoGame-free 5.x port of the 4.x
    /// KhaozEngine.Graphics PrimitiveRenderer: rectangles take a <see cref="Rect"/>, colors are RGBA
    /// <see cref="Vector4"/> (0..1), points are <see cref="Vector2"/>. Rotated primitives (lines, rings)
    /// use the rotated-quad <see cref="SpriteBatch"/> overload so fractional thickness renders faithfully.
    /// </summary>
    public sealed class PrimitiveRenderer : IDisposable
    {
        /// <summary>Full-texture UV span for the owned white pixel: (u0, v0, u1, v1) = (0, 0, 1, 1).</summary>
        public static readonly Vector4 FullUV = new(0f, 0f, 1f, 1f);

        // Centered on the line's thickness (left edge, vertical middle) so DrawLine/DrawRing strokes are
        // centered on their path, matching the 4.x DrawRing line origin.
        static readonly Vector2 LineOrigin = new(0f, 0.5f);

        readonly Texture2D _white;
        readonly bool _ownsWhite;

        /// <summary>
        /// Creates a renderer that owns a fresh 1x1 white pixel on <paramref name="surface"/>'s device.
        /// The pixel is disposed by <see cref="Dispose"/>.
        /// </summary>
        public PrimitiveRenderer(Render2DSurface surface)
            : this(surface is null ? throw new ArgumentNullException(nameof(surface))
                : surface.CreateTexture(WhitePixel, 1, 1), ownsWhite: true) { }

        /// <summary>
        /// Creates a renderer that owns a fresh 1x1 white pixel on the snapshot <paramref name="context"/>'s
        /// device. The pixel is disposed by <see cref="Dispose"/>.
        /// </summary>
        public PrimitiveRenderer(Render2DContext context)
            : this(context is null ? throw new ArgumentNullException(nameof(context))
                : context.CreateTexture(WhitePixel, 1, 1), ownsWhite: true) { }

        /// <summary>
        /// Creates a renderer over a caller-supplied 1x1 white <paramref name="white"/> texture. The texture
        /// is NOT disposed by <see cref="Dispose"/> (the caller keeps ownership).
        /// </summary>
        public PrimitiveRenderer(Texture2D white)
            : this(white ?? throw new ArgumentNullException(nameof(white)), ownsWhite: false) { }

        PrimitiveRenderer(Texture2D white, bool ownsWhite)
        {
            _white = white;
            _ownsWhite = ownsWhite;
        }

        static byte[] WhitePixel => new byte[] { 255, 255, 255, 255 };

        /// <summary>Draws a filled rectangle.</summary>
        public void DrawFilledRect(SpriteBatch batch, Rect r, Color color) =>
            batch.Draw(_white, new Vector4(r.X, r.Y, r.Width, r.Height), color);

        /// <summary>Draws a rectangle outline (border only) as four thin filled rects.</summary>
        public void DrawRect(SpriteBatch batch, Rect r, Color color, float thickness = 1f)
        {
            float t = thickness;
            DrawFilledRect(batch, new Rect(r.X, r.Y, r.Width, t), color);              // top
            DrawFilledRect(batch, new Rect(r.X, r.Bottom - t, r.Width, t), color);     // bottom
            DrawFilledRect(batch, new Rect(r.X, r.Y, t, r.Height), color);             // left
            DrawFilledRect(batch, new Rect(r.Right - t, r.Y, t, r.Height), color);     // right
        }

        /// <summary>
        /// Draws a line from <paramref name="a"/> to <paramref name="c"/> as a rotated quad centered on its
        /// thickness (sub-pixel <paramref name="thickness"/> renders faithfully). No-op for a zero-length line.
        /// </summary>
        public void DrawLine(SpriteBatch batch, Vector2 a, Vector2 c, Color color, float thickness = 1f)
        {
            Vector2 edge = c - a;
            float len = edge.Length();
            if (len <= 0f) return;
            float angle = MathF.Atan2(edge.Y, edge.X);
            batch.Draw(_white, a, new Vector2(len, thickness), LineOrigin, angle, FullUV, color);
        }

        /// <summary>Draws a circle outline as <paramref name="segments"/> line segments.</summary>
        public void DrawCircle(SpriteBatch batch, Vector2 center, float radius, Color color, int segments = 32, float thickness = 1f)
        {
            if (segments < 3) segments = 3;
            float step = MathF.Tau / segments;
            Vector2 prev = center + new Vector2(radius, 0f);
            for (int i = 1; i <= segments; i++)
            {
                float angle = step * i;
                Vector2 cur = center + new Vector2(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);
                DrawLine(batch, prev, cur, color, thickness);
                prev = cur;
            }
        }

        /// <summary>
        /// Segment count for a ring of <paramref name="radius"/>: an explicit <paramref name="segmentsOverride"/>
        /// (floored at 3), or a radius-adaptive count clamped to [18, 64] so small rings stay cheap and large
        /// rings stay smooth.
        /// </summary>
        public static int RingSegments(float radius, int? segmentsOverride) =>
            segmentsOverride.HasValue
                ? Math.Max(3, segmentsOverride.Value)
                : Math.Clamp((int)(radius * 0.35f), 18, 64);

        /// <summary>
        /// Draws a ring (circle outline) with sub-pixel <b>float</b> thickness. Each segment is a rotated quad
        /// centered on the radius path, so fractional thicknesses render faithfully (unlike
        /// <see cref="DrawCircle"/>'s line width). No-op when radius or thickness is non-positive.
        /// </summary>
        public void DrawRing(SpriteBatch batch, Vector2 center, float radius, float thickness, Color color, int? segmentsOverride = null)
        {
            if (radius <= 0f || thickness <= 0f) return;
            int segments = RingSegments(radius, segmentsOverride);
            float step = MathF.Tau / segments;
            Vector2 p0 = center + new Vector2(radius, 0f);
            for (int i = 1; i <= segments; i++)
            {
                float angle = i * step;
                Vector2 p1 = center + new Vector2(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);
                DrawLine(batch, p0, p1, color, thickness);
                p0 = p1;
            }
        }

        /// <summary>Draws a filled circle as stacked 1px horizontal rects.</summary>
        public void DrawFilledCircle(SpriteBatch batch, Vector2 center, float radius, Color color)
        {
            int intRadius = (int)radius;
            for (int y = -intRadius; y <= intRadius; y++)
            {
                int halfWidth = (int)MathF.Sqrt(radius * radius - y * y);
                DrawFilledRect(batch, new Rect(center.X - halfWidth, center.Y + y, halfWidth * 2, 1), color);
            }
        }

        /// <summary>
        /// Draws a vertical gradient by rendering <paramref name="bands"/> horizontal strips with linearly
        /// interpolated colors between <paramref name="top"/> and <paramref name="bottom"/>.
        /// </summary>
        public void DrawVerticalGradient(SpriteBatch batch, Rect r, Color top, Color bottom, int bands = 12)
        {
            if (bands < 1) bands = 1;
            float bandHeight = r.Height / bands;
            Vector4 topV = top, bottomV = bottom;   // lerp in float space; implicit Color -> Vector4
            for (int i = 0; i < bands; i++)
            {
                float t = i / (float)(bands - 1 == 0 ? 1 : bands - 1);
                Color color = (Color)Vector4.Lerp(topV, bottomV, t);
                float y = r.Y + i * bandHeight;
                float h = (i == bands - 1) ? r.Bottom - y : MathF.Ceiling(bandHeight);
                DrawFilledRect(batch, new Rect(r.X, y, r.Width, h), color);
            }
        }

        /// <summary>
        /// Draws a progress bar: a <paramref name="bg"/> background, a <paramref name="fill"/> bar sized by
        /// <paramref name="progress"/> (0..1), and a <paramref name="border"/> outline. Fill geometry is capped
        /// (see <see cref="ComputeProgressBarLayout"/>) so short/thin bars keep a visible fill.
        /// </summary>
        public void DrawProgressBar(SpriteBatch batch, Rect r, float progress, Color fill, Color bg, Color border, float borderThickness = 1f)
        {
            DrawFilledRect(batch, r, bg);

            (Rect fillRect, float effectiveBorder) = ComputeProgressBarLayout(r, progress, borderThickness);
            if (fillRect.Width > 0f && fillRect.Height > 0f)
                DrawFilledRect(batch, fillRect, fill);

            if (effectiveBorder > 0f)
                DrawRect(batch, r, border, effectiveBorder);
        }

        /// <summary>
        /// Computes the inner fill rectangle and the effective border thickness for a progress bar. Pure
        /// geometry, extracted so it can be unit tested headlessly.
        /// </summary>
        /// <remarks>
        /// The requested border is capped so the inner fill area never collapses below 1px in either dimension.
        /// Without this, a short bar (e.g. a zoomed-out HP bar only 2px tall with a 1px border) has zero inner
        /// height: the fill never draws and the border alone covers the whole bar, rendering as a solid line in
        /// the border color. Capping the border lets the fill win on tiny bars.
        /// </remarks>
        internal static (Rect Fill, float EffectiveBorder) ComputeProgressBarLayout(Rect bounds, float progress, float borderThickness)
        {
            float clampedProgress = Math.Clamp(progress, 0f, 1f);

            // Largest border that still leaves >= 1px of inner space on the smaller axis.
            float maxBorder = MathF.Max(0f, (MathF.Min(bounds.Width, bounds.Height) - 1f) / 2f);
            float effectiveBorder = Math.Clamp(borderThickness, 0f, maxBorder);

            float innerWidth = bounds.Width - effectiveBorder * 2f;
            float innerHeight = bounds.Height - effectiveBorder * 2f;
            float fillWidth = innerWidth * clampedProgress;

            return (
                new Rect(bounds.X + effectiveBorder, bounds.Y + effectiveBorder, fillWidth, innerHeight),
                effectiveBorder);
        }

        /// <summary>Disposes the owned 1x1 white pixel (no-op when constructed over a caller-supplied texture).</summary>
        public void Dispose()
        {
            if (_ownsWhite) _white.Dispose();
        }
    }
}
