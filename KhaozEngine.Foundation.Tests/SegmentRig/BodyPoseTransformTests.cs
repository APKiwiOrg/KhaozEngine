using System;
using System.Numerics;
using KhaozEngine.SegmentRig;
using Xunit;

namespace KhaozEngine.Tests.SegmentRig;

/// <summary>
/// THE ONE INTENDED RENAME, pinned. Both rigs used to take a tile-world pose type and now take
/// <see cref="BodyPose"/>, which is the same two numbers under a name the package can own: world metres and
/// radians, with nothing tile-shaped about either. This suite pins the transforms they produce, so a swap
/// that changed the maths rather than the type name fails here.
/// </summary>
/// <remarks>
/// The numbers below are computed by hand rather than read off the rig, which is the entire point: a test
/// that asks the rig what the rig answers pins nothing. A yaw of a quarter turn maps engine x onto engine z,
/// which makes every expected coordinate a short sum you can check by eye.
/// </remarks>
public class BodyPoseTransformTests
{
    const float Quarter = MathF.PI * 0.5f;

    [Fact]
    public void ATwoLeggedBodyPlacesItsJointsAtTheHandComputedPoints()
    {
        BodyRig rig = BodyRig.Human;

        // Standing at the origin facing engine +z, which is a yaw of zero: every joint sits exactly where the
        // rig declares it.
        Matrix4x4 square = rig.Body(new BodyPose(Vector3.Zero, 0f), WalkPose.Rest);
        Assert.Equal(Vector3.Zero, square.Translation);
        Assert.Equal(rig.RightShoulder, Vector3.Transform(rig.RightShoulder, square));
        Assert.Equal(new Vector3(0.215f, 1.12f, 0f), rig.LeftShoulder);
        Assert.Equal(new Vector3(0.075f, 0.62f, 0f), rig.LeftHip);

        // A QUARTER TURN, at a position off the origin. CreateRotationY carries engine x onto engine z, so
        // the right shoulder at x -0.215 lands at z +0.215, and the height is untouched.
        var pose = new BodyPose(new Vector3(2f, 0f, -3f), Quarter);
        Matrix4x4 turned = rig.Body(pose, WalkPose.Rest);
        Vector3 shoulder = Vector3.Transform(rig.RightShoulder, turned);
        Assert.Equal(2f, shoulder.X, 5);
        Assert.Equal(1.12f, shoulder.Y, 5);
        Assert.Equal(-2.785f, shoulder.Z, 5);

        // The BOB runs down the world's y after the facing, so it lands on the height alone.
        Matrix4x4 sunk = rig.Body(pose, WalkPose.Rest with { Bob = -0.02f });
        Vector3 sunkShoulder = Vector3.Transform(rig.RightShoulder, sunk);
        Assert.Equal(shoulder.X, sunkShoulder.X, 5);
        Assert.Equal(shoulder.Z, sunkShoulder.Z, 5);
        Assert.Equal(1.10f, sunkShoulder.Y, 5);

        // A body standing still has the torso frame and the body frame exactly equal, because nothing is
        // breathing: Torso is skipped rather than multiplied by an identity.
        Assert.Equal(turned, rig.Torso(pose, WalkPose.Rest));
    }

    [Fact]
    public void TheLimbHingesCarryAPositiveSwingForwardAndEachFlexionItsOwnWay()
    {
        BodyRig rig = BodyRig.Human;
        // A point a third of a metre down a limb piece's own local axis, which is where the mesh is.
        var down = new Vector3(0f, -0.33f, 0f);

        // A positive swing is FORWARD, engine +z, at every joint.
        Vector3 hangingArm = Vector3.Transform(down, BodyRig.Limb(rig.RightShoulder, 0f));
        Vector3 swungArm = Vector3.Transform(down, BodyRig.Limb(rig.RightShoulder, 0.5f));
        Assert.Equal(rig.RightShoulder + down, hangingArm);
        Assert.True(swungArm.Z > hangingArm.Z + 0.1f, "a positive swing did not carry the arm forward");
        Assert.True(swungArm.Y > hangingArm.Y, "a swung limb did not rise");

        // A positive yaw carries the limb toward engine -x, which is OUT on the weapon side. Measured on an
        // arm that is ALREADY swung, because the yaw is about the body's own up axis and a limb hanging
        // straight down that axis is invariant under it. That is the whole reason the yaw composes OUTSIDE
        // the swing: the swing happens in a plane the yaw has already turned.
        Vector3 raisedArm = Vector3.Transform(down, BodyRig.Limb(rig.RightShoulder, 0.8f, 0f));
        Vector3 yawedArm = Vector3.Transform(down, BodyRig.Limb(rig.RightShoulder, 0.8f, 1f));
        Assert.True(yawedArm.X < raisedArm.X - 0.1f, "a positive arm yaw did not carry it to engine -x");
        // And a zero yaw is the single-axis overload exactly, which is the path every walk frame takes.
        Assert.Equal(BodyRig.Limb(rig.RightShoulder, 0.31f), BodyRig.Limb(rig.RightShoulder, 0.31f, 0f));

        // THE TWO HINGES BEND OPPOSITE WAYS, and that sign lives in these two methods alone.
        Vector3 straightForearm = Vector3.Transform(down, rig.Elbow(0f));
        Vector3 bentForearm = Vector3.Transform(down, rig.Elbow(0.6f));
        Assert.Equal(rig.ElbowFromShoulder + down, straightForearm);
        Assert.True(bentForearm.Z > straightForearm.Z + 0.1f, "the elbow did not carry the hand forward");

        Vector3 straightShin = Vector3.Transform(down, rig.Knee(0f));
        Vector3 bentShin = Vector3.Transform(down, rig.Knee(0.6f));
        Assert.Equal(rig.KneeFromHip + down, straightShin);
        Assert.True(bentShin.Z < straightShin.Z - 0.1f, "the knee did not carry the heel back");
    }

