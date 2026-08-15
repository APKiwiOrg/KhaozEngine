using System;
using System.Collections.Generic;

namespace KhaozEngine.TileWorld;

/// <summary>Per-region-plane collision storage with world-coordinate access. Not persisted: derived at load
/// and after each edit by <see cref="TileCollisionBaker"/>. Reads outside storage answer
/// <see cref="TileCollisionFlags.Blocked"/>, so an unloaded region is a wall rather than a void, and an
/// <see cref="Or"/> outside storage is DROPPED, so a mirrored wall edge or a footprint spilling past the tracked
/// world cannot allocate a region and turn the whole of it walkable. <see cref="EnsureRegion"/> is the only
/// thing that adds storage.</summary>
public sealed class TileCollisionMap
{
    readonly Dictionary<RegionCoord, ushort[][]> _regions = new();

    /// <summary>Planes each region of this map carries, fixed at construction.</summary>
    public int PlaneCount { get; }
    /// <summary>Every region that currently has storage.</summary>
    public IReadOnlyCollection<RegionCoord> Regions => _regions.Keys;

    /// <summary>Builds an empty map whose regions each hold planeCount planes.</summary>
    public TileCollisionMap(int planeCount)
    {
        if (planeCount < 1) throw new ArgumentOutOfRangeException(nameof(planeCount));
        PlaneCount = planeCount;
    }

    /// <summary>True when this region has storage (an absent one reads as blocked).</summary>
    public bool HasRegion(RegionCoord c) => _regions.ContainsKey(c);

    /// <summary>Allocates zeroed storage for the region, or leaves it alone when it already has some.</summary>
    public void EnsureRegion(RegionCoord c)
    {
        if (_regions.ContainsKey(c)) return;
        var planes = new ushort[PlaneCount][];
        for (int p = 0; p < PlaneCount; p++) planes[p] = new ushort[TileRegion.TileCount];
        _regions.Add(c, planes);
    }

    /// <summary>Drops the region's storage, so its tiles read as blocked again.</summary>
    public void RemoveRegion(RegionCoord c) => _regions.Remove(c);

    /// <summary>Flags at this world tile, <see cref="TileCollisionFlags.Blocked"/> when the region has no
    /// storage or the plane is out of range.</summary>
    public TileCollisionFlags Get(int x, int z, int plane)
    {
        if ((uint)plane >= (uint)PlaneCount) return TileCollisionFlags.Blocked;
        if (!_regions.TryGetValue(RegionCoord.Of(x, z), out ushort[][]? planes)) return TileCollisionFlags.Blocked;
        return (TileCollisionFlags)planes[plane][Index(x, z)];
    }

    /// <summary>Adds flags at this world tile, or does nothing when the region has no storage.</summary>
    public void Or(int x, int z, int plane, TileCollisionFlags flags)
    {
        RequirePlane(plane);
        if (!_regions.TryGetValue(RegionCoord.Of(x, z), out ushort[][]? planes)) return;
        planes[plane][Index(x, z)] |= (ushort)flags;
    }

    /// <summary>Zeroes every tile of the rect on the plane, in regions that have storage.</summary>
    public void Clear(TileRect rect, int plane)
    {
        RequirePlane(plane);
        for (int z = rect.Z; z < rect.Z1; z++)
            for (int x = rect.X; x < rect.X1; x++)
                if (_regions.TryGetValue(RegionCoord.Of(x, z), out ushort[][]? planes))
                    planes[plane][Index(x, z)] = 0;
    }

    void RequirePlane(int plane)
    {
        if ((uint)plane >= (uint)PlaneCount) throw new ArgumentOutOfRangeException(nameof(plane));
    }

    static int Index(int x, int z) =>
        TilePlaneData.Index(RegionCoord.FloorMod(x, TileRegion.Size), RegionCoord.FloorMod(z, TileRegion.Size));
}
