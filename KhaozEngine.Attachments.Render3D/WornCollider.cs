using System;
using System.Numerics;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>An authored rest-model capsule and its host bone's inverse bind.</summary>
internal readonly record struct WornCollider(int Bone, ColliderCapsule RestCapsule, Matrix4x4 InverseBind)
{
    internal static WornCollider Bind(int bone, in ColliderDefinition collider, in Matrix4x4 inverseBind) =>
        new(bone, new ColliderCapsule(collider.Start, collider.End, collider.Radius), inverseBind);

    internal ColliderCapsule Pose(in Matrix4x4 jointWorld)
    {
        Matrix4x4 restToWorld = InverseBind * jointWorld;
        // Apply the largest axis scale after composing, so the bind and inverse bind cancel before scaling.
        float scale = MathF.Sqrt(MathF.Max(Vector3.TransformNormal(Vector3.UnitX, restToWorld).LengthSquared(),
            MathF.Max(Vector3.TransformNormal(Vector3.UnitY, restToWorld).LengthSquared(),
                Vector3.TransformNormal(Vector3.UnitZ, restToWorld).LengthSquared())));
        return new ColliderCapsule(Vector3.Transform(RestCapsule.Start, restToWorld),
            Vector3.Transform(RestCapsule.End, restToWorld), RestCapsule.Radius * scale);
    }
}
