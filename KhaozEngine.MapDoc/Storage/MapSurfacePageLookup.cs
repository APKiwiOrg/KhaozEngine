using System;
using System.Collections.Generic;
using System.Linq;

namespace KhaozEngine.MapDoc.Storage;

/// <summary>Manifest metadata is indexed once. Local requests visit only intersecting bounds.</summary>
internal sealed class MapSurfacePageLookup
{
    readonly Dictionary<string, Node> _roots = new(StringComparer.Ordinal);
    internal MapSurfacePageLookup(IEnumerable<MapDirectoryPageRef> pages)
    {
        foreach (var group in pages.GroupBy(p => p.SurfaceId))
            _roots.Add(group.Key, Build(group.ToArray(), 0));
    }
    internal IEnumerable<MapDirectoryPageRef> Covering(string surface, MapSlotRect range, MapPageBudget? budget = null) =>
        _roots.TryGetValue(surface, out Node? node) ? Visit(node, range, budget) : Array.Empty<MapDirectoryPageRef>();
    static Node Build(MapDirectoryPageRef[] pages, int depth)
    {
        if (pages.Length == 1) return new(pages[0].Covers, pages[0], null, null);
        Array.Sort(pages, (a, b) => depth % 2 == 0 ? a.Covers.MinX.CompareTo(b.Covers.MinX) : a.Covers.MinZ.CompareTo(b.Covers.MinZ));
        int mid = pages.Length / 2;
        Node left = Build(pages[..mid], depth + 1), right = Build(pages[mid..], depth + 1);
        return new(new(Math.Min(left.Bounds.MinX, right.Bounds.MinX), Math.Min(left.Bounds.MinZ, right.Bounds.MinZ),
            Math.Max(left.Bounds.MaxXExclusive, right.Bounds.MaxXExclusive), Math.Max(left.Bounds.MaxZExclusive, right.Bounds.MaxZExclusive)),
            null, left, right);
    }
    static IEnumerable<MapDirectoryPageRef> Visit(Node node, MapSlotRect range, MapPageBudget? budget)
    {
        budget?.BeforeMetadata();
        if (!node.Bounds.Overlaps(range)) yield break;
        if (node.Page is { } page) yield return page;
        else
        {
            foreach (var p in Visit(node.Left!, range, budget)) yield return p;
            foreach (var p in Visit(node.Right!, range, budget)) yield return p;
        }
    }
    sealed record Node(MapSlotRect Bounds, MapDirectoryPageRef? Page, Node? Left, Node? Right);
}
