using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.TileWorld;

/// <summary>One authored collision box placed in the world, in absolute document metres: the box tile world physics
/// builds and the tile editor draws and reports. Checked before it is handed out: every value is a finite float, every
/// half extent is above 0 and <see cref="Orientation"/> is a unit quaternion. That is the identity for a box whose
/// whole turn is a quarter turn, with the half extents already along the world axes, and a yaw about +Y otherwise,
/// with <see cref="YawDegrees"/> the turn clockwise seen from above.</summary>
internal readonly record struct TilePlacedCollisionBox(Vector3 Centre, Vector3 HalfExtents, float Bottom, float Top,
    Quaternion Orientation, float YawDegrees)
{
    internal float CentreX => Centre.X;

    internal float CentreZ => Centre.Z;

    internal float HalfX => HalfExtents.X;

    internal float HalfZ => HalfExtents.Z;
}

/// <summary>The loaded regions' bounding corner range, which a box's ground search is clamped to: past it the ground
/// carries on level, as the floor sampler has it. Taken once per pass over a document, so placing many boxes does not
/// walk the region table for each.</summary>
internal readonly struct TileLoadedCorners
{
    readonly int _minX, _maxX, _minZ, _maxZ;
    readonly float _tileSize;

    TileLoadedCorners(int minX, int maxX, int minZ, int maxZ, float tileSize) =>
        (_minX, _maxX, _minZ, _maxZ, _tileSize) = (minX, maxX, minZ, maxZ, tileSize);

    /// <summary>The corners of every loaded region, from the first region's south-west corner to the last one's far
    /// edge inclusive. A document with no region answers the single corner at the origin.</summary>
    internal static TileLoadedCorners Of(TileWorldDocument document)
    {
        int minX = int.MaxValue, maxX = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue;
        foreach (RegionCoord region in document.Regions.Keys)
        {
            minX = Math.Min(minX, region.OriginX);
            maxX = Math.Max(maxX, region.OriginX + TileRegion.Size);
            minZ = Math.Min(minZ, region.OriginZ);
            maxZ = Math.Max(maxZ, region.OriginZ + TileRegion.Size);
        }
        return minX > maxX
            ? new TileLoadedCorners(0, 0, 0, 0, document.TileSize)
            : new TileLoadedCorners(minX, maxX, minZ, maxZ, document.TileSize);
    }

    /// <summary>The lattice corners under a plan rect in world metres, inclusive, clamped to these corners. World z runs
    /// against tile z, so the rect's lowest world z is its highest tile z.</summary>
    internal void Under(double minX, double maxX, double minZ, double maxZ, out int x0, out int x1, out int z0, out int z1)
    {
        x0 = Clamp(Math.Floor(TileWorldSpace.TileX(minX, _tileSize)), _minX, _maxX);
        x1 = Clamp(Math.Ceiling(TileWorldSpace.TileX(maxX, _tileSize)), _minX, _maxX);
        z0 = Clamp(Math.Floor(TileWorldSpace.TileZ(maxZ, _tileSize)), _minZ, _maxZ);
        z1 = Clamp(Math.Ceiling(TileWorldSpace.TileZ(minZ, _tileSize)), _minZ, _maxZ);
    }

    // Clamped before the conversion, because a cast past the int range is not defined to saturate. A NaN takes the
    // minimum rather than an undefined cast.
    static int Clamp(double corner, int min, int max) => !(corner > min) ? min : !(corner < max) ? max : (int)corner;
}

/// <summary>
/// Where an archetype's authored collision boxes (<see cref="TileObjectArchetype.CollisionBoxes"/>) stand once an
/// object is placed. The ONE placement rule tile world physics builds its object colliders from and the tile editor
/// draws and reports, so the two never disagree about where a seat is.
/// <para>Planar: the box's local centre goes through the object's anchor and planar basis exactly as a walk surface
/// rect does (<see cref="TileObjectPlacement.AnchorPlanar"/> and <see cref="TileObjectPlacement.PlanarBasis(TileObjectArchetype, int, out float, out float)"/>),
/// and the box's own axes turn by the whole turn: the instance rotation, the archetype's yaw offset and the box's
/// own yaw. Each of the two turns that is a whole number of quarter turns answers exact axes with no trigonometry,
/// and a whole turn that is one makes the box axis-aligned.</para>
/// <para>Vertical: the top is the standing height (the document height at the anchor) plus the box's
/// <see cref="TileCollisionBox.MaxY"/>, and the bottom is the standing height plus its
/// <see cref="TileCollisionBox.MinY"/>. A box resting on or sunk below the base (MinY at or below 0) reaches further
/// down to the lowest ground lattice corner under its plan bounds when that is lower, the rule the footprint box keeps,
/// so on a slope it meets the ground on its downhill side instead of leaving a gap a foot can catch under. Every corner
/// is read as the document reads it, a missing region's corner taking the edge-extended or base height, within the
/// loaded regions' bounding corners, and the search touches only the loaded regions under the box. A box raised off
/// the base keeps its authored bottom, so the space under a table top or an awning stays open.</para>
/// <para>Every sum is taken in double and only the final values become floats, each checked, so a box whose finite
/// bounds would overflow a float sum still places at its midpoint, and one that a float cannot hold places
/// nothing.</para>
/// </summary>
internal static class TileCollisionBoxes
{
    /// <summary>Whether physics describes a placed object of this archetype by its boxes: a non-roof <c>Solid</c> or
    /// <c>Diagonal</c> archetype that lists any. The loader refuses boxes on the other kinds, and this still holds the
    /// line for a kind edited after load.</summary>
    internal static bool Describes(TileObjectArchetype archetype) =>
        archetype is { IsRoof: false, CollisionBoxes.Count: > 0 }
        && archetype.CollisionKind is TileCollisionKind.Solid or TileCollisionKind.Diagonal;

