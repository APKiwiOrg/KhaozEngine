using System;

namespace KhaozEngine.TileWorld;

public sealed partial class TileWorldDocument
{
    /// <summary><see cref="PlaneHeight"/> in the lattice's centimetre unit.</summary>
    public int PlaneHeightCm => (int)MathF.Round(PlaneHeight * 100f);

    /// <summary>Height in centimetres of the SW corner of tile (x, z) on the plane, following the spec's
    /// one-global-lattice rule: read the owning region, else edge-extend from the region to the west, south, or
    /// south-west, else 0. A plane with no authored heights derives from plane 0 plus its lift.</summary>
    public short CornerHeightCm(int x, int z, int plane)
    {
        RequirePlane(plane);
        if (TryResolveCorner(x, z, out TileRegion? region, out int index))
            return ReadCorner(region!, plane, index);
        return (short)Math.Clamp(plane * PlaneHeightCm, short.MinValue, short.MaxValue);
    }

    /// <summary>The same corner height in metres.</summary>
    public float CornerHeight(int x, int z, int plane) => CornerHeightCm(x, z, plane) * 0.01f;

    /// <summary>Writes a corner. Throws when the corner's own region does not exist (edge-extended reads are
    /// not writable). The first write on a higher plane materialises that plane's derived lattice first.</summary>
    public void SetCornerHeightCm(int x, int z, int plane, short cm)
    {
        if (!TrySetCornerHeightCm(x, z, plane, cm))
            throw new TileWorldException($"corner ({x}, {z}) is in region {RegionCoord.Of(x, z)}, which does not exist. Create it first.");
    }

    /// <summary>Writes a corner, returning false instead of throwing when its region does not exist.</summary>
    public bool TrySetCornerHeightCm(int x, int z, int plane, short cm)
    {
        RequirePlane(plane);
        TileRegion? region = RegionAt(x, z);
        if (region is null) return false;
        TilePlaneData p = region.Plane(plane);
        if (p.Heights is null && plane > 0) FillDerivedHeights(region, plane);
        p.HeightsOrAlloc()[LocalIndex(x, z)] = cm;
        region.Dirty = true;
        return true;
    }

    /// <summary>Allocates plane <paramref name="plane"/>'s heights from plane 0 plus the plane lift.</summary>
    public void FillDerivedHeights(TileRegion region, int plane)
    {
        ArgumentNullException.ThrowIfNull(region);
        short[] h = region.Plane(plane).HeightsOrAlloc();
        short[]? h0 = region.Plane(0).Heights;
        int lift = plane * PlaneHeightCm;
        for (int i = 0; i < h.Length; i++)
            h[i] = (short)Math.Clamp((h0?[i] ?? 0) + lift, short.MinValue, short.MaxValue);
        region.Dirty = true;
    }

    /// <summary>Bilinear height in metres at a world position (world units = tiles * <see cref="TileSize"/>).</summary>
    public float HeightAt(float worldX, float worldZ, int plane)
    {
        float tx = worldX / TileSize, tz = worldZ / TileSize;
        int x0 = (int)MathF.Floor(tx), z0 = (int)MathF.Floor(tz);
        float fx = tx - x0, fz = tz - z0;
        float h00 = CornerHeight(x0, z0, plane), h10 = CornerHeight(x0 + 1, z0, plane);
        float h01 = CornerHeight(x0, z0 + 1, plane), h11 = CornerHeight(x0 + 1, z0 + 1, plane);
        float south = h00 + (h10 - h00) * fx;
        float north = h01 + (h11 - h01) * fx;
        return south + (north - south) * fz;
    }

    short ReadCorner(TileRegion region, int plane, int index)
    {
        short[]? h = region.Plane(plane).Heights;
        if (h is not null) return h[index];
        int baseCm = region.Plane(0).Heights?[index] ?? 0;
        return (short)Math.Clamp(baseCm + plane * PlaneHeightCm, short.MinValue, short.MaxValue);
    }

    bool TryResolveCorner(int x, int z, out TileRegion? region, out int index)
    {
        int lx = RegionCoord.FloorMod(x, TileRegion.Size), lz = RegionCoord.FloorMod(z, TileRegion.Size);
        if ((region = RegionAt(x, z)) is not null) { index = TilePlaneData.Index(lx, lz); return true; }
        if ((region = RegionAt(x - 1, z)) is not null) { index = TilePlaneData.Index(TileRegion.Size - 1, lz); return true; }
        if ((region = RegionAt(x, z - 1)) is not null) { index = TilePlaneData.Index(lx, TileRegion.Size - 1); return true; }
        if ((region = RegionAt(x - 1, z - 1)) is not null) { index = TilePlaneData.Index(TileRegion.Size - 1, TileRegion.Size - 1); return true; }
        index = 0;
        return false;
    }
}
