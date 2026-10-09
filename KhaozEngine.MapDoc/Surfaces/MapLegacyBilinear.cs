using System;

namespace KhaozEngine.MapDoc.Surfaces;

/// <summary>The released legacy height arithmetic, isolated from canonical exact geometry.</summary>
public static class MapLegacyBilinear
{
    public static float Evaluate(int h00Cm, int h10Cm, int h01Cm, int h11Cm, float fx, float fz)
    {
        float h00 = h00Cm * 0.01f, h10 = h10Cm * 0.01f, h01 = h01Cm * 0.01f, h11 = h11Cm * 0.01f;
        float south = h00 + (h10 - h00) * fx;
        float north = h01 + (h11 - h01) * fx;
        return south + (north - south) * fz;
    }

    public static float HeightMetres(MapSurfaceRef surface, MapSurfacePatch patch, MapExactXz world)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(patch);
        if (surface.Frame != MapLatticeFrame.ImportedMetreCentimetre ||
            surface.PresencePolicy != MapPresencePolicy.LegacyTileWorld || surface.Id != patch.Key.SurfaceId)
            throw new ArgumentException("legacy recipe: surface");
        MapExactValue tx = world.X, tz = world.Z.Negate();
        long x0 = tx.Floor(), z0 = tz.Floor();
        Int128 localX = (Int128)x0 - (Int128)patch.Key.SlotX * 64 - patch.CellMinX;
        Int128 localZ = (Int128)z0 - (Int128)patch.Key.SlotZ * 64 - patch.CellMinZ;
        if (localX < 0 || localX >= patch.Width || localZ < 0 || localZ >= patch.Depth)
            throw new ArgumentException("legacy recipe: cell outside patch");
        int x = (int)localX, z = (int)localZ;
        int h00 = patch.Height(x, z), h10 = patch.Height(x + 1, z);
        int h01 = patch.Height(x, z + 1), h11 = patch.Height(x + 1, z + 1);
        if (h00 is < short.MinValue or > short.MaxValue || h10 is < short.MinValue or > short.MaxValue ||
            h01 is < short.MinValue or > short.MaxValue || h11 is < short.MinValue or > short.MaxValue)
            throw new ArgumentException("legacy recipe: height");
        float fx = tx.Subtract(new(x0, 1)).ToSingle(), fz = tz.Subtract(new(z0, 1)).ToSingle();
        return Evaluate(h00, h10, h01, h11, fx, fz);
    }
}
