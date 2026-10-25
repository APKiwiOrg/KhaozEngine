using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal;

/// <summary>Construction-only canonical line table, closed-support bins and version-one scalar packing.</summary>
internal static class WaterOpticsTriangleIndex
{
    private const int ScalarLimit = WaterOpticsTriangleField.MaxDimension * WaterOpticsTriangleField.MaxDimension;

    internal readonly record struct Packed(float[] Scalars, int Width, int Height, int Lines, int References,
        int MaximumOccupancy, float ReconstructionErrorBound);
    private readonly record struct LineKey(BigInteger A, BigInteger B, BigInteger C);

    private sealed class Line
    {
        internal Vector2 Lo, Hi;
        internal int Ordinal;
        internal Line(Vector2 a, Vector2 b)
        {
            Lo = Compare(a, b) <= 0 ? a : b;
            Hi = Compare(a, b) <= 0 ? b : a;
        }
        internal void Include(Vector2 p)
        {
            if (Compare(p, Lo) < 0) Lo = p;
            if (Compare(p, Hi) > 0) Hi = p;
        }
    }

    internal static Packed Build(Vector2 size, int columns, int rows, WaterOpticsTriangle[] sources,
        WaterOpticsRectangle[] certificates)
    {
        int binCount = checked(columns * rows);
        long fixedScalars = checked(16L + 2L * binCount + 12L * sources.Length);
        CheckCapacity(fixedScalars);
        var triangles = new WaterOpticsExactTriangle[sources.Length];
        var lines = new Dictionary<LineKey, Line>();
        for (int i = 0; i < sources.Length; i++)
        {
            triangles[i] = new(sources[i]);
            AddLine(lines, sources[i].A, sources[i].B);
            AddLine(lines, sources[i].B, sources[i].C);
            AddLine(lines, sources[i].C, sources[i].A);
        }
        fixedScalars = checked(fixedScalars + 4L * lines.Count);
        CheckCapacity(fixedScalars);
        var orderedLines = new List<Line>(lines.Values);
        orderedLines.Sort((a, b) => Compare(a.Lo, b.Lo) is int order && order != 0 ? order : Compare(a.Hi, b.Hi));
        for (int i = 0; i < orderedLines.Count; i++) orderedLines[i].Ordinal = i;

        List<int>?[] bins = BuildBins(size, columns, rows, sources, fixedScalars, out int references, out int maximumOccupancy);
        WaterOpticsExact discontinuity = WaterOpticsTriangleCoverage.Validate(triangles, bins, size, columns, rows, certificates);
        float reconstructionError = WaterOpticsTriangleError.Validate(triangles, size, discontinuity);

        int used = checked((int)(fixedScalars + references));
        int width = Math.Min(WaterOpticsTriangleField.MaxDimension, Math.Max(2, used));
        int height = Math.Max(2, checked((used + width - 1) / width));
        if (height > WaterOpticsTriangleField.MaxDimension) CheckCapacity(ScalarLimit + 1L);
        var atlas = new float[checked(width * height)];
        int referenceBase = checked(16 + 2 * binCount);
        int triangleBase = checked(referenceBase + references);
        int edgeBase = checked(triangleBase + 12 * sources.Length);
        atlas[0] = 1;
        atlas[1] = columns;
        atlas[2] = rows;
        atlas[3] = referenceBase;
        atlas[4] = triangleBase;
        atlas[5] = edgeBase;
        atlas[6] = sources.Length;
        atlas[7] = lines.Count;
        atlas[8] = used;
        int cursor = referenceBase;
        for (int i = 0; i < bins.Length; i++)
        {
            atlas[16 + 2 * i] = cursor;
            List<int>? bin = bins[i];
            atlas[17 + 2 * i] = bin?.Count ?? 0;
            if (bin != null) foreach (int ordinal in bin) atlas[cursor++] = ordinal;
        }
        for (int i = 0; i < sources.Length; i++)
        {
            WaterOpticsTriangle t = sources[i];
            int offset = triangleBase + 12 * i;
            Store(atlas, offset, t.A);
            Store(atlas, offset + 3, t.B);
            Store(atlas, offset + 6, t.C);
            atlas[offset + 9] = EdgeReference(lines, t.A, t.B, t.C);
            atlas[offset + 10] = EdgeReference(lines, t.B, t.C, t.A);
            atlas[offset + 11] = EdgeReference(lines, t.C, t.A, t.B);
        }
        foreach (Line line in orderedLines)
        {
            int offset = edgeBase + 4 * line.Ordinal;
            atlas[offset] = line.Lo.X;
            atlas[offset + 1] = line.Lo.Y;
            atlas[offset + 2] = line.Hi.X;
            atlas[offset + 3] = line.Hi.Y;
        }
        return new(atlas, width, height, lines.Count, references, maximumOccupancy, reconstructionError);
    }

