using System.Collections.Generic;
using System.Numerics;
using P = KhaozEngine.Render3D.Internal.WaterOpticsExactPoint;
using R = KhaozEngine.Render3D.Internal.WaterOpticsExact;

namespace KhaozEngine.Render3D.Internal;

/// <summary>Exact closed convex clipping, surface agreement and certificate union containment.</summary>
internal static class WaterOpticsTriangleCoverage
{
    internal static void Validate(WaterOpticsExactTriangle[] triangles, List<int>?[] bins,
        Vector2 size, int columns, int rows, WaterOpticsRectangle[] certificates)
    {
        var pairs = new HashSet<long>();
        foreach (List<int>? bin in bins)
        {
            if (bin == null) continue;
            for (int i = 0; i < bin.Count; i++)
            for (int j = i + 1; j < bin.Count; j++)
            {
                int a = bin[i], b = bin[j];
                if (!pairs.Add(((long)a << 32) | (uint)b)) continue;
                ValidateAgreement(triangles[a], triangles[b]);
            }
        }

        foreach (WaterOpticsRectangle certificate in certificates)
        {
            for (int z = 0; z < rows; z++)
            {
                R minZ = R.Max(R.From(certificate.Minimum.Y), R.From(size.Y) * z / rows);
                R maxZ = R.Min(R.From(certificate.Maximum.Y), R.From(size.Y) * (z + 1) / rows);
                if (minZ.CompareTo(maxZ) >= 0) continue;
                for (int x = 0; x < columns; x++)
                {
                    R minX = R.Max(R.From(certificate.Minimum.X), R.From(size.X) * x / columns);
                    R maxX = R.Min(R.From(certificate.Maximum.X), R.From(size.X) * (x + 1) / columns);
                    if (minX.CompareTo(maxX) >= 0) continue;
                    var polygon = new List<P> { new(minX, minZ), new(maxX, minZ), new(maxX, maxZ), new(minX, maxZ) };
                    ProveCovered(polygon, bins[z * columns + x], triangles, x, z);
                }
            }
        }
    }

    private static void ValidateAgreement(WaterOpticsExactTriangle a, WaterOpticsExactTriangle b)
    {
        if (a.SamePlane(b)) return;
        List<P> intersection = ClipTriangle([a.A, a.B, a.C], b);
        foreach (P point in intersection)
        {
            if (!a.Height(point).Equals(b.Height(point)))
                throw new WaterOpticsSurfaceException(WaterOpticsSurfaceFailure.AmbiguousSurface,
                    "Projected triangles disagree on an overlapping face, edge or vertex.");
        }
    }

    private static void ProveCovered(List<P> rectangle, List<int>? bin, WaterOpticsExactTriangle[] triangles, int x, int z)
    {
        var uncovered = new List<List<P>> { rectangle };
        if (bin != null)
        {
            foreach (int ordinal in bin)
            {
                WaterOpticsExactTriangle triangle = triangles[ordinal];
                var next = new List<List<P>>();
                foreach (List<P> polygon in uncovered) Subtract(polygon, triangle, next);
                uncovered = next;
                if (uncovered.Count == 0) return;
            }
        }
        throw new WaterOpticsSurfaceException(WaterOpticsSurfaceFailure.IncompleteCoverage,
            $"The certified rectangle has uncovered area in bin ({x}, {z}).");
    }

    // Subtracting a convex triangle yields at most three convex positive-area pieces. Repeating this
    // operation proves union containment, rather than summing overlapping areas. Exact arithmetic keeps
    // arbitrarily narrow holes. A finite union of closed triangles is closed. If any point of a nonempty
    // closed rectangle were missing, its open complement would contain a positive-area rectangle portion.
    // Thus absence of positive-area residuals proves the rectangle's boundary segments and vertices too.
    private static void Subtract(List<P> polygon, WaterOpticsExactTriangle triangle, List<List<P>> result)
    {
        P[] vertices = [triangle.A, triangle.B, triangle.C];
        int sign = triangle.Determinant.Sign;
        List<P> remaining = polygon;
        for (int i = 0; i < 3 && remaining.Count != 0; i++)
        {
            P a = vertices[i], b = vertices[(i + 1) % 3];
            List<P> outside = Clip(remaining, a, b, -sign);
            if (HasArea(outside)) result.Add(outside);
            remaining = Clip(remaining, a, b, sign);
        }
    }

    private static bool HasArea(List<P> polygon)
    {
        if (polygon.Count < 3) return false;
        R area = 0;
        for (int i = 1; i + 1 < polygon.Count; i++) area += P.Cross(polygon[0], polygon[i], polygon[i + 1]);
        return area.Sign != 0;
    }

    private static List<P> ClipTriangle(List<P> polygon, WaterOpticsExactTriangle triangle)
    {
        int sign = triangle.Determinant.Sign;
        polygon = Clip(polygon, triangle.A, triangle.B, sign);
        polygon = Clip(polygon, triangle.B, triangle.C, sign);
        return Clip(polygon, triangle.C, triangle.A, sign);
    }

    private static List<P> Clip(List<P> polygon, P a, P b, int sign)
    {
        var result = new List<P>();
        if (polygon.Count == 0) return result;
        P previous = polygon[^1];
        R previousDistance = P.Cross(a, b, previous) * sign;
        foreach (P current in polygon)
        {
            R distance = P.Cross(a, b, current) * sign;
            if ((distance.Sign < 0) != (previousDistance.Sign < 0))
            {
                R t = previousDistance / (previousDistance - distance);
                Add(result, new(previous.X + (current.X - previous.X) * t, previous.Z + (current.Z - previous.Z) * t));
            }
            if (distance.Sign >= 0) Add(result, current);
            previous = current;
            previousDistance = distance;
        }
        if (result.Count > 1 && result[0] == result[^1]) result.RemoveAt(result.Count - 1);
        return result;
    }

    private static void Add(List<P> polygon, P point)
    {
        if (polygon.Count == 0 || polygon[^1] != point) polygon.Add(point);
    }
}
