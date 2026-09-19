using System;
using System.Numerics;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// Turns a head's movement keys into the one <see cref="TileDirection"/> a <c>TileCommand.Steer</c> carries,
/// relative to the camera. Client only, and its output is an integer direction, so no float it computes reaches
/// the simulation.
/// <para>It takes the camera's LOOK VECTOR rather than a yaw angle so no consumer has to match an angle
/// convention, and it owns the mapping from world x and z to tile directions because that is the tile world's
/// own fact.</para>
/// <para>The contract is the player's seat, not the arithmetic: forward walks the body AWAY from the camera,
/// back walks it toward the camera, right walks it toward SCREEN right and left toward screen left, for every
/// camera angle. The two sign facts that make that true are spelled out on <see cref="FromAxes"/>.</para>
/// </summary>
public static class TileSteering
{
    // Below this the camera is looking straight down and "forward" on the ground has no meaning.
    const float MinGroundLength = 1e-4f;

    // Indexed by the octant of the angle measured counter-clockwise from tile east, which is the order
    // MathF.Atan2 over (tile z, tile x) walks.
    static readonly TileDirection[] ByOctant =
    {
        TileDirection.E, TileDirection.NE, TileDirection.N, TileDirection.NW,
        TileDirection.W, TileDirection.SW, TileDirection.S, TileDirection.SE,
    };

    /// <summary>The direction held, or null.</summary>
    /// <param name="right">Right minus left, clamped to -1..1.</param>
    /// <param name="forward">Forward minus back, clamped to -1..1.</param>
    /// <param name="cameraForward">The camera's WORLD-space look direction, as <c>FollowCamera3D.Forward</c>
    /// gives it. Only its ground-plane part is read, and it need not be normalized.</param>
    /// <returns>Null for no input, for opposite keys that cancel, or for a camera looking straight down.</returns>
    public static TileDirection? FromAxes(int right, int forward, Vector3 cameraForward)
    {
        right = Math.Clamp(right, -1, 1);
        forward = Math.Clamp(forward, -1, 1);
        if (right == 0 && forward == 0) return null;

        // FACT ONE, the world-to-tile mirror. Tile north is world MINUS z, so the z component flips crossing the
        // seam. It flips through TileWorldSpace rather than by a local minus so the sign keeps living in the one
        // place that file claims it does. The size is 1 because a direction has no length in tiles and only the
        // sign is wanted here.
        float fx = TileWorldSpace.TileX(cameraForward.X, 1f);
        float fz = TileWorldSpace.TileZ(cameraForward.Z, 1f);
        float length = MathF.Sqrt(fx * fx + fz * fz);
        if (length < MinGroundLength) return null;
        fx /= length;
        fz /= length;

        // FACT TWO, the camera's handedness. The engine's cameras build their view with
        // Matrix4x4.CreateLookAt and world up, which is right handed, so screen right of a camera whose ground
        // forward is WORLD (wx, wz) is WORLD (-wz, wx): a camera facing world +z has world +x on its left. Put
        // that through the same mirror as the forward and screen right of a TILE forward (fx, fz) is (fz, -fx).
        // The two mirrors cancel, which is why looking tile north (world -z) puts tile east on the right.
        float rx = fz, rz = -fx;
        float x = fx * forward + rx * right, z = fz * forward + rz * right;

        // Angle from tile east, counter-clockwise, snapped to the nearest 45 degrees. Flooring (a + half an
        // octant) sends an exact boundary to the HIGHER octant, so a tie resolves toward the counter-clockwise
        // neighbour, which on a north-up map is the anticlockwise one. It is the same answer every time, which is
        // the only property a tie needs.
        float octants = MathF.Atan2(z, x) / (MathF.PI / 4f);
        int index = ((int)MathF.Floor(octants + 0.5f) % 8 + 8) % 8;
        return ByOctant[index];
    }
}
