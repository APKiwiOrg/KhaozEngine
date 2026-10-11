using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion.Contacts;

/// <summary>The whole tick of the contact controller, with the inputs of the legacy step core. It keeps the legacy
/// order: the commitment, the tick's traction gate, intent, the move, then the jump, decided last from the footing
/// the tick ended with, and the carried state. The move starts in the mode the tick's footing calls for (the ground
/// core, a slide, or the air pass), and a footing change part way through runs the rest of the tick in the next mode,
/// with one climb budget shared by the whole tick.</summary>
internal static partial class ContactController
{
    // A bound on one tick's mode changes. Every cycle of mode changes passes through the air pass, and each air
    // segment spends at least one of its substeps before it hands on, so the time left shrinks and the cap is a guard.
    const int MaxSegments = 8;

    static readonly SupportSample NoSupport =
        new(SupportStatus.None, float.NaN, float.NaN, Vector3.Zero, null, -1, Vector3.Zero);

    /// <summary>Advances <paramref name="state"/> one tick. The parameters mean what they mean for the legacy step
    /// core. <see cref="MoveState.Position"/> is the capsule centre, <see cref="MoveTuning.CapsuleHalfHeight"/> above
    /// the feet. A non-null <paramref name="world"/> must offer <see cref="IPhysicsQueryLeaseSource"/> and
    /// <see cref="IPhysicsSupportNeighborhood"/>, and every query of the tick runs under one read lease. A
    /// <paramref name="medium"/> that reports water throws until swimming moves here. Omitted
    /// <paramref name="settings"/> mean <c>new GroundCoreSettings()</c>. <paramref name="clampXz"/> clamps the planned
    /// move's end before the move and every substep's move in every mode, and again after the move as a backstop, as
    /// legacy clamps. A non-finite clamp or result returns the input state with its per-tick events cleared and the
    /// commitment kept.</summary>
    internal static MoveState Step(in MoveState state, Vector2 moveDir, float speedFraction, bool run, bool jump,
        float dt, Func<float, float, float> groundHeight, in MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal, IPhysicsWorld? world,
        Func<float, float, Vector2>? clampXz, Func<float, float, float, MovementMedium>? medium,
        float? faceYaw = null, GroundCoreSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(groundHeight);
        GroundCoreSettings foot = settings ?? new GroundCoreSettings();
        IPhysicsQueryLeaseSource? leases = null;
        if (world is not null)
        {
            leases = world as IPhysicsQueryLeaseSource ?? throw new NotSupportedException(
                "The contact controller needs a physics world that offers IPhysicsQueryLeaseSource.");
            if (world is not IPhysicsSupportNeighborhood)
                throw new NotSupportedException(
                    "The contact controller needs a physics world that offers IPhysicsSupportNeighborhood.");
        }

        using IPhysicsQueryLease? lease = leases?.AcquireQueryReadLease();
        var queries = new DryContactQueryContext(groundHeight, groundNormal, world, lease, tuning, medium);
        return StepWithQueries(state, moveDir, speedFraction, run, jump, dt, tuning, queries, clampXz,
            faceYaw, foot);
    }

    internal static MoveState StepWithQueries(in MoveState state, Vector2 moveDir, float speedFraction, bool run,
        bool jump, float dt, in MoveTuning tuning, ContactQueryContext queries,
        Func<float, float, Vector2>? clampXz = null,
        float? faceYaw = null, GroundCoreSettings? settings = null)
    {
        try
        {
            return StepCore(state, moveDir, speedFraction, run, jump, dt, tuning, queries, clampXz, faceYaw, settings);
        }
        catch (ContactQueryFailure failure)
        {
            queries.Refuse(failure);
            return MovementStepResult.HoldState(state) with { ClimbRateEwma = 0 };
        }
    }

