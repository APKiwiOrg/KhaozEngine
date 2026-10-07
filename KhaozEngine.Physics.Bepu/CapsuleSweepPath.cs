using System;
using System.Numerics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Positions on the original represented C+tD path. Resolved intervals are not exact points
/// unless their endpoints coincide, and neither status supplies a geometry or collision certificate.</summary>
internal static class CapsuleSweepPath
{
    internal static GeometryVector Point(Vector3 centre, Vector3 displacement, double fraction)
    {
        if (!Finite(centre) || !Finite(displacement) || !Finite(centre + displacement) ||
            !double.IsFinite(fraction) || fraction < 0d || fraction > 1d) return default;
        return new(Coordinate(centre.X, displacement.X, fraction),
            Coordinate(centre.Y, displacement.Y, fraction), Coordinate(centre.Z, displacement.Z, fraction));
    }

    static GeometryInterval Coordinate(double start, double delta, double fraction)
    {
        GeometryInterval product = GeometryInterval.Exact(delta).Multiply(GeometryInterval.Exact(fraction));
        double proposedProduct = delta * fraction;
        bool exactProduct = double.IsFinite(proposedProduct) &&
            BoundedGeometryArithmetic.CompareProducts(delta, fraction, proposedProduct, 1d) == GeometrySign.Zero;
        if (exactProduct) product = GeometryInterval.Exact(proposedProduct);
        GeometryInterval coordinate = GeometryInterval.Exact(start).Add(product);
        if (!coordinate.IsResolved || !exactProduct) return coordinate;

        double proposedCoordinate = start + proposedProduct;
        // The sum proposal is exact only if its signed difference from start equals the proved product.
        if (double.IsFinite(proposedCoordinate) &&
            Math.Sign(proposedCoordinate.CompareTo(start)) == Math.Sign(proposedProduct) &&
            BoundedGeometryArithmetic.CompareSquaredDistances([proposedCoordinate], [start],
                [proposedProduct], [0d]) == GeometrySign.Zero)
            return GeometryInterval.Exact(proposedCoordinate);
        return coordinate;
    }

    static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
