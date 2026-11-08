using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Navigation;
using KhaozEngine.Physics;

namespace KhaozEngine.Movement;

/// <summary>One profile's leased admission and directed traversal at certified resting feet. The serial build reads
/// its live context frame. A parallel worker reads the build snapshot instead and never touches the context.</summary>
internal sealed partial class ContactNavigationProof : IDisposable
{
    readonly PhysicsNavBake _bake;
    readonly GroundMoveContext? _context;
    readonly PhysicsNavCaptureBasis _basis;
    readonly EffectiveMovementBinding _proof;
    readonly MovementFrameDescriptor _frame;
    readonly MovementQueryScope _scope;
    readonly NavAreaFootprint _footprint;
    readonly ContactNavigationSnapshot? _snapshot;
    readonly Dictionary<Vector3, float> _anchors = [];
    MovementQueryLeaseId? _leaseBasis;

    internal ContactNavigationProof(PhysicsNavBake bake, GroundMoveContext context, EffectiveMovementBinding source,
        NavAreaFootprint footprint, ContactNavigationReusePolicy? reuse = null, float maxDropMetres = 0)
    {
        _bake = bake;
        _context = context;
        _reuse = reuse ?? ContactNavigationReusePolicy.Capture();
        _basis = bake.Basis ?? throw new NavigationBuildException(NavigationBuildRefusal.CaptureLeaseRequired,
            "Contact navigation requires a capture from an IPhysicsQueryLeaseSource.")
            { RequiredCapability = typeof(IPhysicsQueryLeaseSource) };
        _snapshot = _reuse.InitialHolds || maxDropMetres > 0 ? ContactNavigationSnapshot.Capture(bake, context) : null;
        if (_snapshot is null) _basis.AssertCurrent();
        try
        {
            _proof = source.CreateProofBinding();
            _frame = context.NavigationFrame;
            _scope = _proof.CreateOrdinaryScope(_frame);
            _footprint = footprint;
            ContactNavigationEnvelope.Validate(bake, source.Tuning, _scope);
            using var queries = Acquire(new(bake.Options.MinX, 0, bake.Options.MinZ));
            _snapshot?.Authenticate(queries);
            PrepareDrops(source, maxDropMetres);
        }
        catch { _proof?.Dispose(); throw; }
    }

    /// <summary>A parallel worker's proof over <paramref name="snapshot"/>. The build validated the envelope once.
    /// The first lease is authenticated against the snapshot before any work starts.</summary>
    internal ContactNavigationProof(PhysicsNavBake bake, ContactNavigationSnapshot snapshot,
        EffectiveMovementBinding source, NavAreaFootprint footprint, ContactNavigationReusePolicy reuse, float maxDropMetres = 0)
    {
        _bake = bake;
        _basis = snapshot.Basis;
        _reuse = reuse;
        _snapshot = snapshot;
        try
        {
            _proof = source.CreateProofBinding();
            _frame = snapshot.Frame;
            _scope = _proof.CreateOrdinaryScope(_frame);
            _footprint = footprint;
            using var queries = Acquire(new(bake.Options.MinX, 0, bake.Options.MinZ));
            snapshot.Authenticate(queries);
            PrepareDrops(source, maxDropMetres);
        }
        catch { _proof?.Dispose(); throw; }
    }

    /// <summary>The raw feet this proof admitted, each with its resting height.</summary>
    internal IReadOnlyDictionary<Vector3, float> Anchors => _anchors;

    internal bool Admit(Vector3 rawFeet, float headroom, float nextSurface)
    {
        RestingAnchor anchor;
        using (var queries = Acquire(rawFeet))
        {
            MovementAvailability availability = _proof.TryFindRestingAnchor(State(rawFeet), queries, out anchor);
            if (availability != MovementAvailability.Known)
                throw Failure(NavigationBuildRefusal.RestingAnchorUnavailable, rawFeet,
                    $"Resting anchor query returned {availability}.", availability);
            queries.AssertCurrent();
        }
        MoveTuning tuning = _proof.Tuning;
        Vector3 anchorFeet = anchor.Feet + _frame.PhysicsOrigin;
        if (anchor.Support.Status != SupportStatus.Walkable || !Finite(anchorFeet) ||
            (double)anchorFeet.Y < (double)rawFeet.Y - tuning.GroundedEpsilon ||
            (double)anchorFeet.Y > (double)rawFeet.Y + tuning.StepHeight ||
            anchorFeet.Y >= nextSurface ||
            (double)anchorFeet.Y + 2d * tuning.CapsuleHalfHeight > (double)rawFeet.Y + headroom ||
            !_footprint.Accepts(rawFeet) || !_footprint.Accepts(anchorFeet)) return false;

        MovementStepResult placed;
        using (var queries = Acquire(rawFeet))
        {
            placed = _proof.SettlePlacement(State(anchorFeet), queries);
            queries.AssertCurrent();
        }
        if (!AdmitsPlacement(anchorFeet, Feet(placed), placed.Outcome, placed.State.State.Grounded,
            placed.State.Selection?.Support is not null)) return false;
        if (!TryEdgeCore(anchorFeet, anchorFeet, false)) return false;
        _anchors.Add(rawFeet, anchorFeet.Y);
        return true;
    }

