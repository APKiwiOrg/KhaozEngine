using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Attachments;

/// <summary>Plain triangle geometry: positions and a triangle list of indices into them, three per triangle.</summary>
public readonly record struct CoverageClaimMesh(ReadOnlyMemory<Vector3> Positions, ReadOnlyMemory<uint> Indices);

/// <summary>
/// Checks offline that a piece really lies over every body triangle of every region it claims to cover, at the rest
/// pose. A tool runs it when a piece is made. It casts one ray per claimed body triangle against every piece triangle,
/// which suits low poly meshes and is never meant to run per frame.
/// </summary>
public static class CoverageClaimCheck
{
    /// <summary>
    /// Check <paramref name="piece"/>'s covered regions against the body. <paramref name="bodyMeshes"/> follow
    /// <paramref name="body"/>'s part order. <paramref name="pieceMeshes"/> are already in the body's rest frame. A
    /// claimed body triangle is covered when a ray from its centroid along its outward normal, from counter clockwise
    /// winding, meets a piece triangle from either side at a distance from 0 to <paramref name="maxDistanceMetres"/>
    /// inclusive.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The distance is not finite or not greater than zero.</exception>
    /// <exception cref="CoverageClaimDataException">The geometry or the claim breaks a rule.</exception>
    public static CoverageClaimReport Verify(IReadOnlyList<CoverageClaimMesh> bodyMeshes, BodyRegionMap body, AttachmentSetDefinition piece,
        IReadOnlyList<CoverageClaimMesh> pieceMeshes, float maxDistanceMetres)
    {
        ArgumentNullException.ThrowIfNull(bodyMeshes);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(piece);
        ArgumentNullException.ThrowIfNull(pieceMeshes);
        if (!float.IsFinite(maxDistanceMetres) || maxDistanceMetres <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxDistanceMetres), maxDistanceMetres, "The distance must be finite and greater than zero.");

        if (bodyMeshes.Count != body.Parts.Count)
            throw new CoverageClaimDataException(CoverageClaimRefusal.BodyMeshCountMismatch,
                $"The region map has {body.Parts.Count} parts, not {bodyMeshes.Count} body meshes.")
            { ExpectedMeshCount = body.Parts.Count, ActualMeshCount = bodyMeshes.Count };
        for (int part = 0; part < bodyMeshes.Count; part++)
        {
            CheckMesh(CoverageClaimMeshKind.Body, part, bodyMeshes[part]);
            int triangles = bodyMeshes[part].Indices.Length / 3;
            int expected = body.TriangleCount(part);
            if (triangles != expected)
                throw new CoverageClaimDataException(CoverageClaimRefusal.BodyTriangleCountMismatch,
                    $"Body mesh {part} has {triangles} triangles, not the region map part's {expected}.")
                { MeshKind = CoverageClaimMeshKind.Body, MeshIndex = part, ExpectedTriangleCount = expected, ActualTriangleCount = triangles };
        }

        RegionSet claimed = RegionSet.Empty;
        foreach (string name in piece.Covers)
            claimed = claimed.With(body.TryRegion(name, out int region)
                ? region
                : throw new CoverageClaimDataException(CoverageClaimRefusal.RegionUnknown, $"The body has no region '{name}'.") { RegionName = name });

