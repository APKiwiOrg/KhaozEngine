using System;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Render3D;
using Xunit;
using static KhaozEngine.Tests.Render3D.RigidAttachmentTestRig;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class RigidAttachmentBindingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Map_only_binding_explicitly_refuses_skinned_parts(bool mixed)
    {
        Skeleton skeleton = Create();
        var skinned = new SkinnedAttachmentPartDefinition(7, Part(1, "hip").Mesh);
        var definition = new AttachmentSetDefinition(mixed ? [Part(1, "hip"), skinned] : [skinned]);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => new AttachmentSetBinding(definition, Map(skeleton)));

        Assert.Contains("7", error.Message);
        Assert.Contains("skinned", error.Message);
    }

    [Fact]
    public void First_composition_of_a_large_set_allocates_no_managed_memory()
    {
        Skeleton skeleton = Create();
        Matrix4x4[] palette = skeleton.ComposeRestPose();
        var model = Matrix4x4.CreateTranslation(10, 0, 0);
        RigidAttachmentPartDefinition[] parts = Enumerable.Range(1, 129)
            .Select(id => Part((uint)id, "hip", Matrix4x4.CreateTranslation(id, 0, 0))).ToArray();
        var binding = Bind(skeleton, parts);
        var worlds = new Matrix4x4[129];

        // Warm composition and matrix math without touching the large-set scratch path.
        Bind(skeleton, Part(1, "hip")).ComposeWorlds(skeleton, palette, model, new Matrix4x4[1]);
        AllocAssert.NoPerCallAllocation("a first large-set composition",
            () => binding.ComposeWorlds(skeleton, palette, model, worlds));
        Assert.Equal(129, binding.PartCount);
        Assert.Equal(new Vector3(11, 2, 0), worlds[0].Translation);
        Assert.Equal(new Vector3(139, 2, 0), worlds[128].Translation);
    }

    [Fact]
    public void Reordered_skin_uses_hip_bone_not_node_index()
    {
        Skeleton skeleton = Create();
        var binding = Bind(skeleton, Part(1, "hip", Matrix4x4.CreateTranslation(1, 0, 0)));
        Matrix4x4[] worlds = [Matrix4x4.Identity, Matrix4x4.CreateTranslation(99, 0, 0)];
        var model = Matrix4x4.CreateTranslation(10, 0, 0);

        binding.ComposeWorlds(skeleton, skeleton.ComposeRestPose(), model, worlds);

        Assert.Same(skeleton, binding.Skeleton);
        Assert.Equal(1, binding.PartCount);
        Assert.Equal(new Vector3(11, 2, 0), Vector3.Transform(Vector3.Zero, worlds[0]));
        Assert.Equal(new Vector3(99, 0, 0), worlds[1].Translation);
    }

    [Fact]
    public void Rotated_hip_moves_its_part_and_child()
    {
        Skeleton skeleton = Create();
        var binding = Bind(skeleton, Part(2, "knee"), Part(1, "hip", Matrix4x4.CreateTranslation(1, 0, 0)));
        var rotated = new Matrix4x4[2];
        var model = Matrix4x4.CreateTranslation(10, 0, 0);

        binding.ComposeWorlds(skeleton, RotatedPalette(skeleton), model, rotated);

        Assert.Equal(new Vector3(10, 3, 0), Vector3.Transform(Vector3.Zero, rotated[0]));
        Assert.Equal(new Vector3(11, 2, 0), Vector3.Transform(Vector3.Zero, rotated[1]));
    }

    [Fact]
    public void Turned_rest_joint_cancels_orientation_without_removing_translation()
    {
        Skeleton skeleton = Create(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2));
        var binding = Bind(skeleton, Part(1, "hip", Matrix4x4.CreateTranslation(1, 0, 0)));
        var worlds = new Matrix4x4[1];

        binding.ComposeWorlds(skeleton, skeleton.ComposeRestPose(), Matrix4x4.Identity, worlds);

        Assert.True(Vector3.Distance(new Vector3(1, 2, 0), worlds[0].Translation) < 1e-5f);
        Assert.True(Vector3.Distance(Vector3.UnitX, Vector3.TransformNormal(Vector3.UnitX, worlds[0])) < 1e-5f);
    }

    [Fact]
    public void Reflected_scaled_joint_preserves_handedness_without_stretching_the_part()
    {
        Skeleton skeleton = Create();
        var binding = Bind(skeleton, Part(1, "hip", Matrix4x4.CreateTranslation(1, 0, 0)));
        Matrix4x4[] palette = skeleton.ComposeRestPose();
        palette[1] = Matrix4x4.CreateScale(-2, 3, 4) * Matrix4x4.CreateTranslation(0, 2, 0);
        var worlds = new Matrix4x4[1];

        binding.ComposeWorlds(skeleton, palette, Matrix4x4.CreateTranslation(10, 0, 0), worlds);

        Assert.Equal(new Vector3(9, 2, 0), worlds[0].Translation);
        Assert.Equal(-Vector3.UnitX, Vector3.TransformNormal(Vector3.UnitX, worlds[0]));
        Assert.Equal(Vector3.UnitY, Vector3.TransformNormal(Vector3.UnitY, worlds[0]));
        Assert.Equal(Vector3.UnitZ, Vector3.TransformNormal(Vector3.UnitZ, worlds[0]));
    }

    [Fact]
    public void Foreign_skeleton_with_equal_count_is_refused()
    {
        Skeleton skeleton = Create();
        Skeleton other = Create();
        var binding = Bind(skeleton, Part(1, "hip"));
        Matrix4x4[] palette = other.ComposeRestPose();
        var model = Matrix4x4.Identity;
        var worlds = new Matrix4x4[1];

        Assert.Throws<ArgumentException>(() => binding.ComposeWorlds(other, palette, model, worlds));
    }

    [Fact]
    public void Output_can_overlap_the_input_palette_without_changing_later_parts()
    {
        Skeleton skeleton = Create();
        var binding = Bind(skeleton, Part(1, "hip"), Part(2, "knee"));
        Matrix4x4[] palette = skeleton.ComposeRestPose();

        binding.ComposeWorlds(skeleton, palette, Matrix4x4.Identity, palette);

        Assert.Equal(new Vector3(0, 2, 0), palette[0].Translation);
        Assert.Equal(new Vector3(0, 1, 0), palette[1].Translation);
    }

    [Fact]
    public void Missing_constructor_arguments_are_refused()
    {
        Skeleton skeleton = Create();
        var definition = new AttachmentSetDefinition([Part(1, "hip")]);

        Assert.Throws<ArgumentNullException>(() => new AttachmentSetBinding(null!, Map(skeleton)));
        Assert.Throws<ArgumentNullException>(() => new AttachmentSetBinding(definition, null!));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Palette_length_must_match_the_skin(int length)
    {
        Skeleton skeleton = Create();
        var binding = Bind(skeleton, Part(1, "hip"));
        var palette = new Matrix4x4[length];
        var worlds = new Matrix4x4[1];

        Assert.Throws<ArgumentException>(() => binding.ComposeWorlds(skeleton, palette, Matrix4x4.Identity, worlds));
    }

    [Fact]
    public void Insufficient_output_space_is_refused_before_writing()
    {
        Skeleton skeleton = Create();
        var binding = Bind(skeleton, Part(1, "hip"), Part(2, "knee"));
        Matrix4x4[] worlds = [Matrix4x4.CreateTranslation(99, 0, 0)];

        Assert.Throws<ArgumentException>(() => binding.ComposeWorlds(skeleton, skeleton.ComposeRestPose(), Matrix4x4.Identity, worlds));
        Assert.Equal(new Vector3(99, 0, 0), worlds[0].Translation);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("root")]
    public void Unresolved_later_joint_names_the_part_and_reference(string joint)
    {
        Skeleton skeleton = Create();
        var definition = new AttachmentSetDefinition([Part(1, "hip"), Part(7, joint)]);

        var error = Assert.Throws<InvalidDataException>(() => new AttachmentSetBinding(definition, Map(skeleton)));

        Assert.Contains("7", error.Message);
        Assert.Contains(joint, error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Invalid_model_is_refused_before_writing(int kind)
    {
        Skeleton skeleton = Create();
        var binding = Bind(skeleton, Part(1, "hip"));
        Matrix4x4[] worlds = [Matrix4x4.CreateTranslation(99, 0, 0)];
        Matrix4x4 model = InvalidMatrix(kind);

        Assert.Throws<ArgumentException>(() => binding.ComposeWorlds(skeleton, skeleton.ComposeRestPose(), model, worlds));
        Assert.Equal(new Vector3(99, 0, 0), worlds[0].Translation);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Invalid_later_joint_pose_leaves_all_output_untouched(int kind)
    {
        Skeleton skeleton = Create();
        var binding = Bind(skeleton, Part(1, "hip"), Part(2, "knee"));
        Matrix4x4[] palette = skeleton.ComposeRestPose();
        palette[0] = InvalidMatrix(kind);
        Matrix4x4[] worlds = [Matrix4x4.CreateTranslation(99, 0, 0), Matrix4x4.CreateTranslation(98, 0, 0)];

        Assert.Throws<ArgumentException>(() => binding.ComposeWorlds(skeleton, palette, Matrix4x4.Identity, worlds));
        Assert.Equal(new Vector3(99, 0, 0), worlds[0].Translation);
        Assert.Equal(new Vector3(98, 0, 0), worlds[1].Translation);
    }

    static AttachmentSetBinding Bind(Skeleton skeleton, params RigidAttachmentPartDefinition[] parts) =>
        new(new AttachmentSetDefinition(parts), Map(skeleton));

    static Matrix4x4 InvalidMatrix(int kind)
    {
        Matrix4x4 matrix = Matrix4x4.Identity;
        if (kind == 0) matrix.M41 = float.NaN;
        else if (kind == 1) matrix.M11 = 0;
        else if (kind == 2) matrix.M14 = 0.5f;
        else if (kind == 3) matrix.M22 = float.PositiveInfinity;
        else if (kind == 4) matrix.M44 = float.BitDecrement(1f);
        else matrix.M14 = float.Epsilon;
        return matrix;
    }
}
