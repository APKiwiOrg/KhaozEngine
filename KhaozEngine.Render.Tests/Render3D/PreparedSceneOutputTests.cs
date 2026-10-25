using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class PreparedSceneOutputTests
{
    [Fact]
    public void Prefix_never_binds_output_and_tail_uploads_its_own_final_uniforms_once()
    {
        using var rig = new Rig();
        rig.Scene.Begin();
        rig.Scene.PrepareFrame();
        long token = rig.Scene.PrepareOutput(rig.Prefix, 32, 24);
        Assert.DoesNotContain(rig.Output, rig.Prefix.BoundFramebuffers);
        Assert.NotEmpty(rig.Prefix.BoundFramebuffers);
        rig.Scene.RenderPreparedOutput(rig.Tail, 32, 24, rig.Output, token);
        Assert.Same(rig.Output, Assert.Single(rig.Tail.BoundFramebuffers));
        Assert.Equal(1, rig.Tail.DrawCount);
        var upload = Assert.Single(rig.Tail.Uploads);
        Assert.Equal(16u, upload.Bytes);
        Assert.Equal(0f, BitConverter.ToSingle(upload.Data!, 0));
        Assert.Equal(1f, BitConverter.ToSingle(upload.Data!, 4));
        Assert.Throws<InvalidOperationException>(() =>
            rig.Scene.RenderPreparedOutput(rig.Tail, 32, 24, rig.Output, token));
    }

    [Fact]
    public void A_split_temporal_debug_view_uploads_its_resolve_uniforms_in_the_tail_recording()
    {
        using var rig = new Rig();
        rig.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        rig.Scene.Post.Temporal.Upscale = TemporalUpscale.Native;
        rig.Scene.DebugView = SceneDebugView.History;
        rig.Scene.Begin(); rig.Scene.PrepareFrame();
        long token = rig.Scene.PrepareOutput(rig.Prefix, 32, 24);
        Assert.NotNull(rig.Scene.TemporalResolveRendererForTests);
        rig.Scene.RenderPreparedOutput(rig.Tail, 32, 24, rig.Output, token);
        Assert.Contains(rig.Tail.Uploads, upload =>
            upload.Bytes == KhaozEngine.Render3D.Internal.TemporalResolveUniforms.SizeInBytes
            && upload.FramebufferBindsBefore == 0);
    }

    [Theory]
    [InlineData(BillboardBlend.Alpha)]
    [InlineData(BillboardBlend.Additive)]
    public void Prepared_billboards_republish_the_exact_atmosphere_payload_without_rebuilding_luts(BillboardBlend blend)
    {
        using var rig = new Rig();
        rig.Prefix.CapturePayloads = true;
        rig.Scene.Post.Atmosphere.Enabled = true;
        rig.Scene.Post.Atmosphere.WorldUnitsPerKilometre = 1;
        rig.Scene.Begin();
        rig.Scene.DrawBillboard(Vector3.Zero, 1.4f, new Color(0.8f, 0.3f, 0.1f, 0.7f), blend);
        rig.Scene.PrepareFrame();
        long token = rig.Scene.PrepareOutput(rig.Prefix, 32, 24);
        var atmosphere = rig.Scene.AtmosphereForTests;
        var prefix = Assert.Single(rig.Prefix.Uploads, u => ReferenceEquals(u.Buffer, atmosphere.Bindings.Uniforms));
        int staticBuilds = atmosphere.StaticBuildCount, skyDraws = atmosphere.SkyViewDrawCount;
        rig.Scene.Post.Atmosphere.AerialDistanceScale += 5;
        rig.Scene.Camera.Frame(Vector3.Zero, new Vector3(20, 40, 30));
        rig.Scene.RenderPreparedOutput(rig.Tail, 32, 24, rig.Output, token);
        var tail = Assert.Single(rig.Tail.Uploads, u => ReferenceEquals(u.Buffer, atmosphere.Bindings.Uniforms));
        Assert.Equal(prefix.Data, tail.Data);
        Assert.Equal(0, tail.FramebufferBindsBefore);
        Assert.Equal(staticBuilds, atmosphere.StaticBuildCount);
        Assert.Equal(skyDraws, atmosphere.SkyViewDrawCount);
    }

    [Fact]
    public void A_new_begin_and_a_later_prepare_cannot_consume_an_old_token()
    {
        using var rig = new Rig();
        rig.Scene.Begin();
        rig.Scene.PrepareFrame();
        long old = rig.Scene.PrepareOutput(rig.Prefix, 32, 24);
        rig.Scene.Begin();
        Assert.Throws<InvalidOperationException>(() => rig.Scene.RenderPreparedOutput(rig.Tail, 32, 24, rig.Output, old));
        rig.Scene.PrepareFrame();
        long current = rig.Scene.PrepareOutput(rig.Prefix, 32, 24);
        Assert.NotEqual(old, current);
        Assert.Throws<InvalidOperationException>(() => rig.Scene.RenderPreparedOutput(rig.Tail, 32, 24, rig.Output, old));
        rig.Scene.RenderPreparedOutput(rig.Tail, 32, 24, rig.Output, current);
    }

    [Fact]
    public void Wrong_dimensions_cancel_and_cannot_later_replay_the_prepared_output()
    {
        using var rig = new Rig();
        rig.Scene.Begin();
        rig.Scene.PrepareFrame();
        long token = rig.Scene.PrepareOutput(rig.Prefix, 32, 24);
        Assert.Throws<InvalidOperationException>(() => rig.Scene.RenderPreparedOutput(rig.Tail, 33, 24, rig.Output, token));
        Assert.Empty(rig.Tail.BoundFramebuffers);
        Assert.Throws<InvalidOperationException>(() => rig.Scene.RenderPreparedOutput(rig.Tail, 32, 24, rig.Output, token));
    }

    [Fact]
    public void Legacy_render_invalidates_pending_output_and_keeps_the_single_list_tail()
    {
        using var rig = new Rig();
        rig.Scene.Begin();
        rig.Scene.PrepareFrame();
        long token = rig.Scene.PrepareOutput(rig.Prefix, 32, 24);
        rig.Scene.RenderInternal(rig.Tail, 32, 24, rig.Output);
        Assert.Same(rig.Output, rig.Tail.BoundFramebuffers[^1]);
        Assert.Throws<InvalidOperationException>(() => rig.Scene.RenderPreparedOutput(rig.Tail, 32, 24, rig.Output, token));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("1", true)]
    public void Startup_switch_is_explicit(string? value, bool expected) =>
        Assert.Equal(expected, PreparePhaseSceneSubmission.Parse(value));

    [Theory]
    [InlineData("true")]
    [InlineData(" 1")]
    [InlineData("2")]
    public void Invalid_startup_values_fail_instead_of_silently_changing_the_experiment(string value) =>
        Assert.Throws<ArgumentException>(() => PreparePhaseSceneSubmission.Parse(value));

    [Fact]
    public void Suppressed_prepare_submits_nothing_and_cannot_supply_a_tail()
    {
        using var rig = new Rig();
        using var prepared = new PreparePhaseSceneSubmission(rig.Device, rig.Scene);
        var frame = new Frame { Commands = rig.Tail, Width = 32, Height = 24, RenderSuppressed = true };
        prepared.Prepare(frame);
        Assert.Equal(0, frame.PreparePhaseSubmissionCount);
        Assert.False(frame.PreparePhaseSplitActive);
        Assert.Throws<InvalidOperationException>(() => prepared.Render(frame, rig.Output));
    }

    [Fact]
    public void Frame_phases_submit_the_prefix_before_opening_the_main_list_and_keep_capture_last()
    {
        using var gpu = new FakeGpuDevice(GpuBackendKind.MetalNative);
        using var output = new PostChainRig.Output(gpu, 32, 24);
        using var device = new FrameOutputDevice(gpu, output);
        using var scene = new Scene3D(device, output.Framebuffer.Outputs);
        using var prepared = new PreparePhaseSceneSubmission(device, scene);
        using var commands = device.Factory.CreateCommandList();
        using var taps = new FrameCaptureTaps(device, null);
        var frame = new Frame { Commands = commands, Width = 32, Height = 24 };
        device.BeforeSubmit = _ => Assert.Null(GpuRecording.OpenOwner(device));
        bool captured = false;
        taps.Add(new Tap(context =>
        {
            Assert.Equal(1, device.Submits);
            Assert.Equal(0, device.Presents);
            Assert.Equal(1, frame.PreparePhaseCompositeCount);
            captured = true;
        }));
        try
        {
            KhaozEngine.Tests.Windowing.RendererFramePolicy.Run(frame, true, device, commands, Color.Black,
                f => { scene.Begin(); scene.PrepareFrame(); prepared.Prepare(f); Assert.Equal(1, device.Submits); },
                f => { Assert.Equal(1, device.Submits); prepared.Render(f, output.Framebuffer); }, taps, true);
            Assert.True(captured);
            Assert.Equal(2, device.Submits);
            Assert.Equal(1, device.Presents);
            Assert.Equal(1, frame.PreparePhaseSubmissionCount);
            Assert.Throws<InvalidOperationException>(() => prepared.Render(frame, output.Framebuffer));
        }
        finally { frame.DisposeSubmission(); }
    }

    [Fact]
    public void Submit_failure_does_not_publish_a_prepared_frame_or_keep_a_recording_open()
    {
        using var gpu = new FakeGpuDevice(GpuBackendKind.MetalNative);
        using var output = new PostChainRig.Output(gpu, 32, 24);
        using var device = new FrameOutputDevice(gpu, output);
        using var scene = new Scene3D(device, output.Framebuffer.Outputs);
        using var prepared = new PreparePhaseSceneSubmission(device, scene);
        using var commands = device.Factory.CreateCommandList();
        var frame = new Frame { Commands = commands, Width = 32, Height = 24 };
        scene.Begin(); scene.PrepareFrame();
        device.BeforeSubmit = _ => throw new InvalidOperationException("injected submit failure");
        Assert.Throws<InvalidOperationException>(() => prepared.Prepare(frame));
        Assert.Null(GpuRecording.OpenOwner(device));
        Assert.Equal(0, frame.PreparePhaseSubmissionCount);
        Assert.Throws<InvalidOperationException>(() => prepared.Render(frame, output.Framebuffer));
        device.BeforeSubmit = null;
        scene.Begin(); scene.PrepareFrame();
        prepared.Prepare(frame);
        prepared.Render(frame, output.Framebuffer);
        Assert.Equal(1, frame.PreparePhaseCompositeCount);
    }

    [Fact]
    public void Failure_after_the_prefix_submits_abandons_the_tail_and_drains_before_scene_resources_are_freed()
    {
        using var gpu = new FakeGpuDevice(GpuBackendKind.MetalNative);
        using var output = new PostChainRig.Output(gpu, 32, 24);
        using var device = new FrameOutputDevice(gpu, output);
        var scene = new Scene3D(device, output.Framebuffer.Outputs);
        using var prepared = new PreparePhaseSceneSubmission(device, scene);
        using var commands = device.Factory.CreateCommandList();
        var factory = (FakeGpuResourceFactory)gpu.Factory;
        int beforeMesh = factory.Buffers.Count;
        MeshHandle mesh = scene.LoadMesh(MeshPrimitives.Sphere(0.5f));
        var meshBuffers = factory.Buffers.Skip(beforeMesh).ToArray();
        Assert.NotEmpty(meshBuffers);
        var frame = new Frame { Commands = commands, Width = 32, Height = 24 };
        bool disposed = false;
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                KhaozEngine.Tests.Windowing.RendererFramePolicy.Run(frame, true, device, commands, Color.Black,
                    f =>
                    {
                        scene.Begin(); scene.Draw(mesh, Matrix4x4.Identity, Color.White);
                        scene.PrepareFrame(); prepared.Prepare(f);
                    },
                    f => { prepared.Render(f, output.Framebuffer); throw new InvalidOperationException("injected UI failure"); },
                    null, true));
            Assert.Equal(1, device.Submits);
            Assert.Equal(0, device.Presents);
            Assert.Equal(1, frame.PreparePhaseCompositeCount);
            Assert.Null(GpuRecording.OpenOwner(device));
            bool drained = false;
            gpu.OnWaitForIdle = () =>
            {
                if (!drained) Assert.All(meshBuffers, buffer => Assert.False(buffer.Disposed));
                drained = true;
            };
            scene.Dispose(); disposed = true;
            gpu.OnWaitForIdle = null;
            Assert.True(drained);
            Assert.All(meshBuffers, buffer => Assert.True(buffer.Disposed));
        }
        finally
        {
            gpu.OnWaitForIdle = null;
            if (!disposed) scene.Dispose();
            frame.DisposeSubmission();
        }
    }

    sealed class Tap(Action<FrameCaptureContext> record) : IFrameCaptureTap
    {
        public void Record(FrameCaptureContext context) => record(context);
        public void AfterSubmit(FrameCaptureContext context) { }
        public void Detach(Action<IDisposable> retire) { }
    }

    internal sealed class Rig : IDisposable
    {
        internal readonly FakeGpuDevice Device = new(GpuBackendKind.MetalNative);
        internal readonly Scene3D Scene;
        internal readonly IGpuTexture Texture;
        internal readonly IGpuFramebuffer Output;
        internal readonly RecordingGpuCommandList Prefix, Tail;
        internal Rig()
        {
            Texture = Device.Factory.CreateTexture(GpuTextureDescription.Texture2D(32, 24,
                GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            Output = Device.Factory.CreateFramebuffer(null, Texture);
            Scene = new Scene3D(Device, Output.Outputs, new ShadowSettings { Mode = ShadowMode.Off });
            Scene.Post.RenderScale = RenderScale.MatchViewport;
            Scene.Post.Quality.AntiAliasing = AntiAliasing.Off;
            Scene.Post.Hdr.Enabled = false;
            Scene.Post.Bloom.Enabled = false;
            Scene.Post.Outline = false;
            Scene.Post.Quantize = false;
            Scene.Post.Starfield = false;
            Scene.Camera.Frame(Vector3.Zero, new Vector3(2, 2, 2));
            Prefix = new RecordingGpuCommandList(Device.Factory.CreateCommandList());
            Tail = new RecordingGpuCommandList(Device.Factory.CreateCommandList()) { CapturePayloads = true };
        }
        public void Dispose()
        {
            Scene.Dispose(); Prefix.Dispose(); Tail.Dispose(); Output.Dispose(); Texture.Dispose(); Device.Dispose();
        }
    }
}
