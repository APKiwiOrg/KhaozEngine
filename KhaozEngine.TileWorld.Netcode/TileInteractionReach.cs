using System;
using System.Collections.Generic;
using KhaozEngine.TileWorld;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>Policy-aware interaction reach built on the default <see cref="TileReach"/> contract.</summary>
public static class TileInteractionReach
{
    static readonly (TileDirection Direction, int X, int Z)[] Corners =
    {
        (TileDirection.SW, -1, -1),
        (TileDirection.SE, 1, -1),
        (TileDirection.NW, -1, 1),
        (TileDirection.NE, 1, 1),
    };

    /// <summary>Every interaction anchor admitted by <paramref name="policy"/>, in deterministic order.</summary>
    public static IReadOnlyList<TileCoord> Set(TileCollisionMap map, TileRect footprint, int plane, int agentSize,
        TileInteractionReachPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentOutOfRangeException.ThrowIfLessThan(agentSize, 1);
        if (!Has(policy, TileInteractionReachPolicy.IncludeDiagonals)
            && !Has(policy, TileInteractionReachPolicy.IncludeOverlap))
            return TileReach.Set(map, footprint, plane, agentSize);

        IReadOnlyList<TileCoord> oneTile = OneTileSet(map, footprint, plane, policy);
        if (agentSize == 1) return oneTile;
        var anchors = new List<TileCoord>();
        var seen = new HashSet<TileCoord>();
        for (int i = 0; i < oneTile.Count; i++)
        {
            TileCoord point = oneTile[i];
            for (int dz = 0; dz < agentSize; dz++)
                for (int dx = 0; dx < agentSize; dx++)
                {
                    var anchor = new TileCoord(point.X - dx, point.Z - dz, plane);
                    bool overlaps = Overlaps(anchor, agentSize, footprint);
                    if (overlaps && !Has(policy, TileInteractionReachPolicy.IncludeOverlap)) continue;
                    if (!TileCollision.CanStand(map, anchor.X, anchor.Z, plane, agentSize)) continue;
                    if (seen.Add(anchor)) anchors.Add(anchor);
                }
        }
        return anchors;
    }

    /// <summary>Whether an actor anchor is admitted by the interaction reach policy.</summary>
    public static bool Contains(TileCollisionMap map, TileRect footprint, int plane, TileCoord from, int agentSize,
        TileInteractionReachPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentOutOfRangeException.ThrowIfLessThan(agentSize, 1);
        if (!Has(policy, TileInteractionReachPolicy.IncludeDiagonals)
            && !Has(policy, TileInteractionReachPolicy.IncludeOverlap))
            return TileReach.Contains(map, footprint, plane, from, agentSize);
        if (from.Plane != plane) return false;
        IReadOnlyList<TileCoord> set = Set(map, footprint, plane, agentSize, policy);
        for (int i = 0; i < set.Count; i++) if (set[i].Equals(from)) return true;
        return false;
    }

    /// <summary>Finds the nearest reachable interaction anchor admitted by the policy.</summary>
    public static bool TryNearest(TileCollisionMap map, TileRect footprint, int plane, TileCoord from,
        int agentSize, int maxRadius, TileInteractionReachPolicy policy, out TileCoord reachTile,
        out TilePath path, TilePathfinderScratch? scratch = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentOutOfRangeException.ThrowIfLessThan(agentSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRadius, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxRadius, TilePathfinder.MaxSearchRadius);
        if (!Has(policy, TileInteractionReachPolicy.IncludeDiagonals)
            && !Has(policy, TileInteractionReachPolicy.IncludeOverlap))
            return TileReach.TryNearest(map, footprint, plane, from, agentSize, maxRadius,
                out reachTile, out path, scratch);

        reachTile = default;
        path = TilePath.Empty(from);
        if (from.Plane != plane || footprint.IsEmpty) return false;
        if (TileReach.FootprintDistance(footprint, from) > (long)maxRadius + agentSize) return false;
        IReadOnlyList<TileCoord> candidates = Set(map, footprint, plane, agentSize, policy);
        if (candidates.Count == 0) return false;
        TilePath found = TilePathfinder.FindPathToAny(map, plane, from, candidates, agentSize, maxRadius, scratch,
            out int index);
        if (!found.Reached) return false;
        reachTile = candidates[index];
        path = found;
        return true;
    }

