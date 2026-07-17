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

    /// <summary>
    /// Bakes a step-aware <see cref="NavGrid"/> over the rectangular XZ region
    /// [<paramref name="minX"/>, <paramref name="maxX"/>) by [<paramref name="minZ"/>, <paramref name="maxZ"/>)
    /// from <paramref name="surface"/>. Unlike <see cref="BakeOverworld"/> (which tests a flat band above
    /// analytic terrain and blocks any collider footprint), this reads a per-cell walkable surface height
    /// from <paramref name="surface"/> and marks neighbor traversal walkable when the rise between adjacent
    /// surfaces is within <paramref name="stepHeight"/> and the headroom clears <paramref name="agentHeight"/>,
    /// so low standable props, ramps, and staircases become walkable. Still a single <see cref="NavGrid"/>
    /// layer: the higher side of every step taller than <paramref name="stepHeight"/> bakes blocked, which
    /// keeps the grid planner from crossing it (see <see cref="StepMask"/>). Width and height are derived as
    /// <c>(int)MathF.Ceiling((max - min) / cellSize)</c> per axis (same as <see cref="BakeOverworld"/>).
    /// <paramref name="extraBlocked"/> is an optional gameplay exclusion applied at each cell center.
    /// <paramref name="yMin"/> and <paramref name="yMax"/> pass through to the grid.
    /// </summary>
    public static NavGrid BakeOverworldSteps(
        INavSurfaceProvider surface,
        float minX, float minZ, float maxX, float maxZ,
        float cellSize, float stepHeight, float agentHeight,
        Func<float, float, bool>? extraBlocked = null,
        float yMin = float.NegativeInfinity, float yMax = float.PositiveInfinity)
    {
        if (surface is null) throw new ArgumentNullException(nameof(surface));
        if (maxX <= minX) throw new ArgumentOutOfRangeException(nameof(maxX), maxX, "maxX must be greater than minX.");
        if (maxZ <= minZ) throw new ArgumentOutOfRangeException(nameof(maxZ), maxZ, "maxZ must be greater than minZ.");
        if (cellSize <= 0f) throw new ArgumentOutOfRangeException(nameof(cellSize), cellSize, "Cell size must be positive.");
        if (stepHeight < 0f) throw new ArgumentOutOfRangeException(nameof(stepHeight), stepHeight, "Step height must be non-negative.");
        if (agentHeight < 0f) throw new ArgumentOutOfRangeException(nameof(agentHeight), agentHeight, "Agent height must be non-negative.");

        int width = (int)MathF.Ceiling((maxX - minX) / cellSize);
        int height = (int)MathF.Ceiling((maxZ - minZ) / cellSize);

        NavSurfaceSample Sample(int cx, int cz)
        {
            float wx = minX + (cx + 0.5f) * cellSize;
            float wz = minZ + (cz + 0.5f) * cellSize;

            if (extraBlocked is not null && extraBlocked(wx, wz)) return new NavSurfaceSample(false, 0f, 0f);

            bool ok = surface.TrySample(wx, wz, out float h, out float hr);
            return new NavSurfaceSample(ok, ok ? h : 0f, ok ? hr : 0f);
        }

        return NavGrid.FromSurfaces(width, height, cellSize, minX, minZ, Sample, stepHeight, agentHeight, yMin, yMax);
    }

    /// <summary>
    /// Bakes a step-aware <see cref="NavGrid"/> exactly as <see cref="BakeOverworldSteps"/> does, then
    /// generates same-grid vertical hop links over it (<see cref="NavHopLinks.Generate"/>) and returns a
    /// single-layer <see cref="NavSpace"/> carrying both. A standable top taller than <paramref name="stepHeight"/>
    /// (an unreachable island under the step bake alone) becomes reachable by a hop when its rise is within
    /// <paramref name="jumpHeight"/>. Deterministic. Backward compatible: with no hoppable feature in range the
    /// returned space is identical to <c>NavSpace.Single(BakeOverworldSteps(...))</c>.
    /// </summary>
    public static NavSpace BakeOverworldHops(
        INavSurfaceProvider surface,
        float minX, float minZ, float maxX, float maxZ,
        float cellSize, float stepHeight, float agentHeight, float jumpHeight,
        int maxHopCells = 2,
        Func<float, float, bool>? extraBlocked = null,
        float yMin = float.NegativeInfinity, float yMax = float.PositiveInfinity)
    {
        if (jumpHeight <= stepHeight)
            throw new ArgumentOutOfRangeException(nameof(jumpHeight), jumpHeight, "Jump height must be greater than step height.");
        if (maxHopCells < 2)
            throw new ArgumentOutOfRangeException(nameof(maxHopCells), maxHopCells, "Max hop cells must be at least 2.");

        NavGrid grid = BakeOverworldSteps(
            surface, minX, minZ, maxX, maxZ, cellSize, stepHeight, agentHeight, extraBlocked, yMin, yMax);

        IReadOnlyList<NavLink> hops = NavHopLinks.Generate(grid, stepHeight, jumpHeight, maxHopCells, layer: 0);

        return new NavSpace(new[] { grid }, hops);
    }
}
