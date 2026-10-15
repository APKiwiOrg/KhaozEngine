using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.TileWorld;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class RigidAttachmentAssetTests
{
    [Fact]
    public void Conflicting_part_kinds_are_refused_before_any_mesh_read_or_upload()
    {
        using var harness = new AttachmentAssetHarness();
        var source = new AttachmentTestSource();
        var mesh = new AttachmentMeshRef("parts/unread.glb", new string('a', 64));
        var skinned = new SkinnedAttachmentPartDefinition(7, mesh);
        var definition = new AttachmentSetDefinition(
            [new RigidAttachmentPartDefinition(1, mesh, "hip", Matrix4x4.Identity), skinned]);
        string hash = source.Add("set.json", AttachmentSetJson.Write(definition));
        using var cache = new AttachmentSetCache(harness.Scene, source);
        int buffers = harness.Factory.Buffers.Count, textures = harness.Factory.Textures.Count;

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => cache.Get("set.json", hash));

        Assert.Contains("set.json", error.Message);
        Assert.Contains("7", error.Message);
        Assert.Contains("skinned", error.Message);
        Assert.Equal("set.json", Assert.Single(source.Reads).Key);
        Assert.Equal(buffers, harness.Factory.Buffers.Count);
        Assert.Equal(textures, harness.Factory.Textures.Count);
    }

    [Fact]
    public void Shared_mesh_is_read_and_uploaded_once_across_parts_and_sets()
    {
        using var harness = new AttachmentAssetHarness();
        var source = new AttachmentTestSource();
        AttachmentMeshRef mesh = source.Mesh("parts/shared.glb", AttachmentTestSource.Glb(materials: 2));
        string firstHash = source.Set("sets/first.json", mesh, mesh);
        string secondHash = source.Set("sets/second.json", mesh);
        using var cache = new AttachmentSetCache(harness.Scene, source);
        int from = harness.Factory.Buffers.Count;

        LoadedAttachmentSet first = cache.Get("sets/first.json", firstHash);
        LoadedAttachmentSet second = cache.Get("sets/second.json", secondHash);
        BoundAttachmentSet bound = first.Bind(RigidAttachmentTestRig.Map(RigidAttachmentTestRig.Create()));

        Assert.Same(first, cache.Get("sets/first.json", firstHash));
        Assert.Equal(1, source.Reads[mesh.Key]);
        Assert.Equal(1, source.Reads["sets/first.json"]);
        Assert.Equal(6, harness.Factory.Buffers.Count - from);
        Assert.Equal(firstHash, first.DefinitionSha256);
        Assert.Equal(4, bound.DrawCount);
        Assert.Single(second.Definition.Parts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Changed_bytes_are_refused_before_upload(bool changeDefinition)
    {
        using var harness = new AttachmentAssetHarness();
        var source = new AttachmentTestSource();
        AttachmentMeshRef mesh = source.Mesh("parts/mesh.glb", AttachmentTestSource.Glb());
        string hash = source.Set("set.json", mesh);
        source.Bytes[changeDefinition ? "set.json" : mesh.Key][0] ^= 1;
        using var cache = new AttachmentSetCache(harness.Scene, source);
        int buffers = harness.Factory.Buffers.Count, textures = harness.Factory.Textures.Count;

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => cache.Get("set.json", hash));

        Assert.Contains(changeDefinition ? "set.json" : mesh.Key, error.Message);
        Assert.Equal(buffers, harness.Factory.Buffers.Count);
        Assert.Equal(textures, harness.Factory.Textures.Count);
    }

    [Fact]
    public void Later_invalid_cpu_asset_prevents_every_upload()
    {
        using var harness = new AttachmentAssetHarness();
        var source = new AttachmentTestSource();
        AttachmentMeshRef first = source.Mesh("a.glb", AttachmentTestSource.Glb());
        AttachmentMeshRef invalid = source.Mesh("z.glb", Encoding.UTF8.GetBytes("invalid glb"));
        string hash = source.Set("set.json", first, invalid);
        using var cache = new AttachmentSetCache(harness.Scene, source);
        int buffers = harness.Factory.Buffers.Count;

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => cache.Get("set.json", hash));

        Assert.Contains("z.glb", error.Message);
        Assert.Equal(buffers, harness.Factory.Buffers.Count);
    }

    [Fact]
    public void Source_memory_is_copied_before_a_later_read_can_mutate_it()
    {
        using var harness = new AttachmentAssetHarness();
        var source = new AttachmentTestSource();
        AttachmentMeshRef first = source.Mesh("a.glb", AttachmentTestSource.Glb());
        AttachmentMeshRef second = source.Mesh("z.glb", AttachmentTestSource.Glb());
        string hash = source.Set("set.json", first, second);
        source.OnRead = key =>
        {
            if (key != "z.glb") return;
            Array.Clear(source.Bytes[first.Key]);
            Array.Clear(source.Bytes["set.json"]);
        };
        using var cache = new AttachmentSetCache(harness.Scene, source);

        LoadedAttachmentSet loaded = cache.Get("set.json", hash);

        Assert.Equal(hash, loaded.DefinitionSha256);
        Assert.Equal(2, loaded.Definition.Parts.Count);
        Assert.Equal(2, loaded.Bind(RigidAttachmentTestRig.Map(RigidAttachmentTestRig.Create())).DrawCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_later_upload_retires_only_staged_resources(bool failTexture)
    {
        using var harness = new AttachmentAssetHarness();
        var source = new AttachmentTestSource();
        byte[] glb = AttachmentTestSource.Glb(textured: true);
        AttachmentMeshRef previous = source.Mesh("previous.glb", glb);
        AttachmentMeshRef first = source.Mesh("a.glb", glb);
        AttachmentMeshRef second = source.Mesh("b.glb", glb);
        string previousHash = source.Set("previous.json", previous);
        string failedHash = source.Set("failed.json", previous, first, second);
        var recording = new AttachmentRecordingScene(harness.Scene);
        using var cache = new AttachmentSetCache(recording, source);
        int previousFrom = harness.Factory.Buffers.Count;
        LoadedAttachmentSet previousAsset = cache.Get("previous.json", previousHash);
        List<FakeBuffer> previousBuffers = harness.Factory.Buffers.GetRange(previousFrom,
            harness.Factory.Buffers.Count - previousFrom);
        int buffersFrom = harness.Factory.Buffers.Count, texturesFrom = harness.Factory.Textures.Count;
        if (failTexture) harness.Factory.FailTextureCreationAfter(4);
        else harness.Factory.FailBufferCreationAfter(4);

        Assert.Throws<InvalidOperationException>(() => cache.Get("failed.json", failedHash));
        List<FakeBuffer> stagedBuffers = harness.Factory.Buffers.GetRange(buffersFrom,
            harness.Factory.Buffers.Count - buffersFrom);
        List<FakeTexture> stagedTextures = harness.Factory.Textures.GetRange(texturesFrom,
            harness.Factory.Textures.Count - texturesFrom);
        harness.Factory.ThrowOnBufferCreate = 0;
        harness.Factory.ThrowOnTextureCreate = 0;
        harness.DrainRetired();

        Assert.NotEmpty(stagedBuffers);
        Assert.NotEmpty(stagedTextures);
        Assert.All(stagedBuffers, buffer => Assert.True(buffer.Disposed));
        Assert.All(stagedTextures, texture => Assert.True(texture.Disposed));
        Assert.All(previousBuffers, buffer => Assert.False(buffer.Disposed));
        Assert.Same(previousAsset, cache.Get("previous.json", previousHash));
        recording.DrawMesh(new RigidInstanceDraw(recording.Uploads[0][0], Matrix4x4.Identity));
        Assert.Equal(0, harness.Factory.LiveSetsNamingAFreedTexture);

        LoadedAttachmentSet retried = cache.Get("failed.json", failedHash);
        Assert.Equal(3, retried.Bind(RigidAttachmentTestRig.Map(RigidAttachmentTestRig.Create())).DrawCount);
        Assert.Equal(2, source.Reads[first.Key]);
        Assert.Equal(1, source.Reads[previous.Key]);
    }

    [Fact]
    public void Disposed_cache_releases_unique_uploads_and_refuses_borrowed_views()
    {
        using var harness = new AttachmentAssetHarness();
        var source = new AttachmentTestSource();
        AttachmentMeshRef mesh = source.Mesh("mesh.glb", AttachmentTestSource.Glb(textured: true));
        string hash = source.Set("set.json", mesh, mesh);
        using var cache = new AttachmentSetCache(harness.Scene, source);
        int from = harness.Factory.Buffers.Count, texturesFrom = harness.Factory.Textures.Count;
        LoadedAttachmentSet loaded = cache.Get("set.json", hash);
        ContractJointMap map = RigidAttachmentTestRig.Map(RigidAttachmentTestRig.Create());
        BoundAttachmentSet bound = loaded.Bind(map);
        Assert.Same(map.Skeleton, bound.Skeleton);
        List<FakeBuffer> buffers = harness.Factory.Buffers.GetRange(from, harness.Factory.Buffers.Count - from);
        List<FakeTexture> textures = harness.Factory.Textures.GetRange(texturesFrom, harness.Factory.Textures.Count - texturesFrom);

        cache.Dispose();
        cache.Dispose();
        Assert.Throws<ObjectDisposedException>(() => cache.Get("set.json", hash));
        Assert.Throws<ObjectDisposedException>(() => loaded.Bind(map));
        Assert.Throws<ObjectDisposedException>(() => bound.DrawCount);
        harness.DrainRetired();

        Assert.Equal(3, buffers.Count);
        Assert.Equal(3, textures.Count);
        Assert.All(buffers, buffer => Assert.True(buffer.Disposed));
        Assert.All(textures, texture => Assert.True(texture.Disposed));
    }

    [Fact]
    public void Invalid_joint_binding_leaves_the_loaded_asset_available()
    {
        using var harness = new AttachmentAssetHarness();
        var source = new AttachmentTestSource();
        AttachmentMeshRef mesh = source.Mesh("mesh.glb", AttachmentTestSource.Glb());
        var definition = new AttachmentSetDefinition([new RigidAttachmentPartDefinition(1, mesh, "unknown", Matrix4x4.Identity)]);
        string hash = source.Add("set.json", AttachmentSetJson.Write(definition));
        using var cache = new AttachmentSetCache(harness.Scene, source);
        LoadedAttachmentSet loaded = cache.Get("set.json", hash);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            loaded.Bind(RigidAttachmentTestRig.Map(RigidAttachmentTestRig.Create())));

        Assert.Contains("unknown", error.Message);
        Assert.Same(loaded, cache.Get("set.json", hash));
    }

    [Fact]
    public void Tile_scene_offers_optional_attachment_leases()
    {
        using var harness = new AttachmentAssetHarness();
        ITileWorldScene tileScene = new Scene3DTileWorldScene(harness.Scene);
        var attachmentScene = Assert.IsAssignableFrom<IAttachmentScene>(tileScene);
        int from = harness.Factory.Buffers.Count;
        using IAttachmentMeshLease lease = attachmentScene.UploadAttachment(
            GltfLoader.LoadPartsWithMaterials(AttachmentTestSource.Glb(), "fixture"));

        attachmentScene.DrawMesh(new RigidInstanceDraw(Assert.Single(lease.Handles), Matrix4x4.Identity));
        lease.Dispose();
        lease.Dispose();
        List<FakeBuffer> buffers = harness.Factory.Buffers.GetRange(from, harness.Factory.Buffers.Count - from);
        harness.DrainRetired();

        Assert.All(buffers, buffer => Assert.True(buffer.Disposed));
    }
}

