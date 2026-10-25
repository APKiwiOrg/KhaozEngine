using System.Collections.Generic;
using System.Numerics;
using P = KhaozEngine.Render3D.Internal.WaterOpticsExactPoint;
using R = KhaozEngine.Render3D.Internal.WaterOpticsExact;

namespace KhaozEngine.Render3D.Internal;

/// <summary>Exact closed convex clipping, surface agreement, shared-boundary discontinuity and certificate
/// union containment.</summary>
internal static class WaterOpticsTriangleCoverage
{
    private readonly record struct Line(R A, R B, R C);
    private readonly record struct Contact(Line? Support, R Jump);

    /// <summary>Refuses positive-area disagreement and uncovered certificates. Returns the exact depth
    /// discontinuity charge of coincident shared boundaries, which the caller adds to the error bound.</summary>
    internal static R Validate(WaterOpticsExactTriangle[] triangles, List<int>?[] bins,
        Vector2 size, int columns, int rows, WaterOpticsRectangle[] certificates)
    {
        // The exact query and its mapped point lie in one closed expanded bin, and so does every triangle
        // between them. Moving each endpoint into the interior of its own triangle, the straight segment
        // avoids every vertex. Each change of containing triangle then crosses one shared segment at an
        // interior point, and the segment crosses each supporting line at most once. Beyond slope times
        // displacement, the depth change is at most the sum over discontinuous lines in the bin of each
        // line's largest jump. Point-only contacts follow in the limit, and their own jump is also charged.
        var contacts = new Dictionary<long, Contact>();
        R charge = 0;
        foreach (List<int>? bin in bins)
        {
            if (bin == null) continue;
            var lines = new Dictionary<Line, R>();
            R point = 0;
            for (int i = 0; i < bin.Count; i++)
            for (int j = i + 1; j < bin.Count; j++)
            {
                int a = bin[i], b = bin[j];
                long key = ((long)a << 32) | (uint)b;
                if (!contacts.TryGetValue(key, out Contact contact))
                    contacts.Add(key, contact = Measure(triangles[a], triangles[b]));
                if (contact.Jump.Sign == 0) continue;
                if (contact.Support is not Line line) point = R.Max(point, contact.Jump);
                else lines[line] = lines.TryGetValue(line, out R largest) ? R.Max(largest, contact.Jump) : contact.Jump;
            }
            R sum = 0;
            foreach (R largest in lines.Values) sum += largest;
            charge = R.Max(charge, R.Max(sum, point));
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
        return charge;
    }

    // Distinct planes agree at most on one line, so a positive-area overlap always holds two surfaces.
    // Otherwise the closed intersection is a shared segment or a point. Both depths are linear along a
    // segment, so the largest jump is reached at one of its clipped endpoints, which are all compared.
    private static Contact Measure(WaterOpticsExactTriangle a, WaterOpticsExactTriangle b)
    {
        if (a.SamePlane(b)) return default;
        List<P> intersection = ClipTriangle([a.A, a.B, a.C], b);
        if (intersection.Count == 0) return default;
        if (HasArea(intersection))
            throw new WaterOpticsSurfaceException(WaterOpticsSurfaceFailure.AmbiguousSurface,
                "Projected triangles with distinct planes overlap in positive area.");
        R jump = 0;
        P first = intersection[0], other = first;
        foreach (P point in intersection)
        {
            jump = R.Max(jump, R.Abs(a.Height(point) - b.Height(point)));
            if (point != first) other = point;
        }
        return new(other == first ? null : Through(first, other), jump);
    }

    // The normalized implicit line through two distinct exact points, equal for every collinear segment.
    private static Line Through(P p, P q)
    {
        R a = p.Z - q.Z, b = q.X - p.X, c = p.X * q.Z - q.X * p.Z;
        R scale = a.Sign != 0 ? a : b;
        return new(a / scale, b / scale, c / scale);
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
