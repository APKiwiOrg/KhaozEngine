using System;
using System.Collections.Generic;

namespace KhaozEngine.TileWorld;

/// <summary>Each faded object's fade, from 0 (fully drawn) to 1 (colour withheld), stepped linearly toward 1 while
/// the object contains the eye and toward 0 otherwise. An entry exists only while its fade is above 0.</summary>
internal sealed class TileCameraFadeState
{
    // Within this of an end, a fade is that end, so float steps such as 0.075 / 0.15 land on 0 and 1 exactly.
    const float Snap = 1e-4f;

    readonly Dictionary<long, float> _fades = new();
    readonly List<long> _ids = new();
    readonly HashSet<(RegionCoord Region, int Plane)> _regions = new();

    public int Count => _fades.Count;

    /// <summary>Whether the view's camera fade is configured at all, even with nothing faded this frame.</summary>
    public bool Enabled { get; set; }

    /// <summary>Whether any faded object anchors in this region-plane.</summary>
    public bool AnyOn(RegionCoord region, int plane) => _regions.Count > 0 && _regions.Contains((region, plane));

    /// <summary>Records that a faded object anchors in this region-plane, after <see cref="ClearRegions"/>.</summary>
    public void MarkRegion(RegionCoord region, int plane) => _regions.Add((region, plane));

    public void ClearRegions() => _regions.Clear();

    public bool TryGet(long objectId, out float fade) => _fades.TryGetValue(objectId, out fade);

    /// <summary>The ids currently faded. The list is reused, so read it before the next call.</summary>
    public List<long> Ids()
    {
        _ids.Clear();
        foreach (long id in _fades.Keys) _ids.Add(id);
        return _ids;
    }

    /// <summary>Moves every fade by <paramref name="step"/> toward 1 for ids in <paramref name="inside"/> and
    /// toward 0 for the rest, dropping entries that reach 0. A positive infinite step snaps.</summary>
    public void Advance(HashSet<long> inside, float step)
    {
        foreach (long id in inside) _fades.TryAdd(id, 0f);
        foreach (long id in Ids())
        {
            float fade = _fades[id];
            if (inside.Contains(id))
            {
                fade = MathF.Min(1f, fade + step);
                if (fade > 1f - Snap) fade = 1f;
            }
            else
            {
                fade = MathF.Max(0f, fade - step);
                if (fade < Snap) fade = 0f;
            }
            if (fade > 0f) _fades[id] = fade;
            else _fades.Remove(id);
        }
    }

    public void Remove(long objectId) => _fades.Remove(objectId);

    public void Clear()
    {
        _fades.Clear();
        _regions.Clear();
    }
}
