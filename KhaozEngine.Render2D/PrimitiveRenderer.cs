using System;
using System.Buffers;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;

namespace KhaozEngine.Render2D
{
    /// <summary>
    /// Draws 2D primitives (rectangles, lines, circles, rings, gradients, progress bars, convex polygon fills and
    /// strokes) through a
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

        /// <summary>
        /// Segment count for an arc spanning <paramref name="sweep"/> radians when the caller asked for
        /// <paramref name="segments"/> across a full turn: <c>ceil(segments * |sweep| / 2PI)</c>, floored at 1
        /// so a thin sweep still strokes at least one segment (and a small progress fraction stays smooth
        /// instead of collapsing to the whole <paramref name="segments"/> budget). Returns 0 when
        /// <paramref name="segments"/> is non-positive, so the arc draws nothing. Pure; extracted for headless tests.
        /// </summary>
        public static int ArcSegments(int segments, float sweep)
        {
            if (segments <= 0) return 0;
            int n = (int)MathF.Ceiling(segments * MathF.Abs(sweep) / MathF.Tau);
            return Math.Max(1, n);
        }

        /// <summary>
        /// The signed sweep, in radians, for a radial-progress ring: <c>clamp(fraction, 0, 1) * 2PI</c>, negated
        /// when <paramref name="clockwise"/> is false (screen space, +Y down: a positive sweep goes clockwise).
        /// A <paramref name="fraction"/> of 0 yields 0 (nothing) and 1 yields a full turn. Pure; extracted for
        /// headless tests.
        /// </summary>
        public static float RadialProgressSweep(float fraction, bool clockwise)
        {
            float sweep = Math.Clamp(fraction, 0f, 1f) * MathF.Tau;
            return clockwise ? sweep : -sweep;
        }

        /// <summary>
        /// Strokes a partial ring: an arc outline of <see cref="ArcSegments"/>-scaled line segments from
        /// <paramref name="startAngleRadians"/> spanning <paramref name="sweepAngleRadians"/> radians at
        /// <paramref name="radius"/> from <paramref name="center"/>, each a line of float
        /// <paramref name="thickness"/> (same stroke meaning as <see cref="DrawCircle"/>). Angles are in radians
        /// in screen space (+Y down, so a positive sweep goes clockwise); <paramref name="startAngleRadians"/> of
        /// <c>-PI/2</c> is 12 o'clock. The sweep is arbitrary: it may exceed 2PI (over a full turn) or be negative
        /// (the other direction), and the segment count scales with it so the stroke stays smooth at small
        /// fractions. No-op when <paramref name="radius"/>, <paramref name="thickness"/>, or
        /// <paramref name="segments"/> is non-positive.
        /// </summary>
        public void DrawArc(SpriteBatch batch, Vector2 center, float radius, float thickness,
                            float startAngleRadians, float sweepAngleRadians, Color color, int segments = 64)
        {
            if (radius <= 0f || thickness <= 0f) return;
            int n = ArcSegments(segments, sweepAngleRadians);
            if (n <= 0) return;
            float step = sweepAngleRadians / n;
            Vector2 prev = center + new Vector2(MathF.Cos(startAngleRadians) * radius, MathF.Sin(startAngleRadians) * radius);
            for (int i = 1; i <= n; i++)
            {
                float angle = startAngleRadians + step * i;
                Vector2 cur = center + new Vector2(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);
                DrawLine(batch, prev, cur, color, thickness);
                prev = cur;
            }
        }

        /// <summary>
        /// Strokes a radial-progress ring: <paramref name="fraction"/> (clamped to [0,1]) of a full circle from
        /// <paramref name="startAngleRadians"/> (default <c>-PI/2</c>, 12 o'clock), sweeping
        /// <paramref name="clockwise"/> by default. 0 draws nothing, 1 draws a full ring. Delegates to
        /// <see cref="DrawArc"/> with sweep = <see cref="RadialProgressSweep"/>. Handy for countdown/cooldown
        /// rings. No-op when <paramref name="radius"/>, <paramref name="thickness"/>, or
        /// <paramref name="segments"/> is non-positive.
        /// </summary>
        public void DrawRadialProgress(SpriteBatch batch, Vector2 center, float radius, float thickness,
                                       float fraction, Color color, int segments = 64,
                                       float startAngleRadians = -MathF.PI / 2f, bool clockwise = true)
        {
            DrawArc(batch, center, radius, thickness, startAngleRadians,
                    RadialProgressSweep(fraction, clockwise), color, segments);
        }

