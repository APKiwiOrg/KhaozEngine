using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion.Contacts;

/// <summary>Ground core options that are not yet <see cref="MoveTuning"/> fields. The foot disc radius is
/// <see cref="FootRadiusFraction"/> of the capsule radius.</summary>
internal readonly record struct GroundCoreSettings(float FootRadiusFraction = 0.5f)
{
    // A parameterless new on a record struct would skip the default parameter and give zero.
    public GroundCoreSettings() : this(0.5f) { }
}

/// <summary>What supports the body after a grounded tick. <see cref="Held"/> means the support at the start could
/// not be certified, so the body did not move.</summary>
internal enum GroundFooting : byte { Walkable, Steep, None, Held }

/// <summary>One grounded tick. <see cref="Rise"/> is the change in feet height, positive when climbing.
/// <see cref="Achieved"/> is the horizontal move that happened. <see cref="Blocked"/> says a wall, cliff, steep
/// rise or refusal stopped part of the requested move.</summary>
internal readonly record struct GroundStepResult(Vector3 Feet, GroundFooting Footing, SupportSample Support,
    float Rise, Vector2 Achieved, bool Blocked)
{
    /// <summary>The seconds of the tick left after the footing changed to <see cref="GroundFooting.Steep"/> or
    /// <see cref="GroundFooting.None"/>, for the next mode to run. The substep that found the change counts as
    /// spent. Zero when the footing held or the tick ended for another reason.</summary>
    internal float RemainingTime { get; init; }

    internal bool MediumHandoff { get; init; }
    internal bool HorizontalPlanPreserved { get; init; }

    /// <summary>The climb budget left after the tick's paced climbing, in metres.</summary>
    internal double ClimbBudget { get; init; }

    /// <summary>True when the start was already steep or unsupported, so the footing changed before any time was
    /// spent and <see cref="RemainingTime"/> is the whole tick.</summary>
    internal bool ChangedAtStart { get; init; }

    /// <summary>The signed discrete rise applied by accepted ground placements and lag payments.</summary>
    internal float StepRise { get; init; }

    /// <summary>The feet change from accepted placements and lag payments, excluding start recovery.</summary>
    internal float MoveRise { get; init; }

    /// <summary>True when an accepted walking substep seated across a discrete rise.</summary>
    internal bool RiseSeen { get; init; }

    /// <summary>True when an accepted walking substep seated across a discrete drop.</summary>
    internal bool DropSeen { get; init; }

    /// <summary>How far the start lay certainly below its walkable support, beyond the contact skin and the support's
    /// certified error, whether or not a step was in progress. Zero otherwise.</summary>
    internal float CarriedLag { get; init; }
}

/// <summary>Resolves one grounded tick: recovery, start support, the lift, then per substep the shell sweep and
/// the down pass, with pacing of the step part and a clearance check on every new position.</summary>
internal static partial class GroundCore
{
    // A bound on one tick's substeps, so a huge displacement throws rather than running unbounded.
    internal const int MaxSubsteps = 1024;

    /// <summary>Moves a grounded body whose feet are at <paramref name="feet"/>, in the world's local frame, by
    /// the horizontal <paramref name="displacement"/>. Free motion on walkable support is exact. A start that is
    /// not walkable ends the tick at the start with its own footing. The <paramref name="queries"/> context enforces
    /// the <see cref="FootSupport"/> contract. <paramref name="tractionSlopeRadians"/> is the tick's traction
    /// gate, and every slope decision in the tick uses it. Null means
    /// <see cref="MoveTuning.MaxSlopeRadians"/>. <paramref name="climbBudget"/> is the climb the tick may still pay,
    /// in metres. Null means a whole tick's <c>MaxStepClimbSpeed * dt</c>. <paramref name="clampXz"/> is the
    /// play-area clamp. It cuts every substep's move before the move, a wall's tangent included, and every slide of
    /// the shell along a contact, so no substep that starts inside the bound ends outside it. A substep it shortens
    /// counts as blocked. <paramref name="stepInProgress"/> identifies carried lag as a paced discrete step,
    /// rather than deferred continuous ground.</summary>
    internal static GroundStepResult Step(Vector3 feet, Vector2 displacement, float dt, in MoveTuning tuning,
        in GroundCoreSettings settings, ContactQueryContext queries,
        float? tractionSlopeRadians = null, double? climbBudget = null,
        Func<float, float, Vector2>? clampXz = null, bool stepInProgress = false, ContactMediumPolicy? medium = null) =>
        Move(feet, displacement, dt, tuning, settings, queries,
            tractionSlopeRadians ?? tuning.MaxSlopeRadians, slide: false, climbBudget, clampXz, stepInProgress, medium);

