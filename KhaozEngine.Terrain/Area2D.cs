using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Terrain
{
    /// <summary>A pure XZ-plane region test, used by scatter exclusions and overrides. Implementations must be
    /// stateless and deterministic: Contains(x, z) depends only on the shape's construction values, so scatter
    /// stays tiling-invariant regardless of which chunks are queried.</summary>
    public interface IArea2D
    {
        bool Contains(float x, float z);
    }

    /// <summary>A disc on the XZ plane (radius inclusive).</summary>
    public sealed class DiscArea2D : IArea2D
    {
        readonly float _cx, _cz, _r2;

        public DiscArea2D(float centerX, float centerZ, float radius)
        {
            _cx = centerX;
            _cz = centerZ;
            _r2 = MathF.Max(0f, radius) * MathF.Max(0f, radius);
        }

        public bool Contains(float x, float z)
        {
            float dx = x - _cx, dz = z - _cz;
            return dx * dx + dz * dz <= _r2;
        }
    }

    /// <summary>An axis-aligned box on the XZ plane (both edges inclusive).</summary>
    public sealed class BoxArea2D : IArea2D
    {
        readonly float _minX, _minZ, _maxX, _maxZ;

        public BoxArea2D(float minX, float minZ, float maxX, float maxZ)
        {
            _minX = minX; _minZ = minZ; _maxX = maxX; _maxZ = maxZ;
        }

        public bool Contains(float x, float z)
            => x >= _minX && x <= _maxX && z >= _minZ && z <= _maxZ;
    }

    /// <summary>A simple polygon on the XZ plane (even-odd rule; Vector2.Y is world Z). Fewer than three
    /// points contains nothing. Edge behaviour is unspecified but deterministic.</summary>
    public sealed class PolygonArea2D : IArea2D
    {
        readonly Vector2[] _pts;

        public PolygonArea2D(IReadOnlyList<Vector2> points)
        {
            if (points == null) throw new ArgumentNullException(nameof(points));
            _pts = new Vector2[points.Count];
            for (int i = 0; i < points.Count; i++) _pts[i] = points[i];
        }

        public bool Contains(float x, float z)
        {
            if (_pts.Length < 3) return false;
            bool inside = false;
            for (int i = 0, j = _pts.Length - 1; i < _pts.Length; j = i++)
            {
                if ((_pts[i].Y > z) != (_pts[j].Y > z) &&
                    x < (_pts[j].X - _pts[i].X) * (z - _pts[i].Y) / (_pts[j].Y - _pts[i].Y) + _pts[i].X)
                    inside = !inside;
            }
            return inside;
        }
    }
}