        /// <summary>Upper bound on the number of horizontal bands <see cref="DrawFilledCircle"/> draws,
        /// regardless of radius (mirrors the segment-count clamps on <see cref="RingSegments"/> /
        /// <see cref="SectorSegments"/>, which cap the same unbounded-with-radius growth for outlines).</summary>
        public const int MaxFilledCircleRows = 128;

        /// <summary>
        /// The Y step (pixels) between <see cref="DrawFilledCircle"/>'s horizontal bands: 1 (one row per pixel
        /// of diameter, today's behaviour) while the diameter stays at or under <see cref="MaxFilledCircleRows"/>,
        /// otherwise the smallest step that brings the band count under the cap - so a very large radius
        /// rasterizes as a bounded number of proportionally taller bands instead of one draw call per pixel row.
        /// Pure. Extracted for headless tests.
        /// </summary>
        public static int FilledCircleRowStep(float radius)
        {
            int diameterRows = 2 * (int)radius + 1;
            return diameterRows <= MaxFilledCircleRows ? 1 : (diameterRows + MaxFilledCircleRows - 1) / MaxFilledCircleRows;
        }

        /// <summary>Draws a filled circle as stacked horizontal rects, one row per pixel of diameter up to
        /// <see cref="MaxFilledCircleRows"/> bands (see <see cref="FilledCircleRowStep"/>).</summary>
        public void DrawFilledCircle(SpriteBatch batch, Vector2 center, float radius, Color color)
        {
            int intRadius = (int)radius;
            int step = FilledCircleRowStep(radius);
            for (int y = -intRadius; y <= intRadius; y += step)
            {
                int halfWidth = (int)MathF.Sqrt(radius * radius - y * y);
                DrawFilledRect(batch, new Rect(center.X - halfWidth, center.Y + y, halfWidth * 2, step), color);
            }
        }

        /// <summary>
        /// Draws a vertical gradient as a single quad: <paramref name="top"/> on the upper edge,
        /// <paramref name="bottom"/> on the lower edge, interpolated per-pixel by the GPU (rasterizer vertex
        /// -color interpolation) instead of the earlier CPU-side banded approximation. <paramref name="bands"/>
        /// is unused - kept only for source/binary compatibility with existing call sites - since a single
        /// GPU-interpolated quad has no band count of its own and is smoother than any finite band count.
        /// </summary>
        public void DrawVerticalGradient(SpriteBatch batch, Rect r, Color top, Color bottom, int bands = 12) =>
            batch.Draw(_white, new Vector4(r.X, r.Y, r.Width, r.Height), top, bottom);

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

        /// <summary>
        /// Segment count for a sector/arc spanning <paramref name="sweep"/> radians at <paramref name="radius"/>:
        /// proportional to arc length, floored at 2 and clamped to 96 so a thin sweep stays cheap and a wide one
        /// stays smooth. Pure; extracted for headless tests.
        /// </summary>
        public static int SectorSegments(float radius, float sweep) =>
            Math.Clamp((int)MathF.Ceiling(MathF.Abs(sweep) * MathF.Max(radius, 1f) * 0.25f), 2, 96);

        /// <summary>
        /// The rim point of a sector at normalized angle <paramref name="t"/> in [0,1] across the sweep:
        /// angle = <paramref name="dirAngle"/> - <paramref name="halfAngle"/> + t * (2 * halfAngle), at
        /// <paramref name="radius"/> from <paramref name="center"/>. Pure; extracted for headless tests.
        /// </summary>
        public static Vector2 SectorRimPoint(Vector2 center, float dirAngle, float halfAngle, float radius, float t)
        {
            float a = dirAngle - halfAngle + t * (2f * halfAngle);
            return center + new Vector2(MathF.Cos(a) * radius, MathF.Sin(a) * radius);
        }