    static MoveState StepCore(in MoveState state, Vector2 moveDir, float speedFraction, bool run,
        bool jump, float dt, in MoveTuning tuning, ContactQueryContext queries,
        Func<float, float, Vector2>? clampXz, float? faceYaw, GroundCoreSettings? settings)
    {
        GroundCoreSettings foot = settings ?? new GroundCoreSettings();
        MoveState s = state;
        MoveTuning t = tuning;
        bool commitmentControlled = CharacterMovement.PrepareCommitmentTick(ref s, ref t, ref moveDir,
            ref speedFraction, ref run, ref jump, ref faceYaw, dt, out bool committedFlight, out bool launched);

        Vector3 feet = queries.KernelFeet(s.Position, t);
        queries.CheckDryMedium(feet);
        queries.ValidatePlacement(feet);
        float gate = CharacterMovement.TractionGate(s.Grounded, t);
        // An airborne tick never starts where the air pass cannot recover the feet: below steep analytic terrain at
        // its axis, which the terrain wall treats as a face above them, or below walkable terrain by more than
        // StepHeight, past the reach of its landing. Only a defect leaves the feet there, so the tick seats them on
        // the terrain and its footing takes over, a slide where the terrain is steep. Within StepHeight below
        // walkable terrain the feet are a paced seat's lag, grounded or rising from it on a jump, and the air pass's
        // paced, clearance-checked landing owns them, as the ground core pays a grounded lag from its budget.
        float terrain = queries.HeightAt(feet);
        if (!s.Grounded && feet.Y < terrain && (terrain - feet.Y > t.StepHeight ||
                queries.NormalAt(feet)?.Y < MathF.Cos(gate)))
        {
            var route = new ContactMotionRoute(queries, feet);
            if (!route.Append(feet with { Y = terrain }))
                throw new ContactQueryFailure(MovementAvailability.Known, MovementStepOutcome.Blocked);
            feet.Y = terrain;
        }

        // Intent, as legacy: the walk or run speed scaled by AirControl in the air and by the entity's scale. Under
        // AirMomentum an airborne tick steers the carried velocity instead, and a committed flight flies its own.
        float speedScale = (s.Grounded ? 1f : t.AirControl) * s.SpeedScale;
        float speed = speedFraction > 0f ? (run ? t.RunSpeed : t.WalkSpeed) * speedScale * speedFraction : 0f;
        Vector2 intent = moveDir * speed;
        Vector2 commanded = intent;
        if (committedFlight)
            commanded = s.HorizontalVelocity;
        else if (t.AirMomentum && !s.Grounded)
            commanded = CharacterMovement.ResolveAirborneVelocity(s.HorizontalVelocity, moveDir,
                (run ? t.RunSpeed : t.WalkSpeed) * s.SpeedScale * speedFraction, dt, t);

        // The play-area clamp cuts the planned move's end before the move, as legacy clamps its target. The move
        // then runs at the velocity that reaches the clamped end. A slide plans its own move and clamps it the same
        // way, and every mode clamps each of its substeps and each slide of the shell along a contact, so the clamp
        // after the move is only a backstop.
        Vector2 velocity = commanded, displacement = commanded * dt;
        if (clampXz is not null)
        {
            var from = new Vector2(s.Position.X, s.Position.Z);
            Vector2 target = from + displacement;
            Vector2 clamped = clampXz(target.X, target.Y);
            if (!float.IsFinite(clamped.X) || !float.IsFinite(clamped.Y)) return Held(state, s.Commitment);
            if (clamped != target)
            {
                displacement = clamped - from;
                velocity = displacement / dt;
            }
        }

        Tick tick;
        float climbRate, climbRateEwma, stepDeltaY;
        {
            var context = new Context(queries, t, foot, gate, clampXz);
            Mode mode = Mode.Air;
            SupportSample support = default;
            // An airborne body on steep support slides only while its vertical speed after the tick's gravity does
            // not rise. A jump always rises, so it flies, and the air pass hands it back to a slide once it descends
            // onto steep support. A slide's vertical speed is its descending fall-line speed's vertical part, never
            // positive, so a slide stays a slide whatever its carry. A rising body pressed into a steep face flies,
            // and the air pass's terrain wall and shell contacts take away the horizontal part, so it cannot climb.
            if (s.Grounded)
                mode = Mode.Ground;
            else if (!committedFlight && !(s.VerticalVelocity - t.Gravity * dt > 0f) && SlideCore.InContact(feet,
                         t, foot, gate, queries, out support))
                mode = Mode.Slide;
            tick = Move(context, s, mode, support, feet, commanded, velocity, displacement, intent, dt);
            bool corrected = false;
            if (clampXz is not null)
            {
                Tick bounded = Bound(context, tick, clampXz);
                corrected = bounded.Feet != tick.Feet;
                tick = bounded;
            }
            // Ground signals belong to the move, before a jump consumes its footing at the end of the tick. The
            // backstop is a correction, not motion, so a tick it shifts reports none.
            (climbRate, climbRateEwma, stepDeltaY) = corrected ? (0f, 0f, 0f) : GroundSignals.Resolve(s,
                tick.StepRise, tick.MoveRise, tick.RiseSeen, tick.DropSeen, tick.CarriedLag, tick.GroundTravel,
                tick.Feet, tick.Support,
                tick.Mode == Mode.Ground && tick.Support.Status == SupportStatus.Walkable,
                dt, t, foot.FootRadiusFraction * t.CapsuleRadius, MathF.Cos(gate), queries);
        }

        // The jump, decided last as legacy. The impact and the grant latch first, so a buffered relaunch on the
        // landing tick still reports both.
        bool grounded = tick.Grounded;
        float verticalVelocity = tick.VerticalVelocity;
        float sinceGrounded = grounded ? 0f : s.TimeSinceGrounded + dt;
        bool jumpRequested = jump || s.JumpBufferRemaining > 0f;
        float jumpBuffer = jump ? t.JumpBuffer : MathF.Max(0f, s.JumpBufferRemaining - dt);
        float landingImpact = CharacterMovement.LandingImpact(s.Grounded, grounded, tick.FallSpeed);
        bool supportGranted = grounded;
        if (jumpRequested && (grounded || sinceGrounded <= t.CoyoteTime))
        {
            verticalVelocity = t.JumpSpeed;
            grounded = false;
            sinceGrounded = t.CoyoteTime + dt;
            jumpBuffer = 0f;
        }

        Vector3 end = queries.BodyCentre(tick.Feet);

        var result = new MoveState
        {
            Position = end,
            VerticalVelocity = verticalVelocity,
            Grounded = grounded,
            TimeSinceGrounded = sinceGrounded,
            JumpBufferRemaining = jumpBuffer,
            LandingImpactSpeed = landingImpact,
            SupportGranted = supportGranted,
            StepDeltaY = stepDeltaY,
            ClimbRate = climbRate,
            ClimbRateEwma = climbRateEwma,
            FacingYaw = commitmentControlled ? s.FacingYaw
                : CharacterMovement.ResolveFacing(s.FacingYaw, moveDir, faceYaw, dt, t),
            SpeedScale = state.SpeedScale,
            CommandedVelocity = tick.Commanded,
            // The carry is clipped against the last mode's own drive and time, so a mode change mid-tick neither
            // sheds nor keeps speed for the part of the tick another mode moved.
            HorizontalVelocity = CharacterMovement.ClipCarryToAchieved(tick.Last.Carry, tick.Last.Drive,
                tick.Last.Achieved, tick.Last.Time),
            Commitment = CharacterMovement.FinishCommitmentTick(s.Commitment, launched, grounded, s.Position, end,
                dt),
        };
        return Finite(result) ? result : Held(state, result.Commitment);
    }

