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

    // Builds the Bepu constraint(s) for one seam description. Writes the created Bepu handles into `handles`
    // (a caller-owned buffer of length >= 3) and returns how many were written.
    internal static int Build(BepuSim sim, in ConstraintDescription d, in Resolved r, Span<BepuConstraintHandle> handles)
    {
        SpringSettings spring = ToSpring(d.Stiffness, d.DampingRatio);
        return d.Kind switch
        {
            ConstraintKind.BallSocket => BuildBallSocket(sim, d, r, spring, handles),
            ConstraintKind.Hinge      => BuildHinge(sim, d, r, spring, handles),
            ConstraintKind.Slider     => BuildSlider(sim, d, r, spring, handles),
            ConstraintKind.Distance   => BuildDistance(sim, d, r, spring, handles),
            ConstraintKind.Weld       => BuildWeld(sim, d, r, spring, handles),
            _ => throw new NotSupportedException($"ConstraintKind '{d.Kind}' is not supported by the Bepu backend."),
        };
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
