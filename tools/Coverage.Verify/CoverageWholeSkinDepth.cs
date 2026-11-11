using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tools.Coverage;

/// <summary>World millimetres. UpperEstimateMm is a finite-budget numerical estimate, not an outward-rounded
/// enclosure. Resolved means the estimate differs from the observation by at most the search resolution.</summary>
public readonly record struct CoverageDepthResult(double ObservedMm, double UpperEstimateMm, int Queries, bool Resolved);

/// <summary>Unbanded adaptive search of complete triangle interiors against a validated closed equipment solid.
/// Double winding and distances are sampled evidence with arithmetic limitations, independent of exact certification.</summary>
public sealed class CoverageWholeSkinDepth
{
    public const int DefaultQueryBudget = 4096;
    public const double ResolutionMm = .1;
    readonly CoverageSourceTopology source;
    readonly record struct Triangle(CoverageVector A, CoverageVector B, CoverageVector C);
    readonly record struct Work(Triangle Triangle, double Upper, bool Sampled);

    public CoverageWholeSkinDepth(CoverageSupportSurface support)
    {
        ArgumentNullException.ThrowIfNull(support);
        if (!support.InwardOfClosedSolid)
            throw new CoverageVerificationException(CoverageVerificationRefusal.OpenEquipmentSolid, "Whole-skin depth requires closed equipment geometry.");
        source = support.Source;
    }

