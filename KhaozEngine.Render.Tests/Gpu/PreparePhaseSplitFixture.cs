using System;
using System.Numerics;
using System.Linq;
using System.Collections.Generic;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Windowing;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

// One lazily created scene for the image matrix. Eight-frame sequences align the native jitter cycle,
// and CameraCut drops history between sequences. A separate parity fact checks that reuse against a fresh scene.
public sealed class PreparePhaseSplitFixture : IDisposable
{
    const int Width = 64, Height = 40, Frames = 8;
    readonly int? _recordingSlots;
    public PreparePhaseSplitFixture() { }
    internal PreparePhaseSplitFixture(int recordingSlots) => _recordingSlots = recordingSlots;
    internal int RetireCap { get { EnsureScene(); return _scene!.RetireSealedBatchCap; } }
    GpuDeviceContext? _gpu;
    PostChainRig.Output? _output;
    FrameOutputDevice? _device;
    Scene3D? _scene;
    MeshHandle _mesh;
    FoliageBatch? _probeFoliage;
    internal ShadowStageProbeSnapshot?[] ProbeSnapshots { get; private set; } = [];
    internal RecordingWaitCallbackResult[] WaitCallbacks { get; private set; } = [];
    internal RecordingWaitBoundary[] WaitBoundaries { get; private set; } = [];
    internal string[][] ProbeDrawOrder { get; private set; } = [];

    internal string ValidationProof
    {
        get
        {
            EnsureScene();
            if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
            var native = Assert.IsType<MetalGpuDevice>(_gpu!.GpuDevice);
            var arming = MetalValidationReader.Current();
            string deviceClass = native.Handle.ClassName();
            Assert.True(arming.DebugLayerArmed);
            Assert.False(MetalValidation.DisagreesWithArming(arming.DebugLayerArmed, arming.ShaderValidationArmed, deviceClass));
            return $"Backend={native.Backend}, device={native.Capabilities.DeviceName}, class={deviceClass}, API validation armed={arming.DebugLayerArmed}, shader validation armed={arming.ShaderValidationArmed}.";
        }
    }

    internal GpuBackendKind Backend { get { EnsureScene(); return _device!.Backend; } }

    void EnsureScene()
    {
        if (_scene is not null) return;
        _gpu = _recordingSlots is { } slots
            ? MetalRecordingCapacityFixture.WithSlots(slots, () => GpuDeviceContext.CreateHeadless(GpuBackendKind.MetalNative))
            : GpuDeviceContext.CreateHeadless();
        _output = new PostChainRig.Output(_gpu.GpuDevice, Width, Height);
        _device = new FrameOutputDevice(_gpu.GpuDevice, _output);
        _scene = new Scene3D(_device, _output.Framebuffer.Outputs);
        _mesh = _scene.LoadMesh(MeshPrimitives.Sphere(0.6f));
    }

    public void Dispose()
    {
        _probeFoliage?.Dispose();
        _scene?.Dispose();
        _output?.Dispose();
        _device?.Dispose();
        _gpu?.Dispose();
    }

