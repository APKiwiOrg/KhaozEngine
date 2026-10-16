using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Primitives;

/// <summary>An immutable indexed triangle mesh in its own local frame, the input to the clearance check.
/// <see cref="ClearanceSolid"/> builds an obstacle from it and <see cref="ClearanceSamples"/> builds the probe
/// points of a subject from it. Construction copies and validates the input once, so every later builder can
/// trust it: the index count is a whole number of triangles, every index names a position, every position is
/// finite and there is at least one triangle. <see cref="Min"/> and <see cref="Max"/> bound every position.</summary>
public sealed class ClearanceMesh
{
    readonly Vector3[] _positions;
    readonly int[] _indices;

    /// <summary>Copy and validate a mesh. Throws <see cref="ArgumentException"/> when the index count is not a
    /// multiple of 3, an index falls outside <paramref name="positions"/>, a position is not finite or the mesh
    /// has no triangles.</summary>
    public ClearanceMesh(IReadOnlyList<Vector3> positions, IReadOnlyList<int> indices)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(indices);

        if (indices.Count % 3 != 0)
            throw new ArgumentException($"Index count {indices.Count} is not a whole number of triangles.", nameof(indices));
        if (indices.Count == 0)
            throw new ArgumentException("A clearance mesh needs at least one triangle.", nameof(indices));

        _positions = new Vector3[positions.Count];
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        for (int i = 0; i < _positions.Length; i++)
        {
            Vector3 p = positions[i];
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z))
                throw new ArgumentException($"Position {i} is not finite: {p}.", nameof(positions));
            _positions[i] = p;
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        _indices = new int[indices.Count];
        for (int i = 0; i < _indices.Length; i++)
        {
            int index = indices[i];
            if ((uint)index >= (uint)_positions.Length)
                throw new ArgumentException(
                    $"Index {i} names position {index}, outside 0..{_positions.Length - 1}.", nameof(indices));
            _indices[i] = index;
        }

        Min = min;
        Max = max;
    }

    /// <summary>The vertex positions, in the mesh's local frame.</summary>
    public IReadOnlyList<Vector3> Positions => _positions;

    /// <summary>Three indices into <see cref="Positions"/> per triangle.</summary>
    public IReadOnlyList<int> Indices => _indices;

    /// <summary>The smallest corner of the box bounding every position.</summary>
    public Vector3 Min { get; }

    /// <summary>The largest corner of the box bounding every position.</summary>
    public Vector3 Max { get; }

    /// <summary>Visit every triangle sample at <paramref name="pitch"/>: a barycentric grid with
    /// <c>n = max(1, ceil(longestEdge / pitch))</c> steps per side and every <c>(i, j)</c> with
    /// <c>i + j &lt;= n</c>, so vertices and edges are always included. Shared by both clearance builders.</summary>
    internal void ForEachSample(float pitch, Action<Vector3> visit)
    {
        for (int t = 0; t < _indices.Length; t += 3)
        {
            Vector3 a = _positions[_indices[t]];
            Vector3 b = _positions[_indices[t + 1]];
            Vector3 c = _positions[_indices[t + 2]];

            float longest = MathF.Max(Vector3.Distance(a, b), MathF.Max(Vector3.Distance(b, c), Vector3.Distance(c, a)));
            int n = Math.Max(1, (int)Math.Ceiling(longest / (double)pitch));
            float inv = 1f / n;

            for (int i = 0; i <= n; i++)
            {
                for (int j = 0; i + j <= n; j++)
                {
                    float wb = i * inv;
                    float wc = j * inv;
                    float wa = (n - i - j) * inv;
                    visit(a * wa + b * wb + c * wc);
                }
            }
        }
    }
}
