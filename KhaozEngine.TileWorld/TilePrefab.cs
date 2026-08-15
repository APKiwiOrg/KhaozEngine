using System;
using System.Collections.Generic;
using System.Linq;

namespace KhaozEngine.TileWorld;

/// <summary>A rect of tiles lifted out of a world (all layers, relative corner heights, objects, markers) that
/// can be stamped elsewhere with a rotation. Build one house, stamp a village.</summary>
public sealed class TilePrefab
{
    /// <summary>A human label for the prefab, empty when it was extracted unnamed.</summary>
    public string Name { get; set; } = "";

    /// <summary>Width of the prefab in tiles, west to east.</summary>
    public int Width { get; set; }

    /// <summary>Height of the prefab in tiles, south to north.</summary>
    public int Height { get; set; }

    /// <summary>How many planes the prefab carries, starting at its own plane 0.</summary>
    public int PlaneCount { get; set; }

    /// <summary>One entry per prefab plane, null when that plane carried nothing at all.</summary>
    public List<TilePrefabPlane?> Planes { get; set; } = new();

    /// <summary>Objects whose anchor tile fell inside the extracted rect, in prefab-relative coordinates.</summary>
    public List<TilePrefabObject> Objects { get; set; } = new();

    /// <summary>Markers that fell inside the extracted rect, in prefab-relative coordinates.</summary>
    public List<TilePrefabMarker> Markers { get; set; } = new();
}

/// <summary>One plane of a prefab. Every layer is null when it is entirely default, the same trim rule region
/// files use, so a prefab on disk carries only what was authored.</summary>
public sealed class TilePrefabPlane
{
    /// <summary>(Width+1) x (Height+1) corner heights, cm, relative to the extracted rect's SW corner on plane 0.</summary>
    public short[]? HeightsRelative { get; set; }

    /// <summary>Width x Height ground material ids, 0 = void.</summary>
    public ushort[]? Underlay { get; set; }

    /// <summary>Width x Height overlay material ids, 0 = none.</summary>
    public ushort[]? Overlay { get; set; }

    /// <summary>Width x Height <see cref="TileOverlayShape"/> values.</summary>
    public byte[]? OverlayShape { get; set; }

    /// <summary>Width x Height overlay rotations, quarter turns clockwise.</summary>
    public byte[]? OverlayRotation { get; set; }

    /// <summary>Width x Height <see cref="TileSettings"/> flag bytes.</summary>
    public byte[]? Settings { get; set; }
}

/// <summary>An object carried by a prefab, anchored in prefab-relative coordinates.</summary>
public sealed class TilePrefabObject
{
    /// <summary>The catalog archetype this object instantiates.</summary>
    public string ArchetypeId { get; set; } = "";

    /// <summary>Anchor column, 0 at the prefab's west edge.</summary>
    public int X { get; set; }

    /// <summary>Anchor row, 0 at the prefab's south edge.</summary>
    public int Z { get; set; }

    /// <summary>Plane relative to the prefab's own plane 0.</summary>
    public int Plane { get; set; }

    /// <summary>Quarter turns clockwise, 0 to 3.</summary>
    public int Rotation { get; set; }

    /// <summary>The UNROTATED archetype footprint, stamped at extract time so a prefab can rotate a multi-tile
    /// object without a catalog.</summary>
    public int SizeX { get; set; } = 1;

    /// <summary>The unrotated footprint depth, the companion to <see cref="SizeX"/>.</summary>
    public int SizeZ { get; set; } = 1;

    /// <summary>Free-form tags copied from the source object, null when it had none.</summary>
    public List<string>? Tags { get; set; }
}

/// <summary>A named marker carried by a prefab, in prefab-relative coordinates.</summary>
public sealed class TilePrefabMarker
{
    /// <summary>The marker's name, unique per world once placed.</summary>
    public string Name { get; set; } = "";

    /// <summary>Column, 0 at the prefab's west edge.</summary>
    public int X { get; set; }

    /// <summary>Row, 0 at the prefab's south edge.</summary>
    public int Z { get; set; }

    /// <summary>Plane relative to the prefab's own plane 0.</summary>
    public int Plane { get; set; }

    /// <summary>Free-form tags copied from the source marker, null when it had none.</summary>
    public List<string>? Tags { get; set; }
}

