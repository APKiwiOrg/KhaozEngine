using System;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.Primitives;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapEditor;

/// <summary>GPU-free picking over a <see cref="MapDocument"/>: a camera ray against placement AABBs
/// (<see cref="RayMath.IntersectAabb"/>), spawn marker boxes, and the analytic terrain
/// (<see cref="TerrainRaycast.Raycast(TerrainField, Vector3, Vector3, float, out Vector3, float, int)"/>).
/// Nearest hit wins by entry distance, and placements and spawns
/// beat terrain at equal footing (an object entry T that ties the terrain crossing still selects the object).
/// The picker never normalizes the ray: T values come back in units of the direction's length, so a caller
/// that passes a normalized direction reads T directly as a world distance (the caller-normalizes contract).</summary>
public static class EditorPicking
{
    /// <summary>Fixed spawn marker box: 1.5 world units tall from the ground.</summary>
    const float SpawnHeight = 1.5f;

    /// <summary>Fixed spawn marker box half-width on X and Z (0.5, so a 1.0-unit-wide box).</summary>
    const float SpawnHalfWidth = 0.5f;

    /// <summary>A single pick outcome. <paramref name="Kind"/> is <see cref="SelectionKind.None"/> for a terrain
    /// hit (the ground is not a selectable element) with an empty <paramref name="Id"/>, otherwise the element's
    /// kind and stable id. <paramref name="Point"/> is the ray/geometry hit point and <paramref name="T"/> the
    /// entry distance in units of the ray direction's length.</summary>
    public readonly record struct PickResult(SelectionKind Kind, string Id, Vector3 Point, float T);

    /// <summary>Picks the nearest document element or ground point along a ray. Each placement is boxed as an AABB
    /// centred at (X, groundY + h/2, Z) with half-extents (h*0.3, h/2, h*0.3), where h = heightOf(kind) * scale is
    /// the world-space box height (from the game's AssetManifest HeightMeters times the placement scale), the pick
    /// box is therefore 0.6*h wide, and groundY is the placement's explicit Y or, when null, the terrain height
    /// sampled at (X, Z). Each NPC spawn and each player spawn is boxed as a fixed 1.5-tall, 1.0-wide box based at
    /// the sampled ground. The terrain is raycast for a fallback ground hit. Placements and spawns beat terrain at
    /// equal T, and the smallest
    /// entry T wins overall. Returns false when nothing is hit within <paramref name="maxDistance"/> (T units).
    /// Direction normalization is the caller's job.
    /// <para><paramref name="visible"/>, when supplied, filters out unpickable elements: a placement or spawn for
    /// which <c>visible(kind, id)</c> is false is skipped (the editor hides it from the viewport, so a hidden thing
    /// is not selectable by clicking, though the terrain under it still hits). Null means every element is
    /// pickable.</para></summary>
    public static bool Pick(MapDocument doc, TerrainField field, Vector3 origin, Vector3 direction,
        float maxDistance, Func<string, float> heightOf, out PickResult result,
        Func<SelectionKind, string, bool>? visible = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(heightOf);

        bool haveBest = false;
        float bestT = 0f;
        SelectionKind bestKind = SelectionKind.None;
        string bestId = "";

        foreach (MapPlacement p in doc.Placements)
        {
            if (visible is not null && !visible(SelectionKind.Placement, p.Id)) continue;   // hidden: not pickable
            float h = heightOf(p.Kind) * p.Scale;
            float groundY = p.Y ?? field.SampleHeight(p.X, p.Z);
            float half = h * 0.3f;
            var min = new Vector3(p.X - half, groundY, p.Z - half);
            var max = new Vector3(p.X + half, groundY + h, p.Z + half);
            if (RayMath.IntersectAabb(origin, direction, min, max, out float tNear) && tNear <= maxDistance
                && (!haveBest || tNear < bestT))
            {
                haveBest = true;
                bestT = tNear;
                bestKind = SelectionKind.Placement;
                bestId = p.Id;
            }
        }

        foreach (MapSpawn s in doc.Spawns)
        {
            if (visible is not null && !visible(SelectionKind.Spawn, s.Id)) continue;   // hidden: not pickable
            float groundY = field.SampleHeight(s.X, s.Z);
            var min = new Vector3(s.X - SpawnHalfWidth, groundY, s.Z - SpawnHalfWidth);
            var max = new Vector3(s.X + SpawnHalfWidth, groundY + SpawnHeight, s.Z + SpawnHalfWidth);
            if (RayMath.IntersectAabb(origin, direction, min, max, out float tNear) && tNear <= maxDistance
                && (!haveBest || tNear < bestT))
            {
                haveBest = true;
                bestT = tNear;
                bestKind = SelectionKind.Spawn;
                bestId = s.Id;
            }
        }

        foreach (MapPlayerSpawn s in doc.PlayerSpawns)
        {
            if (visible is not null && !visible(SelectionKind.PlayerSpawn, s.Id)) continue;   // hidden: not pickable
            float groundY = field.SampleHeight(s.X, s.Z);
            var min = new Vector3(s.X - SpawnHalfWidth, groundY, s.Z - SpawnHalfWidth);
            var max = new Vector3(s.X + SpawnHalfWidth, groundY + SpawnHeight, s.Z + SpawnHalfWidth);
            if (RayMath.IntersectAabb(origin, direction, min, max, out float tNear) && tNear <= maxDistance
                && (!haveBest || tNear < bestT))
            {
                haveBest = true;
                bestT = tNear;
                bestKind = SelectionKind.PlayerSpawn;
                bestId = s.Id;
            }
        }

        bool haveTerrain = PickTerrain(field, origin, direction, maxDistance, out Vector3 terrainPoint);
        float terrainT = haveTerrain ? ProjectT(origin, direction, terrainPoint) : 0f;

        // Objects beat terrain at equal footing (bestT <= terrainT), else the nearer of the two wins.
        if (haveBest && (!haveTerrain || bestT <= terrainT))
        {
            result = new PickResult(bestKind, bestId, origin + direction * bestT, bestT);
            return true;
        }

        if (haveTerrain)
        {
            result = new PickResult(SelectionKind.None, "", terrainPoint, terrainT);
            return true;
        }

        result = new PickResult(SelectionKind.None, "", Vector3.Zero, 0f);
        return false;
    }

    /// <summary>Raycasts the analytic terrain for a ground hit, returning the surface point. A thin wrapper over
    /// <see cref="TerrainRaycast.Raycast(TerrainField, Vector3, Vector3, float, out Vector3, float, int)"/>
    /// (default coarse step). Returns false when the ray stays above the
    /// surface for the whole <paramref name="maxDistance"/> (in units of the direction's length). Direction
    /// normalization is the caller's job.</summary>
    public static bool PickTerrain(TerrainField field, Vector3 origin, Vector3 direction,
        float maxDistance, out Vector3 point) =>
        TerrainRaycast.Raycast(field, origin, direction, maxDistance, out point);

    /// <summary>Projects a hit point back onto the ray to recover its T (in units of the direction's length), so
    /// a terrain point compares on the same footing as the AABB entry distances. Zero for a degenerate ray.</summary>
    static float ProjectT(Vector3 origin, Vector3 direction, Vector3 point)
    {
        float d2 = Vector3.Dot(direction, direction);
        return d2 > 0f ? Vector3.Dot(point - origin, direction) / d2 : 0f;
    }
}
