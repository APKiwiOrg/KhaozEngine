using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion.Contacts;

/// <summary>The whole tick of the contact controller, with the inputs of the legacy step core. It keeps the legacy
/// order: the commitment, the tick's traction gate, intent, one branch on the footing the tick started with (the
/// ground core, a slide, or the air pass), then the jump, decided last from the footing the tick ended with, and the
/// carried state.</summary>
internal static class ContactController
{
    // Below this squared length a slide plane's horizontal normal carries no fall line, as in the slide core.
    const float FlatNormalSquared = 1e-12f;

    /// <summary>Advances <paramref name="state"/> one tick. The parameters mean what they mean for the legacy step
    /// core. <see cref="MoveState.Position"/> is the capsule centre, <see cref="MoveTuning.CapsuleHalfHeight"/> above
    /// the feet. A non-null <paramref name="world"/> must offer <see cref="IPhysicsQueryLeaseSource"/> and
    /// <see cref="IPhysicsSupportNeighborhood"/>, and every query of the tick runs under one read lease. A
    /// <paramref name="medium"/> that reports water throws until swimming moves here. The default
    /// <paramref name="settings"/> mean <c>new GroundCoreSettings()</c>. A non-finite result returns the input state
    /// with its per-tick events cleared and the commitment kept.</summary>
    internal static MoveState Step(in MoveState state, Vector2 moveDir, float speedFraction, bool run, bool jump,
        float dt, Func<float, float, float> groundHeight, in MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal, IPhysicsWorld? world,
        Func<float, float, Vector2>? clampXz, Func<float, float, float, MovementMedium>? medium,
        float? faceYaw = null, GroundCoreSettings settings = default)
    {
        ArgumentNullException.ThrowIfNull(groundHeight);
        // A default parameter cannot call the parameterless constructor, so the zero default stands for it.
        if (settings == default) settings = new GroundCoreSettings();
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
        Vector2 carry = commanded;

        Tick tick;
        using (IPhysicsQueryLease? lease = leases?.AcquireQueryReadLease())
        {
            var context = new Context(groundHeight, groundNormal, world, lease, t, settings, gate, dt);
            if (s.Grounded)
                tick = Ground(context, feet, commanded * dt);
            else if (!committedFlight && SlideCore.InContact(feet, t, settings, gate, groundHeight, groundNormal,
                         world, lease, out SupportSample support))
                tick = Slide(context, s, feet, intent, support, out carry, out commanded);
            else
                tick = Air(context, s, feet, commanded);
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
        Vector2 achieved = tick.Achieved;
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
            CommandedVelocity = commanded,
            HorizontalVelocity = CharacterMovement.ClipCarryToAchieved(carry, commanded, achieved, dt),
            Commitment = CharacterMovement.FinishCommitmentTick(s.Commitment, launched, grounded, s.Position, end,
                dt),
        };
        return Finite(result) ? result : state with
        {
            LandingImpactSpeed = 0f,
            SupportGranted = false,
            StepDeltaY = 0f,
            Commitment = result.Commitment,
        };
    }

    /// <summary>The tick's queries and the knobs every branch reads.</summary>
    readonly record struct Context(Func<float, float, float> GroundHeight, Func<float, float, Vector3>? GroundNormal,
        IPhysicsWorld? World, IPhysicsQueryLease? Lease, MoveTuning Tuning, GroundCoreSettings Settings, float Gate,
        float Dt);

    /// <summary>What a branch did. <see cref="FallSpeed"/> is the vertical speed a landing erased, negative
    /// downward, and zero without a landing.</summary>
    readonly record struct Tick(Vector3 Feet, float VerticalVelocity, bool Grounded, Vector2 Achieved,
        float FallSpeed);

    // A grounded tick. Walkable footing keeps the ground, and a held start keeps it too, because the body did not
    // move. No support leaves the ground at the reached position with no vertical speed, and steep support starts a
    // slide next tick.
    static Tick Ground(in Context c, Vector3 feet, Vector2 displacement)
    {
        GroundStepResult step = GroundCore.Step(feet, displacement, c.Dt, c.Tuning, c.Settings, c.GroundHeight,
            c.GroundNormal, c.World, c.Lease, c.Gate);
        return new Tick(step.Feet, 0f, Keeps(step.Footing), step.Achieved, 0f);
    }

    static bool Keeps(GroundFooting footing) => footing is GroundFooting.Walkable or GroundFooting.Held;

    // A tick that starts on steep support. The carry is the slide's own velocity, and the drive adds the steer's
    // contour part, which moved the body but never enters the carry.
    static Tick Slide(in Context c, in MoveState s, Vector3 feet, Vector2 intent, in SupportSample support,
        out Vector2 carry, out Vector2 drive)
    {
        SlideStepResult slide = SlideCore.Step(feet, s.HorizontalVelocity, s.VerticalVelocity, intent, c.Dt,
            c.Tuning, c.Settings, c.Gate, support, c.GroundHeight, c.GroundNormal, c.World, c.Lease);
        carry = slide.HorizontalVelocity;
        drive = carry + Contour(support.Normal, intent);
        return slide.Outcome switch
        {
            SlideOutcome.Landed => new Tick(slide.Feet, 0f, true, slide.Achieved, -slide.ImpactSpeed),
            // A wedge swallows the descent the body carried into the tick.
            SlideOutcome.Wedged => new Tick(slide.Feet, slide.VerticalVelocity, true, slide.Achieved,
                s.VerticalVelocity),
            _ => new Tick(slide.Feet, slide.VerticalVelocity, false, slide.Achieved, 0f),
        };
    }

    // The steer's part along the contour of a plane with this normal, as the slide core moves it.
    static Vector2 Contour(Vector3 normal, Vector2 steer)
    {
        var across = new Vector2(normal.X, normal.Z);
        float squared = across.LengthSquared();
        if (!(squared > FlatNormalSquared) || !float.IsFinite(squared)) return Vector2.Zero;
        Vector2 down = across / MathF.Sqrt(squared);
        var contour = new Vector2(-down.Y, down.X);
        return Vector2.Dot(steer, contour) * contour;
    }

    // A tick without footing. A landing hands the rest of the tick's horizontal displacement to the ground core.
    static Tick Air(in Context c, in MoveState s, Vector3 feet, Vector2 velocity)
    {
        AirStepResult air = AirPass.Step(feet, velocity, s.VerticalVelocity, c.Dt, c.Tuning.Gravity, c.Tuning,
            c.Settings, c.Gate, c.GroundHeight, c.GroundNormal, c.World, c.Lease);
        if (air.Outcome != AirOutcome.Landed)
            return new Tick(air.Feet, air.VerticalVelocity, false, air.Achieved, 0f);
        if (air.Remaining == Vector2.Zero)
            return new Tick(air.Feet, 0f, true, air.Achieved, -air.FallSpeedAtContact);
        GroundStepResult step = GroundCore.Step(air.Feet, air.Remaining, c.Dt, c.Tuning, c.Settings, c.GroundHeight,
            c.GroundNormal, c.World, c.Lease, c.Gate);
        return new Tick(step.Feet, 0f, Keeps(step.Footing), air.Achieved + step.Achieved, -air.FallSpeedAtContact);
    }

    static bool Finite(in MoveState s) =>
        float.IsFinite(s.Position.X) && float.IsFinite(s.Position.Y) && float.IsFinite(s.Position.Z) &&
        float.IsFinite(s.VerticalVelocity) && float.IsFinite(s.HorizontalVelocity.X) &&
        float.IsFinite(s.HorizontalVelocity.Y) && float.IsFinite(s.FacingYaw);
}