    /// <summary>Moves a sliding body as <c>Step</c> moves a grounded one, with three differences. A steep
    /// start moves. A steep seat at or below the feet continues the tick instead of ending it, and must clear the
    /// shell without a push. A steep seat that fails that clearance ends the tick with footing
    /// <see cref="GroundFooting.None"/> at the last clear position, for the air pass. Steep ground above the feet
    /// stays a wall, walkable support grounds the body and the rest of the move walks on, and no support gives
    /// <see cref="GroundFooting.None"/>. Each substep's drop along the start plane stays within half the step
    /// height, so the seat's reach down holds it.</summary>
    internal static GroundStepResult Slide(Vector3 feet, Vector2 displacement, float dt, in MoveTuning tuning,
        in GroundCoreSettings settings, ContactQueryContext queries,
        float tractionSlopeRadians, double? climbBudget = null, Func<float, float, Vector2>? clampXz = null, ContactMediumPolicy? medium = null) =>
        Move(feet, displacement, dt, tuning, settings, queries,
            tractionSlopeRadians, slide: true, climbBudget, clampXz, stepInProgress: false, medium);

    static GroundStepResult Move(Vector3 feet, Vector2 displacement, float dt, in MoveTuning tuning,
        in GroundCoreSettings settings, ContactQueryContext queries,
        float tractionSlopeRadians, bool slide, double? climbBudget, Func<float, float, Vector2>? clampXz,
        bool stepInProgress, ContactMediumPolicy? medium)
    {
        int substeps = Validate(feet, displacement, dt, tuning, settings);
        float gate = tractionSlopeRadians;
        if (!float.IsFinite(gate))
            throw new ArgumentOutOfRangeException(nameof(tractionSlopeRadians), "The traction gate must be finite.");
        double budget = climbBudget ?? Budget(tuning, dt);
        if (!(budget >= 0))
            throw new ArgumentOutOfRangeException(nameof(climbBudget), "The climb budget must not be negative.");
        float footRadius = settings.FootRadiusFraction * tuning.CapsuleRadius;
        float cosMaxSlope = MathF.Cos(gate);

        Vector3 start = ShellMotion.RecoverWithQueries(queries, feet, tuning, out bool cleared);
        if (!cleared && queries.HasMediumProof)
            return new(feet, GroundFooting.Held, default, 0, Vector2.Zero, true) { ClimbBudget = budget };
        if (!cleared) start = feet;
        var startAxis = new Vector2(start.X, start.Z);
        SupportSample support = queries.FindSupport(
            new FootSupportQuery(startAxis, start.Y, footRadius, tuning.StepHeight, tuning.StepHeight, cosMaxSlope));
        bool moving = displacement != Vector2.Zero;
        if (support.Status == SupportStatus.Refused)
            return Result(feet, start, start.Y, GroundFooting.Held, support, Vector2.Zero, moving) with
            {
                ClimbBudget = budget,
            };
        double carriedLag = CarriedLag(support, start.Y);
        bool steepStart = slide && support.Status == SupportStatus.Steep;
        if (!cleared || (support.Status != SupportStatus.Walkable && !steepStart) ||
            (!moving && !Owes(support, start.Y)))
        {
            // A start that is steep or unsupported changes the footing before any time is spent.
            bool changes = cleared && support.Status is SupportStatus.Steep or SupportStatus.None;
            return Result(feet, start, start.Y, Footing(support.Status), support, Vector2.Zero, !cleared) with
            {
                RemainingTime = changes ? dt : 0f,
                ClimbBudget = budget,
                ChangedAtStart = changes,
            };
        }
        if (steepStart) substeps = Math.Max(substeps, SlideSubsteps(displacement, support.Normal, tuning));

        var tick = new Tick(queries, tuning, footRadius, cosMaxSlope, start,
            ShellMotion.LiftWithQueries(queries, start, tuning), support, slide, clampXz, stepInProgress ? carriedLag : 0);
        if (!moving)
        {
            // A body at rest still climbs onto the tread a paced climb left it below.
            bool settled = tick.Settle(ref budget);
            return Result(feet, start, tick.FeetY, GroundFooting.Walkable, support, Vector2.Zero, !settled) with
            {
                ClimbBudget = budget,
                StepRise = tick.StepRise,
                MoveRise = tick.MoveRise,
                CarriedLag = (float)carriedLag,
            };
        }
        Vector2 moved = tick.Run(displacement, substeps, ref budget, out GroundFooting footing, out bool blocked,
            out int unspent, medium, out bool handoff);
        return Result(feet, start, tick.FeetY, footing, tick.Support, moved, blocked) with
        {
            MediumHandoff = handoff,
            HorizontalPlanPreserved = tick.OnPlan && start.X == feet.X && start.Z == feet.Z,
            RemainingTime = unspent == 0 ? 0f : (float)((double)dt * unspent / substeps),
            ClimbBudget = budget,
            StepRise = tick.StepRise,
            MoveRise = tick.MoveRise,
            RiseSeen = tick.RiseSeen,
            DropSeen = tick.DropSeen,
            CarriedLag = (float)carriedLag,
        };
    }

