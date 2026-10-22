using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed class DqOutlineGpuTests(DqOutlineScene fixture, ITestOutputHelper output) : IClassFixture<DqOutlineScene>
{
    [GpuFact]
    public void Both_outline_masks_follow_the_DQ_surface()
    {
        int observed = 0;
        foreach (bool gpu in new[] { false, true })
            foreach (MeshOutlineOcclusion occlusion in new[] { MeshOutlineOcclusion.None, MeshOutlineOcclusion.SceneDepth })
                foreach (bool cutout in new[] { false, true })
                    foreach (bool mixed in new[] { false, true })
                        foreach (bool shifted in new[] { false, true })
                        {
                            output.WriteLine($"{fixture.Device.Backend} {fixture.Device.Capabilities.DeviceName}: gpu={gpu} occlusion={occlusion} cutout={cutout} mixed={mixed} shifted={shifted}");
                            var solid = fixture.Capture(gpu, occlusion, cutout, mixed, shifted, 0);
                            var lbs = fixture.Capture(gpu, occlusion, cutout, mixed, shifted, 0, lbs: true);
                            var reference = fixture.Capture(false, occlusion, cutout, mixed, shifted, 0, reference: true);
                            byte[] dqMaskWords = solid.Full, lbsMaskWords = lbs.Full;
                            int outlineOnlyMaskPixels = dqMaskWords.Count(p => p > 0);
                            Assert.True(outlineOnlyMaskPixels > 0);
                            Assert.False(dqMaskWords.SequenceEqual(lbsMaskWords));
                            Assert.True(outlineOnlyMaskPixels > lbsMaskWords.Count(p => p > 0));
                            AssertMaskNear(reference.FullMask, solid.FullMask);
                            Assert.True(solid.RimPixels > 0);
                            if (!mixed)
                            {
                                int colourDrawCount = solid.ColourDrawCount;
                                Assert.Equal(0, colourDrawCount);
                            }
                            else Assert.Equal(1, solid.ColourDrawCount);
                            if (cutout)
                            {
                                var uncut = fixture.Capture(gpu, occlusion, false, mixed, shifted, 0);
                                Assert.True(outlineOnlyMaskPixels < uncut.Full.Count(p => p > 0));
                            }
                            var plain = fixture.Capture(gpu, occlusion, cutout, mixed, shifted, 1);
                            var complement = fixture.Capture(gpu, occlusion, cutout, mixed, shifted, 2);
                            // Existing through-geometry outlines deliberately keep the complete cutout silhouette.
                            Assert.Equal(solid.Full, plain.Full);
                            Assert.Equal(solid.Full, complement.Full);
                            if (occlusion == MeshOutlineOcclusion.SceneDepth)
                            {
                                var cpuPlain = fixture.Capture(false, occlusion, cutout, mixed, shifted, 1);
                                AssertMaskNear(cpuPlain.VisibleMask, plain.VisibleMask);
                                AssertMaskNear(reference.VisibleMask, solid.VisibleMask);
                                int written = solid.VisibleMask.Count(p => p);
                                Assert.InRange(plain.VisibleMask.Count(p => p), 1, written - 1);
                                Assert.InRange(complement.VisibleMask.Count(p => p), 1, written - 1);
                                for (int p = 0; p < solid.Full.Length; p++)
                                {
                                    Assert.Equal(solid.VisibleMask[p], plain.VisibleMask[p] || complement.VisibleMask[p]);
                                    Assert.False(plain.VisibleMask[p] && complement.VisibleMask[p]);
                                }
                            }
                            observed++;
                            output.WriteLine($"DQ={outlineOnlyMaskPixels} LBS={lbs.Full.Count(p => p > 0)} rim={solid.RimPixels} colour={solid.ColourDrawCount}");
                        }
        Assert.Equal(32, observed);
        output.WriteLine($"{fixture.Device.Backend} {fixture.Device.Capabilities.DeviceName}: matched 32/32 outline witnesses");
    }

    [GpuFact]
    public void Shared_outline_scene_matches_fresh_after_pose_method_and_origin_changes()
    {
        fixture.Capture(false, MeshOutlineOcclusion.None, true, true, true, 2, lbs: true);
        fixture.Capture(true, MeshOutlineOcclusion.SceneDepth, false, false, false, 0);
        var aged = fixture.Capture(true, MeshOutlineOcclusion.SceneDepth, true, true, true, 1);
        using var fresh = new DqOutlineScene(fixture.Device);
        var baseline = fresh.Capture(true, MeshOutlineOcclusion.SceneDepth, true, true, true, 1);
        Assert.True(aged.Full.Count(p => p > 0) > 0);
        Assert.Equal(baseline.Full, aged.Full);
        Assert.Equal(baseline.VisibleMask, aged.VisibleMask);
        Assert.Equal(baseline.RimPixels, aged.RimPixels);
        output.WriteLine($"{fixture.Device.Backend} {fixture.Device.Capabilities.DeviceName}: shared/fresh outline masks identical");
    }

    static void AssertMaskNear(bool[] expected, bool[] actual) =>
        Assert.InRange(expected.Zip(actual).Count(p => p.First != p.Second), 0, 8);
}

