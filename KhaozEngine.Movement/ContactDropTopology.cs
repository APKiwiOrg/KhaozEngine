using System;
using System.Collections.Generic;
using KhaozEngine.Locomotion;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

/// <summary>The format-3 candidate stream. Count before allocating any candidate endpoint array.
/// The frozen Stair recipe and order precede the profile's pure Drop recipe.</summary>
internal static class ContactDropTopology
{
    internal static IEnumerable<NavLink> Enumerate(IReadOnlyList<NavGrid> grids,
        IReadOnlyList<NavTraversalLayer> traversal, MoveTuning tuning, float allowance)
    {
        foreach (NavLink link in NavLayerLinks.EnumerateGrounded(grids, tuning.StepHeight)) yield return link;
        foreach (NavLink link in ContactDropCandidates.Enumerate(grids, traversal, tuning, allowance)) yield return link;
    }

    internal static int Count(IReadOnlyList<NavGrid> grids, IReadOnlyList<NavTraversalLayer> traversal,
        MoveTuning tuning, float allowance)
    {
        int count = 0;
        foreach (NavLink _ in Enumerate(grids, traversal, tuning, allowance))
        {
            if (count == Array.MaxLength) throw new ArgumentOutOfRangeException(nameof(grids), "Too many stored candidates.");
            count++;
        }
        return count;
    }

    internal static IReadOnlyList<NavLink> Generate(IReadOnlyList<NavGrid> grids,
        IReadOnlyList<NavTraversalLayer> traversal, MoveTuning tuning, float allowance, int? counted = null)
    {
        var links = new NavLink[counted ?? Count(grids, traversal, tuning, allowance)];
        int next = 0;
        foreach (NavLink link in Enumerate(grids, traversal, tuning, allowance)) links[next++] = link;
        return links;
    }
}