    // The achieved move counts the recovery offset too, which is exactly zero when recovery did nothing. A body
    // that did not move keeps its start bits, negative zero included.
    static GroundStepResult Result(Vector3 input, Vector3 start, float feetY, GroundFooting footing,
        in SupportSample support, Vector2 moved, bool blocked)
    {
        var final = moved == Vector2.Zero
            ? start with { Y = feetY }
            : new Vector3(start.X + moved.X, feetY, start.Z + moved.Y);
        Vector2 achieved = new Vector2(start.X - input.X, start.Z - input.Z) + moved;
        return new(final, footing, support, feetY - input.Y, achieved, blocked);
    }

    // The lag a start carries below its walkable support, when it exceeds the contact skin and the support's error.
    static double CarriedLag(in SupportSample support, float feetY)
    {
        double lag = (double)support.Height - feetY;
        return support.Status == SupportStatus.Walkable && lag > (double)ShellMotion.ContactSkin + support.HeightError
            ? lag : 0;
    }

    // Feet certainly below their walkable support owe that climb. Feet within the support's error already stand on
    // it.
    static bool Owes(in SupportSample support, float feetY) =>
        support.Status == SupportStatus.Walkable && (double)support.Height - support.HeightError > feetY;

    // Substeps of at most half the capsule radius whose drop along a plane of this normal is at most half the step
    // height. Validation has already rejected a step height that is not positive, because the shell could not clear
    // its walkable plane. A plane too steep to bound takes the cap, and the seat then leaves the body airborne.
    static int SlideSubsteps(Vector2 displacement, Vector3 normal, in MoveTuning tuning)
    {
        double ny = Math.Clamp(normal.Y, 0f, 1f), across = Math.Sqrt(Math.Max(0, 1 - ny * ny));
        double length = 0.5 * tuning.CapsuleRadius;
        if (across > 0) length = Math.Min(length, 0.5 * tuning.StepHeight * ny / across);
        double count = Math.Ceiling(Length(displacement) / length);
        return count < MaxSubsteps ? Math.Max(1, (int)count) : MaxSubsteps;
    }