        /// <summary>
        /// Draws a filled sector (pie wedge) centered at <paramref name="center"/>, facing
        /// <paramref name="dirAngle"/> radians, spanning +/- <paramref name="halfAngle"/>, out to
        /// <paramref name="radius"/>. Built as a fan of exact triangles through degenerate
        /// <c>SpriteBatch.DrawQuad</c> calls. No-op when radius or sweep is non-positive.
        /// </summary>
        public void DrawFilledSector(SpriteBatch batch, Vector2 center, float dirAngle, float halfAngle, float radius, Color color)
        {
            if (radius <= 0f || halfAngle <= 0f) return;
            int segs = SectorSegments(radius, 2f * halfAngle);
            Vector2 prev = SectorRimPoint(center, dirAngle, halfAngle, radius, 0f);
            for (int i = 1; i <= segs; i++)
            {
                Vector2 cur = SectorRimPoint(center, dirAngle, halfAngle, radius, i / (float)segs);
                FillTriangle(batch, center, prev, cur, color);
                prev = cur;
            }
        }

        /// <summary>
        /// Draws a filled arc band (annulus slice) between <paramref name="innerR"/> and <paramref name="outerR"/>,
        /// from <paramref name="startAngle"/> spanning <paramref name="sweep"/> radians, around
        /// <paramref name="center"/>. For a full ring pass sweep = MathF.Tau. No-op for non-positive sizes.
        /// </summary>
        public void DrawFilledArcBand(SpriteBatch batch, Vector2 center, float innerR, float outerR, float startAngle, float sweep, Color color)
        {
            if (outerR <= 0f || sweep == 0f) return;
            innerR = MathF.Max(0f, innerR);
            int segs = SectorSegments(outerR, sweep);
            float step = sweep / segs;
            void Pt(float a, float r, out Vector2 p) => p = center + new Vector2(MathF.Cos(a) * r, MathF.Sin(a) * r);
            Pt(startAngle, innerR, out var pi0);
            Pt(startAngle, outerR, out var po0);
            for (int i = 1; i <= segs; i++)
            {
                float a = startAngle + i * step;
                Pt(a, innerR, out var pi1);
                Pt(a, outerR, out var po1);
                FillTriangle(batch, pi0, po0, po1, color);
                FillTriangle(batch, pi0, po1, pi1, color);
                pi0 = pi1; po0 = po1;
            }
        }

        /// <summary>
        /// Draws a filled arc band with <paramref name="innerColor"/> on its inner edge and
        /// <paramref name="outerColor"/> on its outer edge. The GPU interpolates the colors across one convex
        /// quad per arc segment. Adjacent segments share the same edge vertices and colors, so translucent bands
        /// have no overlapping fan triangles or seams. Geometry, segment count, and no-op conditions match
        /// <see cref="DrawFilledArcBand"/>.
        /// </summary>
        public void DrawFilledArcBandGradient(
            SpriteBatch batch,
            Vector2 center,
            float innerR,
            float outerR,
            float startAngle,
            float sweep,
            Color innerColor,
            Color outerColor)
        {
            if (outerR <= 0f || sweep == 0f) return;
            innerR = MathF.Max(0f, innerR);
            int segs = SectorSegments(outerR, sweep);
            float step = sweep / segs;
            void Pt(float a, float r, out Vector2 p) =>
                p = center + new Vector2(MathF.Cos(a) * r, MathF.Sin(a) * r);
            Pt(startAngle, innerR, out Vector2 inner0);
            Pt(startAngle, outerR, out Vector2 outer0);
            for (int i = 1; i <= segs; i++)
            {
                float angle = startAngle + i * step;
                Pt(angle, innerR, out Vector2 inner1);
                Pt(angle, outerR, out Vector2 outer1);
                batch.DrawQuad(
                    _white,
                    outer0,
                    outer1,
                    inner1,
                    inner0,
                    FullUV,
                    outerColor,
                    innerColor);
                inner0 = inner1;
                outer0 = outer1;
            }
        }

