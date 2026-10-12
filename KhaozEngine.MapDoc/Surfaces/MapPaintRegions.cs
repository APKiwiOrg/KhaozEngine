using System;
using System.Collections.Generic;
using System.Linq;

namespace KhaozEngine.MapDoc.Surfaces;

/// <summary>Render-only material coverage on an unchanged canonical face. Uncovered area retains base paint.
/// Polygons have closed draw edges. Semantic boundary ownership uses R4's half-open rule and ordinal IDs.</summary>
public sealed record MapPaintRegion(MapFaceKey Face, string PaintId, int Priority, MapPaintCoverage Material,
    IReadOnlyList<MapExactPoint> Polygon)
{
    public IReadOnlyList<MapExactPoint> Polygon { get; init; } = Array.AsReadOnly(Polygon.ToArray());
}

/// <summary>A canonical immutable partition of paint coverage, with no support geometry.</summary>
public sealed record MapPaintRegionSet(IReadOnlyList<MapPaintRegion> Regions, string Digest)
{
    public IReadOnlyList<MapPaintRegion> Regions { get; init; } = Array.AsReadOnly(Regions.ToArray());
}
