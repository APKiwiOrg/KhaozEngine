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

    /// <summary>The finest sample pitch the clearance builders accept, in local units.</summary>
    internal const float MinSamplePitch = 0.01f;

    /// <summary>Throw <see cref="ArgumentOutOfRangeException"/> unless <paramref name="pitch"/> is finite and at
    /// least <see cref="MinSamplePitch"/>.</summary>
    internal static void RequireSamplePitch(float pitch, string paramName)
    {
        if (!(pitch >= MinSamplePitch && float.IsFinite(pitch)))
            throw new ArgumentOutOfRangeException(paramName, pitch, "Sample pitch must be finite and at least 0.01.");
    }

    /// <summary>Visit every triangle sample at <paramref name="pitch"/>, shared by both clearance builders. Each
    /// triangle is sampled in rows parallel to its longest edge, spaced no more than <paramref name="pitch"/> apart
    /// across its height, each row sampled no coarser than <paramref name="pitch"/> along its length. The first row
    /// is the longest edge and the last is the opposite vertex. The two other edges get their own samples at the
    /// same spacing. The three vertices are always visited exactly, and the count grows with area plus perimeter
    /// for every triangle shape.</summary>
    internal void ForEachSample(float pitch, Action<Vector3> visit)
    {
        RequireSamplePitch(pitch, nameof(pitch));
        for (int t = 0; t < _indices.Length; t += 3)
            SampleTriangle(_positions[_indices[t]], _positions[_indices[t + 1]], _positions[_indices[t + 2]], pitch, visit);
    }

    static void SampleTriangle(Vector3 a, Vector3 b, Vector3 c, float pitch, Action<Vector3> visit)
    {
        float ab = Vector3.Distance(a, b);
        float bc = Vector3.Distance(b, c);
        float ca = Vector3.Distance(c, a);

        // Name the longest edge start..end and the opposite vertex apex.
        Vector3 start, end, apex;
        float longest;
        if (ab >= bc && ab >= ca)
            (start, end, apex, longest) = (a, b, c, ab);
        else if (bc >= ca)
            (start, end, apex, longest) = (b, c, a, bc);
        else
            (start, end, apex, longest) = (c, a, b, ca);

        if (longest == 0f)
        {
            visit(start);
            return;
        }

        float height = Vector3.Cross(end - start, apex - start).Length() / longest;
        int rows = Steps(height, pitch);
        for (int r = 0; r < rows; r++)
        {
            float s = r / (float)rows;
            Vector3 from = start * (1f - s) + apex * s;
            Vector3 to = end * (1f - s) + apex * s;
            SampleSegment(from, to, Steps(longest * (1f - s), pitch), includeEnds: true, visit);
        }

        visit(apex);
        SampleSegment(start, apex, Steps(Vector3.Distance(start, apex), pitch), includeEnds: false, visit);
        SampleSegment(end, apex, Steps(Vector3.Distance(end, apex), pitch), includeEnds: false, visit);
    }

    /// <summary>Visit <paramref name="steps"/> + 1 evenly spaced points from <paramref name="from"/> to
    /// <paramref name="to"/>, the end points exact, or only the interior ones when <paramref name="includeEnds"/>
    /// is false.</summary>
    static void SampleSegment(Vector3 from, Vector3 to, int steps, bool includeEnds, Action<Vector3> visit)
    {
        if (includeEnds)
            visit(from);
        for (int k = 1; k < steps; k++)
        {
            float w = k / (float)steps;
            visit(from * (1f - w) + to * w);
        }

        if (includeEnds)
            visit(to);
    }

    static int Steps(float length, float pitch) => Math.Max(1, (int)Math.Ceiling(length / (double)pitch));
}
