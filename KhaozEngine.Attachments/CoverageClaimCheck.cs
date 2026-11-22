using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Attachments;

/// <summary>
/// Checks offline that the fixed triangles of a piece lie over every body triangle of every region it claims to
/// cover at the rest pose. It casts one ray per claimed body triangle against the fixed piece triangles, which
/// suits low poly meshes and is never meant to run per frame.
/// </summary>
public static class CoverageClaimCheck
{
    /// <summary>
    /// Check <paramref name="piece"/>'s covered regions against the body. <paramref name="bodyMeshes"/> follow
    /// <paramref name="body"/>'s part order. <paramref name="pieceMeshes"/> are already in the body's rest frame.
    /// For a piece with motion, supply one mesh per definition part in any order and set each mesh's
    /// <see cref="CoverageClaimMesh.PartId"/>. Every part with motion needs a swinging mask. A claimed body triangle
    /// is covered when a ray from its centroid along its outward normal, from counter clockwise winding, meets a
    /// fixed piece triangle from either side at a distance from 0 to
    /// <paramref name="maxDistanceMetres"/> inclusive.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The distance is not finite or not greater than zero.</exception>
    /// <exception cref="ArgumentException">A nonempty swinging mask has a different length than its mesh's positions.</exception>
    /// <exception cref="CoverageClaimDataException">The geometry or the claim breaks a rule.</exception>
    public static CoverageClaimReport Verify(IReadOnlyList<CoverageClaimMesh> bodyMeshes, BodyRegionMap body, AttachmentSetDefinition piece,
        IReadOnlyList<CoverageClaimMesh> pieceMeshes, float maxDistanceMetres)
    {
        CoverageClaimValidation.CheckArguments(bodyMeshes, body, piece, pieceMeshes);
        if (!float.IsFinite(maxDistanceMetres) || maxDistanceMetres <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxDistanceMetres), maxDistanceMetres, "The distance must be finite and greater than zero.");

        CoverageClaimValidation.CheckBody(bodyMeshes, body);
        RegionSet claimed = CoverageClaimValidation.CheckCoveredRegions(body, piece);

