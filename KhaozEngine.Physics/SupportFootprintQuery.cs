using System;
using System.Numerics;

namespace KhaozEngine.Physics;

/// <summary>An original upright downward sweep path. It carries no prior hit to trust. The complete query
/// authenticates the lease before executing either proposal in its selected view.</summary>
public readonly record struct SupportFootprintProposal(CapsuleShape Probe, Pose Start, float Distance);

/// <summary>A closed XZ disc and inclusive contribution band in the selected world's local frame. Radius is at
/// least 0.01 m, ContactBand is the represented 0.0001f and CosMaxSlope must be positive for finite admission.
/// With T = sqrt(1 - c*c) / c, admission covers the disc expanded by ContactBand from
/// BandLower - max(Radius*T, 0.001) - ContactBand to BandUpper + 0.001 + 2*Radius + 0.01 + ContactBand.
/// Bounds are evaluated outward and also enclose the actual float proposal paths. Admission retains whole
/// surfaces. Contribution uses the original disc and band, with no extension of reach.</summary>
public readonly record struct SupportFootprintRequest(Vector2 Axis, float Radius, double BandLower,
    double BandUpper, float CosMaxSlope, float ContactBand, SupportFootprintProposal AxisProposal,
    SupportFootprintProposal FootProposal);

/// <summary>A local witness role, never an encoded raw element id.</summary>
public enum SupportWitnessRole : byte { Axis, Footprint }

/// <summary>The retained sweep's outcome. InvalidStart has no usable contact normal.</summary>
public enum SupportProposalStatus : byte { Miss, Contact, InvalidStart }

/// <summary>Encloses the probe's lowest point at its contact or invalid start. Miss has no usable height.</summary>
public readonly record struct SupportProposalVerdict(SupportProposalStatus Status, double LowestLower,
    double LowestUpper);

/// <summary>An attained surface point and outward unit normal, with absolute position and componentwise normal
/// errors. Polygon witnesses attain the full closed-disc maximum. Each raw curved part publishes the upper
/// surface above the axis when present, then the highest point in the disc if walkable, otherwise the highest
/// walkable point in the disc when one exists, otherwise the steep maximum. UpwardLower certifies the normal's
/// Y component independently of NormalError, preserving exact slope equality. Slope equality is walkable.</summary>
public readonly record struct SupportFootprintWitness(SupportWitnessRole Role, Vector3 Position,
    float PositionErrorMetres, Vector3 Normal, float NormalError, double UpwardLower);

/// <summary>One admitted raw static/element pair. ElementId keeps the released face or curved part convention.
/// PlanePoint and Normal describe the polygon plane. HasDiscMaximum and its bounds describe the maximum on the
/// whole polygon intersected with the original closed disc, independently of proposal contact. UpwardLower
/// certifies Normal.Y independently of NormalError. A curved member carries its first occupied slot's normal,
/// or the exact zero vector when it has no witnesses. A polygon has
/// one Footprint witness in First, or zero for a context-only member. Curved parts have up to two fixed slots,
/// ordered Axis then Footprint, and never join. ProposalMask bits 0 and 1 name the certified axis and foot
/// contact neighborhoods independently of disc eligibility. Context-only polygons still join, cap and feed
/// proposal guards. Joins use the released whole-polygon meeting and mutual-below predicate over admitted pairs.</summary>
public readonly record struct SupportFootprintMember(StaticHandle Static, SupportElementKind Kind, int ElementId,
    Vector3 PlanePoint, float PlanePointErrorMetres, Vector3 Normal, float NormalError, double UpwardLower, bool HasDiscMaximum,
    double DiscHeightLower, double DiscHeightUpper, byte ProposalMask, byte WitnessCount,
    SupportFootprintWitness First, SupportFootprintWitness Second);

