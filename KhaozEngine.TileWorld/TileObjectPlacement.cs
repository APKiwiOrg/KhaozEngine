using System;
using System.Numerics;

namespace KhaozEngine.TileWorld;

/// <summary>
/// Where a placed <see cref="TileObject"/>'s mesh sits in world metres and which way it turns: the ONE transform the
/// prop renderer draws with, the model pick tests, and <see cref="TileWalkSurfaces"/> reads a deck through. It lives
/// here rather than beside the renderer so a GPU-free head (the tile netcode's presenter, a server tool) reaches the
/// same numbers the picture was drawn at, instead of carrying a second copy of the placement rule that drifts.
/// <para>The local-to-world transform is <c>Matrix4x4.CreateRotationY(YawRadians(...)) *
/// Matrix4x4.CreateTranslation(AnchorPosition(...))</c> at scale 1, exactly the matrix a prop draw builds from its
/// placement, and <see cref="LocalToWorld"/> returns it.</para>
/// </summary>
public static class TileObjectPlacement
{
    /// <summary>Degrees of yaw one quarter turn of <see cref="TileObject.Rotation"/> adds.</summary>
    public const float DegreesPerRotation = 90f;

    /// <summary>The yaw in radians for an instance rotation, NEGATIVE per quarter turn. That sign is what makes
    /// <c>Matrix4x4.CreateRotationY</c> turn clockwise seen from above with north up: north is -z in world space
    /// (<see cref="TileWorldSpace"/>), and a row-vector rotation by t sends the west point (-0.5, 0, 0) to
    /// (-0.5 cos t, 0, +0.5 sin t), which only reaches the north point (0, 0, -0.5) at t of -90 degrees. Under
    /// rotation 1 a mesh point on the WEST side of the tile centre therefore lands on the NORTH side, which is
    /// the tile-world convention (0 west, 1 north, 2 east, 3 south). The archetype's yaw offset is folded in
    /// under the same sign, for a mesh authored off-axis.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="archetype"/> is null.</exception>
    public static float YawRadians(TileObjectArchetype archetype, int rotation)
    {
        ArgumentNullException.ThrowIfNull(archetype);
        return -(rotation * DegreesPerRotation + archetype.YawOffsetDegrees) * (MathF.PI / 180f);
    }

    /// <summary>Where an instance's mesh origin sits in world metres: the centre of the footprint it covers after
    /// rotation, at the document's ground height for that spot. A mesh is therefore authored centred on its own
    /// footprint, with its base at y 0.</summary>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static Vector3 AnchorPosition(TileWorldDocument doc, TileObjectArchetype archetype, TileObject o)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(archetype);
        ArgumentNullException.ThrowIfNull(o);

        AnchorPlanar(doc.TileSize, archetype, o, out float cx, out float cz);
        return new Vector3(cx, doc.HeightAt(cx, cz, o.Plane), cz);
    }

    /// <summary>The instance's model matrix: its mesh-local metres to world metres, rotation about +Y by
    /// <see cref="YawRadians"/> and then translation to <see cref="AnchorPosition"/>, at scale 1. The matrix a prop
    /// draw, a silhouette hull and a target outline are all drawn with.</summary>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static Matrix4x4 LocalToWorld(TileWorldDocument doc, TileObjectArchetype archetype, TileObject o) =>
        Matrix4x4.CreateRotationY(YawRadians(archetype, (o ?? throw new ArgumentNullException(nameof(o))).Rotation))
        * Matrix4x4.CreateTranslation(AnchorPosition(doc, archetype, o));

    // The planar half of AnchorPosition, with no height read. A caller that culls by position first (the walk
    // surface query) pays the four-corner lattice sample only for the objects that survive the cull.
    internal static void AnchorPlanar(float tileSize, TileObjectArchetype archetype, TileObject o,
                                      out float worldX, out float worldZ)
    {
        (int sizeX, int sizeZ) = TileFootprint.Rotated(archetype, o.Rotation);
        worldX = TileWorldSpace.WorldX(o.X + sizeX / 2f, tileSize);
        worldZ = TileWorldSpace.WorldZ(o.Z + sizeZ / 2f, tileSize);
    }

    // Cosine and sine of the YawRadians rotation, as CreateRotationY builds them, with one refinement: a total turn
    // that is a whole number of quarter turns answers the exact axis rather than MathF.Cos(-pi/2), which is 4e-8
    // off zero. That error is under a micrometre on any mesh, but it decides which side of an inclusive edge a
    // point exactly ON the edge falls, and a deck edge sits exactly on a tile corner often enough to matter.
    // Local to world is x' = x cos + z sin, z' = z cos - x sin, the row-vector rotation Vector3.Transform applies.
    internal static void PlanarBasis(TileObjectArchetype archetype, int rotation, out float cos, out float sin)
    {
        double quarters = (rotation * (double)DegreesPerRotation + archetype.YawOffsetDegrees) / DegreesPerRotation;
        if (quarters == Math.Floor(quarters) && Math.Abs(quarters) < 1e15)
        {
            // The yaw is minus the turn, so quarter k is an angle of -k * 90 degrees.
            switch ((int)(((long)quarters % 4 + 4) % 4))
            {
                case 0: cos = 1f; sin = 0f; return;
                case 1: cos = 0f; sin = -1f; return;
                case 2: cos = -1f; sin = 0f; return;
                default: cos = 0f; sin = 1f; return;
            }
        }
        float yaw = YawRadians(archetype, rotation);
        cos = MathF.Cos(yaw);
        sin = MathF.Sin(yaw);
    }
}
