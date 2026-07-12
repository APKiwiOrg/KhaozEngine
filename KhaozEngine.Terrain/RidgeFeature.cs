using System;
using System.Numerics;

namespace KhaozEngine.Terrain
{
    /// <summary>Raises a gaussian wall along an infinite line (perpendicular falloff = <c>width</c>), optionally
    /// pierced by a pass: within <c>passWidth</c> of <c>passAlong</c> (signed distance along the line from
    /// <c>point</c>) the wall is gated to ~0 so the ridge reads as mountains with a gap, not a continuous berm.
    /// The pass is opt-in: <c>passWidth &lt;= 0</c> is the no-pass sentinel (the gate is 1 everywhere, a solid
    /// wall), so a bare ridge does not carve a dip at an arbitrary point along its own crest. A positive
    /// <c>passWidth</c>, however small, keeps the original gated behavior (floored at 1e-3 to avoid a
    /// divide-by-effectively-zero smoothstep).</summary>
    public sealed class RidgeFeature : ITerrainFeature
    {
        readonly Vector2 _point, _dir;  // _dir normalized
        readonly float _height, _width, _passAlong, _passWidth;   // _passWidth == 0 means "no pass"

        public RidgeFeature(Vector2 point, Vector2 direction, float height, float width, float passAlong, float passWidth)
        {
            _point = point;
            _dir = direction.LengthSquared() > 1e-12f ? Vector2.Normalize(direction) : new Vector2(1f, 0f);
            _height = height; _width = MathF.Max(1e-3f, width); _passAlong = passAlong;
            _passWidth = passWidth > 0f ? MathF.Max(1e-3f, passWidth) : 0f;
        }

        public float Apply(float x, float z, float h)
        {
            Vector2 rel = new Vector2(x, z) - _point;
            float along = Vector2.Dot(rel, _dir);
            Vector2 perpVec = rel - _dir * along;
            float perp = perpVec.Length();
            float wall = _height * MathF.Exp(-(perp * perp) / (2f * _width * _width));
            // pass gate: 0 at the pass centre, 1 by passWidth away. No pass (passWidth == 0) is always 1.
            float gate = _passWidth > 0f
                ? TerrainNoise.SmoothStep(_passWidth * 0.5f, _passWidth, MathF.Abs(along - _passAlong))
                : 1f;
            return h + wall * gate;
        }
    }
}
