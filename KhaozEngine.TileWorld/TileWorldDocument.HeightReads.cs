using System;

namespace KhaozEngine.TileWorld;

public sealed partial class TileWorldDocument
{
    /// <summary>
    /// Reads a corner without loading data. Records unique availability lookups in owner, west, south,
    /// southwest order. A known unloaded candidate blocks lower-priority fallback. Implicit flat heights
    /// in a loaded region are available. No owner returns false rather than the legacy lifted zero.
    /// </summary>
    /// <param name="x">Authored corner X in tile coordinates.</param>
    /// <param name="z">Authored corner Z in tile coordinates.</param>
    /// <param name="plane">Requested authored plane.</param>
    /// <param name="inspectedRegions">Storage for at least four region coordinates.</param>
    /// <param name="inspectedCount">Number of unique coordinates inspected, including absent regions.</param>
    /// <param name="value">Resolved metres, owner and source plane. A derived plane reports plane zero.</param>
    /// <param name="missingRegion">The blocking unloaded candidate, or the primary owner when none resolves.</param>
    internal bool TryReadCornerHeight(int x, int z, int plane, Span<RegionCoord> inspectedRegions,
        out int inspectedCount, out TileCornerHeightRead value, out RegionCoord missingRegion)
    {
        RequirePlane(plane);
        if (inspectedRegions.Length < 4)
            throw new ArgumentException("Corner reads require storage for four inspected regions.", nameof(inspectedRegions));

        value = default;
        if (!TryResolveCorner(x, z, strict: true, inspectedRegions, out inspectedCount,
            out TileRegion? region, out int index, out missingRegion)) return false;

        int sourcePlane = region!.Plane(plane).Heights is null ? 0 : plane;
        value = new TileCornerHeightRead(ReadCorner(region, plane, index) * 0.01f, region.Coord, sourcePlane);
        return true;
    }
}
