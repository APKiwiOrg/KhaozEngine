using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace KhaozEngine.Collision;

/// <summary>
/// A render-free, broad-phased set of static world colliders (cylinders + oriented boxes) the kinematic capsule
/// resolves against. Backed by the existing <see cref="SpatialHashGrid"/>: each collider is inserted at its
/// centre with its <see cref="WorldCollider.BoundingRadius"/>, so <see cref="Query"/> and the per-tick
/// <c>Resolve</c> only test nearby candidates. Immutable after construction (the static world). Build it
/// from deterministic scatter placements + a hand-placed obstacle list (see
/// <c>KhaozEngine.Terrain.PropColliders.FromScatter</c>). A null or empty set leaves movement unchanged.
/// </summary>
public sealed class WorldColliders
{
    readonly WorldCollider[] colliders;
    readonly SpatialHashGrid grid;

    /// <summary>Builds the set and its broad-phase index. <paramref name="cellSize"/> tunes the spatial hash
    /// (a few colliders per cell is ideal; default 8 world units).</summary>
    public WorldColliders(IEnumerable<WorldCollider> colliders, float cellSize = 8f)
    {
        this.colliders = colliders?.ToArray() ?? Array.Empty<WorldCollider>();
        grid = new SpatialHashGrid(cellSize);
        grid.BeginRebuild(this.colliders.Length);
        for (int i = 0; i < this.colliders.Length; i++)
            grid.Add(i, this.colliders[i].Center, this.colliders[i].BoundingRadius);
    }

    /// <summary>Number of colliders.</summary>
    public int Count => colliders.Length;

    /// <summary>True when there are no colliders (resolution is a no-op).</summary>
    public bool IsEmpty => colliders.Length == 0;

    /// <summary>All colliders, in construction order.</summary>
    public IReadOnlyList<WorldCollider> Colliders => colliders;

    /// <summary>The colliders whose broad-phase cells fall within <paramref name="radius"/> of (x, z): a
    /// superset of those that could overlap a circle of that radius. Allocates a list (for queries/tests);
    /// the per-tick path uses the allocation-free <c>Resolve</c>.</summary>
    public IReadOnlyList<WorldCollider> Query(float x, float z, float radius)
    {
        var list = new List<WorldCollider>();
        int n = grid.QueryCandidates(new Vector2(x, z), radius);
        for (int i = 0; i < n; i++)
            list.Add(colliders[grid.GetQueryIndex(i)]);
        return list;
    }

    /// <summary>Push <paramref name="position"/> (a capsule footprint of <paramref name="radius"/>) out of every
    /// overlapping collider, iterating up to <paramref name="iterations"/> times so corners (resolving one
    /// collider can push into another) settle. Each push removes only the penetrating component, so tangential
    /// motion survives (slide). Returns the corrected XZ; unchanged when clear or when the set is empty.</summary>
    public Vector2 Resolve(Vector2 position, float radius, int iterations = 4)
    {
        if (colliders.Length == 0) return position;
        Vector2 p = position;
        for (int it = 0; it < iterations; it++)
        {
            int n = grid.QueryCandidates(p, radius);
            bool any = false;
            for (int i = 0; i < n; i++)
            {
                if (colliders[grid.GetQueryIndex(i)].Resolve(p, radius, out Vector2 push))
                {
                    p += push;
                    any = true;
                }
            }
            if (!any) break;
        }
        return p;
    }

    /// <summary>Height-aware push-out: like <see cref="Resolve(Vector2,float,int)"/> but a collider is skipped while
    /// the capsule is standing on top of the prop (rather than hitting its side), so a rock/roof is not shoved off.
    /// You count as standing when the feet (<paramref name="footY"/>) are at or above either: the collider's max
    /// solid <see cref="WorldCollider.Top"/> (a flat platform, or a dome's peak), or the walkable surface under the
    /// player (<paramref name="surfaceTop"/>) - a domed prop's surface sits BELOW its max top, so gating only on
    /// <c>Top</c> mis-reads "standing on the dome" as a side hit and pushes you off (the bug this fixes). All within
    /// <paramref name="skin"/>. A thin blocker (a tree: <c>Top = +inf</c>, no surface) is never standable and always
    /// blocks. <paramref name="surfaceTop"/> defaults to +inf (no surface known) -> falls back to <c>Top</c>-only.</summary>
    public Vector2 Resolve(Vector2 position, float radius, float footY,
        float surfaceTop = float.PositiveInfinity, float skin = 0.05f, int iterations = 4)
    {
        if (colliders.Length == 0) return position;
        Vector2 p = position;
        for (int it = 0; it < iterations; it++)
        {
            int n = grid.QueryCandidates(p, radius);
            bool any = false;
            for (int i = 0; i < n; i++)
            {
                WorldCollider c = colliders[grid.GetQueryIndex(i)];
                if (footY >= c.Top - skin) continue;          // at/above the max solid top -> standing, not a side hit
                // At/above the walkable surface under us -> standing on this prop's surface (a dome's surface is below
                // its max top). Only for props that actually provide a surface (finite Top); a thin blocker
                // (Top = +inf) has no surface and must always block, even while the feet rest on a neighbour's surface.
                if (!float.IsInfinity(c.Top) && footY >= surfaceTop - skin) continue;
                if (c.Resolve(p, radius, out Vector2 push)) { p += push; any = true; }
            }
            if (!any) break;
        }
        return p;
    }
}
