using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion.Contacts;

/// <summary>Tick-local, non-owning support, shell and physical-path queries. The dry adapter is a value so warm
/// query passes allocate nothing. Kernel feet remain geometric, and only the leased adapter maps the body.</summary>
internal readonly struct ContactQueryContext
{
    readonly DryContactQueryContext _dry;
    readonly LeasedContactQueryContext? _leased;
    ContactQueryContext(DryContactQueryContext dry) => _dry = dry;
    ContactQueryContext(LeasedContactQueryContext leased) => _leased = leased;
    public static implicit operator ContactQueryContext(DryContactQueryContext dry) => new(dry);
    public static implicit operator ContactQueryContext(LeasedContactQueryContext leased) => new(leased);

    internal ContactCapsuleShapes Shapes => _leased?.Shapes ?? ContactCapsuleShapes.ForThread;
    internal bool HasShell => _leased is not null || _dry.HasShell;
    internal bool HasMediumProof => _leased is not null;
    internal Vector3 KernelFeet(Vector3 centre, in MoveTuning tuning) => _leased is { } leased
        ? leased.KernelFeet(centre, tuning) : _dry.KernelFeet(centre, tuning);
    internal Vector3 BodyCentre(Vector3 feet) => _leased is { } leased ? leased.BodyCentre(feet) : _dry.BodyCentre(feet);
    internal Vector3 SupportedBodyCentre(Vector3 feet, in SupportSample support) => _leased is { } leased
        ? leased.SupportedBodyCentre(feet, support) : _dry.SupportedBodyCentre(feet);
    internal void CheckDryMedium(Vector3 feet)
    {
        if (_leased is null) _dry.CheckMedium(feet);
    }
    internal float HeightAt(Vector3 feet) => _leased is { } leased ? leased.HeightAt(feet) : _dry.HeightAt(feet);
    internal bool TryHeightAt(Vector3 feet, out float height)
    {
        if (_leased is null) return _dry.TryHeightAt(feet, out height);
        height = _leased.HeightAt(feet);
        return !float.IsNaN(height);
    }
    internal Vector3? NormalAt(Vector3 feet) => _leased is { } leased ? leased.NormalAt(feet) : _dry.NormalAt(feet);
    internal SupportSample FindSupport(in FootSupportQuery query) => FindSupport(query, out _);
    internal SupportSample FindSupport(in FootSupportQuery query, out MovementContactSupportBinding? binding) =>
        _leased is { } leased ? leased.FindSupport(query, out binding) : _dry.FindSupport(query, out binding);
    internal MovementContactSupportBinding? BindSupport(Vector3 feet, in SupportSample support, float radius, float gate) =>
        _leased?.BindSupport(feet, support, radius, gate);
    internal bool SweepShell(CapsuleShape shape, Vector3 centre, Vector3 direction, float distance,
        out SweepHit hit, QueryFilter filter = default) => _leased is { } leased
        ? leased.SweepShell(shape, centre, direction, distance, out hit, filter)
        : _dry.SweepShell(shape, centre, direction, distance, out hit, filter);
    internal bool PenetrationShell(CapsuleShape shape, Vector3 centre, out Vector3 mtv) => _leased is { } leased
        ? leased.PenetrationShell(shape, centre, out mtv) : _dry.PenetrationShell(shape, centre, out mtv);
    internal CapsuleContactResult QueryShellContacts(CapsuleShape shape, Vector3 centre, Span<CapsuleContact> contacts) =>
        _leased is { } leased ? leased.QueryShellContacts(shape, centre, contacts) : _dry.QueryShellContacts(shape, centre, contacts);
    internal bool AcceptSegment(Vector3 from, Vector3 to) => _leased?.AcceptSegment(from, to) ?? true;
    internal bool ConstrainGroundDescent(Vector3 from, Vector3 to, Vector3 supportNormal,
        float footRadius, float cosMaxSlope, out Vector2 move)
    {
        move = default;
        return _leased?.ConstrainGroundDescent(from, to, supportNormal, footRadius, cosMaxSlope, out move) ?? false;
    }
    internal void ValidatePlacement(Vector3 feet) => _leased?.ValidatePlacement(feet);
    internal void Refuse(ContactQueryFailure failure) => _leased?.Refuse(failure);
    internal void RefuseRecovery() => _leased?.RefuseRecovery();
    internal void AcceptRoute(bool blocked) => _leased?.AcceptRoute(blocked);
    internal void RequireRecoveryAccepted()
    {
        if (_leased?.RecoveryDenied == true)
            throw new ContactQueryFailure(MovementAvailability.Known, MovementStepOutcome.Blocked);
    }
    internal bool ResolveSegment(Vector3 from, Vector3 to, Span<Vector3> path, out int written, out bool blocked,
        Span<Vector3> velocity = default)
    {
        if (_leased is not null) return _leased.ResolveSegment(from, to, path, out written, out blocked, velocity);
        path[0] = to;
        (written, blocked) = (1, false);
        return true;
    }
    internal MovementWaterPoint? SampleMedium(Vector3 feet) => _leased?.SampleMedium(feet);
    internal MovementCoverageResult TraceMedium(Vector3 feet, Vector3 delta,
        Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts) => _leased is { } leased
        ? leased.TraceMedium(feet, delta, spans, contacts)
        : throw new NotSupportedException("Dry contact queries have no medium producer.");
}

