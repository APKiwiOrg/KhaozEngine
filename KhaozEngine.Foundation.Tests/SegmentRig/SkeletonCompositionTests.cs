using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.SegmentRig;
using Xunit;

namespace KhaozEngine.Tests.SegmentRig;

/// <summary>
/// The two SKELETON composers as a contract rather than as geometry: how many pieces there are, what they are
/// called, that the order is a parent before its children, that a zero pose lands every piece on its own rest
/// offset, and that a span too short is refused rather than half filled.
/// </summary>
/// <remarks>The geometry itself is pinned by every other suite here, which now measures through these two
/// methods. This one pins the shape of the API around them, which is what a game builds its mesh table
/// against.</remarks>
public class SkeletonCompositionTests
{
    /// <summary>
    /// TEN PIECES, NAMED, IN A FIXED ORDER, and a child always follows its own parent. A game maps each index
    /// to a mesh once, so a reorder is a silent swap of two limbs rather than a compile error.
    /// </summary>
    [Fact]
    public void TheTwoLeggedPieceListIsTenNamedPiecesWithEveryParentAheadOfItsChildren()
    {
        Assert.Equal(10, HumanoidSkeleton.PieceCount);
        Assert.Equal(HumanoidSkeleton.PieceCount, HumanoidSkeleton.PieceNames.Count);
        Assert.Equal(
            new[]
            {
                "torso", "upper_arm_l", "upper_arm_r", "forearm_l", "forearm_r",
                "thigh_l", "thigh_r", "shin_l", "shin_r", "head",
            },
            HumanoidSkeleton.PieceNames);

        // The indices are the order, stated once so a constant that drifted off the list fails here.
        Assert.Equal(0, HumanoidSkeleton.Torso);
        Assert.Equal(1, HumanoidSkeleton.UpperArmLeft);
        Assert.Equal(2, HumanoidSkeleton.UpperArmRight);
        Assert.Equal(3, HumanoidSkeleton.ForearmLeft);
        Assert.Equal(4, HumanoidSkeleton.ForearmRight);
        Assert.Equal(5, HumanoidSkeleton.ThighLeft);
        Assert.Equal(6, HumanoidSkeleton.ThighRight);
        Assert.Equal(7, HumanoidSkeleton.ShinLeft);
        Assert.Equal(8, HumanoidSkeleton.ShinRight);
        Assert.Equal(9, HumanoidSkeleton.Head);

        // A forearm's parent is its own upper arm and a shin's is its own thigh, so both come later.
        Assert.True(HumanoidSkeleton.ForearmLeft > HumanoidSkeleton.UpperArmLeft);
        Assert.True(HumanoidSkeleton.ForearmRight > HumanoidSkeleton.UpperArmRight);
        Assert.True(HumanoidSkeleton.ShinLeft > HumanoidSkeleton.ThighLeft);
        Assert.True(HumanoidSkeleton.ShinRight > HumanoidSkeleton.ThighRight);
    }

    /// <summary>The four-legged list, the same contract: ten pieces, the trunk first, then the poll, then the
    /// four upper legs and the four cannons, each leg pair in <see cref="QuadrupedGait.Leg"/> order.</summary>
    [Fact]
    public void TheFourLeggedPieceListIsTenNamedPiecesInLegOrder()
    {
        Assert.Equal(10, QuadrupedSkeleton.PieceCount);
        Assert.Equal(QuadrupedSkeleton.PieceCount, QuadrupedSkeleton.PieceNames.Count);
        Assert.Equal(
            new[]
            {
                "body", "head",
                "upper_fore_l", "upper_fore_r", "upper_hind_l", "upper_hind_r",
                "lower_fore_l", "lower_fore_r", "lower_hind_l", "lower_hind_r",
            },
            QuadrupedSkeleton.PieceNames);

        Assert.Equal(0, QuadrupedSkeleton.Trunk);
        Assert.Equal(1, QuadrupedSkeleton.Poll);
        // The four legs run in the gait's own order from each base, which is what lets a caller index a leg
        // with a plain cast instead of a switch.
        Assert.Equal(2, QuadrupedSkeleton.UpperForeLeft + (int)QuadrupedGait.Leg.LeftFore);
        Assert.Equal(3, QuadrupedSkeleton.UpperForeLeft + (int)QuadrupedGait.Leg.RightFore);
        Assert.Equal(4, QuadrupedSkeleton.UpperForeLeft + (int)QuadrupedGait.Leg.LeftHind);
        Assert.Equal(5, QuadrupedSkeleton.UpperForeLeft + (int)QuadrupedGait.Leg.RightHind);
        Assert.Equal(6, QuadrupedSkeleton.LowerForeLeft + (int)QuadrupedGait.Leg.LeftFore);
        Assert.Equal(9, QuadrupedSkeleton.LowerForeLeft + (int)QuadrupedGait.Leg.RightHind);

        // A cannon comes after the upper leg it hangs off, on every leg.
        for (int leg = 0; leg < 4; leg++)
            Assert.True(QuadrupedSkeleton.LowerForeLeft + leg > QuadrupedSkeleton.UpperForeLeft + leg);
    }

