using System;
using System.Numerics;
using BepuPhysics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Candidate enclosures for the complete operational graph. These bounds never
/// establish contact or enlarge the leaf predicate's radius or numerical allowance.</summary>
internal static class CapsuleSweepPruning
{
    // Verified on every installed operator, rather than inferred from a near-unit quaternion.
    // The infinity norm propagates local arithmetic uncertainty. The Euclidean row norm
    // propagates the query's spherical skin through the inverse transpose.
    const double InverseInfinityNorm = 2;
    const double InverseSkinDeparture = 1d / (1 << 15);

    internal static bool Validate(InstalledPoseOperator map, Quaternion orientation)
    {
        if (!map.IsResolved || !InstalledSweepBounds.OperationalCoefficientsMatch(map, orientation)) return false;
        for (int i = 0; i < 3; i++)
        {
            GeometryInterval x = Component(map.InverseTransposeX, i), y = Component(map.InverseTransposeY, i),
                z = Component(map.InverseTransposeZ, i);
            GeometryInterval sum = GeometryInterval.Exact(Magnitude(x)).Add(GeometryInterval.Exact(Magnitude(y)))
                .Add(GeometryInterval.Exact(Magnitude(z)));
            GeometryInterval squared = x.Square().Add(y.Square()).Add(z.Square());
            double skin = 1 + InverseSkinDeparture;
            if (!sum.IsResolved || sum.Upper > InverseInfinityNorm || !squared.IsResolved || squared.Upper > skin * skin) return false;
        }
        return true;
    }

    internal static FeaturePoint OperationalPoint(InstalledPoseOperator map, Vector3 point) => FeaturePoint.Add(
        FeaturePoint.Add(FeaturePoint.Add(FeaturePoint.Scale(map.InverseTransposeX, FeatureNumber.Exact(point.X)),
            FeaturePoint.Scale(map.InverseTransposeY, FeatureNumber.Exact(point.Y))),
            FeaturePoint.Scale(map.InverseTransposeZ, FeatureNumber.Exact(point.Z))), map.Translation);

    internal static bool TryStoredPad(RigidPose root, RigidPose leaf, Vector3 localOffset, out double pad)
    {
        pad = 0;
        // For every admitted query, |C0 + t D - P| <= 2 Cmax + |P|.
        // Retain original magnitudes even when the final position cancels the path.
        GeometryInterval m = GeometryInterval.Exact(2d * CapsuleGeneralSweepPolicy.MaximumStoredCoordinate)
            .Add(GeometryInterval.Exact(MaxAbs(leaf.Position)));
        if (!m.IsResolved) return false;
        GeometryInterval error = InstalledSweepBounds.BaseOperationalError(m.Upper, CapsuleGeneralSweepPolicy.MaximumQueryHalfLength);
        if (localOffset != Vector3.Zero)
        {
            if (!InstalledPoseOperator.TryCreate(root, out var rootMap)) return false;
            GeometryVector offset = rootMap.RoundedDirection(FeaturePoint.Exact(localOffset).Bounds);
            if (!offset.IsResolved) return false;
            double h = Math.Max(Magnitude(offset.X), Math.Max(Magnitude(offset.Y), Magnitude(offset.Z)));
            GeometryInterval rootM = GeometryInterval.Exact(2d * CapsuleGeneralSweepPolicy.MaximumStoredCoordinate)
                .Add(GeometryInterval.Exact(MaxAbs(root.Position)));
            if (!rootM.IsResolved) return false;
            // Every admitted transpose coefficient is below 2 in magnitude, hence its
            // absolute row sum is below 6. This includes the root/child operational order.
            error = error.Add(InstalledSweepBounds.ChildOperationalError(rootM.Upper, h, MaxAbs(root.Position))
                .Multiply(GeometryInterval.Exact(6)));
        }
        GeometryInterval padding = error.Multiply(GeometryInterval.Exact(InverseInfinityNorm))
            .Add(GeometryInterval.Exact(CapsuleGeneralSweepPolicy.MaximumQueryRadius)
                .Multiply(GeometryInterval.Exact(InverseSkinDeparture)));
        if (!padding.IsResolved || !double.IsFinite(padding.Upper)) return false;
        pad = padding.Upper;
        return true;
    }

    internal static bool TryQueryPad(Vector3 centre, float radius, float half, Vector3 displacement, out double pad)
    {
        pad = 0;
        // A second, general-only rejection uses the same proof with this query's smaller
        // operands. The tree keeps its original aperture and domain-wide stored enclosure.
        const double p = RepresentedGeometryTransforms.MaximumPositionMagnitude, h = 16384;
        GeometryInterval m = GeometryInterval.Exact(MaxAbs(centre)).Add(GeometryInterval.Exact(MaxAbs(displacement)))
            .Add(GeometryInterval.Exact(p));
        if (!m.IsResolved) return false;
        GeometryInterval error = InstalledSweepBounds.BaseOperationalError(m.Upper, half)
            .Add(InstalledSweepBounds.ChildOperationalError(m.Upper, h, p).Multiply(GeometryInterval.Exact(6)));
        GeometryInterval padding = error.Multiply(GeometryInterval.Exact(InverseInfinityNorm))
            .Add(GeometryInterval.Exact(radius).Multiply(GeometryInterval.Exact(InverseSkinDeparture)));
        if (!padding.IsResolved || !double.IsFinite(padding.Upper)) return false;
        pad = padding.Upper;
        return true;
    }

    internal static bool ExpandStored(Vector3 min, Vector3 max, double padding, out Vector3 lo, out Vector3 hi) =>
        Expand(min, max, padding, out lo, out hi) &&
        InstalledPoseOperator.Within(lo, CapsuleGeneralSweepPolicy.MaximumStoredCoordinate) &&
        InstalledPoseOperator.Within(hi, CapsuleGeneralSweepPolicy.MaximumStoredCoordinate);

    static bool Expand(Vector3 min, Vector3 max, double padding, out Vector3 lo, out Vector3 hi)
    {
        lo = new(InstalledSweepBounds.Down(Math.BitDecrement(min.X - padding)),
            InstalledSweepBounds.Down(Math.BitDecrement(min.Y - padding)), InstalledSweepBounds.Down(Math.BitDecrement(min.Z - padding)));
        hi = new(InstalledSweepBounds.Up(Math.BitIncrement(max.X + padding)),
            InstalledSweepBounds.Up(Math.BitIncrement(max.Y + padding)), InstalledSweepBounds.Up(Math.BitIncrement(max.Z + padding)));
        return float.IsFinite(lo.X) && float.IsFinite(lo.Y) && float.IsFinite(lo.Z) &&
            float.IsFinite(hi.X) && float.IsFinite(hi.Y) && float.IsFinite(hi.Z);
    }

    static double MaxAbs(Vector3 value) => Math.Max(Math.Abs((double)value.X), Math.Max(Math.Abs((double)value.Y), Math.Abs((double)value.Z)));
    static double Magnitude(GeometryInterval value) => value.IsResolved ? Math.Max(Math.Abs(value.Lower), Math.Abs(value.Upper)) : double.PositiveInfinity;
    static GeometryInterval Component(FeaturePoint point, int i) => i == 0 ? point.X.Bounds : i == 1 ? point.Y.Bounds : point.Z.Bounds;
}
