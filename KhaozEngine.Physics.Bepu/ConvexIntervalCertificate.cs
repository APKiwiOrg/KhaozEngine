using System;
using System.Numerics;
using Q = KhaozEngine.Physics.Bepu.ConvexQuadratic;
using P = KhaozEngine.Physics.Bepu.ConvexExactPoint;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Fixed-plane separation of the complete continuous shell over a closed translated
/// interval. A positive affine endpoint gap proves every fraction between those endpoints.</summary>
internal static class ConvexIntervalCertificate
{
    internal static bool TryCreate(in ConvexQueryRequest request, in InstalledSweepLeaf leaf, Vector3 direction,
        double from, double to, ref ConvexQueryWork work, ConvexQueryScratch scratch, out ConvexPlaneCertificate certificate)
    {
        certificate = default;
        if (scratch is null || !ConvexQueryPolicy.Contains(request) || work.Exhausted || work.Descendants == 0 ||
            !ValidInterval(from, to) || !InstalledPoseOperator.Within(direction, float.MaxValue) || direction == Vector3.Zero ||
            !ConvexPlanarSource.TryCreate(leaf, out var source)) return false;
        try
        {
            if (!work.Cell()) return false;
            P n = P.From(direction);
            Q minimum = default;
            for (int i = 0; i < source.VertexCount; i++)
            {
                if (!work.Feature()) return false;
                P vertex = source.Vertex(i);
                foreach (Q component in new[] { vertex.X, vertex.Y, vertex.Z })
                    if ((Q.From(2048) - component).Sign < 0 || (Q.From(2048) + component).Sign < 0) return false;
                Q projection = P.Dot(n, vertex);
                if (i == 0 || (projection - minimum).Sign < 0) minimum = projection;
            }
            Q support = P.Dot(n, ConvexFeatureKernel.Support(request.Shape, n));
            Q norm = Q.Root(P.Dot(n, n).A);
            GeometryInterval c = (minimum - P.Dot(n, P.From(request.Pose.Position)) - support).Bounds
                .Subtract((norm * Q.From(request.Shape.Margin)).Bounds);
            if (!TryUniformError(request, source, direction, ref work, out double error)) return false;
            c = c.Subtract(GeometryInterval.Exact(error));
            var proof = new ConvexPlaneProof(request, source, direction, c, P.Dot(n, P.From(request.Displacement)), error, scratch);
            return ConvexPlaneCertificate.TryCreate(proof, from, to, ref work, scratch, out certificate);
        }
        catch (ConvexExactRefusalException) { return work.Refuse(); }
    }

    internal static bool ValidInterval(double from, double to) => double.IsFinite(from) && double.IsFinite(to) && from >= 0 && to <= 1 && from <= to;

    // Fixed installed vertices reuse the pinned forward-operation enclosure, including all
    // scalar/SIMD reduction orders. Only placement varies with time. Its exact coordinate
    // extrema occur at 0 and 1. Product and sum spacing bounds cover the complete range,
    // including fraction conversion, and remain constant when interval endpoints change.
    // No operational inverse or sampled interior placement is used.
    static bool TryUniformError(in ConvexQueryRequest request, in ConvexPlanarSource source, Vector3 n, ref ConvexQueryWork work, out double error)
    {
        error = 0;
        GeometryInterval targetError = I(0), queryError = I(0);
        var leaf = source.Leaf;
        for (int i = 0; i < source.VertexCount; i++)
        {
            if (!work.Feature()) return false;
            Vector3 local = leaf.Geometry.Vertex(i);
            GeometryVector rounded = InstalledPoseOperations.Add(leaf.Map.RoundedDirection(FeaturePoint.Exact(local).Bounds), P.From(leaf.Pose.Position).Bounds);
            GeometryVector exact = source.Vertex(i).Bounds;
            if (!rounded.IsResolved || !exact.IsResolved) return false;
            // Residual subtraction is mathematical, not another binary32 operation.
            GeometryVector residual = new(rounded.X.Subtract(exact.X), rounded.Y.Subtract(exact.Y), rounded.Z.Subtract(exact.Z));
            GeometryInterval bound = I(0);
            for (int axis = 0; axis < 3; axis++)
                bound = bound.Add(InstalledPoseOperations.Abs(Component(residual, axis)).Multiply(I(Math.Abs(Component(n, axis)))));
            if (!bound.IsResolved) return false;
            targetError = I(Math.Max(targetError.Upper, bound.Upper));
        }
        for (int axis = 0; axis < 3; axis++)
        {
            if (!work.Feature()) return false;
            double p = Component(request.Pose.Position, axis), d = Component(request.Displacement, axis);
            GeometryInterval first = I(p), last = first.Add(I(d));
            if (!last.IsResolved) return false;
            double extent = Math.Max(Math.Abs(p), Math.Max(Math.Abs(last.Lower), Math.Abs(last.Upper)));
            GeometryInterval productError = I(Spacing(Math.Abs(d)));
            GeometryInterval sumExtent = I(extent).Add(productError);
            if (!sumExtent.IsResolved) return false;
            GeometryInterval bound = productError.Add(I(Spacing(sumExtent.Upper)))
                .Add(I(Math.Abs(d)).Multiply(I(Spacing(1))));
            queryError = queryError.Add(bound.Multiply(I(Math.Abs(Component(n, axis)))));
        }
        GeometryInterval total = targetError.Add(queryError);
        if (!total.IsResolved || !double.IsFinite(total.Upper)) return false;
        error = total.Upper;
        return true;
    }

