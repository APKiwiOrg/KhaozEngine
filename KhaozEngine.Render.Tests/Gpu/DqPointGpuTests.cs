using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed class DqPointGpuTests(DqPointScene fixture, ITestOutputHelper output) : IClassFixture<DqPointScene>
{
    [GpuFact]
    public void DQ_point_depth_uses_the_current_DQ_pose()
    {
        int observedFaceMask = 0, observed = 0;
        foreach (int actor in new[] { 0, 1, 2 }) // visible, shadow-only, camera-culled
        foreach (bool gpu in new[] { false, true })
        {
            output.WriteLine($"{fixture.Device.Backend} {fixture.Device.Capabilities.DeviceName}: actor={actor} gpu={gpu}");
            float[][] solid = fixture.Capture(actor, gpu, 0);
            float[][] lbs = fixture.Capture(actor, gpu, 0, lbs: true);
            float[][] rest = fixture.Capture(actor, gpu, 0, rest: true);
            float[][] plain = fixture.Capture(actor, gpu, 1);
            float[][] complement = fixture.Capture(actor, gpu, 2);
            float[][] inverted = fixture.Capture(actor, gpu, 3);
            // The exact 50/50 identity/half-turn blend is a rigid quarter-turn about Z. This independent
            // reference uses unskinned identity joints and an exact quarter-turn placement matrix.
            float[][] reference = fixture.Capture(actor, false, 0, reference: true);
            float[][] cpu = fixture.Capture(actor, false, 1);
            for (int row = 0; row < 2; row++)
            for (int face = 0; face < 6; face++)
            {
                int cell = row * 6 + face;
                float[] dqDepthWords = solid[cell], lbsDepthWords = lbs[cell];
                int written = dqDepthWords.Count(Written);
                Assert.True(written > 0, $"Missing DQ face={face} row={row} actor={actor} gpu={gpu}");
                Assert.False(dqDepthWords.SequenceEqual(lbsDepthWords));
                Assert.Equal(0, lbsDepthWords.Count(Written));
                AssertDepthNear(reference[cell], dqDepthWords);
                Assert.InRange(plain[cell].Count(Written), 1, written - 1);
                Assert.InRange(complement[cell].Count(Written), 1, written - 1);
                for (int p = 0; p < dqDepthWords.Length; p++)
                {
                    Assert.Equal(Written(dqDepthWords[p]), Written(plain[cell][p]) || Written(complement[cell][p]));
                    Assert.False(Written(plain[cell][p]) && Written(complement[cell][p]));
                }
                AssertDepthNear(complement[cell], inverted[cell]);
                AssertDepthNear(cpu[cell], plain[cell]);
                observedFaceMask |= 1 << face;
                observed++;
                output.WriteLine($"row={row} face={face} DQ={written} LBS=0 dissolve={plain[cell].Count(Written)}/{complement[cell].Count(Written)}");
            }
            Assert.False(solid.SelectMany(c => c).SequenceEqual(rest.SelectMany(c => c)));
        }
        Assert.Equal(0x3f, observedFaceMask);
        Assert.Equal(72, observed);
        output.WriteLine($"{fixture.Device.Backend} {fixture.Device.Capabilities.DeviceName}: matched 72/72 face/row/actor/execution witnesses");
    }

    [GpuFact]
    public void Shared_point_scene_matches_fresh_after_pose_variant_and_execution_changes()
    {
        fixture.Capture(2, false, 2);
        fixture.Capture(1, true, 3, lbs: true);
        float[][] aged = fixture.Capture(0, true, 1);
        using var fresh = new DqPointScene(fixture.Device);
        float[][] baseline = fresh.Capture(0, true, 1);
        for (int cell = 0; cell < 12; cell++)
        {
            Assert.True(baseline[cell].Count(Written) > 0);
            Assert.Equal(baseline[cell], aged[cell]);
        }
        output.WriteLine($"{fixture.Device.Backend} {fixture.Device.Capabilities.DeviceName}: shared/fresh words identical in 12 point cells");
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

public sealed class DqPointScene : IDisposable
{
    readonly IGpuDevice? _shared;
    GpuDeviceContext? _context;
    Render3DPreview? _preview;
    readonly SkinnedGltfMesh[] _meshes = [Patch(SkinningProfile.DualQuaternionRigid), Patch(SkinningProfile.LinearBlend)];
    readonly SkinnedMeshHandle[] _handles = new SkinnedMeshHandle[2];
    public DqPointScene() { }
    internal DqPointScene(IGpuDevice device) => _shared = device;
    internal IGpuDevice Device => _shared ?? (_context ??= GpuDeviceContext.CreateHeadless()).GpuDevice;

    internal static SkinnedGltfMesh Patch(SkinningProfile profile)
    {
        var vertices = new List<SkinnedVertex>();
        var indices = new List<ushort>();
        foreach (Vector3 axis in new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ })
        {
            Vector3 a = axis.X != 0 ? Vector3.UnitY : Vector3.UnitX;
            Vector3 b = Vector3.Cross(axis, a);
            int start = vertices.Count;
            for (int i = 0; i < 4; i++) vertices.Add(new SkinnedVertex
            {
                Position = axis * 3 + a * (i is 1 or 2 ? .8f : -.8f) + b * (i >= 2 ? .5f : -.5f),
                Normal = axis, Color = Vector4.One, Tangent = Vector4.Zero,
                BoneIndices = new Vector4(0, 1, 0, 0), BoneWeights = new Vector4(.5f, .5f, 0, 0),
            });
            foreach (int index in new[] { 0, 1, 2, 0, 2, 3 }) indices.Add((ushort)(start + index));
        }
        return new SkinnedGltfMesh(vertices.ToArray(), indices.ToArray(),
            [Matrix4x4.Identity, Matrix4x4.Identity], [Matrix4x4.Identity, Matrix4x4.Identity], null, profile);
    }

    internal static void Configure(Scene3D scene)
    {
        DqCascadeScene.Configure(scene);
        scene.Camera.OrthoSize = 6;
        scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        scene.Post.Quality.Shadows.PointShadows.FaceResolution = 64;
        scene.Post.Quality.Shadows.PointShadows.MaxShadowedLights = 2;
        scene.Post.Quality.Shadows.PointShadows.MaxDynamicLightsPerFrame = 1;
    }

    internal static void Lights(Scene3D scene, Vector3 centre)
    {
        scene.AddLight(centre, Color.White, 10, 1, LightShadow.Static(35));
        scene.AddLight(centre, Color.White, 10, 1, LightShadow.Dynamic);
    }

    internal float[][] Capture(int actor, bool gpu, int variant, bool lbs = false, bool rest = false, bool reference = false)
    {
        bool cold = _preview is null;
        if (_preview is null)
        {
            _preview = new Render3DPreview(Device, 32, 32);
            Configure(_preview.Scene);
            for (int i = 0; i < 2; i++) _handles[i] = _preview.Scene.LoadSkinnedMesh(_meshes[i]);
        }
        Scene3D scene = _preview.Scene;
        scene.UseGpuSkinning = gpu;
        Vector3 centre = actor == 2 ? new Vector3(0, 0, 32) : Vector3.Zero;
        void Frame(Scene3D s)
        {
            Lights(s, centre);
            int method = lbs || reference ? 1 : 0;
            Matrix4x4 world = reference ? new(0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1) : Matrix4x4.Identity;
            world *= Matrix4x4.CreateTranslation(centre);
            DqGpuReadback.Queue(s, _meshes[method], new SkinnedInstanceDraw(_handles[method], world)
            {
                ShadowOnly = actor == 1, Dissolve = variant == 0 ? 0 : .5f,
                DissolveComplement = variant == 2 ? 1 : 0, InvertShadowDissolve = variant == 3,
            }, rest || reference ? _meshes[method].RestPose : DqCascadeScene.HalfTurn);
        }
        if (cold)
        {
            _preview.Capture(Frame);
            _preview.Capture(Frame);
        }
        _preview.Capture(Frame);
        Assert.Equal(actor == 0 ? 1 : 0, scene.DrawnSkinnedInstances);
        Assert.Equal(actor == 2 ? 1 : 0, scene.CulledSkinnedInstances);
        bool staticTransientRowsObserved = scene.LastShadowPassDiagnostics.PointStaticTransientSkinnedDrawCalls == 6;
        bool dynamicRowsObserved = scene.LastShadowPassDiagnostics.PointDynamicSkinnedDrawCalls == 6;
        Assert.True(staticTransientRowsObserved && dynamicRowsObserved);
        Assert.Equal(0, scene.PointShadowTransientSlotForLight(0));
        Assert.Equal(-1, scene.PointShadowTransientSlotForLight(1));
        float[][] transient = ReadRow(scene.PointShadowTransientTexture!, 0);
        float[][] dynamic = ReadRow(scene.PointShadowTexture!, scene.PointShadowBaseSlotForLight(1));
        Assert.All(ReadRow(scene.PointShadowTexture!, scene.PointShadowBaseSlotForLight(0)),
            face => Assert.All(face, depth => Assert.Equal(1f, depth)));
        return transient.Concat(dynamic).ToArray();
    }

    float[][] ReadRow(IGpuTexture atlas, int row)
    {
        using var staging = Device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
            atlas.Width, atlas.Height, GpuPixelFormat.R32Float, GpuTextureUsage.Staging));
        using var commands = Device.Factory.CreateCommandList();
        using (GpuRecording.Open(Device, commands, "DQ point face readback")) commands.CopyTexture(atlas, staging);
        Device.Submit(commands);
        Device.WaitForIdle();
        var result = Enumerable.Range(0, 6).Select(_ => new float[64 * 64]).ToArray();
        MappedData map = Device.Map(staging, GpuMapMode.Read);
        try
        {
            for (int y = 0; y < 64; y++)
            for (int face = 0; face < 6; face++)
                Marshal.Copy(IntPtr.Add(map.Data, checked((row * 64 + y) * (int)map.RowPitch + face * 64 * 4)),
                    result[face], y * 64, 64);
        }
        finally { Device.Unmap(staging); }
        return result;
    }

    public void Dispose()
    {
        _preview?.Dispose();
        _context?.Dispose();
    }
}
