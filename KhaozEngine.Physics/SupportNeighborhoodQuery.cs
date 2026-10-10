using System;
using System.Numerics;

namespace KhaozEngine.Physics;

/// <summary>How a support neighborhood element describes its surface.</summary>
public enum SupportElementKind : byte { Polygon, Tangent }

/// <summary>One front-facing surface element whose separation from the probe may lie within the band.
/// <c>ElementId</c> is stable per static: polyhedron leaf * 256 + face, mesh triangle index, or
/// tangent leaf * 256 + part (0 sphere, capsule or cylinder side, 1 cylinder top cap, 2 cylinder bottom cap).
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

/// <summary>Optional support neighborhood capability in the exact selected query view. A backend that offers it
/// publishes every surface element near an upright probe with the joins between them, under the same lease and
/// receiver rules as <see cref="IPhysicsCapsuleFeatures"/>. A backend must establish its geometry and numerical
/// domain independently before returning Complete.</summary>
public interface IPhysicsSupportNeighborhood
{
    /// <summary>Publishes every front-facing element of every selected static whose separation from the probe
    /// may be at most <paramref name="bandMetres"/>, ordered by static handle then element id, and the symmetric
    /// join matrix over them. <paramref name="joins"/> must hold <c>elements.Length * JoinWordsFor(elements.Length)</c>
    /// words, and a shorter join span throws <see cref="ArgumentException"/>. Membership is decided by the separation's
    /// lower bound, so a tie never refuses. CapacityExceeded is returned only when the published element count exceeds
    /// the element span or <see cref="SupportNeighborhoodResult.MaximumElements"/>. Refusal or exception leaves both
    /// spans untouched. A complete result commits elements and joins together
    /// and remains usable only under its original lease and query receiver.</summary>
    SupportNeighborhoodResult QuerySupportNeighborhood(IPhysicsQueryLease lease, CapsuleShape probe, Pose pose,
        float bandMetres, Span<SupportElement> elements, Span<ulong> joins, QueryFilter filter = default);

    /// <summary>Rejects another receiver or lease, an expired interval and a wrong thread, as
    /// <see cref="IPhysicsCapsuleFeatures.AssertFeatureCurrent"/> does for feature results.</summary>
    void AssertNeighborhoodCurrent(in SupportNeighborhoodResult result, IPhysicsQueryLease lease);
}
