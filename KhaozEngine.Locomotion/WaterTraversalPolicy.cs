using System;

namespace KhaozEngine.Locomotion;

public enum WaterTraversalMode
{
    Legacy = 0,
    SurfaceSwimmer = 1,
    WadeOnly = 2,
    DryOnly = 3
}

/// <summary>Opt-in water capability and independent launch tuning. The finite proof limits are
/// part of the query/profile identity. Legacy callers keep using CharacterMovement.</summary>
public readonly record struct WaterTraversalPolicy
{
    public WaterTraversalMode Mode { get; }
    public float SurfaceJumpSpeed { get; }
    public float SurfaceContactToleranceMetres => SurfaceWaterMotion.ContactToleranceMetres;
    public float ContactSkinMetres => MovementQueryLease.CoverageSkinMetres;
    public int MaxCoverageSpans => MovementQueryLease.MaxCoverageSpans;
    public int MaxDomainContacts => MovementQueryLease.MaxDomainContacts;
    public int MaxCorrections => MovementCapsuleResolver.MaximumCorrections;
    public uint ResolverPolicyVersion => MovementCapsuleResolver.PolicyVersion;
    public uint ProjectionPolicyVersion => MovementConstraintProjection.PolicyVersion;
    public int MaxSolidContacts => MovementQueryLease.MaxSolidContacts;
    public int MaxDistinctNormals => MovementConstraintProjection.MaximumDistinctNormals;
    public int MaxWaterDomainsPerLease => MovementQueryLease.MaxWaterDomainsPerLease;
    public bool IsValid => Mode is >= WaterTraversalMode.Legacy and <= WaterTraversalMode.DryOnly &&
        float.IsFinite(SurfaceJumpSpeed) && SurfaceJumpSpeed >= 0;

    public WaterTraversalPolicy(WaterTraversalMode mode, float surfaceJumpSpeed = 0)
    {
        Mode = mode;
        SurfaceJumpSpeed = surfaceJumpSpeed;
        if (!IsValid) throw new ArgumentOutOfRangeException(nameof(mode));
    }
}
