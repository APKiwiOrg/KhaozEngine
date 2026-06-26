using System;

namespace KhaozEngine.Terrain
{
    /// <summary>Levels a hub/landmark region toward <c>targetHeight</c>: full inside <c>radius*(1-blend)</c>,
    /// smoothstep-faded to no effect by <c>radius</c>.</summary>
    public sealed class FlattenFeature : ITerrainFeature
    {
        readonly float _cx, _cz, _radius, _target, _blend;

        public FlattenFeature(float centerX, float centerZ, float radius, float targetHeight, float blend = 0.4f)
        {
            _cx = centerX; _cz = centerZ; _radius = radius; _target = targetHeight; _blend = blend;
        }

        public float Apply(float x, float z, float h)
        {
            float d = MathF.Sqrt((x - _cx) * (x - _cx) + (z - _cz) * (z - _cz));
            float t = 1f - TerrainNoise.SmoothStep(_radius * (1f - _blend), _radius, d);
            return h + (_target - h) * t;
        }
    }
}
