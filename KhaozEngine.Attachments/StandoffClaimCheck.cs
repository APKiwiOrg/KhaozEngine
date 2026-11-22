using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Attachments;

/// <summary>Checks a piece's regional standoff claim offline at the body's rest pose.</summary>
public static class StandoffClaimCheck
{
    const double NearestDistanceToleranceMetres = 0.000001;

    /// <summary>
    /// Finds the nearest nondegenerate body triangle for each fixed piece vertex. It supplies the region and height,
    /// which is the signed distance in metres from its plane along its outward normal.
    /// Body meshes follow the region map's part order. Piece meshes are already in the body's rest frame.
    /// For a piece with motion, supply one mesh per definition part in any order with its
    /// <see cref="CoverageClaimMesh.PartId"/> and a swinging mask for each part with motion.
    /// </summary>
    /// <remarks>
    /// Triangles within one micrometre of the nearest distance compete by height. Equal heights choose the first
    /// triangle in body part and triangle order. A body without nondegenerate triangles yields no measurements.
    /// </remarks>
    /// <exception cref="ArgumentException">A nonempty swinging mask has a different length than its mesh's positions.</exception>
    /// <exception cref="CoverageClaimDataException">The geometry or the claim breaks a rule.</exception>
    public static StandoffClaimReport Verify(IReadOnlyList<CoverageClaimMesh> bodyMeshes, BodyRegionMap body,
        AttachmentSetDefinition piece, IReadOnlyList<CoverageClaimMesh> pieceMeshes)
    {
        CoverageClaimValidation.CheckArguments(bodyMeshes, body, piece, pieceMeshes);
        CoverageClaimValidation.CheckBody(bodyMeshes, body);
        CoverageClaimValidation.CheckCoveredRegions(body, piece);

        var declared = new float?[body.Regions.Count];
        foreach (RegionStandoff entry in piece.Standoff)
            declared[CoverageClaimValidation.ResolveRegion(body, entry.Region)] = entry.Metres;

        CoverageClaimSwingEvidence.Validate(piece, pieceMeshes);
        for (int mesh = 0; mesh < pieceMeshes.Count; mesh++)
            CoverageClaimValidation.CheckPieceMesh(pieceMeshes, mesh);

        BodyTriangle[] triangles = BodyTriangles(bodyMeshes, body);
        if (triangles.Length == 0)
            return new StandoffClaimReport([]);

        var counts = new int[declared.Length];
        var highest = new double[declared.Length];
        var measurements = new (double DistanceSquared, double Height)[triangles.Length];
        foreach (CoverageClaimMesh mesh in pieceMeshes)
        {
            ReadOnlySpan<Vector3> positions = mesh.Positions.Span;
            ReadOnlySpan<bool> swinging = mesh.Swinging.Span;
            for (int vertex = 0; vertex < positions.Length; vertex++)
            {
                if (!swinging.IsEmpty && swinging[vertex])
                    continue;

                (int region, double height) = MeasureNearest(new Point(positions[vertex]), triangles, measurements);
                if (counts[region] == 0 || height > highest[region])
                    highest[region] = height;
                counts[region]++;
            }
        }

        var regions = new List<StandoffClaimRegion>();
        for (int region = 0; region < counts.Length; region++)
            if (counts[region] > 0)
                regions.Add(new StandoffClaimRegion(body.Regions[region], highest[region], counts[region], declared[region]));
        return new StandoffClaimReport(regions.ToArray());
    }

    static BodyTriangle[] BodyTriangles(IReadOnlyList<CoverageClaimMesh> bodyMeshes, BodyRegionMap body)
    {
        var triangles = new List<BodyTriangle>();
        for (int part = 0; part < bodyMeshes.Count; part++)
        {
            ReadOnlySpan<Vector3> positions = bodyMeshes[part].Positions.Span;
            ReadOnlySpan<uint> indices = bodyMeshes[part].Indices.Span;
            var regions = new byte[indices.Length / 3];
            body.WriteTriangleRegions(part, regions);
            for (int triangle = 0; triangle < regions.Length; triangle++)
            {
                int index = triangle * 3;
                var face = new BodyTriangle(regions[triangle], new Point(positions[(int)indices[index]]),
                    new Point(positions[(int)indices[index + 1]]), new Point(positions[(int)indices[index + 2]]));
                if (face.HasNormal) triangles.Add(face);
            }
        }
        return triangles.ToArray();
    }

