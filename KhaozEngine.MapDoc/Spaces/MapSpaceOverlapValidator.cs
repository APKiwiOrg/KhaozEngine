using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Checks shared air and coincident support owners on the exact piecewise-linear partition.</summary>
internal static class MapSpaceOverlapValidator
{
    internal static void Validate(MapScopedSurfaces view, IReadOnlyList<MapSpaceColumn> columns, Action<string, int, string> add)
    {
        for (int i = 0; i < columns.Count; i++)
            for (int j = i + 1; j < columns.Count; j++)
            {
                MapSpaceColumn a = columns[i], b = columns[j];
                if (MapSpaceGeometry.Max(a.Min.X, b.Min.X).CompareTo(MapSpaceGeometry.Min(a.Max.X, b.Max.X)) >= 0 ||
                    MapSpaceGeometry.Max(a.Min.Z, b.Min.Z).CompareTo(MapSpaceGeometry.Min(a.Max.Z, b.Max.Z)) >= 0) continue;
                try
                {
                    bool peer = MapSpaceGeometry.Owner(view, a.Space) != MapSpaceGeometry.Owner(view, b.Space) &&
                        !MapSpaceGeometry.Ancestors(view, a.Space).Any(s => s.Id == b.Space.Id) &&
                        !MapSpaceGeometry.Ancestors(view, b.Space).Any(s => s.Id == a.Space.Id);
                    // D9 compatibility domains have finite bilinear lower bounds and open tops, so peers share air.
                    // Their classified coverage remains provenance, never a physical face or separation claim.
                    bool ambiguous = peer && a.Refinement.CompatibilityCells.Count != 0 && b.Refinement.CompatibilityCells.Count != 0;
                    bool coincident = false;
                    foreach (MapRefinementFace fa in a.Refinement.Faces)
                        foreach (MapRefinementFace fb in b.Refinement.Faces)
                        {
                            if (!MapRefinementPolygon.Bounds.Of(fa.Polygon).Overlaps(MapRefinementPolygon.Bounds.Of(fb.Polygon))) continue;
                            List<MapExactXz> polygon = MapRefinementPolygon.Intersect(fa.Polygon, fb.Polygon);
                            if (MapRefinementPolygon.Area(polygon).Sign == 0) continue;
                            MapExactTriangle la = Triangle(a.Lower, fa.Lower), lb = Triangle(b.Lower, fb.Lower);
                            MapExactTriangle? ua = fa.Upper is { } ta ? Triangle(a.Upper!, ta) : null;
                            MapExactTriangle? ub = fb.Upper is { } tb ? Triangle(b.Upper!, tb) : null;
                            if (fa.Lower.OwnerId != fb.Lower.OwnerId &&
                                a.Footprint.Lower.Kind == MapBoundKind.SupportFloor && b.Footprint.Lower.Kind == MapBoundKind.SupportFloor &&
                                polygon.All(p => Height(la, p) == Height(lb, p)) &&
                                !view.Surfaces.Any(s => s.Role == MapSurfaceRole.PaintOverride &&
                                    (s.PaintTargetSurfaceId == fa.Lower.OwnerId || s.PaintTargetSurfaceId == fb.Lower.OwnerId)))
                                coincident = true;
                            if (!peer) continue;
                            if (ua is { } topA) polygon = Clip(polygon, p => Height(topA, p).Subtract(Height(lb, p)));
                            if (ub is { } topB) polygon = Clip(polygon, p => Height(topB, p).Subtract(Height(la, p)));
                            if (MapRefinementPolygon.Area(polygon).Sign > 0 &&
                                (ua is null || polygon.Any(p => Height(ua.Value, p).Subtract(Height(lb, p)).Sign > 0)) &&
                                (ub is null || polygon.Any(p => Height(ub.Value, p).Subtract(Height(la, p)).Sign > 0))) ambiguous = true;
                        }
                    if (coincident) add(a.Footprint.Id, a.Cell, $"coincident: footprint '{a.Footprint.Id}' cell {a.Cell} with '{b.Footprint.Id}' cell {b.Cell}");
                    if (ambiguous) add(a.Footprint.Id, a.Cell, $"ambiguous: footprint '{a.Footprint.Id}' cell {a.Cell} with '{b.Footprint.Id}' cell {b.Cell}");
                }
                catch (MapExactOverflowException) { add(a.Footprint.Id, a.Cell, $"not representable: footprint '{a.Footprint.Id}' cell {a.Cell}"); }
                catch (MapDocumentException error) { add(a.Footprint.Id, a.Cell, $"missing geometry: footprint '{a.Footprint.Id}' cell {a.Cell} ({error.Message})"); }
            }
    }

    static MapExactTriangle Triangle(IReadOnlyList<MapBoundFace> faces, MapFaceKey key) => faces.First(f => f.Key == key).Triangle;
    static MapExactValue Height(MapExactTriangle triangle, MapExactXz point) => MapSubdividedTriangle.Height(triangle, point.X, point.Z)!.Value;

    static List<MapExactXz> Clip(IReadOnlyList<MapExactXz> polygon, Func<MapExactXz, MapExactValue> distance)
    {
        var result = new List<MapExactXz>();
        if (polygon.Count == 0) return result;
        MapExactXz previous = polygon[^1];
        MapExactValue previousDistance = distance(previous);
        foreach (MapExactXz current in polygon)
        {
            MapExactValue currentDistance = distance(current);
            if ((previousDistance.Sign >= 0) != (currentDistance.Sign >= 0))
            {
                MapExactValue t = previousDistance.Divide(previousDistance.Subtract(currentDistance));
                result.Add(new(previous.X.Add(current.X.Subtract(previous.X).Multiply(t)),
                    previous.Z.Add(current.Z.Subtract(previous.Z).Multiply(t))));
            }
            if (currentDistance.Sign >= 0) result.Add(current);
            previous = current;
            previousDistance = currentDistance;
        }
        return result;
    }
}
