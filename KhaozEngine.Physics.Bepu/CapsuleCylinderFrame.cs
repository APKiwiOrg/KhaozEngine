using System;
using System.Numerics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>The forward circular cylinder and its independently enclosed pinned local-map path.</summary>
internal readonly record struct CapsuleCylinderFrame(FeaturePoint Centre, FeaturePoint Delta, FeaturePoint QueryAxis,
    FeaturePoint CylinderAxis, float Radius, float HalfLength, float QueryRadius, float QueryHalf)
{
    internal bool IsResolved => Centre.IsResolved && Delta.IsResolved && QueryAxis.IsResolved && CylinderAxis.IsResolved;

    internal static bool TryCreate(in InstalledSweepLeaf leaf, Vector3 centre, Vector3 delta,
        in CapsuleQueryDimensions capsule, out CapsuleCylinderFrame frame)
    {
        frame = default;
        FeaturePoint c = FeaturePoint.Subtract(FeaturePoint.Exact(centre), FeaturePoint.Exact(leaf.Pose.Position));
        FeaturePoint d = FeaturePoint.Exact(delta), u = FeaturePoint.Exact(Vector3.UnitY);
        GeometryVector normalized = GeometryVectorOperations.Normalize(leaf.Map.Y.Bounds);
        FeaturePoint axis = From(normalized);
        if (!leaf.Operational)
        {
            if (!CapsuleCurvedCorrespondence.TryForwardError(leaf, out double error)) return false;
            c = CapsuleCurvedCorrespondence.Enclose(c, error);
        }
        if (leaf.Operational)
        {
            FeaturePoint x = leaf.Map.RepresentedX, y = leaf.Map.RepresentedY, z = leaf.Map.RepresentedZ;
            c = new(FeaturePoint.Dot(c, x), FeaturePoint.Dot(c, y), FeaturePoint.Dot(c, z));
            d = new(FeaturePoint.Dot(d, x), FeaturePoint.Dot(d, y), FeaturePoint.Dot(d, z));
            u = new(x.Y, y.Y, z.Y);
            axis = FeaturePoint.Exact(Vector3.UnitY);
            if (!InstalledSweepBounds.TryOperationalError(leaf, centre, delta, capsule.Length * 0.5f, out double error)) return false;
            FeatureNumber uncertainty = FeatureNumber.Enclosed(GeometryInterval.Enclose(-error, error));
            if (CapsuleSweepSourceSample.TryCreate(leaf, centre, delta, 0, out var source))
            {
                c = new(d.X.Sign == GeometrySign.Zero ? FeatureNumber.Exact(source.Centre.X) : c.X.Add(uncertainty),
                    d.Y.Sign == GeometrySign.Zero ? FeatureNumber.Exact(source.Centre.Y) : c.Y.Add(uncertainty),
                    d.Z.Sign == GeometrySign.Zero ? FeatureNumber.Exact(source.Centre.Z) : c.Z.Add(uncertainty));
                u = FeaturePoint.Exact(source.Axis);
            }
            else c = new(c.X.Add(uncertainty), c.Y.Add(uncertainty), c.Z.Add(uncertainty));
        }
        frame = new(c, d, u, axis, leaf.Geometry.Radius, leaf.Geometry.HalfLength, capsule.Radius, capsule.Length * 0.5f);
        return frame.IsResolved;
    }

    internal FeaturePoint Point(double fraction, double s) => FeaturePoint.Add(
        FeaturePoint.Add(Centre, FeaturePoint.Scale(Delta, FeatureNumber.Exact(fraction), true)),
        FeaturePoint.Scale(QueryAxis, FeatureNumber.Exact(s), true));

    internal static FeaturePoint From(GeometryVector value) => new(From(value.X), From(value.Y), From(value.Z));
    static FeatureNumber From(GeometryInterval value) => value.IsResolved && value.Lower == value.Upper
        ? FeatureNumber.Exact(value.Lower) : FeatureNumber.Enclosed(value);

    internal static FeatureNumber Abs(FeatureNumber value) => !value.IsResolved ? default : value.Bounds.Lower >= 0 ? value
        : value.Bounds.Upper <= 0 ? value.Negate() : FeatureNumber.Enclosed(GeometryInterval.Enclose(0, Math.Max(-value.Bounds.Lower, value.Bounds.Upper)));

    internal static FeatureNumber Root(FeatureNumber squared)
    {
        if (!squared.IsResolved) return default;
        if (squared.IsExact && squared.Value >= 0)
        {
            double proposal = Math.Sqrt(squared.Value);
            if (BoundedGeometryArithmetic.CompareProducts(proposal, proposal, squared.Value, 1) == GeometrySign.Zero)
                return FeatureNumber.Exact(proposal);
        }
        // A squared norm is nonnegative even if outward addition crosses zero.
        return FeatureNumber.Enclosed(GeometryInterval.Enclose(Math.Max(0, squared.Bounds.Lower), squared.Bounds.Upper).Sqrt());
    }
}
