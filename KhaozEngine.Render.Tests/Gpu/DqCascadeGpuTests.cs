using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed class DqCascadeGpuTests(DqCascadeScene fixture, ITestOutputHelper output) : IClassFixture<DqCascadeScene>
{
    [GpuFact]
    public void DQ_cascade_silhouettes_follow_the_DQ_body()
    {
        int observedCascadeCount = 0, requestedCascadeCount = 0;
        foreach (int cascades in new[] { 1, 2, 3, 4 })
        foreach (int actor in new[] { 0, 1, 2 }) // visible, shadow-only, behind the camera but up-light
        foreach (bool gpu in new[] { false, true })
        foreach (bool cutout in new[] { false, true })
        {
            output.WriteLine($"{fixture.Device.Backend} {fixture.Device.Capabilities.DeviceName}: " +
                $"checking count={cascades} actor={actor} gpu={gpu} cutout={cutout}");
            float[][] solid = fixture.Capture(cascades, actor, gpu, cutout, 0, false);
            float[][] lbs = fixture.Capture(cascades, actor, gpu, cutout, 0, false, lbs: true);
            float[][] plain = fixture.Capture(cascades, actor, gpu, cutout, 1, false);
            float[][] complement = fixture.Capture(cascades, actor, gpu, cutout, 2, false);
            float[][] inverted = fixture.Capture(cascades, actor, gpu, cutout, 3, false);
            float[][] shifted = fixture.Capture(cascades, actor, gpu, cutout, 1, true);
            float[][] shiftedCpu = fixture.Capture(cascades, actor, false, cutout, 1, true);
            float[][] shiftedSolid = fixture.Capture(cascades, actor, gpu, cutout, 0, true);
            float[][] shiftedLbs = fixture.Capture(cascades, actor, gpu, cutout, 0, true, lbs: true);
            float[][] cpu = fixture.Capture(cascades, actor, false, cutout, 1, false);
            requestedCascadeCount += cascades;
            for (int c = 0; c < cascades; c++)
            {
                float[] dqDepthWords = solid[c], lbsDepthWords = lbs[c];
                int dqWitnessPixelCount = dqDepthWords.Count(Written);
                Assert.True(dqWitnessPixelCount > 0, $"Missing witness cascade={c}, actor={actor}, gpu={gpu}, cutout={cutout}");
                Assert.False(dqDepthWords.SequenceEqual(lbsDepthWords));
                Assert.Equal(0, lbsDepthWords.Count(Written)); // exact half-turn LBS collapses this entire quad
                Assert.InRange(plain[c].Count(Written), 1, dqWitnessPixelCount - 1);
                Assert.InRange(complement[c].Count(Written), 1, dqWitnessPixelCount - 1);
                for (int p = 0; p < dqDepthWords.Length; p++)
                {
                    Assert.Equal(Written(dqDepthWords[p]), Written(plain[c][p]) || Written(complement[c][p]));
                    Assert.False(Written(plain[c][p]) && Written(complement[c][p]));
                }
                AssertDepthNear(complement[c], inverted[c]);
                // FitCascade snaps in render-relative light space. Different origins can move the atlas grid
                // by a fraction of a texel. Compare execution methods at each origin's own grid, not raw pixels
                // from two different projections. The CPU rigid-depth path supplies the independent reference.
                AssertDepthNear(shiftedCpu[c], shifted[c]);
                Assert.True(shiftedSolid[c].Count(Written) > 0);
                Assert.Equal(0, shiftedLbs[c].Count(Written));
                Assert.False(shiftedSolid[c].SequenceEqual(shiftedLbs[c]));
                Assert.InRange(shifted[c].Count(Written), 1, shiftedSolid[c].Count(Written) - 1);
                AssertDepthNear(plain[c], cpu[c]);
                observedCascadeCount++;
                output.WriteLine($"{fixture.Device.Backend} {fixture.Device.Capabilities.DeviceName}: " +
                    $"count={cascades} cascade={c} actor={actor} gpu={gpu} cutout={cutout} " +
                    $"DQ={dqWitnessPixelCount} LBS=0 dissolve={plain[c].Count(Written)}/{complement[c].Count(Written)}");
            }
            if (cutout)
            {
                float[][] uncut = fixture.Capture(cascades, actor, gpu, false, 0, false);
                for (int c = 0; c < cascades; c++) Assert.True(solid[c].Count(Written) < uncut[c].Count(Written));
            }
        }
        Assert.Equal(requestedCascadeCount, observedCascadeCount);
        output.WriteLine($"requested={requestedCascadeCount} observed={observedCascadeCount}, all cascade witnesses checked");
    }

    [GpuFact]
    public void Shared_cascade_scene_matches_fresh_after_variant_execution_and_origin_changes()
    {
        fixture.Capture(1, 1, false, true, 2, true);
        fixture.Capture(3, 2, true, false, 3, false, lbs: true);
        float[][] aged = fixture.Capture(4, 0, true, true, 1, false);
        using var fresh = new DqCascadeScene(fixture.Device);
        float[][] baseline = fresh.Capture(4, 0, true, true, 1, false);
        for (int c = 0; c < 4; c++)
        {
            Assert.True(baseline[c].Count(Written) > 0);
            Assert.Equal(baseline[c], aged[c]);
        }
        output.WriteLine($"{fixture.Device.Backend} {fixture.Device.Capabilities.DeviceName}: shared/fresh depth words identical in all 4 cascades");
    }

    static bool Written(float depth) => float.IsFinite(depth) && depth < 1;

    static void AssertDepthNear(float[] expected, float[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        int differentMask = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(float.IsFinite(actual[i]));
            if (Written(expected[i]) != Written(actual[i])) differentMask++;
            else Assert.InRange(MathF.Abs(expected[i] - actual[i]), 0, 2e-5f);
        }
        Assert.InRange(differentMask, 0, 8);
    }
}

