using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.Versioning;
using KhaozEngine.MapDoc.Render3D;
using static KhaozEngine.Tests.MapDocOracle.ShippedSceneComparison;

namespace KhaozEngine.Tests.MapDocOracle;

[UnsupportedOSPlatform("windows")]
internal readonly record struct SceneSurfacePoint(double X, double Y, double Z)
{
    internal static SceneSurfacePoint World(Vector3 local, MapDrawRecord draw)
    {
        var p = Vector3.Transform(local, draw.LocalTransform);
        return new(p.X + (double)draw.Anchor.X, p.Y + (double)draw.Anchor.Y, p.Z + (double)draw.Anchor.Z);
    }
    internal bool Finite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);
}

[UnsupportedOSPlatform("windows")]
internal sealed record SceneSurfaceTriangle(SceneSurfacePoint A, SceneSurfacePoint B, SceneSurfacePoint C)
{
    internal SceneSurfaceTriangle(Vector3 a, Vector3 b, Vector3 c) : this(new SceneSurfacePoint(a.X, a.Y, a.Z), new SceneSurfacePoint(b.X, b.Y, b.Z), new SceneSurfacePoint(c.X, c.Y, c.Z)) { }
    internal SceneSurfacePoint[] Points => [A, B, C];
    internal double SignedArea => Cross(A, B, C) / 2;
    internal double Area => Math.Abs(SignedArea);
    internal double MinX => Math.Min(A.X, Math.Min(B.X, C.X));
    internal double MaxX => Math.Max(A.X, Math.Max(B.X, C.X));
    internal double MinZ => Math.Min(A.Z, Math.Min(B.Z, C.Z));
    internal double MaxZ => Math.Max(A.Z, Math.Max(B.Z, C.Z));
    internal bool Contains(SceneSurfacePoint p)
    {
        double area = Cross(A, B, C);
        if (!p.Finite || area == 0) return false;
        double b = Cross(A, p, C) / area, c = Cross(A, B, p) / area, a = 1 - b - c;
        return a >= -GeometryTolerance && b >= -GeometryTolerance && c >= -GeometryTolerance &&
            Math.Abs(p.Y - (a * A.Y + b * B.Y + c * C.Y)) <= GeometryTolerance;
    }
    internal static double Cross(SceneSurfacePoint a, SceneSurfacePoint b, SceneSurfacePoint c) =>
        (b.X - a.X) * (c.Z - a.Z) - (b.Z - a.Z) * (c.X - a.X);
}

/// <summary>Checks domains geometrically, including holes and overlaps, without comparing index or triangle order.</summary>
[UnsupportedOSPlatform("windows")]
internal static class SceneSurfaceCoverage
{
    internal static bool Covers(IReadOnlyList<SceneSurfaceTriangle> expected, IReadOnlyList<SceneSurfaceTriangle> actual)
    {
        if (actual.Any(t => t.Points.Any(p => !p.Finite) || t.Area == 0)) return false;
        if (Math.Abs(expected.Sum(t => t.Area) - actual.Sum(t => t.Area)) > GeometryTolerance) return false;
        foreach (var triangle in actual)
            if (Math.Abs(expected.Sum(t => Intersection(t, triangle)) - triangle.Area) > GeometryTolerance) return false;
        foreach (var triangle in expected)
            if (Math.Abs(actual.Sum(t => Intersection(t, triangle)) - triangle.Area) > GeometryTolerance) return false;
        var ordered = actual.OrderBy(t => t.MinX).ToArray();
        for (int i = 0; i < ordered.Length; i++)
            for (int j = i + 1; j < ordered.Length && ordered[j].MinX < ordered[i].MaxX; j++)
                if (ordered[j].MinZ < ordered[i].MaxZ && ordered[j].MaxZ > ordered[i].MinZ &&
                    Intersection(ordered[i], ordered[j]) > GeometryTolerance) return false;
        return true;
    }

    static double Intersection(SceneSurfaceTriangle clip, SceneSurfaceTriangle subject)
    {
        if (clip.MinX >= subject.MaxX || subject.MinX >= clip.MaxX || clip.MinZ >= subject.MaxZ || subject.MinZ >= clip.MaxZ) return 0;
        var polygon = subject.Points.ToList();
        var corners = clip.Points;
        double sign = Math.Sign(clip.SignedArea);
        for (int i = 0; i < 3 && polygon.Count != 0; i++)
        {
            var a = corners[i]; var b = corners[(i + 1) % 3]; var next = new List<SceneSurfacePoint>();
            var previous = polygon[^1]; double before = sign * SceneSurfaceTriangle.Cross(a, b, previous);
            foreach (var point in polygon)
            {
                double after = sign * SceneSurfaceTriangle.Cross(a, b, point);
                if ((before >= 0) != (after >= 0))
                {
                    double t = before / (before - after);
                    next.Add(new(previous.X + t * (point.X - previous.X), previous.Y + t * (point.Y - previous.Y), previous.Z + t * (point.Z - previous.Z)));
                }
                if (after >= 0) next.Add(point);
                previous = point; before = after;
            }
            polygon = next;
        }
        double area = 0;
        for (int i = 1; i + 1 < polygon.Count; i++) area += Math.Abs(SceneSurfaceTriangle.Cross(polygon[0], polygon[i], polygon[i + 1])) / 2;
        return area;
    }
}
