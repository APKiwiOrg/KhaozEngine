using System;
using System.Numerics;

namespace KhaozEngine.Terrain
{
    /// <summary>A gap cut through the rim wall along a heading (the road out): the wall is lowered to ~0 within
    /// <see cref="HalfWidth"/> of the ray from the rim centre at <see cref="AngleRadians"/>, ramping back to the
    /// full wall by <see cref="HalfWidth"/> + <see cref="Falloff"/> (perpendicular distance). The pass only opens
    /// the wall on the heading side, not the opposite wall.</summary>
    public readonly struct RimPass
    {
        public RimPass(float angleRadians, float halfWidth, float falloff)
        {
            AngleRadians = angleRadians;
            HalfWidth = MathF.Max(0f, halfWidth);
            Falloff = MathF.Max(1e-3f, falloff);
        }

        /// <summary>Heading of the corridor from the rim centre (radians; dir = (cos, sin) in (x, z)).</summary>
        public readonly float AngleRadians;
        /// <summary>Half the open corridor width (world units, perpendicular to the heading).</summary>
        public readonly float HalfWidth;
        /// <summary>Perpendicular distance over which the wall ramps from open back to full.</summary>
        public readonly float Falloff;
    }

    /// <summary>
    /// Raises terrain into an enclosing wall around a bounded region: unchanged inside <c>InnerRadius</c>, a
    /// smoothstep ramp up to <c>WallHeight</c> by <c>OuterRadius</c> and held at <c>WallHeight</c> beyond (a
    /// plateau, so you cannot see/walk past it), modulated by a coordinate-hash jagged crest (<c>Ruggedness</c>,
    /// reusing <see cref="TerrainNoise"/>) so it reads as mountains not a smooth berm. <c>Passes</c> cut corridors
    /// through the wall so a road can leave. The visual/diegetic border; the authoritative hard stop is
    /// <c>KhaozEngine.NetWorld.WorldBounds</c>, and the rim is kept un-climbable by the movement slope gate
    /// (pass <see cref="TerrainCollision.GroundNormal"/> as the <c>groundNormal</c> delegate).
    /// MVP is circular: <see cref="Apply"/> is shaped around a "distance to the play-area boundary" (here the
    /// distance from <c>Center</c>) so a rect/polygon variant can swap the distance metric and reuse the ramp.
    /// Pure in (x, z) like every <see cref="ITerrainFeature"/>.
    /// </summary>
    public sealed class RimFeature : ITerrainFeature
    {
        readonly Vector2 _center;
        readonly float _inner, _outer, _wallHeight, _ruggedness, _crestFreq;
        readonly int _seed;
        readonly RimPass[] _passes;

        public RimFeature(Vector2 center, float innerRadius, float outerRadius, float wallHeight,
            float ruggedness = 0.25f, RimPass[]? passes = null, int seed = 1, float crestFrequency = 0.05f)
        {
            _center = center;
            _inner = innerRadius;
            _outer = MathF.Max(innerRadius + 1e-3f, outerRadius);
            _wallHeight = wallHeight;
            _ruggedness = Math.Clamp(ruggedness, 0f, 1f);
            _seed = seed;
            _crestFreq = crestFrequency;
            _passes = passes ?? Array.Empty<RimPass>();
        }

        public float Apply(float x, float z, float h)
        {
            // "distance to the play-area boundary": circular MVP = distance from the centre. A rect/polygon
            // variant replaces only this metric and the inner/outer interpretation; the ramp below is shared.
            float dx = x - _center.X, dz = z - _center.Y;
            float d = MathF.Sqrt(dx * dx + dz * dz);

            float t = TerrainNoise.SmoothStep(_inner, _outer, d);   // 0 inside inner, 1 by outer (and beyond)
            if (t <= 0f) return h;                                  // unchanged inside the play area

            // Jagged crest: symmetric coordinate-hash noise scales the wall height within +/- ruggedness.
            float crest = 1f;
            if (_ruggedness > 0f)
                crest = 1f + _ruggedness * TerrainNoise.Fbm(x * _crestFreq, z * _crestFreq, _seed);

            float gate = PassGate(dx, dz);                          // 1 = full wall, 0 = fully open at a pass
            return h + _wallHeight * t * crest * gate;
        }

        // 1 away from every pass; drops to 0 along an open corridor (perpendicular distance), heading-side only.
        float PassGate(float dx, float dz)
        {
            float gate = 1f;
            for (int i = 0; i < _passes.Length; i++)
            {
                RimPass p = _passes[i];
                Vector2 dir = new(MathF.Cos(p.AngleRadians), MathF.Sin(p.AngleRadians));
                Vector2 rel = new(dx, dz);
                float along = Vector2.Dot(rel, dir);
                if (along <= 0f) continue;                          // pass opens outward along its heading only
                float perp = (rel - dir * along).Length();
                float g = TerrainNoise.SmoothStep(p.HalfWidth, p.HalfWidth + p.Falloff, perp);
                if (g < gate) gate = g;                             // most-open pass wins
            }
            return gate;
        }
    }
}
