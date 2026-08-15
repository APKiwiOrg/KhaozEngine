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

    /// <summary>Whether an agent anchored at (x, z) with an NxN footprint may take one step in <paramref name="dir"/>.
    /// Cardinal: no wall on the leaving edge, target not blocked, no wall on the entering edge. Diagonal: the
    /// target is not blocked, neither corner bit forbids it, and all four cardinal sub-steps around the
    /// diagonal are legal (no corner cutting). NxN: every footprint tile must be able to take the step.</summary>
    public static bool CanStep(TileCollisionMap map, int x, int z, int plane, TileDirection dir, int agentSize = 1)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (agentSize < 1) throw new ArgumentOutOfRangeException(nameof(agentSize));
        for (int dz = 0; dz < agentSize; dz++)
            for (int dx = 0; dx < agentSize; dx++)
                if (!CanStep1(map, x + dx, z + dz, plane, dir)) return false;
        return true;
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
