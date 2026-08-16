using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.TileWorld;

namespace KhaozEngine.TileEdit;

/// <summary>Paints the authoring overlays straight into a captured top-down RGBA buffer: the tile grid, the
/// derived collision, the object anchors and the region borders. Pure CPU and pure arithmetic over the buffer,
/// with no GPU and no scene, so the mapping from tile to pixel is testable on its own without a device.
///
/// <para>The buffer is the one <c>TileWorldSnapshot.CaptureTopDown</c> returns: row major from the TOP, and the
/// top-down camera puts NORTH up, so tile z maps to image row <c>(rect.Z1 - 1 - z) * pxPerTile</c> and tile x to
/// column <c>(x - rect.X) * pxPerTile</c>. Every overlay here goes through those two, so there is one place the
/// orientation can be wrong and one place to fix it if it ever is.</para></summary>
public static class TopDownOverlayPainter
{
    /// <summary>The overlay names <see cref="Parse"/> accepts.</summary>
    public const string OverlayNames = "grid, collision, objects, regions";

    /// <summary>How strongly the grid lines darken what is under them.</summary>
    public const float GridAlpha = 0.35f;

    /// <summary>How strongly a blocked tile is tinted.</summary>
    public const float CollisionAlpha = 0.4f;

    /// <summary>Side of the square dot drawn at an object's anchor, in pixels.</summary>
    public const int ObjectDotSize = 3;

    /// <summary>Width of a region border line, in pixels.</summary>
    public const int RegionLineWidth = 2;

    /// <summary>Colour of the grid lines, blended at <see cref="GridAlpha"/>.</summary>
    public static (byte R, byte G, byte B) GridColor => (0, 0, 0);

    /// <summary>Colour a blocked tile is tinted with, blended at <see cref="CollisionAlpha"/>.</summary>
    public static (byte R, byte G, byte B) CollisionTint => (200, 40, 40);

    /// <summary>Colour of a walled tile edge, drawn solid.</summary>
    public static (byte R, byte G, byte B) WallColor => (220, 40, 40);

    /// <summary>Colour of a region border, drawn solid.</summary>
    public static (byte R, byte G, byte B) RegionColor => (255, 255, 255);

    // Saturated and well separated, because these dots are three pixels across on top of a greybox render: the
    // greybox palette itself is deliberately dull and would vanish into the ground it sits on.
    static readonly (byte R, byte G, byte B)[] ObjectPalette =
    {
        (230, 60, 60), (60, 180, 75), (60, 110, 230), (240, 180, 20),
        (170, 80, 220), (30, 200, 200), (250, 120, 30), (200, 60, 150),
    };

    /// <summary>Splits and validates a comma-separated overlay list, lowercased and de-duplicated. An empty or
    /// null list means no overlays.</summary>
    /// <exception cref="ArgumentException">A name is not one of <see cref="OverlayNames"/>.</exception>
    public static IReadOnlyList<string> Parse(string? overlays)
    {
        if (string.IsNullOrWhiteSpace(overlays)) return Array.Empty<string>();
        var names = new List<string>();
        foreach (string raw in overlays.Split(','))
        {
            string name = raw.Trim().ToLowerInvariant();
            if (name.Length == 0) continue;
            if (name is not ("grid" or "collision" or "objects" or "regions"))
                throw new ArgumentException(
                    $"'{name}' is not an overlay. The overlays are {OverlayNames}.", nameof(overlays));
            if (!names.Contains(name, StringComparer.Ordinal)) names.Add(name);
        }
        return names;
    }

    /// <summary>The dot colour for one archetype id: the same id is the same colour on every machine and every
    /// run, so two renders of the same world are comparable.</summary>
    public static (byte R, byte G, byte B) ObjectColor(string archetypeId)
    {
        ArgumentNullException.ThrowIfNull(archetypeId);
        return ObjectPalette[Hash(archetypeId) % (uint)ObjectPalette.Length];
    }

