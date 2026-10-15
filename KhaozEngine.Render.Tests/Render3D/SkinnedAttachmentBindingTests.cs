using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Attachments;
using Xunit;
using static KhaozEngine.Tests.Render3D.RigidAttachmentTestRig;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class SkinnedAttachmentBindingTests
{
    static readonly AttachmentMeshRef GarmentRef = new("parts/garment.glb", new string('b', 64));

    [Fact]
    public void Map_only_binding_checks_for_skinned_parts_before_resolving_rigid_joints()
    {
        var definition = new AttachmentSetDefinition([Part(1, "missing"), new SkinnedAttachmentPartDefinition(2, GarmentRef)]);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => new AttachmentSetBinding(definition, Map(Create())));

        Assert.Contains("host", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2", error.Message);
    }

    [Fact]
    public void Garment_palette_uses_named_host_skin_indices()
    {
        Skeleton hostSkeleton = Create();
        SkinnedGltfMesh host = Mesh(hostSkeleton,
            [Matrix4x4.CreateTranslation(0, -1, 0), Matrix4x4.CreateTranslation(0, -2, 0)]);
        SkinnedGltfMesh garment = Garment();
        garment.InverseBind[0] = host.InverseBind[1];
        garment.InverseBind[1] = host.InverseBind[0];
        AttachmentSetBinding binding = Bind(host, garment, mixed: true);
        Matrix4x4[] hostPalette = [Matrix4x4.CreateTranslation(3, 4, 5), Matrix4x4.CreateTranslation(6, 7, 8)];
        Matrix4x4[] garmentPalette = [default, default, Matrix4x4.CreateTranslation(99, 0, 0)];

        binding.CopySkinnedPalette(hostSkeleton, hostPalette, 2, garmentPalette);

        Assert.Equal(2, binding.SkinnedJointCount(2));
        Assert.Equal(0, binding.SkinnedJointCount(1));
        Assert.Equal(hostPalette[1], garmentPalette[0]);
        Assert.Equal(hostPalette[0], garmentPalette[1]);
        Assert.Equal(Matrix4x4.CreateTranslation(99, 0, 0), garmentPalette[2]);
        var worlds = new Matrix4x4[3];
        worlds[2] = Matrix4x4.CreateTranslation(98, 0, 0);
        Matrix4x4 model = Matrix4x4.CreateTranslation(10, 0, 0);
        binding.ComposeWorlds(hostSkeleton, hostSkeleton.ComposeRestPose(), model, worlds);
        Assert.Equal(2, binding.PartCount);
        Assert.Equal(new Vector3(11, 2, 0), worlds[0].Translation);
        Assert.Equal(model, worlds[1]);
        Assert.Equal(Matrix4x4.CreateTranslation(98, 0, 0), worlds[2]);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("missing")]
    [InlineData("Hip")]
    public void Named_non_skin_node_cannot_bind_garment(string joint)
    {
        SkinnedGltfMesh garment = Mesh(Skin(joint));

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => Bind(Mesh(Create()), garment));

        Assert.Contains("2", error.Message);
        Assert.Contains(GarmentRef.Key, error.Message);
        Assert.Contains(joint, error.Message);
    }

    [Fact]
    public void Bind_tolerance_checks_every_element_and_exact_boundary()
    {
        SkinnedGltfMesh host = Mesh(Create());
        foreach (float boundary in new[] { 1e-4f, -1e-4f })
        {
            Assert.Null(Record.Exception(() => BindElement(host, 1, boundary)));
            Assert.Throws<InvalidDataException>(() => BindElement(host, 1, MathF.CopySign(MathF.BitIncrement(1e-4f), boundary)));
        }
        for (int element = 0; element < 16; element++)
        {
            float source = Matrix4x4.Identity[element / 4, element % 4];
            Assert.Throws<InvalidDataException>(() => BindElement(host, element, source + 2e-4f));
            foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            {
                Assert.Throws<InvalidDataException>(() => BindElement(host, element, invalid));
                SkinnedGltfMesh invalidHost = Mesh(Create());
                invalidHost.InverseBind[1][element / 4, element % 4] = invalid;
                Assert.Throws<InvalidDataException>(() => Bind(invalidHost, Mesh(Skin("hip"))));
            }
        }
    }

    [Fact]
    public void Host_mesh_identity_and_joint_cap_are_checked()
    {
        Skeleton skeleton = Create();
        var definition = new AttachmentSetDefinition([new SkinnedAttachmentPartDefinition(2, GarmentRef)]);
        var meshes = new Dictionary<AttachmentMeshRef, SkinnedGltfMesh> { [GarmentRef] = Garment() };
        Assert.Throws<ArgumentException>(() => new AttachmentSetBinding(definition, Map(skeleton), Mesh(Create()), meshes));

        Skeleton large = Skin(Enumerable.Range(0, 128).Select(i => "bone-" + i).ToArray());
        var contract = new SkeletonContract(Enumerable.Range(0, 128)
            .Select(i => new ContractJoint("bone-" + i, i == 0 ? null : "bone-0", true)).ToArray());
        var map = new ContractJointMap(large, contract);
        SkinnedGltfMesh host = Mesh(large);
        meshes[GarmentRef] = Mesh(large);
        var accepted = new AttachmentSetBinding(definition, map, host, meshes);
        Assert.Equal(128, accepted.SkinnedJointCount(2));
        meshes[GarmentRef] = Mesh(Skin(Enumerable.Range(0, 129).Select(i => "bone-" + i).ToArray()));
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => new AttachmentSetBinding(definition, map, host, meshes));
        Assert.Contains("128", error.Message);
    }

    [Fact]
    public void Binding_snapshots_its_index_table()
    {
        SkinnedGltfMesh host = Mesh(Create()), garment = Garment();
        AttachmentSetBinding binding = Bind(host, garment);
        garment.Skeleton!.JointToNode[0] = 1;
        garment.Skeleton.JointToNode[1] = 0;
        Array.Fill(garment.InverseBind, Matrix4x4.CreateScale(9));
        Matrix4x4[] hostPalette = [Matrix4x4.CreateTranslation(3, 0, 0), Matrix4x4.CreateTranslation(7, 0, 0)];
        var output = new Matrix4x4[2];

        binding.CopySkinnedPalette(host.Skeleton!, hostPalette, 2, output);

        Assert.Equal(hostPalette[1], output[0]);
        Assert.Equal(hostPalette[0], output[1]);
    }

    [Fact]
    public void Garment_subset_and_reordered_imported_skin_are_accepted()
    {
        SkinnedGltfMesh host = Garment(reordered: true), garment = Garment();
        var map = new ContractJointMap(host.Skeleton!, new SkeletonContract([new("hip", null, true), new("knee", "hip", true)]));
        var definition = new AttachmentSetDefinition([new SkinnedAttachmentPartDefinition(2, GarmentRef)]);
        var binding = new AttachmentSetBinding(definition, map, host,
            new Dictionary<AttachmentMeshRef, SkinnedGltfMesh> { [GarmentRef] = garment });
        Matrix4x4[] palette = [Matrix4x4.CreateTranslation(1, 0, 0), Matrix4x4.CreateTranslation(2, 0, 0)];
        var output = new Matrix4x4[2];
        binding.CopySkinnedPalette(host.Skeleton!, palette, 2, output);
        Assert.Equal(palette[1], output[0]);
        Assert.Equal(palette[0], output[1]);

        AttachmentSetBinding subset = Bind(Mesh(Create()), Mesh(Skin("hip")));
        Assert.Equal(1, subset.SkinnedJointCount(2));
    }

    [Fact]
    public void Missing_garment_metadata_and_inconsistent_bind_counts_are_contextual()
    {
        SkinnedGltfMesh host = Mesh(Create());
        var definition = new AttachmentSetDefinition([new SkinnedAttachmentPartDefinition(2, GarmentRef)]);
        InvalidDataException missing = Assert.Throws<InvalidDataException>(() => new AttachmentSetBinding(definition,
            Map(host.Skeleton!), host, new Dictionary<AttachmentMeshRef, SkinnedGltfMesh>()));
        Assert.Contains("2", missing.Message);
        Assert.Contains(GarmentRef.Key, missing.Message);
        Assert.Throws<InvalidDataException>(() => Bind(host, Mesh(null)));
        Assert.Throws<InvalidDataException>(() => Bind(host, Mesh(Skin("hip", "knee"), [Matrix4x4.Identity])));
        Assert.Throws<InvalidDataException>(() => Bind(Mesh(Create(), [Matrix4x4.Identity]), Garment()));
    }

    [Fact]
    public void Actual_host_inverse_bind_is_checked_even_when_rest_poses_match()
    {
        SkinnedGltfMesh host = Mesh(Create()), garment = Garment();
        Array.Copy(host.RestPose, garment.RestPose, 2);
        host.InverseBind[1] = Matrix4x4.CreateTranslation(1, 0, 0);

        Assert.Throws<InvalidDataException>(() => Bind(host, garment));
    }

    [Fact]
    public void Imported_garment_bind_mismatch_names_the_part_mesh_and_joint()
    {
        SkinnedAttachmentGlbFixture fixture = Fixture();
        fixture.SetInverseBind(1, Matrix4x4.CreateTranslation(2, 0, 0));
        SkinnedGltfMesh garment = GltfLoader.LoadSkinnedPartsWithMaterials(fixture.Build(), "mismatched garment")[0].Mesh;

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => Bind(Mesh(Create()), garment));

        Assert.Contains("2", error.Message);
        Assert.Contains(GarmentRef.Key, error.Message);
        Assert.Contains("knee", error.Message);
    }

    [Fact]
    public void Invalid_frame_arguments_leave_the_destination_untouched()
    {
        SkinnedGltfMesh host = Mesh(Create());
        AttachmentSetBinding binding = Bind(host, Garment(), mixed: true);
        Matrix4x4[] palette = host.Skeleton!.ComposeRestPose();
        Matrix4x4 sentinel = Matrix4x4.CreateTranslation(99, 0, 0);
        Matrix4x4[] output = [sentinel, sentinel];
        Assert.Throws<ArgumentException>(() => binding.CopySkinnedPalette(Create(), palette, 2, output));
        Assert.Throws<ArgumentException>(() => binding.CopySkinnedPalette(host.Skeleton, palette[..1], 2, output));
        Assert.Throws<ArgumentException>(() => binding.CopySkinnedPalette(host.Skeleton, new Matrix4x4[3], 2, output));
        Assert.Throws<ArgumentException>(() => binding.CopySkinnedPalette(host.Skeleton, palette, 2, output.AsSpan(0, 1)));
        Assert.Throws<ArgumentException>(() => binding.CopySkinnedPalette(host.Skeleton, palette, 1, output));
        Assert.Throws<ArgumentException>(() => binding.CopySkinnedPalette(host.Skeleton, palette, 99, output));
        Assert.Throws<ArgumentException>(() => binding.SkinnedJointCount(99));
        Assert.All(output, matrix => Assert.Equal(sentinel, matrix));
    }

    [Fact]
    public void Invalid_garment_joint_pose_leaves_palettes_and_worlds_untouched()
    {
        SkinnedGltfMesh host = Mesh(Create());
        AttachmentSetBinding binding = Bind(host, Garment(), mixed: true);
        Matrix4x4[] palette = host.Skeleton!.ComposeRestPose();
        palette[0].M41 = float.NaN;
        Matrix4x4 sentinel = Matrix4x4.CreateTranslation(99, 0, 0);
        Matrix4x4[] output = [sentinel, sentinel];

        Assert.Throws<ArgumentException>(() => binding.CopySkinnedPalette(host.Skeleton, palette, 2, output));
        Assert.Throws<ArgumentException>(() => binding.ComposeWorlds(host.Skeleton, palette, Matrix4x4.Identity, output));

        Assert.All(output, matrix => Assert.Equal(sentinel, matrix));
    }

    [Fact]
    public void Loaded_binding_uses_captured_metadata_and_checks_host_identity()
    {
        using var harness = new AttachmentAssetHarness();
        var source = new AttachmentTestSource();
        AttachmentMeshRef mesh = source.Mesh(GarmentRef.Key, Fixture().Build());
        string hash = source.Add("set.json", AttachmentSetJson.Write(new AttachmentSetDefinition(
            [new SkinnedAttachmentPartDefinition(2, mesh), new SkinnedAttachmentPartDefinition(3, mesh)])));
        var scene = new AttachmentRecordingScene(harness.Scene);
        using var cache = new AttachmentSetCache(scene, source);
        LoadedAttachmentSet loaded = cache.Get("set.json", hash);
        Skeleton skeleton = Create();
        ContractJointMap map = Map(skeleton);
        Assert.Throws<ArgumentException>(() => loaded.Bind(map, Mesh(Create())));
        InvalidDataException mapOnly = Assert.Throws<InvalidDataException>(() => loaded.Bind(map));
        Assert.Contains("host", mapOnly.Message, StringComparison.OrdinalIgnoreCase);
        SkinnedGltfMesh uploaded = scene.SkinnedParts[0][0].Mesh;
        uploaded.Skeleton!.JointToNode[0] = 1;
        uploaded.InverseBind[0] = Matrix4x4.CreateScale(9);
        uploaded.RestPose[0] = Matrix4x4.CreateScale(9);

        BoundAttachmentSet bound = loaded.Bind(map, Mesh(skeleton));

        Assert.Same(skeleton, bound.Skeleton);
        Assert.Equal(2, bound.DrawCount);
        Assert.Throws<InvalidOperationException>(() => bound.WriteDraws(skeleton, skeleton.ComposeRestPose(),
            Matrix4x4.Identity, MotionKey.None, new RigidInstanceDraw(default, Matrix4x4.Identity), new RigidInstanceDraw[2]));
        cache.Dispose();
        Assert.Throws<ObjectDisposedException>(() => loaded.Bind(map, Mesh(skeleton)));
    }

    [Fact]
    public void Frame_palette_copy_allocates_no_managed_memory()
    {
        SkinnedGltfMesh host = Mesh(Create());
        AttachmentSetBinding binding = Bind(host, Garment());
        Matrix4x4[] palette = host.Skeleton!.ComposeRestPose(), output = new Matrix4x4[2];
        binding.CopySkinnedPalette(host.Skeleton, palette, 2, output);

        AllocAssert.NoPerCallAllocation("a skinned attachment palette copy",
            () => binding.CopySkinnedPalette(host.Skeleton, palette, 2, output));
    }

    static AttachmentSetBinding Bind(SkinnedGltfMesh host, SkinnedGltfMesh garment, bool mixed = false) =>
        new(new AttachmentSetDefinition(mixed
            ? [new SkinnedAttachmentPartDefinition(2, GarmentRef), Part(1, "hip", Matrix4x4.CreateTranslation(1, 0, 0))]
            : [new SkinnedAttachmentPartDefinition(2, GarmentRef)]), Map(host.Skeleton!), host,
            new Dictionary<AttachmentMeshRef, SkinnedGltfMesh> { [GarmentRef] = garment });

    static AttachmentSetBinding BindElement(SkinnedGltfMesh host, int element, float value)
    {
        SkinnedGltfMesh garment = Mesh(Skin("hip"));
        garment.InverseBind[0][element / 4, element % 4] = value;
        return Bind(host, garment);
    }

    static SkinnedGltfMesh Mesh(Skeleton? skeleton, Matrix4x4[]? binds = null)
    {
        binds ??= Enumerable.Repeat(Matrix4x4.Identity, skeleton?.BoneCount ?? 1).ToArray();
        return new SkinnedGltfMesh([], Array.Empty<ushort>(), binds, (Matrix4x4[])binds.Clone(), skeleton);
    }

    static Skeleton Skin(params string[] names) => new(
        Enumerable.Range(0, names.Length).Select(i => i == 0 ? -1 : 0).ToArray(),
        Enumerable.Repeat(JointPose.Identity, names.Length).ToArray(),
        Enumerable.Range(0, names.Length).ToArray(), Enumerable.Range(0, names.Length).ToArray(), names);

    static SkinnedGltfMesh Garment(bool reordered = false) =>
        GltfLoader.LoadSkinnedPartsWithMaterials(Fixture(reordered).Build(), "garment fixture")[0].Mesh;

    static SkinnedAttachmentGlbFixture Fixture(bool reordered = false)
    {
        var fixture = new SkinnedAttachmentGlbFixture(2);
        fixture.Nodes[0]!["name"] = "hip";
        fixture.Nodes[1]!["name"] = "knee";
        fixture.SetSkinJoints(reordered ? [1, 0] : [0, 1]);
        return fixture;
    }
}
