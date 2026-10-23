using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed class DqAttachmentGpuTests(DqAttachmentScene fixture, ITestOutputHelper output) : IClassFixture<DqAttachmentScene>
{
    [GpuFact]
    public void Garment_materials_colour_and_independent_outline_use_DQ_on_GPU_and_CPU()
    {
        int matched = 0;
        foreach (bool gpu in new[] { false, true })
            foreach (bool outline in new[] { false, true })
                foreach (int material in new[] { 0, 1 })
                {
                    output.WriteLine($"{fixture.Device.Backend} {fixture.Device.Capabilities.DeviceName}: gpu={gpu} outline={outline} material={material}");
                    bool[] dq = fixture.Capture(gpu, outline, material);
                    bool[] lbs = fixture.Capture(gpu, outline, material, lbs: true);
                    bool[] reference = fixture.Capture(false, outline, material, reference: true);
                    Assert.True(dq.Count(p => p) > 20, "DQ garment witness must have a visible surface or outline rim.");
                    Assert.Equal(0, lbs.Count(p => p));
                    Assert.InRange(dq.Zip(reference).Count(p => p.First != p.Second), 0, 8);
                    matched++;
                }
        Assert.Equal(8, matched);
        output.WriteLine($"{fixture.Device.Backend} {fixture.Device.Capabilities.DeviceName}: matched {matched}/8 garment witnesses, skipped 0");
    }

    [GpuFact]
    public void Shared_attachment_scene_matches_fresh_after_profile_and_execution_changes()
    {
        fixture.Capture(false, false, 1, lbs: true);
        fixture.Capture(true, true, 0);
        bool[] aged = fixture.Capture(true, false, 1);
        using var fresh = new DqAttachmentScene(fixture.Device);
        bool[] baseline = fresh.Capture(true, false, 1);
        Assert.True(baseline.Count(p => p) > 20);
        Assert.Equal(baseline, aged);
        output.WriteLine($"{fixture.Device.Backend} {fixture.Device.Capabilities.DeviceName}: shared/fresh garment masks identical");
    }
}

public sealed class DqAttachmentScene : IDisposable
{
    const int Size = 96;
    readonly IGpuDevice? _shared;
    GpuDeviceContext? _context;
    Render3DPreview? _preview;
    AttachmentSetCache? _cache;
    DqAttachmentAdapter? _adapter;
    readonly DqAttachmentSource _source = new();
    readonly BoundAttachmentSet[] _bound = new BoundAttachmentSet[2];
    public DqAttachmentScene() { }
    internal DqAttachmentScene(IGpuDevice device) => _shared = device;
    internal IGpuDevice Device => _shared ?? (_context ??= GpuDeviceContext.CreateHeadless()).GpuDevice;

    internal bool[] Capture(bool gpu, bool outline, int material, bool lbs = false, bool reference = false)
    {
        if (_preview is null)
        {
            _preview = new Render3DPreview(Device, Size, Size);
            DqCascadeScene.Configure(_preview.Scene);
            _preview.Scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
            _preview.Scene.Post.Pixelated = false;
            _preview.Scene.Post.AmbientColor = Color.White;
            _preview.Scene.Post.LightColor = Color.Black;
            _preview.Scene.Camera.OrthoSize = 2;
            _adapter = new DqAttachmentAdapter(_preview.Scene);
            _cache = new AttachmentSetCache(_adapter, _source.Assets);
            for (int i = 0; i < 2; i++)
            {
                SkinningProfile profile = i == 0 ? SkinningProfile.DualQuaternionRigid : SkinningProfile.LinearBlend;
                _bound[i] = DqAttachmentSource.Get(_cache, _source.Hash, profile).Bind(_source.Map, _source.Host(profile));
            }
        }
        Scene3D scene = _preview.Scene;
        scene.UseGpuSkinning = gpu;
        _adapter!.Material = material;
        Matrix4x4 quarter = new(0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
        Matrix4x4[] joints = reference ? [quarter, quarter] : DqCascadeScene.HalfTurn;
        _preview.Capture(s =>
        {
            BoundAttachmentSet bound = _bound[lbs || reference ? 1 : 0];
            if (outline)
                bound.QueueOutline(_source.Skeleton, joints, Matrix4x4.Identity,
                    s.BeginMeshOutline(new Color(1, 0, 0, 1), 2, MeshOutlineOcclusion.None));
            else bound.Queue(_source.Skeleton, joints, Matrix4x4.Identity, MotionKey.None,
                new(default, Matrix4x4.Identity) { Tint = new Color(0, 1, 0, 1), CastsShadows = false });
        });
        byte[] pixels = _preview.ReadbackRgba();
        Assert.Equal(outline ? 0 : 1, scene.DrawnSkinnedInstances);
        return Enumerable.Range(0, Size * Size).Select(i => outline
            ? pixels[i * 4] > 100 && pixels[i * 4] > pixels[i * 4 + 1] * 2
            : pixels[i * 4 + 1] > 100 && pixels[i * 4 + 1] > pixels[i * 4] * 2).ToArray();
    }

    public void Dispose()
    {
        _cache?.Dispose();
        _preview?.Dispose();
        _context?.Dispose();
    }
}

// Only this test adapter crosses the closed stage-3 submission seam. Production activation stays refused.
internal sealed class DqAttachmentAdapter(Scene3D scene) : IAttachmentScene
{
    readonly Scene3DAttachmentScene _production = new(scene);
    readonly Dictionary<SkinnedMeshHandle, (SkinnedGltfMesh Mesh, int Material)> _meshes = [];
    internal int Material;
    public IAttachmentMeshLease UploadAttachment(IReadOnlyList<GltfMeshPart> parts) => _production.UploadAttachment(parts);
    public ISkinnedAttachmentMeshLease UploadSkinnedAttachment(IReadOnlyList<GltfSkinnedMeshPart> parts)
    {
        ISkinnedAttachmentMeshLease lease = _production.UploadSkinnedAttachment(parts);
        for (int i = 0; i < parts.Count; i++) _meshes.Add(lease.Handles[i], (parts[i].Mesh, i));
        return lease;
    }
    public void DrawMesh(in RigidInstanceDraw draw) => _production.DrawMesh(draw);
    public void DrawSkinned(in SkinnedInstanceDraw draw, ReadOnlySpan<Matrix4x4> bones)
    {
        var source = _meshes[draw.Mesh];
        if (source.Material == Material) DqGpuReadback.Queue(scene, source.Mesh, draw, bones.ToArray());
    }
    public void DrawMeshOutlineDissolved(MeshOutlineGroup group, MeshHandle mesh, Matrix4x4 world, float dissolve, bool complement) =>
        _production.DrawMeshOutlineDissolved(group, mesh, world, dissolve, complement);
    public void DrawSkinnedOutlineDissolved(MeshOutlineGroup group, SkinnedMeshHandle mesh,
        ReadOnlySpan<Matrix4x4> bones, Matrix4x4 world, float dissolve, bool complement)
    {
        var source = _meshes[mesh];
        if (source.Material == Material) DqOutlineScene.Queue(scene, source.Mesh, group, mesh, bones.ToArray(), world, dissolve, complement);
    }
}
