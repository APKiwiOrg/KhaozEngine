using System.Numerics;

namespace KhaozEngine.Physics;

/// <summary>A rigid transform: world position + orientation. The pose of a static body or a query shape.</summary>
public readonly record struct Pose(Vector3 Position, Quaternion Orientation)
{
    /// <summary>A pose at <paramref name="position"/> with identity orientation.</summary>
    public static Pose At(Vector3 position) => new(position, Quaternion.Identity);

    /// <summary>The identity pose (origin, no rotation). Register a body whose geometry already carries its world
    /// position (e.g. a terrain chunk collision mesh) at this pose.</summary>
    public static Pose Identity => new(Vector3.Zero, Quaternion.Identity);

    /// <summary>Composes <paramref name="child"/> into this parent using the binary32 operations used to flatten
    /// compound colliders. Retain each rounded result before composing the next nesting level.</summary>
    public Pose Compose(Pose child)
    {
        Quaternion orientation = Quaternion.Concatenate(child.Orientation, Orientation);
        Vector3 position = Position + Vector3.Transform(child.Position, Orientation);
        return new(position, orientation);
    }
}