    internal byte[][] Render(bool split, bool transparent, bool temporal, bool geometry, bool hdr,
        SceneDebugView debugView = SceneDebugView.None, bool resize = false, ScreenTransitionStyle? transitionStyle = null,
        BillboardBlend? atmosphereBillboards = null, ShadowStageProbeMode probeMode = ShadowStageProbeMode.Off,
        bool pointShadows = false, bool ringWaitProbe = false)
    {
        EnsureScene();
        IGpuDevice gpu = _gpu!.GpuDevice;
        FrameOutputDevice device = _device!;
        Scene3D scene = _scene!;
        device.Output = _output!;
        device.Submits = device.Presents = 0;
        scene.CameraCut();
        scene.DebugView = debugView;
        scene.Post.Atmosphere.Enabled = atmosphereBillboards.HasValue;
        scene.Post.Atmosphere.WorldUnitsPerKilometre = 1;
        scene.Post.Atmosphere.AerialDistanceScale = 20;
        scene.Post.RenderScale = RenderScale.MatchViewport;
        scene.Post.Quality.AntiAliasing = temporal ? AntiAliasing.Temporal : AntiAliasing.Off;
        scene.Post.Temporal.Upscale = TemporalUpscale.Native;
        scene.Post.Starfield = false;
        scene.Post.Hdr.Enabled = hdr;
        scene.Post.TransparentBackground = transparent;
        scene.Post.Bloom.Enabled = geometry;
        scene.Post.BackgroundColor = new Color(0.05f, 0.1f, 0.2f, 1f);
        scene.Camera.Frame(Vector3.Zero, new Vector3(2.2f, 2.2f, 2.2f));
        IScreenTransition? transition = transitionStyle switch
        {
            ScreenTransitionStyle.Solid => new HardBlink(holdTimeoutSeconds: 0, revealSeconds: 0.4f),
            ScreenTransitionStyle.FrozenCrossfade => new CameraDissolve(holdTimeoutSeconds: 0, revealSeconds: 0.4f),
            _ => null,
        };
        MeshHandle mesh = _mesh;
        ProbeSnapshots = new ShadowStageProbeSnapshot?[Frames];
        WaitCallbacks = new RecordingWaitCallbackResult[Frames];
        WaitBoundaries = new RecordingWaitBoundary[Frames + 1];
        ProbeDrawOrder = new string[Frames][];
        Guid epoch = Guid.NewGuid();
        if (pointShadows)
        {
            scene.Post.Quality.Shadows.PointShadows.FaceResolution = 64;
            _probeFoliage ??= scene.CreateFoliageBatch([
                new(mesh, Matrix4x4.Identity, .1f, new FoliageMotionProfile(.3f, .15f, .4f, .035f), true)]);
        }
        using var prepared = new PreparePhaseSceneSubmission(device, scene);
        using var commands = new RecordingGpuCommandList(gpu.Factory.CreateCommandList())
        { CommandTrace = new List<RecordingGpuCommandList.Command>() };
        using var taps = new FrameCaptureTaps(device, device);
        using var capture = new RetainedCapture(device, Frames);
        taps.Add(capture);
        var frame = new Frame { Commands = commands, Width = Width, Height = Height };
        int mainSubmissions = 0;
        device.BeforeSubmit = list => { if (ReferenceEquals(list, commands)) mainSubmissions++; };
        IGpuTexture[] retained = new IGpuTexture[Frames];
        PostChainRig.Output? resizedOutput = null;
        try
        {
            for (int i = 0; i < Frames; i++)
            {
                int index = i;
                if (resize && i == Frames / 2)
                {
                    device.WaitForIdle();
                    resizedOutput = new PostChainRig.Output(gpu, 96, 64);
                    device.Output = resizedOutput;
                    frame.Width = 96; frame.Height = 64;
                }
                retained[i] = gpu.Factory.CreateTexture(GpuTextureDescription.Texture2D((uint)frame.Width, (uint)frame.Height,
                    GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
                commands.Clear();
                commands.CommandTrace!.Clear();
                frame.BeginShadowStageCallback(new ShadowStageProbeRequest(epoch, i + 1, probeMode));
                frame.BeginRecordingWaitCallback(new(epoch, i + 1, ringWaitProbe, probeMode));
                if (ringWaitProbe) WaitBoundaries[i] = ReadWaitBoundary(device);
                RendererFramePolicy.Run(frame, true, device, commands, Color.Black, _ =>
                {
                    frame.DeclareRecordingWaitPrefix(split);
                    scene.ConfigureShadowStageProbe(frame.ShadowStageRequest);
                    scene.Begin();
                    scene.EffectTimeSeconds = index / 60f;
                    if (index == 2) transition?.Begin();
                    if (index > 2) transition?.Update(0.05f, destinationReady: true);
                    scene.ScreenTransition = transition;
                    if (atmosphereBillboards is { } blend)
                    {
                        scene.Post.Atmosphere.AerialDistanceScale = 4 + index * 4;
                        scene.Camera.Frame(Vector3.Zero, new Vector3(2.2f + index * 0.04f, 2.2f + index * 0.3f, 2.2f));
                        scene.DrawBillboard(new Vector3(0, 0.2f, 0), 1.4f, new Color(0.8f, 0.3f, 0.1f, 0.7f), blend);
                    }
                    if (geometry)
                    {
                        Matrix4x4 world = Matrix4x4.CreateTranslation(index * 0.02f, 0, 0);
                        scene.Draw(mesh, world, Color.White, Material.Glowing(new Color(1f, 0.6f, 0.2f, 1f)));
                        scene.DrawMeshOutline(mesh, world, new Color(0.2f, 1f, 0.2f, 1f), 2);
                        scene.DebugLine(new Vector3(-0.8f, 0, 0), new Vector3(0.8f, 0, 0), Color.White);
                    }
                    if (pointShadows) QueueProbePoint(scene);
                    scene.PrepareFrame();
                    if (split) prepared.Prepare(frame);
                }, _ =>
                {
                    Render3DSurface.RecordSceneOutput(scene, frame, split ? prepared : null, device.SwapchainFramebuffer);
                    commands.CopyTexture(device.Output.Texture, retained[index]);
                }, taps, true);
                frame.CompleteShadowStageCallback();
                frame.CompleteRecordingWaitCallback();
                WaitCallbacks[i] = frame.CompletedRecordingWaitCallback;
                if (ringWaitProbe) WaitBoundaries[i + 1] = ReadWaitBoundary(device);
                var completed = frame.CompletedShadowStageCallback;
                Assert.True(completed.Completed);
                Assert.Equal(probeMode == ShadowStageProbeMode.Off ? 0 : 1, completed.ProducerCount);
                ProbeSnapshots[i] = completed.Snapshot;
                ProbeDrawOrder[i] = commands.CommandTrace.Select(c => $"{c.Kind}/{c.Slot}/{c.Value}").ToArray();
            }
            // Read only after all eight frames. Per-frame drains would conceal ring-wrap lifetime bugs.
            device.WaitForIdle();
            Assert.Equal(Frames, mainSubmissions);
            Assert.True(device.Submits >= Frames * (split ? 2 : 1));
            Assert.Equal(Frames, device.Presents);
            Assert.Equal(split ? Frames : 0, frame.PreparePhaseSubmissionCount);
            Assert.Equal(split ? Frames : 0, frame.PreparePhaseCompositeCount);
            var images = new byte[Frames][];
            for (int i = 0; i < Frames; i++)
            {
                images[i] = GpuReadback.ToRgba(gpu, retained[i], (int)retained[i].Width, (int)retained[i].Height);
                Assert.Equal(images[i], GpuReadback.ToRgba(gpu, capture.Images[i], (int)retained[i].Width, (int)retained[i].Height));
                if (!transparent && !geometry)
                    for (int a = 3; a < images[i].Length; a += 4) Assert.Equal(255, images[i][a]);
            }
            return images;
        }
        finally
        {
            device.WaitForIdle();
            frame.DisposeSubmission();
            foreach (var image in retained) image?.Dispose();
            device.Output = _output!;
            device.BeforeSubmit = null;
            resizedOutput?.Dispose();
        }
    }

    static RecordingWaitBoundary ReadWaitBoundary(IGpuRingWaitSource source)
    {
        var status = source.TryReadRingWaitTotals(out var totals);
        return new(status == GpuRingWaitSnapshotStatus.Available ? RecordingWaitBoundaryStatus.Available
            : status == GpuRingWaitSnapshotStatus.Contended ? RecordingWaitBoundaryStatus.Contended
            : RecordingWaitBoundaryStatus.Unavailable, status == GpuRingWaitSnapshotStatus.Available ? totals : null);
    }

    void QueueProbePoint(Scene3D scene)
    {
        scene.DrawFoliage(_probeFoliage!, Vector3.Zero, new FoliageRenderSettings { DrawRadius = 100, DistantDensity = 1 });
        scene.AddLight(new Vector3(0, 2, 0), Color.White, 6, 1, LightShadow.Dynamic);
    }

    sealed class RetainedCapture(IGpuDevice device, int frames) : IFrameCaptureTap, IDisposable
    {
        internal IGpuTexture[] Images { get; } = new IGpuTexture[frames];
        int _index;
        public void Record(FrameCaptureContext context)
        {
            var image = Assert.IsAssignableFrom<IGpuTexture>(context.AcquireSwapchainImage());
            var copy = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(image.Width, image.Height,
                GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            Images[_index++] = copy;
            context.Commands.CopyTexture(image, copy);
        }
        public void AfterSubmit(FrameCaptureContext context) { }
        public void Detach(Action<IDisposable> retire) { }
        public void Dispose() { foreach (var image in Images) image?.Dispose(); }
    }
}