    /// <summary>The deterministic eight-way facing from an admitted anchor toward the target.</summary>
    public static TileDirection FacingToward(TileCollisionMap map, TileRect footprint, int plane, TileCoord from,
        int agentSize, TileInteractionReachPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentOutOfRangeException.ThrowIfLessThan(agentSize, 1);
        if (!Has(policy, TileInteractionReachPolicy.IncludeDiagonals)
            && !Has(policy, TileInteractionReachPolicy.IncludeOverlap))
            return TileReach.FacingToward(map, footprint, plane, from, agentSize);
        if (footprint.IsEmpty || from.Plane != plane) return TileDirection.W;

        long x0 = from.X, x1 = x0 + agentSize, z0 = from.Z, z1 = z0 + agentSize;
        long fx0 = footprint.X, fx1 = fx0 + footprint.Width;
        long fz0 = footprint.Z, fz1 = fz0 + footprint.Height;
        bool zOverlap = z0 < fz1 && z1 > fz0;
        bool xOverlap = x0 < fx1 && x1 > fx0;
        if (zOverlap && x0 == fx1) return TileDirection.W;
        if (zOverlap && x1 == fx0) return TileDirection.E;
        if (xOverlap && z0 == fz1) return TileDirection.S;
        if (xOverlap && z1 == fz0) return TileDirection.N;
        if (x1 == fx0 && z1 == fz0) return TileDirection.NE;
        if (x0 == fx1 && z1 == fz0) return TileDirection.NW;
        if (x1 == fx0 && z0 == fz1) return TileDirection.SE;
        if (x0 == fx1 && z0 == fz1) return TileDirection.SW;

        long dx = ((2L * footprint.X) + footprint.Width - 1) - ((2L * from.X) + agentSize - 1);
        long dz = ((2L * footprint.Z) + footprint.Height - 1) - ((2L * from.Z) + agentSize - 1);
        return Direction(Math.Sign(dx), Math.Sign(dz));
    }

    static IReadOnlyList<TileCoord> OneTileSet(TileCollisionMap map, TileRect footprint, int plane,
        TileInteractionReachPolicy policy)
    {
        var found = new List<TileCoord>(TileReach.Set(map, footprint, plane));
        if (footprint.IsEmpty) return found;
        if (Has(policy, TileInteractionReachPolicy.IncludeDiagonals))
        {
            for (int i = 0; i < Corners.Length; i++)
            {
                (TileDirection direction, int sx, int sz) = Corners[i];
                int x = sx < 0 ? footprint.X : footprint.X1 - 1;
                int z = sz < 0 ? footprint.Z : footprint.Z1 - 1;
                if (!TileCollision.CanStep(map, x, z, plane, direction)) continue;
                found.Add(new TileCoord(x + sx, z + sz, plane));
            }
        }
        if (Has(policy, TileInteractionReachPolicy.IncludeOverlap))
            for (int z = footprint.Z; z < footprint.Z1; z++)
                for (int x = footprint.X; x < footprint.X1; x++)
                    if (TileCollision.CanStand(map, x, z, plane)) found.Add(new TileCoord(x, z, plane));
        return found;
    }

    static bool Has(TileInteractionReachPolicy policy, TileInteractionReachPolicy value) =>
        (policy & value) != 0;

    static bool Overlaps(TileCoord anchor, int size, TileRect footprint) =>
        (long)anchor.X < (long)footprint.X + footprint.Width
        && (long)anchor.X + size > footprint.X
        && (long)anchor.Z < (long)footprint.Z + footprint.Height
        && (long)anchor.Z + size > footprint.Z;

    static TileDirection Direction(int dx, int dz) => (dx, dz) switch
    {
        (-1, -1) => TileDirection.SW,
        (1, -1) => TileDirection.SE,
        (-1, 1) => TileDirection.NW,
        (1, 1) => TileDirection.NE,
        (-1, 0) => TileDirection.W,
        (1, 0) => TileDirection.E,
        (0, -1) => TileDirection.S,
        (0, 1) => TileDirection.N,
        _ => TileDirection.W,
    };
}
