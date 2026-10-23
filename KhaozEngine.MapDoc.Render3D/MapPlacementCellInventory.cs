using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Acquires complete logical cells selected by a window before mesh decode and proxy preparation.</summary>
internal sealed class MapPlacementCellInventory
{
    internal sealed record Cell(PropClusterKey Key, MapScopedScene Scene, MapPlacementRouting Routing,
        IReadOnlyList<MapResolvedPlacement> Members, IReadOnlySet<string> RoofIds, MapRoofEvaluationBuffer? RoofEvaluation);

    readonly Dictionary<PropClusterKey, Cell> _cells;
    MapPlacementCellInventory(Dictionary<PropClusterKey, Cell> cells) => _cells = cells;
    internal IReadOnlyCollection<Cell> Cells => _cells.Values;
    internal Cell Get(PropClusterKey key) => _cells[key];

    internal static MapPlacementCellInventory Capture(MapScopedScene scene, MapPlacementRouting routing)
    {
        routing.Validate(scene);
        var layers = routing.ClusterLayers;
        int selectedCount = scene.Placements.Count(p => routing.Entries[p.PlacementId] == MapPlacementRoute.Cluster);
        if (selectedCount == 0) return new(new());
        var admission = MapSceneAdmission.Current ?? new MapSceneAdmission(scene.Source.Data.Limits);
        using var activation = admission.Enter();
        admission.ReserveBytes(checked(128L + 64L * selectedCount));
        var roofs = scene.BuildingIntent.Roofs.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var keys = layers.SelectMany(layer => scene.Placements
                .Where(p => layer.AssetIds.Contains(p.AssetId) && routing.Entries[p.PlacementId] == MapPlacementRoute.Cluster)
                .Select(p => MapPlacementCells.Key(layer.Id, NativeMapPlacements.Pose(scene, p), scene.Source.Data.TileSize, roofs.Contains(p.PlacementId))))
            .Distinct().OrderBy(k => k.LayerId, StringComparer.Ordinal).ThenBy(k => k.X).ThenBy(k => k.Z).ToArray();
        var cells = new Dictionary<PropClusterKey, Cell>();
        foreach (var key in keys) cells.Add(key, Acquire(scene, routing, key));
        return new(cells);
    }

    internal static Cell Acquire(MapScopedScene scene, MapPlacementRouting routing, PropClusterKey key)
    {
        var layer = routing.ClusterLayer(key.LayerId)
            ?? throw new MapDocumentException("Native cluster has no matching caller layer.");
        var admission = MapSceneAdmission.Current ?? new MapSceneAdmission(scene.Source.Data.Limits);
        using var activation = admission.Enter();
        var coverage = MapPlacementCells.Coverage(key, scene.Source.Data.TileSize);
        var result = scene.AcquireDependencies(scene.Scope with
        {
            DrawBounds = coverage,
            Surfaces = key.Cohort == 1 ? MapRoofPolicyAcquisition.Scope(coverage, scene.Scope.Surfaces.Limits) : scene.Scope.Surfaces,
            Channels = MapSceneChannels.Placements | MapSceneChannels.Annotations | (key.Cohort == 1 ? MapSceneChannels.Rooms : 0),
            MotionPaddingMetres = 0
        });
        var complete = result.Scene ?? throw new MapSceneSourceException(result.Status, "Native cluster cell discovery refused. " + string.Join("\n", result.Findings), result.Capacity);
        MapRoofEvaluationBuffer? evaluation = null;
        if (key.Cohort == 1)
        {
            complete = MapRoofPolicyAcquisition.Complete(complete);
            var prepared = MapRoofEvaluationBuffer.Prepare(complete, admission, requireCompletePolicy: true);
            evaluation = prepared.Buffer ?? throw new MapSceneSourceException(prepared.Status, prepared.Detail ?? "Canonical roof policy refused.", prepared.Capacity);
        }
        admission.ReserveBytes(checked(128L + 64L * complete.Placements.Count));
        var cellRouting = routing.ForScene(complete);
        var roofIds = complete.BuildingIntent.Roofs.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var members = complete.Placements.Where(p => layer.AssetIds.Contains(p.AssetId) &&
                cellRouting.Entries[p.PlacementId] == MapPlacementRoute.Cluster &&
                MapPlacementCells.Key(layer.Id, NativeMapPlacements.Pose(complete, p), scene.Source.Data.TileSize, roofIds.Contains(p.PlacementId)) == key)
            .OrderBy(p => p.PlacementId, StringComparer.Ordinal).ToArray();
        if (members.Length == 0)
            throw new MapSceneSourceException(MapSceneStatus.Invalid, "Native cluster cell discovery omitted its selected members.");
        admission.ReserveBytes(checked(32L * members.Length));
        return new(key, complete, cellRouting, Array.AsReadOnly(members), roofIds, evaluation);
    }

    internal static void RequireMembers(Cell cell, NativePropClusterRequest request)
    {
        if (!cell.Members.Select(p => p.PlacementId).SequenceEqual(request.Instances.Select(i => i.Id)))
            throw new MapDocumentException("Native cluster request omits canonical cell members.");
    }
}
