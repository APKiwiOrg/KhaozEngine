using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>A scoped complete wide partition or an explicit refusal, never a truncated owner set.</summary>
internal sealed record MapOccupiedPartitionResult(MapRefinementStatus Status, IReadOnlyList<MapOccupiedPiece> Pieces,
    string? Detail, MapReadWitness Witness)
{
    internal MapBoundedOccupiedPartition Narrow()
    {
        if (Status != MapRefinementStatus.Complete) return new(Status, [], Detail, Witness);
        try
        {
            var pieces = Pieces.Select(p =>
            {
                var polygon = p.Polygon.Select(MapWideRefinementGeometry.Narrow).ToArray();
                // Coordinates alone do not certify representable heights on a sloped canonical face.
                foreach (var vertex in p.Polygon)
                {
                    var lower = p.Lift(vertex, false);
                    if (!lower.X.TryNarrow(out _) || !lower.Y.TryNarrow(out _) || !lower.Z.TryNarrow(out _)) throw new MapExactOverflowException();
                    if (p.Top(vertex) is { } upper && !upper.TryNarrow(out _)) throw new MapExactOverflowException();
                }
                return new MapBoundedOccupiedPiece(p.SpaceId, p.FragmentId, p.LowerBoundary, p.UpperBoundary, Array.AsReadOnly(polygon),
                    p.LowerFaces, p.UpperFaces, p.LowerPlane, p.UpperPlane, p.Path, p.AuthoredParent);
            }).ToArray();
            return new(Status, Array.AsReadOnly(pieces), null, Witness);
        }
        catch (MapExactOverflowException) { return new(MapRefinementStatus.NotRepresentable, [], "not representable: occupied geometry", Witness); }
    }
}

internal sealed record MapBoundedOccupiedPiece(string SpaceId, string FragmentId, MapOccupiedBoundary LowerBoundary,
    MapOccupiedBoundary UpperBoundary, IReadOnlyList<MapExactXz> Polygon, IReadOnlyList<MapBoundFace> LowerFaces,
    IReadOnlyList<MapBoundFace>? UpperFaces, MapExactValue? LowerPlane, MapExactValue? UpperPlane,
    IReadOnlyList<string> Path, MapRecordRef? AuthoredParent);

internal sealed record MapBoundedOccupiedPartition(MapRefinementStatus Status, IReadOnlyList<MapBoundedOccupiedPiece> Pieces,
    string? Detail, MapReadWitness Witness);
