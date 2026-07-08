using System;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Constraints;
using KhaozEngine.Physics;

using BepuSim = BepuPhysics.Simulation;
using BepuConstraintHandle = BepuPhysics.ConstraintHandle;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Maps a seam <see cref="ConstraintDescription"/> onto the verified BepuPhysics 2.4 constraint types.
/// Keeps every BepuPhysics constraint type confined to this package. A single seam constraint may expand to more
/// than one Bepu constraint (a slider is a point-on-line + angular-servo + linear-axis-limit trio), so
/// <see cref="Build"/> returns every Bepu handle it created; the world tracks them together and removes them as a
/// unit.
///
/// <para>Bepu 2.4 has no one-body position joints (only <c>OneBodyAngularMotor</c>/<c>OneBodyAngularServo</c>),
/// so a world-space static anchor end is NOT a one-body constraint: it is modelled as an infinite-mass, shapeless
/// kinematic body pinned at the anchor pose (created by the world before calling this factory; shapeless so it
/// stays out of the broadphase and is invisible to queries). Every joint here is therefore a two-body Bepu
/// constraint, whether the second end is dynamic or a kinematic anchor.</para>
///
/// <para>Default spring: when the description leaves <see cref="ConstraintDescription.Stiffness"/> /
/// <see cref="ConstraintDescription.DampingRatio"/> at 0, this applies
/// <see cref="ConstraintDescription.DefaultStiffnessHz"/> (30 Hz) critically damped
/// (<see cref="ConstraintDescription.DefaultDampingRatio"/> = 1.0). Bepu's <see cref="SpringSettings"/> ctor
/// already takes the natural frequency in Hz and the plain damping ratio (it stores AngularFrequency = 2*pi*Hz
/// and TwiceDampingRatio = 2*ratio internally), so the seam values pass straight through.</para></summary>
internal static class ConstraintFactory
{
    // Two ends already resolved to Bepu bodies plus their current world poses. AnchorA/B and axes are still in
    // each body's LOCAL frame; the add-time relative pose (needed by Weld / Slider / Hinge-limit) is derived here
    // from PoseA/PoseB.
    internal readonly record struct Resolved(
        BodyHandle HandleA, BodyHandle HandleB, RigidPose PoseA, RigidPose PoseB);

    // Everything needed to re-describe a motor/servo Bepu constraint for a NEW target WITHOUT rebuilding the
    // joint or allocating. The world stores one of these per powered constraint (the LIVE Bepu handle of the
    // motor/servo element plus its fixed geometry + settings); SetConstraintTarget stack-builds the matching Bepu
    // description with the new target and calls Solver.ApplyDescription (verified allocation-free). Kind selects
    // which Bepu description to rebuild; the geometry fields are those the chosen kind reads (the rest are unused).
    internal readonly record struct MotorState(
        ConstraintMotor Kind,
        BepuConstraintHandle Handle,
        Vector3 AxisOrOffsetA,   // hinge axis (velocity motor), or LocalOffsetA (slider/distance)
        Vector3 OffsetB,         // LocalOffsetB (slider/distance)
        Vector3 Axis,            // slider axis (LocalAxis / LocalPlaneNormal)
        Quaternion BasisA,       // hinge servo TwistServo basis (local Z = hinge axis, see BasisFromAxis)
        Quaternion BasisB,
        SpringSettings Spring,
        ServoSettings Servo,     // servo caps (max speed / force); for a motor, only MaximumForce is used
        MotorSettings Motor);    // motor caps (max force + softness); for a servo, unused

    // Builds the Bepu constraint(s) for one seam description. Writes the created Bepu handles into `handles`
    // (a caller-owned buffer of length >= 4: a slider is 3 and a motor/servo adds a 4th) and returns how many were
    // written. If the description carries a powered drive (d.Motor != None), the drive's Bepu constraint is the
    // LAST handle written and `motor` captures its re-description state; otherwise `motor` is default (Kind None).
    internal static int Build(BepuSim sim, in ConstraintDescription d, in Resolved r, Span<BepuConstraintHandle> handles, out MotorState motor)
    {
        SpringSettings spring = ToSpring(d.Stiffness, d.DampingRatio);
        int count = d.Kind switch
        {
            ConstraintKind.BallSocket => BuildBallSocket(sim, d, r, spring, handles),
            ConstraintKind.Hinge      => BuildHinge(sim, d, r, spring, handles),
            ConstraintKind.Slider     => BuildSlider(sim, d, r, spring, handles),
            ConstraintKind.Distance   => BuildDistance(sim, d, r, spring, handles),
            ConstraintKind.Weld       => BuildWeld(sim, d, r, spring, handles),
            _ => throw new NotSupportedException($"ConstraintKind '{d.Kind}' is not supported by the Bepu backend."),
        };

        motor = default;
        if (d.Motor != ConstraintMotor.None)
            count += BuildMotor(sim, d, r, spring, handles[count..], out motor);
        return count;
    }

