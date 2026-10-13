using System;
using System.Numerics;
using KhaozEngine.Attachments;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Render3D;

internal static class RigidAttachmentTestRig
{
    internal static Skeleton Create(Quaternion? hipRotation = null) => new(
        [-1, 0, 1],
        [JointPose.Identity, Pose(new Vector3(0, 2, 0), hipRotation ?? Quaternion.Identity),
            Pose(new Vector3(0, -1, 0), Quaternion.Identity)],
        [0, 1, 2], [2, 1], ["root", "hip", "knee"]);

    internal static ContractJointMap Map(Skeleton skeleton) => new(skeleton, new SkeletonContract(
        [new("root", null, false), new("hip", "root", true), new("knee", "hip", true)]));

    internal static JointPose Pose(Vector3 translation, Quaternion rotation) => new()
    {
        Translation = translation,
        Rotation = rotation,
        Scale = Vector3.One,
    };

    internal static RigidAttachmentPartDefinition Part(uint id, string joint, Matrix4x4? local = null) =>
        new(id, new AttachmentMeshRef("parts/rigid.glb", new string('a', 64)), joint, local ?? Matrix4x4.Identity);

    internal static Matrix4x4[] RotatedPalette(Skeleton skeleton)
    {
        JointPose[] local = (JointPose[])skeleton.RestLocal.Clone();
        local[1] = Pose(new Vector3(0, 2, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2));
        var palette = new Matrix4x4[skeleton.BoneCount];
        skeleton.ComposeInto(local, palette);
        return palette;
    }
}