    // The input state with the per-tick events cleared and the commitment the tick advanced.
    static MoveState Held(in MoveState state, in MovementCommitment commitment) => state with
    {
        LandingImpactSpeed = 0f,
        SupportGranted = false,
        StepDeltaY = 0f,
        ClimbRate = 0f,
        ClimbRateEwma = 0f,
        Commitment = commitment,
    };

    // The clamp after the move is a backstop. Every mode clamps each substep's move and every slide of the shell
    // along a contact, so only a clearance push or a recovery can still end past the play area. A shifted end is
    // placed at the clamped axis through the ground core's placement, which pays a rise from the climb budget the tick
    // has left and clears the shell. An end that
    // stood on support takes walkable support within StepHeight above or below it, and steep support at or below it.
    // An airborne end takes only walkable support above it, never steep. A placement the shell can clear only by a
    // push leaves the feet where they were, because the push would move the axis again. Whatever the placement, the
    // feet never end below analytic terrain at the axis. A non-finite clamp poisons the end, which holds the tick.
    static Tick Bound(in Context c, in Tick tick, Func<float, float, Vector2> clampXz)
    {
        Vector3 feet = tick.Feet;
        Vector2 axis = clampXz(feet.X, feet.Z);
        if (axis.X == feet.X && axis.Y == feet.Z) return tick;
        Segment last = tick.Last with { Achieved = tick.Last.Achieved + new Vector2(axis.X - feet.X, axis.Y - feet.Z) };
        if (!float.IsFinite(axis.X) || !float.IsFinite(axis.Y))
            return tick with { Feet = new Vector3(axis.X, feet.Y, axis.Y), Last = last };

        var route = new ContactMotionRoute(c.Queries, feet);
        if (!route.Append(new(axis.X, feet.Y, axis.Y)))
            throw new ContactQueryFailure(MovementAvailability.Known, MovementStepOutcome.Blocked);
        MoveTuning t = c.Tuning;
        float y = feet.Y, footRadius = c.Settings.FootRadiusFraction * t.CapsuleRadius, cosGate = MathF.Cos(c.Gate);
        SupportSample support = c.Queries.FindSupport(
            new FootSupportQuery(axis, y, footRadius, t.StepHeight, tick.Supported ? t.StepHeight : 0f, cosGate));
        GroundSeatResult seat = support.Status switch
        {
            SupportStatus.Walkable when tick.Supported || support.Height > y => new(SeatOutcome.Seated,
                support.Height, support, MathF.Max(0f, GroundPlacement.NotBelow((double)support.Height - y)),
                Vector3.Zero),
            SupportStatus.Steep when tick.Supported && support.Height <= y => new(SeatOutcome.SteepSeated,
                support.Height, support, 0f, Vector3.Zero),
            _ => new(SeatOutcome.Airborne, y, support, 0f, Vector3.Zero),
        };
        if (seat.Outcome is SeatOutcome.Seated or SeatOutcome.SteepSeated)
        {
            GroundPlacement placement = GroundPlacement.Try(c.Queries,
                NoSupport, y, axis, Vector2.Zero, seat, footRadius, t, cosGate, tick.Budget, ref route, clearSteep: true);
            if (placement.Valid && !placement.Pushed) y = placement.Feet.Y;
        }
        if (c.Queries.TryHeightAt(new(axis.X, y, axis.Y), out float terrain)) y = MathF.Max(y, terrain);
        if (!route.Append(new(axis.X, y, axis.Y)))
            throw new ContactQueryFailure(MovementAvailability.Known, MovementStepOutcome.Blocked);
        return tick with { Feet = new Vector3(axis.X, y, axis.Y), Last = last };
    }

