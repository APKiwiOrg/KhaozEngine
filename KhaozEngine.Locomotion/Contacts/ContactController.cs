using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion.Contacts;

/// <summary>The whole tick of the contact controller, with the inputs of the legacy step core. It keeps the legacy
/// order: the commitment, the tick's traction gate, intent, the move, then the jump, decided last from the footing
/// the tick ended with, and the carried state. The move starts in the mode the tick's footing calls for (the ground
/// core, a slide, or the air pass), and a footing change part way through runs the rest of the tick in the next mode,
/// with one climb budget shared by the whole tick.</summary>
internal static class ContactController
{
    // A bound on one tick's mode changes. Every cycle of mode changes passes through the air pass, and each air
    // segment spends at least one of its substeps before it hands on, so the time left shrinks and the cap is a guard.
    const int MaxSegments = 8;

    /// <summary>Advances <paramref name="state"/> one tick. The parameters mean what they mean for the legacy step
    /// core. <see cref="MoveState.Position"/> is the capsule centre, <see cref="MoveTuning.CapsuleHalfHeight"/> above
    /// the feet. A non-null <paramref name="world"/> must offer <see cref="IPhysicsQueryLeaseSource"/> and
    /// <see cref="IPhysicsSupportNeighborhood"/>, and every query of the tick runs under one read lease. A
    /// <paramref name="medium"/> that reports water throws until swimming moves here. Omitted
    /// <paramref name="settings"/> mean <c>new GroundCoreSettings()</c>. <paramref name="clampXz"/> clamps the planned
    /// move's end before the move, and again after it, as legacy does. A non-finite clamp or result returns the input
    /// state with its per-tick events cleared and the commitment kept.</summary>
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

        MoveState s = state;
        MoveTuning t = tuning;
        bool commitmentControlled = CharacterMovement.PrepareCommitmentTick(ref s, ref t, ref moveDir,
            ref speedFraction, ref run, ref jump, ref faceYaw, dt, out bool committedFlight, out bool launched);

        float halfHeight = t.CapsuleHalfHeight;
        if (medium is not null && medium(s.Position.X, s.Position.Z, s.Position.Y - halfHeight).InWater)
            throw new NotSupportedException("The contact controller does not swim yet. Water arrives in phase 4.");

