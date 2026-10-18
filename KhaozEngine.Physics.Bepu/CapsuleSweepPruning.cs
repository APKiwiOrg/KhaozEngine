using System;
using System.Numerics;

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

    internal static bool TryAperture(Vector3 centre, float radius, float half, Vector3 displacement,
        out Vector3 min, out Vector3 max)
    {
        if (!CapsuleSweepAperture.TryCreate(centre, radius, half, displacement, out min, out max)) return false;
        // Every posed root/leaf coordinate is within P. Root rotations have coefficients
        // below 2 and child coordinates within 2048, so their rounded offset is below H.
        // Use the original start and displacement independently. A small final position
        // cannot bound the rounded root-relative subtraction followed by the path sum.
        const double p = RepresentedGeometryTransforms.MaximumPositionMagnitude, h = 16384;
        GeometryInterval m = GeometryInterval.Exact(MaxAbs(centre)).Add(GeometryInterval.Exact(MaxAbs(displacement)))
            .Add(GeometryInterval.Exact(p));
        // Same subtract/product/path/child/composed-pose account as TryOperationalError,
        // with the verified maximum absolute transpose row sum 6 for every leaf.
        GeometryInterval error = !m.IsResolved ? default : InstalledSweepBounds.BaseOperationalError(m.Upper, half)
            .Add(InstalledSweepBounds.ChildOperationalError(m.Upper, h, p).Multiply(GeometryInterval.Exact(6)));
        GeometryInterval padding = error.Multiply(GeometryInterval.Exact(InverseInfinityNorm))
            .Add(GeometryInterval.Exact(radius).Multiply(GeometryInterval.Exact(InverseSkinDeparture)));
        if (!padding.IsResolved || !double.IsFinite(padding.Upper) ||
            !Expand(min, max, padding.Upper, out Vector3 expandedMin, out Vector3 expandedMax))
        {
            // An unbounded arithmetic enclosure cannot authorize pruning. Visit the existing
            // tree over its entire admitted stored domain, under the unchanged traversal caps.
            min = Vector3.Min(min, new(-CapsuleGeneralSweepPolicy.MaximumStoredCoordinate));
            max = Vector3.Max(max, new(CapsuleGeneralSweepPolicy.MaximumStoredCoordinate));
        }
        else { min = expandedMin; max = expandedMax; }
        return true;
    }

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
