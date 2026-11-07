using System;
using System.Collections.Generic;
using KhaozEngine.Locomotion;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

/// <summary>Frozen disabled recipes and structural constraints on authenticated enabled workloads.</summary>
internal static class TerrainCandidateContract
{
    internal static bool Validate(IReadOnlyList<NavLink> candidates, IReadOnlyList<NavGrid> grids,
        IReadOnlyList<NavTraversalLayer> traversal, NavBakeProfile profile)
    {
        if (!profile.AllowTerrainDescents)
        {
            int next = 0;
            // This frozen recipe uses scalar arithmetic only, never terrain generation or trigonometry.
            foreach (var link in ContactDropTopology.Enumerate(grids, traversal, profile.Tuning, profile.MaxDropMetres))
                if (next == candidates.Count || link != candidates[next++]) return false;
            return next == candidates.Count;
        }
        foreach (var link in candidates)
        {
            int dx = Math.Abs(link.ToX - link.FromX), dz = Math.Abs(link.ToZ - link.FromZ);
            if (link.Kind == NavLinkKind.Stair)
            {
                if (link.FromLayer == link.ToLayer || Math.Max(dx, dz) != 1 ||
                    MathF.Abs(grids[link.ToLayer].SurfaceHeightAt(link.ToX, link.ToZ)!.Value -
                        grids[link.FromLayer].SurfaceHeightAt(link.FromX, link.FromZ)!.Value) > profile.Tuning.StepHeight)
                    return false;
            }
            else
            {
                if (profile.MaxDropMetres <= 0 || profile.Aquatic || profile.Tuning.Controller != MovementControllerKind.Contact ||
                    dx == 0 && dz == 0 || dx != 0 && dz != 0 && dx != dz) return false;
                double drop = (double)grids[link.FromLayer].RestingHeightAt(link.FromX, link.FromZ)!.Value -
                    grids[link.ToLayer].RestingHeightAt(link.ToX, link.ToZ)!.Value;
                if (drop <= profile.Tuning.StepHeight || drop > profile.MaxDropMetres) return false;
            }
        }
        return true;
    }
}
