using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;

namespace KhaozEngine.Telegraphs
{
    /// <summary>
    /// Immediate-mode 2D telegraph renderer. Call <see cref="Begin"/> with an already-<c>Begin</c>-ed
    /// <see cref="SpriteBatch"/> and a <see cref="PrimitiveRenderer"/> (both owned by the caller), issue shape
    /// draws (fed from the game's sim each frame), then <see cref="End"/>. This renderer owns neither; it holds
    /// no per-frame state and is safe to feed from a deterministic sim.
    /// </summary>
    public sealed class TelegraphRenderer2D
    {
        /// <summary>Most dots one <see cref="DotLane"/> call draws. A lane that needs more draws its first
        /// <see cref="MaxDotLaneDots"/> from the origin and stops.</summary>
        public const int MaxDotLaneDots = 4096;

        SpriteBatch? _batch;
        PrimitiveRenderer? _prim;

        /// <summary>Begin a telegraph pass over an active <paramref name="batch"/> and a
        /// <paramref name="primitives"/> renderer (both owned by the caller).</summary>
        public void Begin(SpriteBatch batch, PrimitiveRenderer primitives)
        {
            _batch = batch ?? throw new ArgumentNullException(nameof(batch));
            _prim = primitives ?? throw new ArgumentNullException(nameof(primitives));
        }

        public void End()
        {
            if (_batch is null) throw new InvalidOperationException("TelegraphRenderer2D.End called before Begin.");
            _batch = null;
            _prim = null;
        }

        (SpriteBatch b, PrimitiveRenderer p) Active()
        {
            if (_batch is null || _prim is null)
                throw new InvalidOperationException("Call TelegraphRenderer2D.Begin before drawing.");
            return (_batch, _prim);
        }

        static BlendMode ToBlend(TelegraphBlend b) => b == TelegraphBlend.Additive ? BlendMode.Additive : BlendMode.Alpha;

        // Brighten a color toward white by the flash amount (additive impact pop).
        static Color WithFlash(Color c, float flash) =>
            flash <= 0f ? c : new Color(
                MathUtil.Clamp01(c.R + flash), MathUtil.Clamp01(c.G + flash), MathUtil.Clamp01(c.B + flash), c.A);

        public void Circle(Vector2 center, float radius, float progress, in TelegraphStyle style)
        {
            var (b, p) = Active();
            var r = TelegraphResolve.Resolve(progress, style);
            b.BlendMode = ToBlend(r.Blend);
            if (r.FillMode != FillMode.Outline)
                p.DrawFilledCircle(b, center, radius * r.FillFraction, WithFlash(r.FillColor, r.FlashAdd));
            if (r.FillMode != FillMode.Fill)
                p.DrawRing(b, center, radius, r.EdgeThickness, r.OutlineColor);
        }

        public void Ring(Vector2 center, float inner, float outer, float progress, in TelegraphStyle style)
        {
            var (b, p) = Active();
            var r = TelegraphResolve.Resolve(progress, style);
            b.BlendMode = ToBlend(r.Blend);
            if (r.FillMode != FillMode.Outline)
            {
                // Sweep grows the band outward from the inner edge.
                float bandOuter = inner + (outer - inner) * r.FillFraction;
                p.DrawFilledArcBand(b, center, inner, bandOuter, 0f, MathF.Tau, WithFlash(r.FillColor, r.FlashAdd));
            }
            if (r.FillMode != FillMode.Fill)
            {
                p.DrawRing(b, center, inner, r.EdgeThickness, r.OutlineColor);
                p.DrawRing(b, center, outer, r.EdgeThickness, r.OutlineColor);
            }
        }

        public void Beam(Vector2 origin, Vector2 direction, float length, float width, float progress, in TelegraphStyle style)
        {
            var (b, p) = Active();
            var r = TelegraphResolve.Resolve(progress, style);
            b.BlendMode = ToBlend(r.Blend);
            Vector2 dir = direction.LengthSquared() > 1e-6f ? Vector2.Normalize(direction) : Vector2.UnitX;
            if (r.FillMode != FillMode.Outline)
            {
                Vector2 end = origin + dir * (length * r.FillFraction);
                p.DrawLine(b, origin, end, WithFlash(r.FillColor, r.FlashAdd), width);
            }
            if (r.FillMode != FillMode.Fill)
            {
                // Outline = the two long edges of the rect.
                Vector2 n = new(-dir.Y, dir.X);
                Vector2 end = origin + dir * length;
                p.DrawLine(b, origin + n * (width * 0.5f), end + n * (width * 0.5f), r.OutlineColor, r.EdgeThickness);
                p.DrawLine(b, origin - n * (width * 0.5f), end - n * (width * 0.5f), r.OutlineColor, r.EdgeThickness);
            }
        }

        public void Cone(Vector2 origin, Vector2 direction, float halfAngleRad, float range, float progress, in TelegraphStyle style)
        {
            var (b, p) = Active();
            var r = TelegraphResolve.Resolve(progress, style);
            b.BlendMode = ToBlend(r.Blend);
            float dirAngle = MathF.Atan2(direction.Y, direction.X);
            if (r.FillMode != FillMode.Outline)
                p.DrawFilledSector(b, origin, dirAngle, halfAngleRad, range * r.FillFraction, WithFlash(r.FillColor, r.FlashAdd));
            if (r.FillMode != FillMode.Fill)
            {
                Vector2 a = PrimitiveRenderer.SectorRimPoint(origin, dirAngle, halfAngleRad, range, 0f);
                Vector2 c = PrimitiveRenderer.SectorRimPoint(origin, dirAngle, halfAngleRad, range, 1f);
                p.DrawLine(b, origin, a, r.OutlineColor, r.EdgeThickness);
                p.DrawLine(b, origin, c, r.OutlineColor, r.EdgeThickness);
                p.DrawArc(b, origin, range, r.EdgeThickness, dirAngle - halfAngleRad, halfAngleRad * 2f, r.OutlineColor);
            }
        }

