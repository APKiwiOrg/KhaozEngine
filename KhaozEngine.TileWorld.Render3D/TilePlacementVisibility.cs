using System.Collections.Generic;
using KhaozEngine.Terrain;

namespace KhaozEngine.TileWorld;

/// <summary>Every per-placement visibility rule a tile-world view applies to its prop-layer clusters: the roof rule's
/// hiding and the camera fade. Both read the same retained generation history, so a placement index always names the
/// object it named when that generation was built.</summary>
internal sealed class TilePlacementVisibility : IPropClusterVisibility
{
    readonly TileRoofLayerVisibility _roofs;
    readonly Dictionary<PropClusterKey, SortedDictionary<long, TilePropLayerSnapshot>> _snapshots;
    readonly TileCameraFadeState _fades;

    public TilePlacementVisibility(TileRoofLayerVisibility roofs,
        Dictionary<PropClusterKey, SortedDictionary<long, TilePropLayerSnapshot>> snapshots, TileCameraFadeState fades)
    {
        _roofs = roofs;
        _snapshots = snapshots;
        _fades = fades;
    }

    // A configured camera fade needs visibility-aware submission even on a frame with nothing faded, so an adapter
    // that cannot honour it refuses at the first draw rather than when the camera first enters an object.
    public bool RequiresVisibility => _roofs.RequiresVisibility || _fades.Enabled;

    public bool Controls(PropClusterKey key) =>
        _roofs.Controls(key) || _fades.AnyOn(new RegionCoord(key.X, key.Z), key.Plane);

    public int PlacementCount(PropClusterKey key, long generation) => Snapshot(key, generation).Placements.Count;

    public bool IsHidden(PropClusterKey key, long generation, int placementIndex) =>
        _roofs.IsHidden(Snapshot(key, generation), placementIndex);

    public float ColourFade(PropClusterKey key, long generation, int placementIndex) =>
        _fades.Count > 0 && _fades.TryGet(Snapshot(key, generation).ObjectIds[placementIndex], out float fade)
            ? fade : 0f;

    TilePropLayerSnapshot Snapshot(PropClusterKey key, long generation) => _snapshots[key][generation];
}
