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
    readonly GroundCoverInstance[]? _items;
    Vector4[] _bounds;
    readonly int[]? _modelIndices;
    readonly string[]? _modelIds;
    IGroundCoverInstanceSource? _source;
    readonly int _count;
    bool _released;

    public GroundCoverBatch(IReadOnlyList<GroundCoverInstance> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _count = source.Count;
        _items = new GroundCoverInstance[source.Count];
        _bounds = new Vector4[(source.Count + RangeSize - 1) / RangeSize];
        _modelIndices = new int[source.Count];
        var modelIndices = new Dictionary<string, int>(StringComparer.Ordinal);
        var modelIds = new List<string>();
        for (int range = 0; range < _bounds.Length; range++)
        {
            var bounds = new Vector4(float.PositiveInfinity, float.PositiveInfinity,
                float.NegativeInfinity, float.NegativeInfinity);
            int end = Math.Min(source.Count, (range + 1) * RangeSize);
            for (int i = range * RangeSize; i < end; i++)
            {
                GroundCoverInstance item = source[i];
                _items[i] = item;
                int modelIndex = -1;
                if (item.ModelId is not null && !modelIndices.TryGetValue(item.ModelId, out modelIndex))
                {
                    modelIndex = modelIds.Count;
                    modelIndices.Add(item.ModelId, modelIndex);
                    modelIds.Add(item.ModelId);
                }
                _modelIndices[i] = modelIndex;
                bounds.X = MathF.Min(bounds.X, item.Position.X);
                bounds.Y = MathF.Min(bounds.Y, item.Position.Z);
                bounds.Z = MathF.Max(bounds.Z, item.Position.X);
                bounds.W = MathF.Max(bounds.W, item.Position.Z);
            }
            _bounds[range] = bounds;
        }
        _modelIds = modelIds.ToArray();
    }

    internal GroundCoverBatch(IGroundCoverInstanceSource source)
    {
        _source = source; _count = source.Count;
        _bounds = new Vector4[_count / RangeSize + (_count % RangeSize == 0 ? 0 : 1)];
        for (int range = 0; range < _bounds.Length; range++)
        {
            var bounds = new Vector4(float.PositiveInfinity, float.PositiveInfinity, float.NegativeInfinity, float.NegativeInfinity);
            int end = Math.Min(_count, checked((range + 1) * RangeSize));
            for (int i = range * RangeSize; i < end; i++)
            {
                var point = source.Columns[i].Position;
                bounds.X = MathF.Min(bounds.X, point.X); bounds.Y = MathF.Min(bounds.Y, point.Z);
                bounds.Z = MathF.Max(bounds.Z, point.X); bounds.W = MathF.Max(bounds.W, point.Z);
            }
            _bounds[range] = bounds;
        }
    }
    internal long RetainedViewBytes => checked(64L + 24L + 16L * _bounds.Length);
    internal void ReleaseResident()
    { if (_source is null) return; _source = null; _bounds = []; _released = true; }

    public int Count => _count;
    public GroundCoverInstance this[int index]
    {
        get { ObjectDisposedException.ThrowIf(_released, this); return _source is { } source ? source.Columns[index] : _items![index]; }
    }
    public IEnumerator<GroundCoverInstance> GetEnumerator() { for (int i = 0; i < Count; i++) yield return this[i]; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal int ModelCount => _source?.Columns.ModelCount ?? _modelIds!.Length;
    internal int ModelIndex(int instanceIndex) => _source is { } source ? source.Columns.ModelIndex(instanceIndex) : _modelIndices![instanceIndex];
    internal string ModelId(int modelIndex) => _source is { } source ? source.Columns.ModelId(modelIndex) : _modelIds![modelIndex];

    internal int SkipOutside(int index, Vector3 focus, float radiusSquared)
    {
        if (index % RangeSize != 0) return index;
        while (index < _count)
        {
            Vector4 bounds = _bounds[index / RangeSize];
            float dx = MathF.Max(0f, MathF.Max(bounds.X - focus.X, focus.X - bounds.Z));
            float dz = MathF.Max(0f, MathF.Max(bounds.Y - focus.Z, focus.Z - bounds.W));
            if (dx * dx + dz * dz <= radiusSquared) break;
            index += RangeSize;
        }
        return Math.Min(index, _count);
    }
}
