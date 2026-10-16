using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Primitives;

/// <summary>Probe points spread over the surface of a subject <see cref="ClearanceMesh"/>, in the mesh's local
/// frame. <see cref="ClearanceProbe.Deepest"/> tests each point against an obstacle. Every triangle is sampled on a
/// barycentric grid at <c>pitch</c> spacing that always includes its vertices and edges, so a finer pitch finds
/// shallower and narrower contacts at the cost of more points.</summary>
public sealed class ClearanceSamples
{
    readonly Vector3[] _points;

    ClearanceSamples(Vector3[] points) => _points = points;

    /// <summary>The sample points, in the mesh's local frame.</summary>
    public IReadOnlyList<Vector3> Points => _points;

    /// <summary>Sample every triangle of <paramref name="mesh"/> at <paramref name="pitch"/>. Throws
    /// <see cref="ArgumentOutOfRangeException"/> when <paramref name="pitch"/> is not a positive finite number.</summary>
    public static ClearanceSamples Build(ClearanceMesh mesh, float pitch)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (!(pitch > 0f && float.IsFinite(pitch)))
            throw new ArgumentOutOfRangeException(nameof(pitch), pitch, "Sample pitch must be positive and finite.");

        var points = new List<Vector3>();
        mesh.ForEachSample(pitch, points.Add);
        return new ClearanceSamples(points.ToArray());
    }
}
