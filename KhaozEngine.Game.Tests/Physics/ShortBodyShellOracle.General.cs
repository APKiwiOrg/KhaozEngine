using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Tests.Physics;

internal static partial class ShortBodyShellOracle
{
    // GJK proposes target barycentrics and a separating direction only. Exact independent
    // continuous support and attained point/meridian distance certify both ends of the result.
    // No proposed convergence or failed iteration can publish an expected distance.
    static ConvexOracleEvent GeneralEntry(in ConvexQueryShape shape, Pose pose, ConvexOracleTarget target, bool separationOnly = false)
    {
        var exact = new R[target.Vertices.Length][];
        var numeric = new D3[exact.Length];
        for (int i = 0; i < exact.Length; i++)
        {
            var v = target.Vertices[i];
            exact[i] = [new R(v.X.Numerator, v.X.Denominator) - R.From(pose.Position.X),
                new R(v.Y.Numerator, v.Y.Denominator) - R.From(pose.Position.Y),
                new R(v.Z.Numerator, v.Z.Denominator) - R.From(pose.Position.Z)];
            numeric[i] = new(ToDouble(exact[i][0]), ToDouble(exact[i][1]), ToDouble(exact[i][2]));
        }
        R faceLower = R.Zero;
        foreach (var face in target.Faces)
        {
            R[] a = exact[face[0]], b = exact[face[1]], c = exact[face[^1]];
            R[] u = [b[0] - a[0], b[1] - a[1], b[2] - a[2]], v = [c[0] - a[0], c[1] - a[1], c[2] - a[2]];
            R[] n = [u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0]];
            faceLower = R.Max(faceLower, SeparationLower(shape, exact, n));
            faceLower = R.Max(faceLower, SeparationLower(shape, exact, [-n[0], -n[1], -n[2]]));
        }
        D3 direction = new(1, 0, 0);
        var simplex = new List<OracleVertex> { Propose(shape, numeric, direction) };
        for (int iteration = 0; iteration < 256; iteration++)
        {
            if (!Closest(simplex, out var weights, out direction)) return default;
            var certificate = Bound(shape, exact, simplex, weights, direction, faceLower);
            if (certificate.IsComplete && ((separationOnly && certificate.Lower > 0) ||
                SingleBracket(certificate.Lower) == SingleBracket(certificate.Upper)))
                return certificate;
            var reduced = new List<OracleVertex>();
            for (int i = 0; i < simplex.Count; i++) if (weights[i] > 0) reduced.Add(simplex[i]);
            OracleVertex next = Propose(shape, numeric, direction);
            if (reduced.Contains(next) || reduced.Count == 4) return default;
            reduced.Add(next);
            simplex = reduced;
        }
        return default;
    }

    static double ToDouble(R r) => (double)r.N / (double)r.D;
    // Finish only when the independent interval fits one binary32 encoding bin. This uses
    // representability rather than an additional distance tolerance or the production answer.
    static (float Lower, float Upper) SingleBracket(double value)
    {
        float rounded = (float)value;
        return (rounded > value ? MathF.BitDecrement(rounded) : rounded,
            rounded < value ? MathF.BitIncrement(rounded) : rounded);
    }
    readonly record struct D3(double X, double Y, double Z)
    {
        public static D3 operator +(D3 a, D3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static D3 operator -(D3 a, D3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static D3 operator *(D3 a, double b) => new(a.X * b, a.Y * b, a.Z * b);
        internal static double Dot(D3 a, D3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    }
    readonly record struct OracleVertex(D3 Difference, int Target);

    static OracleVertex Propose(in ConvexQueryShape shape, D3[] vertices, D3 n)
    {
        int target = 0;
        for (int i = 1; i < vertices.Length; i++)
            if (D3.Dot(vertices[i], n) < D3.Dot(vertices[target], n)) target = i;
        double radial = Math.Sqrt(n.X * n.X + n.Z * n.Z);
        double y = n.Y > 0 ? shape.UpperY : shape.RingY;
        double support = y * n.Y + shape.Radius * radial;
        D3 query = radial == 0 ? new(0, y, 0) : new(n.X * shape.Radius / radial, y, n.Z * shape.Radius / radial);
        if (shape.LowerY * n.Y > support) query = new(0, shape.LowerY, 0);
        return new(vertices[target] - query, target);
    }

    static ConvexOracleEvent Bound(in ConvexQueryShape shape, R[][] vertices, List<OracleVertex> simplex, double[] weights, D3 direction, R faceLower)
    {
        R sum = R.Zero;
        R[] t = [R.Zero, R.Zero, R.Zero];
        for (int i = 0; i < weights.Length; i++)
        {
            if (!double.IsFinite(weights[i]) || weights[i] < 0) return default;
            R w = R.From(weights[i]); sum += w;
            for (int j = 0; j < 3; j++) t[j] += vertices[simplex[i].Target][j] * w;
        }
        if (sum.N.Sign <= 0) return default;
        for (int j = 0; j < 3; j++) t[j] /= sum;
        I primal = MeridianDistance(shape, t[0], t[1], t[2]);
        R margin = R.From(shape.Margin), upper = R.Max(R.Zero, primal.Upper - margin);
        if (upper.N.IsZero) return new(true, 0, 0);
        R[] n = [R.From(direction.X), R.From(direction.Y), R.From(direction.Z)];
        R lower = R.Max(faceLower, SeparationLower(shape, vertices, n));
        if (lower.CompareTo(upper) > 0) return default;
        var bounds = Outward(new(lower, upper));
        return new(true, bounds.Lower, bounds.Upper);
    }

    static R SeparationLower(in ConvexQueryShape shape, R[][] vertices, R[] n)
    {
        R norm2 = n[0] * n[0] + n[1] * n[1] + n[2] * n[2];
        if (norm2.N.IsZero) return R.Zero;
        I norm = Root(norm2), radial = Root(n[0] * n[0] + n[2] * n[2]) * R.From(shape.Radius);
        I a = I.Point(R.From(shape.LowerY) * n[1]);
        I b = I.Point(R.From(shape.RingY) * n[1]) + radial;
        I c = I.Point(R.From(shape.UpperY) * n[1]) + radial;
        I support = new(R.Max(a.Lower, R.Max(b.Lower, c.Lower)), R.Max(a.Upper, R.Max(b.Upper, c.Upper)));
        R Dot(R[] p) => p[0] * n[0] + p[1] * n[1] + p[2] * n[2];
        R minimum = Dot(vertices[0]);
        foreach (var v in vertices) minimum = R.Min(minimum, Dot(v));
        R gap = minimum - support.Upper - R.From(shape.Margin) * norm.Upper;
        return gap.N.Sign > 0 ? gap / norm.Upper : R.Zero;
    }

    static bool Closest(List<OracleVertex> vertices, out double[] weights, out D3 point)
    {
        weights = new double[vertices.Count]; point = default;
        double best = double.PositiveInfinity;
        for (int mask = 1; mask < (1 << vertices.Count); mask++)
        {
            var ids = new List<int>();
            for (int i = 0; i < vertices.Count; i++) if ((mask & (1 << i)) != 0) ids.Add(i);
            if (ids.Count > 4) continue;
            D3 a = vertices[ids[0]].Difference;
            int count = ids.Count - 1;
            var matrix = new double[count, count + 1];
            for (int i = 0; i < count; i++)
            {
                D3 di = vertices[ids[i + 1]].Difference - a;
                for (int j = 0; j < count; j++) matrix[i, j] = D3.Dot(di, vertices[ids[j + 1]].Difference - a);
                matrix[i, count] = -D3.Dot(di, a);
            }
            if (!Solve(matrix, count, out var coordinates)) continue;
            double first = 1;
            D3 candidate = a;
            for (int i = 0; i < count; i++) { first -= coordinates[i]; candidate += (vertices[ids[i + 1]].Difference - a) * coordinates[i]; }
            if (first < 0 || Array.Exists(coordinates, v => v < 0)) continue;
            double square = D3.Dot(candidate, candidate);
            if (!(square < best)) continue;
            best = square; point = candidate;
            Array.Clear(weights); weights[ids[0]] = first;
            for (int i = 0; i < count; i++) weights[ids[i + 1]] = coordinates[i];
        }
        return double.IsFinite(best);
    }

    static bool Solve(double[,] matrix, int count, out double[] result)
    {
        result = new double[count];
        for (int i = 0; i < count; i++)
        {
            int pivot = i;
            for (int j = i + 1; j < count; j++) if (Math.Abs(matrix[j, i]) > Math.Abs(matrix[pivot, i])) pivot = j;
            if (matrix[pivot, i] == 0) return false;
            for (int j = i; j <= count; j++) (matrix[i, j], matrix[pivot, j]) = (matrix[pivot, j], matrix[i, j]);
            double divisor = matrix[i, i];
            for (int j = i; j <= count; j++) matrix[i, j] /= divisor;
            for (int row = 0; row < count; row++)
            {
                if (row == i) continue;
                double scale = matrix[row, i];
                for (int j = i; j <= count; j++) matrix[row, j] -= scale * matrix[i, j];
            }
        }
        for (int i = 0; i < count; i++) result[i] = matrix[i, count];
        return Array.TrueForAll(result, double.IsFinite);
    }
}
