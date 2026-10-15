using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Attachments;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class SkinnedAttachmentAssetTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Mixed_load_verifies_all_CPU_assets_before_upload(bool invalidSkinned)
    {
        using var harness = new AttachmentAssetHarness();
        var source = new AttachmentTestSource();
        AttachmentMeshRef rigid = source.Mesh("a-rigid.glb", AttachmentTestSource.Glb());
        AttachmentMeshRef skinned = source.Mesh("b-skinned.glb", Glb());
        AttachmentMeshRef invalid = source.Mesh("z-invalid.glb", Encoding.UTF8.GetBytes("invalid glb"));
        string hash = Set(source, "set.json", Rigid(1, rigid), Skin(2, skinned),
            invalidSkinned ? Skin(3, invalid) : Rigid(3, invalid));
        using var cache = new AttachmentSetCache(harness.Scene, source);
        int beforeUploads = harness.Factory.Buffers.Count, textures = harness.Factory.Textures.Count;

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => cache.Get("set.json", hash));

        Assert.Contains("z-invalid.glb", error.Message);
        Assert.Equal(1, source.Reads[rigid.Key]);
        Assert.Equal(1, source.Reads[skinned.Key]);
        Assert.Equal(beforeUploads, harness.Factory.Buffers.Count);
        Assert.Equal(textures, harness.Factory.Textures.Count);
        Assert.Equal(0, harness.Scene.LiveSkinnedMeshCount);
    }

    [Fact]
    public void Shared_skinned_reference_is_read_and_uploaded_once_across_parts_and_sets()
    {
        using var harness = new AttachmentAssetHarness();
        var source = new AttachmentTestSource();
        AttachmentMeshRef rigid = source.Mesh("rigid.glb", AttachmentTestSource.Glb());
        AttachmentMeshRef shared = source.Mesh("shared-skinned.glb", Glb(materials: 2));
        string firstHash = Set(source, "first.json", Rigid(1, rigid), Skin(2, shared), Skin(3, shared));
        string secondHash = Set(source, "second.json", Skin(7, shared));
        var recording = new AttachmentRecordingScene(harness.Scene);
        using var cache = new AttachmentSetCache(recording, source);

        LoadedAttachmentSet first = cache.Get("first.json", firstHash);
        LoadedAttachmentSet second = cache.Get("second.json", secondHash);

        Assert.Same(first, cache.Get("first.json", firstHash));
        Assert.Equal(1, source.Reads[shared.Key]);
        Assert.Equal(1, source.Reads["first.json"]);
        IReadOnlyList<SkinnedMeshHandle> materialHandles = Assert.Single(recording.SkinnedUploads);
        Assert.Equal(2, materialHandles.Count);
        Assert.Equal(2, harness.Scene.LiveSkinnedMeshCount);
        Assert.Single(recording.Uploads);
        Assert.Single(second.Definition.Parts);
        Assert.Equal(firstHash, first.DefinitionSha256);
        Assert.Throws<InvalidDataException>(() => first.Bind(RigidAttachmentTestRig.Map(RigidAttachmentTestRig.Create())));
        Assert.Throws<InvalidDataException>(() => second.Bind(RigidAttachmentTestRig.Map(RigidAttachmentTestRig.Create())));
    }

    [Fact]
    public void Later_geometry_decode_failure_prevents_every_upload()
    {
        using var harness = new AttachmentAssetHarness();
        var source = new AttachmentTestSource();
        AttachmentMeshRef rigid = source.Mesh("a-rigid.glb", AttachmentTestSource.Glb());
        var fixture = new SkinnedAttachmentGlbFixture();
        int normals = fixture.Accessors.Count;
        fixture.Accessors.Add(new JsonObject
        {
            ["bufferView"] = fixture.AddView(new byte[3 * sizeof(float)]),
            ["componentType"] = 5126, ["count"] = 1, ["type"] = "VEC3",
        });
        fixture.SetElement(normals, 0, 0, 0, 1);
        fixture.Primitives[0]!["attributes"]!["NORMAL"] = normals;
        byte[] invalidGeometry = fixture.Build();
        AttachmentGlb.ValidateSkinnedEmbedded(invalidGeometry, "fixture");
        AttachmentMeshRef invalid = source.Mesh("z-skinned.glb", invalidGeometry);
        string hash = Set(source, "set.json", Rigid(1, rigid), Skin(2, invalid));
        using var cache = new AttachmentSetCache(harness.Scene, source);
        int buffers = harness.Factory.Buffers.Count, textures = harness.Factory.Textures.Count;

        Assert.ThrowsAny<Exception>(() => cache.Get("set.json", hash));

        Assert.Equal(1, source.Reads[invalid.Key]);
        Assert.Equal(buffers, harness.Factory.Buffers.Count);
        Assert.Equal(textures, harness.Factory.Textures.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Changed_skinned_bytes_are_refused_before_upload(bool changeDefinition)
    {
        using var harness = new AttachmentAssetHarness();
        var source = new AttachmentTestSource();
        AttachmentMeshRef skin = source.Mesh("skin.glb", Glb());
        string hash = Set(source, "set.json", Skin(1, skin));
        source.Bytes[changeDefinition ? "set.json" : skin.Key][0] ^= 1;
        using var cache = new AttachmentSetCache(harness.Scene, source);
        int buffers = harness.Factory.Buffers.Count;

        Assert.Throws<InvalidDataException>(() => cache.Get("set.json", hash));

        Assert.Equal(buffers, harness.Factory.Buffers.Count);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Warm_cache_cannot_bypass_requested_kind_policy(bool warmSkinned, bool mixed)
    {
        using var harness = new AttachmentAssetHarness();
        var source = new AttachmentTestSource();
        AttachmentMeshRef mesh = source.Mesh("shared.glb", warmSkinned ? Glb() : AttachmentTestSource.Glb());
        AttachmentPartDefinition correct = warmSkinned ? Skin(1, mesh) : Rigid(1, mesh);
        AttachmentPartDefinition wrong = warmSkinned ? Rigid(2, mesh) : Skin(2, mesh);
        string previousHash = Set(source, "previous.json", correct);
        string wrongHash = Set(source, "wrong.json", mixed ? [correct, wrong] : [wrong]);
        using var cache = new AttachmentSetCache(harness.Scene, source);
        LoadedAttachmentSet previous = cache.Get("previous.json", previousHash);
        int beforeUploads = harness.Factory.Buffers.Count, textures = harness.Factory.Textures.Count;

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => cache.Get("wrong.json", wrongHash));

        Assert.Contains("shared.glb", error.Message);
        Assert.Equal(1, source.Reads[mesh.Key]);
        Assert.Equal(beforeUploads, harness.Factory.Buffers.Count);
        Assert.Equal(textures, harness.Factory.Textures.Count);
        Assert.Same(previous, cache.Get("previous.json", previousHash));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cold_cache_applies_the_requested_kind_policy(bool skinnedBytes)
    {
        using var harness = new AttachmentAssetHarness();
        var source = new AttachmentTestSource();
        AttachmentMeshRef mesh = source.Mesh("wrong.glb", skinnedBytes ? Glb() : AttachmentTestSource.Glb());
        string hash = Set(source, "set.json", skinnedBytes ? Rigid(1, mesh) : Skin(1, mesh));
        using var cache = new AttachmentSetCache(harness.Scene, source);
        int buffers = harness.Factory.Buffers.Count;

        Assert.Throws<InvalidDataException>(() => cache.Get("set.json", hash));

        Assert.Equal(buffers, harness.Factory.Buffers.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Skinned_upload_rollback_preserves_previous_assets(bool failTexture)
    {
        using var harness = new AttachmentAssetHarness();
        var source = new AttachmentTestSource();
        byte[] rigidBytes = AttachmentTestSource.Glb(textured: true), skinBytes = Glb(textured: true);
        AttachmentMeshRef previousRigid = source.Mesh("previous-rigid.glb", rigidBytes);
        AttachmentMeshRef previousSkin = source.Mesh("previous-skinned.glb", skinBytes);
        AttachmentMeshRef first = source.Mesh("a-rigid.glb", rigidBytes);
        AttachmentMeshRef later = source.Mesh("b-skinned.glb", Glb(materials: 2, textured: true));
        string previousHash = Set(source, "previous.json", Rigid(1, previousRigid), Skin(2, previousSkin));
        string failedHash = Set(source, "failed.json", Rigid(1, previousRigid), Skin(2, previousSkin),
            Rigid(3, first), Skin(4, later));
        var recording = new AttachmentRecordingScene(harness.Scene);
        using var cache = new AttachmentSetCache(recording, source);
        int previousFrom = harness.Factory.Buffers.Count, previousTexturesFrom = harness.Factory.Textures.Count;
        LoadedAttachmentSet previous = cache.Get("previous.json", previousHash);
        List<FakeBuffer> previousBuffers = harness.Factory.Buffers.Skip(previousFrom).ToList();
        List<FakeTexture> previousTextures = harness.Factory.Textures.Skip(previousTexturesFrom).ToList();
        int buffersFrom = harness.Factory.Buffers.Count, texturesFrom = harness.Factory.Textures.Count;
        if (failTexture) harness.Factory.FailTextureCreationAfter(7);
        else harness.Factory.FailBufferCreationAfter(6);

        Assert.Throws<InvalidOperationException>(() => cache.Get("failed.json", failedHash));
        List<FakeBuffer> newBuffers = harness.Factory.Buffers.Skip(buffersFrom).ToList();
        List<FakeTexture> newTextures = harness.Factory.Textures.Skip(texturesFrom).ToList();
        Assert.Equal(2, recording.Uploads.Count);
        Assert.Single(recording.SkinnedUploads);
        harness.Factory.ThrowOnBufferCreate = 0;
        harness.Factory.ThrowOnTextureCreate = 0;
        harness.DrainRetired();

        Assert.NotEmpty(newBuffers);
        Assert.NotEmpty(newTextures);
        Assert.All(newBuffers, b => Assert.True(b.Disposed));
        Assert.All(newTextures, t => Assert.True(t.Disposed));
        Assert.All(previousBuffers, b => Assert.False(b.Disposed));
        Assert.All(previousTextures, t => Assert.False(t.Disposed));
        Assert.Same(previous, cache.Get("previous.json", previousHash));
        recording.DrawMesh(new RigidInstanceDraw(recording.Uploads[0][0], Matrix4x4.Identity));
        harness.Scene.DrawSkinned(new SkinnedInstanceDraw(recording.SkinnedUploads[0][0], Matrix4x4.Identity),
            recording.SkinnedParts[0][0].Mesh.RestPose);
        Assert.Equal(1, harness.Scene.SkinnedInstanceCount);
        Assert.Equal(0, harness.Factory.LiveSetsNamingAFreedTexture);

        LoadedAttachmentSet retried = cache.Get("failed.json", failedHash);
        Assert.Equal(4, retried.Definition.Parts.Count);
        Assert.Equal(3, harness.Scene.LiveSkinnedMeshCount);
        Assert.Equal(2, source.Reads[later.Key]);
        Assert.Equal(1, source.Reads[previousSkin.Key]);
        Assert.Equal(1, source.Reads[previousRigid.Key]);
    }

    [Fact]
    public void Skinned_source_memory_is_copied_before_a_later_read_can_mutate_it()
    {
        using var harness = new AttachmentAssetHarness();
        var source = new AttachmentTestSource();
        AttachmentMeshRef skin = source.Mesh("a-skinned.glb", Glb(materials: 2));
        AttachmentMeshRef later = source.Mesh("z-rigid.glb", AttachmentTestSource.Glb());
        string hash = Set(source, "set.json", Skin(1, skin), Rigid(2, later));
        source.OnRead = key =>
        {
            if (key != later.Key) return;
            Array.Clear(source.Bytes[skin.Key]);
            Array.Clear(source.Bytes["set.json"]);
        };
        var recording = new AttachmentRecordingScene(harness.Scene);
        using var cache = new AttachmentSetCache(recording, source);

        LoadedAttachmentSet loaded = cache.Get("set.json", hash);

        Assert.Equal(hash, loaded.DefinitionSha256);
        Assert.Equal(2, loaded.Definition.Parts.Count);
        IReadOnlyList<GltfSkinnedMeshPart> captured = Assert.Single(recording.SkinnedParts);
        Assert.Equal(2, captured.Count);
        Assert.Equal(new Vector3(1, 0, 0), captured[0].Mesh.Vertices[1].Position);
        Assert.Equal(Matrix4x4.Identity, captured[0].Mesh.InverseBind[0]);
        Assert.Equal("joint-0", captured[0].Mesh.Skeleton!.NodeNames[0]);
        Assert.Same(captured[0].Mesh.Skeleton, captured[1].Mesh.Skeleton);
    }

    [Fact]
    public void Skinned_cache_disposal_retires_each_handle_once()
    {
        using var harness = new AttachmentAssetHarness();
        var source = new AttachmentTestSource();
        AttachmentMeshRef skin = source.Mesh("skin.glb", Glb(materials: 2, textured: true));
        AttachmentMeshRef rigid = source.Mesh("rigid.glb", AttachmentTestSource.Glb(textured: true));
        string hash = Set(source, "set.json", Skin(1, skin), Skin(2, skin), Rigid(3, rigid));
        string otherHash = Set(source, "other.json", Skin(1, skin));
        using var cache = new AttachmentSetCache(harness.Scene, source);
        int from = harness.Factory.Buffers.Count, texturesFrom = harness.Factory.Textures.Count;
        LoadedAttachmentSet loaded = cache.Get("set.json", hash);
        cache.Get("other.json", otherHash);
        List<FakeBuffer> buffers = harness.Factory.Buffers.Skip(from).ToList();
        List<FakeTexture> textures = harness.Factory.Textures.Skip(texturesFrom).ToList();

        cache.Dispose();
        int retired = harness.Scene.RetiredResourceCount;
        cache.Dispose();

        Assert.Equal(7, buffers.Count);
        Assert.Equal(9, textures.Count);
        Assert.Equal(retired, harness.Scene.RetiredResourceCount);
        Assert.True(retired > 0);
        Assert.Equal(0, harness.Scene.LiveSkinnedMeshCount);
        Assert.Throws<ObjectDisposedException>(() => loaded.Definition);
        Assert.Throws<ObjectDisposedException>(() => loaded.DefinitionSha256);
        Assert.Throws<ObjectDisposedException>(() => loaded.Bind(RigidAttachmentTestRig.Map(RigidAttachmentTestRig.Create())));
        Assert.All(buffers, b => Assert.False(b.Disposed));
        harness.DrainRetired();

        Assert.All(buffers, b => Assert.True(b.Disposed));
        Assert.All(textures, t => Assert.True(t.Disposed));
        Assert.Equal(0, harness.Factory.LiveSetsNamingAFreedTexture);
        Assert.Equal(0, harness.Scene.RetiredResourceCount);
    }

    [Fact]
    public void Tile_scene_offers_typed_immutable_skinned_leases()
    {
        using var harness = new AttachmentAssetHarness();
        ITileWorldScene tileScene = new Scene3DTileWorldScene(harness.Scene);
        var attachmentScene = Assert.IsAssignableFrom<IAttachmentScene>(tileScene);
        IReadOnlyList<GltfSkinnedMeshPart> parts = GltfLoader.LoadSkinnedPartsWithMaterials(Glb(materials: 2), "fixture");
        int from = harness.Factory.Buffers.Count;
        using ISkinnedAttachmentMeshLease lease = attachmentScene.UploadSkinnedAttachment(parts);

        Assert.Equal(2, lease.Handles.Count);
        Assert.Throws<NotSupportedException>(() => ((IList<SkinnedMeshHandle>)lease.Handles)[0] = default);
        foreach (SkinnedMeshHandle handle in lease.Handles)
            harness.Scene.DrawSkinned(new SkinnedInstanceDraw(handle, Matrix4x4.Identity), parts[0].Mesh.RestPose);
        Assert.Equal(2, harness.Scene.SkinnedInstanceCount);
        lease.Dispose();
        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => lease.Handles);
        harness.DrainRetired();

        Assert.All(harness.Factory.Buffers.Skip(from), b => Assert.True(b.Disposed));
        Assert.Equal(0, harness.Scene.LiveSkinnedMeshCount);
    }

    static RigidAttachmentPartDefinition Rigid(uint id, AttachmentMeshRef mesh) => new(id, mesh, "hip", Matrix4x4.Identity);
    static SkinnedAttachmentPartDefinition Skin(uint id, AttachmentMeshRef mesh) => new(id, mesh);
    static string Set(AttachmentTestSource source, string key, params AttachmentPartDefinition[] parts) =>
        source.Add(key, AttachmentSetJson.Write(new AttachmentSetDefinition(parts)));

    static byte[] Glb(int materials = 1, bool textured = false)
    {
        var fixture = new SkinnedAttachmentGlbFixture();
        fixture.Root["materials"] = new JsonArray();
        if (textured)
        {
            byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==");
            fixture.Root["images"] = new JsonArray(new JsonObject
            {
                ["bufferView"] = fixture.AddView(png), ["mimeType"] = "image/png",
            });
            fixture.Root["textures"] = JsonNode.Parse("""[{"source":0}]""");
        }
        JsonObject first = fixture.Primitives[0]!.AsObject();
        int uvAccessor = fixture.Accessors.Count;
        fixture.Accessors.Add(new JsonObject
        {
            ["bufferView"] = fixture.AddView(new byte[3 * 2 * sizeof(float)]),
            ["componentType"] = 5126, ["count"] = 3, ["type"] = "VEC2",
        });
        first["attributes"]!["TEXCOORD_0"] = uvAccessor;
        fixture.SetElement(uvAccessor, 1, 1, 0);
        fixture.SetElement(uvAccessor, 2, 0, 1);
        first["material"] = 0;
        for (int i = 0; i < materials; i++)
        {
            JsonNode material = JsonNode.Parse(textured
                ? """{"pbrMetallicRoughness":{"baseColorTexture":{"index":0},"metallicRoughnessTexture":{"index":0}},"normalTexture":{"index":0}}"""
                : """{"pbrMetallicRoughness":{"baseColorFactor":[1,1,1,1]}}""")!;
            fixture.Root["materials"]!.AsArray().Add(material);
            if (i == 0) continue;
            JsonObject next = first.DeepClone().AsObject();
            next["material"] = i;
            fixture.Primitives.Add(next);
        }
        return fixture.Build();
    }
}
