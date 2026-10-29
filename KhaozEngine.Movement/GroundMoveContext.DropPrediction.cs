using System;
using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

public sealed partial class GroundMoveContext
{
    /// <summary>Predicts one Contact slice without collapsing refusal into a held MoveState. State and output
    /// positions are physics-frame local. Callers bound total work with ContactDropControl.PreviewSteps.</summary>
    internal MovementStepResult PredictContactStep(in FramedMovementState state, Vector2 direction, bool run,
        float dt, in MoveTuning tuning, ContactDropPredictionBasis basis, NavDropFootprint? corridor = null,
        PlayerPathInput? playerInput = null)
    {
        ArgumentNullException.ThrowIfNull(basis);
        if (_stepping) throw new InvalidOperationException("Ground movement steps cannot overlap or recurse.");
        ValidateTuning(tuning);
        if (ResolvedBinding.Kind != EffectiveMovementBindingKind.OrdinaryContact)
            throw new NotSupportedException("Contact prediction requires OrdinaryContact movement.");
        _origin = Physics!.Origin;
        MovementFrameDescriptor frame = NavigationFrame;
        if (state.Frame != frame) return new(state, MovementStepOutcome.FrameMismatch);
        _stepping = true;
        try
        {
            EnsureOrigin();
            var binding = ResolvedBinding;
            var scope = binding.CreateOrdinaryScope(frame);
            var availability = binding.TryAcquire(scope, out var queries);
            EnsureOrigin();
            if (availability != MovementAvailability.Known || queries is null)
                return new(state, ExplicitCharacterMovement.Outcome(availability));
            using (queries)
            {
                basis.Authenticate(queries);
                MovementBoundary ordinary = binding.CaptureOrdinaryBoundary(frame);
                MovementBoundary boundary = corridor?.Compose(ordinary, frame) ?? ordinary;
                var start = new FramedMovementState(state.State, frame, null);
                MovementStepResult result = playerInput is { } input
                    ? binding.Step(start, input.Command(new(direction, RangeMoveStatus.Dropping), run), dt, queries, boundary)
                    : binding.StepTowards(start, direction, run, dt, queries, boundary);
                queries.AssertCurrent();
                EnsureOrigin();
                if (NavigationFrame != frame) throw new InvalidOperationException("The Contact prediction frame changed.");
                return result;
            }
        }
        finally { _stepping = false; }
    }
}
