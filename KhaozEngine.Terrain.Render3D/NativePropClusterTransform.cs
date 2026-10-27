using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain;

/// <summary>The binary64 position kernel shared by upper precision admission and non-welded native merges.</summary>
internal readonly struct NativePropClusterTransform
{
    readonly Matrix4x4 _pose;
    readonly double _x, _y, _z;

    internal NativePropClusterTransform(NativePropInstance instance, RenderAnchor anchor)
    {
        _pose = instance.LocalTransform;
        _x = (double)instance.Anchor.X - anchor.X + _pose.M41;
        _y = (double)instance.Anchor.Y - anchor.Y + _pose.M42;
        _z = (double)instance.Anchor.Z - anchor.Z + _pose.M43;
    }

    internal (double X, double Y, double Z) Position(Vector3 vertex) => (
        vertex.X * (double)_pose.M11 + vertex.Y * (double)_pose.M21 + vertex.Z * (double)_pose.M31 + _x,
        vertex.X * (double)_pose.M12 + vertex.Y * (double)_pose.M22 + vertex.Z * (double)_pose.M32 + _y,
        vertex.X * (double)_pose.M13 + vertex.Y * (double)_pose.M23 + vertex.Z * (double)_pose.M33 + _z);

    internal Matrix4x4 Binary32Matrix(NativePropInstance instance)
    {
        if (instance.WeldPlacement is { } placement)
            return Matrix4x4.CreateScale(placement.Scale) * Matrix4x4.CreateRotationY(placement.Yaw) *
                Matrix4x4.CreateTranslation(placement.X, placement.Y, placement.Z);
        var world = _pose;
        world.Translation = new((float)_x, (float)_y, (float)_z);
        return world;
    }
}
