using System;
using Microsoft.Xna.Framework;

namespace KhaozEngine.Sprites;

/// <summary>
/// The eight cardinal/intercardinal facings, ordered to match PixelLab's directional sprite-sheet
/// row layout: S, SE, E, NE, N, NW, W, SW. The integer value of each member is therefore the row
/// index of that direction in a PixelLab grid sheet (see <see cref="PixelLabSpriteLoader"/>).
/// </summary>
public enum Direction8
{
    /// <summary>South (facing down, +Y in screen space). Row 0.</summary>
    S = 0,
    /// <summary>South-east. Row 1.</summary>
    SE = 1,
    /// <summary>East (facing right, +X). Row 2.</summary>
    E = 2,
    /// <summary>North-east. Row 3.</summary>
    NE = 3,
    /// <summary>North (facing up, -Y). Row 4.</summary>
    N = 4,
    /// <summary>North-west. Row 5.</summary>
    NW = 5,
    /// <summary>West (facing left, -X). Row 6.</summary>
    W = 6,
    /// <summary>South-west. Row 7.</summary>
    SW = 7,
}

/// <summary>Helpers for mapping facing vectors to and from <see cref="Direction8"/>.</summary>
public static class Direction8Extensions
{
    // Sector index 0..7 (0 = East, increasing clockwise in y-down screen space) -> Direction8.
    private static readonly Direction8[] SectorToDirection =
    {
        Direction8.E, Direction8.SE, Direction8.S, Direction8.SW,
        Direction8.W, Direction8.NW, Direction8.N, Direction8.NE,
    };

    // Unit vectors per direction in y-down screen space (S = +Y, E = +X, N = -Y).
    private static readonly Vector2[] DirectionToUnit =
    {
        new(0f, 1f),                          // S
        new(0.70710677f, 0.70710677f),        // SE
        new(1f, 0f),                          // E
        new(0.70710677f, -0.70710677f),       // NE
        new(0f, -1f),                         // N
        new(-0.70710677f, -0.70710677f),      // NW
        new(-1f, 0f),                         // W
        new(-0.70710677f, 0.70710677f),       // SW
    };

    /// <summary>
    /// Maps a facing/movement vector to the nearest of the eight directions. Screen space is y-down
    /// (MonoGame convention): +X is east, +Y is south. Magnitude is irrelevant. A vector exactly on a
    /// 22.5-degree seam rounds to the higher (clockwise) direction. A zero vector returns
    /// <paramref name="fallback"/>.
    /// </summary>
    public static Direction8 FromVector(Vector2 facing, Direction8 fallback = Direction8.S)
    {
        if (facing.X == 0f && facing.Y == 0f)
            return fallback;

        // atan2(y, x): 0 = east; positive turns clockwise toward south because +Y is down.
        double degrees = Math.Atan2(facing.Y, facing.X) * (180.0 / Math.PI);
        if (degrees < 0.0)
            degrees += 360.0;

        // Shift by half a sector so each 45-degree sector is centred on its cardinal, then floor.
        int sector = (int)Math.Floor((degrees + 22.5) / 45.0) % 8;
        return SectorToDirection[sector];
    }

    /// <summary>The unit facing vector for a direction, in y-down screen space.</summary>
    public static Vector2 ToVector(this Direction8 direction) => DirectionToUnit[(int)direction];
}
