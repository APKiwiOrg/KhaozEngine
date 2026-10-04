using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using static KhaozEngine.Movement.RangeApproachCore;

namespace KhaozEngine.Movement;

/// <summary>Per-body ground approach to observed exact shape range without a planner. Call Tick exactly once per
/// simulation tick, since the progress windows count ticks. Blocked stays latched until InRange, Reset, or a change
/// of target shape, range or capsule geometry. Call Reset for target replacement, teleport or manual
/// cancellation.
/// <para>A step whose preflight leaves the ground is refused unless <see cref="DirectApproachOptions.MaxDropMetres"/>
/// is positive and the predicted fall, settled with zero input, lands grounded and not swimming within that allowance
/// below the current feet, on a floor the next step would not swim from. A grounded step is refused the same way
/// when its landed feet would start swimming. The step-off tick is Following with the
/// admitted command. The airborne ticks after it are Suspended and count toward neither window until it lands.
/// Stop ring bisection still takes only grounded fractions, and keeps the whole admitted step when none reaches
/// range.</para>
/// <para>It never steers a swimmer. A swimming body is not grounded, so it stays Suspended. The driver has no graph
/// guard and the core does not collide a swimmer, so a direct swim approach could pass through props at the
/// waterline. Steer swimmers with <see cref="MoveToRange"/> and <see cref="RouteApproachOptions.SteerWhileSwimming"/>
/// on an aquatic profile.</para></summary>
public sealed partial class DirectMoveToRange
{
    private readonly StepAdmission _admits;
    private readonly DirectApproachOptions _options;
    private readonly ProgressRing _progress;
    private GroundMoveContext? _stepContext;
    private RangeShapeKey _shape;
    private bool _keyed;
    private bool _targetMoves;
    private bool _blocked;

    public DirectMoveToRange(DirectApproachOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _progress = new ProgressRing(Math.Max(options.StallWindowTicks, options.ApproachWindowTicks) + 1);
        _admits = AllowsStep;
    }

    /// <summary>Returns bounded requested input without writing the supplied state or stepping the world.
    /// Suspended takes precedence over InRange, and neither suspension nor a zero travel bound counts toward a
    /// window. A refused ground preflight requests zero input and still counts. A drop within the options' allowance
    /// is admitted rather than refused. Pass targetMoves true for body targets, which disables the approach window.
    /// Range uses no tolerance.</summary>
    public RangeSteering Tick(in MoveState body, in MoveTuning tuning, in ReachTarget target, float range,
        bool run, bool targetMoves, float dt, GroundMoveContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.ValidateTuning(tuning);
        _stepContext = context;
        ValidateBody(body);
        if (!float.IsFinite(dt) || dt <= 0f) throw new ArgumentOutOfRangeException(nameof(dt));
        MovementBody shape = Body(body, tuning);
        bool within = ReachGeometry.Within(shape, target, range);
        Rekey(tuning, target, range, targetMoves);
        if (!body.Grounded || body.Commitment.IsActive) return Hold(RangeMoveStatus.Suspended);
        if (within)
        {
            ClearProgress();
            return Hold(RangeMoveStatus.InRange);
        }
        if (_blocked) return Hold(RangeMoveStatus.Blocked);

        float bound = TravelBound(body, tuning, run, dt, context);
        if (bound == 0f) return Hold(RangeMoveStatus.Following);
        Vector2 feet = new(body.Position.X, body.Position.Z);
        Vector2 command = BoundedDirection(ClosestHorizontal(body.Position, target) - feet, bound);
        if (command != Vector2.Zero)
        {
            MoveState predicted = context.Step(body, command, run, dt, tuning);
            command = AllowsStep(body, predicted, tuning) || AllowsDrop(body, predicted, tuning, dt, context)
                ? StopAtRange(body, tuning, target, range, run, dt, context, command, predicted, _admits)
                : Vector2.Zero;
        }

        _progress.Record(feet, ReachGeometry.Distance(shape, target));
        if (_progress.StallBreached(_options.StallTravelMetres, _options.StallWindowTicks) ||
            (!targetMoves && _progress.ApproachBreached(_options.ApproachGainMetres, _options.ApproachWindowTicks)))
        {
            _blocked = true;
            return Hold(RangeMoveStatus.Blocked);
        }
        return new RangeSteering(command, RangeMoveStatus.Following);
    }

    /// <summary>Clears both windows, the latched block and the shape snapshot.</summary>
    public void Reset()
    {
        ClearProgress();
        _keyed = false;
    }

    private void Rekey(in MoveTuning tuning, in ReachTarget target, float range, bool targetMoves)
    {
        var key = RangeShapeKey.From(tuning, target, range);
        if (_keyed && key != _shape) ClearProgress();
        else if (_keyed && targetMoves != _targetMoves) _progress.ClearApproach();
        _shape = key;
        _targetMoves = targetMoves;
        _keyed = true;
    }

    private void ClearProgress()
    {
        _progress.ClearAll();
        _blocked = false;
    }

    private static RangeSteering Hold(RangeMoveStatus status) => new(Vector2.Zero, status);

    // A step decides swimming from its starting feet, so a grounded step down a sloped shore can land past the
    // swim-enter line. The landed feet make the next step's swim decision, and a step it would swim from is refused.
    private bool AllowsStep(in MoveState body, in MoveState predicted, in MoveTuning tuning)
    {
        if (!predicted.Grounded || predicted.Swimming || !MovementBody.IsFinite(predicted.Position)) return false;
        Func<float, float, float, MovementMedium>? medium = _stepContext?.Medium;
        if (medium is null) return true;
        Vector3 feet = Feet(predicted, tuning);
        return !CharacterMovement.ResolveSwimming(predicted.Swimming, medium(feet.X, feet.Z, feet.Y), feet.Y, tuning);
    }
}
