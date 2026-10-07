using System;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Support bounds for an installed, identity-oriented box leaf.
/// The caller supplies a live registry/index/pose from the same owner while holding its query gate.
/// This does not establish the selected world's candidate set or a general pose certificate.</summary>
internal static class CapsuleSweepLeafProjection
{
    internal static unsafe GeometryInterval Project(Shapes shapes, TypedIndex shape, RigidPose pose, Vector3 axis)
    {
        if (shapes is null || !shape.Exists || shape.Type != default(Box).TypeId ||
            pose.Orientation != Quaternion.Identity || !Finite(pose.Position) ||
            !Finite(axis) || axis == Vector3.Zero)
            return default;

        shapes[shape.Type].GetShapeData(shape.Index, out void* data, out int size);
        if (data == null || size < sizeof(Box)) return default;
        Box box = *(Box*)data;
        if (!Positive(box.HalfWidth) || !Positive(box.HalfHeight) || !Positive(box.HalfLength)) return default;

        double lower = double.NegativeInfinity, upper = double.NegativeInfinity;
        Matrix3x3 identity = Matrix3x3.Identity;
        for (int corner = 0; corner < 8; corner++)
        {
            Vector3 local = new((corner & 1) == 0 ? -box.HalfWidth : box.HalfWidth,
                (corner & 2) == 0 ? -box.HalfHeight : box.HalfHeight,
                (corner & 4) == 0 ? -box.HalfLength : box.HalfLength);
            GeometryVector vertex = RepresentedGeometryTransforms.Point(identity, pose.Position, local);
            if (!vertex.IsResolved) return default;
            GeometryInterval projection = vertex.X.Multiply(GeometryInterval.Exact(axis.X))
                .Add(vertex.Y.Multiply(GeometryInterval.Exact(axis.Y)))
                .Add(vertex.Z.Multiply(GeometryInterval.Exact(axis.Z)));
            if (!projection.IsResolved) return default;
            // A linear functional on the complete finite box attains its maximum at a vertex.
            // Taking maxima of both endpoint sets encloses the maximum without choosing one face.
            lower = Math.Max(lower, projection.Lower);
            upper = Math.Max(upper, projection.Upper);
        }
        return GeometryInterval.Enclose(lower, upper);
    }

    static bool Positive(float value) => float.IsFinite(value) && value > 0f;
    static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
