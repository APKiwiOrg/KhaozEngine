using System;
using KhaozEngine.Dungeon;

namespace KeDungeon;

/// <summary>
/// Renders one floor of a <see cref="DungeonLayout"/> to an 8-px-per-cell top-down RGBA bitmap for quick
/// visual inspection (the ke-dungeon <c>preview</c> verb). This is dev tooling, not a game-facing renderer:
/// colors are a fixed debug palette, not theme content.
/// </summary>
public static class PreviewRenderer
{
    const int CellPixels = 8;
    const int DotPixels = 3;

    static readonly (byte R, byte G, byte B) EmptyColor = (0x1E, 0x1E, 0x28);
    static readonly (byte R, byte G, byte B) RoomFloorColor = (0xC8, 0xC8, 0xD0);
    static readonly (byte R, byte G, byte B) CorridorColor = (0xB4, 0x9B, 0x78);
    static readonly (byte R, byte G, byte B) WallColor = (0x46, 0x46, 0x5A);
    static readonly (byte R, byte G, byte B) DoorFrameColor = (0x8C, 0x5A, 0x2D);
    static readonly (byte R, byte G, byte B) StairLowerColor = (0x50, 0x78, 0xC8);
    static readonly (byte R, byte G, byte B) StairUpperColor = (0x6E, 0x96, 0xE6);
    static readonly (byte R, byte G, byte B) StairTopColor = (0x96, 0xBE, 0xFF);
    static readonly (byte R, byte G, byte B) StairVoidColor = (0x28, 0x28, 0x3C);

    static readonly (byte R, byte G, byte B) SpawnMarkerColor = (0xFF, 0x64, 0x64);
    static readonly (byte R, byte G, byte B) LootMarkerColor = (0xFF, 0xD2, 0x50);
    static readonly (byte R, byte G, byte B) ObjectiveMarkerColor = (0xFF, 0x00, 0xFF);
    static readonly (byte R, byte G, byte B) EntranceMarkerColor = (0x00, 0xFF, 0x00);

    /// <summary>
    /// Renders <paramref name="floor"/> of <paramref name="layout"/> to a top-to-bottom 8-bit RGBA buffer
    /// (<paramref name="width"/> = <c>layout.Width * 8</c>, <paramref name="height"/> = <c>layout.Depth * 8</c>).
    /// Each cell paints as an 8x8 pixel block in a fixed debug palette. Cells inside an Entrance room are
    /// tinted +(0,40,0), cells inside a Boss room +(40,0,0) (both clamped to 255). Every
    /// <see cref="DungeonCellKind.StairLower"/> cell also gets a 2px lighter chevron. Markers on this floor
    /// draw last, as a 3px dot in their type's color, on top of the cell coloring.
    /// </summary>
    public static byte[] RenderFloorRgba(DungeonLayout layout, int floor, out int width, out int height)
    {
        ArgumentNullException.ThrowIfNull(layout);

        width = layout.Width * CellPixels;
        height = layout.Depth * CellPixels;

        var rgba = new byte[width * height * 4];

        for (int z = 0; z < layout.Depth; z++)
        {
            for (int x = 0; x < layout.Width; x++)
            {
                DungeonCellKind kind = layout.GetCell(x, z, floor);
                (byte r, byte g, byte b) = ApplyRoomTint(layout, floor, x, z, BaseColor(kind));

                PaintCell(rgba, width, x, z, r, g, b);

                if (kind == DungeonCellKind.StairLower)
                {
                    PaintChevron(rgba, width, x, z, r, g, b);
                }
            }
        }

        foreach (DungeonMarker marker in layout.Markers)
        {
            if (marker.Tile.Floor != floor)
            {
                continue;
            }

            (byte r, byte g, byte b) = MarkerColor(marker.Type);
            PaintDot(rgba, width, marker.Tile.X, marker.Tile.Z, r, g, b);
        }

        return rgba;
    }

    static (byte R, byte G, byte B) BaseColor(DungeonCellKind kind) => kind switch
    {
        DungeonCellKind.Empty => EmptyColor,
        DungeonCellKind.RoomFloor => RoomFloorColor,
        DungeonCellKind.Corridor => CorridorColor,
        DungeonCellKind.Wall => WallColor,
        DungeonCellKind.DoorFrame => DoorFrameColor,
        DungeonCellKind.StairLower => StairLowerColor,
        DungeonCellKind.StairUpper => StairUpperColor,
        DungeonCellKind.StairTop => StairTopColor,
        DungeonCellKind.StairVoid => StairVoidColor,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown DungeonCellKind."),
    };

