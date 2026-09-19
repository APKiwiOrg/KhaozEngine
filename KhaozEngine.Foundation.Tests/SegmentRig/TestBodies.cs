using System;
using System.Numerics;
using KhaozEngine.SegmentRig;

namespace KhaozEngine.Tests.SegmentRig;

/// <summary>
/// The bodies and the piece chains these tests measure against. None of them belongs in the package: a
/// creature's proportions are game CONTENT, and the ordered piece list that walks a rig's frames into one
/// transform per mesh is the skeleton composer, which the package does not own yet.
/// </summary>
/// <remarks>
/// The two chains below are deliberately written as the composition a consumer writes, rather than as a
/// transcription of one. That is what makes the assertions in these suites worth anything: they pin the
/// RIG's frames (<c>Body</c>, <c>Torso</c>, <c>Trunk</c>, <c>Limb</c>, <c>Elbow</c>, <c>Knee</c>,
/// <c>ForeHinge</c>, <c>HindHinge</c>) through the order a real draw would use them in, so a sign or a pivot
/// that moved inside the package fails here rather than in a scene nobody can run headlessly.
/// </remarks>
static class TestBodies
{
    /// <summary>A SHORT body, as a fraction of a person. Every length in the rig scales together, which is
    /// what <see cref="BodyRig.Scaled"/> exists for, and 0.55 puts the top of its head at 0.825 m against a
    /// person's 1.5.</summary>
    public const float SmallScale = 0.55f;

    /// <summary>That short body. Declared here rather than in the package, because a size is content.</summary>
    public static BodyRig Small { get; } = BodyRig.Human.Scaled(SmallScale);

    /// <summary>A four-legged GRAZER, roughly cattle sized: its shoulder joint sits 0.92 m up and a little
    /// ahead of the hip's line, its hip 0.84 m up and behind it, both 0.31 m off the centre line, each low
    /// enough that its ball reaches the belly line. The carpus is 0.50 m below the shoulder and the hock 0.40
    /// m below the hip. Each hoof bottom sits a centimetre off the floor at rest.</summary>
    /// <remarks>The STRIDE is 1.5 m, which is what an animal walking at 1.5 m/s actually covers per cycle:
    /// one stride a second. On a leg this long that is a swing of about twenty-nine degrees each way from the
    /// shoulder and thirty-two from the hip (<see cref="QuadrupedGait.SwingFor"/>).</remarks>
    public static QuadrupedRig Grazer { get; } = new()
    {
        RightShoulder = new Vector3(-0.31f, 0.92f, 0.38f),
        RightHip = new Vector3(-0.31f, 0.84f, -0.56f),
        ForeHingeFromShoulder = new Vector3(0f, -0.50f, 0.077f),
        HindHingeFromHip = new Vector3(0f, -0.401f, -0.09f),
        ForeSoleFromHinge = 0.41f,
        HindSoleFromHinge = 0.429f,
        HeadPivot = new Vector3(0f, 1.35f, 0.78f),
        RestHeightMetres = 1.485f,
        StrideMetres = 1.5f,
    };

    /// <summary>The GRAZER's reading of the two-legged type, for the consumers that take a
    /// <see cref="BodyRig"/> for every body: the walk cycle reads its stride and a breath reads its rest
    /// height. Derived from <see cref="Grazer"/> rather than copied off it, so the two cannot disagree about
    /// how far one body walks per cycle.</summary>
    public static BodyRig GrazerBody { get; } =
        BodyRig.Human.Scaled(Grazer.RestHeightMetres / BodyRig.Human.RestHeightMetres)
            with { StrideMetres = Grazer.StrideMetres };

    /// <summary>How far above the floor these hooves are authored, metres.</summary>
    public const float AuthoredFloor = 0.01f;

    /// <summary>A two-legged body's pieces, in the order <see cref="Humanoid"/> writes them.</summary>
    public const int Torso = 0, UpperArmLeft = 1, UpperArmRight = 2, ForearmLeft = 3, ForearmRight = 4;

