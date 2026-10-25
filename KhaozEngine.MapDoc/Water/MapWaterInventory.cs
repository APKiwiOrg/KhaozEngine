using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Water;

/// <summary>Owned compiled water values, excluding consumed bound contexts and compiler workspace.</summary>
internal sealed class MapWaterInventory
{
    readonly HashSet<object> _seen = new(ReferenceEqualityComparer.Instance);
    internal long Pieces(IReadOnlyList<MapOccupiedPiece> pieces) => checked(Rows(pieces, 8) + pieces.Sum(Piece));
    long Rows<T>(IReadOnlyList<T> rows, int element) => _seen.Add(rows) ? checked((long)element * rows.Count) : 0;
    internal long Body(MapCompiledWaterBody body)
    {
        if (!_seen.Add(body)) return 0;
        long bytes = checked(256L + Rows(body.Regions, 8) +
            Rows(body.ClosedContours, Unsafe.SizeOf<MapWaterClosedContour>()) + Rows(body.FragmentBounds, 64) +
            Rows(body.OccupiedPieces, 8) + 64L * (body.Footprints.Count + body.FragmentFootprints.Count));
        foreach (var region in body.Regions)
        {
            if (!_seen.Add(region)) continue;
            bytes = checked(bytes + 128L + Rows(region.Triangles, Unsafe.SizeOf<MapExactTriangle>()) + Rows(region.WidePolygons, 8));
            foreach (var polygon in region.WidePolygons) bytes = checked(bytes + Rows(polygon, 128));
        }
        foreach (var rows in body.Footprints.Values) bytes = checked(bytes + Rows(rows, Unsafe.SizeOf<MapWaterFootprintRect>()));
        foreach (var piece in body.OccupiedPieces) bytes = checked(bytes + Piece(piece));
        foreach (var pieces in body.FragmentFootprints.Values)
        {
            bytes = checked(bytes + Rows(pieces, 8));
            foreach (var piece in pieces) bytes = checked(bytes + Piece(piece));
        }
        if (body.Domain is { } domain && _seen.Add(domain))
        {
            // The domain retains narrow rings, wide rings, per-piece bounds and its convex-part cache.
            bytes = checked(bytes + 128L + 512L * domain.Pieces.Count +
                domain.Pieces.Sum(p => 512L * p.Ring.Count));
        }
        return bytes;
    }
    internal long Piece(MapOccupiedPiece piece)
    {
        if (!_seen.Add(piece)) return 0;
        return checked(128L + Rows(piece.Polygon, 128) + Rows(piece.LowerFaces, Unsafe.SizeOf<MapBoundFace>()) +
            (piece.UpperFaces is null ? 0 : Rows(piece.UpperFaces, Unsafe.SizeOf<MapBoundFace>())) + Rows(piece.Path, 8));
    }
}