    private static List<int>?[] BuildBins(Vector2 size, int columns, int rows, WaterOpticsTriangle[] triangles,
        long fixedScalars, out int references, out int maximumOccupancy)
    {
        var bins = new List<int>?[checked(columns * rows)];
        references = 0;
        maximumOccupancy = 0;
        for (int ordinal = 0; ordinal < triangles.Length; ordinal++)
        {
            WaterOpticsTriangle t = triangles[ordinal];
            float minX = MathF.Min(t.A.X, MathF.Min(t.B.X, t.C.X)), maxX = MathF.Max(t.A.X, MathF.Max(t.B.X, t.C.X));
            float minZ = MathF.Min(t.A.Z, MathF.Min(t.B.Z, t.C.Z)), maxZ = MathF.Max(t.A.Z, MathF.Max(t.B.Z, t.C.Z));
            int x0 = Math.Max(0, (int)(minX / size.X * columns) - 1);
            int x1 = Math.Min(columns - 1, (int)(maxX / size.X * columns) + 1);
            int z0 = Math.Max(0, (int)(minZ / size.Y * rows) - 1);
            int z1 = Math.Min(rows - 1, (int)(maxZ / size.Y * rows) + 1);
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                {
                    Vector2 lo = new(WaterOpticsTriangleMath.BinMinimum(size.X, x, columns), WaterOpticsTriangleMath.BinMinimum(size.Y, z, rows));
                    Vector2 hi = new(WaterOpticsTriangleMath.BinMaximum(size.X, x, columns), WaterOpticsTriangleMath.BinMaximum(size.Y, z, rows));
                    if (!WaterOpticsTriangleMath.Intersects(t, lo, hi)) continue;
                    List<int> bin = bins[z * columns + x] ??= new List<int>();
                    if (bin.Count == WaterOpticsTriangleField.MaxTrianglesPerBin)
                        throw new WaterOpticsSurfaceException(WaterOpticsSurfaceFailure.CapacityExceeded,
                            $"Bin ({x}, {z}) exceeds 32 triangle candidates.");
                    bin.Add(ordinal);
                    references = checked(references + 1);
                    CheckCapacity(checked(fixedScalars + references));
                    maximumOccupancy = Math.Max(maximumOccupancy, bin.Count);
                }
        }
        return bins;
    }

    private static void CheckCapacity(long scalars)
    {
        if (scalars > ScalarLimit)
            throw new WaterOpticsSurfaceException(WaterOpticsSurfaceFailure.CapacityExceeded,
                "The complete triangle atlas exceeds 1024 squared scalar addresses.");
    }

    private static void Store(float[] atlas, int offset, Vector3 vertex)
    {
        atlas[offset] = vertex.X;
        atlas[offset + 1] = vertex.Z;
        atlas[offset + 2] = vertex.Y;
    }

    private static int Compare(Vector2 a, Vector2 b) => a.X.CompareTo(b.X) is int order && order != 0 ? order : a.Y.CompareTo(b.Y);

    private static void AddLine(Dictionary<LineKey, Line> lines, Vector3 a, Vector3 b)
    {
        LineKey key = Key(a, b);
        if (!lines.TryGetValue(key, out Line? line)) lines.Add(key, new(new(a.X, a.Z), new(b.X, b.Z)));
        else
        {
            line.Include(new(a.X, a.Z));
            line.Include(new(b.X, b.Z));
        }
    }

    private static int EdgeReference(Dictionary<LineKey, Line> lines, Vector3 a, Vector3 b, Vector3 opposite)
    {
        Line line = lines[Key(a, b)];
        int sign = WaterOpticsTriangleMath.Orient(line.Lo.X, line.Lo.Y, line.Hi.X, line.Hi.Y, opposite.X, opposite.Z);
        return sign * (line.Ordinal + 1);
    }

    private static LineKey Key(Vector3 a, Vector3 b)
    {
        BigInteger ax = WaterOpticsExact.FloatInteger(a.X), az = WaterOpticsExact.FloatInteger(a.Z);
        BigInteger bx = WaterOpticsExact.FloatInteger(b.X), bz = WaterOpticsExact.FloatInteger(b.Z);
        BigInteger x = az - bz, z = bx - ax, constant = ax * bz - bx * az;
        BigInteger gcd = BigInteger.GreatestCommonDivisor(BigInteger.GreatestCommonDivisor(BigInteger.Abs(x), BigInteger.Abs(z)), BigInteger.Abs(constant));
        x /= gcd;
        z /= gcd;
        constant /= gcd;
        if (x.Sign < 0 || (x.IsZero && z.Sign < 0)) (x, z, constant) = (-x, -z, -constant);
        return new(x, z, constant);
    }
}
