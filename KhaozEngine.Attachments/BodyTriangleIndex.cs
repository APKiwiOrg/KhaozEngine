using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Attachments;

internal readonly record struct BodyTriangleHit(int Triangle, int Region, double HeightMetres, double W1, double W2);

// A bounding-volume hierarchy over the nondegenerate body triangles. A face belongs to exactly one leaf,
// even when it spans the split. The public claim rule uses original mesh order, independent of tree order.
internal sealed class BodyTriangleIndex
{
    const int LeafSize = 4;
    const double TieMetres = 0.000001;

    readonly ClaimBodyTriangle[] triangles;
    readonly Node? root;

    internal BodyTriangleIndex(IReadOnlyList<CoverageClaimMesh> meshes, BodyRegionMap body)
    {
        var faces = new List<ClaimBodyTriangle>();
        int flattened = 0;
        for (int part = 0; part < meshes.Count; part++)
        {
            ReadOnlySpan<Vector3> positions = meshes[part].Positions.Span;
            ReadOnlySpan<uint> indices = meshes[part].Indices.Span;
            var regions = new byte[indices.Length / 3];
            body.WriteTriangleRegions(part, regions);
            for (int triangle = 0; triangle < regions.Length; triangle++, flattened++)
            {
                int index = triangle * 3;
                var face = new ClaimBodyTriangle(flattened, regions[triangle],
                    new ClaimPoint(positions[(int)indices[index]]),
                    new ClaimPoint(positions[(int)indices[index + 1]]),
                    new ClaimPoint(positions[(int)indices[index + 2]]));
                if (face.HasNormal)
                    faces.Add(face);
            }
        }

        triangles = faces.ToArray();
        if (triangles.Length > 0)
            root = Build(0, triangles.Length);
    }

    internal bool HasTriangles => root is not null;

    internal Dictionary<int, Vector3> FaceNormals(IReadOnlySet<int> requested)
    {
        var normals = new Dictionary<int, Vector3>(requested.Count);
        if (requested.Count == 0)
            return normals;

        foreach (ref readonly ClaimBodyTriangle face in triangles.AsSpan())
            if (requested.Contains(face.Index))
                normals.Add(face.Index, face.UnitNormal);
        return normals;
    }

    internal BodyTriangleHit Nearest(ClaimPoint point)
    {
        Node tree = root ?? throw new InvalidOperationException("The body has no nondegenerate triangles.");
        double nearestSquared = double.PositiveInfinity;
        FindDistance(tree, point, tree.Bounds.LowerBoundSquared(point), ref nearestSquared);

        // Start from the true nearest distance. Updating this window from a height winner could chain ties
        // across successively farther faces.
        double slackSquared = TieMetres * (2 * Math.Sqrt(nearestSquared) + TieMetres);
        BodyTriangleHit best = new(int.MaxValue, 0, double.NegativeInfinity, 0, 0);
        FindHighest(tree, point, nearestSquared, slackSquared, ref best);
        return best;
    }

    Node Build(int start, int count)
    {
        Bounds bounds = Bounds.For(triangles.AsSpan(start, count));
        if (count <= LeafSize)
            return new(bounds, start, count, null, null);

        ClaimPoint extent = bounds.Max - bounds.Min;
        IComparer<ClaimBodyTriangle> order = extent.X >= extent.Y && extent.X >= extent.Z ? OrderX
            : extent.Y >= extent.Z ? OrderY : OrderZ;
        Array.Sort(triangles, start, count, order);
        int leftCount = count / 2;
        return new(bounds, start, count, Build(start, leftCount), Build(start + leftCount, count - leftCount));
    }

    void FindDistance(Node node, ClaimPoint point, double lowerBound, ref double nearestSquared)
    {
        if (lowerBound > nearestSquared)
            return;

        if (node.Left is { } left && node.Right is { } right)
        {
            double leftBound = left.Bounds.LowerBoundSquared(point);
            double rightBound = right.Bounds.LowerBoundSquared(point);
            if (rightBound < leftBound)
            {
                (left, right) = (right, left);
                (leftBound, rightBound) = (rightBound, leftBound);
            }
            FindDistance(left, point, leftBound, ref nearestSquared);
            FindDistance(right, point, rightBound, ref nearestSquared);
            return;
        }

        int end = node.Start + node.Count;
        for (int i = node.Start; i < end; i++)
            nearestSquared = Math.Min(nearestSquared, triangles[i].Project(point).DistanceSquared);
    }