    static double Budget(in MoveTuning tuning, float dt) =>
        tuning.MaxStepClimbSpeed > 0 ? (double)tuning.MaxStepClimbSpeed * dt : double.PositiveInfinity;

    static GroundFooting Footing(SupportStatus status) => status switch
    {
        SupportStatus.Walkable => GroundFooting.Walkable,
        SupportStatus.Steep => GroundFooting.Steep,
        SupportStatus.None => GroundFooting.None,
        _ => GroundFooting.Held,
    };

    static int Validate(Vector3 feet, Vector2 displacement, float dt, in MoveTuning tuning,
        in GroundCoreSettings settings)
    {
        if (!float.IsFinite(feet.X) || !float.IsFinite(feet.Y) || !float.IsFinite(feet.Z))
            throw new ArgumentOutOfRangeException(nameof(feet), "The feet must be finite.");
        if (!float.IsFinite(displacement.X) || !float.IsFinite(displacement.Y))
            throw new ArgumentOutOfRangeException(nameof(displacement), "The displacement must be finite.");
        if (!float.IsFinite(dt) || dt <= 0)
            throw new ArgumentOutOfRangeException(nameof(dt), "dt must be positive and finite.");
        if (!float.IsFinite(settings.FootRadiusFraction) || settings.FootRadiusFraction <= 0)
            throw new ArgumentOutOfRangeException(nameof(settings),
                "FootRadiusFraction must be positive and finite.");
        if (!float.IsFinite(tuning.CapsuleRadius) || tuning.CapsuleRadius <= 0 ||
            !float.IsFinite(tuning.CapsuleHalfHeight) || !float.IsFinite(tuning.StepHeight) ||
            tuning.StepHeight < 0 || !float.IsFinite(tuning.MaxSlopeRadians) ||
            !float.IsFinite(tuning.MaxStepClimbSpeed))
            throw new ArgumentOutOfRangeException(nameof(tuning), "The ground core tuning values must be finite.");
        ShellGeometry.Validate(tuning, settings);

        double count = Math.Ceiling(Length(displacement) / (0.5 * tuning.CapsuleRadius));
        if (count > MaxSubsteps)
            throw new ArgumentOutOfRangeException(nameof(displacement),
                "The displacement is too long for one tick.");
        return Math.Max(1, (int)count);
    }

    /// <summary>The state of one tick's substeps. The body's axis is always the start axis plus the achieved
    /// move.</summary>
    partial struct Tick
    {
        readonly ContactQueryContext _queries;
        readonly MoveTuning _tuning;
        readonly float _footRadius;
        readonly float _cosMaxSlope;
        readonly Vector2 _startAxis;
        readonly float _startY;
        readonly float _lift;
        readonly bool _slide;
        readonly Func<float, float, Vector2>? _clampXz;
        Vector2 _achieved;
        double _owedDiscrete;
        double _stepRise;
        double _moveRise;

        internal Tick(ContactQueryContext queries, in MoveTuning tuning, float footRadius,
            float cosMaxSlope, Vector3 start, float lift, in SupportSample support, bool slide,
            Func<float, float, Vector2>? clampXz, double owedDiscrete)
        {
            _queries = queries;
            _tuning = tuning;
            _footRadius = footRadius;
            _cosMaxSlope = cosMaxSlope;
            _startAxis = new Vector2(start.X, start.Z);
            _startY = start.Y;
            _lift = lift;
            _slide = slide;
            _clampXz = clampXz;
            FeetY = start.Y;
            Support = support;
            _owedDiscrete = owedDiscrete;
        }

