using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Examples;

// Keep this actor on its owning render thread. The scene, adapter and source are supplied by the host.
// This exact public-API example is compiled and exercised by KhaozEngine.Render.Tests.
public sealed class EquipmentCoverageConsumer : IDisposable
{
    readonly Scene3D scene;
    readonly IAttachmentScene adapter;
    readonly AttachmentSetCache cache;
    readonly SkinnedGltfMesh hostSkin;
    readonly SkinnedMeshHandle[] hosts;
    readonly Scene3D.SurfaceMaps[] maps;
    readonly ContractJointMap joints;
    readonly MotionKey actor;
    HostCoverageBinding? binding;
    BoundAttachmentSet? equipped;
    bool disposed;

    public Skeleton Skeleton { get; }

    public EquipmentCoverageConsumer(Scene3D scene, IAttachmentScene adapter, IAttachmentAssetSource assets,
        ReadOnlyMemory<byte> hostGlb, ReadOnlySpan<byte> hostSidecar, SkeletonContract contract, MotionKey actor)
    {
        this.scene = scene;
        this.adapter = adapter;
        this.actor = actor;
        // Read strictly, then import and upload every material slice from these exact host bytes.
        HostCoverageMap map = HostCoverageMapJson.Read(hostSidecar, "host.coverage.json");
        var parts = GltfLoader.LoadSkinnedPartsWithMaterials(hostGlb.ToArray(), "host.glb", map);
        hostSkin = parts[0].Mesh;
        Skeleton = hostSkin.Skeleton ?? throw new InvalidDataException("The host needs its exact skeleton.");
        joints = new ContractJointMap(Skeleton, contract);
        hosts = new SkinnedMeshHandle[parts.Count];
        maps = new Scene3D.SurfaceMaps[parts.Count];
        cache = new AttachmentSetCache(adapter, assets);
        try
        {
            for (int i = 0; i < parts.Count; i++)
            {
                maps[i] = scene.LoadSurfaceMaps(parts[i].Maps);
                hosts[i] = scene.LoadSkinnedMesh(parts[i].Mesh, maps[i]);
            }
            binding = new HostCoverageBinding(adapter, Skeleton, hosts, actor, CoverageFallback.UncoveredCurrentPose);
        }
        catch { Dispose(); throw; }
    }

    // Hashes name the exact fetched definition and companion bytes. Missing/malformed assets throw.
    // A failed replacement leaves the previous equipped set active. Cold equip may allocate.
    public bool TryEquip(string setKey, string setSha256, string companionKey, string companionSha256,
        ulong instanceId, out CoverageFailure failure)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        LoadedAttachmentSet loaded = cache.Get(setKey, setSha256);
        LoadedEquipmentCoverage coverage = cache.GetCoverage(loaded, companionKey, companionSha256);
        BoundAttachmentSet replacement = loaded.Bind(joints, hostSkin);
        if (!binding!.TrySetEquipment([new(instanceId, replacement, coverage)], out failure)) return false;
        equipped = replacement;
        return true;
    }

    public bool TryUnequip(out CoverageFailure failure)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!binding!.TrySetEquipment([], out failure)) return false;
        equipped = null;
        return true;
    }

    // Call after scene.Begin and before PrepareFrame, once per view, with one captured current pose.
    // A false return has explicitly queued the uncovered current pose and ordinary active equipment.
    public bool Queue(ReadOnlySpan<Matrix4x4> palette, in Matrix4x4 model, out CoverageFailure failure)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var template = new RigidInstanceDraw(default, Matrix4x4.Identity);
        bool admitted = binding!.TryPrepare(Skeleton, palette, model, template, out var frame, out failure);
        if (!admitted) frame = default;
        MeshOutlineGroup outline = scene.BeginMeshOutline(Color.White, 1.25f);
        foreach (SkinnedMeshHandle host in hosts)
        {
            var draw = new SkinnedInstanceDraw(host, model) { Motion = actor, Coverage = frame };
            scene.DrawSkinned(draw, palette);
            scene.DrawSkinnedOutline(outline, draw, palette);
        }
        if (admitted)
        {
            // Borrowed descriptors and palettes expire on the next preparation or equipment change.
            foreach (AttachmentDraw draw in binding.PreparedEquipmentDraws.Span)
            {
                if (draw.IsSkinned)
                {
                    adapter.DrawSkinned(draw.Skinned, draw.BoneMatrices.Span);
                    scene.DrawSkinnedOutline(outline, draw.Skinned, draw.BoneMatrices.Span);
                }
                else
                {
                    adapter.DrawMesh(draw.Rigid);
                    scene.DrawMeshOutline(outline, draw.Rigid);
                }
            }
        }
        else
        {
            // Never reuse a previous coverage token or prepared equipment snapshot after refusal.
            equipped?.Queue(Skeleton, palette, model, actor, template);
            equipped?.QueueOutline(Skeleton, palette, model, outline);
        }
        return admitted;
    }

    // Dispose before the borrowed scene. Production asset owners can share a longer-lived cache.
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        binding?.Dispose();
        cache.Dispose();
        foreach (SkinnedMeshHandle host in hosts)
            if (host.Generation != 0) scene.UnloadSkinnedMesh(host);
        foreach (Scene3D.SurfaceMaps material in maps) scene.UnloadSurfaceMaps(material);
    }
}

// Existing adapters need no coverage members. They can still draw ordinary equipment, and a requested
// coverage replacement through this adapter returns UnsupportedAdapter before publishing new state.
public sealed class LegacyEquipmentScene(Scene3DAttachmentScene inner) : IAttachmentScene
{
    public IAttachmentMeshLease UploadAttachment(IReadOnlyList<GltfMeshPart> parts) => inner.UploadAttachment(parts);
    public ISkinnedAttachmentMeshLease UploadSkinnedAttachment(IReadOnlyList<GltfSkinnedMeshPart> parts) =>
        inner.UploadSkinnedAttachment(parts);
    public void DrawMesh(in RigidInstanceDraw draw) => inner.DrawMesh(draw);
    public void DrawSkinned(in SkinnedInstanceDraw draw, ReadOnlySpan<Matrix4x4> palette) => inner.DrawSkinned(draw, palette);
    public void DrawMeshOutlineDissolved(MeshOutlineGroup group, MeshHandle mesh, Matrix4x4 world,
        float dissolve, bool complement) => inner.DrawMeshOutlineDissolved(group, mesh, world, dissolve, complement);
    public void DrawSkinnedOutlineDissolved(MeshOutlineGroup group, SkinnedMeshHandle mesh,
        ReadOnlySpan<Matrix4x4> palette, Matrix4x4 world, float dissolve, bool complement) =>
        inner.DrawSkinnedOutlineDissolved(group, mesh, palette, world, dissolve, complement);
}
