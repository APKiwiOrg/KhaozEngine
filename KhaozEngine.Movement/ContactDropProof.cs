using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Navigation;
using KhaozEngine.Physics;

namespace KhaozEngine.Movement;

/// <summary>One worker's full-tuning, unpowered fall proof. Holds, launch and flight are real Contact steps.
/// A fresh canonical state and initial Hold belong to each mode attempt. Ground Hold caches are never read.</summary>
internal sealed class ContactDropProof : IDisposable
{
    readonly PhysicsNavBakeOptions _options;
    readonly EffectiveMovementBinding _binding;
    readonly MovementFrameDescriptor _frame;
    readonly Func<Vector3, MovementQueryLease> _acquire;
    readonly NavAreaFootprint _ground;
    readonly float _allowance;
    readonly ContactNavigationBuildHooks? _hooks;
    readonly bool _reuse;

    internal ContactDropProof(PhysicsNavBakeOptions options, EffectiveMovementBinding source,
        MovementFrameDescriptor frame, Func<Vector3, MovementQueryLease> acquire,
        NavAreaFootprint ground, float allowance, bool reuse)
    {
        _options = options;
        _binding = source.CreateFallProofBinding();
        _frame = frame;
        _acquire = acquire;
        _ground = ground;
        _allowance = allowance;
        _hooks = ContactNavigationBuildHooks.Current;
        _reuse = reuse;
    }

    internal bool TryEdge(NavSpace space, NavLink link)
    {
        Vector3 from = Feet(space.Layers[link.FromLayer], link.FromX, link.FromZ);
        Vector3 to = Feet(space.Layers[link.ToLayer], link.ToX, link.ToZ);
        SupportQueryDiagnostics.Record(SupportQueryCounter.DropCandidates);
        _hooks?.DropCandidate?.Invoke(new(link, (double)from.Y - to.Y,
            _binding.Tuning.WalkSpeed > 0, _binding.Tuning.RunSpeed > 0));
        if (link.Kind != NavLinkKind.Drop || (double)from.Y - to.Y <= _binding.Tuning.StepHeight ||
            (double)from.Y - to.Y > _allowance || !_ground.Accepts(from) || !_ground.Accepts(to)) return false;
        var footprint = new NavDropFootprint(_ground, _binding.Tuning, from, to, _allowance);
        if (!footprint.Certify()) return false;
        MovementBoundary boundary = footprint.Compose(_binding.CaptureOrdinaryBoundary(_frame), _frame);
        bool usable = false;
        foreach (bool run in new[] { false, true })
        {
            if ((run ? _binding.Tuning.RunSpeed : _binding.Tuning.WalkSpeed) == 0) continue;
            usable = true;
            SupportQueryDiagnostics.Record(run ? SupportQueryCounter.DropRunProofs : SupportQueryCounter.DropWalkProofs);
            if (!TryMode(space, link, from, to, footprint, boundary, run)) return false;
        }
        return usable;
    }

    bool TryMode(NavSpace space, NavLink link, Vector3 from, Vector3 to, NavDropFootprint footprint,
        MovementBoundary boundary, bool run)
    {
        using var session = new ContactDropSourceSession(_binding, _reuse);
        try { return TryModeCore(space, link, from, to, footprint, boundary, run, session); }
        catch (Exception failure) { session.Failure = failure; throw; }
    }

