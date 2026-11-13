using System;
using System.Numerics;

namespace KhaozEngine.TileWorld;

/// <summary>One authored collision box placed in the world, in absolute document metres: the box tile world physics
/// builds and the tile editor draws and reports. <see cref="Orientation"/> is the identity for a box whose whole turn
/// is a quarter turn, with <see cref="HalfX"/> and <see cref="HalfZ"/> already along the world axes, and a yaw about
/// +Y otherwise, with <see cref="YawDegrees"/> the turn clockwise seen from above.</summary>
internal readonly record struct TilePlacedCollisionBox(float CentreX, float CentreZ, float HalfX, float HalfZ,
    float Bottom, float Top, Quaternion Orientation, float YawDegrees)
{
    // The centre and half extents a physics box takes, the vertical pair derived from the bottom and the top the way
    // every other tile box derives it.
    internal Vector3 Centre => new(CentreX, (Bottom + Top) * 0.5f, CentreZ);

    internal Vector3 HalfExtents => new(HalfX, (Top - Bottom) * 0.5f, HalfZ);
}

/// <summary>The loaded regions' corner range, the window a box's lowest-corner search is clamped to. Taken once per
/// pass over a document, so placing many boxes does not walk the region table for each.</summary>
internal readonly struct TileLoadedCorners
{
    readonly int _minX, _maxX, _minZ, _maxZ;

    TileLoadedCorners(int minX, int maxX, int minZ, int maxZ) =>
        (_minX, _maxX, _minZ, _maxZ) = (minX, maxX, minZ, maxZ);

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
        return minX > maxX ? new TileLoadedCorners(0, 0, 0, 0) : new TileLoadedCorners(minX, maxX, minZ, maxZ);
    }

    internal int ClampX(float corner) => Clamp(corner, _minX, _maxX);

    internal int ClampZ(float corner) => Clamp(corner, _minZ, _maxZ);

    // Clamped in float before the conversion, because a cast of a float past the int range is not defined to saturate.
    static int Clamp(float corner, int min, int max) => corner <= min ? min : corner >= max ? max : (int)corner;
}