    static (int Region, double Height) MeasureNearest(Point vertex, ReadOnlySpan<BodyTriangle> triangles,
        Span<(double DistanceSquared, double Height)> measurements)
    {
        double nearestSquared = double.PositiveInfinity;
        for (int triangle = 0; triangle < triangles.Length; triangle++)
        {
            measurements[triangle] = triangles[triangle].Measure(vertex);
            nearestSquared = Math.Min(nearestSquared, measurements[triangle].DistanceSquared);
        }

        // Measure the tolerance from the true nearest distance to avoid ties chaining across multiple faces.
        double distanceSlackSquared = NearestDistanceToleranceMetres
            * (2 * Math.Sqrt(nearestSquared) + NearestDistanceToleranceMetres);
        int region = 0;
        double height = double.NegativeInfinity;
        for (int triangle = 0; triangle < triangles.Length; triangle++)
        {
            (double distanceSquared, double candidateHeight) = measurements[triangle];
            if (distanceSquared - nearestSquared > distanceSlackSquared || candidateHeight <= height)
                continue;

            height = candidateHeight;
            region = triangles[triangle].Region;
        }
        return (region, height);
    }

    readonly struct BodyTriangle
    {
        readonly Point a;
        readonly Point b;
        readonly Point c;
        readonly Point ab;
        readonly Point bc;
        readonly Point ca;
        readonly Point normal;
        readonly double normalLength;

        internal BodyTriangle(int region, Point a, Point b, Point c)
        {
            Region = region;
            this.a = a;
            this.b = b;
            this.c = c;
            ab = b - a;
            bc = c - b;
            ca = a - c;
            normal = Point.Cross(ab, c - a);
            normalLength = Math.Sqrt(Point.Dot(normal, normal));
        }

        internal int Region { get; }
        internal bool HasNormal => normalLength > 0;

        internal (double DistanceSquared, double Height) Measure(Point vertex)
        {
            Point fromA = vertex - a;
            Point fromB = vertex - b;
            Point fromC = vertex - c;
            double height = Point.Dot(fromA, normal) / normalLength;

            // The edge tests only see the projection into the triangle's plane. If it falls outside,
            // the closest point lies on one of the closed edge segments.
            if (OnInnerSide(ab, fromA) && OnInnerSide(bc, fromB) && OnInnerSide(ca, fromC))
                return (height * height, height);

            double distanceSquared = Math.Min(SegmentDistanceSquared(ab, fromA, fromB),
                Math.Min(SegmentDistanceSquared(bc, fromB, fromC), SegmentDistanceSquared(ca, fromC, fromA)));
            return (distanceSquared, height);
        }

        bool OnInnerSide(Point edge, Point offset) => Point.Dot(Point.Cross(edge, offset), normal) >= 0;

        static double SegmentDistanceSquared(Point edge, Point fromStart, Point fromEnd)
        {
            double along = Point.Dot(edge, fromStart);
            if (along <= 0)
                return Point.Dot(fromStart, fromStart);

            double lengthSquared = Point.Dot(edge, edge);
            if (along >= lengthSquared)
                return Point.Dot(fromEnd, fromEnd);

            Point offset = fromStart - edge * (along / lengthSquared);
            return Point.Dot(offset, offset);
        }
    }

    readonly record struct Point(double X, double Y, double Z)
    {
        internal Point(Vector3 value) : this(value.X, value.Y, value.Z) { }

        public static Point operator -(Point left, Point right) => new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);

        public static Point operator *(Point value, double scalar) => new(value.X * scalar, value.Y * scalar, value.Z * scalar);

        internal static double Dot(Point left, Point right) => left.X * right.X + left.Y * right.Y + left.Z * right.Z;

        internal static Point Cross(Point left, Point right) =>
            new(left.Y * right.Z - left.Z * right.Y, left.Z * right.X - left.X * right.Z, left.X * right.Y - left.Y * right.X);
    }
}
