using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Terrain;

/// <summary>Immutable cover placements with cached bounds for rejecting distant ranges before visiting
/// individual blades. Preserves source order, transforms and thinning ranks.</summary>
public sealed class GroundCoverBatch : IReadOnlyList<GroundCoverInstance>
{
    const int RangeSize = 128;
    readonly GroundCoverInstance[] _items;
    readonly Vector4[] _bounds;

    public GroundCoverBatch(IReadOnlyList<GroundCoverInstance> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _items = new GroundCoverInstance[source.Count];
        _bounds = new Vector4[(source.Count + RangeSize - 1) / RangeSize];
        for (int range = 0; range < _bounds.Length; range++)
        {
            var bounds = new Vector4(float.PositiveInfinity, float.PositiveInfinity,
                float.NegativeInfinity, float.NegativeInfinity);
            int end = Math.Min(source.Count, (range + 1) * RangeSize);
            for (int i = range * RangeSize; i < end; i++)
            {
                GroundCoverInstance item = source[i];
                _items[i] = item;
                bounds.X = MathF.Min(bounds.X, item.Position.X);
                bounds.Y = MathF.Min(bounds.Y, item.Position.Z);
                bounds.Z = MathF.Max(bounds.Z, item.Position.X);
                bounds.W = MathF.Max(bounds.W, item.Position.Z);
            }
            _bounds[range] = bounds;
        }
    }

    public int Count => _items.Length;
    public GroundCoverInstance this[int index] => _items[index];
    public IEnumerator<GroundCoverInstance> GetEnumerator() => ((IEnumerable<GroundCoverInstance>)_items).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    internal ReadOnlySpan<GroundCoverInstance> Items => _items;

    internal int SkipOutside(int index, Vector3 focus, float radiusSquared)
    {
        if (index % RangeSize != 0) return index;
        while (index < _items.Length)
        {
            Vector4 bounds = _bounds[index / RangeSize];
            float dx = MathF.Max(0f, MathF.Max(bounds.X - focus.X, focus.X - bounds.Z));
            float dz = MathF.Max(0f, MathF.Max(bounds.Y - focus.Z, focus.Z - bounds.W));
            if (dx * dx + dz * dz <= radiusSquared) break;
            index += RangeSize;
        }
        return Math.Min(index, _items.Length);
    }
}
