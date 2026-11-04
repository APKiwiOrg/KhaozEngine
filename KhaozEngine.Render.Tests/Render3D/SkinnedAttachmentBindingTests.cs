using System;
using System.Collections.Generic;
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

        AttachmentBindingDataException error = Assert.Throws<AttachmentBindingDataException>(() => new AttachmentSetBinding(definition, Map(Create())));

        Assert.Equal(AttachmentBindingRefusal.HostMeshRequired, error.Reason);
        Assert.Equal(2u, error.PartId);
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

        AttachmentBindingDataException error = Assert.Throws<AttachmentBindingDataException>(() => Bind(Mesh(Create()), garment));

        Assert.Equal(AttachmentBindingRefusal.GarmentJointUnresolved, error.Reason);
        Assert.Equal(2u, error.PartId);
        Assert.Equal(GarmentRef.Key, error.MeshKey);
        Assert.Equal(joint, error.JointName);
        Assert.NotNull(error.InnerException);
    }

    [Fact]
    public void Bind_tolerance_checks_every_element_and_exact_boundary()
    {
        SkinnedGltfMesh host = Mesh(Create());
        foreach (float boundary in new[] { 1e-4f, -1e-4f })
        {
            Assert.Null(Record.Exception(() => BindElement(host, 1, boundary)));
            AssertGarmentRefusal(AttachmentBindingRefusal.GarmentBindMismatch, () => BindElement(host, 1, MathF.CopySign(MathF.BitIncrement(1e-4f), boundary)));
        }
        for (int element = 0; element < 16; element++)
        {
            float source = Matrix4x4.Identity[element / 4, element % 4];
            AssertGarmentRefusal(AttachmentBindingRefusal.GarmentBindMismatch, () => BindElement(host, element, source + 2e-4f));
            foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            {
                AssertGarmentRefusal(AttachmentBindingRefusal.GarmentBindMismatch, () => BindElement(host, element, invalid));
                SkinnedGltfMesh invalidHost = Mesh(Create());
                invalidHost.InverseBind[1][element / 4, element % 4] = invalid;
                var error = Assert.Throws<AttachmentBindingDataException>(() => Bind(invalidHost, Mesh(Skin("hip"))));
                Assert.Equal(AttachmentBindingRefusal.HostInverseBindNonFinite, error.Reason);
                Assert.Equal(1, error.BoneIndex);
            }
        }
    }

    [Fact]
    public void Host_mesh_identity_and_joint_cap_are_checked()
    {
        Skeleton skeleton = Create();
        var definition = new AttachmentSetDefinition([new SkinnedAttachmentPartDefinition(2, GarmentRef)]);
        var meshes = new Dictionary<AttachmentMeshRef, SkinnedGltfMesh> { [GarmentRef] = Garment() };
        var identity = Assert.Throws<AttachmentBindingArgumentException>(() => new AttachmentSetBinding(definition, Map(skeleton), Mesh(Create()), meshes));
        Assert.Equal(AttachmentBindingRefusal.HostSkeletonMismatch, identity.Reason);
        Assert.Equal("hostMesh", identity.ParamName);

        Skeleton large = Skin(Enumerable.Range(0, 128).Select(i => "bone-" + i).ToArray());
        var contract = new SkeletonContract(Enumerable.Range(0, 128)
            .Select(i => new ContractJoint("bone-" + i, i == 0 ? null : "bone-0", true)).ToArray());
        var map = new ContractJointMap(large, contract);
        SkinnedGltfMesh host = Mesh(large);
        meshes[GarmentRef] = Mesh(large);
        var accepted = new AttachmentSetBinding(definition, map, host, meshes);
        Assert.Equal(128, accepted.SkinnedJointCount(2));
        meshes[GarmentRef] = Mesh(Skin(Enumerable.Range(0, 129).Select(i => "bone-" + i).ToArray()));
        AttachmentBindingDataException error = Assert.Throws<AttachmentBindingDataException>(() => new AttachmentSetBinding(definition, map, host, meshes));
        Assert.Equal(AttachmentBindingRefusal.GarmentJointCountOutsideRange, error.Reason);
        Assert.Equal(2u, error.PartId);
        Assert.Equal(GarmentRef.Key, error.MeshKey);
        Assert.Equal(129, error.JointCount);
        Assert.Equal(1, error.MinimumJointCount);
        Assert.Equal(128, error.MaximumJointCount);
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
        AttachmentBindingDataException missing = Assert.Throws<AttachmentBindingDataException>(() => new AttachmentSetBinding(definition,
            Map(host.Skeleton!), host, new Dictionary<AttachmentMeshRef, SkinnedGltfMesh>()));
        Assert.Equal(AttachmentBindingRefusal.GarmentMetadataMissing, missing.Reason);
        Assert.Equal(2u, missing.PartId);
        Assert.Equal(GarmentRef.Key, missing.MeshKey);
        AssertGarmentRefusal(AttachmentBindingRefusal.GarmentSkeletonMissing, () => Bind(host, Mesh(null)));
        var garment = AssertGarmentRefusal(AttachmentBindingRefusal.GarmentBindCountsMismatch,
            () => Bind(host, Mesh(Skin("hip", "knee"), [Matrix4x4.Identity])));
        Assert.Equal(2, garment.JointCount);
        Assert.Equal(1, garment.InverseBindCount);
        Assert.Equal(1, garment.RestCount);
        var invalidHost = Assert.Throws<AttachmentBindingDataException>(() => Bind(Mesh(Create(), [Matrix4x4.Identity]), Garment()));
        Assert.Equal(AttachmentBindingRefusal.HostBindCountsMismatch, invalidHost.Reason);
        Assert.Equal(2, invalidHost.JointCount);
        Assert.Equal(1, invalidHost.InverseBindCount);
        Assert.Equal(1, invalidHost.RestCount);
    }

    [Fact]
    public void Actual_host_inverse_bind_is_checked_even_when_rest_poses_match()
    {
        SkinnedGltfMesh host = Mesh(Create()), garment = Garment();
        Array.Copy(host.RestPose, garment.RestPose, 2);
        host.InverseBind[1] = Matrix4x4.CreateTranslation(1, 0, 0);

        AssertGarmentRefusal(AttachmentBindingRefusal.GarmentBindMismatch, () => Bind(host, garment));
    }

    [Fact]
    public void Imported_garment_bind_mismatch_names_the_part_and_mesh()
    {
        SkinnedAttachmentGlbFixture fixture = Fixture();
        fixture.SetInverseBind(1, Matrix4x4.CreateTranslation(2, 0, 0));
        SkinnedGltfMesh garment = GltfLoader.LoadSkinnedPartsWithMaterials(fixture.Build(), "mismatched garment")[0].Mesh;

        var error = AssertGarmentRefusal(AttachmentBindingRefusal.GarmentBindMismatch, () => Bind(Mesh(Create()), garment));
        Assert.Equal("knee", error.JointName);
    }

    [Fact]
    public void Invalid_frame_arguments_leave_the_destination_untouched()
    {
        SkinnedGltfMesh host = Mesh(Create());
        AttachmentSetBinding binding = Bind(host, Garment(), mixed: true);
        Matrix4x4[] palette = host.Skeleton!.ComposeRestPose();
        Matrix4x4 sentinel = Matrix4x4.CreateTranslation(99, 0, 0);
        Matrix4x4[] output = [sentinel, sentinel];
        var skeleton = Assert.Throws<AttachmentBindingArgumentException>(() => binding.CopySkinnedPalette(Create(), palette, 2, output));
        Assert.Equal(AttachmentBindingRefusal.SkeletonMismatch, skeleton.Reason);
        Assert.Equal("skeleton", skeleton.ParamName);
        var shortPalette = Assert.Throws<AttachmentBindingArgumentException>(() => binding.CopySkinnedPalette(host.Skeleton, palette[..1], 2, output));
        Assert.Equal(AttachmentBindingRefusal.HostPaletteLengthMismatch, shortPalette.Reason);
        Assert.Equal(1, shortPalette.ActualCount);
        Assert.Equal(2, shortPalette.RequiredCount);
        var longPalette = Assert.Throws<AttachmentBindingArgumentException>(() => binding.CopySkinnedPalette(host.Skeleton, new Matrix4x4[3], 2, output));
        Assert.Equal(AttachmentBindingRefusal.HostPaletteLengthMismatch, longPalette.Reason);
        Assert.Equal(3, longPalette.ActualCount);
        Assert.Equal(2, longPalette.RequiredCount);
        var capacity = Assert.Throws<AttachmentBindingArgumentException>(() => binding.CopySkinnedPalette(host.Skeleton, palette, 2, output.AsSpan(0, 1)));
        Assert.Equal(AttachmentBindingRefusal.GarmentPaletteOutputTooSmall, capacity.Reason);
        Assert.Equal(2u, capacity.PartId);
        Assert.Equal(1, capacity.ActualCapacity);
        Assert.Equal(2, capacity.RequiredCapacity);
        Assert.Equal("destination", capacity.ParamName);
        var rigid = Assert.Throws<AttachmentBindingArgumentException>(() => binding.CopySkinnedPalette(host.Skeleton, palette, 1, output));
        Assert.Equal(AttachmentBindingRefusal.PartHasNoGarmentPalette, rigid.Reason);
        Assert.Equal(1u, rigid.PartId);
        Assert.Equal("partId", rigid.ParamName);
        var missing = Assert.Throws<AttachmentBindingArgumentException>(() => binding.CopySkinnedPalette(host.Skeleton, palette, 99, output));
        Assert.Equal(AttachmentBindingRefusal.PartMissing, missing.Reason);
        Assert.Equal(99u, missing.PartId);
        Assert.Equal("partId", missing.ParamName);
        var missingCount = Assert.Throws<AttachmentBindingArgumentException>(() => binding.SkinnedJointCount(99));
        Assert.Equal(AttachmentBindingRefusal.PartMissing, missingCount.Reason);
        Assert.Equal(99u, missingCount.PartId);
        Assert.Equal("partId", missingCount.ParamName);
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

        var copy = Assert.Throws<AttachmentBindingArgumentException>(() => binding.CopySkinnedPalette(host.Skeleton, palette, 2, output));
        Assert.Equal(AttachmentBindingRefusal.HostJointPoseInvalid, copy.Reason);
        Assert.Equal(2u, copy.PartId);
        Assert.Equal(0, copy.BoneIndex);
        Assert.Equal("hostPalette", copy.ParamName);
        var world = Assert.Throws<AttachmentBindingArgumentException>(() => binding.ComposeWorlds(host.Skeleton, palette, Matrix4x4.Identity, output));
        Assert.Equal(AttachmentBindingRefusal.HostJointPoseInvalid, world.Reason);
        Assert.Equal(2u, world.PartId);
        Assert.Equal(0, world.BoneIndex);

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
        var identity = Assert.Throws<AttachmentBindingArgumentException>(() => loaded.Bind(map, Mesh(Create())));
        Assert.Equal(AttachmentBindingRefusal.HostSkeletonMismatch, identity.Reason);
        Assert.Equal("hostMesh", identity.ParamName);
        AttachmentBindingDataException mapOnly = Assert.Throws<AttachmentBindingDataException>(() => loaded.Bind(map));
        Assert.Equal(AttachmentBindingRefusal.HostMeshRequired, mapOnly.Reason);
        Assert.Equal(2u, mapOnly.PartId);
        SkinnedGltfMesh uploaded = scene.SkinnedParts[0][0].Mesh;
        uploaded.Skeleton!.JointToNode[0] = 1;
        uploaded.InverseBind[0] = Matrix4x4.CreateScale(9);
        uploaded.RestPose[0] = Matrix4x4.CreateScale(9);

        BoundAttachmentSet bound = loaded.Bind(map, Mesh(skeleton));

        Assert.Same(skeleton, bound.Skeleton);
        Assert.Equal(2, bound.DrawCount);
        var submission = Assert.Throws<AttachmentSubmissionStateException>(() => bound.WriteDraws(skeleton, skeleton.ComposeRestPose(),
            Matrix4x4.Identity, MotionKey.None, new RigidInstanceDraw(default, Matrix4x4.Identity), new RigidInstanceDraw[2]));
        Assert.Equal(AttachmentSubmissionRefusal.MixedDrawRequired, submission.Reason);
        Assert.Equal(2, submission.SkinnedPartCount);
        Assert.IsAssignableFrom<InvalidOperationException>(submission);
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

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void Garment_joint_must_refer_to_an_existing_node(int node)
    {
        SkinnedGltfMesh host = Mesh(Create()), garment = Mesh(Skin("hip", "knee"));
        Assert.Equal(2, Bind(host, garment).SkinnedJointCount(2));
        garment.Skeleton!.JointToNode[0] = node;

        var error = AssertGarmentRefusal(AttachmentBindingRefusal.GarmentNodeMissing, () => Bind(host, garment));

        Assert.Equal(0, error.BoneIndex);
        Assert.Equal(node, error.NodeIndex);
        Assert.Equal(2, error.NodeCount);
    }

    static AttachmentBindingDataException AssertGarmentRefusal(AttachmentBindingRefusal reason, Action action)
    {
        var error = Assert.Throws<AttachmentBindingDataException>(action);
        Assert.Equal(reason, error.Reason);
        Assert.Equal(2u, error.PartId);
        Assert.Equal(GarmentRef.Key, error.MeshKey);
        if (reason == AttachmentBindingRefusal.GarmentBindMismatch)
            Assert.Equal(1e-4f, error.BindTolerance);
        return error;
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
