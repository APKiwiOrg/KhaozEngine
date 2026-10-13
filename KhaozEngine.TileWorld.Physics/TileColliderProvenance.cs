using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.TileWorld.Physics;

internal readonly record struct TileColliderProvenance(string OwnerLocalId,
    Vector3 AbsoluteMinimum, Vector3 AbsoluteMaximum)
{
    internal static TileColliderProvenance Capture(string owner, TileCollider collider)
    {
        Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
        if (collider.Shape is TriangleMeshShape mesh)
            foreach (Vector3 point in mesh.Vertices) Include(point, collider.Pose, ref min, ref max);
        else if (collider.Shape is BoxShape box)
            for (int x = -1; x <= 1; x += 2)
                for (int y = -1; y <= 1; y += 2)
                    for (int z = -1; z <= 1; z += 2)
                        Include(new Vector3(x, y, z) * box.HalfExtents, collider.Pose, ref min, ref max);
        else throw new NotSupportedException("Tile installer provenance requires a box or ground mesh.");
        return new(owner, new(MathF.BitDecrement(min.X), MathF.BitDecrement(min.Y), MathF.BitDecrement(min.Z)),
            new(MathF.BitIncrement(max.X), MathF.BitIncrement(max.Y), MathF.BitIncrement(max.Z)));
    }

    static void Include(Vector3 point, Pose pose, ref Vector3 min, ref Vector3 max)
    {
        Vector3 absolute = Vector3.Transform(point, pose.Orientation) + pose.Position;
        min = Vector3.Min(min, absolute);
        max = Vector3.Max(max, absolute);
    }
}
