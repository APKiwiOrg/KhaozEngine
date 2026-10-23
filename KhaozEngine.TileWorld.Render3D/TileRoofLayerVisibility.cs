using System;
using System.Collections.Generic;
using KhaozEngine.Terrain;

namespace KhaozEngine.TileWorld;

/// <summary>Reads roof metadata from the same generation history retained for cluster presentation.</summary>
internal sealed class TileRoofLayerVisibility : IPropClusterVisibility
{
    readonly Dictionary<PropClusterKey, SortedDictionary<long, TilePropLayerSnapshot>> _snapshots;
    // Validation makes roof policy a property of the whole layer, independent of retained generations.
    readonly HashSet<string> _roofLayers = new(StringComparer.Ordinal);

    public TileRoofLayerVisibility(Dictionary<PropClusterKey, SortedDictionary<long, TilePropLayerSnapshot>> snapshots,
        IReadOnlyList<TileWorldPropClusters.ValidatedLayer> layers)
    {
        _snapshots = snapshots;
        for (int i = 0; i < layers.Count; i++)
            if (layers[i].IsRoofLayer) _roofLayers.Add(layers[i].Definition.Id);
    }

    public TileRoofVisibilityRule Rule { get; set; }

    public bool RequiresVisibility => _roofLayers.Count > 0;

    public bool Controls(PropClusterKey key) => _roofLayers.Contains(key.LayerId);

    public int PlacementCount(PropClusterKey key, long generation) => Snapshot(key, generation).Placements.Count;

    public bool IsHidden(PropClusterKey key, long generation, int placementIndex) =>
        IsHidden(Snapshot(key, generation), placementIndex);

    public bool IsHidden(TilePropLayerSnapshot snapshot, int placementIndex) => snapshot.IsRoofLayer
        && Rule.IsHidden(snapshot.RoofFootprints[placementIndex], snapshot.Plane);

    TilePropLayerSnapshot Snapshot(PropClusterKey key, long generation) => _snapshots[key][generation];
}
