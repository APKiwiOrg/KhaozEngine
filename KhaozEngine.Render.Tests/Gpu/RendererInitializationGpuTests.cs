using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Windowing;
using KhaozEngine.Windowing;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

public sealed class RendererInitializationGpuTests(ITestOutputHelper log)
{
    const int Width = 64, Height = 40, Frames = 2;
    static readonly Color Poison = new(0.9f, 0.1f, 0.7f, 0.3f);

    [GpuTheory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, false)]
    [InlineData(false, false, false, true)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, true)]
    public void Full_scene_output_and_same_frame_capture_are_identical_with_host_initialization_on_or_off(
        bool transparent, bool temporal, bool geometry, bool hdr)
    {
        using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
        byte[][] host = Render(gpu.GpuDevice, false, transparent, temporal, geometry, hdr);
        byte[][] renderer = Render(gpu.GpuDevice, true, transparent, temporal, geometry, hdr);
        int zeroAlpha = 0, visibleAlpha = 0;
        for (int frame = 0; frame < Frames; frame++)
        {
            Assert.Equal(Width * Height * 4, renderer[frame].Length);
            Assert.Equal(host[frame], renderer[frame]);
            for (int i = 3; i < renderer[frame].Length; i += 4)
            {
                byte alpha = renderer[frame][i];
                if (!transparent) Assert.Equal(255, alpha);
                if (alpha == 0) zeroAlpha++;
                else visibleAlpha++;
            }
        }
        if (transparent)
        {
            Assert.True(zeroAlpha > 0, "The empty background must remain transparent.");
            if (geometry) Assert.True(visibleAlpha > 0, "The fixture must render visible geometry.");
            else Assert.Equal(0, visibleAlpha);
        }
        if (geometry) Assert.NotEqual(renderer[0][..4], renderer[0][((Height / 2 * Width + Width / 2) * 4)..][..4]);
        log.WriteLine($"{gpu.GpuDevice.Backend}: transparent={transparent}, temporal={temporal}, geometry={geometry}, hdr={hdr}, "
            + $"{Frames} frames, {Frames * Width * Height} pixels, {Frames * Width * Height * 4} RGBA bytes identical. "
            + $"Alpha zero={zeroAlpha}, visible={visibleAlpha}. Capture matches every byte before present.");
    }

    static byte[][] Render(IGpuDevice gd, bool rendererOwned, bool transparent, bool temporal, bool geometry, bool hdr)
    {
        using var output = new PostChainRig.Output(gd, Width, Height);
        using var device = new FrameOutputDevice(gd, output);
        using var scene = new Scene3D(gd, output.Framebuffer.Outputs);
        scene.Post.RenderScale = RenderScale.MatchViewport;
        scene.Post.Quality.AntiAliasing = temporal ? AntiAliasing.Temporal : AntiAliasing.Off;
        scene.Post.Temporal.Upscale = TemporalUpscale.Native;
        scene.Post.Starfield = false;
        scene.Post.Hdr.Enabled = hdr;
        scene.Post.TransparentBackground = transparent;
        scene.Post.Bloom.Enabled = geometry;
        scene.Post.BackgroundColor = new Color(0.05f, 0.1f, 0.2f, 1f);
        scene.Camera.Frame(Vector3.Zero, new Vector3(2.2f, 2.2f, 2.2f));
        MeshHandle sphere = geometry ? scene.LoadMesh(MeshPrimitives.Sphere(0.6f)) : default;
        using var commands = new RecordingGpuCommandList(gd.Factory.CreateCommandList());
        using var taps = new FrameCaptureTaps(device, device);
        var capture = new CaptureTap(gd, device);
        taps.Add(capture);
        var frame = new Frame { Commands = commands, Width = Width, Height = Height };
        var results = new byte[Frames][];
        try
        {
            for (int index = 0; index < Frames; index++)
            {
                int current = index;
                commands.Clear();
                RendererFramePolicy.Run(frame, true, device, commands, Poison, _ =>
                {
                    // Distinct stale contents prove the final composition overwrites the entire output.
                    using IGpuCommandList seed = gd.Factory.CreateCommandList();
                    using (GpuRecording.Open(gd, seed, nameof(RendererInitializationGpuTests)))
                    {
                        seed.SetFramebuffer(output.Framebuffer);
                        seed.ClearColorTarget(0, Poison);
                    }
                    gd.Submit(seed);
                    scene.Begin();
                    scene.EffectTimeSeconds = current / 60f;
                    if (geometry) scene.Draw(sphere, Matrix4x4.CreateTranslation(current * 0.05f, 0f, 0f),
                        Color.White, Material.Glowing(new Color(1f, 0.6f, 0.2f, 1f)));
                    scene.PrepareFrame();
                }, _ =>
                {
                    Assert.Equal(rendererOwned ? 0 : 1, commands.FramebufferBinds);
                    Assert.Equal(rendererOwned ? 0 : 1, commands.ColourClears.Count);
                    scene.RenderInternal(commands, Width, Height, output.Framebuffer);
                    Assert.Equal(transparent ? Color.Transparent : Color.Black, commands.ColourClears[^1].Rgba);
                }, taps, rendererOwned);
                results[index] = GpuReadback.ToRgba(gd, output.Texture, Width, Height);
                Assert.Equal(results[index], capture.Rgba);
            }
            Assert.Equal(Frames, device.Submits);
            Assert.Equal(Frames, device.Presents);
            return results;
        }
        finally { frame.DisposeSubmission(); }
    }

    sealed class CaptureTap(IGpuDevice gd, FrameOutputDevice device) : IFrameCaptureTap
    {
        IGpuTexture? _image;
        internal byte[]? Rgba;
        public void Record(FrameCaptureContext context) => _image = Assert.IsAssignableFrom<IGpuTexture>(context.AcquireSwapchainImage());
        public void AfterSubmit(FrameCaptureContext context)
        {
            Assert.Equal(device.Presents + 1, device.Submits);
            gd.WaitForIdle();
            Rgba = GpuReadback.ToRgba(gd, _image!, Width, Height);
        }
        public void Detach(Action<IDisposable> retire) { }
    }
}