        float gate = CharacterMovement.TractionGate(s.Grounded, t);
        var feet = new Vector3(s.Position.X, s.Position.Y - halfHeight, s.Position.Z);

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
        // then runs at the velocity that reaches the clamped end. A slide plans its own move, so the clamp after the
        // move bounds it.
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
        using (IPhysicsQueryLease? lease = leases?.AcquireQueryReadLease())
        {
            var context = new Context(groundHeight, groundNormal, world, lease, t, foot, gate);
            Mode mode = Mode.Air;
            SupportSample support = default;
            if (s.Grounded)
                mode = Mode.Ground;
            else if (!committedFlight && SlideCore.InContact(feet, t, foot, gate, groundHeight, groundNormal,
                         world, lease, out support))
                mode = Mode.Slide;
            tick = Move(context, s, mode, support, feet, commanded, velocity, displacement, intent, dt);
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

        var end = new Vector3(tick.Feet.X, tick.Feet.Y + halfHeight, tick.Feet.Z);
        Vector2 achieved = tick.Last.Achieved;
        if (clampXz is not null)
        {
            Vector2 c = clampXz(end.X, end.Z);
            if (c.X != end.X || c.Y != end.Z)
            {
                achieved += new Vector2(c.X - end.X, c.Y - end.Z);
                end = new Vector3(c.X, end.Y, c.Y);
            }
        }

        var result = new MoveState
        {
            Position = end,
            VerticalVelocity = verticalVelocity,
            Grounded = grounded,
            TimeSinceGrounded = sinceGrounded,
            JumpBufferRemaining = jumpBuffer,
            LandingImpactSpeed = landingImpact,
            SupportGranted = supportGranted,
            FacingYaw = commitmentControlled ? s.FacingYaw
                : CharacterMovement.ResolveFacing(s.FacingYaw, moveDir, faceYaw, dt, t),
            SpeedScale = state.SpeedScale,
            CommandedVelocity = tick.Commanded,
            // The carry is clipped against the last mode's own drive and time, so a mode change mid-tick neither
            // sheds nor keeps speed for the part of the tick another mode moved.
            HorizontalVelocity = CharacterMovement.ClipCarryToAchieved(tick.Last.Carry, tick.Last.Drive, achieved,
                tick.Last.Time),
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
        Commitment = commitment,
    };

    /// <summary>The tick's queries and the knobs every mode reads.</summary>
    readonly record struct Context(Func<float, float, float> GroundHeight, Func<float, float, Vector3>? GroundNormal,
        IPhysicsWorld? World, IPhysicsQueryLease? Lease, MoveTuning Tuning, GroundCoreSettings Settings, float Gate);

    enum Mode : byte { Ground, Slide, Air }

    /// <summary>One mode's part of the tick: the velocity it carries on, the velocity that drove its move, the move it
    /// achieved and its time.</summary>
    readonly record struct Segment(Vector2 Carry, Vector2 Drive, Vector2 Achieved, float Time);

    /// <summary>What the move did. <see cref="FallSpeed"/> is the vertical speed the tick's last landing erased,
    /// negative downward, and zero without one. <see cref="Commanded"/> is the first mode's drive.</summary>
    readonly record struct Tick(Vector3 Feet, float VerticalVelocity, bool Grounded, float FallSpeed,
        Vector2 Commanded, Segment Last);

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
        bool grounded = false;
        Vector2? first = null;
        Segment last = new(commanded, commanded, Vector2.Zero, dt);
        // A tick that starts sliding slides its own carry. The input only steers along the contour, so it never enters
        // the carry, as legacy.
        if (mode == Mode.Slide) velocity = s.HorizontalVelocity;
        for (int segment = 0; segment < MaxSegments; segment++)
        {
            if (mode == Mode.Ground)
            {
                GroundStepResult step = GroundCore.Step(feet, displacement, time, t, c.Settings, c.GroundHeight,
                    c.GroundNormal, c.World, c.Lease, c.Gate, budget);
                (feet, budget, vertical) = (step.Feet, step.ClimbBudget, 0f);
                grounded = step.Footing is GroundFooting.Walkable or GroundFooting.Held;
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
                support = step.Support;
                mode = step.Footing == GroundFooting.Steep ? Mode.Slide : Mode.Air;
                continue;
            }
            if (mode == Mode.Slide)
            {
                SlideStepResult slide = SlideCore.Step(feet, velocity, vertical, intent, time, t, c.Settings, c.Gate,
                    support, c.GroundHeight, c.GroundNormal, c.World, c.Lease, budget);
                Vector2 drive = slide.HorizontalVelocity + slide.Steer;
                last = new Segment(slide.HorizontalVelocity, drive, slide.Achieved, time);
                first ??= drive;
                // A wedge swallows the descent the body carried into it.
                float entering = vertical;
                (feet, budget, velocity, vertical) =
                    (slide.Feet, slide.ClimbBudget, slide.HorizontalVelocity, slide.VerticalVelocity);
                grounded = slide.Outcome is SlideOutcome.Landed or SlideOutcome.Wedged;
                if (slide.Outcome == SlideOutcome.Landed) (vertical, fallSpeed) = (0f, -slide.ImpactSpeed);
                if (slide.Outcome == SlideOutcome.Wedged) fallSpeed = entering;
                if (slide.Outcome != SlideOutcome.Airborne || !(slide.RemainingTime > 0)) break;
                time = slide.RemainingTime;
                mode = Mode.Air;
                continue;
            }
            AirStepResult air = AirPass.Step(feet, velocity, vertical, time, t.Gravity, t, c.Settings, c.Gate,
                c.GroundHeight, c.GroundNormal, c.World, c.Lease, budget);
            Vector2 asked = segment == 0 ? commanded : velocity;
            last = new Segment(asked, asked, air.Achieved, time);
            first ??= asked;
            (feet, budget, velocity, vertical) = (air.Feet, air.ClimbBudget, air.HorizontalVelocity, air.VerticalVelocity);
            grounded = air.Outcome == AirOutcome.Landed;
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
                support = air.Support;
                mode = Mode.Slide;
            }
        }
        return new Tick(feet, vertical, grounded, fallSpeed, first ?? commanded, last);
    }

    static bool Finite(in MoveState s) =>
        float.IsFinite(s.Position.X) && float.IsFinite(s.Position.Y) && float.IsFinite(s.Position.Z) &&
        float.IsFinite(s.VerticalVelocity) && float.IsFinite(s.HorizontalVelocity.X) &&
        float.IsFinite(s.HorizontalVelocity.Y) && float.IsFinite(s.FacingYaw);
}
