using System.Collections.Generic;

namespace KhaozEngine.TileWorld;

/// <summary>Retains the materialised region ring for a snapshot sequence, settling changes before capture.</summary>
internal sealed class TileWorldSequenceRegions(TileWorldView view, TileWorldDocument doc)
{
    readonly HashSet<RegionCoord> _desired = new();
    readonly List<RegionCoord> _loaded = new();
    RegionCoord? _centre;

    internal void Update(RegionCoord centre)
    {
        if (_centre == centre) return;
        _centre = centre;
        _desired.Clear();
        int radius = TileWorldSnapshot.PerspectiveRegionRadius;
        for (int dz = -radius; dz <= radius; dz++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                RegionCoord region = centre.Offset(dx, dz);
                if (doc.GetRegion(region) is not null) _desired.Add(region);
            }
        _loaded.Clear();
        view.CollectLoadedRegions(_loaded);
        foreach (RegionCoord region in _loaded)
            if (!_desired.Contains(region)) view.UnloadRegion(region);
        foreach (RegionCoord region in _desired) view.LoadRegion(region, TileRegionResidencyState.Gameplay);
        // A sequence must not capture a frame before a newly visible region's rebuild has settled.
        view.SettleForCapture(centre);
    }
}
