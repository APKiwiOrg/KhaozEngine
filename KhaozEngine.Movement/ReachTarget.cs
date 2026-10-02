using System;
using System.Numerics;

namespace KhaozEngine.Movement;

/// <summary>A capsule, yawed box or point in absolute metres. The default value is invalid.</summary>
public readonly struct ReachTarget
{
    internal ReachTargetKind Kind { get; }
    internal MovementBody Body { get; }
    internal Vector3 Centre { get; }
    internal Vector3 HalfExtents { get; }
    internal float YawRadians { get; }

    ReachTarget(ReachTargetKind kind, MovementBody body, Vector3 centre, Vector3 halfExtents, float yawRadians)
    {
        Kind = kind;
        Body = body;
        Centre = centre;
        HalfExtents = halfExtents;
        YawRadians = yawRadians;
    }

    public static ReachTarget Capsule(in MovementBody body)
    {
        body.Validate(nameof(body));
        return new ReachTarget(ReachTargetKind.Capsule, body, body.Centre, default, 0f);
    }

    /// <summary>Positive yaw rotates local +X toward world -Z, matching a physics pose about +Y.</summary>
    public static ReachTarget Box(Vector3 centre, Vector3 halfExtents, float yawRadians = 0f)
    {
        if (!MovementBody.IsFinite(centre))
            throw new ArgumentOutOfRangeException(nameof(centre), "Centre must be finite.");
        if (!MovementBody.IsFinite(halfExtents) || halfExtents.X <= 0f || halfExtents.Y < 0f || halfExtents.Z <= 0f)
            throw new ArgumentOutOfRangeException(nameof(halfExtents), "Horizontal half-extents must be positive and vertical half-extent nonnegative, all finite.");
        if (!float.IsFinite(yawRadians))
            throw new ArgumentOutOfRangeException(nameof(yawRadians), "Yaw must be finite.");
        return new ReachTarget(ReachTargetKind.Box, default, centre, halfExtents, yawRadians);
    }

    public static ReachTarget Point(Vector3 position)
    {
        if (!MovementBody.IsFinite(position))
            throw new ArgumentOutOfRangeException(nameof(position), "Position must be finite.");
        return new ReachTarget(ReachTargetKind.Point, default, position, default, 0f);
    }
}

internal enum ReachTargetKind : byte
{
    Invalid,
    Capsule,
    Box,
    Point,
}
