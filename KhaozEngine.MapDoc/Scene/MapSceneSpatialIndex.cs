using KhaozEngine.Diagnostics;
using DemandDiagnostics = KhaozEngine.Diagnostics.CapacityDemandDiagnostics;
using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Editing;

namespace KhaozEngine.MapDoc.Scene;

/// <summary>A stable median hierarchy. Wide resources remain leaves, with no global overflow scan.</summary>
internal sealed class MapSceneSpatialIndex
{
    const int LeafCapacity = 8;
    readonly Node? _root;
    internal MapSceneSpatialIndex(IReadOnlyList<MapSceneResourceMetadata> rows) => _root = Build(rows.ToArray());

    internal IReadOnlyList<MapSceneResourceKey> Query(MapBox3 bounds, out long nodesVisited, int maxResources = int.MaxValue, Func<MapSceneResourceKey, bool>? accepts = null)
    {
        long visits = 0;
        var result = new List<MapSceneResourceKey>();
        void Visit(Node node)
        {
            visits++;
            if (!Touches(node.Bounds, bounds)) return;
            if (node.Rows is { } rows)
            {
                foreach (var row in rows)
                    if (Touches(row.Bounds, bounds) && (accepts is null || accepts(row.Key)))
                    {
                        if (DemandDiagnostics.Check(result, "MapSceneSpatialIndex.MaxSelectedResources.1", "MaxSelectedResources", result.Count, 1, maxResources, result.Count >= maxResources, CapacityDemandLifetime.Inventory)) throw new MapSceneCapacityException(nameof(MapSceneLimits.MaxSelectedResources), (long)result.Count + 1, maxResources);
                        result.Add(row.Key);
                    }
            }
            else { Visit(node.Left!); Visit(node.Right!); }
        }
        nodesVisited = 0;
        try
        {
            if (_root is not null) Visit(_root);
            return Array.AsReadOnly(result.OrderBy(k => k.Kind, StringComparer.Ordinal).ThenBy(k => k.Id, StringComparer.Ordinal).ToArray());
        }
        finally { nodesVisited = visits; }
    }

    static Node? Build(MapSceneResourceMetadata[] rows)
    {
        if (rows.Length == 0) return null;
        var bounds = rows[0].Bounds;
        foreach (var row in rows.AsSpan(1)) bounds = bounds.Union(row.Bounds);
        if (rows.Length <= LeafCapacity) return new(bounds, rows, null, null);
        bool x = bounds.MaxX - bounds.MinX >= bounds.MaxZ - bounds.MinZ;
        var sorted = rows.OrderBy(r => x ? r.Bounds.MinX / 2 + r.Bounds.MaxX / 2 : r.Bounds.MinZ / 2 + r.Bounds.MaxZ / 2)
            .ThenBy(r => r.Key.Kind, StringComparer.Ordinal).ThenBy(r => r.Key.Id, StringComparer.Ordinal).ToArray();
        int middle = sorted.Length / 2;
        return new(bounds, null, Build(sorted[..middle]), Build(sorted[middle..]));
    }

    internal static bool Touches(MapBox3 a, MapBox3 b) => a.MinX <= b.MaxX && a.MaxX >= b.MinX && a.MinZ <= b.MaxZ && a.MaxZ >= b.MinZ;
    sealed record Node(MapBox3 Bounds, MapSceneResourceMetadata[]? Rows, Node? Left, Node? Right);
}
