using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Collision;
using KhaozEngine.Terrain;

namespace KhaozEngine.Navigation;

/// <summary>
/// Bridges the existing overworld representation (analytic terrain slope plus static XZ prop colliders)
/// into a baked <see cref="NavGrid"/>, so pathfinding never re-touches <see cref="TerrainCollision"/> or
/// <see cref="WorldColliders"/> at query time.
/// </summary>
public static class NavGridBaker
{
    /// <summary>
    /// Bakes a <see cref="NavGrid"/> over the rectangular XZ region
    /// [<paramref name="minX"/>, <paramref name="maxX"/>) by [<paramref name="minZ"/>, <paramref name="maxZ"/>)
    /// from <paramref name="terrain"/> and <paramref name="colliders"/>. A cell is blocked when any of: the
    /// terrain slope at its center exceeds <paramref name="maxSlopeRadians"/> (per
    /// <see cref="TerrainCollision.IsWalkable"/>), <paramref name="extraBlocked"/> returns true for its
    /// center (an optional gameplay-authored exclusion, e.g. a scripted no-go zone), or a nearby
    /// <see cref="WorldCollider"/> overlaps a probe circle at its center of radius
    /// <c>cellSize * 0.70710678</c>, half the cell diagonal. That inflate radius is the conservative
    /// center-point test: it is exactly large enough that a collider touching any corner of the cell is
    /// caught, so the bake never marks a cell passable when part of it is actually covered, at the cost of
    /// occasionally blocking a cell a collider only clips at a corner. v1 conservatism: a collider blocks
    /// regardless of its finite <see cref="WorldCollider.Top"/>. An overhang or low platform a creature
    /// could duck under is still treated as fully solid for navigation (height-aware clearance is a later
    /// pass). Width and height are derived as <c>(int)MathF.Ceiling((max - min) / cellSize)</c> on each axis,
    /// so the baked region may extend slightly past <paramref name="maxX"/> or <paramref name="maxZ"/> when
    /// the span is not an exact multiple of <paramref name="cellSize"/>. <paramref name="yMin"/> and
    /// <paramref name="yMax"/> pass straight through to the resulting <see cref="NavGrid"/>, per
    /// <see cref="NavGrid.FromWalkable"/>.
    /// </summary>
    public static NavGrid BakeOverworld(
        TerrainCollision terrain,
        WorldColliders colliders,
        float minX, float minZ, float maxX, float maxZ,
        float cellSize, float maxSlopeRadians,
        Func<float, float, bool>? extraBlocked = null,
        float yMin = float.NegativeInfinity, float yMax = float.PositiveInfinity)
    {
        if (terrain is null) throw new ArgumentNullException(nameof(terrain));
        if (colliders is null) throw new ArgumentNullException(nameof(colliders));
        if (maxX <= minX) throw new ArgumentOutOfRangeException(nameof(maxX), maxX, "maxX must be greater than minX.");
        if (maxZ <= minZ) throw new ArgumentOutOfRangeException(nameof(maxZ), maxZ, "maxZ must be greater than minZ.");
        if (cellSize <= 0f) throw new ArgumentOutOfRangeException(nameof(cellSize), cellSize, "Cell size must be positive.");

        int width = (int)MathF.Ceiling((maxX - minX) / cellSize);
        int height = (int)MathF.Ceiling((maxZ - minZ) / cellSize);
        float inflate = cellSize * 0.70710678f;

        bool Walkable(int cx, int cz)
        {
            float wx = minX + (cx + 0.5f) * cellSize;
            float wz = minZ + (cz + 0.5f) * cellSize;

            if (!terrain.IsWalkable(wx, wz, maxSlopeRadians)) return false;
            if (extraBlocked is not null && extraBlocked(wx, wz)) return false;

            IReadOnlyList<WorldCollider> candidates = colliders.Query(wx, wz, inflate);
            for (int i = 0; i < candidates.Count; i++)
                if (candidates[i].Resolve(new Vector2(wx, wz), inflate, out _)) return false;

            return true;
        }

        return NavGrid.FromWalkable(width, height, cellSize, minX, minZ, Walkable, yMin, yMax);
    }
}
