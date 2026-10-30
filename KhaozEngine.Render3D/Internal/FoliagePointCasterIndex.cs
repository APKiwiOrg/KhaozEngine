using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D.Internal;

internal readonly record struct FoliagePointCasterRef(int SourceOrdinal, uint ProxySlot);

/// <summary>The current frame's selected foliage proxies in source, range and proxy order. Sequential index
/// slots keep that order independent of spatial cells, while references retain the original stream slots.</summary>
internal sealed class FoliagePointCasterIndex
{
    readonly List<(FoliagePointCasterRef Reference, Vector4 Sphere)> _entries = new();
    readonly PointCasterIndex _index = new();
    readonly List<int> _hits = new();

    internal int TouchTests => _index.TouchTests;
    internal int SortInputs => _index.SortInputs;
    internal int Count => _entries.Count;

    internal void Reset()
    {
        _entries.Clear();
        _index.Reset(0);
    }

    internal void Add(int sourceOrdinal, FoliagePointShadowStream stream, ReadOnlySpan<FoliagePointShadowRange> ranges)
    {
        foreach (FoliagePointShadowRange range in ranges)
            foreach (ref readonly FoliagePointShadowCaster caster in stream.SelectRange(range))
                _entries.Add((new FoliagePointCasterRef(sourceOrdinal, caster.Slot), caster.Sphere));
    }

    internal void Seal()
    {
        _index.Reset(_entries.Count);
        for (int slot = 0; slot < _entries.Count; slot++)
        {
            Vector4 sphere = _entries[slot].Sphere;
            _index.Add(slot, 0, new Vector3(sphere.X, sphere.Y, sphere.Z), sphere.W);
        }
        _index.Seal();
    }

    internal void Query(Vector3 lightPosition, float radius, float nearRadius,
        Vector3 exclusionMin, Vector3 exclusionMax, List<FoliagePointCasterRef> result)
    {
        ArgumentNullException.ThrowIfNull(result);
        result.Clear();
        _index.Query(lightPosition, radius, nearRadius, exclusionMin, exclusionMax, _hits);
        foreach (int slot in _hits) result.Add(_entries[slot].Reference);
    }
}
