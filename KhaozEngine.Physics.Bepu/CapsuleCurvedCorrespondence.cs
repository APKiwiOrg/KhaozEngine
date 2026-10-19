using System;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Encloses the complete affine image against a circular reference parameterization.
/// The departure is carried as operand uncertainty, never a radius, skin or band adjustment.</summary>
internal static class CapsuleCurvedCorrespondence
{
    internal static bool TryForwardError(in InstalledSweepLeaf leaf, out double error)
    {
        error = 0;
        if (leaf.Map.IsExactlyRigid) return true;
        GeometryVector y = GeometryVectorOperations.Normalize(leaf.Map.Y.Bounds);
        if (!y.IsResolved) return false;
        // A real orthonormal frame exists by Gram-Schmidt on the admitted positive-
        // determinant map. Its radial circle is independent of the chosen X direction.
        GeometryInterval dot = GeometryVectorOperations.Dot(leaf.Map.X.Bounds, y);
        GeometryVector projected = Subtract(leaf.Map.X.Bounds, Scale(y, dot));
        GeometryVector x = GeometryVectorOperations.Normalize(projected);
        GeometryVector z = GeometryVectorOperations.Cross(x, y);
        if (!x.IsResolved || !z.IsResolved) return false;
        GeometryVector dx = Subtract(leaf.Map.X.Bounds, x), dy = Subtract(leaf.Map.Y.Bounds, y), dz = Subtract(leaf.Map.Z.Bounds, z);
        double radius = leaf.Geometry.Radius;
        // Round leaves retain the actual affine centre segment. Only their spherical
        // skin needs this enclosure. A cylinder also retains the axial scale departure.
        double axial = leaf.Geometry.Curved == SupportCurvedShape.Cylinder ? leaf.Geometry.HalfLength : 0;
        for (int component = 0; component < 3; component++)
        {
            GeometryInterval a = Component(dx, component), b = Component(dy, component), c = Component(dz, component);
            GeometryInterval bound = GeometryInterval.Exact(Magnitude(a)).Multiply(GeometryInterval.Exact(radius))
                .Add(GeometryInterval.Exact(Magnitude(b)).Multiply(GeometryInterval.Exact(radius + axial)))
                .Add(GeometryInterval.Exact(Magnitude(c)).Multiply(GeometryInterval.Exact(radius)));
            if (!bound.IsResolved) return false;
            error = Math.Max(error, bound.Upper);
        }
        return double.IsFinite(error);
    }

    internal static FeaturePoint Enclose(FeaturePoint value, double error)
    {
        if (error == 0) return value;
        FeatureNumber uncertainty = FeatureNumber.Enclosed(GeometryInterval.Enclose(-error, error));
        return new(value.X.Add(uncertainty), value.Y.Add(uncertainty), value.Z.Add(uncertainty));
    }

    static GeometryInterval Component(GeometryVector v, int i) => i == 0 ? v.X : i == 1 ? v.Y : v.Z;
    static double Magnitude(GeometryInterval v) => v.IsResolved ? Math.Max(Math.Abs(v.Lower), Math.Abs(v.Upper)) : double.PositiveInfinity;
    static GeometryVector Scale(GeometryVector v, GeometryInterval s) => new(v.X.Multiply(s), v.Y.Multiply(s), v.Z.Multiply(s));
    static GeometryVector Subtract(GeometryVector a, GeometryVector b) => new(a.X.Subtract(b.X), a.Y.Subtract(b.Y), a.Z.Subtract(b.Z));
}
