using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace KhaozEngine.Collision;

/// <summary>
/// A render-free, broad-phased set of placed prop surfaces. Backed by the existing <see cref="SpatialHashGrid"/>
/// (each surface inserted at its centre with its bounding radius), so <see cref="Query"/> only samples nearby
/// surfaces. <see cref="Query"/> returns the maximum top height of the surfaces covering (x, z) - a player where
/// two props overlap stands on the higher - or null when none cover it. Immutable; null/empty = no surfaces
/// (movement uses terrain only). Build it from scatter placements + an obstacle list (see
/// <c>KhaozEngine.Terrain.PropSurfaces.FromScatter</c>).
/// </summary>
public sealed class WorldSurfaces
{
    readonly WorldSurface[] surfaces;
    readonly SpatialHashGrid grid;

    public WorldSurfaces(IEnumerable<WorldSurface> surfaces, float cellSize = 8f)
    {
        this.surfaces = surfaces?.ToArray() ?? Array.Empty<WorldSurface>();
        grid = new SpatialHashGrid(cellSize);
        grid.BeginRebuild(this.surfaces.Length);
        for (int i = 0; i < this.surfaces.Length; i++)
            grid.Add(i, this.surfaces[i].Center, this.surfaces[i].BoundingRadius);
    }

    /// <summary>Number of surfaces.</summary>
    public int Count => surfaces.Length;

    /// <summary>True when there are no surfaces (query is always null).</summary>
    public bool IsEmpty => surfaces.Length == 0;

    /// <summary>All surfaces, in construction order.</summary>
    public IReadOnlyList<WorldSurface> Surfaces => surfaces;

    /// <summary>The max top height of the surfaces covering (x, z), or null when none cover it.</summary>
    public float? Query(float x, float z)
    {
        if (surfaces.Length == 0) return null;
        int n = grid.QueryCandidates(new Vector2(x, z), 0f);
        float best = float.NegativeInfinity; bool any = false;
        for (int k = 0; k < n; k++)
        {
            float? h = surfaces[grid.GetQueryIndex(k)].SampleWorld(x, z);
            if (h.HasValue && (!any || h.Value > best)) { best = h.Value; any = true; }
        }
        return any ? best : (float?)null;
    }
}