    /// <summary>
    /// A ZERO POSE lands every piece exactly on its own rest offset, which is what makes
    /// <c>RestOffset</c> the frame a body's bounds are measured in. Stated on the whole transform as well as
    /// on the translation: at rest there is nothing to rotate, so each piece is a pure translation.
    /// </summary>
    [Fact]
    public void AZeroPoseComposesEveryTwoLeggedPieceOntoItsOwnRestOffset()
    {
        Span<Matrix4x4> at = stackalloc Matrix4x4[HumanoidSkeleton.PieceCount];
        foreach (BodyRig rig in new[] { BodyRig.Human, TestBodies.Small })
        {
            HumanoidSkeleton.Compose(rig, BodyPose.Origin, WalkPose.Rest, at);
            for (int piece = 0; piece < HumanoidSkeleton.PieceCount; piece++)
            {
                Vector3 rest = HumanoidSkeleton.RestOffset(rig, piece);
                Assert.Equal(Matrix4x4.CreateTranslation(rest), at[piece]);
            }

            // The offsets really are a chain rather than nine copies of the same point: a forearm is its
            // shoulder plus its own elbow, and a shin is its hip plus its own knee.
            Assert.Equal(rig.LeftShoulder + rig.ElbowFromShoulder,
                HumanoidSkeleton.RestOffset(rig, HumanoidSkeleton.ForearmLeft));
            Assert.Equal(rig.RightHip + rig.KneeFromHip,
                HumanoidSkeleton.RestOffset(rig, HumanoidSkeleton.ShinRight));
            Assert.Equal(rig.HeadFromFeet, HumanoidSkeleton.RestOffset(rig, HumanoidSkeleton.Head));
            Assert.Equal(Vector3.Zero, HumanoidSkeleton.RestOffset(rig, HumanoidSkeleton.Torso));
        }
    }

    /// <summary>The four-legged half of the same claim.</summary>
    [Fact]
    public void AZeroPoseComposesEveryFourLeggedPieceOntoItsOwnRestOffset()
    {
        QuadrupedRig rig = TestBodies.Grazer;
        Span<Matrix4x4> at = stackalloc Matrix4x4[QuadrupedSkeleton.PieceCount];
        QuadrupedSkeleton.Compose(rig, BodyPose.Origin, WalkPose.Rest, QuadrupedPose.Rest, at);
        for (int piece = 0; piece < QuadrupedSkeleton.PieceCount; piece++)
            Assert.Equal(Matrix4x4.CreateTranslation(QuadrupedSkeleton.RestOffset(rig, piece)), at[piece]);

        Assert.Equal(Vector3.Zero, QuadrupedSkeleton.RestOffset(rig, QuadrupedSkeleton.Trunk));
        Assert.Equal(rig.HeadPivot, QuadrupedSkeleton.RestOffset(rig, QuadrupedSkeleton.Poll));
        Assert.Equal(rig.LeftShoulder + rig.ForeHingeFromShoulder,
            QuadrupedSkeleton.RestOffset(rig, QuadrupedSkeleton.LowerForeLeft));
        Assert.Equal(rig.RightHip + rig.HindHingeFromHip,
            QuadrupedSkeleton.RestOffset(rig, QuadrupedSkeleton.LowerHindRight));
    }

