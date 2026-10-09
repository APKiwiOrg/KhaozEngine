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
    float Rise, Vector2 Achieved, bool Blocked);

/// <summary>Resolves one grounded tick: recovery, start support, the lift, then per substep the shell sweep and
/// the down pass, with pacing of the step part and a clearance check on every new position.</summary>
internal static class GroundCore
{
    // A bound on one tick's substeps, so a huge displacement throws rather than running unbounded.
    const int MaxSubsteps = 1024;

    /// <summary>Moves a grounded body whose feet are at <paramref name="feet"/>, in the world's local frame, by
    /// the horizontal <paramref name="displacement"/>. Free motion on walkable support is exact. A start that is
    /// not walkable ends the tick at the start with its own footing. A non-null <paramref name="world"/> follows
    /// the <see cref="FootSupport.Find"/> contract.</summary>
    internal static GroundStepResult Step(Vector3 feet, Vector2 displacement, float dt, in MoveTuning tuning,
        in GroundCoreSettings settings, Func<float, float, float>? groundHeight,
        Func<float, float, Vector3>? groundNormal, IPhysicsWorld? world, IPhysicsQueryLease? lease)
    {
        int substeps = Validate(feet, displacement, dt, tuning, settings);
        float footRadius = settings.FootRadiusFraction * tuning.CapsuleRadius;
        float cosMaxSlope = MathF.Cos(tuning.MaxSlopeRadians);

        Vector3 start = ShellMotion.Recover(world, feet, tuning, out bool cleared);
        if (!cleared) start = feet;
        var startAxis = new Vector2(start.X, start.Z);
        SupportSample support = FootSupport.Find(groundHeight, groundNormal, world, lease,
            new FootSupportQuery(startAxis, start.Y, footRadius, tuning.StepHeight, tuning.StepHeight, cosMaxSlope));
        bool moving = displacement != Vector2.Zero;
        if (support.Status == SupportStatus.Refused)
            return Result(feet, start, start.Y, GroundFooting.Held, support, Vector2.Zero, moving);
        if (!cleared || support.Status != SupportStatus.Walkable || !moving)
            return Result(feet, start, start.Y, Footing(support.Status), support, Vector2.Zero, !cleared);

        var tick = new Tick(groundHeight, groundNormal, world, lease, tuning, footRadius, cosMaxSlope, start,
            ShellMotion.Lift(world, start, tuning), support);
        Vector2 moved = tick.Run(displacement, substeps, dt, out GroundFooting footing, out bool blocked);
        return Result(feet, start, tick.FeetY, footing, tick.Support, moved, blocked);
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
        ShellGeometry.Validate(tuning);

        double count = Math.Ceiling(Length(displacement) / (0.5 * tuning.CapsuleRadius));
        if (count > MaxSubsteps)
            throw new ArgumentOutOfRangeException(nameof(displacement),
                "The displacement is too long for one tick.");
        return Math.Max(1, (int)count);
    }

    /// <summary>The state of one tick's substeps. The body's axis is always the start axis plus the achieved
    /// move.</summary>
    struct Tick
    {
        readonly Func<float, float, float>? _groundHeight;
        readonly Func<float, float, Vector3>? _groundNormal;
        readonly IPhysicsWorld? _world;
        readonly IPhysicsQueryLease? _lease;
        readonly MoveTuning _tuning;
        readonly float _footRadius;
        readonly float _cosMaxSlope;
        readonly Vector2 _startAxis;
        readonly float _startY;
        readonly float _lift;
        Vector2 _achieved;

        internal Tick(Func<float, float, float>? groundHeight, Func<float, float, Vector3>? groundNormal,
            IPhysicsWorld? world, IPhysicsQueryLease? lease, in MoveTuning tuning, float footRadius,
            float cosMaxSlope, Vector3 start, float lift, in SupportSample support)
        {
            _groundHeight = groundHeight;
            _groundNormal = groundNormal;
            _world = world;
            _lease = lease;
            _tuning = tuning;
            _footRadius = footRadius;
            _cosMaxSlope = cosMaxSlope;
            _startAxis = new Vector2(start.X, start.Z);
            _startY = start.Y;
            _lift = lift;
            FeetY = start.Y;
            Support = support;
        }

        internal float FeetY { get; private set; }
        internal SupportSample Support { get; private set; }

        readonly Vector2 Axis => _startAxis + _achieved;

