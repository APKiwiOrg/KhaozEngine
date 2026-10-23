using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Attachments;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class DqAttachmentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Matching_host_and_garment_profiles_bind(bool dq)
    {
        using var harness = new AttachmentAssetHarness();
        var source = new DqAttachmentSource();
        var recording = new AttachmentRecordingScene(harness.Scene);
        using var cache = new AttachmentSetCache(recording, source.Assets);
        SkinningProfile profile = dq ? SkinningProfile.DualQuaternionRigid : SkinningProfile.LinearBlend;
        LoadedAttachmentSet loaded = DqAttachmentSource.Get(cache, source.Hash, profile);
        Assert.Equal(profile, loaded.Profile);
        BoundAttachmentSet bound = loaded.Bind(source.Map, source.Host(profile));
        var draws = new AttachmentDraw[bound.DrawCount];
        Assert.Equal(2, bound.WriteDraws(source.Skeleton, DqCascadeScene.HalfTurn, Matrix4x4.Identity,
            MotionKey.From(7), new(default, Matrix4x4.Identity), draws));
        Assert.All(Assert.Single(recording.SkinnedParts), p => Assert.Equal(profile, p.Mesh.Profile));
        Assert.All(draws, d => Assert.Equal(profile, typeof(AttachmentDraw).GetProperty("Profile")?.GetValue(d)));
        Assert.All(draws, d => Assert.Equal(DqCascadeScene.HalfTurn.Reverse(), d.BoneMatrices.ToArray()));

        harness.Scene.Begin();
        if (dq)
        {
            bound.Queue(source.Skeleton, DqCascadeScene.HalfTurn, Matrix4x4.Identity, MotionKey.None);
            Assert.Equal(2, harness.Scene.QueuedSkinnedInstancesForTests.Count);
            MeshOutlineGroup group = harness.Scene.BeginMeshOutline(Color.White, 2);
            bound.QueueOutline(source.Skeleton, DqCascadeScene.HalfTurn, Matrix4x4.Identity, group);
            Assert.Equal(2, harness.Scene.MeshOutlinePartCount);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Profile_mismatch_or_cached_reuse_refuses_before_draw(bool dqFirst)
    {
        using var harness = new AttachmentAssetHarness();
        var source = new DqAttachmentSource();
        var recording = new AttachmentRecordingScene(harness.Scene);
        using var cache = new AttachmentSetCache(recording, source.Assets);
        SkinningProfile first = dqFirst ? SkinningProfile.DualQuaternionRigid : SkinningProfile.LinearBlend;
        SkinningProfile second = dqFirst ? SkinningProfile.LinearBlend : SkinningProfile.DualQuaternionRigid;
        LoadedAttachmentSet a = DqAttachmentSource.Get(cache, source.Hash, first);
        LoadedAttachmentSet b = DqAttachmentSource.Get(cache, source.Hash, second);
        Assert.NotSame(a, b);
        Assert.Same(a, DqAttachmentSource.Get(cache, source.Hash, first));
        Assert.Same(b, DqAttachmentSource.Get(cache, source.Hash, second));
        Assert.Same(dqFirst ? b : a, cache.Get("garment.json", source.Hash));
        Assert.Equal(2, recording.SkinnedUploads.Count);
        Assert.Empty(recording.SkinnedUploads[0].Intersect(recording.SkinnedUploads[1]));
        Assert.All(recording.SkinnedParts[0], p => Assert.Equal(first, p.Mesh.Profile));
        Assert.All(recording.SkinnedParts[1], p => Assert.Equal(second, p.Mesh.Profile));
        int buffers = harness.Factory.Buffers.Count;
        foreach ((LoadedAttachmentSet garment, SkinningProfile host) in new[] { (a, second), (b, first) })
        {
            InvalidDataException error = Assert.Throws<InvalidDataException>(() => garment.Bind(source.Map, source.Host(host)));
            Assert.Contains("part 7", error.Message);
            Assert.Contains("garment.glb", error.Message);
            Assert.Contains("host", error.Message);
            Assert.Contains("LinearBlend", error.Message);
            Assert.Contains("DualQuaternionRigid", error.Message);
        }
        Assert.Equal(buffers, harness.Factory.Buffers.Count);
        Assert.Empty(harness.Scene.QueuedSkinnedInstancesForTests);
        Assert.Equal(0, harness.Scene.MeshOutlinePartCount);
        Assert.Equal((0, 0, 0), harness.Scene.MotionKeyCounts);
        Assert.Equal(0, ((KhaozEngine.Render3D.Internal.PreparedSkinningStore)
            DqGpuReadback.Field(harness.Scene, "_preparedSkinning")).PoseCount);
        BoundAttachmentSet boundA = a.Bind(source.Map, source.Host(first));
        BoundAttachmentSet boundB = b.Bind(source.Map, source.Host(second));
        Assert.NotEqual(boundA.FitIdentity, boundB.FitIdentity);
        var drawsA = new AttachmentDraw[2];
        var drawsB = new AttachmentDraw[2];
        boundA.WriteDraws(source.Skeleton, DqCascadeScene.HalfTurn, Matrix4x4.Identity, MotionKey.From(7), new(default, Matrix4x4.Identity), drawsA);
        boundB.WriteDraws(source.Skeleton, DqCascadeScene.HalfTurn, Matrix4x4.Identity, MotionKey.From(7), new(default, Matrix4x4.Identity), drawsB);
        Assert.All(drawsA, draw => Assert.DoesNotContain(draw.Skinned.Motion, drawsB.Select(b => b.Skinned.Motion)));
    }

    [Fact]
    public void Direct_metadata_binding_refuses_mismatched_profiles()
    {
        var source = new DqAttachmentSource();
        SkinnedGltfMesh garment = GltfLoader.LoadSkinnedPartsWithMaterials(source.Assets.Bytes["garment.glb"],
            "synthetic garment", SkinningProfile.DualQuaternionRigid)[0].Mesh;
        var meshes = new Dictionary<AttachmentMeshRef, SkinnedGltfMesh> { [source.Mesh] = garment };
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => new AttachmentSetBinding(
            source.Definition, source.Map, source.Host(SkinningProfile.LinearBlend), meshes));
        Assert.Contains("part 7", error.Message);
        Assert.Contains("host", error.Message);
        var binding = new AttachmentSetBinding(source.Definition, source.Map,
            source.Host(SkinningProfile.DualQuaternionRigid), meshes);
        var palette = new Matrix4x4[2];
        binding.CopySkinnedPalette(source.Skeleton, DqCascadeScene.HalfTurn, 7, palette);
        Assert.Equal(DqCascadeScene.HalfTurn.Reverse(), palette);
    }

    [Fact]
    public void Upload_refuses_incoherent_material_profiles_before_mutation()
    {
        using var harness = new AttachmentAssetHarness();
        var source = new DqAttachmentSource();
        byte[] bytes = source.Assets.Bytes["garment.glb"];
        var dq = GltfLoader.LoadSkinnedPartsWithMaterials(bytes, "synthetic garment", SkinningProfile.DualQuaternionRigid);
        var lbs = GltfLoader.LoadSkinnedPartsWithMaterials(bytes, "synthetic garment");
        var adapter = new Scene3DAttachmentScene(harness.Scene);
        int buffers = harness.Factory.Buffers.Count, textures = harness.Factory.Textures.Count;
        ArgumentException error = Assert.Throws<ArgumentException>(() => adapter.UploadSkinnedAttachment([dq[0], lbs[1]]));
        Assert.Contains("material part 1", error.Message);
        Assert.Equal(buffers, harness.Factory.Buffers.Count);
        Assert.Equal(textures, harness.Factory.Textures.Count);
        Assert.Equal(0, harness.Scene.LiveSkinnedMeshCount);
    }

    [Fact]
    public void Rigid_socket_transform_is_unchanged()
    {
        using var harness = new AttachmentAssetHarness();
        var source = new DqAttachmentSource();
        AttachmentMeshRef rigid = source.Assets.Mesh("rigid.glb", AttachmentTestSource.Glb());
        string hash = source.Assets.Add("rigid.json", AttachmentSetJson.Write(new AttachmentSetDefinition([
            new RigidAttachmentPartDefinition(3, rigid, "a", Matrix4x4.CreateTranslation(.3f, .7f, 0))])));
        using var cache = new AttachmentSetCache(harness.Scene, source.Assets);
        LoadedAttachmentSet loaded = cache.Get("rigid.json", hash);
        BoundAttachmentSet lbs = loaded.Bind(source.Map, source.Host(SkinningProfile.LinearBlend));
        BoundAttachmentSet dq = loaded.Bind(source.Map, source.Host(SkinningProfile.DualQuaternionRigid));
        var oldDraws = new AttachmentDraw[1];
        var newDraws = new AttachmentDraw[1];
        Matrix4x4 model = Matrix4x4.CreateScale(2) * Matrix4x4.CreateTranslation(4, 5, 6);
        foreach ((BoundAttachmentSet bound, AttachmentDraw[] draws) in new[] { (lbs, oldDraws), (dq, newDraws) })
            bound.WriteDraws(source.Skeleton, DqCascadeScene.HalfTurn, model, MotionKey.From(7), new(default, model), draws);
        Assert.Equal(oldDraws[0].Rigid.World, newDraws[0].Rigid.World);
        Assert.Equal(BoneSocket.ComposeRigid(Matrix4x4.CreateTranslation(.3f, .7f, 0),
            DqCascadeScene.HalfTurn[0], model), newDraws[0].Rigid.World);
        Assert.NotEqual(DqAttachmentSource.FitIdentity(lbs), DqAttachmentSource.FitIdentity(dq));
        Assert.NotEqual(oldDraws[0].Rigid.Motion, newDraws[0].Rigid.Motion);
        Assert.Equal(DqAttachmentSource.FitIdentity(dq), DqAttachmentSource.FitIdentity(
            loaded.Bind(source.Map, source.Host(SkinningProfile.DualQuaternionRigid))));
    }
}