    private static int BuildBallSocket(BepuSim sim, in ConstraintDescription d, in Resolved r, SpringSettings spring, Span<BepuConstraintHandle> handles)
    {
        var ballSocket = new BallSocket
        {
            LocalOffsetA = d.AnchorA,
            LocalOffsetB = d.AnchorB,
            SpringSettings = spring,
        };
        handles[0] = sim.Solver.Add(r.HandleA, r.HandleB, in ballSocket);
        return 1;
    }

    private static int BuildHinge(BepuSim sim, in ConstraintDescription d, in Resolved r, SpringSettings spring, Span<BepuConstraintHandle> handles)
    {
        Vector3 axisA = SafeNormalize(d.AxisA, Vector3.UnitY);
        Vector3 axisB = SafeNormalize(d.AxisB, Vector3.UnitY);
        // Bepu's Hinge already fuses the point-to-point pin and the axis-parallel angular constraint, so a single
        // Hinge gives the revolute joint. Its own axes live in each body's local frame, exactly as the seam supplies.
        var hinge = new Hinge
        {
            LocalOffsetA = d.AnchorA,
            LocalHingeAxisA = axisA,
            LocalOffsetB = d.AnchorB,
            LocalHingeAxisB = axisB,
            SpringSettings = spring,
        };
        handles[0] = sim.Solver.Add(r.HandleA, r.HandleB, in hinge);
        int count = 1;

        if (d.HasAngularLimit)
        {
            // Clamp the swing about the hinge axis with a TwistLimit. Its LocalBasisA/B encode the reference frame
            // whose twist (rotation about the hinge axis) is measured; build a basis whose local X is the hinge
            // axis so the twist angle is the hinge angle, then offset the min/max by the add-time relative twist
            // so the limits are measured from the joint's rest angle (0 rad = the pose at add time).
            Quaternion basisA = BasisFromAxis(axisA);
            Quaternion basisB = BasisFromAxis(axisB);
            var twistLimit = new TwistLimit
            {
                LocalBasisA = basisA,
                LocalBasisB = basisB,
                MinimumAngle = d.MinAngle,
                MaximumAngle = d.MaxAngle,
                // A hard end-stop wants a stiffer spring than the joint pin so a fast swing does not overshoot the
                // limit by much. Use at least 60 Hz (or the caller's higher stiffness): the pin can stay compliant
                // while the stop bites. Still critically damped by default.
                SpringSettings = StiffenLimit(spring),
            };
            handles[count++] = sim.Solver.Add(r.HandleA, r.HandleB, in twistLimit);
        }
        return count;
    }

    private static int BuildSlider(BepuSim sim, in ConstraintDescription d, in Resolved r, SpringSettings spring, Span<BepuConstraintHandle> handles)
    {
        Vector3 axis = SafeNormalize(d.AxisA, Vector3.UnitY);
        // A prismatic joint = keep B's anchor on the line through A's anchor along the axis (PointOnLineServo,
        // removes the two off-axis translation DOF) + lock relative orientation (AngularServo, removes all 3
        // rotational DOF) + clamp travel along the axis (LinearAxisLimit). The one remaining DOF is translation
        // along the axis between the limits.
        var onLine = new PointOnLineServo
        {
            LocalOffsetA = d.AnchorA,
            LocalOffsetB = d.AnchorB,
            LocalDirection = axis,
            ServoSettings = ServoSettings.Default,
            SpringSettings = spring,
        };
        handles[0] = sim.Solver.Add(r.HandleA, r.HandleB, in onLine);

        // Lock relative rotation to whatever it is at add time (target = current relative orientation of B in A).
        Quaternion relOrientation = RelativeOrientation(r.PoseA, r.PoseB);
        var angular = new AngularServo
        {
            TargetRelativeRotationLocalA = relOrientation,
            SpringSettings = spring,
            ServoSettings = ServoSettings.Default,
        };
        handles[1] = sim.Solver.Add(r.HandleA, r.HandleB, in angular);

        var limit = new LinearAxisLimit
        {
            LocalOffsetA = d.AnchorA,
            LocalOffsetB = d.AnchorB,
            LocalAxis = axis,
            MinimumOffset = d.MinOffset,
            MaximumOffset = d.MaxOffset,
            SpringSettings = spring,
        };
        handles[2] = sim.Solver.Add(r.HandleA, r.HandleB, in limit);
        return 3;
    }

