using System;

namespace KhaozEngine.TileWorld;

/// <summary>The one movement primitive the tick movement and the pathfinder share.</summary>
public static class TileCollision
{
    /// <summary>Whether the whole tile is impassable, which needs no edge test.</summary>
    public static bool IsBlocked(TileCollisionMap map, int x, int z, int plane)
    {
        ArgumentNullException.ThrowIfNull(map);
        return (map.Get(x, z, plane) & TileCollisionFlags.Blocked) != 0;
    }

    /// <summary>Whether an agent anchored at (x, z) with an NxN footprint may STAND there: no footprint tile is Blocked
    /// (a region the map does not hold reads Blocked, so an unloaded tile refuses too) and no wall lies on an edge
    /// between two tiles of the footprint, so a body never stands across a fence. Each internal edge is read from
    /// BOTH of its tiles, the way <see cref="CanStep"/> reads the edge it crosses, so a wall set on one side only
    /// still refuses. For a one tile agent this is exactly <see cref="IsBlocked"/> negated.</summary>
    public static bool CanStand(TileCollisionMap map, int x, int z, int plane, int agentSize = 1)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (agentSize < 1) throw new ArgumentOutOfRangeException(nameof(agentSize));
        for (int dz = 0; dz < agentSize; dz++)
            for (int dx = 0; dx < agentSize; dx++)
            {
                TileCollisionFlags f = map.Get(x + dx, z + dz, plane);
                if ((f & TileCollisionFlags.Blocked) != 0) return false;
                // BOTH sides of every internal edge, the style CanStepCardinal already uses. The baker mirrors a wall
                // onto both tiles of the edge it blocks, so for anything it bakes the east and north reads alone
                // would answer the same. A map built any other way, a hand-built one or a future writer that sets one
                // side, is then read the way a step reads it rather than half of it.
                if (dx + 1 < agentSize && (f & TileCollisionFlags.WallE) != 0) return false;
                if (dz + 1 < agentSize && (f & TileCollisionFlags.WallN) != 0) return false;
                if (dx > 0 && (f & TileCollisionFlags.WallW) != 0) return false;
                if (dz > 0 && (f & TileCollisionFlags.WallS) != 0) return false;
            }
        return true;
    }

    /// <summary>Whether an agent anchored at (x, z) with an NxN footprint may take one step in <paramref name="dir"/>.
    /// Cardinal: no wall on the leaving edge, target not blocked, no wall on the entering edge. Diagonal: the
    /// target is not blocked, neither corner bit forbids it, and all four cardinal sub-steps around the
    /// diagonal are legal (no corner cutting). NxN: every footprint tile must be able to take the step, and the
    /// destination must satisfy <see cref="CanStand"/>.</summary>
    public static bool CanStep(TileCollisionMap map, int x, int z, int plane, TileDirection dir, int agentSize = 1)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (agentSize < 1) throw new ArgumentOutOfRangeException(nameof(agentSize));
        for (int dz = 0; dz < agentSize; dz++)
            for (int dx = 0; dx < agentSize; dx++)
                if (!CanStep1(map, x + dx, z + dz, plane, dir)) return false;
        if (agentSize == 1) return true;
        // The per-tile steps cross every internal edge along the axis of travel but not the perpendicular ones, so a
        // body could otherwise straddle a wall it walks beside.
        (int ddx, int ddz) = TileDirections.Delta(dir);
        return CanStand(map, x + ddx, z + ddz, plane, agentSize);
    }

    static bool CanStep1(TileCollisionMap map, int x, int z, int plane, TileDirection dir)
    {
        (int dx, int dz) = TileDirections.Delta(dir);
        if (!TileDirections.IsDiagonal(dir)) return CanStepCardinal(map, x, z, plane, dir);

        TileCollisionFlags target = map.Get(x + dx, z + dz, plane);
        if ((target & TileCollisionFlags.Blocked) != 0) return false;
        (TileCollisionFlags ownCorner, TileCollisionFlags targetCorner) = dir switch
        {
            TileDirection.NE => (TileCollisionFlags.CornerNE, TileCollisionFlags.CornerSW),
            TileDirection.NW => (TileCollisionFlags.CornerNW, TileCollisionFlags.CornerSE),
            TileDirection.SE => (TileCollisionFlags.CornerSE, TileCollisionFlags.CornerNW),
            _ => (TileCollisionFlags.CornerSW, TileCollisionFlags.CornerNE),
        };
        if ((map.Get(x, z, plane) & ownCorner) != 0 || (target & targetCorner) != 0) return false;

        TileDirection dirX = dx < 0 ? TileDirection.W : TileDirection.E;
        TileDirection dirZ = dz < 0 ? TileDirection.S : TileDirection.N;
        return CanStepCardinal(map, x, z, plane, dirX)
            && CanStepCardinal(map, x, z, plane, dirZ)
            && CanStepCardinal(map, x + dx, z, plane, dirZ)
            && CanStepCardinal(map, x, z + dz, plane, dirX);
    }

    static bool CanStepCardinal(TileCollisionMap map, int x, int z, int plane, TileDirection dir)
    {
        (int dx, int dz) = TileDirections.Delta(dir);
        TileCollisionFlags here = map.Get(x, z, plane);
        if ((here & TileCollisionBaker.EdgeFlag(dir)) != 0) return false;
        TileCollisionFlags there = map.Get(x + dx, z + dz, plane);
        if ((there & TileCollisionFlags.Blocked) != 0) return false;
        TileDirection back = dir switch
        {
            TileDirection.W => TileDirection.E, TileDirection.E => TileDirection.W,
            TileDirection.N => TileDirection.S, _ => TileDirection.N,
        };
        return (there & TileCollisionBaker.EdgeFlag(back)) == 0;
    }
}