public sealed class DqCascadeScene : IDisposable
{
    readonly IGpuDevice? _shared;
    GpuDeviceContext? _context;
    Render3DPreview? _preview;
    readonly SkinnedGltfMesh[] _meshes = [Patch(SkinningProfile.DualQuaternionRigid), Patch(SkinningProfile.LinearBlend)];
    readonly SkinnedMeshHandle[,] _handles = new SkinnedMeshHandle[2, 2];
    public DqCascadeScene() { }
    internal DqCascadeScene(IGpuDevice device) => _shared = device;
    internal IGpuDevice Device => _shared ?? (_context ??= GpuDeviceContext.CreateHeadless()).GpuDevice;

    internal static Matrix4x4[] HalfTurn => [Matrix4x4.Identity,
        new(-1, 0, 0, 0, 0, -1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1)];

    internal static SkinnedGltfMesh Patch(SkinningProfile profile)
    {
        SkinnedGltfMesh patch = DqGpuReadback.Patch(profile);
        for (int i = 0; i < 4; i++)
            patch.Vertices[i].Position = new Vector3(i is 1 or 2 ? .8f : -.8f, i >= 2 ? .8f : -.8f, 0);
        // Both windings exercise the depth pass's front-face culling without relying on a backend convention.
        return new SkinnedGltfMesh(patch.Vertices, new ushort[] { 0, 1, 2, 0, 2, 3, 2, 1, 0, 3, 2, 0 },
            patch.InverseBind, patch.RestPose, null, profile);
    }

    internal static void Configure(Scene3D scene)
    {
        scene.Post.Starfield = false;
        scene.Post.Outline = false;
        scene.Post.Quality.AntiAliasing = AntiAliasing.Off;
        scene.Post.RenderScale = RenderScale.MatchViewport;
        scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
        scene.Post.Quality.Shadows.ShadowNearDistance = 6;
        scene.Post.Quality.Shadows.ShadowMaxDistance = 16;
        scene.Post.LightDirection = -Vector3.UnitZ;
        scene.Camera.Azimuth = 0;
        scene.Camera.Elevation = 0;
        scene.Camera.Distance = 8;
        scene.Camera.OrthoSize = 4;
        scene.Camera.NearPlane = .1f;
        scene.Camera.FarPlane = 20;
    }

    internal float[][] Capture(int cascades, int actor, bool gpu, bool cutout, int variant, bool shifted, bool lbs = false)
    {
        if (_preview is null)
        {
            _preview = new Render3DPreview(Device, 32, 32,
                new ShadowSettings { Mode = ShadowMode.ShadowMap, ShadowMapResolution = 256, ShadowCascadeCount = 4 });
            Configure(_preview.Scene);
            var mask = _preview.Scene.LoadTexture([255, 255, 255, 0, 255, 255, 255, 255], 2, 1);
            for (int i = 0; i < 2; i++)
            {
                _handles[i, 0] = _preview.Scene.LoadSkinnedMesh(_meshes[i]);
                _handles[i, 1] = _preview.Scene.LoadSkinnedMesh(_meshes[i], new Scene3D.SurfaceMaps(mask, alphaCutoff: .5f));
            }
        }
        Scene3D scene = _preview.Scene;
        scene.ReplaceShadowLayout(256, cascades);
        scene.UseGpuSkinning = gpu;
        scene.RenderOrigin = shifted ? new Vector3(32, -16, 8) : Vector3.Zero;
        _preview.Capture(s =>
        {
            int method = lbs ? 1 : 0;
            DqGpuReadback.Queue(s, _meshes[method], new SkinnedInstanceDraw(_handles[method, cutout ? 1 : 0],
                Matrix4x4.CreateTranslation(0, 0, actor == 2 ? 12 : 0))
            {
                ShadowOnly = actor == 1, Dissolve = variant == 0 ? 0 : .5f,
                DissolveComplement = variant == 2 ? 1 : 0, InvertShadowDissolve = variant == 3,
            }, HalfTurn);
        });
        Assert.Equal(actor == 0 ? 1 : 0, scene.DrawnSkinnedInstances);
        Assert.Equal(actor == 2 ? 1 : 0, scene.CulledSkinnedInstances);
        Assert.Equal(cascades, scene.LastShadowPassDiagnostics.CascadeCount);
        Assert.Equal(cascades, scene.LastShadowPassDiagnostics.SkinnedDrawCalls);
        var renderer = (ModelRenderer)DqGpuReadback.Field(scene, "_model");
        return ReadAtlas(Device, renderer.ShadowMap.ShadowTexture, cascades);
    }

    static float[][] ReadAtlas(IGpuDevice device, IGpuTexture atlas, int cascades)
    {
        using var staging = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
            atlas.Width, atlas.Height, GpuPixelFormat.R32Float, GpuTextureUsage.Staging));
        using var commands = device.Factory.CreateCommandList();
        using (GpuRecording.Open(device, commands, "DQ per-cascade depth readback")) commands.CopyTexture(atlas, staging);
        device.Submit(commands);
        device.WaitForIdle();
        var result = Enumerable.Range(0, cascades).Select(_ => new float[256 * 256]).ToArray();
        MappedData map = device.Map(staging, GpuMapMode.Read);
        try
        {
            for (int y = 0; y < 256; y++)
            for (int c = 0; c < cascades; c++)
                Marshal.Copy(IntPtr.Add(map.Data, checked(y * (int)map.RowPitch + c * 256 * 4)), result[c], y * 256, 256);
        }
        finally { device.Unmap(staging); }
        return result;
    }

    public void Dispose()
    {
        _preview?.Dispose();
        _context?.Dispose();
    }
}
