using System;
using System.Numerics;
using KhaozEngine.SegmentRig;
using Xunit;

namespace KhaozEngine.Tests.SegmentRig;

/// <summary>
/// The four-legged strike, pinned without a scene: it keeps the punch's clock exactly, it puts the head down
/// and the body forward hardest at the blow, it stands still on guard between blows, and the whole lunge
/// happens over four hooves that do not move and four joints that do not open.
/// </summary>
/// <remarks>
/// The planted and closed measurements go through <see cref="QuadrupedSkeleton.Compose"/>, the piece chain a draw
/// writes, so a change to the composition's order fails here too.
/// </remarks>
public class HeadbuttTests
{
    static readonly QuadrupedRig Rig = TestBodies.Grazer;

    /// <summary>Somewhere other than the origin and facing somewhere other than +z, so a surge that ignored
    /// the facing, or a pitch about the wrong point, cannot hide behind zeroes.</summary>
    static readonly BodyPose Standing = new(new Vector3(3f, 0.2f, -4f), 0.6f);

    /// <summary>How far a hoof or a socket may move at full weight, metres. The stance is SOLVED rather than
    /// tuned, so the only thing left is single-precision noise, about a micron over a chain a metre and a half
    /// long: a millimetre sits far above that and still well below anything the eye could read.</summary>
    const float Planted = 0.001f;

    /// <summary>How far a hoof may move while the strike is still BLENDING in or out, metres. The blend
    /// scales a pose solved for full weight rather than solving again, which drifts a hoof about four
    /// millimetres at half weight and puts it back exactly at both ends. A centimetre is the ceiling on that
    /// trade, and it lasts the blend's 0.15 s at the start and end of a fight.</summary>
    const float PlantedWhileBlending = 0.01f;

    static void ComposeAt(in QuadrupedPose pose, Span<Matrix4x4> into) =>
        QuadrupedSkeleton.Compose(Rig, Standing, WalkPose.Rest, pose, into);

    static QuadrupedPose StrikeAt(float phase, float weight = 1f) =>
        Headbutt.Compose(QuadrupedPose.Rest, Headbutt.PoseAt(phase, Rig), weight);

    // One hoof's sole in the world, the point QuadrupedGaitTests stands on the floor.
    static Vector3 Sole(ReadOnlySpan<Matrix4x4> at, int leg)
    {
        float sole = leg < 2 ? Rig.ForeSoleFromHinge : Rig.HindSoleFromHinge;
        return Vector3.Transform(new Vector3(0f, -sole, 0f), at[QuadrupedSkeleton.LowerForeLeft + leg]);
    }

    [Fact]
    public void TheTwoEndsOfTheStrikeAreTheSamePoseAndBothAreTheBlow()
    {
        // What lets a swing event seed phase zero without a snap, the punch's contract exactly.
        Assert.Equal(Headbutt.PoseAt(0f, Rig), Headbutt.PoseAt(1f, Rig));
        Assert.True(Headbutt.PoseAt(0f, Rig).IsImpact);
        Assert.True(Headbutt.PoseAt(AttackSwing.ImpactPhase * 0.5f, Rig).IsImpact);
        Assert.False(Headbutt.PoseAt(AttackSwing.ImpactPhase, Rig).IsImpact);
        Assert.False(Headbutt.PoseAt(0.5f, Rig).IsImpact);
        Assert.False(Headbutt.PoseAt(AttackSwing.StrikePhase, Rig).IsImpact);
        // A raw accumulator is legal, and anything that is not a number is the blow.
        Assert.Equal(Headbutt.PoseAt(0.9375f, Rig), Headbutt.PoseAt(2.9375f, Rig));
        Assert.Equal(Headbutt.PoseAt(0f, Rig), Headbutt.PoseAt(float.NaN, Rig));
    }

