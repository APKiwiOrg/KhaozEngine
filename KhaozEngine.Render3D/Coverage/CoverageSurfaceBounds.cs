using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>Absolute surface bounds represented as a render-origin anchor plus relative coordinates.
/// Converting an ordinary MeshBounds means absolute coordinates with a zero anchor.</summary>
internal readonly struct CoverageSurfaceBounds
{
    internal MeshBounds Relative { get; }
    internal Vector3 Origin { get; }
    internal CoverageScalarBounds Weight { get; }
    internal CoverageSurfaceBounds(Vector3 min, Vector3 max) : this(min, max, Vector3.Zero, CoverageIntervals.One) { }
    CoverageSurfaceBounds(Vector3 min, Vector3 max, Vector3 origin, CoverageScalarBounds weight)
    { Relative = new(min, max); Origin = origin; Weight = weight; }
    internal static CoverageSurfaceBounds FromRender(MeshBounds relative, Vector3 origin) =>
        new(relative.Min, relative.Max, origin, CoverageIntervals.One);
    /// <summary>Reanchors the shared CPU envelope without reading back or rescanning a GPU surface.</summary>
    internal static CoverageSurfaceBounds FromWorld(in MeshBounds world, Vector3 origin)
    {
        Vector3 low = default, high = default;
        for (int axis = 0; axis < 3; axis++)
        {
            var relative = CoverageIntervals.Subtract(new(world.Min[axis], world.Max[axis]), new(origin[axis], origin[axis]));
            low[axis] = CoverageActorBounds.Lower(relative.Min);
            high[axis] = CoverageActorBounds.Upper(relative.Max);
        }
        return new(low, high, origin, CoverageIntervals.One);
    }
    internal static CoverageSurfaceBounds FromHomogeneousRender(MeshBounds relative, Vector3 origin, CoverageScalarBounds weight) =>
        new(relative.Min, relative.Max, origin, weight);
    public static implicit operator CoverageSurfaceBounds(MeshBounds absolute) => new(absolute.Min, absolute.Max);
}

/// <summary>Reduces bounded homogeneous render positions before adding any absolute translation.</summary>
internal struct CoverageSurfaceBoundsBuilder(Vector3 origin)
{
    Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
    bool any;
    double minW = double.PositiveInfinity, maxW = double.NegativeInfinity;
    bool uncertain;

    internal void Add(CoveragePositionBounds position)
    {
        any = true;
        if (!position.IsFinite) { uncertain = true; return; }
        min = Vector3.Min(min, new((float)position.X.Min, (float)position.Y.Min, (float)position.Z.Min));
        max = Vector3.Max(max, new((float)position.X.Max, (float)position.Y.Max, (float)position.Z.Max));
        minW = Math.Min(minW, position.W.Min); maxW = Math.Max(maxW, position.W.Max);
    }

    internal CoverageSurfaceBounds? Build(double worldError = 0)
    {
        if (!any) return null;
        if (uncertain) return CoverageSurfaceBounds.FromHomogeneousRender(default, origin, CoverageIntervals.Invalid);
        float margin = (float)worldError;
        if (margin < worldError) margin = MathF.BitIncrement(margin);
        Vector3 low = min - new Vector3(margin), high = max + new Vector3(margin);
        for (int axis = 0; axis < 3; axis++)
        {
            low[axis] = MathF.BitDecrement(low[axis]);
            high[axis] = MathF.BitIncrement(high[axis]);
        }
        return CoverageSurfaceBounds.FromHomogeneousRender(new MeshBounds(low, high), origin, new(minW, maxW));
    }
}
