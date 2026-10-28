using System;
using System.Numerics;

namespace KhaozEngine.Primitives;

/// <summary>
/// Allocation-free point-against-triangle-soup queries: whether a mesh encloses a point, and how close its nearest
/// triangle is. Positions and indices are a plain triangle list, three indices per triangle.
/// </summary>
/// <remarks>
/// Containment is the generalized winding number rather than a ray parity count, because authored low-poly
/// models are rarely watertight. An uncapped trunk, canopy blobs that interpenetrate, or a shell exported inside
/// out all still give a clear answer: about 1 in magnitude inside a shell, about 0 outside, and a fractional value
/// near the gap of an open one. Callers that treat a magnitude of at least 0.5 as inside are robust to all three.
/// </remarks>
public static class MeshContainment
{
    /// <summary>The generalized winding number of the mesh at <paramref name="point"/>: the sum of each triangle's
    /// signed solid angle seen from the point, over 4 pi. Positive inside an outward-wound closed shell, negative
    /// inside an inward-wound one, 0 outside, and summed where shells overlap.</summary>
    /// <param name="positions">Vertex positions.</param>
    /// <param name="indices">Triangle list indices into <paramref name="positions"/>.</param>
    /// <param name="point">The point asked about, in the same frame as <paramref name="positions"/>.</param>
    public static float WindingNumber(ReadOnlySpan<Vector3> positions, ReadOnlySpan<int> indices, Vector3 point)
    {
        double total = 0d;
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            Vector3 a = positions[indices[i]] - point;
            Vector3 b = positions[indices[i + 1]] - point;
            Vector3 c = positions[indices[i + 2]] - point;
            float la = a.Length();
            float lb = b.Length();
            float lc = c.Length();
            // Van Oosterom and Strackee: tan(omega / 2) = det[a b c] / (|a||b||c| + (a.b)|c| + (a.c)|b| + (b.c)|a|).
            float det = Vector3.Dot(a, Vector3.Cross(b, c));
            float denominator = la * lb * lc + Vector3.Dot(a, b) * lc + Vector3.Dot(a, c) * lb + Vector3.Dot(b, c) * la;
            total += 2d * Math.Atan2(det, denominator);
        }
        return (float)(total / (4d * Math.PI));
    }

    /// <summary>The squared distance from <paramref name="point"/> to the nearest point on any triangle, or
    /// <see cref="float.PositiveInfinity"/> for a mesh with no triangles.</summary>
    /// <param name="positions">Vertex positions.</param>
    /// <param name="indices">Triangle list indices into <paramref name="positions"/>.</param>
    /// <param name="point">The point asked about, in the same frame as <paramref name="positions"/>.</param>
    public static float NearestDistanceSquared(ReadOnlySpan<Vector3> positions, ReadOnlySpan<int> indices,
        Vector3 point)
    {
        float best = float.PositiveInfinity;
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            Vector3 closest = ClosestPointOnTriangle(point,
                positions[indices[i]], positions[indices[i + 1]], positions[indices[i + 2]]);
            float d = Vector3.DistanceSquared(point, closest);
            if (d < best) best = d;
        }
        return best;
    }

    // Ericson, Real-Time Collision Detection 5.1.5: classify the point against the triangle's Voronoi regions.
    static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 ab = b - a;
        Vector3 ac = c - a;
        Vector3 ap = p - a;
        float d1 = Vector3.Dot(ab, ap);
        float d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0f && d2 <= 0f) return a;

        Vector3 bp = p - b;
        float d3 = Vector3.Dot(ab, bp);
        float d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0f && d4 <= d3) return b;

        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f) return a + ab * (d1 / (d1 - d3));

        Vector3 cp = p - c;
        float d5 = Vector3.Dot(ab, cp);
        float d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0f && d5 <= d6) return c;

        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f) return a + ac * (d2 / (d2 - d6));

        float va = d3 * d6 - d5 * d4;
        if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
            return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));

        float denominator = 1f / (va + vb + vc);
        return a + ab * (vb * denominator) + ac * (vc * denominator);
    }
}