    /// <summary>The tick's queries and the knobs every mode reads.</summary>
    readonly record struct Context(ContactQueryContext Queries, MoveTuning Tuning, GroundCoreSettings Settings, float Gate,
        Func<float, float, Vector2>? ClampXz);

    enum Mode : byte { Ground, Slide, Air }

    /// <summary>One mode's part of the tick: the velocity it carries on, the velocity that drove its move, the move it
    /// achieved and its time.</summary>
    readonly record struct Segment(Vector2 Carry, Vector2 Drive, Vector2 Achieved, float Time);

    /// <summary>What the move did. <see cref="FallSpeed"/> is the vertical speed the tick's last landing erased,
    /// negative downward, and zero without one. <see cref="Commanded"/> is the first mode's drive.
    /// <see cref="Supported"/> says the tick ended on support, walkable or steep. <see cref="Budget"/> is the climb the
    /// tick has left, in metres. <see cref="Mode"/> and <see cref="Support"/> keep the final footing.
    /// <see cref="StepRise"/>, <see cref="MoveRise"/> and <see cref="GroundTravel"/> sum only walking ground
    /// segments, excluding air, landings and slides. <see cref="RiseSeen"/> and <see cref="DropSeen"/> say whether any
    /// of them seated across a discrete rise or drop, and <see cref="CarriedLag"/> is the first one's start
    /// lag.</summary>
    readonly record struct Tick(Vector3 Feet, float VerticalVelocity, bool Grounded, float FallSpeed,
        Vector2 Commanded, Segment Last, bool Supported, double Budget, Mode Mode, SupportSample Support,
        float StepRise, float MoveRise, Vector2 GroundTravel, bool RiseSeen, bool DropSeen, float CarriedLag);