public sealed class DqOutlineScene : IDisposable
{
    const int Size = 96;
    readonly IGpuDevice? _shared;
    GpuDeviceContext? _context;
    Render3DPreview? _preview;
    readonly SkinnedGltfMesh[] _meshes = [Patch(SkinningProfile.DualQuaternionRigid), Patch(SkinningProfile.LinearBlend)];
    readonly SkinnedMeshHandle[,,] _handles = new SkinnedMeshHandle[2, 2, 2];
    public DqOutlineScene() { }
    internal DqOutlineScene(IGpuDevice device) => _shared = device;
    internal IGpuDevice Device => _shared ?? (_context ??= GpuDeviceContext.CreateHeadless()).GpuDevice;

    internal static SkinnedGltfMesh Patch(SkinningProfile profile)
    {
        SkinnedGltfMesh patch = DqGpuReadback.Patch(profile);
        for (int i = 0; i < 4; i++)
            patch.Vertices[i].Position = new Vector3(i is 1 or 2 ? .7f : -.7f, i >= 2 ? .45f : -.45f, 0);
        return patch;
    }

    // Internal-only stage-3 activation. Prepare the same retained source as production, then append the
    // independent outline through its actual private seam. No public gate or consumer is enabled by tests.
    internal static void Queue(Scene3D scene, SkinnedGltfMesh mesh, MeshOutlineGroup group,
        SkinnedMeshHandle handle, Matrix4x4[] joints, Matrix4x4 world, float dissolve = 0, bool complement = false)
    {
        var entries = (System.Collections.IList)DqGpuReadback.Field(scene, "_skinnedMeshes");
        var source = (SkinnedGltfMesh)DqGpuReadback.Field(entries[handle.Index]!, "Source");
        Assert.Equal(mesh.Profile, source.Profile);
        var store = (PreparedSkinningStore)DqGpuReadback.Field(scene, "_preparedSkinning");
        PreparedSkinningPose pose = store.Prepare(source, joints, new("synthetic independent outline", null, 0));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object drawGroup = typeof(Scene3D).GetMethod("RequireMeshOutlineGroup", flags)!.Invoke(scene, [group])!;
        typeof(Scene3D).GetMethod("AppendSkinnedOutlinePart", flags)!.Invoke(scene,
            [drawGroup, handle, world, dissolve, complement, pose]);
    }

    internal sealed record CaptureResult(byte[] Full, bool[] VisibleMask, int ColourDrawCount, int RimPixels)
    {
        internal bool[] FullMask => Full.Select(p => p > 0).ToArray();
    }

