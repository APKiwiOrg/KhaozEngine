using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.SegmentRig;

namespace KhaozEngine.Tests.SegmentRig;

/// <summary>
/// The HELD PIECE half of these suites: the grips a thing in a fist sits at, and the neutral shapes the
/// stroke tests measure.
/// </summary>
/// <remarks>
/// None of this belongs in the package. A grip orientation is a property of a MESH, so it is content: how far
/// a blade leans out of a fist is a question about the model, not about the rig. And the tool shapes below
/// are a test's own measuring stick, sampled off the kind of geometry a game authors rather than off anything
/// the package knows.
/// <para>The two socket ROTATIONS used to be transcribed here, because they are size-free and shape-free and
/// the package had nowhere to put them. It does now: <see cref="SegmentSockets.Wrist"/> and
/// <see cref="SegmentSockets.OffTurn"/> are what the <c>RightWrist</c> and <c>LeftWrist</c> channels MEAN,
/// and everything below reads them off the package.</para>
/// </remarks>
static class TestHeldPieces
{
    /// <summary>How far forward a SHORT-HAFTED tool leans out of the hand, radians about the body's local x.
    /// About thirty-four degrees, which is what lifts a haft off the forearm it hangs beside.</summary>
    public const float ToolTiltRadians = 0.6f;

    /// <summary>How far forward a LONG blade leans out of the hand, radians, and its own number rather than
    /// the tool's: at the shared lean a blade hangs all but upright and reads as carried rather than
    /// held.</summary>
    public const float BladeTiltRadians = 0.95f;

    /// <summary>A quarter turn about a blade's OWN length, which is what puts the edge forward rather than
    /// the flat.</summary>
    public const float BladeRollRadians = MathF.PI / 2f;

    /// <summary>A quarter turn about the BODY's up axis, which turns a plate's face from forward to out to
    /// the character's left, where a carried one hangs.</summary>
    public const float PlateTurnRadians = MathF.PI / 2f;

    /// <summary>How far the turned plate is pulled back IN toward the arm, metres along the hand's own
    /// x.</summary>
    public const float PlateInsetMetres = 0.025f;

    /// <summary>The orientation a short-hafted TOOL sits in the hand at: a lean toward the way the body
    /// faces.</summary>
    public static Matrix4x4 ToolGrip => Matrix4x4.CreateRotationX(ToolTiltRadians);

    /// <summary>The orientation a BLADE sits in the hand at: a quarter turn about its own length while it is
    /// still vertical, then the lean. The ORDER is the content: row-vector maths applies the roll first.
    /// </summary>
    public static Matrix4x4 BladeGrip =>
        Matrix4x4.CreateRotationY(BladeRollRadians) * Matrix4x4.CreateRotationX(BladeTiltRadians);

    /// <summary>The orientation a PLATE sits in the off hand at: the quarter turn about the body's up axis,
    /// then the small shift back in toward the arm along the hand's own x.</summary>
    public static Matrix4x4 PlateGrip =>
        Matrix4x4.CreateRotationY(PlateTurnRadians)
        * Matrix4x4.CreateTranslation(-PlateInsetMetres, 0f, 0f);

    /// <summary>The WEAPON arm's two transforms at a set of angles, in BODY space, through the chain a draw
    /// composes: the shoulder's pitch and yaw out to the shoulder joint, then the elbow out to the elbow
    /// joint.</summary>
    public static (Matrix4x4 Upper, Matrix4x4 Forearm) WeaponArm(BodyRig rig, float shoulder, float elbow,
        float yaw)
    {
        ArgumentNullException.ThrowIfNull(rig);
        Matrix4x4 upper = BodyRig.Limb(rig.RightShoulder, shoulder, yaw);
        return (upper, rig.Elbow(elbow) * upper);
    }

    /// <summary>A piece held in the WEAPON hand, in body space, through the package's own socket chain: its
    /// own grip, the wrist that hand is bent to, the hand socket down the forearm, then the arm.</summary>
    public static Matrix4x4 HeldInWeaponHand(BodyRig rig, in Matrix4x4 grip, float shoulder, float elbow,
        float yaw, float wrist)
    {
        ArgumentNullException.ThrowIfNull(rig);
        Matrix4x4 forearm = WeaponArm(rig, shoulder, elbow, yaw).Forearm;
        return SegmentSockets.Held(rig, grip, forearm, tip: wrist);
    }

