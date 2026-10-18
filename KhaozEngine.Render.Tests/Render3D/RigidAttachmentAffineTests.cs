using System;
using System.Numerics;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Render3D;
using Xunit;
using static KhaozEngine.Tests.Render3D.RigidAttachmentTestRig;

namespace KhaozEngine.Tests.Render3D;

public sealed class RigidAttachmentAffineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Oblique_base_alignment_remains_affine_through_real_attachment_composition(bool reflected)
    {
        Skeleton skeleton = Create(Quaternion.CreateFromYawPitchRoll(0.3f, 0.7f, -0.2f));
        skeleton.RestLocal[1].Translation = new Vector3(2, 3, 4);
        skeleton.RestLocal[1].Scale = new Vector3(reflected ? -1 : 1, 1, 1);
        ContractJointMap map = Map(skeleton);
        var binding = new AttachmentSetBinding(
            new AttachmentSetDefinition([Part(1, "hip", Matrix4x4.CreateTranslation(1, 2, 3))]), map);
        Matrix4x4[] palette = skeleton.ComposeRestPose();
        var model = new Matrix4x4(
            0, 1, 0, 0,
            -1, 0, 0, 0,
            0, 0, 1, 0,
            10, 20, 30, 1);
        var worlds = new Matrix4x4[1];

        binding.ComposeWorlds(skeleton, palette, model, worlds);

        AssertAffine(map.BodyAlignment(map.Node("hip")));
        AssertPlacement(worlds[0], new Vector3(5, 23, 37), Vector3.UnitY, -Vector3.UnitX);

        // Lower triangular scale and shear leave the rigid axes in their original order.
        var scaleAndShear = new Matrix4x4(
            2, 0, 0, 0,
            0.4f, 3, 0, 0,
            0.6f, -0.2f, 4, 0,
            0, 0, 0, 1);
        Matrix4x4 baseAxes = palette[map.Bone("hip")];
        baseAxes.Translation = Vector3.Zero;
        Matrix4x4 quarterTurn = model;
        quarterTurn.Translation = Vector3.Zero;
        palette[map.Bone("hip")] = scaleAndShear * baseAxes * quarterTurn
            * Matrix4x4.CreateTranslation(5, 6, 7);

        binding.ComposeWorlds(skeleton, palette, model, worlds);

        AssertPlacement(worlds[0], new Vector3(3, 23, 40), -Vector3.UnitX, -Vector3.UnitY);
    }

    static void AssertPlacement(Matrix4x4 world, Vector3 position, Vector3 x, Vector3 y)
    {
        AssertAffine(world);
        AssertNear(position, Vector3.Transform(Vector3.Zero, world));
        AssertNear(x, Vector3.TransformNormal(Vector3.UnitX, world));
        AssertNear(y, Vector3.TransformNormal(Vector3.UnitY, world));
        AssertNear(Vector3.UnitZ, Vector3.TransformNormal(Vector3.UnitZ, world));
    }

    static void AssertAffine(Matrix4x4 matrix)
    {
        Assert.Equal(0f, matrix.M14);
        Assert.Equal(0f, matrix.M24);
        Assert.Equal(0f, matrix.M34);
        Assert.Equal(1f, matrix.M44);
    }

    static void AssertNear(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < 1e-5f, $"Expected {expected}, got {actual}.");
}