/// <summary>Extract, rotate and stamp <see cref="TilePrefab"/>s.</summary>
public static class TilePrefabs
{
    /// <summary>Lifts the tiles of <paramref name="rect"/> on planes
    /// <paramref name="planeFrom"/>..<paramref name="planeFrom"/> + <paramref name="planeCount"/> - 1 out of the
    /// document. Heights come out relative to the rect's SW corner on <paramref name="planeFrom"/>, all-default
    /// layers come out null.</summary>
    public static TilePrefab Extract(TileWorldDocument doc, TileWorldCatalogs catalogs, TileRect rect, int planeFrom, int planeCount, bool includeObjects = true, bool includeMarkers = true, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(catalogs);
        if (rect.IsEmpty) throw new ArgumentException("rect is empty", nameof(rect));
        if (planeFrom < 0 || planeCount < 1 || planeFrom + planeCount > doc.PlaneCount)
            throw new ArgumentOutOfRangeException(nameof(planeCount), $"planes {planeFrom}..{planeFrom + planeCount - 1} exceed the world's {doc.PlaneCount}");
        int w = rect.Width, h = rect.Height;
        var prefab = new TilePrefab { Name = name ?? "", Width = w, Height = h, PlaneCount = planeCount };
        int baseCm = doc.CornerHeightCm(rect.X, rect.Z, planeFrom);
        for (int pi = 0; pi < planeCount; pi++)
        {
            int p = planeFrom + pi;
            var plane = new TilePrefabPlane
            {
                HeightsRelative = new short[(w + 1) * (h + 1)], Underlay = new ushort[w * h], Overlay = new ushort[w * h],
                OverlayShape = new byte[w * h], OverlayRotation = new byte[w * h], Settings = new byte[w * h],
            };
            for (int cz = 0; cz <= h; cz++)
                for (int cx = 0; cx <= w; cx++)
                    plane.HeightsRelative[cz * (w + 1) + cx] = (short)Math.Clamp(doc.CornerHeightCm(rect.X + cx, rect.Z + cz, p) - baseCm, short.MinValue, short.MaxValue);
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                {
                    int i = z * w + x, wx = rect.X + x, wz = rect.Z + z;
                    plane.Underlay[i] = doc.GetUnderlay(wx, wz, p);
                    plane.Overlay[i] = doc.GetOverlay(wx, wz, p);
                    plane.OverlayShape[i] = (byte)doc.GetOverlayShape(wx, wz, p);
                    plane.OverlayRotation[i] = (byte)doc.GetOverlayRotation(wx, wz, p);
                    plane.Settings[i] = (byte)doc.GetSettings(wx, wz, p);
                }
            TrimPlane(plane);
            prefab.Planes.Add(plane);
        }
        if (includeObjects)
            foreach (TileObject o in doc.ObjectsIn(rect))
                if (o.Plane >= planeFrom && o.Plane < planeFrom + planeCount)
                {
                    TileObjectArchetype? a = catalogs.Archetype(o.ArchetypeId);
                    prefab.Objects.Add(new TilePrefabObject
                    {
                        ArchetypeId = o.ArchetypeId, X = o.X - rect.X, Z = o.Z - rect.Z, Plane = o.Plane - planeFrom, Rotation = o.Rotation,
                        SizeX = a?.SizeX ?? 1, SizeZ = a?.SizeZ ?? 1, Tags = o.Tags?.ToList(),
                    });
                }
        if (includeMarkers)
            foreach (TileMarker m in doc.AllMarkers())
                if (rect.Contains(m.X, m.Z) && m.Plane >= planeFrom && m.Plane < planeFrom + planeCount)
                    prefab.Markers.Add(new TilePrefabMarker { Name = m.Name, X = m.X - rect.X, Z = m.Z - rect.Z, Plane = m.Plane - planeFrom, Tags = m.Tags?.ToList() });
        return prefab;
    }

    /// <summary>The prefab turned <paramref name="rotation"/> quarter turns clockwise (north up). The rotated
    /// prefab is re-based so its new SW corner on plane 0 is height 0, the same datum Extract uses, and then
    /// re-trimmed, so a layer the re-base or the overlay-rotation bump drove to all-default comes out null and
    /// the result is shaped exactly like a fresh Extract of the same content. The input is not
    /// modified.</summary>
    public static TilePrefab Rotate(TilePrefab prefab, int rotation)
    {
        ArgumentNullException.ThrowIfNull(prefab);
        TilePrefab result = Clone(prefab);
        for (int i = 0; i < (rotation & 3); i++) result = RotateOnce(result);
        Rebase(result);
        // Re-canonicalise, so a rotated prefab is shaped exactly like an Extract of the same content: the
        // re-base and the overlay-rotation bump can both drive a layer to all-default, and a stale array there
        // would carry a "written" layer that Extract would have dropped.
        foreach (TilePrefabPlane? plane in result.Planes)
            if (plane is not null) TrimPlane(plane);
        return result;
    }

