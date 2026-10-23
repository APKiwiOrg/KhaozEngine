using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed class DqCoverageUnionGpuTests(DqCoverageUnionGpuScene fixture) : IClassFixture<DqCoverageUnionGpuScene>
{
    const int Width = 32, Height = 24;

    [GpuTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Disjoint_production_inputs_reject_the_union_and_preserve_stable_history(bool split) =>
        Capture(fixture.Scene, split);

    [GpuFact]
    public void Reused_union_scene_matches_fresh_scene_at_all_three_actor_probes()
    {
        Capture(fixture.Scene, false);
        float[] reused = Capture(fixture.Scene, true);
        using var fresh = new CoverageSceneFixture(executeNative: true);
        Assert.Equal(reused, Capture(fresh, true));
    }

    static float[] Capture(CoverageSceneFixture h, bool split)
    {
        Scene3D scene = h.Scene;
        RejectionWitness.Configure(scene);
        scene.Camera.OrthoSize = 4.5f;
        scene.CameraCut();
        scene.UseGpuSkinning = true;
        scene.Post.Temporal.Upscale = TemporalUpscale.Native;
        scene.TemporalResolveEntryForTests = split ? TemporalResolveEntry.Split : TemporalResolveEntry.Fused;
        SkinnedGltfMesh lbs = DqMotionWitness.Patch(SkinningProfile.LinearBlend);
        SkinnedGltfMesh dq = DqMotionWitness.Patch(SkinningProfile.DualQuaternionRigid);
        SkinnedMeshHandle actor = scene.LoadSkinnedMesh(lbs), stable = scene.LoadSkinnedMesh(lbs);
        MotionKey ordinaryKey = MotionKey.From(101), stableKey = MotionKey.From(102);
        IGpuTexture? oldOrdinary = null, oldCoverage = null;
        var evidence = new List<float>();
        for (int phase = 0; phase < 5; phase++)
        {
            if (phase == 1)
            {
                scene.UnloadSkinnedMesh(actor);
                actor = scene.LoadSkinnedMesh(dq);
            }
            h.Begin();
            SkinnedCoverageFrame covered = default;
            if (phase < 3)
            {
                covered = h.Stage(Matrix4x4.CreateTranslation(-3.5f, 0, 0), instance: phase == 0 ? 1ul : 2ul);
                scene.DrawSkinned(h.HostDraw(covered), h.Palette);
                h.SubmitEquipment();
            }
            float x = phase == 0 ? 1 : 2;
            scene.DrawSkinned(new SkinnedInstanceDraw(actor, Matrix4x4.CreateTranslation(x, 0, 0))
            { Motion = ordinaryKey }, DqMotionWitness.Current);
            scene.DrawSkinned(new SkinnedInstanceDraw(stable, Matrix4x4.CreateTranslation(5, 0, 0))
            { Motion = stableKey }, DqMotionWitness.Current);
            h.Render(Width, Height);
            var inputs = h.BoundRejectionInputs;
            MaskPlane ordinary = ReadMask(h.NativeDevice, inputs.Ordinary);
            MaskPlane coverage = ReadMask(h.NativeDevice, inputs.Coverage);
            var a = Site(scene, x);
            var b = Site(scene, -3.5f);
            var c = Site(scene, 5);
            if (phase < 3)
            {
                var reservation = covered.Preparation!.GpuReservation;
                var recorded = h.NativeDraws.First(draw => draw.Surface.CurrentBase == reservation.CurrentBase);
                uint[] words = CoverageSelectedReadback.Read(h.NativeDevice, recorded.Surface.Current);
                Assert.Equal(0u, words[reservation.CurrentBase + 14]);
                Assert.Equal(phase == 2 ? 0u : 1u, words[reservation.CurrentBase + 15]);
                Assert.Same(scene.CoverageTransitionForTests.Texture, inputs.Coverage);
            }
            if (phase == 1)
            {
                Assert.Contains((byte)255, ordinary.Values);
                Assert.Contains((byte)255, coverage.Values);
                Assert.Equal(255, ordinary.At(a)); Assert.Equal(0, coverage.At(a));
                Assert.Equal(0, ordinary.At(b)); Assert.Equal(255, coverage.At(b));
                Assert.DoesNotContain(Enumerable.Range(0, Width * Height), i =>
                    ordinary.At((i % Width, i / Width)) != 0 && coverage.At((i % Width, i / Width)) != 0);
                oldOrdinary = inputs.Ordinary;
                oldCoverage = inputs.Coverage;
            }
            else
            {
                Assert.Equal(1u, inputs.Ordinary.Width);
                Assert.All(ordinary.Values, value => Assert.Equal(0, value));
            }
            if (phase == 2)
            {
                // The conditional draw remains bound, but Select reports no transition and the target was cleared.
                Assert.All(coverage.Values, value => Assert.Equal(0, value));
                Assert.Contains((byte)255, ReadMask(h.NativeDevice, oldOrdinary!).Values);
            }
            if (phase == 4)
            {
                // Phase 3 removed the covered actor and filled its old region. The retained target stays dirty,
                // while the actual resolve must now bind its zero input rather than reuse an old descriptor.
                Assert.Equal(1u, inputs.Coverage.Width);
                Assert.All(coverage.Values, value => Assert.Equal(0, value));
                Assert.Contains((byte)255, ReadMask(h.NativeDevice, oldCoverage!).Values);
            }
            Assert.Equal(0, ordinary.At(c)); Assert.Equal(0, coverage.At(c));
            float[] confidence = Resolve(h.NativeDevice, inputs.Ordinary, inputs.Coverage, split);
            for (int y = 0; y < Height; y++)
                for (int px = 0; px < Width; px++)
                    Confidence(confidence, (px, y), ordinary.At((px, y)) != 0 || coverage.At((px, y)) != 0 ? 1f / 15 : 9f / 15);
            Confidence(confidence, c, 9f / 15);
            if (phase == 1)
            {
                Confidence(confidence, a, 1f / 15); Confidence(confidence, b, 1f / 15);
                float[] onlyCoverage = Resolve(h.NativeDevice, null, inputs.Coverage, split);
                float[] onlyOrdinary = Resolve(h.NativeDevice, inputs.Ordinary, null, split);
                Confidence(onlyCoverage, a, 9f / 15); Confidence(onlyCoverage, b, 1f / 15);
                Confidence(onlyOrdinary, a, 1f / 15); Confidence(onlyOrdinary, b, 9f / 15);
            }
            if (phase > 0)
            {
                Assert.True(scene.ActiveMotionHistory!.TryGetPreviousSkinned(stableKey, out Matrix4x4 world, out var palette));
                Assert.Equal(Matrix4x4.CreateTranslation(5, 0, 0), world);
                Assert.Equal(DqMotionWitness.Current, palette.ToArray());
                Assert.True(scene.TemporalHistory.IsValid);
            }
            evidence.AddRange([ordinary.At(a), coverage.At(a), ordinary.At(b), coverage.At(b),
                confidence[(a.Y * Width + a.X) * 2], confidence[(b.Y * Width + b.X) * 2], confidence[(c.Y * Width + c.X) * 2]]);
        }
        scene.UnloadSkinnedMesh(actor);
        scene.UnloadSkinnedMesh(stable);
        return evidence.ToArray();
    }

    static (int X, int Y) Site(Scene3D scene, float x)
    {
        Vector4 clip = Vector4.Transform(new Vector4(new Vector3(x, 0, 0) - scene.CurrentFrameView.RenderOrigin, 1),
            scene.CurrentFrameView.ViewProjection);
        return ((int)((clip.X / clip.W * .5f + .5f) * Width), (int)((.5f - clip.Y / clip.W * .5f) * Height));
    }

    static void Confidence(float[] values, (int X, int Y) p, float expected) =>
        Assert.InRange(values[(p.Y * Width + p.X) * 2], expected - .001f, expected + .001f);

    readonly record struct MaskPlane(int Width, int Height, byte[] Values)
    {
        internal byte At((int X, int Y) p) => Values[Math.Clamp(p.Y * Height / DqCoverageUnionGpuTests.Height, 0, Height - 1)
            * Width + Math.Clamp(p.X * Width / DqCoverageUnionGpuTests.Width, 0, Width - 1)];
    }

    static MaskPlane ReadMask(IGpuDevice device, IGpuTexture texture)
    {
        using var staging = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(texture.Width,
            texture.Height, GpuPixelFormat.R8UNorm, GpuTextureUsage.Staging));
        using var commands = device.Factory.CreateCommandList();
        using (GpuRecording.Open(device, commands, "DQ.CoverageUnion.Mask")) commands.CopyTexture(texture, staging);
        device.Submit(commands); device.WaitForIdle();
        var map = device.Map(staging, GpuMapMode.Read);
        var values = new byte[texture.Width * texture.Height];
        try
        {
            for (int y = 0; y < texture.Height; y++)
                Marshal.Copy(map.Data + y * (int)map.RowPitch, values, y * (int)texture.Width, (int)texture.Width);
        }
        finally { device.Unmap(staging); }
        return new((int)texture.Width, (int)texture.Height, values);
    }

    // Flat stationary background with eight stored samples isolates rejection from depth and shading changes.
    // The two mask inputs above are borrowed from the actual scene's production resolve bindings.
    static float[] Resolve(IGpuDevice device, IGpuTexture? ordinary, IGpuTexture? coverage, bool split)
    {
        var history = new TemporalHistory();
        try
        {
            history.EnsureTargets(device, Width, Height, Width, Height);
            history.BeginResolve(1);
            using var retired = GpuRetireQueue.Create(device);
            using var renderer = new TemporalResolveRenderer(device);
            renderer.SelectEntry(split ? TemporalResolveEntry.Split : TemporalResolveEntry.Fused, retired);
            using var colour = Input(GpuPixelFormat.R16G16B16A16Float);
            using var depth = Input(GpuPixelFormat.R32Float);
            using var motion = Input(GpuPixelFormat.R16G16Float);
            float[] flat = Enumerable.Range(0, Width * Height * 4).Select(i => i % 4 == 3 ? 1f : .3f).ToArray();
            TemporalTextureIo.Upload(device, colour, flat);
            TemporalTextureIo.Upload(device, depth, new float[Width * Height]);
            TemporalTextureIo.Upload(device, motion, Enumerable.Repeat(MotionMath.Sentinel, Width * Height * 2).ToArray());
            TemporalTextureIo.Upload(device, history.Color(history.ReadIndex), flat);
            TemporalTextureIo.Upload(device, history.Confidence(history.ReadIndex),
                Enumerable.Range(0, Width * Height * 2).Select(i => i % 2 == 0 ? 8f / 15 : 0).ToArray());
            TemporalTextureIo.Upload(device, history.PreviousDepth(history.ReadIndex), Enumerable.Repeat(1e30f, Width * Height).ToArray());
            renderer.BindInputs(new TemporalResolveInputs(colour, colour, depth, motion, ordinary) { CoverageRejection = coverage }, history);
            var projection = Matrix4x4.CreateOrthographic(12, 9, .1f, 100);
            var view = new TemporalViewInput(Matrix4x4.Identity, projection);
            var uniforms = TemporalResolveMath.BuildUniforms(view, view, Vector2.Zero, Width, Height, Width, Height,
                historyValid: true, screenFixedBackground: true);
            using var commands = device.Factory.CreateCommandList();
            using (GpuRecording.Open(device, commands, "DQ.CoverageUnion.Resolve"))
            {
                renderer.PrepareUniforms(commands, uniforms, TemporalResolveMath.BuildDepthStore(projection));
                renderer.Run(commands, history);
            }
            device.Submit(commands); device.WaitForIdle();
            return TemporalTextureIo.Read(device, history.Confidence(history.WriteIndex));
            IGpuTexture Input(GpuPixelFormat format) => device.Factory.CreateTexture(
                GpuTextureDescription.Texture2D(Width, Height, format, GpuTextureUsage.Sampled));
        }
        finally { history.ReleaseTargets(); }
    }
}

public sealed class DqCoverageUnionGpuScene : IDisposable
{
    CoverageSceneFixture? scene;
    internal CoverageSceneFixture Scene => scene ??= new(executeNative: true);
    public void Dispose() => scene?.Dispose();
}
