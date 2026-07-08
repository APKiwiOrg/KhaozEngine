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

/// <summary>One end of a constraint: either a dynamic body (by handle) or a fixed world-space anchor. A
/// world-space anchor is realised by the backend as an infinite-mass kinematic body at that pose, which is the
/// clean way BepuPhysics 2.4 models a static constraint side (its constraint types are all two-body, and it has
/// no one-body position joints). Build one with <see cref="OnBody"/> or <see cref="AtWorld(Pose)"/>.</summary>
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
}
