using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Physics;

namespace KhaozEngine.MapDoc.Physics;

/// <summary>Owned physical output, counting shared shapes and backing arrays once per product.</summary>
internal sealed class MapPhysicsInventory
{
    MapWaterInventory? _water;
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
        8L * world.Water.Count + world.Water.Sum((_water ??= new()).Body));
    internal static long Witness(MapSceneWitness witness) => checked(64L *
        (witness.Required.Count + (long)witness.Present.Count + witness.KnownEmpty.Count + witness.Unavailable.Count));

}