    private static int BuildDistance(BepuSim sim, in ConstraintDescription d, in Resolved r, SpringSettings spring, Span<BepuConstraintHandle> handles)
    {
        var limit = new DistanceLimit
        {
            LocalOffsetA = d.AnchorA,
            LocalOffsetB = d.AnchorB,
            MinimumDistance = d.MinDistance,
            MaximumDistance = d.MaxDistance,
            SpringSettings = spring,
        };
        handles[0] = sim.Solver.Add(r.HandleA, r.HandleB, in limit);
        return 1;
    }

    private static int BuildWeld(BepuSim sim, in ConstraintDescription d, in Resolved r, SpringSettings spring, Span<BepuConstraintHandle> handles)
    {
        // Weld holds B rigid relative to A at the add-time relative pose. LocalOffset is B's origin expressed in
        // A's local frame; LocalOrientation is B's orientation in A's local frame. Both are derived from the two
        // current world poses so welding "captures" the current relative transform (per the seam contract).
        Quaternion invA = Quaternion.Conjugate(r.PoseA.Orientation);
        Vector3 localOffset = Vector3.Transform(r.PoseB.Position - r.PoseA.Position, invA);
        Quaternion localOrientation = RelativeOrientation(r.PoseA, r.PoseB);
        var weld = new Weld
        {
            LocalOffset = localOffset,
            LocalOrientation = localOrientation,
            SpringSettings = spring,
        };
        handles[0] = sim.Solver.Add(r.HandleA, r.HandleB, in weld);
        return 1;
    }

    // Adds the powered-drive Bepu constraint for a joint that carries one (d.Motor != None) and captures the state
    // needed to retarget it allocation-free. Validates the drive matches the joint kind (a hinge drive on a slider
    // throws). Returns 1 (one drive handle written to handles[0]).
    private static int BuildMotor(BepuSim sim, in ConstraintDescription d, in Resolved r, SpringSettings spring, Span<BepuConstraintHandle> handles, out MotorState motor)
    {
        ServoSettings servo = ToServo(d.MotorMaxSpeed, d.MotorMaxForce);
        MotorSettings motorSettings = ToMotor(d.MotorMaxForce);

        switch (d.Motor)
        {
            case ConstraintMotor.HingeVelocity:
            {
                RequireKind(d, ConstraintKind.Hinge);
                Vector3 axisA = SafeNormalize(d.AxisA, Vector3.UnitY);
                // AngularAxisMotor drives the relative angular velocity of the two bodies about a body-A-local axis
                // toward TargetVelocity. LocalAxisA is the hinge axis in A's frame.
                var m = new AngularAxisMotor { LocalAxisA = axisA, TargetVelocity = d.MotorTarget, Settings = motorSettings };
                handles[0] = sim.Solver.Add(r.HandleA, r.HandleB, in m);
                motor = new MotorState(d.Motor, handles[0], axisA, default, default, default, default, spring, servo, motorSettings);
                return 1;
            }
            case ConstraintMotor.HingeAngle:
            {
                RequireKind(d, ConstraintKind.Hinge);
                Vector3 axisA = SafeNormalize(d.AxisA, Vector3.UnitY);
                Vector3 axisB = SafeNormalize(d.AxisB, Vector3.UnitY);
                // TwistServo drives the twist of the relative rotation about the basis's LOCAL Z axis to TargetAngle,
                // the SAME basis convention TwistLimit uses (BasisFromAxis builds local-Z = hinge axis). Verified
                // empirically against a live Z-axis hinge sim: with this basis the servo reaches the target angle
                // exactly across every speed cap (2..inf rad/s); an early local-X draft only worked for a Y-axis
                // hinge by coincidence and spun a Z-axis hinge wildly. Both bodies use the same construction so the
                // rest twist is 0 when their hinge axes align.
                Quaternion basisA = BasisFromAxis(axisA);
                Quaternion basisB = BasisFromAxis(axisB);
                var m = new TwistServo { LocalBasisA = basisA, LocalBasisB = basisB, TargetAngle = d.MotorTarget, SpringSettings = spring, ServoSettings = servo };
                handles[0] = sim.Solver.Add(r.HandleA, r.HandleB, in m);
                motor = new MotorState(d.Motor, handles[0], default, default, default, basisA, basisB, spring, servo, motorSettings);
                return 1;
            }
            case ConstraintMotor.SliderVelocity:
            {
                RequireKind(d, ConstraintKind.Slider);
                Vector3 axis = SafeNormalize(d.AxisA, Vector3.UnitY);
                var m = new LinearAxisMotor { LocalOffsetA = d.AnchorA, LocalOffsetB = d.AnchorB, LocalAxis = axis, TargetVelocity = d.MotorTarget, Settings = motorSettings };
                handles[0] = sim.Solver.Add(r.HandleA, r.HandleB, in m);
                motor = new MotorState(d.Motor, handles[0], d.AnchorA, d.AnchorB, axis, default, default, spring, servo, motorSettings);
                return 1;
            }
            case ConstraintMotor.SliderPosition:
            {
                RequireKind(d, ConstraintKind.Slider);
                Vector3 axis = SafeNormalize(d.AxisA, Vector3.UnitY);
                float target = Math.Clamp(d.MotorTarget, d.MinOffset, d.MaxOffset);
                // LinearAxisServo drives B's offset from A along LocalPlaneNormal to TargetOffset (its field is named
                // "PlaneNormal" but empirically it IS the drive axis: a target of 2 along +Y parks the body at +2).
                var m = new LinearAxisServo { LocalOffsetA = d.AnchorA, LocalOffsetB = d.AnchorB, LocalPlaneNormal = axis, TargetOffset = target, ServoSettings = servo, SpringSettings = spring };
                handles[0] = sim.Solver.Add(r.HandleA, r.HandleB, in m);
                motor = new MotorState(d.Motor, handles[0], d.AnchorA, d.AnchorB, axis, default, default, spring, servo, motorSettings);
                return 1;
            }
            case ConstraintMotor.DistanceLength:
            {
                RequireKind(d, ConstraintKind.Distance);
                var m = new DistanceServo { LocalOffsetA = d.AnchorA, LocalOffsetB = d.AnchorB, TargetDistance = MathF.Max(0f, d.MotorTarget), ServoSettings = servo, SpringSettings = spring };
                handles[0] = sim.Solver.Add(r.HandleA, r.HandleB, in m);
                motor = new MotorState(d.Motor, handles[0], d.AnchorA, d.AnchorB, default, default, default, spring, servo, motorSettings);
                return 1;
            }
            default:
                throw new NotSupportedException($"ConstraintMotor '{d.Motor}' is not supported by the Bepu backend.");
        }
    }

