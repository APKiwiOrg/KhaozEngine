using System;
using System.Numerics;
using KhaozEngine.SegmentRig;

namespace KhaozEngine.Tests.SegmentRig;

/// <summary>
/// The bodies these tests measure against. None of them belongs in the package: a creature's proportions are
/// game CONTENT, and the reference proportion every one of these is derived from is already
/// <see cref="BodyRig.Human"/>.
/// </summary>
/// <remarks>
/// The piece CHAINS used to live here too, written as the composition a consumer writes rather than as a
/// transcription of one, because the package did not own a composer yet. It does now
/// (<see cref="HumanoidSkeleton.Compose"/> and <see cref="QuadrupedSkeleton.Compose"/>), so the chains are
/// gone and every suite measures through the package's own. That those suites kept passing unchanged is what
/// says the lift kept the maths.
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

    /// <summary>The lowest point of one shin piece, in world metres, through whatever transform it was
    /// composed at. These bodies are authored with the sole exactly on y 0, and the shin piece hangs from the
    /// knee, so the sole is that far down the shin's own local axis.</summary>
    public static Vector3 Sole(BodyRig rig, ReadOnlySpan<Matrix4x4> at, int shin)
    {
        ArgumentNullException.ThrowIfNull(rig);
        float fromKnee = rig.RightHip.Y + rig.KneeFromHip.Y;
        return Vector3.Transform(new Vector3(0f, -fromKnee, 0f), at[shin]);
    }

    /// <summary>Where one hoof's sole is at a pose, through the package's own four-legged chain.</summary>
    public static Vector3 Hoof(in BodyPose pose, in QuadrupedPose gait, QuadrupedGait.Leg leg)
    {
        Span<Matrix4x4> at = stackalloc Matrix4x4[QuadrupedSkeleton.PieceCount];
        QuadrupedSkeleton.Compose(Grazer, pose, WalkPose.Rest, gait, at);
        int piece = QuadrupedSkeleton.LowerForeLeft + (int)leg;
        float sole = QuadrupedGait.IsFore(leg) ? Grazer.ForeSoleFromHinge : Grazer.HindSoleFromHinge;
        return Vector3.Transform(new Vector3(0f, -sole, 0f), at[piece]);
    }
}
