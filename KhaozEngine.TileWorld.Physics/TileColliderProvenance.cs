using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.TileWorld.Physics;

internal readonly record struct TileColliderProvenance(string OwnerLocalId,
    Vector3 AbsoluteMinimum, Vector3 AbsoluteMaximum)
{
    // Bounds by TileColliderBounds, the rule collision box and walk surface placement check against too, so every
    // authored box or surface that places captures finite bounds here.
    internal static TileColliderProvenance Capture(string owner, TileCollider collider)
    {
        Vector3 min = TileColliderBounds.EmptyMinimum, max = TileColliderBounds.EmptyMaximum;
        Pose pose = collider.Pose;
        if (collider.Shape is TriangleMeshShape mesh)
            foreach (Vector3 point in mesh.Vertices)
                TileColliderBounds.Include(point, pose.Position, pose.Orientation, ref min, ref max);
        else if (collider.Shape is BoxShape box)
            TileColliderBounds.IncludeBox(box.HalfExtents, pose.Position, pose.Orientation, ref min, ref max);
        else throw new NotSupportedException("Tile installer provenance requires a box or ground mesh.");
        (Vector3 minimum, Vector3 maximum) = TileColliderBounds.Widened(min, max);
        return new(owner, minimum, maximum);
    }
}
