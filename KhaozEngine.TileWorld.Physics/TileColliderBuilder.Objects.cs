using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.TileWorld.Physics;

// The colliders read from placed objects: wall edges, solid and diagonal boxes, and walk surface tops. Which tiles and
// edges an object blocks mirrors TileCollisionBaker, so the boxes stand where the tile collision the editor and the
// server read says they do.
static partial class TileColliderBuilder
{
    // Every plane-0 object of the loaded regions, by region in the given order, then anchor z, x and id.
    internal static TileObject[] PlacedObjects(TileWorldDocument document, RegionCoord[] regions) =>
        regions.SelectMany(c => document.GetRegion(c)!.Objects
                .Where(o => o.Plane == Plane)
                .OrderBy(o => o.Z).ThenBy(o => o.X).ThenBy(o => o.Id))
            .ToArray();

    internal static void AddWalls(TileWorldDocument document, TileWorldCatalogs catalogs, TileObject[] placed,
                                  float thickness, List<TileCollider> into)
    {
        foreach (TileObject o in placed)
        {
            if (Collidable(catalogs, o) is not { } archetype) continue;
            switch (archetype.CollisionKind)
            {
                case TileCollisionKind.Wall:
                    AddEdge(document, archetype, o, TileCollisionBaker.WallFacing(o.Rotation), thickness, into);
                    break;
                case TileCollisionKind.WallCorner:
                    // The baker's corner table (0 W+N, 1 N+E, 2 E+S, 3 S+W) is the facing of this rotation and the
                    // next one.
                    AddEdge(document, archetype, o, TileCollisionBaker.WallFacing(o.Rotation), thickness, into);
                    AddEdge(document, archetype, o, TileCollisionBaker.WallFacing(o.Rotation + 1), thickness, into);
                    break;
            }
        }
    }

    internal static void AddObjects(TileWorldDocument document, TileWorldCatalogs catalogs, TileObject[] placed,
                                    List<TileCollider> into)
    {
        float tileSize = document.TileSize;
        foreach (TileObject o in placed)
        {
            if (Collidable(catalogs, o) is not { } archetype) continue;
            TileRect tiles;
            switch (archetype.CollisionKind)
            {
                case TileCollisionKind.Solid:
                    tiles = TileFootprint.Of(archetype, o.X, o.Z, o.Rotation);
                    break;
                case TileCollisionKind.Diagonal:
                    tiles = new TileRect(o.X, o.Z, 1, 1);
                    break;
                default:
                    continue;
            }
            float bottom = LowestCorner(document, tiles.X, tiles.Z, tiles.X1, tiles.Z1);
            into.Add(Box(TileColliderKind.Object,
                TileWorldSpace.WorldX(tiles.X + tiles.Width * 0.5f, tileSize),
                TileWorldSpace.WorldZ(tiles.Z + tiles.Height * 0.5f, tileSize),
                tiles.Width * tileSize * 0.5f, tiles.Height * tileSize * 0.5f,
                bottom, Top(document, archetype, o)));
        }
    }