    /// <summary>Places one box of a placed object's archetype. False for a null box, or one edited after load into a
    /// non-finite or empty one, and for one whose placed centre, extents or turn leave the float range or whose
    /// thickness does not survive being added to its standing height. Each places nothing, the way a walk surface rect
    /// that resolves empty covers nothing.</summary>
    internal static bool TryPlace(TileWorldDocument document, TileLoadedCorners corners, TileObjectArchetype archetype,
        TileObject o, TileCollisionBox? box, out TilePlacedCollisionBox placed)
    {
        placed = default;
        if (box is null) return false;
        float minX = box.MinX, maxX = box.MaxX, minY = box.MinY, maxY = box.MaxY, minZ = box.MinZ, maxZ = box.MaxZ;
        float boxYaw = box.YawDegrees;
        // A NaN fails every comparison, so it places nothing, like an inverted bound.
        if (!(minX < maxX && minY < maxY && minZ < maxZ) || !float.IsFinite(boxYaw)) return false;

        TileObjectPlacement.AnchorPlanar(document.TileSize, archetype, o, out float anchorX, out float anchorZ);
        TileObjectPlacement.PlanarBasis(archetype, o.Rotation, out float cos, out float sin);
        float standing = document.HeightAt(anchorX, anchorZ, o.Plane);

        // Every sum and difference is taken in double, so finite bounds whose float sum or difference overflows still
        // give the midpoint and the half extent a float holds. IEEE double arithmetic is the same on every
        // architecture, so a quarter-turned box stays exact. Only the final values become floats, and they are checked.
        double localX = ((double)minX + maxX) * 0.5, localZ = ((double)minZ + maxZ) * 0.5;
        double halfX = ((double)maxX - minX) * 0.5, halfZ = ((double)maxZ - minZ) * 0.5;
        // Local to world through the object's basis, the inverse of the walk surface query's ToLocal.
        double centreX = anchorX + localX * cos + localZ * sin;
        double centreZ = anchorZ + localZ * cos - localX * sin;

        // The double sum decides the exact quarter turn from the authored values themselves. The float one is the angle
        // the trigonometry takes otherwise, summed in the order YawRadians sums the object's own turn. A sum that leaves
        // the float range has no orientation and fails the final check.
        double turnDegrees =
            o.Rotation * (double)TileObjectPlacement.DegreesPerRotation + archetype.YawOffsetDegrees + boxYaw;
        float turn = o.Rotation * TileObjectPlacement.DegreesPerRotation + archetype.YawOffsetDegrees + boxYaw;
        float yawRadians = -turn * (MathF.PI / 180f);
        TileObjectPlacement.PlanarBasis(turnDegrees, yawRadians, out float boxCos, out float boxSin);
        bool quarterTurn = (boxCos == 0f && MathF.Abs(boxSin) == 1f) || (boxSin == 0f && MathF.Abs(boxCos) == 1f);

        double top = (double)standing + maxY;
        double bottom = (double)standing + minY;
        if (minY <= 0f)
        {
            // The plan bounds of the turned box, exact on a quarter turn where the basis is exact.
            double spanX = Math.Abs(boxCos) * halfX + Math.Abs(boxSin) * halfZ;
            double spanZ = Math.Abs(boxSin) * halfX + Math.Abs(boxCos) * halfZ;
            corners.Under(centreX - spanX, centreX + spanX, centreZ - spanZ, centreZ + spanZ,
                out int x0, out int x1, out int z0, out int z1);
            // Metres as the document's own CornerHeight converts them.
            bottom = Math.Min(bottom, LowestCornerCm(document, o.Plane, x0, x1, z0, z1, out _) * 0.01f);
        }

        if (quarterTurn && boxSin != 0f) (halfX, halfZ) = (halfZ, halfX);
        var centre = new Vector3((float)centreX, (float)((bottom + top) * 0.5), (float)centreZ);
        var halfExtents = new Vector3((float)halfX, (float)((top - bottom) * 0.5), (float)halfZ);
        Quaternion orientation = quarterTurn
            ? Quaternion.Identity
            : Quaternion.CreateFromAxisAngle(Vector3.UnitY, yawRadians);
        float bottomAt = (float)bottom, topAt = (float)top;
        if (!Holdable(centre, halfExtents, bottomAt, topAt, orientation)) return false;

        placed = new TilePlacedCollisionBox(centre, halfExtents, bottomAt, topAt, orientation, quarterTurn ? 0f : turn);
        return true;
    }