/// <summary>
/// Where an archetype's authored collision boxes (<see cref="TileObjectArchetype.CollisionBoxes"/>) stand once an
/// object is placed. The ONE placement rule tile world physics builds its object colliders from and the tile editor
/// draws and reports, so the two never disagree about where a seat is.
/// <para>Planar: the box's local centre goes through the object's anchor and planar basis exactly as a walk surface
/// rect does (<see cref="TileObjectPlacement.AnchorPlanar"/> and <see cref="TileObjectPlacement.PlanarBasis(TileObjectArchetype, int, out float, out float)"/>),
/// and the box's own axes turn by the whole turn: the instance rotation, the archetype's yaw offset and the box's
/// own yaw. A whole number of quarter turns answers exact axes, so such a box is axis-aligned with no
/// trigonometry.</para>
/// <para>Vertical: the top is the standing height (the document height at the anchor) plus the box's
/// <see cref="TileCollisionBox.MaxY"/>, and the bottom is the standing height plus its
/// <see cref="TileCollisionBox.MinY"/>.
/// A box resting on or sunk below the base (MinY at or below 0) reaches further down to the lowest ground lattice
/// corner under its plan bounds when that is lower, the rule the footprint box keeps, so on a slope it meets the
/// ground on its downhill side instead of leaving a gap a foot can catch under. A box raised off the base keeps its
/// authored bottom, so the space under a table top or an awning stays open.</para>
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
    /// non-finite or empty one, which places nothing, the way a walk surface rect that resolves empty covers
    /// nothing.</summary>
    internal static bool TryPlace(TileWorldDocument document, TileLoadedCorners corners, TileObjectArchetype archetype,
        TileObject o, TileCollisionBox? box, out TilePlacedCollisionBox placed)
    {
        placed = default;
        if (box is null) return false;
        float minX = box.MinX, maxX = box.MaxX, minY = box.MinY, maxY = box.MaxY, minZ = box.MinZ, maxZ = box.MaxZ;
        float boxYaw = box.YawDegrees;
        // A NaN fails every comparison, so it places nothing, like an inverted bound.
        if (!(minX < maxX && minY < maxY && minZ < maxZ) || !float.IsFinite(boxYaw)) return false;

        float tileSize = document.TileSize;
        TileObjectPlacement.AnchorPlanar(tileSize, archetype, o, out float anchorX, out float anchorZ);
        TileObjectPlacement.PlanarBasis(archetype, o.Rotation, out float cos, out float sin);
        float standing = document.HeightAt(anchorX, anchorZ, o.Plane);

        float localX = (minX + maxX) * 0.5f, localZ = (minZ + maxZ) * 0.5f;
        float halfX = (maxX - minX) * 0.5f, halfZ = (maxZ - minZ) * 0.5f;
        // Local to world through the object's basis, the inverse of the walk surface query's ToLocal.
        float centreX = anchorX + localX * cos + localZ * sin;
        float centreZ = anchorZ + localZ * cos - localX * sin;

        // The double sum decides the exact quarter turn from the authored values themselves. The float one is the angle
        // the trigonometry takes otherwise, summed in the order YawRadians sums the object's own turn.
        double turnDegrees =
            o.Rotation * (double)TileObjectPlacement.DegreesPerRotation + archetype.YawOffsetDegrees + boxYaw;
        float turn = o.Rotation * TileObjectPlacement.DegreesPerRotation + archetype.YawOffsetDegrees + boxYaw;
        float yawRadians = -turn * (MathF.PI / 180f);
        TileObjectPlacement.PlanarBasis(turnDegrees, yawRadians, out float boxCos, out float boxSin);
        bool quarterTurn = (boxCos == 0f && MathF.Abs(boxSin) == 1f) || (boxSin == 0f && MathF.Abs(boxCos) == 1f);

        float top = standing + maxY;
        float bottom = standing + minY;
        if (minY <= 0f)
        {
            // The plan bounds of the turned box, exact on a quarter turn where the basis is exact.
            float spanX = MathF.Abs(boxCos) * halfX + MathF.Abs(boxSin) * halfZ;
            float spanZ = MathF.Abs(boxSin) * halfX + MathF.Abs(boxCos) * halfZ;
            bottom = MathF.Min(bottom, LowestCornerUnder(document, corners, o.Plane,
                centreX - spanX, centreX + spanX, centreZ - spanZ, centreZ + spanZ));
        }

        float placedHalfX = halfX, placedHalfZ = halfZ;
        if (quarterTurn && boxSin != 0f) (placedHalfX, placedHalfZ) = (halfZ, halfX);
        // A value past the float range, or a box too thin to survive being added to its standing height, places
        // nothing rather than a collider no backend can hold.
        if (!(bottom < top) || !float.IsFinite(bottom) || !float.IsFinite(top) || !float.IsFinite(centreX)
            || !float.IsFinite(centreZ) || !float.IsFinite(placedHalfX) || !float.IsFinite(placedHalfZ))
            return false;

        placed = new TilePlacedCollisionBox(centreX, centreZ, placedHalfX, placedHalfZ, bottom, top,
            quarterTurn ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(Vector3.UnitY, yawRadians),
            quarterTurn ? 0f : turn);
        return true;
    }

    // The lowest lattice corner under a plan rect given in world metres, in metres. World z runs against tile z, so the
    // rect's lowest world z is its highest tile z. Clamped to the loaded corners, beyond which the ground is only ever
    // edge-extended from them, so a box reaching far past the world can neither overflow the corner range nor walk an
    // unbounded loop.
    static float LowestCornerUnder(TileWorldDocument document, TileLoadedCorners corners, int plane,
        float minX, float maxX, float minZ, float maxZ)
    {
        float tileSize = document.TileSize;
        int x0 = corners.ClampX(MathF.Floor(TileWorldSpace.TileX(minX, tileSize)));
        int x1 = corners.ClampX(MathF.Ceiling(TileWorldSpace.TileX(maxX, tileSize)));
        int z0 = corners.ClampZ(MathF.Floor(TileWorldSpace.TileZ(maxZ, tileSize)));
        int z1 = corners.ClampZ(MathF.Ceiling(TileWorldSpace.TileZ(minZ, tileSize)));
        short lowest = short.MaxValue;
        for (int z = z0; z <= z1; z++)
            for (int x = x0; x <= x1; x++)
                lowest = Math.Min(lowest, document.CornerHeightCm(x, z, plane));
        return lowest * 0.01f;
    }
}
