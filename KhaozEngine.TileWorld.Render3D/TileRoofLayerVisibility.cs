using System.Collections.Generic;
using KhaozEngine.Terrain;

namespace KhaozEngine.TileWorld;

/// <summary>Reads roof metadata from the same generation history retained for cluster presentation.</summary>
internal sealed class TileRoofLayerVisibility(
    Dictionary<PropClusterKey, SortedDictionary<long, TilePropLayerSnapshot>> snapshots) : IPropClusterVisibility
{
    public TileRoofVisibilityRule Rule { get; set; }

    public bool RequiresVisibility
    {
        get
        {
            foreach (PropClusterKey key in snapshots.Keys)
                if (Controls(key)) return true;
            return false;
        }
    }

    public bool Controls(PropClusterKey key)
    {
        if (!snapshots.TryGetValue(key, out var generations)) return false;
        foreach (TilePropLayerSnapshot snapshot in generations.Values) return snapshot.IsRoofLayer;
        return false;
    }

    public int PlacementCount(PropClusterKey key, long generation) => Snapshot(key, generation).Placements.Count;

    public bool IsHidden(PropClusterKey key, long generation, int placementIndex)
    {
        TilePropLayerSnapshot snapshot = Snapshot(key, generation);
        return Rule.IsHidden(snapshot.RoofFootprints[placementIndex], snapshot.Plane);
    }

    TilePropLayerSnapshot Snapshot(PropClusterKey key, long generation) => snapshots[key][generation];
}