    /// <summary>The four leg pieces.</summary>
    public const int ThighLeft = 5, ThighRight = 6, ShinLeft = 7, ShinRight = 8;

    /// <summary>The piece that rides the neck base inside the torso frame.</summary>
    public const int Head = 9;

    /// <summary>How many pieces <see cref="Humanoid"/> writes.</summary>
    public const int HumanoidPieceCount = 10;

    /// <summary>
    /// One frame's world transforms for a two-legged body, one per piece.
    /// </summary>
    /// <remarks>
    /// TWO PARENT FRAMES, and which piece hangs off which is the whole shape of this. The two thighs compose
    /// inside <see cref="BodyRig.Body"/>, the WHOLE body's transform. Everything above the hips (the torso,
    /// both upper arms, and through them the forearms) composes inside <see cref="BodyRig.Torso(in WalkPose,
    /// in Matrix4x4)"/>, which is that same body matrix with the breath's rise and tip laid over it. So a
    /// breathing body moves its chest, its head and its arms and leaves its legs exactly where they were, and
    /// the feet stay planted.
    /// <para>A forearm and a shin compose against their own parent's finished transform rather than against
    /// the body, which is what makes the flex a hinge instead of a second swing from the shoulder.</para>
    /// </remarks>
    public static void Humanoid(BodyRig rig, in BodyPose pose, in WalkPose walk, Span<Matrix4x4> into)
    {
        ArgumentNullException.ThrowIfNull(rig);
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

    /// <summary>The lowest point of one shin piece, in world metres, through whatever transform it was
    /// composed at. These bodies are authored with the sole exactly on y 0, and the shin piece hangs from the
    /// knee, so the sole is that far down the shin's own local axis.</summary>
    public static Vector3 Sole(BodyRig rig, ReadOnlySpan<Matrix4x4> at, int shin)
    {
        ArgumentNullException.ThrowIfNull(rig);
        float fromKnee = rig.RightHip.Y + rig.KneeFromHip.Y;
        return Vector3.Transform(new Vector3(0f, -fromKnee, 0f), at[shin]);
    }

    /// <summary>A four-legged body's pieces, in the order <see cref="Quadruped"/> writes them.</summary>
    public const int Trunk = 0, Poll = 1;

    /// <summary>The four upper leg pieces, in <see cref="QuadrupedGait.Leg"/> order.</summary>
    public const int UpperForeLeft = 2, UpperForeRight = 3, UpperHindLeft = 4, UpperHindRight = 5;

    /// <summary>The four cannon pieces, in the same order.</summary>
    public const int LowerForeLeft = 6, LowerForeRight = 7, LowerHindLeft = 8, LowerHindRight = 9;

    /// <summary>How many pieces <see cref="Quadruped"/> writes.</summary>
    public const int QuadrupedPieceCount = 10;

    /// <summary>One frame's world transforms for a four-legged body, one per piece.</summary>
    /// <remarks>The legs take the trunk yaw off the PLAIN body frame, not the rolled and risen one, so the
    /// sockets they hang from turn with the trunk while the hooves stay on the ground.</remarks>
    public static void Quadruped(QuadrupedRig rig, in BodyPose pose, in WalkPose walk, in QuadrupedPose gait,
        Span<Matrix4x4> into)
    {
        ArgumentNullException.ThrowIfNull(rig);
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

    /// <summary>Where one hoof's sole is at a pose, through the chain above.</summary>
    public static Vector3 Hoof(in BodyPose pose, in QuadrupedPose gait, QuadrupedGait.Leg leg)
    {
        Span<Matrix4x4> at = stackalloc Matrix4x4[QuadrupedPieceCount];
        Quadruped(Grazer, pose, WalkPose.Rest, gait, at);
        int piece = LowerForeLeft + (int)leg;
        float sole = QuadrupedGait.IsFore(leg) ? Grazer.ForeSoleFromHinge : Grazer.HindSoleFromHinge;
        return Vector3.Transform(new Vector3(0f, -sole, 0f), at[piece]);
    }
}