    static GeometryInterval I(double value) => GeometryInterval.Exact(value);
    static double Spacing(double value)
    {
        float f = (float)value;
        return float.IsFinite(f) ? (double)MathF.BitIncrement(f) - MathF.BitDecrement(f) : double.PositiveInfinity;
    }
    static GeometryInterval Component(GeometryVector v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
    static double Component(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
}

internal sealed record ConvexPlaneProof(ConvexQueryRequest Request, ConvexPlanarSource Source, Vector3 Direction,
    GeometryInterval Constant, Q Beta, double UniformError, ConvexQueryScratch Scratch);

/// <summary>A cached original-target support proof and its strictly positive endpoint gaps.</summary>
internal readonly struct ConvexPlaneCertificate
{
    readonly ConvexPlaneProof? _proof;
    internal double From { get; }
    internal double To { get; }
    internal GeometryInterval FromGap { get; }
    internal GeometryInterval ToGap { get; }
    internal double UniformError => _proof?.UniformError ?? 0;
    internal ConvexQueryRequest Request => _proof!.Request;
    internal ConvexPlanarSource Source => _proof!.Source;
    internal Vector3 Direction => _proof!.Direction;
    ConvexPlaneCertificate(ConvexPlaneProof proof, double from, double to, GeometryInterval first, GeometryInterval last)
    { _proof = proof; From = from; To = to; FromGap = first; ToGap = last; }

    internal static bool TryCreate(ConvexPlaneProof proof, double from, double to, ref ConvexQueryWork work,
        ConvexQueryScratch scratch, out ConvexPlaneCertificate certificate)
    {
        certificate = default;
        if (!ConvexIntervalCertificate.ValidInterval(from, to) || !proof.Source.IsValid || !ReferenceEquals(proof.Scratch, scratch) ||
            !Gap(proof, from, ref work, out var first) || !Gap(proof, to, ref work, out var last) || first.Lower <= 0 || last.Lower <= 0) return false;
        certificate = new(proof, from, to, first, last);
        return true;
    }

    internal bool TryRestrict(double from, double to, ref ConvexQueryWork work, ConvexQueryScratch scratch, out ConvexPlaneCertificate certificate)
    {
        certificate = default;
        return _proof is not null && work.Cell() && TryCreate(_proof, from, to, ref work, scratch, out certificate);
    }
    internal bool TryGap(double at, ref ConvexQueryWork work, ConvexQueryScratch scratch, out GeometryInterval gap)
    {
        gap = default;
        return _proof is not null && _proof.Source.IsValid && ReferenceEquals(_proof.Scratch, scratch) &&
            double.IsFinite(at) && at >= From && at <= To && Gap(_proof, at, ref work, out gap);
    }
    static bool Gap(ConvexPlaneProof proof, double at, ref ConvexQueryWork work, out GeometryInterval gap)
    {
        gap = default;
        if (!work.Feature()) return false;
        try { gap = proof.Constant.Subtract((proof.Beta * Q.From(at)).Bounds); return gap.IsResolved; }
        catch (ConvexExactRefusalException) { return work.Refuse(); }
    }
}