    /// <summary>Paints the requested overlays into <paramref name="rgba"/> in a fixed order (grid, collision,
    /// regions, objects) whatever order they were asked for, so the anchors stay on top and two renders of the
    /// same world with the same overlays are byte identical.</summary>
    /// <exception cref="ArgumentException">The buffer, the rect and the pixels per tile do not describe the same
    /// image.</exception>
    public static void Paint(byte[] rgba, int width, int height, TileRect rect, int plane, int pxPerTile,
        TileWorldDocument doc, TileCollisionMap collision, IReadOnlyList<string> overlays)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(collision);
        ArgumentNullException.ThrowIfNull(overlays);
        ArgumentOutOfRangeException.ThrowIfLessThan(pxPerTile, 1);
        if (rect.IsEmpty)
            throw new ArgumentException("the rect covers no tiles, so there is nothing to paint over.", nameof(rect));
        if (width != rect.Width * pxPerTile || height != rect.Height * pxPerTile)
            throw new ArgumentException(
                $"the image is {width} by {height}, which is not the rect's {rect.Width} by {rect.Height} tiles at {pxPerTile} px each.",
                nameof(width));
        if (rgba.Length != width * height * 4)
            throw new ArgumentException(
                $"the buffer holds {rgba.Length} bytes, a {width} by {height} RGBA image is {width * height * 4}.",
                nameof(rgba));
        if (overlays.Count == 0) return;

