using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion.Contacts;

/// <summary>Borrows one composed interval. Support stays geometric. Shell and medium queries see the actual
/// body, with the fixed vertical placement skin included before scope and clearance checks.</summary>
internal sealed partial class LeasedContactQueryContext
{
    internal const float PlacementSkin = 0.001f;
    MovementQueryLease _lease = null!;
    internal ContactCapsuleShapes Shapes => _lease.Workspace.Shapes;
    MoveTuning _tuning;
    MovementSelection _selection;
    MovementTransitionContext _transition;
    ContactComposition _composition;

    internal LeasedContactQueryContext(MovementQueryLease lease, MoveTuning tuning, MovementSelection selection,
        MovementTransitionContext transition, float placementOffset = PlacementSkin,
        WaterTraversalMode waterMode = WaterTraversalMode.DryOnly, MovementBoundary? boundary = null)
        => Initialize(lease, tuning, selection, transition, placementOffset, waterMode, boundary);

    internal LeasedContactQueryContext(MovementQueryLease lease, MoveTuning tuning, MovementSelection selection,
        MovementTransitionContext transition, in ContactComposition composition,
        WaterTraversalMode waterMode = WaterTraversalMode.DryOnly, MovementBoundary? boundary = null)
        => Initialize(lease, tuning, selection, transition, composition, waterMode, boundary);

    internal LeasedContactQueryContext() { }

    internal void Reset()
    {
        _lease = null!;
        _tuning = default;
        _selection = default;
        _transition = default;
        _composition = default;
        _waterMode = default;
        _boundary = null;
        Outcome = MovementStepOutcome.Advanced;
        RecoveryDenied = false;
    }

    internal void Initialize(MovementQueryLease lease, MoveTuning tuning, MovementSelection selection,
        MovementTransitionContext transition, float placementOffset = PlacementSkin,
        WaterTraversalMode waterMode = WaterTraversalMode.DryOnly, MovementBoundary? boundary = null)
    {
        Reset();
        ArgumentNullException.ThrowIfNull(lease);
        Initialize(lease, tuning, selection, transition,
            new ContactComposition(placementOffset, new(tuning.FootRadiusFraction)), waterMode, boundary);
    }

    internal void Initialize(MovementQueryLease lease, MoveTuning tuning, MovementSelection selection,
        MovementTransitionContext transition, in ContactComposition composition,
        WaterTraversalMode waterMode = WaterTraversalMode.DryOnly, MovementBoundary? boundary = null)
    {
        Reset();
        ArgumentNullException.ThrowIfNull(lease);
        lease.AssertCurrent();
        if (!selection.IsValid || selection.Identity != lease.Identity ||
            selection.Space.WorldId != lease.Witness.Scope.WorldId || !transition.IsValid ||
            transition.OriginSpace.WorldId != selection.Space.WorldId)
            throw new ContactQueryFailure(MovementAvailability.Invalid);
        (_lease, _tuning, _selection, _transition, _composition) = (lease, tuning, selection, transition, composition);
        (_waterMode, _boundary) = (waterMode, boundary);
    }

    internal bool HasShell => true;
    internal Vector3 KernelFeet(Vector3 centre, in MoveTuning tuning) =>
        new(centre.X, _composition.PlacementOffset == 0 ? centre.Y - tuning.CapsuleHalfHeight :
            Mapped((double)centre.Y - tuning.CapsuleHalfHeight - _composition.PlacementOffset), centre.Z);
    internal Vector3 BodyCentre(Vector3 feet) =>
        new(feet.X, SupportedCapsulePose.Centre(feet.Y, _tuning.CapsuleHalfHeight, _composition.PlacementOffset), feet.Z);
    internal float HeightAt(Vector3 feet) => Analytic(feet)?.Height ?? float.NaN;
    internal Vector3? NormalAt(Vector3 feet) => Analytic(feet)?.Normal;

    MovementAnalyticGround? Analytic(Vector3 feet)
    {
        var query = new FootSupportQuery(new(feet.X, feet.Z), feet.Y,
            _composition.Foot.FootRadiusFraction * _tuning.CapsuleRadius,
            _tuning.StepHeight, _tuning.StepHeight, MathF.Cos(_tuning.MaxSlopeRadians));
        MovementSupportRequest request = Request(query);
        Require(_lease.CheckContactSupportQuery(request, query));
        Require(_lease.SampleAnalyticGround(request, out var ground));
        return ground;
    }

