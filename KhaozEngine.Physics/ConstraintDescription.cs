using System;
using System.Numerics;

namespace KhaozEngine.Physics;

/// <summary>Which joint a <see cref="ConstraintDescription"/> builds.</summary>
public enum ConstraintKind
{
    /// <summary>A point-to-point joint: the two body-local anchor points are held coincident, rotation free
    /// about that shared point (a shoulder / pendulum pivot). Uses <see cref="ConstraintDescription.AnchorA"/>
    /// and <see cref="ConstraintDescription.AnchorB"/>.</summary>
    BallSocket,

    /// <summary>A hinge (revolute) joint: the anchor points are held coincident AND the two body-local hinge
    /// axes are held parallel, so relative rotation is confined to a single axis (a door, a lid, a knee). Uses
    /// the anchors plus <see cref="ConstraintDescription.AxisA"/>/<see cref="ConstraintDescription.AxisB"/>, and
    /// optionally <see cref="ConstraintDescription.MinAngle"/>/<see cref="ConstraintDescription.MaxAngle"/> to
    /// clamp the swing.</summary>
    Hinge,

    /// <summary>A slider (prismatic) joint: the bodies may only translate along one shared body-local axis, all
    /// rotation and off-axis translation removed (a drawer, a piston, a lift platform on rails). Uses the
    /// anchors plus <see cref="ConstraintDescription.AxisA"/> and the linear travel limits
    /// <see cref="ConstraintDescription.MinOffset"/>/<see cref="ConstraintDescription.MaxOffset"/>.</summary>
    Slider,

    /// <summary>A distance joint: the anchor points are kept between <see cref="ConstraintDescription.MinDistance"/>
    /// and <see cref="ConstraintDescription.MaxDistance"/> apart, free in between (a rope / chain / tether). Set
    /// min == max for a rigid rod. Uses the anchors and the two distances.</summary>
    Distance,

    /// <summary>A weld joint: the two bodies are held in a fixed relative pose, position and orientation both
    /// rigid (glue two crates, attach a fixture). Uses <see cref="ConstraintDescription.AnchorA"/> as the weld
    /// point on body A; the current relative pose at add time is captured as the target.</summary>
    Weld,
}

/// <summary>An optional powered drive layered onto a joint: a MOTOR chases a target velocity (an unbounded
/// door-opener, a patrolling belt), a SERVO chases a target position/angle/length and holds there (a door that
/// stops at 90 degrees, a lift that parks at a floor, a winch that reels to a length). Selected on
/// <see cref="ConstraintDescription.Motor"/>; the live target is set at add time by the drive factory and updated
/// per frame, allocation-free, via <see cref="IPhysicsWorld.SetConstraintTarget"/>. A drive only applies to the
/// joint kind it names; mixing (e.g. a hinge drive on a slider) throws at add time.</summary>
public enum ConstraintMotor
{
    /// <summary>No powered drive: a passive joint (the default).</summary>
    None,

    /// <summary>Hinge velocity motor: spins the hinge toward a target angular velocity (rad/s) about the hinge
    /// axis, force-capped by <see cref="ConstraintDescription.MotorMaxForce"/>. Unbounded rotation unless the hinge
    /// also has an angular limit, which clamps it. Only on <see cref="ConstraintKind.Hinge"/>.</summary>
    HingeVelocity,

    /// <summary>Hinge angle servo: drives the hinge to a target angle (radians, measured from the add-time relative
    /// orientation about the hinge axis) and holds it, speed-capped by <see cref="ConstraintDescription.MotorMaxSpeed"/>
    /// and force-capped by <see cref="ConstraintDescription.MotorMaxForce"/>. Only on <see cref="ConstraintKind.Hinge"/>.</summary>
    HingeAngle,

    /// <summary>Slider velocity motor: drives the slider toward a target linear velocity (m/s) along the slider
    /// axis, force-capped by <see cref="ConstraintDescription.MotorMaxForce"/>. The travel limits still clamp it.
    /// Only on <see cref="ConstraintKind.Slider"/>.</summary>
    SliderVelocity,