    static (byte R, byte G, byte B) MarkerColor(DungeonMarkerType type) => type switch
    {
        DungeonMarkerType.Spawn => SpawnMarkerColor,
        DungeonMarkerType.Loot => LootMarkerColor,
        DungeonMarkerType.Objective => ObjectiveMarkerColor,
        DungeonMarkerType.Entrance => EntranceMarkerColor,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown DungeonMarkerType."),
    };

    // Entrance/Boss room tint is a per-cell lookup against every room's rect on this floor: rooms never
    // overlap, so the first (only) match decides the tint, and cells outside every room (corridors, walls
    // between rooms) fall through unchanged.
    static (byte R, byte G, byte B) ApplyRoomTint(DungeonLayout layout, int floor, int x, int z, (byte R, byte G, byte B) color)
    {
        foreach (DungeonRoom room in layout.Rooms)
        {
            if (room.Floor != floor)
            {
                continue;
            }

            if (x < room.X || x >= room.X + room.Width || z < room.Z || z >= room.Z + room.Depth)
            {
                continue;
            }

            return room.RoomType switch
            {
                DungeonRoomType.Entrance => (color.R, Clamp255(color.G + 40), color.B),
                DungeonRoomType.Boss => (Clamp255(color.R + 40), color.G, color.B),
                _ => color,
            };
        }

        return color;
    }

    static void PaintCell(byte[] rgba, int stride, int cellX, int cellZ, byte r, byte g, byte b)
    {
        int startX = cellX * CellPixels;
        int startY = cellZ * CellPixels;

        for (int dy = 0; dy < CellPixels; dy++)
        {
            for (int dx = 0; dx < CellPixels; dx++)
            {
                SetPixel(rgba, stride, startX + dx, startY + dy, r, g, b);
            }
        }
    }

    // A 2px-thick "^" chevron (apex at the top of the cell, arms widening toward the bottom corners),
    // painted in a lighter shade of the cell's own color, over StairLower cells only.
    static void PaintChevron(byte[] rgba, int stride, int cellX, int cellZ, byte baseR, byte baseG, byte baseB)
    {
        (byte r, byte g, byte b) = Lighten(baseR, baseG, baseB);
        int startX = cellX * CellPixels;
        int startY = cellZ * CellPixels;

        for (int dy = 0; dy < CellPixels; dy++)
        {
            int half = dy / 2;
            int left = 3 - half;
            int right = 4 + half;

            PaintArmPixel(rgba, stride, startX, startY, left, dy, r, g, b);
            PaintArmPixel(rgba, stride, startX, startY, left - 1, dy, r, g, b);
            PaintArmPixel(rgba, stride, startX, startY, right, dy, r, g, b);
            PaintArmPixel(rgba, stride, startX, startY, right + 1, dy, r, g, b);
        }
    }

    static void PaintArmPixel(byte[] rgba, int stride, int startX, int startY, int dx, int dy, byte r, byte g, byte b)
    {
        if (dx < 0 || dx >= CellPixels)
        {
            return;
        }

        SetPixel(rgba, stride, startX + dx, startY + dy, r, g, b);
    }

    static void PaintDot(byte[] rgba, int stride, int cellX, int cellZ, byte r, byte g, byte b)
    {
        int margin = (CellPixels - DotPixels) / 2;
        int startX = cellX * CellPixels + margin;
        int startY = cellZ * CellPixels + margin;

        for (int dy = 0; dy < DotPixels; dy++)
        {
            for (int dx = 0; dx < DotPixels; dx++)
            {
                SetPixel(rgba, stride, startX + dx, startY + dy, r, g, b);
            }
        }
    }

    static void SetPixel(byte[] rgba, int stride, int x, int y, byte r, byte g, byte b)
    {
        int i = (y * stride + x) * 4;
        rgba[i] = r;
        rgba[i + 1] = g;
        rgba[i + 2] = b;
        rgba[i + 3] = 255;
    }

    static (byte R, byte G, byte B) Lighten(byte r, byte g, byte b)
    {
        return (Clamp255(r + (255 - r) / 2), Clamp255(g + (255 - g) / 2), Clamp255(b + (255 - b) / 2));
    }

    static byte Clamp255(int value)
    {
        return (byte)Math.Clamp(value, 0, 255);
    }
}