        CoverageClaimSwingEvidence.Validate(piece, pieceMeshes);
        PieceTriangle[] targets = PieceTriangles(pieceMeshes, out int ignoredSwingingTriangleCount);
        return Report(bodyMeshes, body, claimed, targets, maxDistanceMetres, ignoredSwingingTriangleCount);
    }

    static CoverageClaimReport Report(IReadOnlyList<CoverageClaimMesh> bodyMeshes, BodyRegionMap body, RegionSet claimed,
        PieceTriangle[] targets, double maxDistance, int ignoredSwingingTriangleCount)
    {
        int regionCount = body.Regions.Count;
        var triangleCounts = new int[regionCount];
        var degenerateCounts = new int[regionCount];
        var uncovered = new List<CoverageClaimTriangle>[regionCount];
        for (int region = 0; region < regionCount; region++)
            uncovered[region] = [];

        if (!claimed.IsEmpty)
        {
            for (int part = 0; part < bodyMeshes.Count; part++)
            {
                ReadOnlySpan<Vector3> positions = bodyMeshes[part].Positions.Span;
                ReadOnlySpan<uint> indices = bodyMeshes[part].Indices.Span;
                var regions = new byte[indices.Length / 3];
                body.WriteTriangleRegions(part, regions);
                for (int triangle = 0; triangle < regions.Length; triangle++)
                {
                    int region = regions[triangle];
                    if (!claimed.Contains(region))
                        continue;
                    triangleCounts[region]++;
                    Point a = new(positions[(int)indices[triangle * 3]]);
                    Point b = new(positions[(int)indices[triangle * 3 + 1]]);
                    Point c = new(positions[(int)indices[triangle * 3 + 2]]);
                    Point normal = Point.Cross(b - a, c - a);
                    double length = Math.Sqrt(Point.Dot(normal, normal));
                    // A zero area triangle has no outward direction to test, so it cannot show the claim false.
                    if (length == 0)
                    {
                        degenerateCounts[region]++;
                        continue;
                    }
                    Point centroid = (a + b + c) / 3;
                    if (!Hits(targets, centroid, normal / length, maxDistance))
                        uncovered[region].Add(new CoverageClaimTriangle(part, triangle));
                }
            }
        }

        var report = new List<CoverageClaimRegion>(claimed.Count);
        for (int region = 0; region < regionCount; region++)
            if (claimed.Contains(region))
                report.Add(new CoverageClaimRegion(body.Regions[region], triangleCounts[region], degenerateCounts[region], uncovered[region].ToArray()));
        return new CoverageClaimReport(report.ToArray(), ignoredSwingingTriangleCount);
    }

    // Woop, Benthin and Wald's watertight ray and triangle test, without back face culling so a piece covers whichever
    // way its triangles wind. A garment's inner surface faces the skin, and that is the surface an outward ray meets
    // first. Each vertex moves into the ray's frame by the same arithmetic whichever triangle lists it, and an edge's
    // side test changes sign exactly when the edge is walked the other way. So a ray that crosses an edge or a vertex
    // two triangles share lands inside at least one of them, whatever their index order, and no tolerance lets a ray
    // that passes outside the piece count as a hit.
    static bool Hits(PieceTriangle[] targets, Point origin, Point direction, double maxDistance)
    {
        var frame = new RayFrame(direction);
        foreach (PieceTriangle target in targets)
        {
            double along = Point.Dot(target.Normal, direction);
            // Zero for a ray in the triangle's plane or a zero area piece triangle, neither of which lies over the skin.
            if (along == 0)
                continue;
            Point toA = target.A - origin;
            // Taken from the triangle's plane, so the distance does not depend on where in the triangle the ray lands.
            double distance = Point.Dot(target.Normal, toA) / along;
            if (distance < 0 || distance > maxDistance)
                continue;
            (double X, double Y) a = frame.Project(toA);
            (double X, double Y) b = frame.Project(target.B - origin);
            (double X, double Y) c = frame.Project(target.C - origin);
            double u = Edge(b, c);
            double v = Edge(c, a);
            double w = Edge(a, b);
            if ((u < 0 || v < 0 || w < 0) && (u > 0 || v > 0 || w > 0))
                continue;
            // All three are zero only when the triangle projects to a line or a point along the ray.
            if (u + v + w != 0)
                return true;
        }
        return false;
    }

    // Twice the signed area of the ray, the edge's start and its end in the ray's frame. The two products only swap
    // places when the ends swap, so the value is exactly negated.
    static double Edge((double X, double Y) start, (double X, double Y) end) => end.X * start.Y - end.Y * start.X;

    static PieceTriangle[] PieceTriangles(IReadOnlyList<CoverageClaimMesh> pieceMeshes, out int ignoredSwingingTriangleCount)
    {
        var triangles = new List<PieceTriangle>();
        ignoredSwingingTriangleCount = 0;
        for (int part = 0; part < pieceMeshes.Count; part++)
        {
            CoverageClaimMesh mesh = CoverageClaimValidation.CheckPieceMesh(pieceMeshes, part);
            ReadOnlySpan<Vector3> positions = mesh.Positions.Span;
            ReadOnlySpan<uint> indices = mesh.Indices.Span;
            ReadOnlySpan<bool> swinging = mesh.Swinging.Span;
            for (int index = 0; index < indices.Length; index += 3)
            {
                int aIndex = (int)indices[index];
                int bIndex = (int)indices[index + 1];
                int cIndex = (int)indices[index + 2];
                if (!swinging.IsEmpty && (swinging[aIndex] || swinging[bIndex] || swinging[cIndex]))
                {
                    ignoredSwingingTriangleCount++;
                    continue;
                }
                Point a = new(positions[aIndex]);
                Point b = new(positions[bIndex]);
                Point c = new(positions[cIndex]);
                triangles.Add(new PieceTriangle(a, b, c, Point.Cross(b - a, c - a)));
            }
        }
        return triangles.ToArray();
    }

    readonly record struct PieceTriangle(Point A, Point B, Point C, Point Normal);

    // The ray's frame: the axis of the direction's largest component is the depth axis, and the other two are sheared
    // so the ray runs straight down it. Only the two sheared coordinates are needed for the side tests.
    readonly struct RayFrame
    {
        readonly int x;
        readonly int y;
        readonly int z;
        readonly double shearX;
        readonly double shearY;

        internal RayFrame(Point direction)
        {
            z = Math.Abs(direction.X) >= Math.Abs(direction.Y)
                ? (Math.Abs(direction.X) >= Math.Abs(direction.Z) ? 0 : 2)
                : (Math.Abs(direction.Y) >= Math.Abs(direction.Z) ? 1 : 2);
            x = (z + 1) % 3;
            y = (x + 1) % 3;
            shearX = direction[x] / direction[z];
            shearY = direction[y] / direction[z];
        }

        internal (double X, double Y) Project(Point offset) => (offset[x] - shearX * offset[z], offset[y] - shearY * offset[z]);
    }

    // Double precision, so cross products of float coordinates neither overflow nor underflow, and a normal is zero
    // only for a triangle of truly zero area.
    readonly record struct Point(double X, double Y, double Z)
    {
        public Point(Vector3 value) : this(value.X, value.Y, value.Z) { }

        public double this[int axis] => axis == 0 ? X : axis == 1 ? Y : Z;

        public static Point operator +(Point left, Point right) => new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);

        public static Point operator -(Point left, Point right) => new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);

        public static Point operator /(Point value, double divisor) => new(value.X / divisor, value.Y / divisor, value.Z / divisor);

        public static double Dot(Point left, Point right) => left.X * right.X + left.Y * right.Y + left.Z * right.Z;

        public static Point Cross(Point left, Point right) =>
            new(left.Y * right.Z - left.Z * right.Y, left.Z * right.X - left.X * right.Z, left.X * right.Y - left.Y * right.X);
    }
}