    [Fact]
    public void AFourLeggedBodyPlacesItsJointsAtTheHandComputedPoints()
    {
        QuadrupedRig rig = TestBodies.Grazer;

        Matrix4x4 square = rig.Body(new BodyPose(Vector3.Zero, 0f), WalkPose.Rest, QuadrupedPose.Rest);
        Assert.Equal(Vector3.Zero, square.Translation);
        Assert.Equal(new Vector3(0.31f, 0.92f, 0.38f), rig.LeftShoulder);
        Assert.Equal(new Vector3(0.31f, 0.84f, -0.56f), rig.LeftHip);

        // A quarter turn at a position: engine x onto engine z and engine z onto engine -x, so the right
        // shoulder at (-0.31, 0.92, 0.38) lands at x 5 + 0.38 and z 1 + 0.31.
        var pose = new BodyPose(new Vector3(5f, 0f, 1f), Quarter);
        Matrix4x4 turned = rig.Body(pose, WalkPose.Rest, QuadrupedPose.Rest);
        Vector3 shoulder = Vector3.Transform(rig.RightShoulder, turned);
        Assert.Equal(5.38f, shoulder.X, 5);
        Assert.Equal(0.92f, shoulder.Y, 5);
        Assert.Equal(1.31f, shoulder.Z, 5);

        // The leg reaches are the two lengths the gait sizes its swing from, hand summed off the rig.
        Assert.Equal(0.50f + 0.41f, rig.ForeLegMetres, 5);
        Assert.Equal(0.401f + 0.429f, rig.HindLegMetres, 5);

        // The HEAD sits at the poll inside the trunk's frame, and a nod carries the muzzle down.
        Vector3 poll = Vector3.Transform(Vector3.Zero, rig.Head(0f));
        Assert.Equal(rig.HeadPivot, poll);
        var muzzle = new Vector3(0f, 0f, 0.3f);
        Assert.True(Vector3.Transform(muzzle, rig.Head(0.4f)).Y < Vector3.Transform(muzzle, rig.Head(0f)).Y,
            "a positive nod did not dip the muzzle");

        // And a rest gait leaves the trunk frame exactly the body frame: no roll, no rise, no yaw, so each
        // of the three is skipped rather than multiplied by an identity.
        Assert.Equal(turned, rig.Trunk(0f, rig.Torso(WalkPose.Rest, QuadrupedPose.Rest, turned)));
    }

    /// <summary>
    /// The two rigs agree about which side is which, which is the half that inverts silently: both wear the
    /// character's RIGHT at engine -x, and both mirror in x alone so a height or a fore-aft offset is
    /// untouched.
    /// </summary>
    [Fact]
    public void BothRigsMirrorInXAloneAndAgreeWhichSideIsWhich()
    {
        BodyRig human = BodyRig.Human;
        Assert.True(human.RightShoulder.X < 0f && human.LeftShoulder.X > 0f);
        Assert.Equal(-human.RightShoulder.X, human.LeftShoulder.X, 6);
        Assert.Equal(human.RightShoulder.Y, human.LeftShoulder.Y, 6);
        Assert.Equal(human.RightShoulder.Z, human.LeftShoulder.Z, 6);

        QuadrupedRig grazer = TestBodies.Grazer;
        Assert.True(grazer.RightHip.X < 0f && grazer.LeftHip.X > 0f);
        Assert.Equal(-grazer.RightHip.X, grazer.LeftHip.X, 6);
        Assert.Equal(grazer.RightHip.Y, grazer.LeftHip.Y, 6);
        Assert.Equal(grazer.RightHip.Z, grazer.LeftHip.Z, 6);

        // The hand socket is derived rather than written down twice, so it cannot drift off the elbow.
        Assert.Equal(human.HandFromShoulder - human.ElbowFromShoulder, human.HandFromElbow);
    }

    /// <summary>
    /// <see cref="BodyPose"/> itself: two fields, value equality, and a default that is the origin facing
    /// engine +z. A consumer builds one per frame, so it holds nothing else.
    /// </summary>
    [Fact]
    public void ABodyPoseIsTwoNumbersAndNothingElse()
    {
        var pose = new BodyPose(new Vector3(1f, 2f, 3f), 0.5f);
        Assert.Equal(new Vector3(1f, 2f, 3f), pose.Position);
        Assert.Equal(0.5f, pose.Yaw);
        Assert.Equal(pose, new BodyPose(new Vector3(1f, 2f, 3f), 0.5f));
        Assert.NotEqual(pose, new BodyPose(new Vector3(1f, 2f, 3f), 0.6f));

        Assert.Equal(default, BodyPose.Origin);
        Assert.Equal(Vector3.Zero, BodyPose.Origin.Position);
        Assert.Equal(0f, BodyPose.Origin.Yaw);
        Assert.Equal(pose with { Yaw = 0f }, new BodyPose(new Vector3(1f, 2f, 3f), 0f));
    }
}