/// <summary>An atomic complete footprint query. Members counts raw pairs, Witnesses counts their occupied
/// slots. Joins is a symmetric bit matrix indexed by raw members, row i at i*JoinWordsPerRow, column j at
/// word j/64 and bit j%64. Default is unresolved. A refusal exposes no prefix, metadata or usable verdict.</summary>
public readonly struct SupportFootprintResult
{
    public const int MaximumElements = 256;
    public const int MaximumWitnesses = 512;

    public CapsuleFeatureStatus Status { get; }
    public int Members { get; }
    public int Witnesses { get; }
    public int RequiredElements { get; }
    public int JoinWordsPerRow { get; }
    public IPhysicsWorld? QueryWorld { get; }
    public IPhysicsWorld? SourceWorld { get; }
    public IPhysicsQueryLease? Lease { get; }
    public Vector3 Origin { get; }
    public long GeometryGeneration { get; }
    public SupportProposalVerdict AxisVerdict { get; }
    public SupportProposalVerdict FootVerdict { get; }

    SupportFootprintResult(CapsuleFeatureStatus status, int requiredElements)
    {
        this = default;
        Status = status;
        RequiredElements = requiredElements;
    }

    /// <summary>The join row stride for a raw member count.</summary>
    public static int JoinWordsFor(int members) => members < 0
        ? throw new ArgumentOutOfRangeException(nameof(members)) : members / 64 + (members % 64 == 0 ? 0 : 1);

    /// <summary>A refusal has no output. RequiredElements is diagnostic only for CapacityExceeded.
    /// At 257 raw pairs the backend refuses with both caller spans untouched.</summary>
    public static SupportFootprintResult Refused(CapsuleFeatureStatus status, int requiredElements = 0)
    {
        if (status > CapsuleFeatureStatus.CapacityExceeded || status == CapsuleFeatureStatus.Complete)
            throw new ArgumentOutOfRangeException(nameof(status));
        if (requiredElements < 0 || (requiredElements != 0 && status != CapsuleFeatureStatus.CapacityExceeded))
            throw new ArgumentOutOfRangeException(nameof(requiredElements));
        return new(status, requiredElements);
    }

    /// <summary>Publishes structurally valid counts, proposal verdicts and captured lease metadata. This factory
    /// does not authenticate the receiver, prove completeness or certify geometry. The backend does that before
    /// atomically committing the member and join spans. Expired or changed leases cannot publish a result.</summary>
    public static SupportFootprintResult Completed(IPhysicsWorld receiver, IPhysicsQueryLease lease, int members,
        int witnesses, SupportProposalVerdict axis, SupportProposalVerdict foot) =>
        new(receiver, lease, members, witnesses, axis, foot);

    SupportFootprintResult(IPhysicsWorld receiver, IPhysicsQueryLease lease, int members, int witnesses,
        SupportProposalVerdict axis, SupportProposalVerdict foot)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        ArgumentNullException.ThrowIfNull(lease);
        lease.AssertCurrent();
        if (members is < 0 or > MaximumElements) throw new ArgumentOutOfRangeException(nameof(members));
        if (witnesses < 0 || witnesses > 2 * members) throw new ArgumentOutOfRangeException(nameof(witnesses));
        Validate(axis);
        Validate(foot);
        IPhysicsWorld source = lease.SourceWorld;
        Vector3 origin = lease.Origin;
        long generation = lease.GeometryGeneration;
        if (source is null || !float.IsFinite(origin.X) || !float.IsFinite(origin.Y) || !float.IsFinite(origin.Z) ||
            generation < 0)
            throw new ArgumentException("Lease metadata must be valid.", nameof(lease));
        this = default;
        Status = CapsuleFeatureStatus.Complete;
        Members = members;
        Witnesses = witnesses;
        JoinWordsPerRow = JoinWordsFor(members);
        QueryWorld = receiver;
        SourceWorld = source;
        Lease = lease;
        Origin = origin;
        GeometryGeneration = generation;
        AxisVerdict = axis;
        FootVerdict = foot;
    }

    static void Validate(SupportProposalVerdict verdict)
    {
        if (verdict.Status > SupportProposalStatus.InvalidStart ||
            (verdict.Status != SupportProposalStatus.Miss && (!double.IsFinite(verdict.LowestLower) ||
             !double.IsFinite(verdict.LowestUpper) || verdict.LowestLower > verdict.LowestUpper)))
            throw new ArgumentException("Proposal verdict must have a valid status and ordered contact bounds.");
    }
}

/// <summary>Optional complete footprint capability, separate from the released neighborhood and feature APIs.
/// Every support, proposal and guard query uses the same authenticated lease and exact selected view. Missing
/// capability, unsupported geometry, unresolved predicates or scope failure cannot fall back to incomplete
/// support. The backend owns geometry, extrema, proposal contacts and joins. Locomotion owns plane evaluation
/// and selection, including unchanged inclusive reach and tie ordering.</summary>
public interface IPhysicsSupportFootprint
{
    /// <summary>Publishes every relevant admitted raw pair ordered by static then element id, with fixed witness roles and
    /// the join matrix, after authenticating the lease and executing both original sweeps. The join span must hold
    /// members.Length*JoinWordsFor(members.Length) words. A short join span throws ArgumentException. Capacity is
    /// 256 relevant raw pairs and at most 512 occupied slots. A proved below-band member can be omitted only when
    /// it cannot contribute, join a band-capable member or establish a required proposal guard.
    /// Every refusal or exception leaves both spans untouched.
    /// No prefix, pagination, extra probe or lower plausible answer is a complete result.</summary>
    SupportFootprintResult QuerySupportFootprint(IPhysicsQueryLease lease, in SupportFootprintRequest request,
        Span<SupportFootprintMember> members, Span<ulong> joins, QueryFilter filter = default);

    /// <summary>Rejects a foreign receiver, source, lease, expired interval, wrong thread or changed origin or
    /// generation before backend work. A later same-generation lease cannot revive an earlier result.</summary>
    void AssertFootprintCurrent(in SupportFootprintResult result, IPhysicsQueryLease lease);
}
