using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Navigation;
using static KhaozEngine.Movement.ContactDropFollower;
using static KhaozEngine.Movement.RangeApproachCore;

namespace KhaozEngine.Movement;

/// <summary>Executes only the stored terrain policy. Predicted state must be witnessed by the next supplied body.</summary>
internal sealed class ContactTerrainDropFollower(GroundNavigation navigation, NavDropTraversal traversal)
{
    ContactTerrainDropAttempt? _attempt;
    ContactDropExecutionBasis? _execution;
    bool _interrupted;
    internal ContactDropRuntimeRefusal Refusal { get; private set; }
    internal bool Preparing => Refusal == ContactDropRuntimeRefusal.None &&
        (_attempt is null || _attempt.Policy.Phase == ContactTerrainDropPhase.LaunchPrefix);

    internal RangeSteering Tick(in MoveState body, in MoveTuning tuning, bool run, float dt,
        GroundMoveContext context, PlayerPathInput? input, out bool complete, out bool replan)
    {
        complete = replan = false;
        if (Refusal != ContactDropRuntimeRefusal.None) return Blocked();
        try { return TickCore(body, tuning, run, dt, context, input, out complete, out replan); }
        catch (PhysicsNavDescentEvidenceException failure) when (failure.Reason == PhysicsNavDescentEvidenceReason.DependencyMissing)
        {
            _attempt = null;
            _execution = null;
            Refusal = ContactDropRuntimeRefusal.DependencyMissing;
            return Blocked();
        }
    }

    RangeSteering TickCore(in MoveState body, in MoveTuning tuning, bool run, float dt,
        GroundMoveContext context, PlayerPathInput? input, out bool complete, out bool replan)
    {
        complete = replan = false;
        if (_execution is not null && !_execution.Matches(body, run, dt, context.NavigationFrame)) _interrupted = true;
        if (_interrupted)
        {
            replan = body.Grounded && !body.Swimming;
            return Hold(replan ? RangeMoveStatus.Following : RangeMoveStatus.Suspended);
        }
        Vector3 feet = Feet(body, tuning);
        if (_attempt is null)
        {
            if (!body.Grounded || body.Swimming) return Hold(RangeMoveStatus.Suspended);
            if (!GroundTraversalProbe.Near(feet, traversal.StartFeet))
            {
                float bound = TravelBound(body, tuning, run, dt, context);
                if (bound == 0) return Hold(RangeMoveStatus.Following);
                Vector2 direction = BoundedDirection(new(traversal.StartFeet.X - feet.X, traversal.StartFeet.Z - feet.Z), bound);
                var aligned = Predict(body, direction, run, dt, tuning, context, playerInput: input);
                return Accepted(aligned, tuning, context) && Supported(aligned) &&
                    navigation.AllowsSegment(feet, AbsoluteFeet(aligned, tuning))
                    ? new(direction, RangeMoveStatus.Following) : Hold(Status(aligned, RangeMoveStatus.Following));
            }
            if (body.SpeedScale == 0 || (run ? tuning.RunSpeed : tuning.WalkSpeed) == 0) return Hold(RangeMoveStatus.Following);
            var basis = new ContactDropPredictionBasis();
            var preview = ContactDropPreview.Prove(navigation, traversal, body, tuning, run, dt, context, basis, input);
            if (preview != RangeMoveStatus.Dropping) return Hold(preview);
            if (!navigation.Terrain!.TryGet(traversal.Link, out var certificate)) return Hold(RangeMoveStatus.Blocked);
            _attempt = new(certificate!, context.NavigationFrame, tuning, navigation.MaxDropMetres);
            if (!_attempt.Predict(body, Vector2.Zero, run, dt, tuning, context, basis, input,
                    initial: true, hold: true, out var held)) return Hold(Status(held, RangeMoveStatus.Blocked));
            _execution = new(basis, run, dt, context.NavigationFrame);
        }
        if (body.Swimming) return Hold(RangeMoveStatus.Suspended);
        var water = context.SampleWaterForPacing(body, tuning, out float scale);
        if (water.Swimming) return Hold(RangeMoveStatus.Suspended);
        bool hold = _attempt.Policy.Phase == ContactTerrainDropPhase.TerminalHold;
        bool witnessedHold = _attempt.Policy.Complete;
        Vector2 command = hold ? Vector2.Zero : ContactDropControl.Input(body, traversal.LandingFeet, tuning, run, scale, dt);
        var stepBasis = new ContactDropPredictionBasis();
        if (!_attempt.Predict(body, command, run, dt, tuning, context, stepBasis, input,
                initial: false, hold, out var next))
        {
            replan = _attempt.Policy.Refusal == ContactTerrainDropRefusal.UnexpectedSupport && body.Grounded && !body.Swimming;
            return Hold(replan ? RangeMoveStatus.Following : Status(next, RangeMoveStatus.Blocked));
        }
        if (!_execution!.SameEnvironment(stepBasis))
        {
            _interrupted = true;
            replan = body.Grounded && !body.Swimming;
            return Hold(replan ? RangeMoveStatus.Following : RangeMoveStatus.Suspended);
        }
        if (_attempt.Policy.Complete && witnessedHold)
        {
            // The supplied body now authenticates the previously issued terminal Hold, not just its prediction.
            complete = body.Grounded && GroundTraversalProbe.Near(feet, traversal.LandingFeet);
            return Hold(complete ? RangeMoveStatus.Following : RangeMoveStatus.Blocked);
        }
        _execution.Expect(next);
        _attempt.Policy.ReadyToHold(AbsoluteFeet(next, tuning));
        return new(command, RangeMoveStatus.Dropping);
    }

    RangeSteering Blocked() => new(Vector2.Zero, RangeMoveStatus.Blocked) { DropRefusal = Refusal };
    static RangeSteering Hold(RangeMoveStatus status) => new(Vector2.Zero, status);
}
