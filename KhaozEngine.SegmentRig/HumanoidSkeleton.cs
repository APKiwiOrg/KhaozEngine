using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.SegmentRig;

/// <summary>A two-legged body's ORDERED PIECES, and the composition that turns a <see cref="BodyRig"/> plus a
/// pose plus a <see cref="WalkPose"/> into one world transform per piece. Transforms out, nothing drawn and
/// nothing allocated.</summary>
/// <remarks>
/// TWO PARENT FRAMES, and which piece hangs off which is the whole shape of this. The two thighs compose
/// inside <see cref="BodyRig.Body"/>, the WHOLE body's transform. Everything above the hips (the torso, both
/// upper arms, and through them the forearms and whatever the hands hold) composes inside
/// <see cref="BodyRig.Torso(in WalkPose, in Matrix4x4)"/>, which is that same body matrix with the breath's
/// rise and tip laid over it. So a breathing body moves its chest, its head and its arms and leaves its legs
/// exactly where they were, and the feet stay planted.
/// <para>The chain is TWO deep on each limb: a forearm's parent is its upper arm and a shin's is its thigh,
/// so a bent elbow or knee is composed inside a swinging shoulder or hip rather than added to it. Four pieces
/// hang off a root frame and four more hang off those.</para>
/// <para>TWO COMPOSERS RATHER THAN ONE, and <see cref="QuadrupedSkeleton"/> is the other. The two share a
/// vocabulary (<see cref="PieceNames"/>, <see cref="PieceCount"/>, <see cref="Compose"/>,
/// <see cref="RestOffset"/>) and nothing else, because they genuinely do not share a shape: this one has two
/// root frames, a shoulder yaw on each arm and a piece on the neck base, and the four-legged one has a trunk
/// yaw its legs take off the PLAIN body frame, a poll, and a second pose type. Folding them into one
/// data-driven walk would need a parent table plus a per-piece selector over two unrelated rig types and two
/// pose types, which is more machinery than either method is.</para>
/// <para>Static and rig-taking rather than an instance method, so a headless test can pin the real
/// composition with no scene to draw into.</para>
/// </remarks>
public static class HumanoidSkeleton
{
    /// <summary>How many transforms <see cref="Compose"/> writes, and the length of
    /// <see cref="PieceNames"/>.</summary>
    public const int PieceCount = 10;

    /// <summary>The torso, then the two upper arms, then the two forearms. These are indices into
    /// <see cref="PieceNames"/> and into the span <see cref="Compose"/> fills, so a game maps each one to a
    /// mesh of its own once and never re-derives the order.</summary>
    public const int Torso = 0, UpperArmLeft = 1, UpperArmRight = 2, ForearmLeft = 3, ForearmRight = 4;

    /// <summary>The four leg pieces. See <see cref="Torso"/>.</summary>
    public const int ThighLeft = 5, ThighRight = 6, ShinLeft = 7, ShinRight = 8;

    /// <summary>The piece on the NECK BASE, which rides the torso frame out at
    /// <see cref="BodyRig.HeadFromFeet"/>. A body with nothing to draw there simply draws nothing: the
    /// transform is written anyway, because it costs nothing and a hole in the span is a trap for the next
    /// reader.</summary>
    public const int Head = 9;

    /// <summary>The ten pieces in composition order, and a child always follows its own parent. A game is
    /// free to resolve a mesh by index and ignore these entirely: they are here so the ORDER has names, which
    /// is what a piece-per-file kit needs to line its assets up against.</summary>
    public static IReadOnlyList<string> PieceNames { get; } =
    [
        "torso", "upper_arm_l", "upper_arm_r", "forearm_l", "forearm_r",
        "thigh_l", "thigh_r", "shin_l", "shin_r", "head",
    ];