    void FindHighest(Node node, ClaimPoint point, double nearestSquared, double slackSquared, ref BodyTriangleHit best)
    {
        if (node.Bounds.LowerBoundSquared(point) - nearestSquared > slackSquared)
            return;

        if (node.Left is { } left && node.Right is { } right)
        {
            FindHighest(left, point, nearestSquared, slackSquared, ref best);
            FindHighest(right, point, nearestSquared, slackSquared, ref best);
            return;
        }

        int end = node.Start + node.Count;
        for (int i = node.Start; i < end; i++)
        {
            ref readonly ClaimBodyTriangle triangle = ref triangles[i];
            BodyTriangleProjection hit = triangle.Project(point);
            if (hit.DistanceSquared - nearestSquared > slackSquared || hit.HeightMetres < best.HeightMetres ||
                (hit.HeightMetres == best.HeightMetres && triangle.Index >= best.Triangle))
                continue;

            best = new(triangle.Index, triangle.Region, hit.HeightMetres, hit.W1, hit.W2);
        }
    }

    static readonly IComparer<ClaimBodyTriangle> OrderX = MidpointOrder(triangle => triangle.Min.X + triangle.Max.X);
    static readonly IComparer<ClaimBodyTriangle> OrderY = MidpointOrder(triangle => triangle.Min.Y + triangle.Max.Y);
    static readonly IComparer<ClaimBodyTriangle> OrderZ = MidpointOrder(triangle => triangle.Min.Z + triangle.Max.Z);

    static IComparer<ClaimBodyTriangle> MidpointOrder(Func<ClaimBodyTriangle, double> midpointTwice) =>
        Comparer<ClaimBodyTriangle>.Create((a, b) =>
        {
            int compare = midpointTwice(a).CompareTo(midpointTwice(b));
            return compare != 0 ? compare : a.Index.CompareTo(b.Index);
        });

    sealed record Node(Bounds Bounds, int Start, int Count, Node? Left, Node? Right);

    readonly record struct Bounds(ClaimPoint Min, ClaimPoint Max, double Roundoff)
    {
        const double MachineEpsilon = 2.2204460492503131e-16;

        internal static Bounds For(ReadOnlySpan<ClaimBodyTriangle> faces)
        {
            ClaimPoint min = faces[0].Min;
            ClaimPoint max = faces[0].Max;
            double condition = 1;
            foreach (ref readonly ClaimBodyTriangle face in faces)
            {
                min = ClaimPoint.Min(min, face.Min);
                max = ClaimPoint.Max(max, face.Max);
                condition = Math.Max(condition, face.ShapeCondition);
            }

            return new(min, max, 128 * MachineEpsilon * condition * condition);
        }

        internal double LowerBoundSquared(ClaimPoint point)
        {
            double x = AxisDistance(point.X, Min.X, Max.X);
            double y = AxisDistance(point.Y, Min.Y, Max.Y);
            double z = AxisDistance(point.Z, Min.Z, Max.Z);

            // Projection uses both the normal and oriented edge tests. Pad the geometric lower bound
            // for their roundoff at the query's scale and the worst triangle shape in this node.
            // Very ill-conditioned faces naturally get a zero bound and are still measured.
            ClaimPoint extent = Max - Min;
            double padding = Roundoff * ((x + extent.X) + (y + extent.Y) + (z + extent.Z));
            x = Math.Max(0, x - padding);
            y = Math.Max(0, y - padding);
            z = Math.Max(0, z - padding);
            return x * x + y * y + z * z;
        }

        static double AxisDistance(double value, double min, double max) =>
            value < min ? min - value : Math.Max(0, value - max);
    }
}