    internal static void AddWalkSurfaces(TileWorldDocument document, TileWorldCatalogs catalogs, TileObject[] placed,
                                         float thickness, List<TileCollider> into)
    {
        float tileSize = document.TileSize;
        foreach (TileObject o in placed)
        {
            if (catalogs.Archetype(o.ArchetypeId) is not { IsRoof: false, WalkSurfaces: { Count: > 0 } surfaces } archetype)
                continue;

            // Placed exactly as TileWalkSurfaces places it: the anchor, the planar basis and the footprint half sizes
            // that stand in for a null extent.
            TileObjectPlacement.AnchorPlanar(tileSize, archetype, o, out float anchorX, out float anchorZ);
            TileObjectPlacement.PlanarBasis(archetype, o.Rotation, out float cos, out float sin);
            float anchorY = document.HeightAt(anchorX, anchorZ, o.Plane);
            float footX = archetype.SizeX * tileSize * 0.5f, footZ = archetype.SizeZ * tileSize * 0.5f;
            // PlanarBasis answers exact axes for a quarter turn, so the box stays axis-aligned with no trigonometry.
            bool quarterTurn = (cos == 0f && MathF.Abs(sin) == 1f) || (sin == 0f && MathF.Abs(cos) == 1f);
            Quaternion orientation = quarterTurn
                ? Quaternion.Identity
                : Quaternion.CreateFromAxisAngle(Vector3.UnitY, TileObjectPlacement.YawRadians(archetype, o.Rotation));

            foreach (TileWalkSurface? surface in surfaces)
            {
                if (surface is null) continue;
                float minX = surface.MinX ?? -footX, maxX = surface.MaxX ?? footX;
                float minZ = surface.MinZ ?? -footZ, maxZ = surface.MaxZ ?? footZ;
                if (!(minX < maxX && minZ < maxZ)) continue;

                float localX = (minX + maxX) * 0.5f, localZ = (minZ + maxZ) * 0.5f;
                float halfX = (maxX - minX) * 0.5f, halfZ = (maxZ - minZ) * 0.5f;
                // Local to world through the basis, the inverse of the query's ToLocal.
                float centreX = anchorX + localX * cos + localZ * sin;
                float centreZ = anchorZ + localZ * cos - localX * sin;
                if (quarterTurn && sin != 0f) (halfX, halfZ) = (halfZ, halfX);

                float top = anchorY + surface.Height;
                into.Add(new TileCollider(TileColliderKind.WalkSurface,
                    new BoxShape(new Vector3(halfX, thickness * 0.5f, halfZ)),
                    new Pose(new Vector3(centreX, top - thickness * 0.5f, centreZ), orientation)));
            }
        }
    }

    // One box on an edge of the object's anchor tile, centred on the edge and as long as it.
    static void AddEdge(TileWorldDocument document, TileObjectArchetype archetype, TileObject o, TileDirection edge,
                        float thickness, List<TileCollider> into)
    {
        float tileSize = document.TileSize;
        int x = o.X, z = o.Z;
        float half = thickness * 0.5f, tileHalf = tileSize * 0.5f;
        float top = Top(document, archetype, o);
        TileCollider box = edge switch
        {
            TileDirection.W => Box(TileColliderKind.Wall,
                TileWorldSpace.WorldX(x, tileSize), TileWorldSpace.WorldZ(z + 0.5f, tileSize), half, tileHalf,
                LowestCorner(document, x, z, x, z + 1), top),
            TileDirection.E => Box(TileColliderKind.Wall,
                TileWorldSpace.WorldX(x + 1, tileSize), TileWorldSpace.WorldZ(z + 0.5f, tileSize), half, tileHalf,
                LowestCorner(document, x + 1, z, x + 1, z + 1), top),
            TileDirection.N => Box(TileColliderKind.Wall,
                TileWorldSpace.WorldX(x + 0.5f, tileSize), TileWorldSpace.WorldZ(z + 1, tileSize), tileHalf, half,
                LowestCorner(document, x, z + 1, x + 1, z + 1), top),
            TileDirection.S => Box(TileColliderKind.Wall,
                TileWorldSpace.WorldX(x + 0.5f, tileSize), TileWorldSpace.WorldZ(z, tileSize), tileHalf, half,
                LowestCorner(document, x, z, x + 1, z), top),
            _ => throw new ArgumentOutOfRangeException(nameof(edge)),
        };
        into.Add(box);
    }

    // The archetype of an object that can carry a wall or object box: defined and not a roof.
    static TileObjectArchetype? Collidable(TileWorldCatalogs catalogs, TileObject o) =>
        catalogs.Archetype(o.ArchetypeId) is { IsRoof: false } archetype ? archetype : null;

    // The top of a wall or object box: the height the model stands at plus its collision height.
    static float Top(TileWorldDocument document, TileObjectArchetype archetype, TileObject o)
    {
        if (archetype.CollisionHeight is not { } height)
            throw new TileWorldException(string.Create(CultureInfo.InvariantCulture,
                $"archetype '{archetype.Id}' is placed as a {archetype.CollisionKind} collider at ({o.X}, {o.Z}) but has no collisionHeight. Set one in its catalog."));
        return TileObjectPlacement.AnchorPosition(document, archetype, o).Y + height;
    }
}