        PieceTriangle[] targets = PieceTriangles(pieceMeshes);
        return Report(bodyMeshes, body, claimed, targets, maxDistanceMetres);
    }

    static CoverageClaimReport Report(IReadOnlyList<CoverageClaimMesh> bodyMeshes, BodyRegionMap body, RegionSet claimed, PieceTriangle[] targets, double maxDistance)
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
        return new CoverageClaimReport(report.ToArray());
    }

    // Moller and Trumbore's ray and triangle test without back face culling, so a piece covers whichever way its
    // triangles wind. A garment's inner surface faces the skin, and that is the surface an outward ray meets first.
    static bool Hits(PieceTriangle[] targets, Point origin, Point direction, double maxDistance)
    {
        foreach (PieceTriangle target in targets)
        {
            Point p = Point.Cross(direction, target.Edge2);
            double determinant = Point.Dot(target.Edge1, p);
            // Zero for a ray in the triangle's plane or a zero area piece triangle, neither of which lies over the skin.
            if (determinant == 0)
                continue;
            Point s = origin - target.Corner;
            double u = Point.Dot(s, p) / determinant;
            if (u < 0 || u > 1)
                continue;
            Point q = Point.Cross(s, target.Edge1);
            double v = Point.Dot(direction, q) / determinant;
            if (v < 0 || u + v > 1)
                continue;
            double distance = Point.Dot(target.Edge2, q) / determinant;
            if (distance >= 0 && distance <= maxDistance)
                return true;
        }
        return false;
    }

    static PieceTriangle[] PieceTriangles(IReadOnlyList<CoverageClaimMesh> pieceMeshes)
    {
        var triangles = new List<PieceTriangle>();
        for (int mesh = 0; mesh < pieceMeshes.Count; mesh++)
        {
            CheckMesh(CoverageClaimMeshKind.Piece, mesh, pieceMeshes[mesh]);
            ReadOnlySpan<Vector3> positions = pieceMeshes[mesh].Positions.Span;
            ReadOnlySpan<uint> indices = pieceMeshes[mesh].Indices.Span;
            for (int index = 0; index < indices.Length; index += 3)
            {
                Point corner = new(positions[(int)indices[index]]);
                triangles.Add(new PieceTriangle(corner, new Point(positions[(int)indices[index + 1]]) - corner, new Point(positions[(int)indices[index + 2]]) - corner));
            }
        }
        return triangles.ToArray();
    }

    static void CheckMesh(CoverageClaimMeshKind kind, int meshIndex, CoverageClaimMesh mesh)
    {
        ReadOnlySpan<Vector3> positions = mesh.Positions.Span;
        ReadOnlySpan<uint> indices = mesh.Indices.Span;
        if (indices.Length % 3 != 0)
            throw new CoverageClaimDataException(CoverageClaimRefusal.IndexCountNotTriangles, $"{kind} mesh {meshIndex}: {indices.Length} indices is not a whole number of triangles.")
            { MeshKind = kind, MeshIndex = meshIndex, IndexCount = indices.Length };
        for (int position = 0; position < indices.Length; position++)
            if (indices[position] >= (uint)positions.Length)
                throw new CoverageClaimDataException(CoverageClaimRefusal.IndexOutOfRange, $"{kind} mesh {meshIndex}: index {position} names vertex {indices[position]} of {positions.Length}.")
                { MeshKind = kind, MeshIndex = meshIndex, IndexPosition = position, VertexIndex = indices[position], VertexCount = positions.Length };
        for (int vertex = 0; vertex < positions.Length; vertex++)
        {
            Vector3 value = positions[vertex];
            if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
                throw new CoverageClaimDataException(CoverageClaimRefusal.PositionNotFinite, $"{kind} mesh {meshIndex}: vertex {vertex} is not finite.")
                { MeshKind = kind, MeshIndex = meshIndex, VertexIndex = (uint)vertex };
        }
    }

    readonly record struct PieceTriangle(Point Corner, Point Edge1, Point Edge2);

    // Double precision, so cross products of float coordinates neither overflow nor underflow, and a normal is zero
    // only for a triangle of truly zero area.
    readonly record struct Point(double X, double Y, double Z)
    {
        public Point(Vector3 value) : this(value.X, value.Y, value.Z) { }

        public static Point operator +(Point left, Point right) => new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);

        public static Point operator -(Point left, Point right) => new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);

        public static Point operator /(Point value, double divisor) => new(value.X / divisor, value.Y / divisor, value.Z / divisor);

        public static double Dot(Point left, Point right) => left.X * right.X + left.Y * right.Y + left.Z * right.Z;

        public static Point Cross(Point left, Point right) =>
            new(left.Y * right.Z - left.Z * right.Y, left.Z * right.X - left.X * right.Z, left.X * right.Y - left.Y * right.X);
    }
}