    internal static bool AdmitsPlacement(Vector3 anchorFeet, Vector3 settledFeet, MovementStepOutcome outcome,
        bool grounded, bool hasFooting) => outcome == MovementStepOutcome.Advanced && grounded && hasFooting &&
        GroundTraversalProbe.Near(settledFeet, anchorFeet);

    internal NavSpace WithRestingHeights(NavSpace space) => WithRestingHeights(space, _anchors);

    internal static NavSpace WithRestingHeights(NavSpace space, IReadOnlyDictionary<Vector3, float> anchors)
    {
        var layers = new NavGrid[space.Layers.Count];
        for (int layer = 0; layer < layers.Length; layer++)
        {
            NavGrid grid = space.Layers[layer];
            float[] heights = grid.CopySurfaceHeights();
            for (int z = 0; z < grid.Height; z++)
                for (int x = 0; x < grid.Width; x++)
                {
                    if (grid.ClearanceAt(x, z) == 0) continue;
                    int cell = z * grid.Width + x;
                    Vector2 centre = grid.CellCenter(x, z);
                    heights[cell] = anchors[new(centre.X, heights[cell], centre.Y)];
                }
            layers[layer] = grid.WithRestingHeights(heights);
        }
        return new(layers, space.Links);
    }

    internal bool TryEdgeCore(Vector3 fromFeet, Vector3 toFeet, bool edgeProof)
    {
        if (!Finite(fromFeet) || !Finite(toFeet) ||
            !_footprint.Accepts(fromFeet) || !_footprint.Accepts(toFeet)) return false;
        FramedMovementState state = State(fromFeet);
        MovementStepResult result = InitialHold(state, fromFeet, edgeProof, out Vector3 feet, out var remember);
        if (!Accepted(result, feet) || !Near(feet, fromFeet)) return false;
        if (remember is { } key)
            _sourceScope!.Hold.Remember(key, result.State.State, result.State.Frame, feet, result.Outcome);
        if (Near(feet, toFeet)) return true;
        state = result.State;
        for (int slice = 1; slice < _bake.Options.MaxEdgeProbeSteps; slice++)
        {
            Vector2 delta = new(toFeet.X - feet.X, toFeet.Z - feet.Z);
            float distance = delta.Length();
            if (!float.IsFinite(distance)) return false;
            Vector2 direction = distance > 0
                ? delta / distance * MathF.Min(1, distance / _bake.Options.EdgeProbeSeconds) : Vector2.Zero;
            result = Step(state, direction, fromFeet, edgeProof);
            feet = Feet(result);
            if (!Accepted(result, feet)) return false;
            if (Near(feet, toFeet)) return true;
            state = result.State;
        }
        return false;
    }

    MovementStepResult Step(in FramedMovementState state, Vector2 direction, Vector3 cell, bool edgeProof)
    {
        using var queries = Acquire(cell);
        EnsureSourceSession(queries);
        return StepLeased(state, direction, queries, edgeProof);
    }

    MovementStepResult StepLeased(in FramedMovementState state, Vector2 direction, MovementQueryLease queries, bool edgeProof)
    {
        // Each null-hint lease must rebuild its selection before selected-space queries.
        var unselected = new FramedMovementState(state.State, state.Frame, null);
        if (edgeProof) SupportQueryDiagnostics.Record(SupportQueryCounter.EdgeSteps);
        MovementStepResult result = _proof.StepTowards(unselected, direction, false, _bake.Options.EdgeProbeSeconds, queries);
        queries.AssertCurrent();
        return result;
    }

    bool Accepted(in MovementStepResult result, Vector3 feet) => result.Outcome == MovementStepOutcome.Advanced &&
        result.State.State.Grounded && Finite(feet) && _footprint.Accepts(feet);

    FramedMovementState State(Vector3 feet) => new(new MoveState
    {
        Position = feet - _frame.PhysicsOrigin + Vector3.UnitY * _proof.Tuning.CapsuleHalfHeight,
        Grounded = true,
        SpeedScale = 1,
    }, _frame, null);

    Vector3 Feet(in MovementStepResult result) => result.State.State.Position -
        Vector3.UnitY * _proof.Tuning.CapsuleHalfHeight + _frame.PhysicsOrigin;

