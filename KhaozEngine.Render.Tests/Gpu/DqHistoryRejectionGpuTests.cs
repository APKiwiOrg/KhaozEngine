using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed class DqHistoryRejectionGpuTests(DqHistoryRejectionScene fixture, ITestOutputHelper output)
    : IClassFixture<DqHistoryRejectionScene>
{
    [GpuFact]
    public void Replaced_actor_rejects_history_without_resetting_the_other_actor()
    {
        TemporalFixture fx = fixture.Fixture;
        output.WriteLine($"backend={fx.Device.Backend} adapter={fx.Device.Capabilities.DeviceName}");
        foreach (bool gpu in new[] { true, false })
        foreach (string scenario in new[] { "source", "method", "method-back", "absent", "camera-origin" })
        {
            Scene3D scene = fx.Scene;
            scene.UseGpuSkinning = gpu;
            scene.RenderOrigin = Vector3.Zero;
            scene.Camera.Target = Vector3.Zero;
            scene.CameraCut();
            SkinnedGltfMesh mesh = DqMotionWitness.Patch(scenario == "method-back"
                ? SkinningProfile.LinearBlend : SkinningProfile.DualQuaternionRigid);
            SkinnedMeshHandle a = scene.LoadSkinnedMesh(mesh), b = scene.LoadSkinnedMesh(mesh);
            fx.Frame((s, _) =>
            {
                if (scenario != "absent") Queue(a, mesh, 1, -2);
                Queue(b, mesh, 2, 3);
            });
            scene.UnloadSkinnedMesh(a);
            SkinnedGltfMesh replacement = DqMotionWitness.Patch(scenario == "method"
                ? SkinningProfile.LinearBlend : SkinningProfile.DualQuaternionRigid);
            SkinnedMeshHandle next = scene.LoadSkinnedMesh(replacement);
            Assert.Equal(a.Index, next.Index);
            Assert.NotEqual(a.Generation, next.Generation);
            if (scenario == "camera-origin")
            {
                scene.Camera.Target += new Vector3(.125f, 0, 0);
                scene.RenderOrigin = new Vector3(128, 0, 128);
            }
            fx.Frame((s, _) => { Queue(next, replacement, 1, 0); Queue(b, mesh, 2, 3); });
            Assert.NotNull(scene.PreviousFrameView);
            IGpuTexture? mask = RejectionWitness.Texture(RejectionWitness.SceneMask(scene));
            byte[] pixels = ReadMask(fx.Device, mask, 128, 64);
            (int X, int Y) old = Site(-2), current = Site(0), other = Site(3);
            if (scenario != "absent")
                Assert.True(pixels[old.Y * 128 + old.X] == 255,
                    $"No rejection region at old actor A. Actual={pixels[old.Y * 128 + old.X]}, expected=255.");
            Assert.Equal(255, pixels[current.Y * 128 + current.X]);
            Assert.Equal(0, pixels[other.Y * 128 + other.X]);
            foreach (TemporalResolveEntry entry in new[] { TemporalResolveEntry.Fused, TemporalResolveEntry.Split })
            foreach (int scale in new[] { 1, 2 })
            {
                float[] state = Resolve(fx.Device, mask, entry, scale);
                // Upscaling's current sample weight depends on its reconstruction kernel. Read the independent
                // no-history control, then require zero extra samples at A and eight retained samples at B.
                float[] seeded = Resolve(fx.Device, null, entry, scale, historyValid: false);
                if (scenario != "absent") AssertConfidence(state, old, scale, Confidence(seeded, old, scale));
                AssertConfidence(state, current, scale, Confidence(seeded, current, scale));
                AssertConfidence(state, other, scale, Confidence(seeded, other, scale) + 8f / 15f);
            }
            byte[] retained = ReadMask(fx.Device, mask, 128, 64);
            int read = scene.TemporalHistory.ReadIndex, write = scene.TemporalHistory.WriteIndex;
            fx.RenderSecond(128, 64);
            Assert.False(scene.ResolvedLastRenderForTests);
            Assert.Equal(read, scene.TemporalHistory.ReadIndex);
            Assert.Equal(write, scene.TemporalHistory.WriteIndex);
            Assert.Equal(retained, ReadMask(fx.Device, mask, 128, 64));
            fx.Frame((s, _) => { Queue(next, replacement, 1, 0); Queue(b, mesh, 2, 3); });
            Assert.Same(mask, RejectionWitness.Texture(RejectionWitness.SceneMask(scene)));
            Assert.All(ReadMask(fx.Device, mask, 128, 64), value => Assert.Equal(0, value));
            foreach (TemporalResolveEntry entry in new[] { TemporalResolveEntry.Fused, TemporalResolveEntry.Split })
            {
                float[] warm = Resolve(fx.Device, mask, entry, 1);
                AssertConfidence(warm, current, 1, 9f / 15f);
                AssertConfidence(warm, other, 1, 9f / 15f);
                float[] defaultMask = Resolve(fx.Device, null, entry, 1);
                AssertConfidence(defaultMask, current, 1, 9f / 15f);
            }
            scene.UnloadSkinnedMesh(next);
            scene.UnloadSkinnedMesh(b);
            output.WriteLine($"gpu={gpu} scenario={scenario} mask/readback, fused/split native+2x, warm reuse, zero default passed");

            void Queue(SkinnedMeshHandle handle, SkinnedGltfMesh source, ulong key, float x) =>
                DqMotionWitness.Queue(scene, source, handle, MotionKey.From(key), Matrix4x4.CreateTranslation(x, 0, 0), DqMotionWitness.Current, 0);
            (int X, int Y) Site(float x)
            {
                Vector4 clip = Vector4.Transform(new Vector4(new Vector3(x, 0, 0) - scene.CurrentFrameView.RenderOrigin, 1),
                    scene.CurrentFrameView.ViewProjection);
                return ((int)((clip.X / clip.W * .5f + .5f) * 128), (int)((.5f - clip.Y / clip.W * .5f) * 64));
            }
        }
        // Parity after the shared scene crossed every configuration, against a fresh scene at the same input.
        using var fresh = new TemporalFixture(128, 64, RejectionWitness.Configure);
        fresh.Frame((s, _) => { });
        Assert.All(ReadMask(fresh.Device, RejectionWitness.Texture(RejectionWitness.SceneMask(fresh.Scene)), 128, 64),
            value => Assert.Equal(0, value));
    }

    static void AssertConfidence(float[] state, (int X, int Y) site, int scale, float expected) =>
        Assert.InRange(MathF.Abs(Confidence(state, site, scale) - expected), 0f, .001f);

    static float Confidence(float[] state, (int X, int Y) site, int scale) =>
        state[((site.Y * scale) * 128 * scale + site.X * scale) * 2];

    [GpuFact]
    public void Projected_edges_near_plane_and_unbounded_boxes_cover_the_required_pixels()
    {
        IGpuDevice device = fixture.Fixture.Device;
        output.WriteLine($"backend={device.Backend} adapter={device.Capabilities.DeviceName}");
        using var mask = new RejectionWitness.Mask(device);
        FrameView view = RejectionWitness.View();
        mask.Add(new MeshBounds(new Vector3(.2f, .2f, .2f), new Vector3(.4f, .4f, .3f)), null);
        Prepare(view);
        byte[] asymmetric = ReadMask(device, mask.Texture, 100, 100);
        Assert.True(asymmetric[35 * 100 + 65] == 255, "No rejection region in the projected positive-Y box.");
        Assert.Equal(0, asymmetric[65 * 100 + 65]);
        mask.Clear();
        mask.Add(new MeshBounds(new Vector3(.01f, -.02f, -.2f), new Vector3(.2f, .02f, .1f)), null);
        Prepare(RejectionWitness.View(Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, .1f, 10)));
        byte[] near = ReadMask(device, mask.Texture, 100, 100);
        Assert.Equal(255, near[50 * 100 + 99]);
        Assert.Equal(0, near[50 * 100 + 5]);
        foreach (bool previous in new[] { false, true })
        {
            mask.Clear();
            var unbounded = new MeshBounds(new Vector3(float.NegativeInfinity), new Vector3(float.PositiveInfinity));
            mask.Add(previous ? null : unbounded, previous ? unbounded : null);
            Prepare(view);
            Assert.All(ReadMask(device, mask.Texture, 100, 100), value => Assert.Equal(255, value));
        }

        void Prepare(FrameView frame)
        {
            using var commands = device.Factory.CreateCommandList();
            using (GpuRecording.Open(device, commands, "Project actor rejection")) mask.Prepare(commands, frame, null);
            device.Submit(commands);
            device.WaitForIdle();
        }
    }

    // Flat, stationary background with eight stored samples. There is no depth, motion, shading or clipping reason
    // to lose history. Only the production actor mask can change confidence from 9/15 to 1/15.
    static float[] Resolve(IGpuDevice device, IGpuTexture? mask, TemporalResolveEntry entry, int scale, bool historyValid = true)
    {
        const int w = 128, h = 64;
        var history = new TemporalHistory();
        try
        {
        history.EnsureTargets(device, w * scale, h * scale, w, h);
        history.BeginResolve(1);
        using var renderer = new TemporalResolveRenderer(device);
        using var retired = GpuRetireQueue.Create(device);
        renderer.SelectEntry(entry, retired);
        using var scene = Input(GpuPixelFormat.R16G16B16A16Float);
        using var depth = Input(GpuPixelFormat.R32Float);
        using var motion = Input(GpuPixelFormat.R16G16Float);
        float[] colour = Enumerable.Range(0, w * h * 4).Select(i => i % 4 == 3 ? 1f : .3f).ToArray();
        TemporalTextureIo.Upload(device, scene, colour);
        TemporalTextureIo.Upload(device, depth, new float[w * h]);
        TemporalTextureIo.Upload(device, motion, Enumerable.Repeat(MotionMath.Sentinel, w * h * 2).ToArray());
        TemporalTextureIo.Upload(device, history.Color(history.ReadIndex),
            Enumerable.Range(0, w * h * scale * scale * 4).Select(i => i % 4 == 3 ? 1f : .3f).ToArray());
        TemporalTextureIo.Upload(device, history.Confidence(history.ReadIndex),
            Enumerable.Range(0, w * h * scale * scale * 2).Select(i => i % 2 == 0 ? 8f / 15f : 0).ToArray());
        TemporalTextureIo.Upload(device, history.PreviousDepth(history.ReadIndex), Enumerable.Repeat(1e30f, w * h).ToArray());
        object?[] inputs = typeof(TemporalResolveInputs).GetConstructors().Any(c => c.GetParameters().Length == 5)
            ? [scene, scene, depth, motion, mask] : [scene, scene, depth, motion];
        renderer.BindInputs((TemporalResolveInputs)Activator.CreateInstance(typeof(TemporalResolveInputs), inputs)!, history);
        var lens = Matrix4x4.CreateOrthographic(8, 4, .1f, 100);
        var view = new TemporalViewInput(Matrix4x4.Identity, lens);
        var uniforms = TemporalResolveMath.BuildUniforms(view, view, Vector2.Zero, w, h, w * scale, h * scale, historyValid,
            screenFixedBackground: true);
        using var commands = device.Factory.CreateCommandList();
        using (GpuRecording.Open(device, commands, "DQ history rejection witness"))
        {
            renderer.PrepareUniforms(commands, uniforms, TemporalResolveMath.BuildDepthStore(lens));
            renderer.Run(commands, history);
        }
        device.Submit(commands);
        device.WaitForIdle();
        return TemporalTextureIo.Read(device, history.Confidence(history.WriteIndex));

        IGpuTexture Input(GpuPixelFormat format) => device.Factory.CreateTexture(
            GpuTextureDescription.Texture2D(w, h, format, GpuTextureUsage.Sampled));
        }
        finally { history.ReleaseTargets(); }
    }

    internal static byte[] ReadMask(IGpuDevice device, IGpuTexture? mask, int width, int height)
    {
        var result = new byte[width * height];
        if (mask is null) return result;
        Assert.Equal(GpuPixelFormat.R8UNorm, mask.Format);
        Assert.Equal((uint)width, mask.Width);
        Assert.Equal((uint)height, mask.Height);
        using var staging = device.Factory.CreateTexture(GpuTextureDescription.Texture2D((uint)width, (uint)height,
            GpuPixelFormat.R8UNorm, GpuTextureUsage.Staging));
        using var commands = device.Factory.CreateCommandList();
        using (GpuRecording.Open(device, commands, "Read actor rejection")) commands.CopyTexture(mask, staging);
        device.Submit(commands);
        device.WaitForIdle();
        MappedData map = device.Map(staging, GpuMapMode.Read);
        try
        {
            for (int y = 0; y < height; y++) Marshal.Copy(map.Data + (int)(y * map.RowPitch), result, y * width, width);
        }
        finally { device.Unmap(staging); }
        return result;
    }
}

public sealed class DqHistoryRejectionScene : IDisposable
{
    TemporalFixture? _fixture;
    internal TemporalFixture Fixture => _fixture ??= new TemporalFixture(128, 64, RejectionWitness.Configure);
    public void Dispose() => _fixture?.Dispose();
}