        // Polygons up to this many vertices keep their offset rings on the stack. Larger ones rent the rings from the
        // shared array pool and return them before the call ends, so neither path allocates per call.
        const int PolygonStackVertices = 64;

        /// <summary>
        /// Fills a convex polygon given in either winding as a triangle fan from its first vertex, one
        /// coincident-corner <see cref="SpriteBatch.DrawQuad(Texture2D, Vector2, Vector2, Vector2, Vector2, Vector4, Color)"/>
        /// per triangle, so no pixel is covered twice and a translucent <paramref name="color"/> composites evenly.
        /// <paramref name="feather"/> above 0 anti-aliases the edge with a strip that wide outside it (the polygon
        /// offset outward by <see cref="ConvexPolygon.Offset"/>): one gradient quad per edge fading from
        /// <paramref name="color"/> to the same RGB at alpha 0. The batch blends straight, non-premultiplied alpha
        /// (source alpha over inverse source alpha), so fading alpha alone leaves no fringe, where fading to
        /// transparent black would drag the rim's colour toward black. No-op for fewer than 3 points, zero area,
        /// or any non-finite point. Allocation-free (offset rings up to 64 vertices live on the stack, larger ones
        /// come from the shared array pool).
        /// </summary>
        public void FillConvexPolygon(SpriteBatch batch, ReadOnlySpan<Vector2> points, Color color, float feather = 0f)
        {
            if (!ConvexPolygon.IsDrawable(points)) return;
            FanFill(batch, points, color);
            if (!IsFeathered(feather)) return;

            int n = points.Length;
            Vector2[]? rented = null;
            Span<Vector2> ring = n <= PolygonStackVertices
                ? stackalloc Vector2[PolygonStackVertices]
                : (rented = ArrayPool<Vector2>.Shared.Rent(n));
            try
            {
                ring = ring[..n];
                ConvexPolygon.Offset(points, feather, ring);
                FeatherStrip(batch, points, ring, color);
            }
            finally
            {
                if (rented is not null) ArrayPool<Vector2>.Shared.Return(rented);
            }
        }