        internal float FeetY { get; private set; }
        internal SupportSample Support { get; private set; }
        internal readonly float StepRise => (float)_stepRise;
        internal readonly float MoveRise => (float)_moveRise;
        internal bool RiseSeen { get; private set; }
        internal bool DropSeen { get; private set; }
        internal bool OnPlan { get; private set; }

        readonly Vector2 Axis => _startAxis + _achieved;

        // unspent counts the substeps left after the footing changed to steep or none.
        internal Vector2 Run(Vector2 displacement, int substeps, ref double budget, out GroundFooting footing,
            out bool blocked, out int unspent, ContactMediumPolicy? medium, out bool handoff)
        {
            footing = Support.Status == SupportStatus.Steep ? GroundFooting.Steep : GroundFooting.Walkable;
            blocked = false;
            unspent = 0;
            handoff = false;
            if (!PayLag(ref budget)) { blocked = true; return _achieved; }
            // While nothing has deviated from the plan, the achieved move is the planned prefix itself, so free
            // motion is exact however many substeps it takes.
            bool onPlan = true;
            Vector2 planned = Vector2.Zero;
            for (int i = 0; i < substeps; i++)
            {
                Vector2 next = i == substeps - 1 ? displacement : displacement * ((float)(i + 1) / substeps);
                Vector2 move = next - planned;
                planned = next;
                if (move == Vector2.Zero) continue;
                SupportQueryDiagnostics.GroundState(new(Axis.X, FeetY, Axis.Y), Support.Height, Support.HeightError,
                    Support.Status is SupportStatus.Walkable or SupportStatus.Steep);
                SupportQueryDiagnostics.BeginGroundSubstep();
                try
                {
                    // A substep off the plan, or one whose prefix leaves a bound that is not convex, ends past the
                    // play area unless the clamp cuts it. A cut substep is a block.
                    if (PlayArea.Clamp(Axis, ref move, _clampXz))
                    {
                        blocked = true;
                        onPlan = false;
                        if (move == Vector2.Zero) break;
                    }

                    Attempt attempt = Resolve(move, onPlan ? next : null, budget);
                    if (attempt.Moved && attempt.Seat.Outcome == SeatOutcome.Wall)
                    {
                        // Undo the substep and slide the whole of it along the wall's horizontal tangent once.
                        blocked = true;
                        onPlan = false;
                        SupportQueryDiagnostics.Route(SupportQueryRoute.WallRetry);
                        Vector2 tangent = Tangent(move, attempt.Seat.WallNormal);
                        PlayArea.Clamp(Axis, ref tangent, _clampXz);
                        if (tangent == Vector2.Zero) break;
                        attempt = Resolve(tangent, null, budget);
                        if (attempt.Moved && attempt.Seat.Outcome == SeatOutcome.Wall) break;
                    }
                    // The same substep from the same position gives the same result, so a stop ends the tick.
                    if (!attempt.Moved || attempt.Seat.Outcome == SeatOutcome.Refused)
                    {
                        blocked = true;
                        break;
                    }
                    if (attempt.Blocked || attempt.Achieved != move)
                    {
                        blocked |= attempt.Blocked;
                        onPlan = false;
                    }

                    GroundSeatResult seat = attempt.Seat;
                    GroundPlacement placement = attempt.Placement;
                    if (!placement.Valid)
                    {
                        // A rejected slide seat leaves no accepted motion in this substep. The air pass owns its
                        // time too, whether the next support was steep or walkable, so the old support cannot stall it.
                        if (_slide && seat.Outcome is SeatOutcome.Seated or SeatOutcome.SteepSeated)
                        {
                            footing = GroundFooting.None;
                            unspent = substeps - i;
                        }
                        blocked = true;
                        break;
                    }
                    budget = placement.Budget;
                    if (placement.Pushed)
                    {
                        SupportQueryDiagnostics.Route(SupportQueryRoute.PushedPlacement);
                        blocked = true;
                        onPlan = false;
                    }
                    _achieved = placement.Total;
                    _moveRise += (double)placement.Feet.Y - FeetY;
                    FeetY = placement.Feet.Y;
                    Support = placement.Support;
                    if (Support.Status == SupportStatus.Walkable)
                        _queries.RecordAcceptedSupport(new(Axis.X, Support.Height, Axis.Y));
                    if (!_slide && seat.Outcome == SeatOutcome.Seated)
                    {
                        AccountDiscrete(seat.DiscreteStep);
                        RiseSeen |= seat.DiscreteStep > 0;
                        DropSeen |= seat.DiscreteStep < 0;
                    }
                    footing = Footing(Support.Status);
                    if (medium?.ChangesOwner(new(Axis.X, FeetY, Axis.Y), 0, Support,
                            footing == GroundFooting.Walkable) == true)
                    {
                        SupportQueryDiagnostics.Route(SupportQueryRoute.MediumHandoff);
                        handoff = true;
                        unspent = substeps - i - 1;
                        break;
                    }
                    if (seat.Outcome == SeatOutcome.SteepSeated)
                    {
                        footing = GroundFooting.Steep;
                        if (_slide) continue;
                        unspent = substeps - i - 1;
                        break;
                    }
                    footing = GroundFooting.Walkable;
                    if (seat.Outcome == SeatOutcome.Airborne)
                    {
                        footing = GroundFooting.None;
                        unspent = substeps - i - 1;
                        break;
                    }
                }
                finally
                {
                    SupportQueryDiagnostics.GroundState(new(Axis.X, FeetY, Axis.Y), Support.Height, Support.HeightError,
                        Support.Status is SupportStatus.Walkable or SupportStatus.Steep);
                    SupportQueryDiagnostics.EndGroundSubstep();
                }
            }
            OnPlan = onPlan;
            return _achieved;
        }