    internal SupportSample FindSupport(in FootSupportQuery query) => FindSupport(query, out _);
    internal SupportSample FindSupport(in FootSupportQuery query, out MovementContactSupportBinding? binding)
    {
        using var phase = ContactAllocationDiagnostics.Begin(ContactAllocationPhase.SupportRead);
        Require(_lease.FindContactSupport(Request(query), query, out SupportSample support, out binding));
        return support;
    }

    internal MovementContactSupportBinding BindSupport(Vector3 feet, in SupportSample support, float radius, float gate)
    {
        var query = new FootSupportQuery(new(feet.X, feet.Z), feet.Y, radius,
            _tuning.StepHeight, _tuning.StepHeight, MathF.Cos(gate));
        Require(_lease.BindContactSupportAtKernel(Request(query), support, feet.Y, out var binding));
        return binding;
    }

    internal bool SweepShell(CapsuleShape shape, Vector3 centre, Vector3 direction, float distance,
        out SweepHit hit, QueryFilter filter = default)
    {
        using var phase = ContactAllocationDiagnostics.Begin(ContactAllocationPhase.ShellSweep);
        Require(_lease.SweepContactShell(ShellBody(shape, centre), shape, direction, distance, filter, out bool blocked, out hit));
        return blocked;
    }

    internal bool PenetrationShell(CapsuleShape shape, Vector3 centre, out Vector3 mtv)
    {
        using var phase = ContactAllocationDiagnostics.Begin(ContactAllocationPhase.ShellSweep);
        Require(_lease.PenetrateContactShell(ShellBody(shape, centre), shape, out bool overlapping, out mtv));
        return overlapping;
    }

    internal CapsuleContactResult QueryShellContacts(CapsuleShape shape, Vector3 centre,
        Span<CapsuleContact> contacts)
    {
        using var phase = ContactAllocationDiagnostics.Begin(ContactAllocationPhase.ShellSweep);
        Require(_lease.QuerySolidContactsFiltered(ShellBody(shape, centre), 0, contacts, out var result, QueryFilter.StaticsOnly));
        return result;
    }

    internal MovementWaterPoint? SampleMedium(Vector3 feet)
    {
        // A centre point alone does not authorize a full-body query at a scope edge.
        Require(_lease.CheckContactBodyPath(Body(feet), Vector3.Zero));
        MovementWaterPoint point = _lease.SampleCentreWater(Body(feet));
        Require(point.Availability);
        return point;
    }

    internal MovementCoverageResult TraceMedium(Vector3 feet, Vector3 delta,
        Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts)
    {
        MovementCoverageResult result = _lease.TraceWater(new(Body(feet), delta), spans, contacts);
        Require(result.Availability);
        return result;
    }

    MovementSupportRequest Request(in FootSupportQuery query) =>
        new(Body(new(query.Axis.X, query.FeetY, query.Axis.Y)), query.ReachUp, query.ReachDown,
            MathF.Acos(query.CosMaxSlope), _transition);
    MovementBodyQuery Body(Vector3 feet) => new(BodyCentre(feet), _tuning.CapsuleRadius,
        _tuning.CapsuleHalfHeight, _selection.Space, _selection.Support);
    MovementBodyQuery ShellBody(CapsuleShape shape, Vector3 centre)
    {
        var body = new MovementBodyQuery(new(centre.X, _composition.PlacementOffset == 0 ? centre.Y :
            Mapped(_composition.RestingFeet((double)centre.Y)), centre.Z),
            shape.Radius, GroundPlacement.NotBelow((double)shape.Radius + (double)shape.Length / 2),
            _selection.Space, _selection.Support);
        // The certificate encloses the original cylinder, including the body-to-shape float conversion.
        // The raw shared primitive still receives its original immutable CapsuleShape.
        if (!MovementCapsuleRounding.TryEnclose(body, Vector3.Zero, out var enclosure, out _, out _))
            throw new ContactQueryFailure(MovementAvailability.Unresolved);
        return enclosure;
    }

    static float Mapped(double value)
    {
        float rounded = (float)value;
        // The lease compares exact double bounds expanded by the coverage skin. Refuse a mapping whose
        // rounding alone exhausts that enclosure rather than certifying an inward-rounded physical pose.
        if (!float.IsFinite(rounded) || Math.Abs(value - rounded) > MovementQueryLease.CoverageSkinMetres)
            throw new ContactQueryFailure(MovementAvailability.Unresolved);
        return rounded;
    }

    static void Require(MovementAvailability availability)
    {
        if (availability != MovementAvailability.Known) throw new ContactQueryFailure(availability);
    }
}
