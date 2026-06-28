using System.Numerics;

namespace KhaozEngine.Physics;

/// <summary>A rigid transform: world position + orientation. The pose of a static body or a query shape.</summary>
public readonly record struct Pose(Vector3 Position, Quaternion Orientation)
{
    /// <summary>A pose at <paramref name="position"/> with identity orientation.</summary>
    public static Pose At(Vector3 position) => new(position, Quaternion.Identity);
}