    [Fact]
    public void TheBodyStandsStillOnGuardThroughTheMiddleOfTheCadence()
    {
        // No wind-up, every stroke's rule here: from the end of the recovery to the start of the strike
        // nothing moves, and what it holds is the body standing square.
        HeadbuttPose guard = Headbutt.PoseAt(AttackSwing.RestPhase, Rig);
        Assert.Equal(default(HeadbuttPose), guard);
        for (int i = 0; i <= 100; i++)
        {
            float phase = AttackSwing.RestPhase + ((AttackSwing.StrikePhase - AttackSwing.RestPhase) * i / 100f);
            Assert.Equal(guard, Headbutt.PoseAt(phase, Rig));
        }
        // And the strike window is the same one a consumer's prediction gate reads: under way just after it.
        Assert.NotEqual(guard, Headbutt.PoseAt(AttackSwing.StrikePhase + 0.05f, Rig));
    }

    [Fact]
    public void TheHeadIsLowestAndTheBodyFurthestForwardAtTheBlow()
    {
        HeadbuttPose blow = Headbutt.PoseAt(0f, Rig);
        // A strong nod, poll leading, and a lunge of about fifteen centimetres.
        Assert.InRange(blow.HeadNod, 25f * MathF.PI / 180f, 35f * MathF.PI / 180f);
        Assert.Equal(Headbutt.SurgeMetres, blow.Surge, 5);
        Assert.InRange(blow.Surge, 0.12f, 0.18f);
        for (int i = 0; i <= 400; i++)
        {
            HeadbuttPose pose = Headbutt.PoseAt(i / 400f, Rig);
            Assert.InRange(pose.HeadNod, 0f, blow.HeadNod + 1e-5f);
            Assert.InRange(pose.Surge, 0f, blow.Surge + 1e-5f);
        }
        // Eased in: barely started half way through the strike window, most of the way there just before it.
        float halfway = AttackSwing.StrikePhase + ((1f - AttackSwing.StrikePhase) * 0.5f);
        Assert.True(Headbutt.PoseAt(halfway, Rig).Surge < blow.Surge * 0.2f, "the lunge is not eased in");
        Assert.True(Headbutt.PoseAt(0.99f, Rig).Surge > blow.Surge * 0.7f, "the lunge arrives late");
    }

    [Fact]
    public void ComposingNoStrikeIsTheGaitUntouched()
    {
        QuadrupedPose walking = QuadrupedGait.PoseAt(Rig, 0.37f, 1f);
        HeadbuttPose blow = Headbutt.PoseAt(0f, Rig);
        Assert.Equal(walking, Headbutt.Compose(walking, blow, 0f));
        Assert.Equal(walking, Headbutt.Compose(walking, blow, -1f));
        Assert.Equal(walking, Headbutt.Compose(walking, blow, float.NaN));
        Assert.Equal(walking, Headbutt.Compose(walking, default, 1f));
        Assert.Equal(Headbutt.Compose(walking, blow, 1f), Headbutt.Compose(walking, blow, 2f));

        // ADDED over the gait, never replacing it, so a body striking mid stride is still striding.
        QuadrupedPose half = Headbutt.Compose(walking, blow, 0.5f);
        Assert.Equal(walking.HeadNod + (blow.HeadNod * 0.5f), half.HeadNod, 5);
        Assert.Equal(walking.Bob + (blow.Bob * 0.5f), half.Bob, 5);
        Assert.Equal(walking.Surge + (blow.Surge * 0.5f), half.Surge, 5);
        Assert.Equal(walking.Pitch + (blow.Pitch * 0.5f), half.Pitch, 5);
        Assert.Equal(walking.LeftForeSwing + (blow.ForeSwing * 0.5f), half.LeftForeSwing, 5);
        Assert.Equal(walking.RightForeSwing + (blow.ForeSwing * 0.5f), half.RightForeSwing, 5);
        Assert.Equal(walking.LeftHindSwing + (blow.HindSwing * 0.5f), half.LeftHindSwing, 5);
        Assert.Equal(walking.RightHindSwing + (blow.HindSwing * 0.5f), half.RightHindSwing, 5);
        Assert.Equal(walking.LeftForeFlex + (blow.ForeFlex * 0.5f), half.LeftForeFlex, 5);
        Assert.Equal(walking.RightHindFlex + (blow.HindFlex * 0.5f), half.RightHindFlex, 5);
        // The channels a strike has no business in are the gait's own.
        Assert.Equal(walking.Roll, half.Roll);
        Assert.Equal(walking.TrunkYaw, half.TrunkYaw);
        Assert.Equal(walking.HeadYaw, half.HeadYaw);
    }

