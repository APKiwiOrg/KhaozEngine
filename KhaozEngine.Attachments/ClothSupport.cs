using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

/// <summary>A kinematic support with fixed local geometry and a rigid world pose.</summary>
public readonly struct ClothSupport
{
    private ClothSupport(ColliderCapsule capsule, Vector3 planeNormal, Quaternion rotation, Vector3 translation,
        ClothSupportIdentity identity = default, bool lowerSegment = false, bool adjustable = false,
        ClothLowerArc lowerArc = default, int pathStart = 0, int pathCount = 0)
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
        Adjustable = adjustable;
        _lowerArc = lowerArc;
        _pathStart = pathStart;
        _pathCount = pathCount;
    }

    internal ColliderCapsule LocalCapsule { get; }
    internal Vector3 LocalPlaneNormal { get; }
    internal Quaternion Rotation { get; }
    internal Vector3 Translation { get; }
    public ClothSupportIdentity Identity { get; }
    public bool IsLowerSegment { get; }

    private bool Adjustable { get; }
    private readonly ClothLowerArc _lowerArc;
    private readonly int _pathStart, _pathCount;

    private bool SharesLowerArc(in ClothSupport other) => _lowerArc.IsActive && other._lowerArc.IsActive &&
        _pathCount == other._pathCount;

    private ReadOnlySpan<ClothHeldEdge> Edges(ClothHeldEdge[]? path) =>
        _pathCount == 0 ? [] : path.AsSpan(_pathStart, _pathCount);
    private bool LinearLower(in ClothSupport other) => IsLowerSegment && !SharesLowerArc(other);
    private ColliderCapsule LowerWorld => _lowerArc.IsActive ? WorldCapsule : LocalCapsule;

    internal static ClothSupport PosedCapsule(ColliderCapsule local, Quaternion rotation, Vector3 translation,
        ClothSupportIdentity identity)
    {
        ValidateCapsule(local);
        return new ClothSupport(local, default, rotation, translation, identity, adjustable: true);
    }

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

    internal static ClothSupport HeldLowerSegment(in HeldChainSegment segment, ClothHeldEdge edge,
        ClothSupportIdentity identity, int pathStart, int pathCount)
    {
        ColliderCapsule world = segment.Capsule;
        ValidateCapsule(world);
        return new ClothSupport(new ColliderCapsule(Vector3.Zero, edge.LocalAxis, world.Radius), default,
            segment.Rotation, world.Start, identity, lowerSegment: true, adjustable: true,
            ClothLowerArc.From(segment.Anchor, segment.ChainRoot), pathStart, pathCount);
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
    internal ColliderCapsule CapsuleAt(in ClothSupport target, float time, in ClothHeldPaths paths)
    {
        if (time == 0) return WorldCapsule;
        if (time == 1) return target.WorldCapsule;
        if (LinearLower(target))
        {
            ColliderCapsule opening = LowerWorld, closing = target.LowerWorld;
            return new ColliderCapsule(Vector3.Lerp(opening.Start, closing.Start, time),
                Vector3.Lerp(opening.End, closing.End, time), float.Lerp(opening.Radius, closing.Radius, time));
        }
        Quaternion rotation = ClothContactSweep.Interpolate(Rotation, target.Rotation, time);
        Vector3 translation = SharesLowerArc(target) ? HeldRootAt(target, time, paths) :
            Vector3.Lerp(Translation, target.Translation, time);
        ColliderCapsule local = Adjustable ? new ColliderCapsule(
            Vector3.Lerp(LocalCapsule.Start, target.LocalCapsule.Start, time),
            Vector3.Lerp(LocalCapsule.End, target.LocalCapsule.End, time),
            float.Lerp(LocalCapsule.Radius, target.LocalCapsule.Radius, time)) : LocalCapsule;
        return new ColliderCapsule(Vector3.Transform(local.Start, rotation) + translation,
            Vector3.Transform(local.End, rotation) + translation, local.Radius);
    }

    private Vector3 HeldRootAt(in ClothSupport target, float time, in ClothHeldPaths paths)
    {
        Vector3 root = _lowerArc.At(target._lowerArc, time);
        var before = Edges(paths.Opening);
        var after = target.Edges(paths.Closing);
        for (int i = 0; i < before.Length; i++) root += before[i].At(after[i], time);
        return root;
    }

    internal Vector3 PlaneNormal => Vector3.Transform(LocalPlaneNormal, Rotation);

    internal bool Matches(in ClothSupport other) =>
        (IsPlane || LocalCapsule.Radius > 0) && Identity == other.Identity &&
        IsLowerSegment == other.IsLowerSegment && LocalPlaneNormal == other.LocalPlaneNormal &&
        (IsLowerSegment || Adjustable == other.Adjustable) &&
        (IsLowerSegment || Adjustable ? other.LocalCapsule.Radius > 0 : LocalCapsule == other.LocalCapsule);

    internal bool SamePose(in ClothSupport other, in ClothHeldPaths paths) =>
        Rotation == other.Rotation && Translation == other.Translation &&
        LocalCapsule == other.LocalCapsule && _lowerArc == other._lowerArc &&
        Edges(paths.Opening).SequenceEqual(other.Edges(paths.Closing));

    internal float CurvedCapsuleReach(in ClothSupport other, float angle, in ClothHeldPaths paths)
    {
        if (LinearLower(other)) return 0;
        float reach = MathF.Max(LocalCapsule.Start.Length(), LocalCapsule.End.Length());
        if (!Adjustable) return reach * angle * angle;
        float lastReach = MathF.Max(other.LocalCapsule.Start.Length(), other.LocalCapsule.End.Length());
        float shift = MathF.Max(Vector3.Distance(LocalCapsule.Start, other.LocalCapsule.Start),
            Vector3.Distance(LocalCapsule.End, other.LocalCapsule.End));
        float curvature = MathF.Max(reach, lastReach) * angle * angle + 2 * angle * shift;
        if (!SharesLowerArc(other)) return curvature;
        curvature += _lowerArc.CurvatureTo(other._lowerArc);
        var before = Edges(paths.Opening);
        var after = other.Edges(paths.Closing);
        for (int i = 0; i < before.Length; i++) curvature += before[i].CurvatureTo(after[i]);
        return curvature;
    }

    internal ClothSupport InterpolateRigid(in ClothSupport target, float amount)
    {
        if (amount == 1) return target;
        var local = new ColliderCapsule(Vector3.Lerp(LocalCapsule.Start, target.LocalCapsule.Start, amount),
            Vector3.Lerp(LocalCapsule.End, target.LocalCapsule.End, amount),
            float.Lerp(LocalCapsule.Radius, target.LocalCapsule.Radius, amount));
        return PosedCapsule(local, Quaternion.Slerp(Rotation, target.Rotation, amount),
            Vector3.Lerp(Translation, target.Translation, amount), Identity);
    }

    internal ClothSupport Rebased(Vector3 change) => IsLowerSegment && !_lowerArc.IsActive
        ? LowerSegment(LocalCapsule with { Start = LocalCapsule.Start + change, End = LocalCapsule.End + change }, Identity)
        : new ClothSupport(LocalCapsule, LocalPlaneNormal, Rotation, Translation + change, Identity,
            IsLowerSegment, Adjustable, _lowerArc.Rebased(change), _pathStart, _pathCount);

    internal Vector3 SurfaceDisplacement(in ClothSupport previous, Vector3 worldPoint)
    {
        Vector3 local = Vector3.Transform(worldPoint - Translation, Quaternion.Conjugate(Rotation));
        return worldPoint - previous.ToWorld(local);
    }

    internal Vector3 SurfaceClosingDerivative(in ClothSupport previous, Vector3 worldPoint) =>
        Translation - previous.Translation + Vector3.Cross(
            ClothContactSweep.AngularDisplacement(previous.Rotation, Rotation), worldPoint - Translation);

    private Vector3 HeldRootClosingDerivative(in ClothSupport previous, in ClothHeldPaths paths)
    {
        Vector3 root = Vector3.Transform(_lowerArc.LocalStart, _lowerArc.AnchorRotation) + _lowerArc.AnchorOrigin;
        Vector3 derivative = _lowerArc.ClosingDerivative(previous._lowerArc, root);
        var before = previous.Edges(paths.Opening);
        var after = Edges(paths.Closing);
        for (int i = 0; i < after.Length; i++) derivative += after[i].ClosingDerivative(before[i]);
        return derivative;
    }

    internal Vector3 CapsuleClosingDerivative(in ClothSupport previous, Vector3 point,
        ChainCapsuleCollision.AxisFraction fraction, Vector3 normal, in ClothHeldPaths paths)
    {
        if (!IsLowerSegment && !Adjustable) return SurfaceClosingDerivative(previous, point);

        bool linear = LinearLower(previous);
        ColliderCapsule before = linear ? previous.LowerWorld : previous.LocalCapsule;
        ColliderCapsule now = linear ? LowerWorld : LocalCapsule;
        Vector3 axisChange = (float)fraction.StartWeight * (now.Start - before.Start) +
            (float)fraction.EndWeight * (now.End - before.End);
        Vector3 radiusChange = (now.Radius - before.Radius) * normal;
        if (linear) return axisChange + radiusChange;
        Vector3 surface = SurfaceClosingDerivative(previous, point);
        if (SharesLowerArc(previous)) surface += HeldRootClosingDerivative(previous, paths) - (Translation - previous.Translation);
        return surface + Vector3.Transform(axisChange, Rotation) + radiusChange;
    }

    internal Vector3 CapsuleDisplacement(in ClothSupport previous, Vector3 point,
        ChainCapsuleCollision.AxisFraction fraction, Vector3 normal)
    {
        if (!IsLowerSegment && !Adjustable) return SurfaceDisplacement(previous, point);

        bool linear = LinearLower(previous);
        ColliderCapsule before = linear ? previous.LowerWorld : previous.LocalCapsule;
        ColliderCapsule now = linear ? LowerWorld : LocalCapsule;
        Vector3 priorCenter = (float)fraction.StartWeight * before.Start + (float)fraction.EndWeight * before.End;
        if (linear)
        {
            Vector3 previousNormal = PreviousRadial(now.End - now.Start, before.End - before.Start, normal);
            return point - (priorCenter + before.Radius * previousNormal);
        }

        Vector3 localNormal = Vector3.Transform(normal, Quaternion.Conjugate(Rotation));
        Vector3 priorRadial = PreviousRadial(now.End - now.Start, before.End - before.Start, localNormal);
        return point - previous.ToWorld(priorCenter + before.Radius * priorRadial);
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
