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
    }
}