        // A body certainly below its walkable support, left there by an earlier tick's pacing, pays that owed
        // climb first from the tick's budget, within the lift the up pass proved clear. Paid after a refusal
        // instead, it would never be paid, because the refusal repeats from the same feet every tick.
        bool PayLag(ref double budget)
        {
            if (!Owes(Support, FeetY)) return true;
            double owed = Math.Min(Math.Min((double)Support.Height - FeetY, budget), _lift);
            if (!(owed > 0)) return true;
            float paidFeetY = GroundPlacement.NotAbove(FeetY + owed);
            var route = new ContactMotionRoute(_queries, new(Axis.X, FeetY, Axis.Y));
            if (!route.Append(new(Axis.X, paidFeetY, Axis.Y))) return false;
            _moveRise += (double)paidFeetY - FeetY;
            FeetY = paidFeetY;
            budget -= owed;
            if (!_slide) AccountDiscrete(0);
            return true;
        }

        // Every accepted move keeps only the discrete debt still below its new support. A partial drop erases
        // only the debt its lower support removes, and continuous lag never adds to that debt.
        void AccountDiscrete(double discreteStep)
        {
            double pending = _owedDiscrete + discreteStep;
            double lag = Support.Status == SupportStatus.Walkable ? (double)Support.Height - FeetY : 0;
            _owedDiscrete = Math.Clamp(lag, 0, Math.Max(pending, 0));
            _stepRise += pending - _owedDiscrete;
        }

        // A tick without displacement only pays owed climb.
        internal bool Settle(ref double budget) => PayLag(ref budget);