    /// <summary>
    /// EVERY HOOF STAYS WHERE IT STOOD. The body lunges fifteen centimetres over four hooves that do not
    /// move, through the whole cadence, sampled through the real composition. A body that skids its feet
    /// forward into a strike is the slide the gait removed.
    /// </summary>
    [Fact]
    public void EveryHoofStaysPlantedThroughTheWholeStrike()
    {
        Span<Matrix4x4> rest = stackalloc Matrix4x4[QuadrupedSkeleton.PieceCount];
        ComposeAt(QuadrupedPose.Rest, rest);
        Span<Matrix4x4> at = stackalloc Matrix4x4[QuadrupedSkeleton.PieceCount];
        float worst = 0f, worstBlending = 0f;
        for (int i = 0; i <= 400; i++)
        {
            float phase = i / 400f;
            ComposeAt(StrikeAt(phase), at);
            for (int leg = 0; leg < 4; leg++)
                worst = MathF.Max(worst, Vector3.Distance(Sole(rest, leg), Sole(at, leg)));
            foreach (float weight in (ReadOnlySpan<float>)[0.25f, 0.5f, 0.75f])
            {
                ComposeAt(StrikeAt(phase, weight), at);
                for (int leg = 0; leg < 4; leg++)
                    worstBlending = MathF.Max(worstBlending, Vector3.Distance(Sole(rest, leg), Sole(at, leg)));
            }
        }
        Assert.True(worst <= Planted, $"a hoof moved {worst} m under the strike");
        Assert.True(worstBlending <= PlantedWhileBlending, $"a hoof moved {worstBlending} m while blending");

        // And the body really did go somewhere over them, along the way it faces.
        ComposeAt(StrikeAt(0f), at);
        Vector3 facing = new(MathF.Sin(Standing.Yaw), 0f, MathF.Cos(Standing.Yaw));
        float lunge = Vector3.Dot(at[QuadrupedSkeleton.Trunk].Translation - rest[QuadrupedSkeleton.Trunk].Translation, facing);
        Assert.True(lunge > 0.1f, $"the body only moved {lunge} m forward");
    }

    /// <summary>
    /// THE SOCKETS STAY ON THEIR BALLS. Every upper leg is a ball centred on its joint that turns in a socket
    /// cut into the body, so the joint seen from the trunk and the joint seen from the leg have to be one
    /// point, or the ball shows its edge and the socket its floor.
    /// </summary>
    [Fact]
    public void TheShoulderAndHipSocketsStayOnTheirBallsThroughTheStrike()
    {
        Vector3[] joints = [Rig.LeftShoulder, Rig.RightShoulder, Rig.LeftHip, Rig.RightHip];
        Span<Matrix4x4> at = stackalloc Matrix4x4[QuadrupedSkeleton.PieceCount];
        float worst = 0f;
        for (int i = 0; i <= 400; i++)
        {
            ComposeAt(StrikeAt(i / 400f), at);
            for (int leg = 0; leg < 4; leg++)
            {
                Vector3 socket = Vector3.Transform(joints[leg], at[QuadrupedSkeleton.Trunk]);
                Vector3 ball = Vector3.Transform(Vector3.Zero, at[QuadrupedSkeleton.UpperForeLeft + leg]);
                worst = MathF.Max(worst, Vector3.Distance(socket, ball));
            }
        }
        Assert.True(worst <= Planted, $"a socket opened {worst} m off its ball");
    }