    // A box a backend can hold: every coordinate a finite float, every half extent above 0, and the orientation a unit
    // quaternion. A value past the float range, a turn whose sum left it, or a box too thin to survive being added to
    // its standing height fails here.
    static bool Holdable(Vector3 centre, Vector3 halfExtents, float bottom, float top, Quaternion orientation) =>
        float.IsFinite(centre.X) && float.IsFinite(centre.Y) && float.IsFinite(centre.Z)
        && Positive(halfExtents.X) && Positive(halfExtents.Y) && Positive(halfExtents.Z)
        && float.IsFinite(bottom) && float.IsFinite(top)
        && float.IsFinite(orientation.X) && float.IsFinite(orientation.Y) && float.IsFinite(orientation.Z)
        && float.IsFinite(orientation.W) && MathF.Abs(orientation.LengthSquared() - 1f) < 1e-3f;

    static bool Positive(float value) => value > 0f && float.IsFinite(value);

    /// <summary>
    /// The lowest lattice corner from (<paramref name="x0"/>, <paramref name="z0"/>) to (<paramref name="x1"/>,
    /// <paramref name="z1"/>) inclusive, in centimetres, each corner read as <see cref="TileWorldDocument.CornerHeightCm"/>
    /// reads it, but found from the loaded regions alone, so the work is the loaded terrain under the range and never
    /// the range's area. <see cref="short.MaxValue"/> for an empty range.
    /// <para>A corner a loaded region owns is read with that region. A corner of a missing region reads a loaded
    /// neighbour's edge only when that neighbour lies west, south or south-west of it and the corner sits on the missing
    /// region's first column or row, which puts it on the east or north seam of that loaded region, so the seams are
    /// read too, each corner once. Every other corner of a missing region reads the base height, which therefore counts
    /// once exactly when the range holds more corners than the owned and seam corners found in it.</para>
    /// <para><paramref name="cornersRead"/> is how many corners were read, which never exceeds the owned and seam
    /// corners of the loaded regions the range reaches.</para>
    /// </summary>
    internal static short LowestCornerCm(TileWorldDocument document, int plane, int x0, int x1, int z0, int z1,
        out long cornersRead)
    {
        cornersRead = 0;
        if (x0 > x1 || z0 > z1) return short.MaxValue;
        const int size = TileRegion.Size;
        short lowest = short.MaxValue;
        long owned = 0, read = 0;
        HashSet<(int X, int Z)>? seams = null;
        foreach (RegionCoord region in document.Regions.Keys)
        {
            int ox = region.OriginX, oz = region.OriginZ;
            int ax = Math.Max(x0, ox), bx = Math.Min(x1, ox + size - 1);
            int az = Math.Max(z0, oz), bz = Math.Min(z1, oz + size - 1);
            if (ax <= bx && az <= bz)
            {
                for (int z = az; z <= bz; z++)
                    for (int x = ax; x <= bx; x++)
                        lowest = Math.Min(lowest, document.CornerHeightCm(x, z, plane));
                long count = (long)(bx - ax + 1) * (bz - az + 1);
                owned += count;
                read += count;
            }
            // The east seam column, its north-east corner included, then the north seam row.
            int east = ox + size, north = oz + size;
            if (east >= x0 && east <= x1)
                for (int z = Math.Max(z0, oz), last = Math.Min(z1, north); z <= last; z++)
                    ReadSeamCorner(document, plane, east, z, ref seams, ref lowest, ref read);
            if (north >= z0 && north <= z1)
                for (int x = Math.Max(x0, ox), last = Math.Min(x1, east); x <= last; x++)
                    ReadSeamCorner(document, plane, x, north, ref seams, ref lowest, ref read);
        }
        long total = (long)(x1 - x0 + 1) * (z1 - z0 + 1);
        if (total > owned + (seams?.Count ?? 0)) lowest = Math.Min(lowest, document.UnsuppliedCornerHeightCm(plane));
        cornersRead = read;
        return lowest;
    }

    // A seam corner that a loaded region owns is read with that region. One in a missing region edge-reads, so it is
    // read here, once, however many seams it lies on.
    static void ReadSeamCorner(TileWorldDocument document, int plane, int x, int z, ref HashSet<(int X, int Z)>? seams,
        ref short lowest, ref long read)
    {
        if (document.GetRegion(RegionCoord.Of(x, z)) is not null) return;
        seams ??= new HashSet<(int X, int Z)>();
        if (!seams.Add((x, z))) return;
        lowest = Math.Min(lowest, document.CornerHeightCm(x, z, plane));
        read++;
    }
}
