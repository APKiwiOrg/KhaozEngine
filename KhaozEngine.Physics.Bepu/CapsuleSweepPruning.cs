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

    internal static bool TryQueryPad(Vector3 centre, float radius, float half, Vector3 displacement,
        in CapsuleSweepPruningBounds bounds, out double pad, bool includePreimage = true)
    {
        pad = 0;
        if (!bounds.IsResolved || !CapsuleGeneralSweepPolicy.ContainsQuery(centre, radius, half, displacement)) return false;
        GeometryInterval start = GeometryInterval.Exact(MaxAbs(centre)), delta = GeometryInterval.Exact(MaxAbs(displacement));
        GeometryInterval root = start.Add(GeometryInterval.Exact(bounds.Root));
        GeometryInterval leaf = start.Add(delta).Add(GeometryInterval.Exact(bounds.Leaf));
        GeometryInterval subtraction = Round(root), product = Round(delta);
        GeometryInterval input = subtraction.Add(product);
        input = input.Add(Round(root.Add(delta).Add(input)));
        if (bounds.HasChild)
        {
            input = input.Add(GeometryInterval.Exact(bounds.CompositionError));
            input = input.Add(Round(leaf.Add(input)));
        }
        GeometryInterval relative = leaf.Add(input);
        GeometryInterval products = relative.Multiply(GeometryInterval.Exact(bounds.TransposeNorm))
            .Multiply(GeometryInterval.Exact(UnitRoundoff)).Add(GeometryInterval.Exact(3 * HalfSubnormal));
        GeometryInterval firstSum = Round(relative.Multiply(GeometryInterval.Exact(bounds.PairNorm)).Add(products));
        GeometryInterval axis = GeometryInterval.Exact(half).Multiply(GeometryInterval.Exact(bounds.Axis));
        GeometryInterval axisError = Round(axis);
        // At an operational contact the final rounded axis point is within radius of
        // the local solid. Reverse the final rounding to bound its error and then the
        // preceding transpose sum. A pruned candidate cannot contain such a contact.
        GeometryInterval contact = GeometryInterval.Exact(bounds.Local).Add(GeometryInterval.Exact(radius));
        GeometryInterval axisAdd = ReverseRound(contact);
        GeometryInterval finalSum = ReverseRound(contact.Add(axis).Add(axisError).Add(axisAdd));
        GeometryInterval error = input.Multiply(GeometryInterval.Exact(bounds.TransposeNorm))
            .Add(products).Add(firstSum).Add(finalSum).Add(axisError).Add(axisAdd);
        GeometryInterval padding = error.Multiply(GeometryInterval.Exact(bounds.InverseNorm))
            .Add(GeometryInterval.Exact(radius).Multiply(GeometryInterval.Exact(InverseSkinDeparture)));
        if (includePreimage) padding = padding.Add(GeometryInterval.Exact(bounds.PreimageExcess));
        if (!padding.IsResolved || !double.IsFinite(padding.Upper)) return false;
        pad = padding.Upper;
        return true;
    }

    const double UnitRoundoff = 1d / (1 << 24);
    const double HalfSubnormal = 7.006492321624085e-46;
    static GeometryInterval Round(GeometryInterval magnitude) => magnitude.Multiply(GeometryInterval.Exact(UnitRoundoff))
        .Add(GeometryInterval.Exact(HalfSubnormal));
    static GeometryInterval ReverseRound(GeometryInterval magnitude) => Round(magnitude)
        .Divide(GeometryInterval.Exact(1 - UnitRoundoff));

    internal static bool ExpandAperture(Vector3 min, Vector3 max, double padding, out Vector3 lo, out Vector3 hi) =>
        Expand(min, max, padding, out lo, out hi);

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
