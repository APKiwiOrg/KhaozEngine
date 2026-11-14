using System;
using System.Numerics;

namespace KhaozEngine.TileWorld;

/// <summary>
/// The absolute bounds tile world physics declares for each collider it builds, which feed the movement extent it
/// hands the movement installer: every point of the shape moved by its pose in float, then widened outward by one
/// representable value on every side. The one rule physics provenance captures its bounds with and collision box
/// and walk surface placement check against, so a box that places has finite declared bounds, as the movement
/// extent requires.
/// </summary>
internal static class TileColliderBounds
{
    /// <summary>Bounds that hold nothing yet, which the first point included replaces.</summary>
    internal static readonly Vector3 EmptyMinimum = new(float.PositiveInfinity), EmptyMaximum = new(float.NegativeInfinity);

    /// <summary>Whether a backend and the declared movement extent can hold a placed box: finite values, positive
    /// half extents, bottom below top, a unit orientation and finite widened bounds, including turned corners.</summary>
    internal static bool HoldsBox(Vector3 centre, Vector3 halfExtents, float bottom, float top, Quaternion orientation)
    {
        if (!(Finite(centre) && Positive(halfExtents.X) && Positive(halfExtents.Y) && Positive(halfExtents.Z)
              && float.IsFinite(bottom) && float.IsFinite(top) && bottom < top
              && float.IsFinite(orientation.X) && float.IsFinite(orientation.Y) && float.IsFinite(orientation.Z)
              && float.IsFinite(orientation.W) && MathF.Abs(orientation.LengthSquared() - 1f) < 1e-3f))
            return false;
        Vector3 min = EmptyMinimum, max = EmptyMaximum;
        IncludeBox(halfExtents, centre, orientation, ref min, ref max);
        (Vector3 minimum, Vector3 maximum) = Widened(min, max);
        return Finite(minimum) && Finite(maximum);
    }

    static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    static bool Positive(float value) => value > 0f && float.IsFinite(value);

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