    /// <summary>This frame's world transforms, one per piece, in <see cref="PieceNames"/> order.</summary>
    /// <param name="rig">The body's proportions.</param>
    /// <param name="pose">Where the body is drawn and which way it faces.</param>
    /// <param name="walk">The limb angles, the body's own bob and lean, and the torso's own rise and lean.
    /// </param>
    /// <param name="into">At least <see cref="PieceCount"/> long. Anything past that is left alone, so a
    /// caller with extra slots of its own may hand over its whole array.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="into"/> is shorter than
    /// <see cref="PieceCount"/>. Refused up front rather than part way through, so a short span never leaves
    /// half a body composed.</exception>
    /// <remarks>
    /// The WALK's lean pivots at HIP height rather than on the feet, which is <see cref="BodyRig.Body"/>'s
    /// own rule. A limb goes INSIDE its parent, so a swing happens in the body's frame and turns with the
    /// body rather than relative to a world axis. A forearm and a shin compose against their own parent's
    /// finished transform rather than against the body, which is what makes the flex a hinge instead of a
    /// second swing from the shoulder.
    /// <para>BOTH shoulders carry a yaw under their swing, so an arm can sweep across the body instead of
    /// only up and down the plane the body faces. The OFF one is a guard pose's and the weapon one is a
    /// stroke's, and both are zero for every other pose, which is what keeps these the same transforms a walk
    /// always composed.</para>
    /// </remarks>
    public static void Compose(BodyRig rig, in BodyPose pose, in WalkPose walk, Span<Matrix4x4> into)
    {
        ArgumentNullException.ThrowIfNull(rig);
        ArgumentOutOfRangeException.ThrowIfLessThan(into.Length, PieceCount);
        Matrix4x4 body = rig.Body(pose, walk);
        Matrix4x4 torso = rig.Torso(walk, body);
        into[Torso] = torso;
        into[UpperArmLeft] = BodyRig.Limb(rig.LeftShoulder, walk.LeftArm, walk.LeftArmYaw) * torso;
        into[UpperArmRight] = BodyRig.Limb(rig.RightShoulder, walk.RightArm, walk.RightArmYaw) * torso;
        into[ForearmLeft] = rig.Elbow(walk.LeftElbow) * into[UpperArmLeft];
        into[ForearmRight] = rig.Elbow(walk.RightElbow) * into[UpperArmRight];
        into[ThighLeft] = BodyRig.Limb(rig.LeftHip, walk.LeftLeg) * body;
        into[ThighRight] = BodyRig.Limb(rig.RightHip, walk.RightLeg) * body;
        into[ShinLeft] = rig.Knee(walk.LeftKnee) * into[ThighLeft];
        into[ShinRight] = rig.Knee(walk.RightKnee) * into[ThighRight];
        into[Head] = Matrix4x4.CreateTranslation(rig.HeadFromFeet) * torso;
    }

    /// <summary>Where each piece sits at REST, which is its own joint carried up through every joint above
    /// it, and the origin for the torso. The same offsets <see cref="Compose"/> lands on at
    /// <see cref="WalkPose.Rest"/> and <see cref="BodyPose.Origin"/>, which is what makes this the frame to
    /// measure a body's bounds in.</summary>
    /// <param name="rig">The body's proportions.</param>
    /// <param name="piece">The piece index, one of the constants on this type.</param>
    public static Vector3 RestOffset(BodyRig rig, int piece)
    {
        ArgumentNullException.ThrowIfNull(rig);
        return piece switch
        {
            UpperArmLeft => rig.LeftShoulder,
            UpperArmRight => rig.RightShoulder,
            ForearmLeft => rig.LeftShoulder + rig.ElbowFromShoulder,
            ForearmRight => rig.RightShoulder + rig.ElbowFromShoulder,
            ThighLeft => rig.LeftHip,
            ThighRight => rig.RightHip,
            ShinLeft => rig.LeftHip + rig.KneeFromHip,
            ShinRight => rig.RightHip + rig.KneeFromHip,
            Head => rig.HeadFromFeet,
            _ => Vector3.Zero,
        };
    }
}
