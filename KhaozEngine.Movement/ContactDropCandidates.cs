using System;
using System.Collections.Generic;
using KhaozEngine.Locomotion;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

/// <summary>Pure, ordered brink recipe shared by fresh builds, stored acceptance decoding and data-only counts.
/// Enumeration performs no movement queries. Each source emits at most eight directed candidates.</summary>
internal static class ContactDropCandidates
{
    internal static IEnumerable<ContactDropCandidateMeasurement> Measure(IReadOnlyList<NavGrid> grids,
        IReadOnlyList<NavTraversalLayer> layers, MoveTuning tuning, float allowance)
    {
        foreach (NavLink link in Enumerate(grids, layers, tuning, allowance))
            yield return new(link, (double)grids[link.FromLayer].RestingHeightAt(link.FromX, link.FromZ)!.Value -
                grids[link.ToLayer].RestingHeightAt(link.ToX, link.ToZ)!.Value, tuning.WalkSpeed > 0, tuning.RunSpeed > 0);
    }

    internal static IReadOnlyList<NavLink> Generate(IReadOnlyList<NavGrid> grids,
        IReadOnlyList<NavTraversalLayer> layers, MoveTuning tuning, float allowance)
    {
        var links = new List<NavLink>();
        foreach (NavLink link in Enumerate(grids, layers, tuning, allowance))
        {
            if (links.Count == Array.MaxLength) throw new ArgumentOutOfRangeException(nameof(grids), "Too many Drop candidates.");
            links.Add(link);
        }
        return links;
    }

    internal static int Count(IReadOnlyList<NavGrid> grids, IReadOnlyList<NavTraversalLayer> layers,
        MoveTuning tuning, float allowance)
    {
        int count = 0;
        foreach (NavLink _ in Enumerate(grids, layers, tuning, allowance))
        {
            if (count == Array.MaxLength) throw new ArgumentOutOfRangeException(nameof(grids), "Too many Drop candidates.");
            count++;
        }
        return count;
    }

    internal static IEnumerable<NavLink> Enumerate(IReadOnlyList<NavGrid> grids,
        IReadOnlyList<NavTraversalLayer> layers, MoveTuning tuning, float allowance)
    {
        if (!float.IsFinite(allowance) || allowance < 0) throw new ArgumentOutOfRangeException(nameof(allowance));
        if (allowance == 0) yield break;
        if (grids.Count == 0) throw new ArgumentException("Drop candidates need at least one grid.", nameof(grids));
        if (grids.Count != layers.Count) throw new ArgumentException("Traversal layers must match grids.", nameof(layers));
        NavGrid first = grids[0];
        double horizon = 1d + Math.Ceiling(2d * tuning.CapsuleRadius / first.CellSize);
        if (!double.IsFinite(horizon) || horizon > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(tuning), "Drop horizon is not representable.");
        int range = Math.Min((int)horizon, Math.Max(first.Width, first.Height));
        var emitted = new HashSet<NavLink>();
        foreach (NavGrid grid in grids)
        {
            if (!grid.HasRestingHeights || grid.Width != first.Width || grid.Height != first.Height ||
                grid.CellSize != first.CellSize || grid.OriginX != first.OriginX || grid.OriginZ != first.OriginZ ||
                grid.YawRadians != first.YawRadians)
                throw new ArgumentException("Drop grids need co-registered physical resting heights.", nameof(grids));
        }
        for (int source = 0; source < grids.Count; source++)
        {
            NavGrid grid = grids[source];
            for (int z = 0; z < grid.Height; z++)
                for (int x = 0; x < grid.Width; x++)
                {
                    if (!layers[source].IsAccepted(x, z)) continue;
                    float from = grid.RestingHeightAt(x, z)!.Value;
                    emitted.Clear();
                    for (int direction = 0; direction < 8; direction++)
                    {
                        if ((layers[source].ExitMask(x, z) & (1 << direction)) != 0) continue;
                        for (int distance = 1; distance <= range; distance++)
                        {
                            long nextX = x + (long)NavTraversalLayer.NeighborX(direction) * distance;
                            long nextZ = z + (long)NavTraversalLayer.NeighborZ(direction) * distance;
                            if (nextX < 0 || nextZ < 0 || nextX >= grid.Width || nextZ >= grid.Height) break;
                            int nx = (int)nextX, nz = (int)nextZ, target = -1;
                            float highest = float.NegativeInfinity;
                            bool continuation = false;
                            for (int layer = 0; layer < grids.Count; layer++)
                            {
                                if (!layers[layer].IsAccepted(nx, nz)) continue;
                                float to = grids[layer].RestingHeightAt(nx, nz)!.Value;
                                double descent = (double)from - to;
                                if (Math.Abs(descent) <= tuning.StepHeight) continuation = true;
                                if (descent > tuning.StepHeight && descent <= allowance && to > highest)
                                { highest = to; target = layer; }
                            }
                            if (continuation) break;
                            if (target < 0) continue;
                            var link = new NavLink(source, x, z, target, nx, nz) { Kind = NavLinkKind.Drop };
                            if (emitted.Add(link)) yield return link;
                            break;
                        }
                    }
                }
        }
    }
}
