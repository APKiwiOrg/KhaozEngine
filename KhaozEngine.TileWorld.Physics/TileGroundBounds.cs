using System;

namespace KhaozEngine.TileWorld.Physics;

/// <summary>The represented half-open loaded rectangle, shared by sampling and residency.</summary>
internal readonly struct TileGroundBounds
{
    readonly float _minX, _maxX, _minZ, _maxZ;
    readonly bool _nonempty;

    internal TileGroundBounds(RegionCoord[] regions)
    {
        if (regions.Length == 0) return;
        int minX = int.MaxValue, maxX = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue;
        foreach (RegionCoord region in regions)
        {
            minX = Math.Min(minX, region.Rx);
            maxX = Math.Max(maxX, region.Rx);
            minZ = Math.Min(minZ, region.Rz);
            maxZ = Math.Max(maxZ, region.Rz);
        }
        _minX = AtOrAbove((double)minX * TileRegion.Size);
        _minZ = AtOrAbove((double)minZ * TileRegion.Size);
        _maxX = Before((maxX + 1L) * (double)TileRegion.Size);
        _maxZ = Before((maxZ + 1L) * (double)TileRegion.Size);
        _nonempty = _minX <= _maxX && _minZ <= _maxZ;
    }

    internal bool TryMap(float worldX, float worldZ, float tileSize, out float x, out float z)
    {
        x = z = 0;
        if (!_nonempty) return false;
        x = Math.Clamp(TileWorldSpace.TileX(worldX, tileSize), _minX, _maxX);
        z = Math.Clamp(TileWorldSpace.TileZ(worldZ, tileSize), _minZ, _maxZ);
        return float.IsFinite(x) && float.IsFinite(z);
    }

    static float AtOrAbove(double value)
    {
        float rounded = (float)value;
        return rounded < value ? MathF.BitIncrement(rounded) : rounded;
    }

    static float Before(double exclusive)
    {
        float rounded = (float)exclusive;
        return rounded >= exclusive ? MathF.BitDecrement(rounded) : rounded;
    }
}
