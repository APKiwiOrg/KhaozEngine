using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Water;

/// <summary>A closed edge or vertex where the upper bound only touches the nominal water level. It has no drawing
/// area, but remains part of the compiled semantic classifier.</summary>
internal readonly record struct MapWaterClosedContour(string SpaceId, MapExactXz A, MapExactXz B)
{
    internal string? FragmentId { get; init; }
    internal MapWideXz? WideA { get; init; }
    internal MapWideXz? WideB { get; init; }
    internal MapWaterClosedContour(string spaceId, string fragmentId, MapWideXz a, MapWideXz b)
        : this(spaceId, MapWaterRegionGeometry.Drawing(a), MapWaterRegionGeometry.Drawing(b))
    { FragmentId = fragmentId; WideA = a; WideB = b; }
    internal bool Holds(MapWideXz point)
    {
        MapWideXz a = WideA ?? MapWideXz.From(A), b = WideB ?? MapWideXz.From(B);
        return MapWaterPolygons.Cross(a, b, point).Sign == 0 && MapWaterPolygons.Bounds.Of(a, b).Contains(point);
    }
}
