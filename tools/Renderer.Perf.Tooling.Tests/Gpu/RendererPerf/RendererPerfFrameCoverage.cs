using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

/// <summary>The fraction of the frame a world polygon covers, clipped to the orthographic view volume.</summary>
internal static class RendererPerfFrameCoverage
{
    internal static double Fraction(IReadOnlyList<Vector3> polygon, Matrix4x4 viewProjection)
    {
        var clip = new List<Vector3>(polygon.Count);
        foreach (Vector3 point in polygon)
        {
            Vector4 projected = Vector4.Transform(new Vector4(point, 1f), viewProjection);
            if (projected.W != 1f) throw new InvalidOperationException("Coverage expects an orthographic projection.");
            clip.Add(new Vector3(projected.X, projected.Y, projected.Z));
        }
        // Sutherland-Hodgman against x and y in [-1, 1] and depth in [0, 1].
        for (int axis = 0; axis < 3; axis++)
        {
            clip = Clip(clip, axis, axis == 2 ? 0f : -1f, false);
            clip = Clip(clip, axis, 1f, true);
        }
        double area = 0d;
        for (int i = 0; i < clip.Count; i++)
        {
            Vector3 a = clip[i], b = clip[(i + 1) % clip.Count];
            area += (double)a.X * b.Y - (double)b.X * a.Y;
        }
        // The NDC frame spans 2 by 2.
        return Math.Abs(area) * .5d / 4d;
    }

    static List<Vector3> Clip(List<Vector3> polygon, int axis, float limit, bool upper)
    {
        var result = new List<Vector3>(polygon.Count + 1);
        for (int i = 0; i < polygon.Count; i++)
        {
            Vector3 a = polygon[i], b = polygon[(i + 1) % polygon.Count];
            bool aIn = Inside(a), bIn = Inside(b);
            if (aIn) result.Add(a);
            if (aIn != bIn)
            {
                float t = (limit - a[axis]) / (b[axis] - a[axis]);
                result.Add(a + (b - a) * t);
            }
        }
        return result;

        bool Inside(Vector3 point) => upper ? point[axis] <= limit : point[axis] >= limit;
    }
}