    internal CaptureResult Capture(bool gpu, MeshOutlineOcclusion occlusion, bool cutout, bool mixed,
        bool shifted, int variant, bool lbs = false, bool reference = false)
    {
        if (_preview is null)
        {
            _preview = new Render3DPreview(Device, Size, Size);
            DqCascadeScene.Configure(_preview.Scene);
            _preview.Scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
            _preview.Scene.Post.Pixelated = false;
            _preview.Scene.Camera.OrthoSize = 3;
            var mask = _preview.Scene.LoadTexture([255, 255, 255, 0, 255, 255, 255, 255], 2, 1);
            for (int method = 0; method < 2; method++)
                for (int actor = 0; actor < 2; actor++)
                {
                    _handles[method, 0, actor] = _preview.Scene.LoadSkinnedMesh(_meshes[method]);
                    _handles[method, 1, actor] = _preview.Scene.LoadSkinnedMesh(_meshes[method],
                        new Scene3D.SurfaceMaps(mask, alphaCutoff: .5f));
                }
        }
        Scene3D scene = _preview.Scene;
        scene.UseGpuSkinning = gpu;
        scene.RenderOrigin = shifted ? new Vector3(32, -16, 8) : Vector3.Zero;
        _preview.Capture(s =>
        {
            int method = lbs || reference ? 1 : 0;
            int material = cutout ? 1 : 0;
            MeshOutlineGroup group = s.BeginMeshOutline(new Color(1f, 0f, 0f, 1f), 2, occlusion);
            for (int actor = 0; actor < 2; actor++)
            {
                Matrix4x4 world = Matrix4x4.CreateTranslation(actor == 0 ? -1.1f : 1.1f, 0, 0);
                if (mixed && actor == 0)
                    DqGpuReadback.Queue(s, _meshes[method], new SkinnedInstanceDraw(_handles[method, material, actor], world)
                    { CastsShadows = false }, _meshes[method].RestPose);
                if (reference && actor == 0)
                    world = new Matrix4x4(0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1) * world;
                Queue(s, _meshes[method], group, _handles[method, material, actor],
                    reference || actor == 1 ? _meshes[method].RestPose : DqCascadeScene.HalfTurn,
                    world, variant == 0 ? 0 : .5f, variant == 2);
            }
        });
        object renderer = DqGpuReadback.Field(scene, "_targetOutlines");
        byte[] full = Read((IGpuTexture)DqGpuReadback.Field(renderer, "_fullCoverage"), 1);
        bool[] visible = new bool[Size * Size];
        if (occlusion == MeshOutlineOcclusion.SceneDepth)
        {
            byte[] words = Read((IGpuTexture)DqGpuReadback.Field(renderer, "_visibleCoverage"), 4);
            for (int i = 0; i < visible.Length; i++)
                visible[i] = BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(words, i * 4)) > (Half)0;
        }
        byte[] colour = GpuReadback.ToRgba(Device, (IGpuTexture)DqGpuReadback.Field(_preview, "_target"), Size, Size);
        int rim = Enumerable.Range(0, Size * Size).Count(i => colour[i * 4] > 100
            && colour[i * 4] > colour[i * 4 + 1] * 2 && colour[i * 4] > colour[i * 4 + 2] * 2);
        return new(full, visible, scene.DrawnSkinnedInstances, rim);
    }

    byte[] Read(IGpuTexture texture, int pixelBytes)
    {
        using var staging = Device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
            texture.Width, texture.Height, texture.Format, GpuTextureUsage.Staging));
        using var commands = Device.Factory.CreateCommandList();
        using (GpuRecording.Open(Device, commands, "DQ outline mask readback")) commands.CopyTexture(texture, staging);
        Device.Submit(commands);
        Device.WaitForIdle();
        byte[] words = new byte[Size * Size * pixelBytes];
        MappedData map = Device.Map(staging, GpuMapMode.Read);
        try
        {
            for (int y = 0; y < Size; y++)
                Marshal.Copy(IntPtr.Add(map.Data, checked(y * (int)map.RowPitch)), words, y * Size * pixelBytes, Size * pixelBytes);
        }
        finally { Device.Unmap(staging); }
        return words;
    }

    public void Dispose()
    {
        _preview?.Dispose();
        _context?.Dispose();
    }
}
