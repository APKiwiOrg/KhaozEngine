using System;
using System.Numerics;

namespace KhaozEngine.TileWorld;

/// <summary>
/// The absolute bounds tile world physics declares for each collider it builds, which feed the movement extent it
/// hands the movement installer: every point of the shape moved by its pose in float, then widened outward by one
/// representable value on every side. The one rule physics provenance captures its bounds with and the collision box
/// placement checks a box against, so a box that places is one whose declared bounds are finite, as the movement
/// extent requires.
/// </summary>
internal static class TileColliderBounds
{
    /// <summary>Bounds that hold nothing yet, which the first point included replaces.</summary>
    internal static readonly Vector3 EmptyMinimum = new(float.PositiveInfinity), EmptyMaximum = new(float.NegativeInfinity);

    /// <summary>Grows the bounds by one point of a shape, moved by its pose.</summary>
    internal static void Include(Vector3 point, Vector3 position, Quaternion orientation, ref Vector3 min, ref Vector3 max)
    {
        Vector3 absolute = Vector3.Transform(point, orientation) + position;
        min = Vector3.Min(min, absolute);
        max = Vector3.Max(max, absolute);
    }

    /// <summary>Grows the bounds by the eight corners of a box, moved by its pose.</summary>
    internal static void IncludeBox(Vector3 halfExtents, Vector3 position, Quaternion orientation,
        ref Vector3 min, ref Vector3 max)
    {
        for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
                for (int z = -1; z <= 1; z += 2)
                    Include(new Vector3(x, y, z) * halfExtents, position, orientation, ref min, ref max);
    }

    /// <summary>The bounds widened outward by one representable value on every side, so a backend's own rounding of
    /// the same transform never puts a point outside them. A bound on the float range's edge widens to an infinity.</summary>
    internal static (Vector3 Minimum, Vector3 Maximum) Widened(Vector3 min, Vector3 max) =>
        (new(MathF.BitDecrement(min.X), MathF.BitDecrement(min.Y), MathF.BitDecrement(min.Z)),
         new(MathF.BitIncrement(max.X), MathF.BitIncrement(max.Y), MathF.BitIncrement(max.Z)));
}