// Reflection leaves the test revision compilable before the new cache overload and identity exist.
internal sealed class DqAttachmentSource
{
    internal readonly AttachmentTestSource Assets = new();
    internal readonly Skeleton Skeleton = new([-1, 0], [JointPose.Identity, JointPose.Identity], [0, 1], [0, 1], ["a", "b"]);
    internal readonly ContractJointMap Map;
    internal readonly AttachmentMeshRef Mesh;
    internal readonly AttachmentSetDefinition Definition;
    internal readonly string Hash;

    internal DqAttachmentSource()
    {
        Map = new ContractJointMap(Skeleton, new SkeletonContract([new("a", null, true), new("b", "a", true)]));
        var glb = new SkinnedAttachmentGlbFixture(2);
        glb.Nodes[0]!["name"] = "a";
        glb.Nodes[1]!["name"] = "b";
        glb.SetSkinJoints(1, 0);
        Vector3[] positions = [new(-.7f, -.4f, 0), new(.7f, -.4f, 0), new(0, .5f, 0)];
        for (int i = 0; i < positions.Length; i++)
        {
            Vector3 p = positions[i];
            glb.SetElement(0, i, p.X, p.Y, p.Z);
            glb.SetElement(2, i, .5f, .5f, 0, 0);
        }
        glb.Accessors[0]!["min"] = new JsonArray(-.7f, -.4f, 0);
        glb.Accessors[0]!["max"] = new JsonArray(.7f, .5f, 0);
        glb.Root["materials"] = new JsonArray(new JsonObject(), new JsonObject());
        glb.Primitives[0]!["material"] = 0;
        glb.Primitives.Add(glb.Primitives[0]!.DeepClone());
        glb.Primitives[1]!["material"] = 1;
        Mesh = Assets.Mesh("garment.glb", glb.Build());
        Definition = new AttachmentSetDefinition([new SkinnedAttachmentPartDefinition(7, Mesh)]);
        Hash = Assets.Add("garment.json", AttachmentSetJson.Write(Definition));
    }

    internal SkinnedGltfMesh Host(SkinningProfile profile) => new([], Array.Empty<ushort>(),
        [Matrix4x4.Identity, Matrix4x4.Identity], [Matrix4x4.Identity, Matrix4x4.Identity], Skeleton, profile);

    internal static LoadedAttachmentSet Get(AttachmentSetCache cache, string hash, SkinningProfile profile)
    {
        MethodInfo? method = typeof(AttachmentSetCache).GetMethod("Get", [typeof(string), typeof(string), typeof(SkinningProfile)]);
        if (method is null) return cache.Get("garment.json", hash);
        try { return (LoadedAttachmentSet)method.Invoke(cache, ["garment.json", hash, profile])!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }

    internal static object FitIdentity(BoundAttachmentSet bound) =>
        typeof(BoundAttachmentSet).GetProperty("FitIdentity")?.GetValue(bound) ?? "profile identity missing";
}