    /// <summary>Slider position servo: drives the slider to a target offset (metres along the slider axis, measured
    /// from the add-time anchor separation) and holds it against gravity/load, speed- and force-capped. The target
    /// is clamped to the travel limits. Only on <see cref="ConstraintKind.Slider"/>.</summary>
    SliderPosition,

    /// <summary>Distance servo (winch): drives the anchor separation to a target length (metres) and holds it,
    /// speed-capped by <see cref="ConstraintDescription.MotorMaxSpeed"/> (the reel rate) and force-capped. Shrinking
    /// the target over time reels a hanging body up. Only on <see cref="ConstraintKind.Distance"/>.</summary>
    DistanceLength,
}

/// <summary>One end of a constraint: either a dynamic body (by handle) or a fixed world-space anchor. A
/// world-space anchor is a fixed point in the world the constraint pins the other end against; the backend
/// realises it however it best models a static constraint side, and the anchor is not itself a collidable (it
/// is never hit by a raycast or sweep). Build one with <see cref="OnBody"/> or <see cref="AtWorld(Pose)"/>.</summary>
public readonly record struct ConstraintAttachment
{
    /// <summary>The dynamic body this end attaches to, or null for a fixed world-space anchor.</summary>
    public DynamicBodyHandle? Body { get; private init; }

    /// <summary>The world-space pose of a fixed anchor (used only when <see cref="Body"/> is null). The
    /// constraint's body-local anchor offset is applied relative to this pose.</summary>
    public Pose WorldAnchor { get; private init; }

    /// <summary>Attach this end to a dynamic body. The constraint's anchor offset is body-local to it.</summary>
    public static ConstraintAttachment OnBody(DynamicBodyHandle body) => new() { Body = body, WorldAnchor = Pose.Identity };

    /// <summary>Attach this end to a fixed point in the world at <paramref name="pose"/>. The backend pins a
    /// zero-velocity kinematic body there for the constraint to solve against. Default orientation is identity;
    /// supply an orientation to align a hinge/slider axis anchored to the world.</summary>
    public static ConstraintAttachment AtWorld(Pose pose) => new() { Body = null, WorldAnchor = pose };

    /// <summary>Attach this end to a fixed world point at <paramref name="position"/> (identity orientation).</summary>
    public static ConstraintAttachment AtWorld(Vector3 position) => AtWorld(Pose.At(position));

    /// <summary>True when this end is a fixed world-space anchor rather than a dynamic body.</summary>
    public bool IsWorldAnchor => Body is null;
}