/// <summary>Ordinary dry delegates and primitives, forwarded without changing arithmetic or selection.</summary>
internal readonly struct DryContactQueryContext(Func<float, float, float>? height,
    Func<float, float, Vector3>? normal, IPhysicsWorld? world, IPhysicsQueryLease? lease,
    MoveTuning tuning, Func<float, float, float, MovementMedium>? medium = null, SupportRequestMemo? memo = null)
{
    readonly float _halfHeight = tuning.CapsuleHalfHeight;
    internal bool HasShell => world is not null;
    internal Vector3 KernelFeet(Vector3 centre, in MoveTuning knobs) =>
        new(centre.X, centre.Y - knobs.CapsuleHalfHeight, centre.Z);
    internal Vector3 BodyCentre(Vector3 feet) => new(feet.X, feet.Y + _halfHeight, feet.Z);
    internal Vector3 SupportedBodyCentre(Vector3 feet) =>
        new(feet.X, SupportedCapsulePose.Centre(feet.Y, _halfHeight), feet.Z);
    internal void CheckMedium(Vector3 feet)
    {
        if (medium is not null && medium(feet.X, feet.Z, feet.Y).InWater)
            throw new NotSupportedException("Point-sampled water cannot certify contact movement. Use an ordinary movement head with IMovementWaterField.");
    }
    internal float HeightAt(Vector3 feet)
    {
        if (height is null) return float.NaN;
        SupportQueryDiagnostics.Record(SupportQueryCounter.AnalyticSamples);
        return height(feet.X, feet.Z);
    }
    internal bool TryHeightAt(Vector3 feet, out float value)
    {
        value = HeightAt(feet);
        return height is not null;
    }
    internal Vector3? NormalAt(Vector3 feet) => normal?.Invoke(feet.X, feet.Z);
    internal SupportSample FindSupport(in FootSupportQuery query, out MovementContactSupportBinding? binding)
    {
        binding = null;
        return FootSupport.Find(height, normal, world, lease, query, memo);
    }
    internal bool SweepShell(CapsuleShape shape, Vector3 centre, Vector3 direction, float distance,
        out SweepHit hit, QueryFilter filter = default)
    {
        hit = default;
        return world is not null && world.SweepCapsule(shape, Pose.At(centre), direction, distance, out hit, filter);
    }
    internal bool PenetrationShell(CapsuleShape shape, Vector3 centre, out Vector3 mtv)
    {
        mtv = default;
        return world is not null && world.ComputePenetration(shape, Pose.At(centre), out mtv);
    }
    internal CapsuleContactResult QueryShellContacts(CapsuleShape shape, Vector3 centre, Span<CapsuleContact> contacts) =>
        world is IPhysicsCapsuleContacts source
            ? source.QueryCapsuleContacts(shape, Pose.At(centre), 0, contacts, QueryFilter.StaticsOnly)
            : throw new NotSupportedException("Complete shell contacts are unavailable.");
}

/// <summary>A query refusal, distinct from known absent support or a proved obstruction. The borrowed tick's
/// caller maps availability to its whole-tick hold result.</summary>
internal sealed class ContactQueryFailure(MovementAvailability availability,
    MovementStepOutcome? outcome = null) : Exception("Contact query evidence is unavailable.")
{
    internal MovementAvailability Availability { get; } = availability;
    internal MovementStepOutcome Outcome { get; } = outcome ?? (availability == MovementAvailability.Invalid
        ? MovementStepOutcome.EnvironmentInvalid : MovementStepOutcome.EnvironmentUnresolved);
}
