using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Navigation;
using static KhaozEngine.Movement.RangeApproachCore;

namespace KhaozEngine.Movement;

/// <summary>One body's execution of a named baked Drop. The host still advances the real controller.
/// Launch preview proves the actual body and controls, never an alternative edge or a predicted arrival.</summary>
internal sealed class ContactDropFollower(GroundNavigation navigation, NavDropTraversal traversal)
{
    enum Phase { Align, Launch, Falling, Landed }
    Phase _phase;
    NavDropFootprint? _corridor;
    ContactDropExecutionBasis? _execution;
    bool _interrupted;
    float _highest = traversal.StartFeet.Y;

    internal RangeSteering Tick(in MoveState body, in MoveTuning tuning, bool run, float dt,
        GroundMoveContext context, PlayerPathInput? playerInput, out bool complete, out bool replan)
    {
        complete = replan = false;
        Vector3 feet = Feet(body, tuning);
        if (_execution is not null && !_execution.Matches(body, run, dt, context.NavigationFrame)) _interrupted = true;
        if (_interrupted)
        {
            replan = body.Grounded && !body.Swimming;
            return Hold(replan ? RangeMoveStatus.Following : RangeMoveStatus.Suspended);
        }
        if (_phase == Phase.Align)
        {
            if (!body.Grounded || body.Swimming) return Hold(RangeMoveStatus.Suspended);
            if (!GroundTraversalProbe.Near(feet, traversal.StartFeet))
            {
                float bound = TravelBound(body, tuning, run, dt, context);
                if (bound == 0) return Hold(RangeMoveStatus.Following);
                Vector2 input = BoundedDirection(new(traversal.StartFeet.X - feet.X, traversal.StartFeet.Z - feet.Z), bound);
                var aligned = Predict(body, input, run, dt, tuning, context, playerInput: playerInput);
                return Accepted(aligned, tuning, context) && Supported(aligned) &&
                    navigation.AllowsSegment(feet, AbsoluteFeet(aligned, tuning))
                    ? new(input, RangeMoveStatus.Following) : Hold(Status(aligned, RangeMoveStatus.Following));
            }
            _phase = Phase.Launch;
        }

        if (_corridor is null)
        {
            if (!body.Grounded || body.Swimming) return Hold(RangeMoveStatus.Suspended);
            if (body.SpeedScale == 0 || (run ? tuning.RunSpeed : tuning.WalkSpeed) == 0)
                return Hold(RangeMoveStatus.Following);
            var previewBasis = new ContactDropPredictionBasis();
            RangeMoveStatus preview = ContactDropPreview.Prove(navigation, traversal, body, tuning, run, dt, context, previewBasis, playerInput);
            if (preview != RangeMoveStatus.Dropping) return Hold(preview);
            _execution = new(previewBasis, run, dt, context.NavigationFrame);
            _corridor = new(navigation.Footprint, tuning, traversal.StartFeet, traversal.LandingFeet, navigation.MaxDropMetres);
            if (!_corridor.Certify()) return Hold(RangeMoveStatus.Blocked);
        }

        if (!_corridor.Contains(feet) || (double)_highest - feet.Y > navigation.MaxDropMetres)
        {
            replan = body.Grounded && !body.Swimming;
            return Hold(replan ? RangeMoveStatus.Following : RangeMoveStatus.Blocked);
        }
        if (!body.Grounded) _phase = Phase.Falling;
        if (body.Grounded && !body.Swimming)
        {
            if (Target(feet) && GroundTraversalProbe.Near(feet, traversal.LandingFeet))
            {
                // Completion is witnessed from the supplied body and a real supported Hold at that body.
                var held = Predict(body, Vector2.Zero, run, dt, tuning, context, _corridor, playerInput: playerInput);
                if (Accepted(held, tuning, context) && Supported(held) && Target(AbsoluteFeet(held, tuning)) &&
                    GroundTraversalProbe.Near(AbsoluteFeet(held, tuning), traversal.LandingFeet))
                {
                    _phase = Phase.Landed;
                    complete = true;
                    return Hold(RangeMoveStatus.Following);
                }
                return Hold(Status(held, RangeMoveStatus.Blocked));
            }
            if (_phase == Phase.Falling || feet.Y < traversal.StartFeet.Y - tuning.StepHeight)
            {
                // A real unexpected support is the only valid origin for the next route.
                if (!Target(feet))
                {
                    replan = true;
                    return Hold(RangeMoveStatus.Following);
                }
            }
            _highest = MathF.Max(_highest, feet.Y);
        }
        else if (body.Swimming) return Hold(RangeMoveStatus.Suspended);

        var sample = context.SampleWaterForPacing(body, tuning, out float landScale);
        if (sample.Swimming) return Hold(RangeMoveStatus.Suspended);
        Vector2 command = ContactDropControl.Input(body, traversal.LandingFeet, tuning, run, landScale, dt);
        var stepBasis = new ContactDropPredictionBasis();
        var next = Predict(body, command, run, dt, tuning, context, _corridor, stepBasis, playerInput);
        if (next.Outcome == MovementStepOutcome.Advanced && !_execution!.SameEnvironment(stepBasis))
        {
            _interrupted = true;
            replan = body.Grounded && !body.Swimming;
            return Hold(replan ? RangeMoveStatus.Following : RangeMoveStatus.Suspended);
        }
        if (!Accepted(next, tuning, context) || !_corridor.Contains(AbsoluteFeet(next, tuning)) ||
            (double)_highest - AbsoluteFeet(next, tuning).Y > navigation.MaxDropMetres ||
            next.ContactFirstAirLandingFeet is { } first && !Target(first + next.State.Frame.PhysicsOrigin))
            return Hold(Status(next, RangeMoveStatus.Blocked));
        _execution!.Expect(next);
        return new(command, RangeMoveStatus.Dropping);
    }