    /// <summary>Measures every included triangle, including uncommanded and attributes-only changed triangles.
    /// Both surfaces are transformed to world metres. The query budget is inclusive and never exceeded.
    /// Empty classes resolve zero. Unqueried work retains an upper estimate and cannot silently pass.</summary>
    public CoverageDepthResult Measure(ReadOnlySpan<uint> indices, ReadOnlySpan<ModelVertex> skin,
        Matrix4x4 actorModel, Matrix4x4 equipmentWorld, ReadOnlySpan<bool> include, int queryBudget = DefaultQueryBudget)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queryBudget);
        if (indices.Length % 3 != 0 || include.Length != indices.Length / 3)
            throw new ArgumentException("Depth requires complete triangle indices and an exact class mask.");
        if (!CoverageAffine.TryFrom(actorModel, out var actor) || !actor.TryInverse(out _)
            || !CoverageAffine.TryFrom(equipmentWorld, out var equipment) || !equipment.TryInverse(out _)) throw Arithmetic();
        var solid = new Triangle[source.Triangles.Count];
        var minimum = new CoverageVector(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        var maximum = new CoverageVector(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
        for (int i = 0; i < solid.Length; i++)
        {
            var face = source.Triangles[i];
            var triangle = new Triangle(equipment.Apply(source.Positions[face.A]), equipment.Apply(source.Positions[face.B]), equipment.Apply(source.Positions[face.C]));
            Validate(triangle);
            solid[i] = triangle;
            minimum = Min(minimum, Min(triangle.A, Min(triangle.B, triangle.C)));
            maximum = Max(maximum, Max(triangle.A, Max(triangle.B, triangle.C)));
        }
        double diameter = (maximum - minimum).Length;
        if (!(diameter > 0) || !double.IsFinite(diameter)) throw Arithmetic();
        var pending = new PriorityQueue<Work, double>();
        int queries = 0;
        double observed = 0;
        for (int i = 0; i < include.Length; i++)
        {
            if (!include[i]) continue;
            uint a = indices[i * 3], b = indices[i * 3 + 1], c = indices[i * 3 + 2];
            if (a >= skin.Length || b >= skin.Length || c >= skin.Length)
                throw new ArgumentException("Triangle index is outside the supplied records.", nameof(indices));
            Add(new(actor.Apply(skin[(int)a].Position), actor.Apply(skin[(int)b].Position), actor.Apply(skin[(int)c].Position)), diameter);
        }
        while (queries < queryBudget && pending.TryPeek(out var next, out _) && next.Upper > observed + ResolutionMm / 1000)
        {
            pending.Dequeue();
            var t = next.Triangle;
            if (!next.Sampled) { Add(t, next.Upper); continue; }
            // Bisect the longest edge. This covers the entire parent and avoids growing four children per query.
            double ab = (t.A - t.B).Length, bc = (t.B - t.C).Length, ca = (t.C - t.A).Length;
            if (bc > ab && bc >= ca) t = new(t.B, t.C, t.A);
            else if (ca > ab) t = new(t.C, t.A, t.B);
            var midpoint = (t.A + t.B) * .5;
            Add(new(t.A, midpoint, t.C), next.Upper);
            Add(new(midpoint, t.B, t.C), next.Upper);
        }
        double upper = pending.TryPeek(out var remaining, out _) ? Math.Max(observed, remaining.Upper) : observed;
        if (!double.IsFinite(observed * 1000) || !double.IsFinite(upper * 1000)) throw Arithmetic();
        return new(observed * 1000, upper * 1000, queries, upper - observed <= ResolutionMm / 1000);

        void Add(Triangle triangle, double parentUpper)
        {
            Validate(triangle);
            var low = Min(triangle.A, Min(triangle.B, triangle.C));
            var high = Max(triangle.A, Max(triangle.B, triangle.C));
            if (Disjoint(low, high, minimum, maximum)) return;
            if (queries == queryBudget) { Enqueue(new(triangle, parentUpper, false)); return; }
            var center = (triangle.A + triangle.B + triangle.C) * (1d / 3);
            double signed = SignedDepth(center, solid);
            queries++;
            if (signed <= 0 && BoundarySeparated(triangle, solid)) return;
            observed = Math.Max(observed, Math.Max(0, signed));
            double radius = Math.Max((center - triangle.A).Length, Math.Max((center - triangle.B).Length, (center - triangle.C).Length));
            double bound = Math.Min(parentUpper, Math.Max(0, signed + radius));
            if (!double.IsFinite(bound)) throw Arithmetic();
            Enqueue(new(triangle, bound, true));
        }
        void Enqueue(Work work) => pending.Enqueue(work, -work.Upper);
    }

    static double SignedDepth(CoverageVector point, Triangle[] solid)
    {
        double angle = 0, nearest = double.PositiveInfinity;
        foreach (var triangle in solid)
        {
            var closest = CoverageTriangleGeometry.ClosestOnTriangle(point, triangle.A, triangle.B, triangle.C);
            double distance = (point - closest).Length;
            if (distance == 0) return 0;
            nearest = Math.Min(nearest, distance);
            angle += CoverageTriangleGeometry.SolidAngle(triangle.A - point, triangle.B - point, triangle.C - point);
        }
        if (!double.IsFinite(angle) || !double.IsFinite(nearest)) throw Arithmetic();
        return Math.Abs(angle) > 2 * Math.PI ? nearest : -nearest;
    }

    static bool BoundarySeparated(Triangle skin, Triangle[] solid)
    {
        foreach (var triangle in solid)
            if (!(CoverageTriangleGeometry.SeparationLowerBound(skin.A, skin.B, skin.C, triangle.A, triangle.B, triangle.C) > 0)) return false;
        return true;
    }
    static CoverageVector Min(CoverageVector a, CoverageVector b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
    static CoverageVector Max(CoverageVector a, CoverageVector b) => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));
    static bool Disjoint(CoverageVector low, CoverageVector high, CoverageVector min, CoverageVector max) =>
        low.X > max.X || high.X < min.X || low.Y > max.Y || high.Y < min.Y || low.Z > max.Z || high.Z < min.Z;
    static void Validate(Triangle triangle)
    {
        if (!double.IsFinite(triangle.A.MaxAbs) || !double.IsFinite(triangle.B.MaxAbs) || !double.IsFinite(triangle.C.MaxAbs)) throw Arithmetic();
    }
    static CoverageVerificationException Arithmetic() => new(CoverageVerificationRefusal.DepthArithmetic, "Whole-skin arithmetic is nonfinite or unsupported.");
}
