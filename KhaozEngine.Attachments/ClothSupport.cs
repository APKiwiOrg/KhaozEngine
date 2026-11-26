using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

/// <summary>A kinematic support with fixed local geometry and a rigid world pose.</summary>
public readonly struct ClothSupport
{
    private ClothSupport(ColliderCapsule capsule, Vector3 planeNormal, Quaternion rotation, Vector3 translation)
    {
        if (!float.IsFinite(rotation.LengthSquared()) || MathF.Abs(rotation.LengthSquared() - 1) > .0001f)
            throw new ArgumentOutOfRangeException(nameof(rotation), "A unit rotation is required.");
        if (!ChainMath.IsFinite(translation))
            throw new ArgumentOutOfRangeException(nameof(translation));

        LocalCapsule = capsule;
        LocalPlaneNormal = planeNormal;
        Rotation = Quaternion.Normalize(rotation);
        Translation = translation;
    }

    internal ColliderCapsule LocalCapsule { get; }
    internal Vector3 LocalPlaneNormal { get; }
    internal Quaternion Rotation { get; }
    internal Vector3 Translation { get; }
    internal bool IsPlane => LocalPlaneNormal != Vector3.Zero;

    /// <summary>Poses a capsule whose radius and end points are in local metres.</summary>
    public static ClothSupport Capsule(ColliderCapsule capsule, Quaternion rotation, Vector3 translation)
    {
        if (!float.IsFinite(capsule.Radius) || capsule.Radius <= 0 || !ChainMath.IsFinite(capsule.Start) ||
            !ChainMath.IsFinite(capsule.End))
            throw new ArgumentOutOfRangeException(nameof(capsule));

        return new ClothSupport(capsule, default, rotation, translation);
    }

    /// <summary>Poses a plane through the local origin. Its unit normal identifies the exterior.</summary>
    public static ClothSupport Plane(Vector3 normal, Quaternion rotation, Vector3 translation)
    {
        if (!ChainMath.IsFinite(normal) || MathF.Abs(normal.LengthSquared() - 1) > .0001f)
            throw new ArgumentOutOfRangeException(nameof(normal), "A unit normal is required.");

        return new ClothSupport(default, Vector3.Normalize(normal), rotation, translation);
    }

    internal ColliderCapsule WorldCapsule =>
        new(ToWorld(LocalCapsule.Start), ToWorld(LocalCapsule.End), LocalCapsule.Radius);
    internal ColliderCapsule CapsuleAt(in ClothSupport target, float time)
    {
        if (time == 0) return WorldCapsule;
        if (time == 1) return target.WorldCapsule;
        Quaternion rotation = Quaternion.Slerp(Rotation, target.Rotation, time);
        Vector3 translation = Vector3.Lerp(Translation, target.Translation, time);
        return new ColliderCapsule(Vector3.Transform(LocalCapsule.Start, rotation) + translation,
            Vector3.Transform(LocalCapsule.End, rotation) + translation, LocalCapsule.Radius);
    }

    internal Vector3 PlaneNormal => Vector3.Transform(LocalPlaneNormal, Rotation);

    internal bool Matches(in ClothSupport other) =>
        (IsPlane || LocalCapsule.Radius > 0) && LocalPlaneNormal == other.LocalPlaneNormal &&
        LocalCapsule == other.LocalCapsule;

    internal Vector3 SurfaceDisplacement(in ClothSupport previous, Vector3 worldPoint)
    {
        Vector3 local = Vector3.Transform(worldPoint - Translation, Quaternion.Conjugate(Rotation));
        return worldPoint - previous.ToWorld(local);
    }

    private Vector3 ToWorld(Vector3 local) => Vector3.Transform(local, Rotation) + Translation;
}
