using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Water;

/// <summary>A closed edge or vertex where the upper bound only touches the nominal water level. It has no drawing
/// area, but remains part of the compiled semantic classifier.</summary>
internal readonly record struct MapWaterClosedContour(string SpaceId, MapExactXz A, MapExactXz B)
{
    internal bool Holds(MapWideXz point)
    {
        MapWideXz a = MapWideXz.From(A), b = MapWideXz.From(B);
        return MapWaterPolygons.Cross(a, b, point).Sign == 0 && MapWaterPolygons.Bounds.Of(a, b).Contains(point);
    }
}