    /// <summary>Stamps the prefab with its SW tile at (x, z) on <paramref name="plane"/> (prefab plane i lands
    /// on plane + i, clipped to the world's plane count). The prefab's SW corner is its height datum, so it
    /// lands on the existing ground at (x, z) whatever the rotation. Every region of the TILE rect is required
    /// before the first write, so a bad stamp cannot tear half way through. The far-edge CORNER writes at x + w
    /// and z + h are the exception: their region may not exist at the edge of the authored world, and those
    /// writes are SKIPPED rather than refusing the stamp, because the corner is edge-extended from the tile rect
    /// there and is not readable as its own value anyway. Objects get fresh ids, markers replace same-name
    /// markers. Returns the touched rect (one tile wider to the west and south and one row/column further north
    /// and east, for the corner writes).</summary>
    public static TileRect Place(TileWorldDocument doc, TilePrefab prefab, int x, int z, int plane, int rotation)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(prefab);
        // Shape first, before the turn: rotating a mis-sized layer would fail with a raw index error instead of
        // a message naming the prefab. Then the regions, so the whole pre-flight is done before the first write.
        RequireShape(prefab);
        TilePrefab p = Rotate(prefab, rotation);
        int w = p.Width, h = p.Height;
        for (int tz = z; tz < z + h; tz++)
            for (int tx = x; tx < x + w; tx++) doc.RequireRegion(tx, tz);