    bool TryModeCore(NavSpace space, NavLink link, Vector3 from, Vector3 to, NavDropFootprint footprint,
        MovementBoundary boundary, bool run, ContactDropSourceSession session)
    {
        MoveTuning tuning = _binding.Tuning;
        footprint.BeginMode();
        var state = new FramedMovementState(new MoveState
        {
            Position = from - _frame.PhysicsOrigin + Vector3.UnitY * tuning.CapsuleHalfHeight,
            Grounded = true,
            SpeedScale = 1,
        }, _frame, null);
        MovementStepResult result = Step(state, from, to, run, boundary, session, hold: true);
        Vector3 feet = Feet(result);
        if (!Accepted(result, feet, footprint) || !Supported(result) || !GroundTraversalProbe.Near(feet, from)) return false;
        state = result.State;
        bool flew = false, landed = false;
        Vector3 lastLaunch = feet;
        float highestLaunch = feet.Y;
        for (int slice = 1; slice < _options.MaxEdgeProbeSteps; slice++)
        {
            result = Step(state, from, to, run, boundary, session, hold: false);
            feet = Feet(result);
            if (!Accepted(result, feet, footprint) || (double)highestLaunch - feet.Y > _allowance) return false;
            if (result.ContactFirstAirLandingFeet is { } first && !Target(space, link, first + _frame.PhysicsOrigin, to))
                return false;
            bool supported = Supported(result);
            flew |= !result.State.State.Grounded || result.ContactAirLanded;
            if (supported && !flew)
            {
                lastLaunch = feet;
                highestLaunch = MathF.Max(highestLaunch, feet.Y);
            }
            else if (supported)
            {
                if (!Target(space, link, feet, to)) return false;
                landed = true;
            }
            else if (result.State.State.Grounded || landed) return false;
            if ((double)lastLaunch.Y - feet.Y > _allowance) return false;
            if (landed && GroundTraversalProbe.Near(feet, to))
            {
                if (slice + 1 >= _options.MaxEdgeProbeSteps) return false;
                result = Step(result.State, from, to, run, boundary, session, hold: true);
                feet = Feet(result);
                return Accepted(result, feet, footprint) && Supported(result) && Target(space, link, feet, to) &&
                    GroundTraversalProbe.Near(feet, to) && (double)highestLaunch - feet.Y <= _allowance;
            }
            state = result.State;
        }
        return false;
    }

    MovementStepResult Step(in FramedMovementState state, Vector3 source, Vector3 target, bool run,
        MovementBoundary boundary, ContactDropSourceSession session, bool hold)
    {
        using MovementQueryLease queries = _acquire(source);
        session.Bind(queries);
        var unselected = new FramedMovementState(state.State, _frame, null);
        var water = _binding.SampleWaterForPacing(unselected, queries, out bool swimming, out float landScale);
        if (water.Availability != MovementAvailability.Known)
            throw new InvalidOperationException($"Contact Drop pacing returned {water.Availability}.");
        if (swimming) return new(unselected, MovementStepOutcome.PlacementRefused);
        MoveState absolute = state.State with { Position = state.State.Position + _frame.PhysicsOrigin };
        Vector2 input = hold ? Vector2.Zero : ContactDropControl.Input(absolute, target, _binding.Tuning,
            run, landScale, _options.EdgeProbeSeconds);
        SupportQueryDiagnostics.Record(SupportQueryCounter.DropSteps);
        MovementStepResult result = _binding.StepTowards(unselected, input, run, _options.EdgeProbeSeconds, queries, boundary);
        queries.AssertCurrent();
        _hooks?.DropStep?.Invoke(new(source, Feet(result), run, hold, result.Outcome, result.State.State.Grounded,
            result.State.State.CommandedVelocity));
        if (result.Outcome is MovementStepOutcome.EnvironmentInvalid or MovementStepOutcome.EnvironmentUnresolved or MovementStepOutcome.FrameMismatch)
            throw new InvalidOperationException($"Contact Drop step returned {result.Outcome}.");
        return result;
    }

    static bool Target(NavSpace space, NavLink link, Vector3 feet, Vector3 target)
    {
        var cell = space.Layers[link.ToLayer].CellOf(feet.X, feet.Z);
        return cell.X == link.ToX && cell.Z == link.ToZ &&
            GroundTraversalProbe.Near(new(target.X, feet.Y, target.Z), target);
    }

    static bool Supported(in MovementStepResult result) => result.State.State.Grounded && result.ContactWalkableSupport &&
        result.State.Selection?.Support is not null;

    static bool Accepted(in MovementStepResult result, Vector3 feet, NavDropFootprint footprint)
    {
        MoveState body = result.State.State;
        return result.Outcome == MovementStepOutcome.Advanced && footprint.Contains(feet) && !body.Swimming &&
            !result.ContactUnwalkableSupport &&
            !body.Commitment.IsActive && body.JumpBufferRemaining == 0 && float.IsFinite(body.VerticalVelocity) &&
            float.IsFinite(body.HorizontalVelocity.X) && float.IsFinite(body.HorizontalVelocity.Y);
    }

    Vector3 Feet(in MovementStepResult result) => result.State.State.Position + _frame.PhysicsOrigin -
        Vector3.UnitY * _binding.Tuning.CapsuleHalfHeight;

    internal static Vector3 Feet(NavGrid grid, int x, int z)
    {
        Vector2 centre = grid.CellCenter(x, z);
        return new(centre.X, grid.RestingHeightAt(x, z)!.Value, centre.Y);
    }

    public void Dispose() => _binding.Dispose();
}