        internal Vector2 Run(Vector2 displacement, int substeps, float dt, out GroundFooting footing,
            out bool blocked)
        {
            double budget = _tuning.MaxStepClimbSpeed > 0
                ? (double)_tuning.MaxStepClimbSpeed * dt
                : double.PositiveInfinity;
            footing = GroundFooting.Walkable;
            blocked = false;
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

                Attempt attempt = Resolve(move);
                if (attempt.Moved && attempt.Seat.Outcome == SeatOutcome.Wall)
                {
                    // Undo the substep and slide the whole of it along the wall's horizontal tangent once.
                    blocked = true;
                    onPlan = false;
                    Vector2 tangent = Tangent(move, attempt.Seat.WallNormal);
                    if (tangent == Vector2.Zero) break;
                    attempt = Resolve(tangent);
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
                float feetY = seat.Outcome switch
                {
                    SeatOutcome.Seated => Paced(seat, ref budget),
                    SeatOutcome.SteepSeated => seat.FeetY,
                    _ => FeetY,
                };
                Vector2 axis = Axis + attempt.Achieved;
                var target = new Vector3(axis.X, feetY, axis.Y);
                // Steep motion is phase 3 work, so a steep seat keeps its target without a clearance push and
                // never depends on a clearance float tie.
                Vector3 placed = target;
                if (seat.Outcome != SeatOutcome.SteepSeated && !Clear(target, out placed))
                {
                    blocked = true;
                    break;
                }
                if (placed != target) onPlan = false;
                _achieved = onPlan ? next : _achieved + attempt.Achieved +
                    new Vector2(placed.X - target.X, placed.Z - target.Z);
                FeetY = placed.Y;
                Support = seat.Support;
                if (seat.Outcome == SeatOutcome.SteepSeated)
                {
                    footing = GroundFooting.Steep;
                    break;
                }
                if (seat.Outcome == SeatOutcome.Airborne)
                {
                    footing = GroundFooting.None;
                    break;
                }
            }
            return _achieved;
        }

        // A lifted attempt that ends on a refusal is repeated once without the lift. The lifted shell travels at
        // one height, so a body that climbs within the tick never raises it above the swept lift.
        readonly Attempt Resolve(Vector2 move)
        {
            float lift = FeetY == _startY ? _lift : (float)Math.Clamp((double)_startY + _lift - FeetY, 0, _lift);
            Attempt attempt = Try(move, lift);
            if (attempt.Moved && attempt.Seat.Outcome == SeatOutcome.Refused && lift > 0)
                attempt = Try(move, 0);
            return attempt;
        }

        readonly Attempt Try(Vector2 move, float lift)
        {
            var feet = new Vector3(Axis.X, FeetY, Axis.Y);
            ShellSweep sweep = ShellMotion.Sweep(_world, feet, lift, move, _tuning, _cosMaxSlope);
            if (sweep.Achieved == Vector2.Zero)
                return new Attempt(false, Vector2.Zero, sweep.Blocked, default);
            GroundSeatResult seat = GroundSeat.Resolve(_groundHeight, _groundNormal, _world, _lease, Support, Axis,
                FeetY, Axis + sweep.Achieved, Direction(move), _footRadius, _tuning);
            return new Attempt(true, sweep.Achieved, sweep.Blocked, seat);
        }

        // The step part of this substep plus any climb an earlier tick left unpaid, the body's lag below its
        // support. A positive total is paid from the tick's budget and the rest leaves the feet below the tread.
        readonly float Paced(in GroundSeatResult seat, ref double budget)
        {
            double lag = Support.Status == SupportStatus.Walkable ? Math.Max(0, (double)Support.Height - FeetY) : 0;
            double wanted = (double)seat.StepPart + lag;
            if (!(wanted > 0)) return seat.FeetY;
            double paid = Math.Min(wanted, budget);
            budget -= paid;
            return paid < wanted ? (float)((double)seat.FeetY - (wanted - paid)) : seat.FeetY;
        }

        // The shell at a new position must not overlap. An overlap is pushed out once along the MTV plus the skin.
        // False when the pushed shell still overlaps. A touching shell is clear.
        readonly bool Clear(Vector3 target, out Vector3 placed)
        {
            placed = target;
            if (_world is null) return true;
            CapsuleShape shape = ShellGeometry.Shape(_tuning);
            if (!_world.ComputePenetration(shape, Pose.At(ShellGeometry.Centre(target, _tuning)), out Vector3 mtv))
                return true;
            float depth = mtv.Length();
            if (depth == 0) return true;
            if (!float.IsFinite(depth)) return false;
            Vector3 pushed = target + mtv * ((depth + ShellMotion.ContactSkin) / depth);
            if (_world.ComputePenetration(shape, Pose.At(ShellGeometry.Centre(pushed, _tuning)), out Vector3 again) &&
                again != Vector3.Zero)
                return false;
            placed = pushed;
            return true;
        }
    }

    readonly record struct Attempt(bool Moved, Vector2 Achieved, bool Blocked, GroundSeatResult Seat);

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