        public void Arc(Vector2 center, float radius, float bandWidth, float startAngle, float sweepAngle, float progress, in TelegraphStyle style)
        {
            var (b, p) = Active();
            var r = TelegraphResolve.Resolve(progress, style);
            b.BlendMode = ToBlend(r.Blend);
            float inner = MathF.Max(0f, radius - bandWidth * 0.5f);
            float outer = radius + bandWidth * 0.5f;
            if (r.FillMode != FillMode.Outline)
                p.DrawFilledArcBand(b, center, inner, outer, startAngle, sweepAngle * r.FillFraction, WithFlash(r.FillColor, r.FlashAdd));
            if (r.FillMode != FillMode.Fill)
            {
                p.DrawArc(b, center, inner, r.EdgeThickness, startAngle, sweepAngle, r.OutlineColor);
                p.DrawArc(b, center, outer, r.EdgeThickness, startAngle, sweepAngle, r.OutlineColor);
                if (MathF.Abs(sweepAngle) < MathF.Tau - 0.01f)
                {
                    // Radial end caps close the band on a partial sweep. A full ring needs none.
                    float endAngle = startAngle + sweepAngle;
                    Vector2 startDir = new(MathF.Cos(startAngle), MathF.Sin(startAngle));
                    Vector2 endDir = new(MathF.Cos(endAngle), MathF.Sin(endAngle));
                    p.DrawLine(b, center + startDir * inner, center + startDir * outer, r.OutlineColor, r.EdgeThickness);
                    p.DrawLine(b, center + endDir * inner, center + endDir * outer, r.OutlineColor, r.EdgeThickness);
                }
            }
        }

        /// <summary>
        /// A row of dots every <paramref name="spacing"/> from <paramref name="origin"/> along
        /// <paramref name="direction"/> out to <paramref name="length"/>. Dots appear outward as the
        /// <paramref name="reveal"/> front moves from 0 to 1 and disappear outward, origin end first, as the
        /// <paramref name="fade"/> front does. Each dot eases in and out over two spacings, per
        /// <see cref="TelegraphResolve.DotLaneDotAlpha"/>. Colour resolves from <paramref name="reveal"/> like the
        /// progress of the other shapes, and the impact flash is scaled by <c>1 - fade</c> so the dots pop at the
        /// strike and cool as they wash out. <see cref="ResolvedTelegraph.FillFraction"/> is not used because the
        /// reveal front replaces the sweep. <see cref="FillMode.Fill"/> draws filled dots in the fill colour,
        /// <see cref="FillMode.Outline"/> draws rings at the edge thickness in the outline colour, and
        /// <see cref="FillMode.OutlineAndFill"/> draws both. A dot whose alpha is zero is not drawn. At most
        /// <see cref="MaxDotLaneDots"/> dots are drawn, the ones nearest the origin.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="spacing"/> is not finite and positive.
        /// </exception>
        /// <exception cref="InvalidOperationException">Called before <see cref="Begin"/>.</exception>
        /// <remarks>A non-positive or non-finite <paramref name="length"/> draws nothing.</remarks>
        public void DotLane(Vector2 origin, Vector2 direction, float length, float spacing, float dotRadius,
            float reveal, float fade, in TelegraphStyle style)
        {
            if (!float.IsFinite(spacing) || spacing <= 0f)
                throw new ArgumentOutOfRangeException(nameof(spacing), spacing, "Spacing must be finite and positive.");
            var (b, p) = Active();
            if (!float.IsFinite(length) || length <= 0f) return;

            var r = TelegraphResolve.Resolve(reveal, style);
            b.BlendMode = ToBlend(r.Blend);
            Vector2 dir = direction.LengthSquared() > 1e-6f ? Vector2.Normalize(direction) : Vector2.UnitX;
            float flash = r.FlashAdd * (1f - MathUtil.Clamp01(fade));
            Color fill = WithFlash(r.FillColor, flash);
            float ramp = MathF.Min(1f, 2f * spacing / length);

            int last = DotLaneLastIndex(length, spacing);
            for (int i = 0; i <= last; i++)
            {
                float along = i * spacing;
                float alpha = TelegraphResolve.DotLaneDotAlpha(along / length, reveal, fade, ramp);
                if (alpha <= 0f) continue;
                Vector2 center = origin + dir * along;
                if (r.FillMode != FillMode.Outline)
                    p.DrawFilledCircle(b, center, dotRadius, fill.WithAlpha(fill.A * alpha));
                if (r.FillMode != FillMode.Fill)
                {
                    Color ring = r.OutlineColor.WithAlpha(r.OutlineColor.A * alpha);
                    p.DrawRing(b, center, dotRadius, r.EdgeThickness, ring);
                }
            }
        }

        // Index of the last dot, clamped in float before the int conversion. A float to int conversion
        // saturates, so an unclamped ratio of 2^31 or more would give int.MaxValue and the loop would never end.
        internal static int DotLaneLastIndex(float length, float spacing) =>
            (int)MathF.Min(MathF.Floor(length / spacing), MaxDotLaneDots - 1);
    }
}