        /// <summary>
        /// Strokes the outline of a convex polygon given in either winding as exactly ONE quad per edge between an
        /// outer and an inner mitred ring (<see cref="ConvexPolygon.Offset"/>). Neighbouring quads share the two ring
        /// vertices on each corner's bisector, so joins never overlap and a translucent <paramref name="color"/>
        /// composites evenly all the way round, which a line per edge cannot do. <paramref name="alignment"/> places
        /// the rings: <see cref="StrokeAlignment.Inside"/> at 0 and <c>-thickness</c>,
        /// <see cref="StrokeAlignment.Center"/> at <c>+/- thickness / 2</c>, <see cref="StrokeAlignment.Outside"/>
        /// at <c>+thickness</c> and 0. <paramref name="miterLimit"/> clamps each corner's miter to that multiple of
        /// its ring distance.
        /// <para>
        /// When the inner ring's inward distance would reach or pass <see cref="ConvexPolygon.InsetLimit"/>, the
        /// polygon is smaller than its own line weight and the stroke COLLAPSES to a fill of its outer ring, which
        /// reads as a solid shape instead of an inverted bow tie. <paramref name="feather"/> above 0 adds the same
        /// straight-alpha fade strip as <see cref="FillConvexPolygon"/> outside the outer ring and inside the inner
        /// ring (outside only when collapsed), with the inner strip stopping at the inset limit. No-op for fewer
        /// than 3 points, zero area, any non-finite point, or a <paramref name="thickness"/> that is not a positive
        /// finite number. Allocation-free, as for the fill.
        /// </para>
        /// </summary>
        public void StrokeConvexPolygon(SpriteBatch batch, ReadOnlySpan<Vector2> points, float thickness, Color color,
            StrokeAlignment alignment = StrokeAlignment.Center, float feather = 0f, float miterLimit = 4f)
        {
            if (!(thickness > 0f) || !float.IsFinite(thickness) || !ConvexPolygon.IsDrawable(points)) return;
            (float outerDistance, float innerDistance) = ConvexPolygon.StrokeRingDistances(thickness, alignment);
            float insetLimit = ConvexPolygon.InsetLimit(points);
            bool feathered = IsFeathered(feather);

            int n = points.Length;
            Vector2[]? rented = null;
            Span<Vector2> rings = n <= PolygonStackVertices
                ? stackalloc Vector2[PolygonStackVertices * 4]
                : (rented = ArrayPool<Vector2>.Shared.Rent(n * 4));
            try
            {
                Span<Vector2> outer = rings.Slice(0, n);
                Span<Vector2> inner = rings.Slice(n, n);
                Span<Vector2> outerFade = rings.Slice(2 * n, n);
                Span<Vector2> innerFade = rings.Slice(3 * n, n);

                ConvexPolygon.Offset(points, outerDistance, outer, miterLimit);
                if (feathered) ConvexPolygon.Offset(points, outerDistance + feather, outerFade, miterLimit);

                if (innerDistance < 0f && -innerDistance >= insetLimit)
                {
                    FanFill(batch, outer, color);
                    if (feathered) FeatherStrip(batch, outer, outerFade, color);
                    return;
                }

                ConvexPolygon.Offset(points, innerDistance, inner, miterLimit);
                for (int i = 0; i < n; i++)
                {
                    int next = i + 1 == n ? 0 : i + 1;
                    batch.DrawQuad(_white, outer[i], outer[next], inner[next], inner[i], FullUV, color);
                }
                if (!feathered) return;

                FeatherStrip(batch, outer, outerFade, color);
                ConvexPolygon.Offset(points, MathF.Max(innerDistance - feather, -insetLimit), innerFade, miterLimit);
                FeatherStrip(batch, inner, innerFade, color);
            }
            finally
            {
                if (rented is not null) ArrayPool<Vector2>.Shared.Return(rented);
            }
        }

        static bool IsFeathered(float feather) => feather > 0f && float.IsFinite(feather);

        // A triangle fan from the first vertex. Each triangle is one exact coincident-corner quad (see FillTriangle).
        void FanFill(SpriteBatch batch, ReadOnlySpan<Vector2> points, Color color)
        {
            Vector2 apex = points[0];
            for (int i = 1; i + 1 < points.Length; i++)
                FillTriangle(batch, apex, points[i], points[i + 1], color);
        }

        // One gradient quad per edge between an exposed edge ring and its fade ring: `color` along the edge and the
        // same RGB at alpha 0 along the fade ring (straight alpha, see FillConvexPolygon).
        void FeatherStrip(SpriteBatch batch, ReadOnlySpan<Vector2> edge, ReadOnlySpan<Vector2> fade, Color color)
        {
            Color clear = color.WithAlpha(0f);
            int n = edge.Length;
            for (int i = 0; i < n; i++)
            {
                int next = i + 1 == n ? 0 : i + 1;
                batch.DrawQuad(_white, edge[i], edge[next], fade[next], fade[i], FullUV, color, clear);
            }
        }

        // SpriteBatch emits (tl, tr, br) and (tl, br, bl). Repeating the first point as bl leaves one exact
        // triangle plus one zero-area triangle, with no rectangular spill or overlap outside the authored shape.
        void FillTriangle(SpriteBatch batch, Vector2 a, Vector2 b, Vector2 c, Color color) =>
            batch.DrawQuad(_white, a, b, c, a, FullUV, color);

        /// <summary>Disposes the owned 1x1 white pixel (no-op when constructed over a caller-supplied texture).</summary>
        public void Dispose()
        {
            if (_ownsWhite) _white.Dispose();
        }
    }
}
