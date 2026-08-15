using System;

namespace KhaozEngine.TileWorld;

/// <summary>Authored per-tile flags. Bits 4-7 are free. Collision is DERIVED from these plus objects, never
/// authored here (see the collision baker).</summary>
[Flags]
public enum TileSettings : byte
{
    None = 0,
    /// <summary>Impassable ground (water, cliff face).</summary>
    Blocked = 1,
    /// <summary>Standing here hides roofs on the planes above (the OSRS global roof-hide trigger).</summary>
    Indoors = 2,
    /// <summary>Reserved for the over/under bridge plane trick. No semantics in this program.</summary>
    Bridge = 4,
    /// <summary>Skip the ground quad but keep the tile walkable (a plane whose floor is an object).</summary>
    NoDraw = 8,
}

/// <summary>How an overlay material cuts a tile. Values are stable on disk, add new shapes by value.</summary>
public enum TileOverlayShape : byte
{
    Full = 0,
    /// <summary>One triangle of the tile, along the diagonal the tile triangulation selects for it.</summary>
    DiagonalHalf = 1,
    CornerQuarter = 2,
    CornerThreeQuarter = 3,
}

/// <summary>The six dense layers of one plane of one region. Each layer is null until first written and
/// nulled again by <see cref="Trim(int)"/> when it is entirely default, so a region file carries only what was
/// authored. <see cref="Heights"/> above plane 0 is the one exception, see <see cref="Trim(int)"/>. Index with
/// <see cref="Index"/> (row-major, z then x).</summary>
public sealed class TilePlaneData
{
    /// <summary>Height of each tile's SW corner in centimetres. Null on plane 0 means 0 everywhere, null on a
    /// higher plane means "derive from plane 0 plus the document's plane height" (see
    /// TileWorldDocument.CornerHeightCm, which resolves this).</summary>
    public short[]? Heights { get; set; }
    /// <summary>Ground material id, 0 = void (no ground drawn, tile blocked).</summary>
    public ushort[]? Underlay { get; set; }
    /// <summary>Overlay material id, 0 = none.</summary>
    public ushort[]? Overlay { get; set; }
    /// <summary><see cref="TileOverlayShape"/> per tile.</summary>
    public byte[]? OverlayShape { get; set; }
    /// <summary>Quarter turns 0..3 per tile.</summary>
    public byte[]? OverlayRotation { get; set; }
    /// <summary><see cref="TileSettings"/> per tile.</summary>
    public byte[]? Settings { get; set; }

    public bool IsEmpty =>
        Heights is null && Underlay is null && Overlay is null && OverlayShape is null && OverlayRotation is null && Settings is null;

    public static int Index(int lx, int lz) => lz * TileRegion.Size + lx;

    /// <summary>Nulls any layer that is entirely default so the file form stays minimal. Pass the index of the
    /// plane this data belongs to: <see cref="Heights"/> is only dropped on plane 0, because above plane 0 a
    /// null height layer means "derive from plane 0 plus the plane lift", which is a DIFFERENT terrain from an
    /// authored flat zero, and dropping it would silently lift the plane on the next load.</summary>
    public void Trim(int planeIndex)
    {
        if (planeIndex == 0 && Heights is not null && AllZero(Heights)) Heights = null;
        if (Underlay is not null && AllZero(Underlay)) Underlay = null;
        if (Overlay is not null && AllZero(Overlay)) Overlay = null;
        if (OverlayShape is not null && AllZero(OverlayShape)) OverlayShape = null;
        if (OverlayRotation is not null && AllZero(OverlayRotation)) OverlayRotation = null;
        if (Settings is not null && AllZero(Settings)) Settings = null;
    }

    static bool AllZero<T>(T[] a) where T : struct, IEquatable<T>
    {
        T zero = default;
        foreach (T v in a) if (!v.Equals(zero)) return false;
        return true;
    }

    internal short[] HeightsOrAlloc() => Heights ??= new short[TileRegion.TileCount];
    internal ushort[] UnderlayOrAlloc() => Underlay ??= new ushort[TileRegion.TileCount];
    internal ushort[] OverlayOrAlloc() => Overlay ??= new ushort[TileRegion.TileCount];
    internal byte[] OverlayShapeOrAlloc() => OverlayShape ??= new byte[TileRegion.TileCount];
    internal byte[] OverlayRotationOrAlloc() => OverlayRotation ??= new byte[TileRegion.TileCount];
    internal byte[] SettingsOrAlloc() => Settings ??= new byte[TileRegion.TileCount];
}