    MovementQueryLease Acquire(Vector3 cell)
    {
        if (_context is not null)
        {
            _ = _bake.Context;
            AssertFrame(cell, _context.NavigationFrame);
        }
        MovementAvailability availability = _proof.TryAcquire(_scope, out var queries);
        AssertAvailability(cell, availability);
        // EffectiveMovementBinding delegates to MovementEnvironmentContext, which returns Known only after
        // constructing its non-null lease. No producer can supply Known with a null lease here.
        if (queries is null) throw new InvalidOperationException(
            $"Contact navigation cell ({CellX(cell)}, {CellZ(cell)}): Movement lease acquisition returned {availability}.");
        try
        {
            queries.AssertCurrent();
            AssertLeaseBasis(cell, queries.Id, queries.Frame);
            _leaseBasis ??= queries.Id;
            return queries;
        }
        catch { queries.Dispose(); throw; }
    }

    void AssertFrame(Vector3 cell, MovementFrameDescriptor actual)
    {
        if (actual != _frame) throw Failure(NavigationBuildRefusal.ProofFrameChanged, cell,
            "The navigation frame changed.", actualFrame: actual);
    }

    void AssertAvailability(Vector3 cell, MovementAvailability availability)
    {
        if (availability != MovementAvailability.Known)
            throw Failure(NavigationBuildRefusal.MovementLeaseUnavailable, cell,
                $"Movement lease acquisition returned {availability}.", availability);
    }

    void AssertLeaseBasis(Vector3 cell, MovementQueryLeaseId id, MovementFrameDescriptor frame)
    {
        NavigationBuildRefusal? reason = !ReferenceEquals(id.SourceWorld, _basis.Source)
            ? NavigationBuildRefusal.MovementLeaseSourceMismatch
            : id.GeometryGeneration != _basis.GeometryGeneration ? NavigationBuildRefusal.MovementLeaseGenerationMismatch
            : frame.PhysicsOrigin != _basis.Origin ? NavigationBuildRefusal.MovementLeaseOriginMismatch
            : frame != _frame ? NavigationBuildRefusal.MovementLeaseFrameMismatch
            : _leaseBasis is { } prior && id != prior ? NavigationBuildRefusal.MovementLeaseIdentityChanged : null;
        if (reason is { } refused) throw Failure(refused, cell,
            "The movement lease differs from the capture or proof basis.", actualFrame: frame, actualLease: id);
    }

    // Value-only fault seams exercise the same guards without modifying the proof's retained basis.
    internal void InjectChangedProofFrameForTests(Vector3 cell, MovementFrameDescriptor frame) => AssertFrame(cell, frame);
    internal void InjectUnavailableLeaseForTests(Vector3 cell, MovementAvailability availability) => AssertAvailability(cell, availability);
    internal void InjectMismatchedLeaseForTests(Vector3 cell, MovementQueryLeaseId id, MovementFrameDescriptor frame) =>
        AssertLeaseBasis(cell, id, frame);

    int CellX(Vector3 feet) => (int)MathF.Floor((feet.X - _bake.Options.MinX) / _bake.Options.CellSize);
    int CellZ(Vector3 feet) => (int)MathF.Floor((feet.Z - _bake.Options.MinZ) / _bake.Options.CellSize);

    NavigationBuildException Failure(NavigationBuildRefusal reason, Vector3 feet, string message,
        MovementAvailability? availability = null, MovementFrameDescriptor? actualFrame = null, MovementQueryLeaseId? actualLease = null) =>
        new(reason, $"Contact navigation cell ({CellX(feet)}, {CellZ(feet)}): {message}")
        {
            CellX = CellX(feet), CellZ = CellZ(feet), Availability = availability,
            ExpectedFrame = reason is NavigationBuildRefusal.ProofFrameChanged or NavigationBuildRefusal.MovementLeaseFrameMismatch ? _frame : null,
            ActualFrame = reason is NavigationBuildRefusal.ProofFrameChanged or NavigationBuildRefusal.MovementLeaseFrameMismatch ? actualFrame : null,
            ExpectedGeneration = reason == NavigationBuildRefusal.MovementLeaseGenerationMismatch ? _basis.GeometryGeneration : null,
            ActualGeneration = reason == NavigationBuildRefusal.MovementLeaseGenerationMismatch ? actualLease?.GeometryGeneration : null,
            ExpectedOrigin = reason == NavigationBuildRefusal.MovementLeaseOriginMismatch ? _basis.Origin : null,
            ActualOrigin = reason == NavigationBuildRefusal.MovementLeaseOriginMismatch ? actualFrame?.PhysicsOrigin : null,
            ExpectedLease = reason == NavigationBuildRefusal.MovementLeaseIdentityChanged ? _leaseBasis : null,
            ActualLease = reason == NavigationBuildRefusal.MovementLeaseIdentityChanged ? actualLease : null,
        };

    static bool Near(Vector3 a, Vector3 b) => GroundTraversalProbe.Near(a, b);

    static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    public void Dispose()
    {
        try { _sourceScope?.Dispose(); }
        finally
        {
            try { _drop?.Dispose(); }
            finally { _proof.Dispose(); }
        }
    }
}