    [Fact]
    public void TheHeadComesDownAndForwardIntoTheBlow()
    {
        Span<Matrix4x4> rest = stackalloc Matrix4x4[QuadrupedSkeleton.PieceCount];
        ComposeAt(QuadrupedPose.Rest, rest);
        Span<Matrix4x4> blow = stackalloc Matrix4x4[QuadrupedSkeleton.PieceCount];
        ComposeAt(StrikeAt(0f), blow);
        Vector3 facing = new(MathF.Sin(Standing.Yaw), 0f, MathF.Cos(Standing.Yaw));

        // The poll LEADS: it is carried forward by most of the lunge.
        Vector3 pollRest = rest[QuadrupedSkeleton.Poll].Translation;
        Vector3 pollBlow = blow[QuadrupedSkeleton.Poll].Translation;
        Assert.True(Vector3.Dot(pollBlow - pollRest, facing) > 0.1f, "the poll did not drive forward");

        // And the face goes down under it. A point out along the muzzle, inside the reach the head piece is
        // authored to, is lower by a hand and still ahead of where it stood.
        var muzzle = new Vector3(0f, -0.1f, 0.4f);
        Vector3 muzzleRest = Vector3.Transform(muzzle, rest[QuadrupedSkeleton.Poll]);
        Vector3 muzzleBlow = Vector3.Transform(muzzle, blow[QuadrupedSkeleton.Poll]);
        Assert.True(muzzleBlow.Y < muzzleRest.Y - 0.1f, $"the muzzle only dropped {muzzleRest.Y - muzzleBlow.Y} m");
        Assert.True(Vector3.Dot(muzzleBlow - muzzleRest, facing) > 0f, "the muzzle went back into the blow");

        // Nowhere in the cadence is the muzzle lower than at the blow.
        Span<Matrix4x4> at = stackalloc Matrix4x4[QuadrupedSkeleton.PieceCount];
        for (int i = 0; i <= 200; i++)
        {
            ComposeAt(StrikeAt(i / 200f), at);
            Assert.True(Vector3.Transform(muzzle, at[QuadrupedSkeleton.Poll]).Y >= muzzleBlow.Y - 1e-4f,
                $"the muzzle is lower at phase {i / 200f} than at the blow");
        }
    }

    /// <summary>
    /// THE LEGS BRACE rather than the body floating. The forelegs lean back under the lunge and the hind legs
    /// lean back further, pushing, both hinges folding the way they fold and never past straight, and the
    /// trunk itself barely tips or sinks: the eye reads the nod and the lunge, and an animal that visibly
    /// squats or rears is a cartoon.
    /// </summary>
    [Fact]
    public void TheLegsBraceBackUnderTheLungeAndTheTrunkBarelyTips()
    {
        for (int i = 0; i <= 400; i++)
        {
            HeadbuttPose pose = Headbutt.PoseAt(i / 400f, Rig);
            Assert.True(pose.ForeFlex >= 0f && pose.HindFlex >= 0f, $"a hinge folded the wrong way at {i / 400f}");
            Assert.InRange(pose.Pitch, -0.06f, 0.06f);
            Assert.InRange(pose.Bob, -0.03f, 0.005f);
        }

        Span<Matrix4x4> rest = stackalloc Matrix4x4[QuadrupedSkeleton.PieceCount];
        ComposeAt(QuadrupedPose.Rest, rest);
        Span<Matrix4x4> blow = stackalloc Matrix4x4[QuadrupedSkeleton.PieceCount];
        ComposeAt(StrikeAt(0f), blow);
        Vector3 facing = new(MathF.Sin(Standing.Yaw), 0f, MathF.Cos(Standing.Yaw));
        // How far each hoof sits behind its own joint along the facing, at rest and at the blow.
        float Behind(ReadOnlySpan<Matrix4x4> at, int leg) =>
            Vector3.Dot(at[QuadrupedSkeleton.UpperForeLeft + leg].Translation - Sole(at, leg), facing);
        for (int leg = 0; leg < 4; leg++)
            Assert.True(Behind(blow, leg) > Behind(rest, leg) + 0.1f, $"leg {leg} did not brace back");
        Assert.True(Behind(blow, 2) > Behind(blow, 0), "the hind legs are not pushing from further back");
    }
}
