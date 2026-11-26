using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

/// <summary>A kinematic support with fixed local geometry and a rigid world pose.</summary>
public readonly struct ClothSupport
{
    private ClothSupport(ColliderCapsule capsule, Vector3 planeNormal, Quaternion rotation, Vector3 translation,
        ClothSupportIdentity identity = default, bool lowerSegment = false)
    {
        if (!float.IsFinite(rotation.LengthSquared()) || MathF.Abs(rotation.LengthSquared() - 1) > .0001f)
            throw new ArgumentOutOfRangeException(nameof(rotation), "A unit rotation is required.");
        if (!ChainMath.IsFinite(translation))
            throw new ArgumentOutOfRangeException(nameof(translation));

        LocalCapsule = capsule;
        LocalPlaneNormal = planeNormal;
        Rotation = Quaternion.Normalize(rotation);
        Translation = translation;
        Identity = identity;
        IsLowerSegment = lowerSegment;
    }

    internal ColliderCapsule LocalCapsule { get; }
    internal Vector3 LocalPlaneNormal { get; }
    internal Quaternion Rotation { get; }
    internal Vector3 Translation { get; }
    public ClothSupportIdentity Identity { get; }
    public bool IsLowerSegment { get; }

    internal bool IsPlane => LocalPlaneNormal != Vector3.Zero;

    /// <summary>Poses a capsule whose radius and end points are in local metres.</summary>
    public static ClothSupport Capsule(ColliderCapsule capsule, Quaternion rotation, Vector3 translation) =>
        Capsule(capsule, rotation, translation, default);

    /// <summary>Poses a capsule with a stable owning part and optional detailed geometry group.</summary>
    public static ClothSupport Capsule(ColliderCapsule capsule, Quaternion rotation, Vector3 translation,
        ClothSupportIdentity identity)
    {
        ValidateCapsule(capsule);
        return new ClothSupport(capsule, default, rotation, translation, identity);
    }

    /// <summary>Supplies a lower chain's already solved world segment and radius at one end of the step.</summary>
    public static ClothSupport LowerSegment(ColliderCapsule capsule, ClothSupportIdentity identity)
    {
        ValidateCapsule(capsule);
        return new ClothSupport(capsule, default, Quaternion.Identity, Vector3.Zero, identity, lowerSegment: true);
    }

    private static void ValidateCapsule(ColliderCapsule capsule)
    {
        if (!float.IsFinite(capsule.Radius) || capsule.Radius <= 0 || !ChainMath.IsFinite(capsule.Start) ||
            !ChainMath.IsFinite(capsule.End))
            throw new ArgumentOutOfRangeException(nameof(capsule));
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
        if (IsLowerSegment)
            return new ColliderCapsule(Vector3.Lerp(LocalCapsule.Start, target.LocalCapsule.Start, time),
                Vector3.Lerp(LocalCapsule.End, target.LocalCapsule.End, time),
                float.Lerp(LocalCapsule.Radius, target.LocalCapsule.Radius, time));
        Quaternion rotation = ClothContactSweep.Interpolate(Rotation, target.Rotation, time);
        Vector3 translation = Vector3.Lerp(Translation, target.Translation, time);
        return new ColliderCapsule(Vector3.Transform(LocalCapsule.Start, rotation) + translation,
            Vector3.Transform(LocalCapsule.End, rotation) + translation, LocalCapsule.Radius);
    }

    internal Vector3 PlaneNormal => Vector3.Transform(LocalPlaneNormal, Rotation);

    internal bool Matches(in ClothSupport other) =>
        (IsPlane || LocalCapsule.Radius > 0) && Identity == other.Identity &&
        IsLowerSegment == other.IsLowerSegment && LocalPlaneNormal == other.LocalPlaneNormal &&
        (IsLowerSegment ? other.LocalCapsule.Radius > 0 : LocalCapsule == other.LocalCapsule);

    internal Vector3 SurfaceDisplacement(in ClothSupport previous, Vector3 worldPoint)
    {
        Vector3 local = Vector3.Transform(worldPoint - Translation, Quaternion.Conjugate(Rotation));
        return worldPoint - previous.ToWorld(local);
    }

    internal Vector3 SurfaceClosingDerivative(in ClothSupport previous, Vector3 worldPoint) =>
        Translation - previous.Translation + Vector3.Cross(
            ClothContactSweep.AngularDisplacement(previous.Rotation, Rotation), worldPoint - Translation);

    internal Vector3 CapsuleClosingDerivative(in ClothSupport previous, Vector3 point,
        ChainCapsuleCollision.AxisFraction fraction, Vector3 normal)
    {
        if (!IsLowerSegment) return SurfaceClosingDerivative(previous, point);

        ColliderCapsule before = previous.LocalCapsule, now = LocalCapsule;
        return (float)fraction.StartWeight * (now.Start - before.Start) +
            (float)fraction.EndWeight * (now.End - before.End) + (now.Radius - before.Radius) * normal;
    }

    internal Vector3 CapsuleDisplacement(in ClothSupport previous, Vector3 point,
        ChainCapsuleCollision.AxisFraction fraction, Vector3 normal)
    {
        if (!IsLowerSegment) return SurfaceDisplacement(previous, point);

        ColliderCapsule before = previous.LocalCapsule, now = LocalCapsule;
        Vector3 priorCenter = (float)fraction.StartWeight * before.Start + (float)fraction.EndWeight * before.End;
        Vector3 previousNormal = PreviousRadial(now.End - now.Start, before.End - before.Start, normal);
        return point - (priorCenter + before.Radius * previousNormal);
    }

    private static Vector3 PreviousRadial(Vector3 from, Vector3 to, Vector3 normal)
    {
        float firstLength = from.Length(), lastLength = to.Length();
        if (firstLength == 0 || lastLength == 0) return normal;
        from /= firstLength;
        to /= lastLength;
        float cosine = Vector3.Dot(from, to);
        if (cosine <= -.999999f)
            return normal - 2 * Vector3.Dot(normal, from) * from;
        Vector3 cross = Vector3.Cross(from, to);
        // Minimum rotation transporting the radial vector between the two axes.
        Vector3 rotated = Vector3.Cross(cross, normal);
        return normal + rotated + Vector3.Cross(cross, rotated) / (1 + cosine);
    }

    private Vector3 ToWorld(Vector3 local) => Vector3.Transform(local, Rotation) + Translation;
}
