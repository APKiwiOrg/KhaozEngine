using System;
using KhaozEngine.MapDoc.Scene;

namespace KhaozEngine.Tests.MapDoc.Interiors;

/// <summary>Fixture-derived simultaneous frozen columns. No geometry compiler, receipt or observed peak is read.</summary>
internal static class MapMembershipArenaInventoryModel
{
    readonly record struct Shape(int Cells, int Faces, int Triangles, int Corners = 0, int ExtraAffines = 0,
        int Patches = 1, int Surfaces = 1, int Bounds = 1, int Openings = 0, int Compatibility = 0, int Pieces = 0);
    internal static long PageBytes(int rows, int stride, int page)
    {
        if (rows == 0) return 0;
        int pages = checked((rows + page - 1) / page), directory = 1;
        while (directory < pages) directory = checked(directory * 2);
        return checked(24L + 8L * directory + pages * ((24L + (long)page * stride + 7) & ~7L));
    }
    internal static long FootprintArena(int cells) => checked(768 + PageBytes(cells, 80, 64) +
        PageBytes(4 * cells, 32, 128) + PageBytes(4 * cells, 1, 128) + PageBytes(16 * cells, 16, 256));
    internal static long FootprintIdentities(int cells)
    {
        int polygon = (cells + 63) / 64, vertex = (4 * cells + 127) / 128, token = (16 * cells + 255) / 256;
        return 15L + polygon + 2 * vertex + token + (cells == 0 ? 0 : 4);
    }
    internal static long FootprintRoot(int cells) => checked(FootprintArena(cells) + 4096 +
        ((24L + 4L * cells + 7) & ~7L) + 512 * (FootprintIdentities(cells) + 1));
    internal static long Subtotal(MapScopedScene scene, string name)
    {
        Shape s = name switch
        {
            "flat" or "short" or "long" => new(1, 2, 2),
            "subdivided" or "transformed" => new(1, 18, 18),
            "mixed" => new(5, 10, 10, Patches: 2, Surfaces: 2, Bounds: 5),
            "face-free" => new(1, 0, 0, Compatibility: 1),
            "opening" => new(2, 4, 4, Patches: 2, Surfaces: 2, Bounds: 2, Openings: 1),
            "portal" => new(1, 2, 3, ExtraAffines: 2),
            "prism" or "clipped" => new(2, 4, 4, Corners: 4, Patches: 2, Surfaces: 2, Bounds: 2, Pieces: 1),
            "fragmented" => new(2, 4, 4, Corners: 128, Patches: 2, Surfaces: 2, Bounds: 2, Pieces: 32),
            _ => throw new ArgumentOutOfRangeException(nameof(name))
        };
        int corners = 4 * s.Cells + s.Corners;
        int coefficients = 4 * (corners + 4 * s.Triangles + s.ExtraAffines);
        long arena = 768 + PageBytes(s.Cells + s.Pieces, 80, 64) + PageBytes(corners, 32, 128) +
            PageBytes(corners, 1, 128) + PageBytes(s.Triangles, 160, 32) + PageBytes(s.Faces, 32, 128) +
            PageBytes(s.Cells, 64, 64) + PageBytes(coefficients, 16, 256) +
            PageBytes(s.Surfaces, 8, 32) + PageBytes(s.Patches, 8, 32) + PageBytes(s.Surfaces, 8, 32) +
            PageBytes(s.Faces == 0 ? 0 : 1, 8, 32) + PageBytes(s.Faces == 0 ? 0 : 1, 32, 32);
        long bounds = 320 + PageBytes(s.Bounds + 2, 32, 128) + PageBytes(1, 8, 64) +
            PageBytes(s.Openings, 8, 64) + PageBytes(s.Compatibility, 40, 64);
        // Per physical identity, count only the retained receipt 64, entry 48, empty view dictionary 80,
        // release lease 32, delegate 64, closure 56 and covered-list 32. Omitted maps and controls strengthen the proof.
        long metadataSubtotal = 376 * (FootprintIdentities(1) + 1) + 32;
        return checked(MapMembershipInventoryModel.Header(scene) + arena + FootprintArena(1) + bounds + metadataSubtotal);
    }
}