    // Re-applies a NEW target to a live motor/servo constraint, allocation-free (stack-built description +
    // Solver.ApplyDescription, verified to allocate zero bytes). Called by the world's SetConstraintTarget. The
    // stored MotorState carries the fixed geometry + settings; only the target value changes. ApplyDescription
    // wakes the involved bodies so a retargeted drive on a sleeping joint takes effect.
    internal static void ApplyTarget(BepuSim sim, in MotorState s, float target)
    {
        switch (s.Kind)
        {
            case ConstraintMotor.HingeVelocity:
            {
                var m = new AngularAxisMotor { LocalAxisA = s.AxisOrOffsetA, TargetVelocity = target, Settings = s.Motor };
                sim.Solver.ApplyDescription(s.Handle, in m);
                break;
            }
            case ConstraintMotor.HingeAngle:
            {
                var m = new TwistServo { LocalBasisA = s.BasisA, LocalBasisB = s.BasisB, TargetAngle = target, SpringSettings = s.Spring, ServoSettings = s.Servo };
                sim.Solver.ApplyDescription(s.Handle, in m);
                break;
            }
            case ConstraintMotor.SliderVelocity:
            {
                var m = new LinearAxisMotor { LocalOffsetA = s.AxisOrOffsetA, LocalOffsetB = s.OffsetB, LocalAxis = s.Axis, TargetVelocity = target, Settings = s.Motor };
                sim.Solver.ApplyDescription(s.Handle, in m);
                break;
            }
            case ConstraintMotor.SliderPosition:
            {
                var m = new LinearAxisServo { LocalOffsetA = s.AxisOrOffsetA, LocalOffsetB = s.OffsetB, LocalPlaneNormal = s.Axis, TargetOffset = target, ServoSettings = s.Servo, SpringSettings = s.Spring };
                sim.Solver.ApplyDescription(s.Handle, in m);
                break;
            }
            case ConstraintMotor.DistanceLength:
            {
                var m = new DistanceServo { LocalOffsetA = s.AxisOrOffsetA, LocalOffsetB = s.OffsetB, TargetDistance = MathF.Max(0f, target), ServoSettings = s.Servo, SpringSettings = s.Spring };
                sim.Solver.ApplyDescription(s.Handle, in m);
                break;
            }
            default:
                throw new NotSupportedException($"ConstraintMotor '{s.Kind}' cannot be retargeted.");
        }
    }

