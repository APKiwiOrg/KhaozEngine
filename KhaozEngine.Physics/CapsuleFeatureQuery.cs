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
/// The normal is geometric, not a smoothed collision-manifold normal. In backend-published output,
/// incidence describes this face's participation in the shared classified witness.</summary>
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

    /// <summary>Publishes structurally valid witness data under its original live interval. This does
    /// not authenticate a backend, prove face completeness or enforce a backend's numerical ceilings.
    /// The provider must establish those facts and commit the validated faces to the caller together.</summary>
    public static CapsuleFeatureResult Completed(IPhysicsWorld queryWorld, IPhysicsQueryLease lease,
        StaticHandle target, int leafId, int featureId, CapsuleFeatureKind kind, Vector3 axisPoint,
        Vector3 geometryPoint, Vector3 separationNormal, double separationLower, double separationUpper,
        float positionErrorMetres, float normalError, ReadOnlySpan<CapsuleIncidentFace> faces) =>
        new(queryWorld, lease, target, leafId, featureId, kind, axisPoint, geometryPoint, separationNormal,
            separationLower, separationUpper, positionErrorMetres, normalError, faces);

    CapsuleFeatureResult(IPhysicsWorld queryWorld, IPhysicsQueryLease lease, StaticHandle target,
        int leafId, int featureId, CapsuleFeatureKind kind, Vector3 axisPoint, Vector3 geometryPoint,
        Vector3 separationNormal, double separationLower, double separationUpper, float positionErrorMetres,
        float normalError, ReadOnlySpan<CapsuleIncidentFace> faces)
    {
        ArgumentNullException.ThrowIfNull(queryWorld);
        ArgumentNullException.ThrowIfNull(lease);
        lease.AssertCurrent();
        if (target.Value < 0 || leafId < 0 || featureId < 0 || !ValidKind(kind))
            throw new ArgumentException("Feature identities and kind must be valid.");
        if (!Finite(axisPoint) || !Finite(geometryPoint) || !Finite(separationNormal) || separationNormal == Vector3.Zero)
            throw new ArgumentException("Feature witness points and a nonzero normal must be finite.");
        if (!double.IsFinite(separationLower) || !double.IsFinite(separationUpper) || separationLower > separationUpper)
            throw new ArgumentException("Feature separation bounds must be finite and ordered.");
        if (!ValidError(positionErrorMetres) || !ValidError(normalError))
            throw new ArgumentException("Feature error bounds must be finite and nonnegative.");
        if (faces.Length is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(faces), "A complete feature has one to 256 incident faces.");
        for (int i = 0; i < faces.Length; i++)
        {
            CapsuleIncidentFace face = faces[i];
            if (face.FaceId < 0 || !Finite(face.Normal) || face.Normal == Vector3.Zero ||
                !ValidError(face.NormalError) || !ValidKind(face.Incidence))
                throw new ArgumentException("Incident face fields must be valid.", nameof(faces));
            for (int previous = 0; previous < i; previous++)
                if (faces[previous].FaceId == face.FaceId)
                    throw new ArgumentException("Incident face identities must be distinct.", nameof(faces));
        }
        IPhysicsWorld source = lease.SourceWorld;
        Vector3 origin = lease.Origin;
        long generation = lease.GeometryGeneration;
        if (source is null || !Finite(origin) || generation < 0)
            throw new ArgumentException("Lease metadata must be valid.", nameof(lease));
        this = default;
        Status = CapsuleFeatureStatus.Complete;
        Written = faces.Length;
        QueryWorld = queryWorld;
        SourceWorld = source;
        Lease = lease;
        Origin = origin;
        GeometryGeneration = generation;
        Target = target;
        LeafId = leafId;
        FeatureId = featureId;
        Kind = kind;
        AxisPoint = axisPoint;
        GeometryPoint = geometryPoint;
        SeparationNormal = separationNormal;
        SeparationLower = separationLower;
        SeparationUpper = separationUpper;
        PositionErrorMetres = positionErrorMetres;
        NormalError = normalError;
    }

    static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    static bool ValidError(float value) => float.IsFinite(value) && value >= 0;
    static bool ValidKind(CapsuleFeatureKind value) => value is > CapsuleFeatureKind.None and <= CapsuleFeatureKind.Vertex;
}

/// <summary>How a support neighborhood element describes its surface.</summary>
public enum SupportElementKind : byte { Polygon, Tangent }

/// <summary>One front-facing surface element whose separation from the probe may lie within the band.
/// <c>ElementId</c> is stable per static: polyhedron leaf * 256 + face, mesh triangle index, or
/// tangent leaf * 4 + part (0 sphere, capsule or cylinder side, 1 cylinder top cap, 2 cylinder bottom cap).
/// The normal is the element's outward geometric normal. The witness is the element's closest point to the
/// probe segment, enclosed by <c>PositionErrorMetres</c>.</summary>
public readonly record struct SupportElement(StaticHandle Static, SupportElementKind Kind, int ElementId,
    Vector3 Normal, float NormalError, Vector3 Witness, float PositionErrorMetres,
    double SeparationLower, double SeparationUpper);

