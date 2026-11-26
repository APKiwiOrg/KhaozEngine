using System;
using System.Collections.Generic;
using System.Numerics;
using System.Security.Cryptography;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Attachments;

// Real format 8 import and host binding with no render device.
internal sealed class WornClothFloatingPointFixture : IDisposable, IAttachmentAssetSource, IAttachmentScene
{
    private readonly Dictionary<string, byte[]> _assets = new();
    private readonly AttachmentSetCache _cache;

    internal Skeleton Host { get; }
    internal BoundAttachmentSet Set { get; }

    internal WornClothFloatingPointFixture()
    {
        Host = new Skeleton([-1, 0], [
            new JointPose { Translation = Vector3.UnitY, Rotation = Quaternion.Identity, Scale = Vector3.One },
            new JointPose { Translation = Vector3.UnitY, Rotation = Quaternion.Identity, Scale = Vector3.One }],
            [0, 1], [0, 1], ["hip", "knee"]);
        var host = new SkinnedGltfMesh([new SkinnedVertex { BoneWeights = Vector4.UnitX },
            new SkinnedVertex { BoneWeights = Vector4.UnitX, Position = Vector3.UnitX },
            new SkinnedVertex { BoneWeights = Vector4.UnitX, Position = Vector3.UnitY }],
            [0, 1, 2], [Matrix4x4.CreateTranslation(0, -1, 0), Matrix4x4.CreateTranslation(0, -2, 0)],
            Host.ComposeRestPose(), Host);
        var map = new ContractJointMap(Host, new SkeletonContract([new("hip", null, true), new("knee", "hip", true)]));

        Vector3[] sheet = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY];
        var glb = SkinnedAttachmentGlbFixture.Chained();
        glb.SetIndexedMaterialParts([0, 1, 2], 2);
        glb.SetUniformSkinning(0, 3, new Vector4(3), Vector4.UnitX);
        glb.SetUniformSkinning(1, 3, Vector4.Zero, Vector4.UnitX);
        var mesh = new AttachmentMeshRef("fp-cloth.glb", Add("fp-cloth.glb", glb.Build()));
        var sidecar = new ClothSurfaceFile(mesh.Sha256, 6, 2, ClothMaterial.HeavyWoven,
            [.. sheet], [Vector2.Zero, Vector2.UnitX, Vector2.UnitY], [new(0, 1, 2)], [],
            [new(0, new(0, 0, 0)), new(1, new(0, 0, 1))],
            [new(new(0, 1, 0), 0, Vector3.UnitX, 0), new(new(0, 1, 1), 0, Vector3.UnitY, 0),
                new(new(0, 1, 2), 0, Vector3.UnitZ, 0)],
            [new(0, 1, 0), new(0, 1, 1), new(0, 1, 2)]);
        var reference = new ClothSurfaceRef("fp-cloth.kecs", Add("fp-cloth.kecs", ClothSurfaceJson.Write(sidecar)));
        var settings = new ChainSettings(0, 2, 1, 1, MathF.PI, .01f);
        var fallback = new ChainMotionDefinition([new ChainDefinition(["left0", "left1"], Vector3.UnitX * .5f, settings)], []);
        var definition = new AttachmentSetDefinition([new SkinnedAttachmentPartDefinition(1, mesh)
            { Motion = new ClothMotionDefinition(reference, fallback), Regions = [new("panel", 2)] }], AttachmentLayer.Armour, []);
        string definitionSha = Add("fp-cloth.json", AttachmentSetJson.Write(definition));
        _cache = new(this, this);
        Set = _cache.Get("fp-cloth.json", definitionSha).Bind(map, host);
    }

    private string Add(string key, byte[] bytes)
    {
        _assets.Add(key, bytes);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public ReadOnlyMemory<byte> Read(string key) => _assets[key];
    public void Dispose() => _cache.Dispose();
    public ISkinnedAttachmentMeshLease UploadSkinnedAttachment(IReadOnlyList<GltfSkinnedMeshPart> parts) => new Lease(parts.Count);
    public IAttachmentMeshLease UploadAttachment(IReadOnlyList<GltfMeshPart> parts) => throw new NotSupportedException();
    public void DrawMesh(in RigidInstanceDraw draw) => throw new NotSupportedException();
    public void DrawSkinned(in SkinnedInstanceDraw draw, ReadOnlySpan<Matrix4x4> boneMatrices) => throw new NotSupportedException();
    public void DrawMeshOutlineDissolved(MeshOutlineGroup group, MeshHandle mesh,
        Matrix4x4 world, float dissolve, bool dissolveComplement) => throw new NotSupportedException();
    public void DrawSkinnedOutlineDissolved(MeshOutlineGroup group, SkinnedMeshHandle mesh,
        ReadOnlySpan<Matrix4x4> boneMatrices, Matrix4x4 world, float dissolve, bool dissolveComplement) =>
        throw new NotSupportedException();

    private sealed class Lease : ISkinnedAttachmentMeshLease
    {
        internal Lease(int count)
        {
            var handles = new SkinnedMeshHandle[count];
            for (int i = 0; i < count; i++) handles[i] = new(i);
            Handles = handles;
        }

        public IReadOnlyList<SkinnedMeshHandle> Handles { get; }
        public void Dispose() { }
    }
}