    private static void RequireKind(in ConstraintDescription d, ConstraintKind required)
    {
        if (d.Kind != required)
            throw new ArgumentException(
                $"Motor '{d.Motor}' requires a {required} joint but the constraint is a {d.Kind}.", nameof(d));
    }

    // --- helpers ------------------------------------------------------------

    // Bepu's SpringSettings(frequency, dampingRatio) ctor takes the natural frequency in Hz (it stores
    // AngularFrequency = 2*pi*frequency) and the plain damping ratio (it stores TwiceDampingRatio = 2*ratio),
    // which is exactly the seam's representation, so no manual conversion is needed. A zero on either seam field
    // means "backend default": 30 Hz, critically damped (ratio 1).
    private static SpringSettings ToSpring(float stiffnessHz, float dampingRatio)
    {
        float hz = stiffnessHz > 0f ? stiffnessHz : ConstraintDescription.DefaultStiffnessHz;
        float ratio = dampingRatio > 0f ? dampingRatio : ConstraintDescription.DefaultDampingRatio;
        return new SpringSettings(hz, ratio);
    }

    // A limit end-stop uses at least a 60 Hz spring so a fast swing does not overshoot the clamp much, keeping the
    // caller's frequency if they asked for something stiffer. Damping ratio is preserved.
    private static SpringSettings StiffenLimit(SpringSettings spring)
    {
        const float minLimitHz = 60f;
        float hz = spring.AngularFrequency / (2f * MathF.PI);
        float ratio = spring.TwiceDampingRatio * 0.5f;
        return new SpringSettings(MathF.Max(hz, minLimitHz), ratio);
    }

    private static Vector3 SafeNormalize(Vector3 v, Vector3 fallback)
    {
        float len = v.Length();
        return len > 1e-6f ? v / len : fallback;
    }

    // Servo caps from the seam. A zero max-speed means "backend default" (DefaultServoMaxSpeed = 2), so a servo
    // eases toward its target rather than snapping; a zero max-force means DefaultMotorMaxForce. BaseSpeed 0: the
    // servo has no minimum crawl speed, it slows smoothly as it nears the target.
    private static ServoSettings ToServo(float maxSpeed, float maxForce)
    {
        float speed = maxSpeed > 0f ? maxSpeed : ConstraintDescription.DefaultServoMaxSpeed;
        float force = maxForce > 0f ? maxForce : ConstraintDescription.DefaultMotorMaxForce;
        return new ServoSettings(speed, 0f, force);
    }

    // Motor caps from the seam. A pure velocity motor has no speed cap (its target IS the speed), only a force cap
    // (0 = DefaultMotorMaxForce). Softness 0 keeps the motor maximally stiff (it applies full force up to the cap
    // to chase the target velocity); a positive softness would let it slip.
    private static MotorSettings ToMotor(float maxForce)
    {
        float force = maxForce > 0f ? maxForce : ConstraintDescription.DefaultMotorMaxForce;
        return new MotorSettings(force, 0f);
    }

    // Relative orientation of B in A's local frame: conjugate(A) * B.
    private static Quaternion RelativeOrientation(in RigidPose a, in RigidPose b)
        => Quaternion.Normalize(Quaternion.Concatenate(b.Orientation, Quaternion.Conjugate(a.Orientation)));

    // A quaternion basis whose local Z axis is `axis` (unit). BepuPhysics TwistLimit / TwistServo measure the
    // twist of the relative rotation about the basis's LOCAL Z axis (verified empirically against a live hinge sim:
    // an identity basis clamps a Z-axis hinge, and aligning basis-Z with an arbitrary hinge axis clamps that hinge;
    // aligning basis-X does nothing). So aligning basis-Z with the hinge axis makes the measured twist the hinge
    // angle. Any consistent perpendicular pair completes the frame; both bodies use the same construction so their
    // rest twist is 0 when their hinge axes align. The basis axes are the COLUMNS of the local->world rotation.
    private static Quaternion BasisFromAxis(Vector3 axis)
    {
        Vector3 z = axis;
        Vector3 helper = MathF.Abs(z.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
        Vector3 x = Vector3.Normalize(Vector3.Cross(helper, z));
        Vector3 y = Vector3.Cross(z, x);
        var m = new Matrix4x4(
            x.X, y.X, z.X, 0f,
            x.Y, y.Y, z.Y, 0f,
            x.Z, y.Z, z.Z, 0f,
            0f, 0f, 0f, 1f);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
    }
}