    bool Target(Vector3 feet) => ContactDropPreview.Target(navigation.Space, traversal, feet);

    internal static MovementStepResult Predict(in MoveState body, Vector2 input, bool run, float dt,
        in MoveTuning tuning, GroundMoveContext context, NavDropFootprint? corridor = null,
        ContactDropPredictionBasis? basis = null, PlayerPathInput? playerInput = null)
    {
        var frame = context.NavigationFrame;
        return context.PredictContactStep(new(body with { Position = body.Position - frame.PhysicsOrigin }, frame, null),
            input, run, dt, tuning, basis ?? new ContactDropPredictionBasis(), corridor, playerInput);
    }

    internal static Vector3 AbsoluteFeet(in MovementStepResult result, in MoveTuning tuning)
        => result.State.State.Position + result.State.Frame.PhysicsOrigin - Vector3.UnitY * tuning.CapsuleHalfHeight;

    internal static bool Accepted(in MovementStepResult result, in MoveTuning tuning, GroundMoveContext context)
    {
        MoveState state = result.State.State;
        return result.Outcome == MovementStepOutcome.Advanced && result.State.Frame == context.NavigationFrame &&
            !state.Swimming && !state.Commitment.IsActive && state.JumpBufferRemaining == 0 &&
            !result.ContactUnwalkableSupport && !result.ContactEnteredSwimming && MovementBody.IsFinite(AbsoluteFeet(result, tuning)) &&
            float.IsFinite(state.VerticalVelocity) && float.IsFinite(state.HorizontalVelocity.X) && float.IsFinite(state.HorizontalVelocity.Y);
    }

    internal static bool Supported(in MovementStepResult result)
        => result.State.State.Grounded && result.ContactWalkableSupport && result.State.Selection?.Support is not null;

    internal static RangeMoveStatus Status(in MovementStepResult result, RangeMoveStatus known)
        => result.Outcome is MovementStepOutcome.EnvironmentInvalid or MovementStepOutcome.EnvironmentUnresolved or MovementStepOutcome.FrameMismatch
            ? RangeMoveStatus.Suspended : known;

    static RangeSteering Hold(RangeMoveStatus status) => new(Vector2.Zero, status);
}
