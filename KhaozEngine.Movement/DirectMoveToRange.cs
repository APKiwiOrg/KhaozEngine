using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using static KhaozEngine.Movement.RangeApproachCore;

namespace KhaozEngine.Movement;

/// <summary>Per-body ground approach to observed exact shape range without a planner. Call Tick exactly once per
/// simulation tick, since the progress windows count ticks. Blocked stays latched until InRange, Reset, or a change
/// of target shape, range or capsule geometry. Call Reset for target replacement, teleport or manual
/// cancellation.</summary>
public sealed partial class DirectMoveToRange
{
    private static readonly StepAdmission Admits = AllowsStep;
    private readonly DirectApproachOptions _options;
    private readonly ProgressRing _progress;
    private RangeShapeKey _shape;
    private bool _keyed;
    private bool _targetMoves;
    private bool _blocked;

    public DirectMoveToRange(DirectApproachOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _progress = new ProgressRing(Math.Max(options.StallWindowTicks, options.ApproachWindowTicks) + 1);
    }

    /// <summary>Returns bounded requested input without writing the supplied state or stepping the world.
    /// Suspended takes precedence over InRange, and neither suspension nor a zero travel bound counts toward a
    /// window. A refused ground preflight requests zero input and still counts. Pass targetMoves true for body
    /// targets, which disables the approach window. Range uses no tolerance.</summary>
    public RangeSteering Tick(in MoveState body, in MoveTuning tuning, in ReachTarget target, float range,
        bool run, bool targetMoves, float dt, GroundMoveContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.ValidateTuning(tuning);
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
            command = AllowsStep(body, predicted, tuning)
                ? StopAtRange(body, tuning, target, range, run, dt, context, command, predicted, Admits)
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

    private static bool AllowsStep(in MoveState body, in MoveState predicted, in MoveTuning tuning)
        => predicted.Grounded && !predicted.Swimming && MovementBody.IsFinite(predicted.Position);
}
