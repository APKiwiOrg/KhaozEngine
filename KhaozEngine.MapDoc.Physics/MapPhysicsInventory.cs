using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Physics;

namespace KhaozEngine.MapDoc.Physics;

/// <summary>Owned physical output, counting shared shapes and backing arrays once per product.</summary>
internal sealed class MapPhysicsInventory
{
    readonly HashSet<object> _seen = new(ReferenceEqualityComparer.Instance);
    internal long Shape(PhysicsShape? shape)
    {
        if (shape is null || !_seen.Add(shape)) return 0;
        return checked(shape switch
        {
            ConvexHullShape hull => 128L + Array(hull.Points, 12),
            TriangleMeshShape mesh => 128L + Array(mesh.Vertices, 12) + Array(mesh.Indices, 4),
            CompoundShape compound => 128L + Array(compound.Children, 64) + compound.Children.Sum(c => Shape(c.Shape)),
            _ => 64L,
        });
    }
    long Array(System.Array values, int element) => _seen.Add(values) ? checked(element * (long)values.Length) : 0;
    internal long Placement(MapPlacementGeometry placement) => checked(256L + Shape(placement.Collider) +
        Shape(placement.SourceCollider) + Shape(placement.Envelope.Shape));
    internal long Placements(IReadOnlyList<MapPlacementGeometry> placements) =>
        checked(128L + 8L * placements.Count + placements.Sum(Placement));
    internal long Asset(MapAssetShapes asset)
    {
        var surfaces = new MapSceneInventory();
        return checked(128L + Shape(asset.Collider) + Shape(asset.Selection) + 8L * asset.SupportResources.Count +
            asset.SupportResources.Sum(s => 128L + 8L * s.References.Count + surfaces.Surfaces(s.Surfaces) +
                s.Surfaces.Refs.Sum(r => 8L * (r.IndoorSpan?.DomainTags.Count ?? 0))));
    }
    internal long Chunk(MapTerrainChunk chunk) => checked(256L + 2L * chunk.ChunkId.Length + Shape(chunk.Shape) +
        (long)Unsafe.SizeOf<MapFaceKey>() * chunk.TriangleOwners.Count + chunk.TriangleRoles.Count);
    internal long Terrain(MapTerrainChunkSet terrain) => checked(128L + 8L * terrain.Chunks.Count + terrain.Chunks.Sum(Chunk));
    internal long World(MapScopedWorld world) => checked(512L + Placements(world.Placements) + Terrain(world.Terrain) +
        8L * MapSceneAdmission.CloneCapacity(world.Statics.Count, true) + 192L * world.Statics.Count +
        8L * world.Water.Count + world.Water.Sum(Water));
    internal static long Witness(MapSceneWitness witness) => checked(64L *
        (witness.Required.Count + (long)witness.Present.Count + witness.KnownEmpty.Count + witness.Unavailable.Count));

    long Rows<T>(IReadOnlyList<T> rows, int element) => _seen.Add(rows) ? checked((long)element * rows.Count) : 0;
    long Water(MapCompiledWaterBody body)
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
    long Piece(MapOccupiedPiece piece)
    {
        if (!_seen.Add(piece)) return 0;
        return checked(128L + Rows(piece.Polygon, 128) + Rows(piece.LowerFaces, Unsafe.SizeOf<MapBoundFace>()) +
            (piece.UpperFaces is null ? 0 : Rows(piece.UpperFaces, Unsafe.SizeOf<MapBoundFace>())) + Rows(piece.Path, 8));
    }
}
