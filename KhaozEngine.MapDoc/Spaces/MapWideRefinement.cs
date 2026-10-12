using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Internal authoritative refinement. Generated geometry stays wide until a bounded adapter is requested.</summary>
internal sealed record MapWideRefinementFace(MapFaceKey Lower, MapFaceKey? Upper,
    IReadOnlyList<MapWideXz> Polygon, MapWideRational? MinSeparation);

internal sealed record MapWideCellRefinement(MapRefinementStatus Status, IReadOnlyList<MapWideXz> Vertices,
    IReadOnlyList<MapWideRefinementFace> Faces, MapWideRational CellArea, MapWideRational LowerArea,
    MapWideRational CompatibilityArea, IReadOnlyList<MapLegacyCellTag> CompatibilityCells,
    MapWideRational? UpperArea, MapWideRational? MinSeparation, int PairChecks, string? Detail)
{
    internal static MapWideCellRefinement Failure(MapRefinementStatus status, string? detail) =>
        new(status, [], [], default, default, default, [], null, null, 0, detail);

    internal static MapWideCellRefinement FromBounded(MapCellRefinement result) => new(result.Status,
        result.Vertices.Select(MapWideXz.From).ToArray(), result.Faces.Select(f => new MapWideRefinementFace(f.Lower, f.Upper,
            f.Polygon.Select(MapWideXz.From).ToArray(), f.MinSeparation is { } gap ? MapWideRational.From(gap) : null)).ToArray(),
        MapWideRational.From(result.CellArea), MapWideRational.From(result.LowerArea), MapWideRational.From(result.CompatibilityArea),
        result.CompatibilityCells, result.UpperArea is { } upper ? MapWideRational.From(upper) : null,
        result.MinSeparation is { } separation ? MapWideRational.From(separation) : null, result.PairChecks, result.Detail);

    /// <summary>Preserves released bounded output semantics. A refusal never exposes a partial refinement.</summary>
    internal MapCellRefinement Narrow()
    {
        if (Status != MapRefinementStatus.Complete) return MapCommonRefinement.Failure(Status, Detail);
        try
        {
            var vertices = Vertices.Select(MapWideRefinementGeometry.Narrow).ToArray();
            var faces = Faces.Select(f => new MapRefinementFace(f.Lower, f.Upper,
                f.Polygon.Select(MapWideRefinementGeometry.Narrow).ToArray(), f.MinSeparation is { } gap ? NarrowValue(gap) : null)).ToArray();
            return new(Status, Array.AsReadOnly(vertices), Array.AsReadOnly(faces), NarrowValue(CellArea), NarrowValue(LowerArea),
                NarrowValue(CompatibilityArea), CompatibilityCells, UpperArea is { } upper ? NarrowValue(upper) : null,
                MinSeparation is { } separation ? NarrowValue(separation) : null, PairChecks, null);
        }
        catch (MapExactOverflowException) { return MapCommonRefinement.Failure(MapRefinementStatus.NotRepresentable, "overflow"); }
    }
    static MapExactValue NarrowValue(MapWideRational value) => value.TryNarrow(out var exact) ? exact : throw new MapExactOverflowException();
}

/// <summary>Canonical ordering and exact boundary conversion shared by refinement and occupied polygons.</summary>
internal static class MapWideRefinementGeometry
{
    internal static IReadOnlyList<MapWideXz> Normalize(IEnumerable<MapWideXz> points)
    {
        var polygon = points.Distinct().ToList();
        if (polygon.Count == 0) return Array.Empty<MapWideXz>();
        if (MapEffectiveGeometry.AreaWide(polygon).Sign < 0) polygon.Reverse();
        int first = 0;
        for (int i = 1; i < polygon.Count; i++) if (polygon[i].CompareTo(polygon[first]) < 0) first = i;
        return Array.AsReadOnly(Enumerable.Range(0, polygon.Count).Select(i => polygon[(first + i) % polygon.Count]).ToArray());
    }
    internal static MapExactXz Narrow(MapWideXz point) => point.TryNarrow(out var exact) ? exact : throw new MapExactOverflowException();
}