/// <summary>A discriminated description of a joint constraint for <see cref="IPhysicsWorld.AddConstraint"/>.
/// <see cref="Kind"/> selects the joint; the anchors and axes are body-local and only the fields a given kind
/// documents are read. Anchors are offsets in each body's LOCAL frame (metres); axes are unit directions in the
/// same local frame. Prefer the static factory methods (<see cref="BallSocketJoint"/>, <see cref="HingeJoint"/>,
/// <see cref="SliderJoint"/>, <see cref="DistanceJoint"/>, <see cref="WeldJoint"/>) which fill only the fields
/// that kind uses and leave the rest at sane defaults. <see cref="Stiffness"/> and <see cref="DampingRatio"/>
/// tune the joint's spring (see their docs for the defaults and what they mean).</summary>
public readonly record struct ConstraintDescription
{
    /// <summary>Which joint this describes.</summary>
    public ConstraintKind Kind { get; init; }

    /// <summary>The first end of the joint (usually a dynamic body).</summary>
    public ConstraintAttachment A { get; init; }

    /// <summary>The second end of the joint (a dynamic body, or a world-space static anchor).</summary>
    public ConstraintAttachment B { get; init; }

    /// <summary>Body-local anchor offset on end <see cref="A"/> (metres, in A's local frame). Where on body A the
    /// joint attaches. Read by every kind.</summary>
    public Vector3 AnchorA { get; init; }

    /// <summary>Body-local anchor offset on end <see cref="B"/> (metres, in B's local frame). Read by BallSocket,
    /// Hinge, Slider and Distance. (Weld derives B's frame from the add-time relative pose, so it ignores
    /// <see cref="AnchorB"/>.)</summary>
    public Vector3 AnchorB { get; init; }

    /// <summary>Body-local axis on end <see cref="A"/> (unit). For Hinge, the rotation axis; for Slider, the
    /// single permitted translation axis. Ignored by BallSocket, Distance and Weld.</summary>
    public Vector3 AxisA { get; init; }

    /// <summary>Body-local axis on end <see cref="B"/> (unit). For Hinge, the axis on B held parallel to
    /// <see cref="AxisA"/>. Ignored by Slider (which constrains B onto A's axis), BallSocket, Distance and
    /// Weld.</summary>
    public Vector3 AxisB { get; init; }

    /// <summary>Minimum allowed separation between the anchors, metres (Distance only). Zero lets the anchors
    /// touch.</summary>
    public float MinDistance { get; init; }

    /// <summary>Maximum allowed separation between the anchors, metres (Distance only). This is the rope length:
    /// the body hangs freely until it reaches this, then is held. Set equal to <see cref="MinDistance"/> for a
    /// rigid rod.</summary>
    public float MaxDistance { get; init; }

    /// <summary>Whether the Hinge swing is clamped to <see cref="MinAngle"/>/<see cref="MaxAngle"/>. False (the
    /// default) is a free-swinging hinge. Ignored by every non-Hinge kind.</summary>
    public bool HasAngularLimit { get; init; }

    /// <summary>Minimum hinge angle in radians about the hinge axis, measured from the relative orientation at
    /// add time (Hinge, only when <see cref="HasAngularLimit"/>). Must be &lt;= <see cref="MaxAngle"/>.</summary>
    public float MinAngle { get; init; }

    /// <summary>Maximum hinge angle in radians about the hinge axis (Hinge, only when
    /// <see cref="HasAngularLimit"/>).</summary>
    public float MaxAngle { get; init; }

    /// <summary>Minimum offset along the slider axis, metres, measured from the add-time anchor separation
    /// (Slider). The lower travel stop.</summary>
    public float MinOffset { get; init; }

    /// <summary>Maximum offset along the slider axis, metres (Slider). The upper travel stop. Must be
    /// &gt;= <see cref="MinOffset"/>.</summary>
    public float MaxOffset { get; init; }

    /// <summary>The joint's spring stiffness as an undamped natural frequency in Hz (angular frequency /
    /// 2*pi). Higher is stiffer / less compliant. Zero (the default) means "use the backend default"
    /// (<see cref="DefaultStiffnessHz"/> = 30 Hz), a firm joint that resolves within a couple of steps at
    /// 60 Hz without going explosively stiff. Match it to your step rate: a stiffness far above your step
    /// frequency can ring or destabilise. See the backend README for per-use-case recommendations.</summary>
    public float Stiffness { get; init; }

    /// <summary>The joint spring's damping ratio (dimensionless). 1.0 is critically damped (no overshoot, the
    /// firm default). Below 1 is springy / bouncy, above 1 is sluggish. Zero (the default value) means "use the
    /// backend default" (<see cref="DefaultDampingRatio"/> = 1.0). To make a genuinely undamped, oscillating
    /// joint, set a small positive value like 0.05 rather than 0.</summary>
    public float DampingRatio { get; init; }

    /// <summary>The default spring stiffness (30 Hz) the backend applies when <see cref="Stiffness"/> is 0. A
    /// firm, well-behaved joint at a 60 Hz step: it removes constraint error within a couple of steps without
    /// the ringing a much higher frequency invites. Matches the contact spring the dynamics backend uses.</summary>
    public const float DefaultStiffnessHz = 30f;

    /// <summary>The default damping ratio (1.0, critically damped) the backend applies when
    /// <see cref="DampingRatio"/> is 0. No overshoot: the joint settles to its target without bouncing.</summary>
    public const float DefaultDampingRatio = 1f;

    /// <summary>The optional powered drive layered onto this joint (a motor chasing a velocity, or a servo chasing
    /// a position and holding). <see cref="ConstraintMotor.None"/> (the default) is a passive joint. Set it with a
    /// drive factory (<see cref="WithHingeMotor"/>, <see cref="WithHingeServo"/>, <see cref="WithSliderMotor"/>,
    /// <see cref="WithSliderServo"/>, <see cref="WithWinch"/>); the drive must match <see cref="Kind"/> or the add
    /// throws.</summary>
    public ConstraintMotor Motor { get; init; }

    /// <summary>The initial drive target: an angular/linear velocity for a motor, or an angle/offset/length for a
    /// servo (units per <see cref="Motor"/>). Read only when <see cref="Motor"/> is not
    /// <see cref="ConstraintMotor.None"/>. Update it per frame with
    /// <see cref="IPhysicsWorld.SetConstraintTarget"/> (allocation-free).</summary>
    public float MotorTarget { get; init; }

    /// <summary>The drive's maximum force / torque (newtons or newton-metres). Caps how hard the motor/servo pushes
    /// so a light drive cannot fling a heavy load or fight gravity infinitely. Zero (the default) means "use the
    /// backend default" (<see cref="DefaultMotorMaxForce"/>). Set <see cref="float.MaxValue"/> for an uncapped
    /// (physically stiff, potentially explosive) drive.</summary>
    public float MotorMaxForce { get; init; }

    /// <summary>The servo's maximum speed (rad/s for a hinge angle servo, m/s for a slider position servo or a
    /// winch). Caps how fast the servo travels toward its target, so it eases in instead of snapping. Ignored by
    /// the pure velocity motors (their target IS the speed). Zero (the default) means "use the backend default"
    /// (<see cref="DefaultServoMaxSpeed"/>).</summary>
    public float MotorMaxSpeed { get; init; }

    /// <summary>The default drive max force / torque (2000) the backend applies when <see cref="MotorMaxForce"/>
    /// is 0. Enough to move typical game props and hold them against gravity without the numeric explosiveness of
    /// an uncapped drive fighting a stiff constraint. Raise it for heavy loads, lower it for a weak/slippable
    /// motor.</summary>
    public const float DefaultMotorMaxForce = 2000f;

    /// <summary>The default servo max speed (2) the backend applies when <see cref="MotorMaxSpeed"/> is 0: a servo
    /// eases toward its target at up to 2 rad/s or 2 m/s rather than snapping instantly, which keeps it stable and
    /// game-readable. Raise it for a snappier servo, lower it for a slow, deliberate one.</summary>
    public const float DefaultServoMaxSpeed = 2f;

    /// <summary>A ball-socket (point-to-point) joint pinning <paramref name="anchorA"/> on A to
    /// <paramref name="anchorB"/> on B, both body-local. Rotation about the shared point is free.</summary>
    public static ConstraintDescription BallSocketJoint(ConstraintAttachment a, ConstraintAttachment b, Vector3 anchorA, Vector3 anchorB)
        => new() { Kind = ConstraintKind.BallSocket, A = a, B = b, AnchorA = anchorA, AnchorB = anchorB };

    /// <summary>A hinge (revolute) joint. The anchors are pinned coincident and the body-local axes
    /// <paramref name="axisA"/>/<paramref name="axisB"/> are held parallel, leaving one rotational
    /// degree of freedom. Free-swinging (no limit); call <see cref="WithAngularLimit"/> to clamp it.</summary>
    public static ConstraintDescription HingeJoint(ConstraintAttachment a, ConstraintAttachment b, Vector3 anchorA, Vector3 anchorB, Vector3 axisA, Vector3 axisB)
        => new() { Kind = ConstraintKind.Hinge, A = a, B = b, AnchorA = anchorA, AnchorB = anchorB, AxisA = axisA, AxisB = axisB };

    /// <summary>A slider (prismatic) joint along the shared body-local axis <paramref name="axis"/>, with travel
    /// clamped to [<paramref name="minOffset"/>, <paramref name="maxOffset"/>] metres from the add-time
    /// separation. All rotation and off-axis translation is removed.</summary>
    public static ConstraintDescription SliderJoint(ConstraintAttachment a, ConstraintAttachment b, Vector3 anchorA, Vector3 anchorB, Vector3 axis, float minOffset, float maxOffset)
        => new() { Kind = ConstraintKind.Slider, A = a, B = b, AnchorA = anchorA, AnchorB = anchorB, AxisA = axis, MinOffset = minOffset, MaxOffset = maxOffset };

    /// <summary>A distance joint keeping the anchors between <paramref name="minDistance"/> and
    /// <paramref name="maxDistance"/> apart (metres). A rope: min 0, max = rope length. A rigid rod: min ==
    /// max.</summary>
    public static ConstraintDescription DistanceJoint(ConstraintAttachment a, ConstraintAttachment b, Vector3 anchorA, Vector3 anchorB, float minDistance, float maxDistance)
        => new() { Kind = ConstraintKind.Distance, A = a, B = b, AnchorA = anchorA, AnchorB = anchorB, MinDistance = minDistance, MaxDistance = maxDistance };

    /// <summary>A weld joint welding A to B at the current relative pose, with <paramref name="anchorA"/> the
    /// body-local weld point on A. Position and orientation are both held rigid.</summary>
    public static ConstraintDescription WeldJoint(ConstraintAttachment a, ConstraintAttachment b, Vector3 anchorA)
        => new() { Kind = ConstraintKind.Weld, A = a, B = b, AnchorA = anchorA };

    /// <summary>This hinge with a swing limit of [<paramref name="minAngle"/>, <paramref name="maxAngle"/>]
    /// radians about the hinge axis (measured from the add-time relative orientation). No-op semantics on a
    /// non-hinge kind: the limit fields are only read by <see cref="ConstraintKind.Hinge"/>.</summary>
    public ConstraintDescription WithAngularLimit(float minAngle, float maxAngle)
    {
        if (maxAngle < minAngle)
            throw new ArgumentException($"Hinge maxAngle ({maxAngle}) must be >= minAngle ({minAngle}).", nameof(maxAngle));
        return this with { HasAngularLimit = true, MinAngle = minAngle, MaxAngle = maxAngle };
    }

    /// <summary>This description with an explicit spring (<paramref name="stiffnessHz"/> as a natural frequency
    /// in Hz, <paramref name="dampingRatio"/> dimensionless). See <see cref="Stiffness"/>/<see cref="DampingRatio"/>.</summary>
    public ConstraintDescription WithSpring(float stiffnessHz, float dampingRatio)
        => this with { Stiffness = stiffnessHz, DampingRatio = dampingRatio };

    /// <summary>Layer a HINGE VELOCITY MOTOR onto this hinge: it spins the joint toward
    /// <paramref name="targetAngularVelocity"/> (rad/s, about the hinge axis) and keeps spinning. A door-opener, a
    /// turning wheel, a patrol arm. <paramref name="maxTorque"/> caps the drive (0 = <see cref="DefaultMotorMaxForce"/>);
    /// an angular limit on the hinge clamps the rotation, otherwise it spins without end. Change the target speed
    /// per frame with <see cref="IPhysicsWorld.SetConstraintTarget"/>. Use only on a hinge.</summary>
    public ConstraintDescription WithHingeMotor(float targetAngularVelocity, float maxTorque = 0f)
        => this with { Motor = ConstraintMotor.HingeVelocity, MotorTarget = targetAngularVelocity, MotorMaxForce = maxTorque };

    /// <summary>Layer a HINGE ANGLE SERVO onto this hinge: it drives the joint to <paramref name="targetAngle"/>
    /// (radians, measured from the add-time relative orientation about the hinge axis) and holds it there. A door
    /// that opens to exactly 90 degrees, a lever that parks. <paramref name="maxSpeed"/> caps the approach speed
    /// (rad/s, 0 = <see cref="DefaultServoMaxSpeed"/>) and <paramref name="maxTorque"/> caps the drive
    /// (0 = <see cref="DefaultMotorMaxForce"/>). Retarget per frame with
    /// <see cref="IPhysicsWorld.SetConstraintTarget"/>. Use only on a hinge.</summary>
    public ConstraintDescription WithHingeServo(float targetAngle, float maxSpeed = 0f, float maxTorque = 0f)
        => this with { Motor = ConstraintMotor.HingeAngle, MotorTarget = targetAngle, MotorMaxSpeed = maxSpeed, MotorMaxForce = maxTorque };

    /// <summary>Layer a SLIDER VELOCITY MOTOR onto this slider: it drives the joint toward
    /// <paramref name="targetVelocity"/> (m/s along the slider axis). A conveyor, a piston pushing at a rate. The
    /// travel limits still clamp it. <paramref name="maxForce"/> caps the drive (0 = <see cref="DefaultMotorMaxForce"/>).
    /// Retarget per frame with <see cref="IPhysicsWorld.SetConstraintTarget"/>. Use only on a slider.</summary>
    public ConstraintDescription WithSliderMotor(float targetVelocity, float maxForce = 0f)
        => this with { Motor = ConstraintMotor.SliderVelocity, MotorTarget = targetVelocity, MotorMaxForce = maxForce };

    /// <summary>Layer a SLIDER POSITION SERVO onto this slider: it drives the joint to <paramref name="targetOffset"/>
    /// (metres along the slider axis, from the add-time separation) and holds it against gravity/load. A lift that
    /// parks at a floor, a platform that patrols between offsets. <paramref name="maxSpeed"/> caps the approach
    /// (m/s, 0 = <see cref="DefaultServoMaxSpeed"/>), <paramref name="maxForce"/> caps the drive
    /// (0 = <see cref="DefaultMotorMaxForce"/>). The add-time target is clamped to the travel limits. A per-frame
    /// retarget via <see cref="IPhysicsWorld.SetConstraintTarget"/> is not re-clamped: an out-of-range target simply
    /// drives against the physical travel limit, which holds. Use only on a slider.</summary>
    public ConstraintDescription WithSliderServo(float targetOffset, float maxSpeed = 0f, float maxForce = 0f)
        => this with { Motor = ConstraintMotor.SliderPosition, MotorTarget = targetOffset, MotorMaxSpeed = maxSpeed, MotorMaxForce = maxForce };

    /// <summary>Layer a WINCH (distance servo) onto this distance joint: it drives the anchor separation to
    /// <paramref name="targetLength"/> (metres) and holds it. Shrinking the target over frames reels a hanging body
    /// up; growing it lowers the body. <paramref name="maxSpeed"/> is the reel rate (m/s, 0 =
    /// <see cref="DefaultServoMaxSpeed"/>), <paramref name="maxForce"/> caps the pull (0 =
    /// <see cref="DefaultMotorMaxForce"/>). Retarget per frame with <see cref="IPhysicsWorld.SetConstraintTarget"/>.
    /// Use only on a distance joint. The passive min/max band still applies alongside the winch, so keep
    /// <see cref="MaxDistance"/> at or above the longest length you command (and <see cref="MinDistance"/> at or
    /// below the shortest) or the band fights the servo.</summary>
    public ConstraintDescription WithWinch(float targetLength, float maxSpeed = 0f, float maxForce = 0f)
        => this with { Motor = ConstraintMotor.DistanceLength, MotorTarget = targetLength, MotorMaxSpeed = maxSpeed, MotorMaxForce = maxForce };
}