    // Runs the tick's modes in turn. A grounded move that loses its footing flies or slides the time it has left. A
    // grounded start on steep support slides the whole tick with the carried velocity, as a slide start does. A slide
    // that loses its support flies the rest. A flight that lands walks the rest of its planned move, and one that
    // meets steep support slides the rest. Every mode pays climbing from the budget the earlier modes left. The first
    // segment moves at the clamped velocity but carries and reports the commanded one, so a clamp sheds carry as a
    // denial, as legacy does.
    static Tick Move(in Context c, in MoveState s, Mode mode, SupportSample support, Vector3 feet, Vector2 commanded,
        Vector2 velocity, Vector2 displacement, Vector2 intent, float dt)
    {
        MoveTuning t = c.Tuning;
        double budget = t.MaxStepClimbSpeed > 0 ? (double)t.MaxStepClimbSpeed * dt : double.PositiveInfinity;
        float vertical = s.VerticalVelocity, time = dt, fallSpeed = 0f;
        float stepRise = 0f, moveRise = 0f;
        bool riseSeen = false, dropSeen = false;
        float? carriedLag = null;
        Vector2 groundTravel = Vector2.Zero;
        bool grounded = false, supported = false;
        Vector2? first = null;
        Segment last = new(commanded, commanded, Vector2.Zero, dt);
        // A tick that starts sliding slides its own carry. The input only steers along the contour, so it never enters
        // the carry, as legacy.
        if (mode == Mode.Slide) velocity = s.HorizontalVelocity;
        for (int segment = 0; segment < MaxSegments; segment++)
        {
            if (mode == Mode.Ground)
            {
                GroundStepResult step = GroundCore.Step(feet, displacement, time, t, c.Settings, c.Queries, c.Gate, budget, c.ClampXz,
                    stepInProgress: s.StepDeltaY > 0 || GroundSignals.Ascending(s.ClimbRate));
                c.Queries.RequireRecoveryAccepted();
                c.Queries.AcceptRoute(step.Blocked);
                (feet, budget, vertical) = (step.Feet, step.ClimbBudget, 0f);
                support = step.Support;
                stepRise += step.StepRise;
                moveRise += step.MoveRise;
                riseSeen |= step.RiseSeen;
                dropSeen |= step.DropSeen;
                carriedLag ??= step.CarriedLag;
                groundTravel += step.Achieved;
                grounded = step.Footing is GroundFooting.Walkable or GroundFooting.Held;
                supported = grounded || step.Footing == GroundFooting.Steep;
                bool steepStart = segment == 0 && step.Footing == GroundFooting.Steep && step.ChangedAtStart;
                if (steepStart)
                {
                    (velocity, vertical) = (s.HorizontalVelocity, s.VerticalVelocity);
                }
                else
                {
                    Vector2 ask = segment == 0 ? commanded : velocity;
                    last = new Segment(ask, ask, step.Achieved, time);
                    first ??= ask;
                }
                if (grounded || !(step.RemainingTime > 0)) break;
                time = step.RemainingTime;
                mode = step.Footing == GroundFooting.Steep ? Mode.Slide : Mode.Air;
                continue;
            }
            if (mode == Mode.Slide)
            {
                SlideStepResult slide = SlideCore.Step(feet, velocity, vertical, intent, time, t, c.Settings, c.Gate,
                    support, c.Queries, budget, c.ClampXz);
                Vector2 drive = slide.HorizontalVelocity + slide.Steer;
                last = new Segment(slide.HorizontalVelocity, drive, slide.Achieved, time);
                first ??= drive;
                (feet, budget, velocity, vertical) =
                    (slide.Feet, slide.ClimbBudget, slide.HorizontalVelocity, slide.VerticalVelocity);
                support = slide.Support;
                grounded = slide.Outcome is SlideOutcome.Landed or SlideOutcome.Wedged;
                supported = slide.Outcome != SlideOutcome.Airborne;
                if (slide.Outcome == SlideOutcome.Landed) (vertical, fallSpeed) = (0f, -slide.ImpactSpeed);
                // A wedge counts as grounded, so it latches the fall the slide integrated as its landing and holds no
                // vertical speed, as legacy.
                if (slide.Outcome == SlideOutcome.Wedged)
                    (vertical, fallSpeed) = (0f, slide.IntegratedVerticalVelocity);
                if (slide.Outcome != SlideOutcome.Airborne || !(slide.RemainingTime > 0)) break;
                time = slide.RemainingTime;
                mode = Mode.Air;
                continue;
            }
            AirStepResult air = AirPass.Step(feet, velocity, vertical, time, t.Gravity, t, c.Settings, c.Gate,
                c.Queries, budget, c.ClampXz);
            c.Queries.AcceptRoute(air.Blocked);
            Vector2 asked = segment == 0 ? commanded : velocity;
            last = new Segment(asked, asked, air.Achieved, time);
            first ??= asked;
            (feet, budget, velocity, vertical) = (air.Feet, air.ClimbBudget, air.HorizontalVelocity, air.VerticalVelocity);
            support = air.Support;
            grounded = air.Outcome == AirOutcome.Landed;
            supported = air.Outcome != AirOutcome.Airborne;
            if (grounded) fallSpeed = -air.FallSpeedAtContact;
            if (air.Outcome == AirOutcome.Airborne || !(air.RemainingTime > 0)) break;
            time = air.RemainingTime;
            if (grounded)
            {
                // The rest of the move at the velocity the contacts left, not the planned one.
                displacement = velocity * time;
                mode = Mode.Ground;
            }
            else
            {
                mode = Mode.Slide;
            }
        }
        return new Tick(feet, vertical, grounded, fallSpeed, first ?? commanded, last, supported, budget, mode,
            support, stepRise, moveRise, groundTravel, riseSeen, dropSeen, carriedLag ?? 0f);
    }

    static bool Finite(in MoveState s) =>
        float.IsFinite(s.Position.X) && float.IsFinite(s.Position.Y) && float.IsFinite(s.Position.Z) &&
        float.IsFinite(s.VerticalVelocity) && float.IsFinite(s.HorizontalVelocity.X) &&
        float.IsFinite(s.HorizontalVelocity.Y) && float.IsFinite(s.FacingYaw) && float.IsFinite(s.ClimbRate) &&
        float.IsFinite(s.ClimbRateEwma) && float.IsFinite(s.StepDeltaY);
}