        int baseCm = doc.CornerHeightCm(x, z, plane);
        for (int pi = 0; pi < p.PlaneCount && plane + pi < doc.PlaneCount; pi++)
        {
            int tp = plane + pi;
            TilePrefabPlane? src = p.Planes[pi];
            if (src is null) continue;
            // Try, not Require: the corner row at x + w and the column at z + h can fall in a region the world
            // does not have, and dropping those writes is deliberate (see the summary). The tile rect's own
            // regions were required above, so nothing INSIDE the stamp can be dropped here.
            if (src.HeightsRelative is not null)
                for (int cz = 0; cz <= h; cz++)
                    for (int cx = 0; cx <= w; cx++)
                        doc.TrySetCornerHeightCm(x + cx, z + cz, tp, (short)Math.Clamp(baseCm + src.HeightsRelative[cz * (w + 1) + cx], short.MinValue, short.MaxValue));
            for (int tz = 0; tz < h; tz++)
                for (int tx = 0; tx < w; tx++)
                {
                    int i = tz * w + tx, wx = x + tx, wz = z + tz;
                    if (src.Underlay is not null) doc.SetUnderlay(wx, wz, tp, src.Underlay[i]);
                    if (src.Overlay is not null) doc.SetOverlay(wx, wz, tp, src.Overlay[i]);
                    if (src.OverlayShape is not null) doc.SetOverlayShape(wx, wz, tp, (TileOverlayShape)src.OverlayShape[i]);
                    if (src.OverlayRotation is not null) doc.SetOverlayRotation(wx, wz, tp, src.OverlayRotation[i]);
                    if (src.Settings is not null) doc.SetSettings(wx, wz, tp, (TileSettings)src.Settings[i]);
                }
        }
        foreach (TilePrefabObject o in p.Objects)
            if (plane + o.Plane < doc.PlaneCount) doc.AddObject(o.ArchetypeId, x + o.X, z + o.Z, plane + o.Plane, o.Rotation, o.Tags);
        foreach (TilePrefabMarker m in p.Markers)
            if (plane + m.Plane < doc.PlaneCount) doc.SetMarker(m.Name, x + m.X, z + m.Z, plane + m.Plane, m.Tags);
        return TileRect.FromCorners(x - 1, z - 1, x + w, z + h);
    }

    // A prefab can arrive hand-built or straight off disk, so its arrays are checked against its declared size
    // before anything is written rather than tearing half way through a stamp.
    static void RequireShape(TilePrefab p)
    {
        if (p.Width < 1 || p.Height < 1)
            throw new TileWorldException($"prefab '{p.Name}': size {p.Width}x{p.Height} is not positive");
        if (p.Planes.Count != p.PlaneCount)
            throw new TileWorldException($"prefab '{p.Name}': declares {p.PlaneCount} planes but carries {p.Planes.Count}");
        int tiles = p.Width * p.Height, corners = (p.Width + 1) * (p.Height + 1);
        for (int i = 0; i < p.Planes.Count; i++)
        {
            TilePrefabPlane? plane = p.Planes[i];
            if (plane is null) continue;
            RequireLength(p, i, "heights", plane.HeightsRelative?.Length, corners);
            RequireLength(p, i, "underlay", plane.Underlay?.Length, tiles);
            RequireLength(p, i, "overlay", plane.Overlay?.Length, tiles);
            RequireLength(p, i, "overlayShape", plane.OverlayShape?.Length, tiles);
            RequireLength(p, i, "overlayRotation", plane.OverlayRotation?.Length, tiles);
            RequireLength(p, i, "settings", plane.Settings?.Length, tiles);
        }
        // Planes are checked here too, not left to AddObject. A stamp writes every layer before it places a
        // single object, so an out-of-range plane caught there would fault a prefab that had already half
        // landed, which is the tear the whole pre-flight exists to prevent.
        foreach (TilePrefabObject o in p.Objects)
            if ((uint)o.Plane >= (uint)p.PlaneCount)
                throw new TileWorldException($"prefab '{p.Name}': object '{o.ArchetypeId}' at ({o.X}, {o.Z}) is on plane {o.Plane}, the prefab has {p.PlaneCount}");
        foreach (TilePrefabMarker m in p.Markers)
            if ((uint)m.Plane >= (uint)p.PlaneCount)
                throw new TileWorldException($"prefab '{p.Name}': marker '{m.Name}' at ({m.X}, {m.Z}) is on plane {m.Plane}, the prefab has {p.PlaneCount}");
    }

    static void RequireLength(TilePrefab p, int plane, string layer, int? actual, int expected)
    {
        if (actual is not null && actual.Value != expected)
            throw new TileWorldException($"prefab '{p.Name}': plane {plane} {layer} has {actual.Value} entries, expected {expected} for {p.Width}x{p.Height}");
    }

    // A turn moves the SW corner to a different physical corner, so the heights come out relative to a corner
    // that is no longer the prefab's own (0, 0). Put the datum back on that corner. The shift is read from plane
    // 0 and applied to EVERY plane, so the offsets between planes survive it. Cheap and idempotent: an
    // Extract-fresh prefab is already at datum 0, so this is a no-op on the unrotated path.
    static void Rebase(TilePrefab p)
    {
        if (p.Planes.Count == 0) return;
        short[]? datum = p.Planes[0]?.HeightsRelative;
        if (datum is null || datum.Length == 0 || datum[0] == 0) return;
        int shift = datum[0];
        foreach (TilePrefabPlane? plane in p.Planes)
        {
            short[]? h = plane?.HeightsRelative;
            if (h is null) continue;
            for (int i = 0; i < h.Length; i++) h[i] = (short)Math.Clamp(h[i] - shift, short.MinValue, short.MaxValue);
        }
    }

    static TilePrefab RotateOnce(TilePrefab p)
    {
        int w = p.Width, h = p.Height;
        var r = new TilePrefab { Name = p.Name, Width = h, Height = w, PlaneCount = p.PlaneCount };
        foreach (TilePrefabPlane? plane in p.Planes)
        {
            if (plane is null) { r.Planes.Add(null); continue; }
            var np = new TilePrefabPlane
            {
                HeightsRelative = plane.HeightsRelative is null ? null : RotateCorners(plane.HeightsRelative, w, h),
                Underlay = plane.Underlay is null ? null : RotateTiles(plane.Underlay, w, h),
                Overlay = plane.Overlay is null ? null : RotateTiles(plane.Overlay, w, h),
                OverlayShape = plane.OverlayShape is null ? null : RotateTiles(plane.OverlayShape, w, h),
                OverlayRotation = plane.OverlayRotation is null ? null : RotateTiles(plane.OverlayRotation, w, h),
                Settings = plane.Settings is null ? null : RotateTiles(plane.Settings, w, h),
            };
            // Turning the tiles turns the overlays with them. TrimPlane nulls an all-zero rotation layer
            // independently of Overlay, so a prefab whose overlays are all authored at rotation 0 arrives here
            // with no layer at all: materialise it rather than skipping the bump, or a DiagonalHalf path comes
            // out of a quarter turn still pointing the old way.
            if (np.Overlay is not null)
            {
                np.OverlayRotation ??= new byte[w * h];
                for (int i = 0; i < np.OverlayRotation.Length; i++)
                    if (np.Overlay[i] != 0) np.OverlayRotation[i] = (byte)((np.OverlayRotation[i] + 1) & 3);
            }
            r.Planes.Add(np);
        }
        foreach (TilePrefabObject o in p.Objects)
        {
            // The footprint WIDTH as it lies before this turn (rotation swaps the axes on odd quarter turns).
            // Only the width is needed: the turn sends the occupied columns [px, px + sx) to the rows
            // [w - px - sx, w - px), so the new anchor row is w - px - sx, while the new anchor column is just
            // pz whatever the depth.
            int sx = (o.Rotation & 1) == 0 ? o.SizeX : o.SizeZ;
            r.Objects.Add(new TilePrefabObject
            {
                ArchetypeId = o.ArchetypeId, X = o.Z, Z = w - o.X - sx, Plane = o.Plane, Rotation = (o.Rotation + 1) & 3,
                SizeX = o.SizeX, SizeZ = o.SizeZ, Tags = o.Tags?.ToList(),
            });
        }
        foreach (TilePrefabMarker m in p.Markers)
            r.Markers.Add(new TilePrefabMarker { Name = m.Name, X = m.Z, Z = w - 1 - m.X, Plane = m.Plane, Tags = m.Tags?.ToList() });
        return r;
    }

    static T[] RotateTiles<T>(T[] src, int w, int h)
    {
        var dst = new T[w * h];
        for (int z = 0; z < h; z++)
            for (int x = 0; x < w; x++)
                dst[(w - 1 - x) * h + z] = src[z * w + x];
        return dst;
    }

    static short[] RotateCorners(short[] src, int w, int h)
    {
        var dst = new short[(w + 1) * (h + 1)];
        for (int cz = 0; cz <= h; cz++)
            for (int cx = 0; cx <= w; cx++)
                dst[(w - cx) * (h + 1) + cz] = src[cz * (w + 1) + cx];
        return dst;
    }

    static void TrimPlane(TilePrefabPlane p)
    {
        if (p.HeightsRelative is not null && p.HeightsRelative.All(v => v == 0)) p.HeightsRelative = null;
        if (p.Underlay is not null && p.Underlay.All(v => v == 0)) p.Underlay = null;
        if (p.Overlay is not null && p.Overlay.All(v => v == 0)) p.Overlay = null;
        if (p.OverlayShape is not null && p.OverlayShape.All(v => v == 0)) p.OverlayShape = null;
        if (p.OverlayRotation is not null && p.OverlayRotation.All(v => v == 0)) p.OverlayRotation = null;
        if (p.Settings is not null && p.Settings.All(v => v == 0)) p.Settings = null;
    }

    static TilePrefab Clone(TilePrefab p) => new()
    {
        Name = p.Name, Width = p.Width, Height = p.Height, PlaneCount = p.PlaneCount,
        Planes = p.Planes.Select(pl => pl is null ? null : new TilePrefabPlane
        {
            HeightsRelative = (short[]?)pl.HeightsRelative?.Clone(), Underlay = (ushort[]?)pl.Underlay?.Clone(), Overlay = (ushort[]?)pl.Overlay?.Clone(),
            OverlayShape = (byte[]?)pl.OverlayShape?.Clone(), OverlayRotation = (byte[]?)pl.OverlayRotation?.Clone(), Settings = (byte[]?)pl.Settings?.Clone(),
        }).ToList(),
        Objects = p.Objects.Select(o => new TilePrefabObject { ArchetypeId = o.ArchetypeId, X = o.X, Z = o.Z, Plane = o.Plane, Rotation = o.Rotation, SizeX = o.SizeX, SizeZ = o.SizeZ, Tags = o.Tags?.ToList() }).ToList(),
        Markers = p.Markers.Select(m => new TilePrefabMarker { Name = m.Name, X = m.X, Z = m.Z, Plane = m.Plane, Tags = m.Tags?.ToList() }).ToList(),
    };
}