internal sealed class AttachmentAssetHarness : IDisposable
{
    readonly FakeGpuDevice _device = new();
    internal FakeGpuResourceFactory Factory => (FakeGpuResourceFactory)_device.Factory;
    internal Scene3D Scene { get; }
    internal AttachmentAssetHarness() => Scene = new Scene3D(_device,
        new GpuOutputDescription(null, [GpuPixelFormat.R8G8B8A8UNorm]));
    internal void DrainRetired()
    {
        for (int i = 0; i < GpuRetireQueue.DefaultFrameDelay; i++) Scene.Begin();
    }
    public void Dispose() { Scene.Dispose(); _device.Dispose(); }
}

internal sealed class AttachmentRecordingScene(Scene3D scene) : IAttachmentScene
{
    readonly Scene3DAttachmentScene _adapter = new(scene);
    internal List<IReadOnlyList<MeshHandle>> Uploads { get; } = [];
    internal List<IReadOnlyList<SkinnedMeshHandle>> SkinnedUploads { get; } = [];
    internal List<IReadOnlyList<GltfSkinnedMeshPart>> SkinnedParts { get; } = [];
    public IAttachmentMeshLease UploadAttachment(IReadOnlyList<GltfMeshPart> parts)
    {
        IAttachmentMeshLease lease = _adapter.UploadAttachment(parts);
        Uploads.Add(lease.Handles);
        return lease;
    }
    public ISkinnedAttachmentMeshLease UploadSkinnedAttachment(IReadOnlyList<GltfSkinnedMeshPart> parts)
    {
        ISkinnedAttachmentMeshLease lease = _adapter.UploadSkinnedAttachment(parts);
        SkinnedUploads.Add(lease.Handles);
        SkinnedParts.Add(parts);
        return lease;
    }
    public void DrawMesh(in RigidInstanceDraw draw) => _adapter.DrawMesh(draw);
    public void DrawSkinned(in SkinnedInstanceDraw draw, ReadOnlySpan<Matrix4x4> bones) => _adapter.DrawSkinned(draw, bones);
    public void DrawMeshOutlineDissolved(MeshOutlineGroup group, MeshHandle mesh, Matrix4x4 world, float dissolve, bool complement) =>
        _adapter.DrawMeshOutlineDissolved(group, mesh, world, dissolve, complement);
    public void DrawSkinnedOutlineDissolved(MeshOutlineGroup group, SkinnedMeshHandle mesh,
        ReadOnlySpan<Matrix4x4> bones, Matrix4x4 world, float dissolve, bool complement) =>
        _adapter.DrawSkinnedOutlineDissolved(group, mesh, bones, world, dissolve, complement);
}