/// <summary>Immutable support neighborhood query data. A refusal has no lease and no written prefix. A complete
/// result names how many elements were committed and the row stride of the join matrix committed with them.
/// Joins form a symmetric bit matrix over the published elements: row <c>i</c> starts at word
/// <c>i * JoinWordsPerRow</c> and bit <c>j</c> of that row (word <c>j / 64</c>, bit <c>j % 64</c>) is set when
/// elements <c>i</c> and <c>j</c> are joined. Two polygon elements are joined when they meet within the band and
/// each lies on or below the other's plane within the band.</summary>
public readonly struct SupportNeighborhoodResult
{
    /// <summary>The backend cap on published elements per query.</summary>
    public const int MaximumElements = 256;

    public CapsuleFeatureStatus Status { get; }
    public int Elements { get; }
    public int RequiredElements { get; }
    public int JoinWordsPerRow { get; }
    public IPhysicsWorld? QueryWorld { get; }
    public IPhysicsWorld? SourceWorld { get; }
    public IPhysicsQueryLease? Lease { get; }
    public Vector3 Origin { get; }
    public long GeometryGeneration { get; }

    SupportNeighborhoodResult(CapsuleFeatureStatus status, int requiredElements)
    {
        this = default;
        Status = status;
        RequiredElements = requiredElements;
    }

    /// <summary>The 64 bit words one join matrix row needs for <paramref name="elements"/> elements.</summary>
    public static int JoinWordsFor(int elements) => elements < 0
        ? throw new ArgumentOutOfRangeException(nameof(elements)) : (elements + 63) / 64;

    /// <summary>Creates a refusal without usable elements. A known element requirement is diagnostic only and
    /// may be supplied exclusively for <see cref="CapsuleFeatureStatus.CapacityExceeded"/>. The join span it
    /// needs is <c>requiredElements * JoinWordsFor(requiredElements)</c> words.</summary>
    public static SupportNeighborhoodResult Refused(CapsuleFeatureStatus status, int requiredElements = 0)
    {
        if (status > CapsuleFeatureStatus.CapacityExceeded || status == CapsuleFeatureStatus.Complete)
            throw new ArgumentOutOfRangeException(nameof(status));
        if (requiredElements < 0 || (requiredElements != 0 && status != CapsuleFeatureStatus.CapacityExceeded))
            throw new ArgumentOutOfRangeException(nameof(requiredElements));
        return new(status, requiredElements);
    }

    /// <summary>Publishes the element count and join stride under their original live interval. This does not
    /// authenticate a backend or prove completeness. The provider establishes those facts and commits the
    /// elements and the join matrix to the caller together.</summary>
    public static SupportNeighborhoodResult Completed(IPhysicsWorld queryWorld, IPhysicsQueryLease lease,
        int elements) => new(queryWorld, lease, elements);

    SupportNeighborhoodResult(IPhysicsWorld queryWorld, IPhysicsQueryLease lease, int elements)
    {
        ArgumentNullException.ThrowIfNull(queryWorld);
        ArgumentNullException.ThrowIfNull(lease);
        lease.AssertCurrent();
        if (elements is < 0 or > MaximumElements)
            throw new ArgumentOutOfRangeException(nameof(elements));
        IPhysicsWorld source = lease.SourceWorld;
        Vector3 origin = lease.Origin;
        long generation = lease.GeometryGeneration;
        if (source is null || !float.IsFinite(origin.X) || !float.IsFinite(origin.Y) || !float.IsFinite(origin.Z) ||
            generation < 0)
            throw new ArgumentException("Lease metadata must be valid.", nameof(lease));
        this = default;
        Status = CapsuleFeatureStatus.Complete;
        Elements = elements;
        JoinWordsPerRow = JoinWordsFor(elements);
        QueryWorld = queryWorld;
        SourceWorld = source;
        Lease = lease;
        Origin = origin;
        GeometryGeneration = generation;
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

    /// <summary>Publishes every front-facing element of every selected static whose separation from the probe
    /// may be at most <paramref name="bandMetres"/>, ordered by static handle then element id, and the symmetric
    /// join matrix over them. <paramref name="joins"/> must hold <c>elements.Length * JoinWordsFor(elements.Length)</c>
    /// words. Membership is decided by the separation's lower bound, so a tie never refuses. CapacityExceeded is
    /// returned only when the element span or the join matrix for the published element count does not fit.
    /// Refusal or exception leaves both spans untouched. A complete result commits elements and joins together
    /// and remains usable only under its original lease and query receiver.</summary>
    SupportNeighborhoodResult QuerySupportNeighborhood(IPhysicsQueryLease lease, CapsuleShape probe, Pose pose,
        float bandMetres, Span<SupportElement> elements, Span<ulong> joins, QueryFilter filter = default);

    /// <summary>Rejects another receiver or lease, an expired interval and a wrong thread, as
    /// <see cref="AssertFeatureCurrent"/> does for feature results.</summary>
    void AssertNeighborhoodCurrent(in SupportNeighborhoodResult result, IPhysicsQueryLease lease);
}