        // Clear substeps keep one attempt. Only a shortened sweep or invalid placement needs the standing
        // route. Lift-created obstructions disqualify the lifted route, even if its seat is walkable.
        readonly Attempt ResolveUnconstrained(Vector2 move, Vector2? planned, double budget)
        {
            float lift = FeetY == _startY ? _lift : (float)Math.Clamp((double)_startY + _lift - FeetY, 0, _lift);
            Attempt lifted = Try(move, lift, planned, budget);
            if (lift <= 0 || lifted.Seat.Outcome == SeatOutcome.Wall ||
                (!lifted.Blocked && lifted.Placement.Valid)) return lifted;
            Attempt standing = Try(move, 0, planned, budget);
            bool liftedValid = lifted.Placement.Valid &&
                (!lifted.Blocked || standing.Obstructions.Includes(lifted.Obstructions));
            bool standingValid = standing.Placement.Valid;
            // Either kind of slide seat that fails clearance is reported, so the tick can hand the body to air.
            if (!liftedValid)
                return standingValid || standing.Seat.Outcome == SeatOutcome.Wall ||
                    (_slide && standing.Moved && standing.Seat.Outcome is SeatOutcome.Seated or SeatOutcome.SteepSeated)
                    ? standing : DeniedMediumSeat(lifted, standing);
            if (!standingValid) return lifted;
            return Progress(standing.Achieved, move) > Progress(lifted.Achieved, move) ? standing : lifted;
        }

        static double Progress(Vector2 achieved, Vector2 move) =>
            (double)achieved.X * move.X + (double)achieved.Y * move.Y;

        // The seat is queried at the axis the body will report, the start axis plus the achieved total. A full move
        // on plan totals the planned prefix itself, so the feet are certified at their own axis, not at a summed
        // axis an ulp away.
        readonly Attempt Try(Vector2 move, float lift, Vector2? planned, double budget)
        {
            var feet = new Vector3(Axis.X, FeetY, Axis.Y);
            var route = new ContactMotionRoute(_queries, feet);
            ShellSweep sweep = ShellMotion.SweepWithQueries(_queries, feet, lift, move, _tuning, _cosMaxSlope,
                ref route, _clampXz);
            if (route.Denied) return default;
            if (sweep.Achieved == Vector2.Zero)
                return new Attempt(false, Vector2.Zero, sweep.Blocked, default, _achieved, default, sweep.Obstructions);
            Vector2 total = planned is Vector2 prefix && !sweep.Blocked && sweep.Achieved == move
                ? prefix
                : _achieved + sweep.Achieved;
            GroundSeatResult seat = GroundSeat.Resolve(_queries, Support, Axis,
                FeetY, _startAxis + total, Direction(move), _footRadius, _tuning, _cosMaxSlope);
            GroundPlacement placement = seat.Outcome is SeatOutcome.Wall or SeatOutcome.Refused ? default :
                GroundPlacement.Try(_queries, Support, FeetY,
                    _startAxis, total, seat, _footRadius, _tuning, _cosMaxSlope, budget, ref route, _slide);
            Vector2 achieved = placement.Valid && placement.Pushed
                ? placement.Total - _achieved
                : sweep.Achieved;
            return new Attempt(true, achieved, sweep.Blocked, seat, total, placement, sweep.Obstructions);
        }
    }

    // Total is the achieved prefix before clearance. Placement and its budget are committed only after selection.
    readonly record struct Attempt(bool Moved, Vector2 Achieved, bool Blocked, GroundSeatResult Seat, Vector2 Total,
        GroundPlacement Placement, ShellObstructions Obstructions);

    // The move less its component into the wall. Zero when the move does not press into the wall.
    static Vector2 Tangent(Vector2 move, Vector3 wallNormal)
    {
        var across = new Vector2(wallNormal.X, wallNormal.Z);
        float into = Vector2.Dot(move, across);
        return into < 0 ? move - into * across : Vector2.Zero;
    }

    // The unit direction of a non-zero move, normalised in double so a tiny move still has one.
    static Vector2 Direction(Vector2 move)
    {
        double length = Length(move);
        return new Vector2((float)(move.X / length), (float)(move.Y / length));
    }

    static double Length(Vector2 v) => Math.Sqrt((double)v.X * v.X + (double)v.Y * v.Y);
}
