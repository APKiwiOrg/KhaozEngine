using System;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Conservative finite mesh locality. Boxes contain the real affine vertices and their
/// convex hull, not rounded proposals or infinite planes. An uncertain bound never excludes.</summary>
internal static class CapsuleFeatureMeshBounds
{
    internal static bool Far(FeaturePoint lower, FeaturePoint upper, FeatureNumber bandSquared,
        ReadOnlySpan<FeaturePoint> points)
    {
        if (!lower.IsResolved || !upper.IsResolved || !bandSquared.IsResolved || points.Length == 0)
            return false;
        GeometryInterval sum = GeometryInterval.Exact(0);
        for (int i = 0; i < 3; i++)
        {
            double low = Math.Min(Component(lower, i).Lower, Component(upper, i).Lower);
            double high = Math.Max(Component(lower, i).Upper, Component(upper, i).Upper);
            if (!Range(points, i, out double shapeLow, out double shapeHigh)) return false;
            GeometryInterval gap = high < shapeLow
                ? GeometryInterval.Exact(shapeLow).Subtract(GeometryInterval.Exact(high))
                : shapeHigh < low ? GeometryInterval.Exact(low).Subtract(GeometryInterval.Exact(shapeHigh))
                : GeometryInterval.Exact(0);
            sum = sum.Add(gap.Square());
        }
        return sum.IsResolved && sum.Lower > bandSquared.Bounds.Upper;
    }

    internal static bool Disjoint(FeaturePoint point, FeaturePoint a, FeaturePoint b, FeaturePoint c)
    {
        if (!point.IsResolved) return false;
        ReadOnlySpan<FeaturePoint> triangle = [a, b, c];
        for (int i = 0; i < 3; i++)
        {
            if (!Range(triangle, i, out double low, out double high)) return false;
            GeometryInterval p = Component(point, i);
            if (p.Upper < low || p.Lower > high) return true;
        }
        return false;
    }

    static bool Range(ReadOnlySpan<FeaturePoint> points, int component, out double low, out double high)
    {
        low = double.PositiveInfinity;
        high = double.NegativeInfinity;
        foreach (FeaturePoint point in points)
        {
            GeometryInterval value = Component(point, component);
            if (!value.IsResolved) return false;
            low = Math.Min(low, value.Lower);
            high = Math.Max(high, value.Upper);
        }
        return true;
    }

    static GeometryInterval Component(FeaturePoint point, int axis) =>
        axis == 0 ? point.X.Bounds : axis == 1 ? point.Y.Bounds : point.Z.Bounds;
}
