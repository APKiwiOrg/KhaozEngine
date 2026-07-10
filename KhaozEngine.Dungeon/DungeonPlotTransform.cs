using System;

namespace KhaozEngine.Dungeon;

/// <summary>
/// Maps dungeon-local tile coordinates (see <see cref="DungeonTile"/>) onto world-space positions for a
/// single placed dungeon plot. The plot occupies a square raster of <c>cellSize</c>-wide cells. This
/// transform rotates that raster by <see cref="YawRadians"/> around its own origin, then offsets it to
/// (<see cref="OriginX"/>, <see cref="OriginZ"/>) in world space, with vertical floors stacked from
/// <see cref="BaseY"/> at <c>floorHeight</c> per level.
/// </summary>
/// <param name="OriginX">World-space X of the plot origin (the tile-space (0, 0) corner, pre-rotation pivot).</param>
/// <param name="OriginZ">World-space Z of the plot origin.</param>
/// <param name="BaseY">World-space Y of floor 0.</param>
/// <param name="YawRadians">Rotation, in radians, applied to the tile raster around the origin before translation.</param>
public readonly record struct DungeonPlotTransform(float OriginX, float OriginZ, float BaseY, float YawRadians)
{
    /// <summary>
    /// Computes the world-space center of <paramref name="tile"/>: the tile-local center
    /// ((X + 0.5) * <paramref name="cellSize"/>, (Z + 0.5) * <paramref name="cellSize"/>) is rotated by
    /// <see cref="YawRadians"/> around the origin, then offset by (<see cref="OriginX"/>, <see cref="OriginZ"/>).
    /// The vertical position is <see cref="BaseY"/> plus <see cref="DungeonTile.Floor"/> multiplied by
    /// <paramref name="floorHeight"/>.
    /// </summary>
    public (float X, float Y, float Z) TileCenter(DungeonTile tile, float cellSize, float floorHeight)
    {
        var localX = (tile.X + 0.5f) * cellSize;
        var localZ = (tile.Z + 0.5f) * cellSize;

        var cos = MathF.Cos(YawRadians);
        var sin = MathF.Sin(YawRadians);

        var rotatedX = localX * cos - localZ * sin;
        var rotatedZ = localX * sin + localZ * cos;

        var x = rotatedX + OriginX;
        var z = rotatedZ + OriginZ;
        var y = BaseY + tile.Floor * floorHeight;

        return (x, y, z);
    }
}