internal sealed class AttachmentTestSource : IAttachmentAssetSource
{
    internal Dictionary<string, byte[]> Bytes { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, int> Reads { get; } = new(StringComparer.Ordinal);
    internal Action<string>? OnRead { get; set; }
    public ReadOnlyMemory<byte> Read(string key)
    {
        Reads[key] = Reads.GetValueOrDefault(key) + 1;
        OnRead?.Invoke(key);
        return Bytes[key];
    }
    internal string Add(string key, byte[] bytes)
    {
        Bytes[key] = (byte[])bytes.Clone();
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
    internal AttachmentMeshRef Mesh(string key, byte[] bytes) => new(key, Add(key, bytes));
    internal string Set(string key, params AttachmentMeshRef[] meshes) => Add(key,
        AttachmentSetJson.Write(new AttachmentSetDefinition(meshes.Select((mesh, index) =>
            new RigidAttachmentPartDefinition((uint)index + 1, mesh, "hip", Matrix4x4.Identity)))));
    internal static byte[] Glb(int materials = 1, bool textured = false)
    {
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==");
        var mesh = new MeshBuilder<VertexPositionNormal, VertexTexture1>("triangle");
        for (int i = 0; i < materials; i++)
        {
            var material = new MaterialBuilder("material" + i);
            if (textured) material = material.WithBaseColor(new SharpGLTF.Memory.MemoryImage(png))
                .WithNormal(new SharpGLTF.Memory.MemoryImage(png))
                .WithMetallicRoughness(new SharpGLTF.Memory.MemoryImage(png));
            VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty> V(Vector3 p, Vector2 uv) =>
                new(new VertexPositionNormal(p, Vector3.UnitZ), new VertexTexture1(uv));
            mesh.UsePrimitive(material).AddTriangle(V(new(i, 0, 0), new(0, 0)),
                V(new(i + 1, 0, 0), new(1, 0)), V(new(i, 1, 0), new(0, 1)));
        }
        var scene = new SceneBuilder();
        scene.AddRigidMesh(mesh, Matrix4x4.Identity);
        string path = Path.Combine(Path.GetTempPath(), $"attachment-{Guid.NewGuid():N}.glb");
        try { scene.ToGltf2().SaveGLB(path); return File.ReadAllBytes(path); }
        finally { File.Delete(path); }
    }
}
