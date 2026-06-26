using System;

namespace KhaozEngine.Terrain
{
    /// <summary>Carves a basin toward the water by subtracting up to <c>depth</c> at the centre, smoothstep-faded
    /// to zero by <c>radius*outerFraction</c> (the greybox clearing's lake trick).</summary>
    public sealed class LakeFeature : ITerrainFeature
    {
        readonly float _cx, _cz, _radius, _depth, _inner, _outer;

        public LakeFeature(float centerX, float centerZ, float radius, float depth, float innerFraction = 0.45f, float outerFraction = 1.30f)
        {
            _cx = centerX; _cz = centerZ; _radius = radius; _depth = depth; _inner = innerFraction; _outer = outerFraction;
        }

        public float Apply(float x, float z, float h)
        {
            float d = MathF.Sqrt((x - _cx) * (x - _cx) + (z - _cz) * (z - _cz));
            float carve = 1f - TerrainNoise.SmoothStep(_radius * _inner, _radius * _outer, d);
            return h - _depth * carve;
        }
    }
}
