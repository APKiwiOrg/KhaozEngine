using System;
using System.Collections.Generic;
using KhaozEngine.Physics;

namespace KhaozEngine.Movement;

/// <summary>Frozen exact identities and group intervals. All allocations share the capture's checked index reserve.</summary>
internal sealed class DescentEvidenceLookup
{
    readonly DescentRawBinding[] _raw;
    readonly Dictionary<(int X, int Z, int Bits), int> _rawIndex;
    readonly Dictionary<(string Source, long Address), DescentSolidInterval> _solids = [];
    readonly HashSet<DescentFace> _faces;
    internal DescentEvidenceLookup(DescentFace[] faces, DescentRawBinding[] raw, DescentCaptureIndexBudget budget)
    {
        // Include membership, temporary grouping and frozen group bookkeeping, not only member ordinals.
        budget.Reserve(faces.Length, 384);
        budget.Reserve(raw.Length, 64);
        _raw = raw; _rawIndex = new(raw.Length); _faces = new(faces);
        for (int i = 0; i < raw.Length; i++)
        {
            var row = raw[i]; var key = (row.X, row.Z, row.HeightBits);
            if (_rawIndex.TryGetValue(key, out int previous))
            {
                // Retain the first row even for an ambiguous key, matching the old scan's out value.
                if (previous >= 0) _rawIndex[key] = ~previous;
            }
            else _rawIndex.Add(key, i);
        }
        var members = new Dictionary<(string, long), List<int>>();
        for (int i = 0; i < faces.Length; i++)
        {
            var face = faces[i];
            if (face.Kind != PhysicsEvidenceKind.SolidPolygon) continue;
            var key = (face.Key.Source, face.Key.Address);
            if (!members.TryGetValue(key, out var group)) members.Add(key, group = []);
            group.Add(i);
        }
        foreach (var (key, group) in members)
        {
            double min = double.PositiveInfinity, max = double.NegativeInfinity;
            foreach (int i in group)
                foreach (var p in faces[i].Points) { min = Math.Min(min, p.Y); max = Math.Max(max, p.Y); }
            _solids.Add(key, new(min, max, group.ToArray()));
        }
    }
    internal bool Owns(DescentFace face) => _faces.Contains(face);
    internal bool TryRaw(int x, int z, int bits, out DescentRawBinding result)
    {
        result = default;
        if (!_rawIndex.TryGetValue((x, z, bits), out int index)) return false;
        result = _raw[index >= 0 ? index : ~index];
        return index >= 0;
    }
    internal DescentSolidInterval Solid(DescentFace face) =>
        _solids.TryGetValue((face.Key.Source, face.Key.Address), out var group) ? group : DescentSolidInterval.Empty;
}

/// <summary>Exact full-solid Y interval with its original contributing face order.</summary>
internal sealed class DescentSolidInterval(double minimum, double maximum, int[] members)
{
    internal static DescentSolidInterval Empty { get; } = new(double.PositiveInfinity, double.NegativeInfinity, []);
    internal double Minimum { get; } = minimum;
    internal double Maximum { get; } = maximum;
    internal ReadOnlySpan<int> Members => members;
}
