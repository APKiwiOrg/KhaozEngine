using System;
using System.Numerics;

namespace KhaozEngine.Terrain
{
    /// <summary>Raises a gaussian wall along an infinite line (perpendicular falloff = <c>width</c>), pierced by a
    /// pass: within <c>passWidth</c> of <c>passAlong</c> (signed distance along the line from <c>point</c>) the
    /// wall is gated to ~0 so the ridge reads as mountains with a gap, not a continuous berm.</summary>
    public sealed class RidgeFeature : ITerrainFeature
    {
        readonly Vector2 _point, _dir;  // _dir normalized
        readonly float _height, _width, _passAlong, _passWidth;

        public RidgeFeature(Vector2 point, Vector2 direction, float height, float width, float passAlong, float passWidth)
        {
            _point = point;
            _dir = direction.LengthSquared() > 1e-12f ? Vector2.Normalize(direction) : new Vector2(1f, 0f);
            _height = height; _width = MathF.Max(1e-3f, width); _passAlong = passAlong; _passWidth = MathF.Max(1e-3f, passWidth);
        }

        public float Apply(float x, float z, float h)
        {
            Vector2 rel = new Vector2(x, z) - _point;
            float along = Vector2.Dot(rel, _dir);
            Vector2 perpVec = rel - _dir * along;
            float perp = perpVec.Length();
            float wall = _height * MathF.Exp(-(perp * perp) / (2f * _width * _width));
            // pass gate: 0 at the pass centre, 1 by passWidth away.
            float gate = TerrainNoise.SmoothStep(_passWidth * 0.5f, _passWidth, MathF.Abs(along - _passAlong));
            return h + wall * gate;
        }
    }
}
