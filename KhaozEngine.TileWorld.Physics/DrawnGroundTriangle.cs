using System;
using System.Numerics;

namespace KhaozEngine.TileWorld.Physics;

/// <summary>The drawn lattice triangle and its one selection and height evaluation rule.</summary>
internal readonly record struct DrawnGroundTriangle(TileLatticePoint PlanA, TileLatticePoint PlanB,
    TileLatticePoint PlanC, Vector3 A, Vector3 B, Vector3 C)
{
    internal Vector3 Weights(Vector2 point)
    {
        var (b, c) = DrawnGroundEvaluation.Weights(DrawnGroundEvaluation.Scalar.From(point.X),
            DrawnGroundEvaluation.Scalar.From(point.Y), this);
        return new(1f - b.Value - c.Value, b.Value, c.Value);
    }

    internal float Height(Vector3 weights) => DrawnGroundEvaluation.Height(
        DrawnGroundEvaluation.Scalar.From(weights.Y), DrawnGroundEvaluation.Scalar.From(weights.Z), this).Value;

    internal Vector3 Normal
    {
        get
        {
            Vector3 n = Vector3.Cross(B - A, C - A);
            return Vector3.Normalize(n.Y < 0 ? -n : n);
        }
    }

    internal static int Describe(TileWorldDocument document, int x, int z, Span<DrawnGroundTriangle> output)
    {
        Span<TileLatticeTriangle> layout = stackalloc TileLatticeTriangle[TileTriangulation.MaxTriangles];
        if (!TileGroundTriangles.TryDescribe(document, x, z, 0, out var cell, layout)) return 0;
        for (int i = 0; i < cell.TriangleCount; i++)
        {
            TileLatticeTriangle t = layout[i];
            output[i] = new(t.A, t.B, t.C,
                TileGroundTriangles.LatticePosition(document, x, z, 0, t.A, x, z),
                TileGroundTriangles.LatticePosition(document, x, z, 0, t.B, x, z),
                TileGroundTriangles.LatticePosition(document, x, z, 0, t.C, x, z));
        }
        return cell.TriangleCount;
    }

    internal static int Select(ReadOnlySpan<DrawnGroundTriangle> triangles, Vector2 point, out Vector3 weights)
    {
        int best = -1;
        float bestMinimum = float.NegativeInfinity;
        weights = default;
        for (int i = 0; i < triangles.Length; i++)
        {
            Vector3 candidate = triangles[i].Weights(point);
            float minimum = MathF.Min(candidate.X, MathF.Min(candidate.Y, candidate.Z));
            if (minimum <= bestMinimum) continue;
            best = i;
            bestMinimum = minimum;
            weights = candidate;
        }
        return best;
    }
}