        if (overlays.Contains("grid", StringComparer.Ordinal)) PaintGrid(rgba, width, height, pxPerTile, rect);
        if (overlays.Contains("collision", StringComparer.Ordinal))
            PaintCollision(rgba, width, height, rect, plane, pxPerTile, collision);
        if (overlays.Contains("regions", StringComparer.Ordinal)) PaintRegions(rgba, width, height, rect, pxPerTile);
        if (overlays.Contains("objects", StringComparer.Ordinal))
            PaintObjects(rgba, width, height, rect, plane, pxPerTile, doc);
    }

    // One line on the west edge of every tile column and the north edge of every tile row, plus the far east and
    // south edges of the image, so the grid closes rather than leaving the last tile open on two sides.
    static void PaintGrid(byte[] rgba, int width, int height, int pxPerTile, TileRect rect)
    {
        for (int i = 0; i < rect.Width; i++) Fill(rgba, width, height, i * pxPerTile, 0, 1, height, GridColor, GridAlpha);
        Fill(rgba, width, height, width - 1, 0, 1, height, GridColor, GridAlpha);
        for (int i = 0; i < rect.Height; i++) Fill(rgba, width, height, 0, i * pxPerTile, width, 1, GridColor, GridAlpha);
        Fill(rgba, width, height, 0, height - 1, width, 1, GridColor, GridAlpha);
    }

    static void PaintCollision(byte[] rgba, int width, int height, TileRect rect, int plane, int pxPerTile,
        TileCollisionMap collision)
    {
        for (int z = rect.Z; z < rect.Z1; z++)
            for (int x = rect.X; x < rect.X1; x++)
            {
                TileCollisionFlags f = collision.Get(x, z, plane);
                if (f == TileCollisionFlags.None) continue;
                int left = Column(rect, pxPerTile, x), top = Row(rect, pxPerTile, z);
                if ((f & TileCollisionFlags.Blocked) != 0)
                    Fill(rgba, width, height, left, top, pxPerTile, pxPerTile, CollisionTint, CollisionAlpha);
                // A wall sits ON the edge it blocks. North is the TOP row of the tile's band, because the image
                // runs north up, which is the one place this pair could be swapped without anything else noticing.
                if ((f & TileCollisionFlags.WallN) != 0) Fill(rgba, width, height, left, top, pxPerTile, 1, WallColor, 1f);
                if ((f & TileCollisionFlags.WallS) != 0) Fill(rgba, width, height, left, top + pxPerTile - 1, pxPerTile, 1, WallColor, 1f);
                if ((f & TileCollisionFlags.WallW) != 0) Fill(rgba, width, height, left, top, 1, pxPerTile, WallColor, 1f);
                if ((f & TileCollisionFlags.WallE) != 0) Fill(rgba, width, height, left + pxPerTile - 1, top, 1, pxPerTile, WallColor, 1f);
            }
    }

    // Objects on the QUERIED plane only. The render itself draws every plane (a map view looks at the roofs), but
    // an anchor dot is an authoring aid for the plane being edited, and one dot per plane stacked on one tile
    // would say nothing about either.
    static void PaintObjects(byte[] rgba, int width, int height, TileRect rect, int plane, int pxPerTile,
        TileWorldDocument doc)
    {
        int half = ObjectDotSize / 2;
        foreach (TileObject o in doc.ObjectsIn(rect, plane))
        {
            int cx = Column(rect, pxPerTile, o.X) + pxPerTile / 2;
            int cy = Row(rect, pxPerTile, o.Z) + pxPerTile / 2;
            Fill(rgba, width, height, cx - half, cy - half, ObjectDotSize, ObjectDotSize, ObjectColor(o.ArchetypeId), 1f);
        }
    }

    // The borders BETWEEN regions, which are tile coordinates divisible by the region size. The east and north
    // edges of the rect are borders too when they fall on one, so the sweep runs to X1 and Z1 inclusive and the
    // line is nudged back inside the image when it lands on the far edge.
    static void PaintRegions(byte[] rgba, int width, int height, TileRect rect, int pxPerTile)
    {
        for (int x = rect.X; x <= rect.X1; x++)
        {
            if (!OnRegionBorder(x)) continue;
            int left = Math.Clamp(Column(rect, pxPerTile, x), 0, width - RegionLineWidth);
            Fill(rgba, width, height, left, 0, RegionLineWidth, height, RegionColor, 1f);
        }
        for (int z = rect.Z; z <= rect.Z1; z++)
        {
            if (!OnRegionBorder(z)) continue;
            // The border at tile z is the NORTH edge of that tile's band, which is one whole band above the row
            // the tile itself starts on.
            int top = Math.Clamp((rect.Z1 - z) * pxPerTile, 0, height - RegionLineWidth);
            Fill(rgba, width, height, 0, top, width, RegionLineWidth, RegionColor, 1f);
        }
    }

    static bool OnRegionBorder(int tile) => ((tile % TileRegion.Size) + TileRegion.Size) % TileRegion.Size == 0;

    static int Column(TileRect rect, int pxPerTile, int x) => (x - rect.X) * pxPerTile;

    static int Row(TileRect rect, int pxPerTile, int z) => (rect.Z1 - 1 - z) * pxPerTile;

    // A clipped rectangle blend. Alpha 1 writes the colour outright, which is what the solid lines want, and
    // every painted pixel comes out opaque: a capture is opaque and an overlay must not punch holes in it.
    static void Fill(byte[] rgba, int width, int height, int x, int y, int w, int h,
        (byte R, byte G, byte B) color, float alpha)
    {
        int x0 = Math.Max(0, x), y0 = Math.Max(0, y);
        int x1 = Math.Min(width, x + w), y1 = Math.Min(height, y + h);
        for (int py = y0; py < y1; py++)
            for (int px = x0; px < x1; px++)
            {
                int i = (py * width + px) * 4;
                rgba[i] = Mix(rgba[i], color.R, alpha);
                rgba[i + 1] = Mix(rgba[i + 1], color.G, alpha);
                rgba[i + 2] = Mix(rgba[i + 2], color.B, alpha);
                rgba[i + 3] = 255;
            }
    }

    static byte Mix(byte dst, byte src, float alpha) =>
        alpha >= 1f ? src : (byte)MathF.Round(dst + (src - dst) * alpha);

    static uint Hash(string s)
    {
        uint h = 2166136261u;
        unchecked
        {
            for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619u; }
            h ^= h >> 13;
            h *= 0x5bd1e995u;
            h ^= h >> 15;
        }
        return h;
    }
}
