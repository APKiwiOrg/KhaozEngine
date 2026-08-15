using System;

namespace KhaozEngine.TileWorld;

/// <summary>Derives <see cref="TileCollisionMap"/> from settings and objects. Full bake at load, rect rebake
/// after an edit. Never authored, never persisted, and it never throws on a dangling archetype (the validator
/// reports that, an edit must not fault the map).</summary>
public static class TileCollisionBaker
{
    /// <summary>Bakes every loaded region and every plane from scratch.</summary>
    public static TileCollisionMap Bake(TileWorldDocument doc, TileWorldCatalogs catalogs)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(catalogs);
        var map = new TileCollisionMap(doc.PlaneCount);
        foreach (TileRegion region in doc.Regions.Values)
        {
            map.EnsureRegion(region.Coord);
            for (int p = 0; p < doc.PlaneCount; p++) BakeGround(map, doc, region.Coord.Rect, p);
        }
        foreach (TileRegion region in doc.Regions.Values)
            foreach (TileObject o in region.Objects) ApplyObject(map, catalogs, o);
        return map;
    }

    /// <summary>Recomputes the dirty rect (expanded by one tile for mirrored edges) on one plane.</summary>
    public static void Rebake(TileCollisionMap map, TileWorldDocument doc, TileWorldCatalogs catalogs, TileRect dirty, int plane)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(catalogs);
        if (dirty.IsEmpty) return;
        TileRect clear = dirty.Expand(1);
        map.Clear(clear, plane);
        for (int z = clear.Z; z < clear.Z1; z++)
            for (int x = clear.X; x < clear.X1; x++)
            {
                if (doc.RegionAt(x, z) is null) continue;
                if (IsGroundBlocked(doc, x, z, plane)) map.Or(x, z, plane, TileCollisionFlags.Blocked);
            }
        foreach (TileRegion region in doc.RegionsTouching(clear.Expand(2)))
            foreach (TileObject o in region.Objects)
            {
                if (o.Plane != plane) continue;
                TileObjectArchetype? a = catalogs.Archetype(o.ArchetypeId);
                if (a is null) continue;
                if (TileFootprint.Of(a, o.X, o.Z, o.Rotation).Expand(1).Intersects(clear)) ApplyObject(map, catalogs, o);
            }
    }

    /// <summary>The wall bit for one cardinal direction: W to WallW, N to WallN, E to WallE, S to WallS.</summary>
    public static TileCollisionFlags EdgeFlag(TileDirection d) => d switch
    {
        TileDirection.W => TileCollisionFlags.WallW,
        TileDirection.N => TileCollisionFlags.WallN,
        TileDirection.E => TileCollisionFlags.WallE,
        TileDirection.S => TileCollisionFlags.WallS,
        _ => throw new ArgumentOutOfRangeException(nameof(d), "edge flags exist for the four cardinal directions only"),
    };

    /// <summary>The edge a wall archetype occupies for a rotation: 0 west, 1 north, 2 east, 3 south.</summary>
    public static TileDirection WallFacing(int rotation) => (rotation & 3) switch
    {
        0 => TileDirection.W, 1 => TileDirection.N, 2 => TileDirection.E, _ => TileDirection.S,
    };

    static TileDirection Opposite(TileDirection d) => d switch
    {
        TileDirection.W => TileDirection.E, TileDirection.E => TileDirection.W,
        TileDirection.N => TileDirection.S, TileDirection.S => TileDirection.N,
        _ => throw new ArgumentOutOfRangeException(nameof(d)),
    };

    static void BakeGround(TileCollisionMap map, TileWorldDocument doc, TileRect rect, int plane)
    {
        for (int z = rect.Z; z < rect.Z1; z++)
            for (int x = rect.X; x < rect.X1; x++)
                if (IsGroundBlocked(doc, x, z, plane)) map.Or(x, z, plane, TileCollisionFlags.Blocked);
    }

    static bool IsGroundBlocked(TileWorldDocument doc, int x, int z, int plane) =>
        doc.GetUnderlay(x, z, plane) == 0 || (doc.GetSettings(x, z, plane) & TileSettings.Blocked) != 0;

    static void ApplyObject(TileCollisionMap map, TileWorldCatalogs catalogs, TileObject o)
    {
        TileObjectArchetype? a = catalogs.Archetype(o.ArchetypeId);
        if (a is null || (uint)o.Plane >= (uint)map.PlaneCount) return;
        switch (a.CollisionKind)
        {
            case TileCollisionKind.Solid:
                TileRect fp = TileFootprint.Of(a, o.X, o.Z, o.Rotation);
                for (int z = fp.Z; z < fp.Z1; z++)
                    for (int x = fp.X; x < fp.X1; x++) map.Or(x, z, o.Plane, TileCollisionFlags.Blocked);
                break;
            case TileCollisionKind.Diagonal:
                map.Or(o.X, o.Z, o.Plane, TileCollisionFlags.Blocked);
                break;
            case TileCollisionKind.Wall:
                ApplyEdge(map, o.X, o.Z, o.Plane, WallFacing(o.Rotation));
                break;
            case TileCollisionKind.WallCorner:
                ApplyCorner(map, o);
                break;
        }
    }

    static void ApplyEdge(TileCollisionMap map, int x, int z, int plane, TileDirection facing)
    {
        map.Or(x, z, plane, EdgeFlag(facing));
        (int dx, int dz) = TileDirections.Delta(facing);
        map.Or(x + dx, z + dz, plane, EdgeFlag(Opposite(facing)));
    }

    static void ApplyCorner(TileCollisionMap map, TileObject o)
    {
        // rotation 0 NW (W+N), 1 NE (N+E), 2 SE (E+S), 3 SW (S+W)
        (TileDirection first, TileDirection second, TileCollisionFlags own, TileCollisionFlags mirror, int ddx, int ddz) = (o.Rotation & 3) switch
        {
            0 => (TileDirection.W, TileDirection.N, TileCollisionFlags.CornerNW, TileCollisionFlags.CornerSE, -1, 1),
            1 => (TileDirection.N, TileDirection.E, TileCollisionFlags.CornerNE, TileCollisionFlags.CornerSW, 1, 1),
            2 => (TileDirection.E, TileDirection.S, TileCollisionFlags.CornerSE, TileCollisionFlags.CornerNW, 1, -1),
            _ => (TileDirection.S, TileDirection.W, TileCollisionFlags.CornerSW, TileCollisionFlags.CornerNE, -1, -1),
        };
        ApplyEdge(map, o.X, o.Z, o.Plane, first);
        ApplyEdge(map, o.X, o.Z, o.Plane, second);
        map.Or(o.X, o.Z, o.Plane, own);
        map.Or(o.X + ddx, o.Z + ddz, o.Plane, mirror);
    }
}