    /// <summary>The PLATE's world transform off a finished composition, the chain a draw builds for the off
    /// hand: the grip, the socket down the forearm, then the off hand's own turn about the body's vertical.
    /// </summary>
    /// <param name="at">A composition, from <see cref="HumanoidSkeleton.Compose"/>.</param>
    /// <param name="leftWrist">The pose's <c>LeftWrist</c>. Zero is the plate exactly where it hangs.</param>
    public static Matrix4x4 HeldInOffHand(ReadOnlySpan<Matrix4x4> at, float leftWrist = 0f) =>
        SegmentSockets.Held(BodyRig.Human, PlateGrip, at[HumanoidSkeleton.ForearmLeft], turn: leftWrist);

    /// <summary>A person standing at the origin facing engine +z, through the real piece chain.</summary>
    public static Matrix4x4[] Standing(in WalkPose walk)
    {
        var at = new Matrix4x4[HumanoidSkeleton.PieceCount];
        HumanoidSkeleton.Compose(BodyRig.Human, new BodyPose(Vector3.Zero, 0f), walk, at);
        return at;
    }

    /// <summary>The TOOL's head, in the piece's own frame: the centre of the block ahead of the haft. The kind
    /// of geometry a short-hafted tool carries, and the number the chop's own constants are tuned
    /// against.</summary>
    public static readonly Vector3 ToolHead = new(0f, 0.3375f, 0.0925f);

    /// <summary>The near corner of the head's own BOX in that frame: everything on the tool ahead of the
    /// haft. The clearance checks sample its eight corners rather than the centre, because a centre point can
    /// miss an arm a 0.155 m blade is already inside.</summary>
    public static readonly Vector3 ToolHeadMin = new(-0.026f, 0.285f, 0.03f);

    /// <summary>The far corner of that box.</summary>
    public static readonly Vector3 ToolHeadMax = new(0.026f, 0.385f, 0.185f);

    /// <summary>Half the haft, metres: a shaft 0.038 m square, so a point on the haft's AXIS carries this
    /// much wood around it and an axis-to-axis clearance is exactly this much too generous.</summary>
    public const float HaftHalfWidthMetres = 0.019f;

    /// <summary>The pommel's base, metres BELOW the grip. Nothing on the tool reaches further back down the
    /// arm.</summary>
    public const float PommelBaseMetres = -0.055f;

    /// <summary>Half the fist, metres: a 0.09 m box centred on the grip, so the forearm PIECE ends here and
    /// the haft below this point is inside the hand that holds it.</summary>
    public const float FistHalfMetres = 0.045f;

    /// <summary>A long BLADE's point in its own frame, 0.625 m up the piece's local y.</summary>
    public static readonly Vector3 BladePoint = new(0f, 0.625f, 0f);

    /// <summary>Every point of the tool a clearance is measured at, in the space
    /// <paramref name="tool"/> is composed in: samples up the haft's own axis from <paramref name="from"/> to
    /// the head, then the head box's eight corners. The start is a parameter rather than a filter over one
    /// shared grid, so the first sample lands exactly on it.</summary>
    public static IEnumerable<Vector3> ToolPoints(Matrix4x4 tool, float from)
    {
        const int steps = 12;
        for (int i = 0; i <= steps; i++)
        {
            float along = from + ((ToolHead.Y - from) * i / steps);
            yield return Vector3.Transform(new Vector3(0f, along, 0f), tool);
        }

        for (int corner = 0; corner < 8; corner++)
        {
            var local = new Vector3(
                (corner & 1) == 0 ? ToolHeadMin.X : ToolHeadMax.X,
                (corner & 2) == 0 ? ToolHeadMin.Y : ToolHeadMax.Y,
                (corner & 4) == 0 ? ToolHeadMin.Z : ToolHeadMax.Z);
            yield return Vector3.Transform(local, tool);
        }
    }

    /// <summary>The distance from a point to a segment, metres.</summary>
    public static float ToSegment(Vector3 point, Vector3 from, Vector3 to)
    {
        Vector3 along = to - from;
        float length = along.LengthSquared();
        float t = length <= 0f ? 0f : Math.Clamp(Vector3.Dot(point - from, along) / length, 0f, 1f);
        return Vector3.Distance(point, from + (along * t));
    }
}
