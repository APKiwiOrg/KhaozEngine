using System;
using System.Numerics;

namespace KhaozEngine.Physics;

/// <summary>The outcome of a bounded capsule/finite-feature query. Default is unresolved.</summary>
public enum CapsuleFeatureStatus : byte
{
    Unresolved,
    Complete,
    NoFeature,
    Unavailable,
    Unsupported,
    Ambiguous,
    CapacityExceeded,
}

/// <summary>Finite geometric incidence, independent of gameplay support eligibility.</summary>
public enum CapsuleFeatureKind : byte
{
    None,
    FaceInterior,
    OpenBoundary,
    ConvexCrease,
    ConcaveCrease,
    Vertex,
}

/// <summary>One incident geometric face. Its identity is local to the result's original read interval.
/// The normal is geometric, not a smoothed collision-manifold normal.</summary>
public readonly record struct CapsuleIncidentFace(int FaceId, Vector3 Normal, float NormalError,
    CapsuleFeatureKind Incidence);

/// <summary>Immutable query data. A refusal has no usable witness, lease or written face prefix.
/// Structural values alone do not certify geometry or permit movement.</summary>
public readonly struct CapsuleFeatureResult
{
    public CapsuleFeatureStatus Status { get; }
    public int Written { get; }
    public int RequiredCapacity { get; }
    public IPhysicsWorld? QueryWorld { get; }
    public IPhysicsWorld? SourceWorld { get; }
    public IPhysicsQueryLease? Lease { get; }
    public Vector3 Origin { get; }
    public long GeometryGeneration { get; }
    public StaticHandle Target { get; }
    public int LeafId { get; }
    public int FeatureId { get; }
    public CapsuleFeatureKind Kind { get; }
    public Vector3 AxisPoint { get; }
    public Vector3 GeometryPoint { get; }
    public Vector3 SeparationNormal { get; }
    public double SeparationLower { get; }
    public double SeparationUpper { get; }
    public float PositionErrorMetres { get; }
    public float NormalError { get; }

    CapsuleFeatureResult(CapsuleFeatureStatus status, int requiredCapacity)
    {
        this = default;
        Status = status;
        RequiredCapacity = requiredCapacity;
    }

    /// <summary>Creates a refusal without usable geometry. A known capacity requirement is diagnostic
    /// only and may be supplied exclusively for <see cref="CapsuleFeatureStatus.CapacityExceeded"/>.</summary>
    public static CapsuleFeatureResult Refused(CapsuleFeatureStatus status, int requiredCapacity = 0)
    {
        if (status > CapsuleFeatureStatus.CapacityExceeded || status == CapsuleFeatureStatus.Complete)
            throw new ArgumentOutOfRangeException(nameof(status));
        if (requiredCapacity < 0 || (requiredCapacity != 0 && status != CapsuleFeatureStatus.CapacityExceeded))
            throw new ArgumentOutOfRangeException(nameof(requiredCapacity));
        return new(status, requiredCapacity);
    }
}

/// <summary>Optional finite-feature correspondence in the exact selected query view.
/// A backend must establish its geometry and numerical domain independently before returning Complete.</summary>
public interface IPhysicsCapsuleFeatures
{
    /// <summary>Queries one current static in the source scoped by <paramref name="lease"/>.
    /// Refusal or exception leaves <paramref name="faces"/> untouched. A complete result commits all
    /// incident faces together and remains usable only under its original lease and query receiver.</summary>
    CapsuleFeatureResult QueryCapsuleFeature(IPhysicsQueryLease lease, StaticHandle target,
        CapsuleShape capsule, Pose pose, float maximumSeparationMetres, Span<CapsuleIncidentFace> faces,
        QueryFilter filter = default);

    /// <summary>Rejects another receiver or lease, an expired interval and a wrong thread before entering
    /// the owner query monitor. A later same-generation lease cannot revive an old result.</summary>
    void AssertFeatureCurrent(in CapsuleFeatureResult result, IPhysicsQueryLease lease);
}
