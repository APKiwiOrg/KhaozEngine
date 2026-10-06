using System;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Locomotion;

/// <summary>Opaque portable water-domain identity. Default is not a known domain.</summary>
public readonly record struct MovementDomainKey
{
    public string WorldId { get; }
    public string LocalId { get; }
    public bool IsValid => MovementEnvironmentValidation.Name(WorldId) && MovementEnvironmentValidation.Name(LocalId);

    public MovementDomainKey(string worldId, string localId)
    {
        MovementEnvironmentValidation.RequireName(worldId, nameof(worldId));
        MovementEnvironmentValidation.RequireName(localId, nameof(localId));
        WorldId = worldId;
        LocalId = localId;
    }
}

/// <summary>Opaque portable occupied-space identity, independent of storage pages.</summary>
public readonly record struct MovementSpaceKey
{
    public string WorldId { get; }
    public string LocalId { get; }
    public bool IsValid => MovementEnvironmentValidation.Name(WorldId) && MovementEnvironmentValidation.Name(LocalId);

    public MovementSpaceKey(string worldId, string localId)
    {
        MovementEnvironmentValidation.RequireName(worldId, nameof(worldId));
        MovementEnvironmentValidation.RequireName(localId, nameof(localId));
        WorldId = worldId;
        LocalId = localId;
    }
}

/// <summary>Canonical support owner. Aliased seam pieces share this identity.</summary>
public readonly record struct MovementSupportKey
{
    public string WorldId { get; }
    public string LocalId { get; }
    public bool IsValid => MovementEnvironmentValidation.Name(WorldId) && MovementEnvironmentValidation.Name(LocalId);

    public MovementSupportKey(string worldId, string localId)
    {
        MovementEnvironmentValidation.RequireName(worldId, nameof(worldId));
        MovementEnvironmentValidation.RequireName(localId, nameof(localId));
        WorldId = worldId;
        LocalId = localId;
    }
}

/// <summary>Portable semantic compatibility. Local backing IDs and lease generations do not belong here.</summary>
public readonly record struct MovementQueryIdentity
{
    public string ClosureId { get; }
    public uint PolicyVersion { get; }
    public string ScopeDigest { get; }
    public bool IsValid => MovementEnvironmentValidation.Name(ClosureId) && PolicyVersion != 0 &&
        MovementEnvironmentValidation.Name(ScopeDigest);

    public MovementQueryIdentity(string closureId, uint policyVersion, string scopeDigest)
    {
        MovementEnvironmentValidation.RequireName(closureId, nameof(closureId));
        MovementEnvironmentValidation.Require(policyVersion != 0, nameof(policyVersion));
        MovementEnvironmentValidation.RequireName(scopeDigest, nameof(scopeDigest));
        ClosureId = closureId;
        PolicyVersion = policyVersion;
        ScopeDigest = scopeDigest;
    }
}

/// <summary>Host frame plus the exact physics origin. Facade vectors and heights use that physics frame.</summary>
public readonly record struct MovementFrameDescriptor
{
    public WorldFrame Frame { get; }
    public Vector3 PhysicsOrigin { get; }
    public ulong Epoch { get; }
    public bool IsValid => MovementEnvironmentValidation.Finite(PhysicsOrigin);

    public MovementFrameDescriptor(WorldFrame frame, Vector3 physicsOrigin, ulong epoch)
    {
        MovementEnvironmentValidation.Require(MovementEnvironmentValidation.Finite(physicsOrigin), nameof(physicsOrigin));
        Frame = frame;
        PhysicsOrigin = physicsOrigin;
        Epoch = epoch;
    }
}

internal static class MovementEnvironmentValidation
{
    public static bool Name(string? value) => !string.IsNullOrWhiteSpace(value);
    public static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    public static bool Nonnegative(float value) => float.IsFinite(value) && value >= 0f;
    public static bool Unit(Vector3 value) => Finite(value) && MathF.Abs(value.LengthSquared() - 1f) <= 0.0001f;
    public static bool Availability(MovementAvailability value) =>
        value is MovementAvailability.Unresolved or MovementAvailability.Known or MovementAvailability.Stale or
            MovementAvailability.Invalid or MovementAvailability.CapacityExceeded;
    public static void Require(bool condition, string parameter)
    {
        if (!condition) throw new ArgumentException("Invalid explicit movement environment value.", parameter);
    }
    public static void RequireName(string? value, string parameter) => Require(Name(value), parameter);
}