    /// <summary>
    /// A span shorter than the piece count is REFUSED, up front, rather than throwing part way through and
    /// leaving half a body composed. A longer one is filled and the tail is left alone, so a caller with
    /// slots of its own may hand over its whole array.
    /// </summary>
    [Fact]
    public void ComposeRefusesAShortSpanAndLeavesTheTailOfALongOneAlone()
    {
        BodyRig rig = BodyRig.Human;
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            Span<Matrix4x4> tooShort = stackalloc Matrix4x4[HumanoidSkeleton.PieceCount - 1];
            HumanoidSkeleton.Compose(rig, BodyPose.Origin, WalkPose.Rest, tooShort);
        });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            Span<Matrix4x4> tooShort = stackalloc Matrix4x4[QuadrupedSkeleton.PieceCount - 1];
            QuadrupedSkeleton.Compose(TestBodies.Grazer, BodyPose.Origin, WalkPose.Rest, QuadrupedPose.Rest,
                tooShort);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            Span<Matrix4x4> at = stackalloc Matrix4x4[HumanoidSkeleton.PieceCount];
            HumanoidSkeleton.Compose(null!, BodyPose.Origin, WalkPose.Rest, at);
        });

        // The origin is the torso's real answer, so an index off either end must refuse rather than return it.
        foreach (int piece in new[] { -1, HumanoidSkeleton.PieceCount })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => HumanoidSkeleton.RestOffset(rig, piece));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => QuadrupedSkeleton.RestOffset(TestBodies.Grazer, piece));
        }
        Assert.Equal(Vector3.Zero, HumanoidSkeleton.RestOffset(rig, HumanoidSkeleton.Torso));
        Assert.Equal(Vector3.Zero, QuadrupedSkeleton.RestOffset(TestBodies.Grazer, QuadrupedSkeleton.Trunk));

        // A longer span: exactly PieceCount transforms written, and the sentinel past the end untouched.
        var sentinel = Matrix4x4.CreateScale(7f);
        Span<Matrix4x4> roomy = stackalloc Matrix4x4[HumanoidSkeleton.PieceCount + 2];
        roomy.Fill(sentinel);
        HumanoidSkeleton.Compose(rig, BodyPose.Origin, WalkPose.Rest, roomy);
        for (int piece = 0; piece < HumanoidSkeleton.PieceCount; piece++)
            Assert.NotEqual(sentinel, roomy[piece]);
        Assert.Equal(sentinel, roomy[HumanoidSkeleton.PieceCount]);
        Assert.Equal(sentinel, roomy[HumanoidSkeleton.PieceCount + 1]);
    }

    /// <summary>
    /// Every piece is carried through the SAME body transform, under the pose's own yaw and out to its own
    /// joint. Rotation THEN translation: the wrong order shows up as a translation that is not the pose's at
    /// all, because a rotation applied after a translation swings the position around the origin. Split off
    /// Grimhollow's avatar draw suite, whose other half needed a scene.
    /// </summary>
    [Fact]
    public void EveryPieceIsCarriedThroughTheSameBodyTransformUnderThePosesYaw()
    {
        BodyRig rig = BodyRig.Human;
        var pose = new BodyPose(new Vector3(30f, 3f, -44f), MathF.PI / 2f);
        Matrix4x4 expected = Matrix4x4.CreateRotationY(pose.Yaw) * Matrix4x4.CreateTranslation(pose.Position);

        Span<Matrix4x4> at = stackalloc Matrix4x4[HumanoidSkeleton.PieceCount];
        HumanoidSkeleton.Compose(rig, pose, WalkPose.Rest, at);

        var placed = new List<Vector3>();
        for (int piece = 0; piece < HumanoidSkeleton.PieceCount; piece++)
        {
            // The rotation half of every piece is the body's, because nothing is swung at rest.
            Assert.Equal(expected.M11, at[piece].M11, 4);
            Assert.Equal(expected.M13, at[piece].M13, 4);
            Assert.Equal(expected.M31, at[piece].M31, 4);
            Assert.Equal(expected.M33, at[piece].M33, 4);
            // And each one is AT ITS OWN JOINT carried through that transform, so a body at rest is ten
            // pieces at ten places rather than ten stacked at the feet. To four decimals rather than exactly,
            // because the composition reaches a joint through a chain of multiplies and this reaches it with
            // one, which is the same point and not always the same last bit.
            Vector3 joint = Vector3.Transform(HumanoidSkeleton.RestOffset(rig, piece), expected);
            Assert.Equal(joint.X, at[piece].Translation.X, 4);
            Assert.Equal(joint.Y, at[piece].Translation.Y, 4);
            Assert.Equal(joint.Z, at[piece].Translation.Z, 4);
            placed.Add(at[piece].Translation);
        }

        Assert.Equal(pose.Position, at[HumanoidSkeleton.Torso].Translation);
        // TEN DISTINCT POINTS: the torso sits on the pose itself and no two other pieces share a joint, so a
        // body at rest really is ten places. A forearm drawn at its shoulder is an arm with no upper half.
        Assert.Equal(HumanoidSkeleton.PieceCount, new HashSet<Vector3>(placed).Count);
    }
}
