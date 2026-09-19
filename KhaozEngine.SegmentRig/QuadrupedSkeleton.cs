using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.SegmentRig;

/// <summary>A four-legged body's ORDERED PIECES, and the composition that turns a <see cref="QuadrupedRig"/>
/// plus a pose plus a <see cref="QuadrupedPose"/> into one world transform per piece.
/// <see cref="HumanoidSkeleton"/>'s sibling, built the same way for the same reason.</summary>
/// <remarks>
/// TWO PARENT FRAMES, the two-legged shape one level down. The trunk piece and the head compose inside
/// <see cref="QuadrupedRig.Torso"/>, the whole body with the roll and the breath's rise on it, through the
/// trunk's yaw. The four upper legs compose inside <see cref="QuadrupedRig.Body"/>, the PLAIN body transform,
/// through the SAME yaw, so the hips and shoulders travel with the trunk and the hooves turn under it, and
/// never through the roll or the rise, so a standing body's ribcage lifts and its hooves stay on the ground.
/// Each cannon composes against its own upper leg's finished transform, which makes the carpus and the hock
/// hinges rather than second swings from the shoulder.
/// <para>NOT A SPECIALISATION of the two-legged composer and deliberately not folded into it: see the note on
/// <see cref="HumanoidSkeleton"/> for why the two stay separate.</para>
/// </remarks>
public static class QuadrupedSkeleton
{
    /// <summary>How many transforms <see cref="Compose"/> writes, and the length of
    /// <see cref="PieceNames"/>.</summary>
    public const int PieceCount = 10;

    /// <summary>The TRUNK, the body's own piece, and the POLL, the head piece that rides
    /// <see cref="QuadrupedRig.HeadPivot"/> inside it. Indices into <see cref="PieceNames"/> and into the
    /// span <see cref="Compose"/> fills.</summary>
    public const int Trunk = 0, Poll = 1;

    /// <summary>The four upper leg pieces, in <see cref="QuadrupedGait.Leg"/> order from
    /// <see cref="UpperForeLeft"/>. See <see cref="Trunk"/>.</summary>
    public const int UpperForeLeft = 2, UpperForeRight = 3, UpperHindLeft = 4, UpperHindRight = 5;

    /// <summary>The four cannon pieces, in the same order. See <see cref="Trunk"/>.</summary>
    public const int LowerForeLeft = 6, LowerForeRight = 7, LowerHindLeft = 8, LowerHindRight = 9;

    /// <summary>The ten pieces in composition order, and a child always follows its own parent. As on
    /// <see cref="HumanoidSkeleton.PieceNames"/>, a game is free to resolve a mesh by index and ignore these:
    /// they exist so the ORDER has names. Two of them differ from the constants on purpose:
    /// <see cref="Trunk"/> is named <c>body</c> and <see cref="Poll"/> is named <c>head</c>, because the
    /// strings are the asset suffixes an existing kit already carries, and the constants could not reuse
    /// <c>Head</c> without reading as <see cref="QuadrupedRig.Head"/>.</summary>
    public static IReadOnlyList<string> PieceNames { get; } =
    [
        "body", "head",
        "upper_fore_l", "upper_fore_r", "upper_hind_l", "upper_hind_r",
        "lower_fore_l", "lower_fore_r", "lower_hind_l", "lower_hind_r",
    ];

    /// <summary>This frame's world transforms, one per piece, in <see cref="PieceNames"/> order.</summary>
    /// <param name="rig">The body's proportions.</param>
    /// <param name="pose">Where the body is drawn and which way it faces.</param>
    /// <param name="walk">The two-legged pose, read for the run lean and the breath rise alone.</param>
    /// <param name="gait">The four-legged pose, from
    /// <see cref="QuadrupedGait.PoseAt(QuadrupedRig, float, float)"/>. Its roll and trunk yaw are applied
    /// here too.</param>
    /// <param name="into">At least <see cref="PieceCount"/> long. Anything past that is left alone.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="into"/> is shorter than
    /// <see cref="PieceCount"/>.</exception>
    /// <remarks>The legs take the trunk yaw off the PLAIN body frame, not the rolled and risen one, so the
    /// sockets they hang from turn with the trunk while the hooves stay on the ground.</remarks>
    public static void Compose(QuadrupedRig rig, in BodyPose pose, in WalkPose walk, in QuadrupedPose gait,
        Span<Matrix4x4> into)
    {
        ArgumentNullException.ThrowIfNull(rig);
        ArgumentOutOfRangeException.ThrowIfLessThan(into.Length, PieceCount);
        Matrix4x4 body = rig.Body(pose, walk, gait);
        Matrix4x4 trunk = rig.Trunk(gait.TrunkYaw, rig.Torso(walk, gait, body));
        into[Trunk] = trunk;
        into[Poll] = rig.Head(gait.HeadNod, gait.HeadYaw) * trunk;
        Matrix4x4 legFrame = rig.Trunk(gait.TrunkYaw, body);
        into[UpperForeLeft] = BodyRig.Limb(rig.LeftShoulder, gait.LeftForeSwing) * legFrame;
        into[UpperForeRight] = BodyRig.Limb(rig.RightShoulder, gait.RightForeSwing) * legFrame;
        into[UpperHindLeft] = BodyRig.Limb(rig.LeftHip, gait.LeftHindSwing) * legFrame;
        into[UpperHindRight] = BodyRig.Limb(rig.RightHip, gait.RightHindSwing) * legFrame;
        into[LowerForeLeft] = rig.ForeHinge(gait.LeftForeFlex) * into[UpperForeLeft];
        into[LowerForeRight] = rig.ForeHinge(gait.RightForeFlex) * into[UpperForeRight];
        into[LowerHindLeft] = rig.HindHinge(gait.LeftHindFlex) * into[UpperHindLeft];
        into[LowerHindRight] = rig.HindHinge(gait.RightHindFlex) * into[UpperHindRight];
    }

    /// <summary>Where each piece sits at REST, which is its own joint carried up through every joint above
    /// it, and the origin for the trunk. The same offsets <see cref="Compose"/> lands on at
    /// <see cref="QuadrupedPose.Rest"/>, <see cref="WalkPose.Rest"/> and
    /// <see cref="BodyPose.Origin"/>.</summary>
    /// <param name="rig">The body's proportions.</param>
    /// <param name="piece">The piece index, one of the constants on this type.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="piece"/> is not one of the
    /// <see cref="PieceCount"/> pieces. See <see cref="HumanoidSkeleton.RestOffset"/>.</exception>
    public static Vector3 RestOffset(QuadrupedRig rig, int piece)
    {
        ArgumentNullException.ThrowIfNull(rig);
        return piece switch
        {
            Trunk => Vector3.Zero,
            Poll => rig.HeadPivot,
            UpperForeLeft => rig.LeftShoulder,
            UpperForeRight => rig.RightShoulder,
            UpperHindLeft => rig.LeftHip,
            UpperHindRight => rig.RightHip,
            LowerForeLeft => rig.LeftShoulder + rig.ForeHingeFromShoulder,
            LowerForeRight => rig.RightShoulder + rig.ForeHingeFromShoulder,
            LowerHindLeft => rig.LeftHip + rig.HindHingeFromHip,
            LowerHindRight => rig.RightHip + rig.HindHingeFromHip,
            _ => throw new ArgumentOutOfRangeException(nameof(piece), piece, null),
        };
    }
}
