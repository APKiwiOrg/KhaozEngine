using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Diagnostics;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal sealed class PostTraceRig : IDisposable
{
    readonly string _mode;
    readonly RendererPerfRecipe _recipe;
    readonly IGpuTexture _texture;
    readonly IGpuFramebuffer _target;
    readonly SpriteBatch? _ui;
    internal readonly PostTraceDevice Device = new();
    internal readonly GeometryTraceCommands Commands;
    internal readonly Scene3D Scene;
    internal int PreparationSubmits;
    internal PostTraceRig(string mode, RendererPerfRecorder? recorder = null)
    {
        _mode = mode;
        _recipe = RendererPerfRecipe.Create(mode == "outdoors" ? "outdoors" : "effects-ui");
        IGpuDevice device = recorder is null ? Device : RendererPerfDevice.Wrap(Device, recorder);
        _texture = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(1920, 1080,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        _target = device.Factory.CreateFramebuffer(null, _texture);
        Scene = new Scene3D(device, _target.Outputs);
        _recipe.ResetBlock(Scene);
        Scene.TemporalResolveEntryForTests = mode == "split" ? TemporalResolveEntry.Split : TemporalResolveEntry.Fused;
        if (_recipe.UiScene is { } ui)
        {
            ui.Load(device);
            _ui = new SpriteBatch(device, _target.Outputs);
        }
        Commands = (GeometryTraceCommands)Device.Factory.CreateCommandList();
        Device.UploadTrace = Commands;
    }
    internal void Frame(int index, IGpuCommandList? list = null)
    {
        Commands.Events.Clear();
        Scene.Begin();
        _recipe.Queue(Scene, index);
        if (_mode != "outdoors")
        {
            if (_mode == "legacy" || _mode == "msaa-blob")
            {
                Scene.Post.Quality.AntiAliasing = _mode == "legacy" ? AntiAliasing.Fxaa : AntiAliasing.Msaa(4, true);
                Scene.Post.Quantize = true;
                Scene.Post.Background = BackgroundMode.Starfield;
                if (_mode == "legacy") Scene.Post.Hdr.Enabled = false;
                if (_mode == "msaa-blob")
                {
                    Scene.Post.Quality.Shadows.Mode = ShadowMode.Blob;
                    Scene.AddShadowBlob(new(Vector3.Zero, 0f, 1f));
                }
            }
            else if (Scene.DebugView == SceneDebugView.None) Scene.DebugView = SceneDebugView.History;
            Scene.DebugFilledQuad(Vector3.Zero, 1f, Color.White);
            Scene.DebugLine(Vector3.Zero, Vector3.One, Color.White);
            Scene.DebugWireSphere(Vector3.Zero, 1f, Color.White);
            Scene.DrawBillboard(Vector3.Zero, 1f, Color.White, BillboardBlend.Additive);
            Scene.DrawBillboard(Vector3.One, 1f, Color.White, BillboardBlend.Alpha);
            // Exercise both fallback and frozen-image transition writers, then cached snapshot skipping.
            if (index == 64) { var blink = new HardBlink(); blink.Begin(); Scene.ScreenTransition = blink; }
            if (index == 66) { var fade = new CameraDissolve(); fade.Begin(); Scene.ScreenTransition = fade; }
        }
        int submits = Device.Native.Submits;
        Scene.PrepareFrame();
        PreparationSubmits = Device.Native.Submits - submits;
        var commands = list ?? Commands;
        commands.Begin();
        try
        {
            Scene.RenderInternal(commands, 1920, 1080, _target);
            if (_ui is { } batch)
            {
                var key = new RenderPassKey("ui.sprites");
                Scene.PassObserver?.BeginPass(commands, key);
                commands.SetFramebuffer(_target);
                batch.NewFrame(commands, 1920, 1080);
                batch.Begin(); _recipe.UiScene!.Queue(batch, index); batch.End();
                Scene.PassObserver?.EndPass(commands, key);
            }
        }
        finally { commands.End(); }
    }
    public void Dispose()
    {
        _ui?.Dispose(); Scene.Dispose(); Commands.Dispose(); _target.Dispose(); _texture.Dispose();
        _recipe.Dispose(); Device.Dispose();
    }
}

/// <summary>Inert resources with real compute recording and sparse MSAA routing enabled.</summary>
internal sealed class PostTraceDevice : IGpuDevice
{
    internal readonly OpenListTrackingGpuDevice Native = new();
    readonly PostTraceFactory _factory;
    internal PostTraceDevice() => _factory = new(Native.Factory);
    internal System.Collections.Generic.List<GeometryTraceCommands> Lists => _factory.Lists;
    internal int Waits;
    internal GeometryTraceCommands? UploadTrace;
    public GpuBackendKind Backend => Native.Backend;
    public GpuCapabilities Capabilities { get; } = new(false, true, "post trace", maxMsaaSampleCount: 4,
        supportsCompute: true, supportsShadowMaps: true)
    { MaxColorAttachments = 8, MaxStorageBufferRangeBytes = 536870912 };
    public IGpuResourceFactory Factory => _factory;
    public IGpuFramebuffer? SwapchainFramebuffer => null;
    public IGpuSampler PointSampler => Native.PointSampler;
    public IGpuSampler LinearSampler => Native.LinearSampler;
    public bool SyncToVerticalBlank { get; set; }
    public void Submit(IGpuCommandList cl) => Native.Submit(cl);
    public void Submit(IGpuCommandList cl, IGpuFence fence) => Native.Submit(cl, fence);
    public void WaitForIdle() { Waits++; Native.WaitForIdle(); }
    public void UpdateBuffer<T>(IGpuBuffer b, uint offset, in T data) where T : unmanaged
    { UploadTrace?.DeviceBuffer(b, offset, MemoryMarshal.CreateReadOnlySpan(in data, 1)); Native.UpdateBuffer(b, offset, in data); }
    public void UpdateBuffer<T>(IGpuBuffer b, uint offset, ReadOnlySpan<T> data) where T : unmanaged
    { UploadTrace?.DeviceBuffer(b, offset, data); Native.UpdateBuffer(b, offset, data); }
    public void UpdateBuffer<T>(IGpuBuffer b, uint offset, T[] data) where T : unmanaged
    { UploadTrace?.DeviceBuffer<T>(b, offset, data); Native.UpdateBuffer(b, offset, data); }
    public void UpdateTexture(IGpuTexture t, byte[] data, uint x, uint y, uint w, uint h)
    { UploadTrace?.DeviceTexture(t, data, x, y, w, h); Native.UpdateTexture(t, data, x, y, w, h); }
    public void UpdateTexture(IGpuTexture t, byte[] data, uint x, uint y, uint w, uint h, uint mip, uint layer)
    { UploadTrace?.DeviceTexture(t, data, x, y, w, h, mip, layer); Native.UpdateTexture(t, data, x, y, w, h, mip, layer); }
    public MappedData Map(IGpuTexture t, GpuMapMode mode) => Native.Map(t, mode);
    public void Unmap(IGpuTexture t) => Native.Unmap(t);
    public MappedData Map(IGpuBuffer b, GpuMapMode mode) => Native.Map(b, mode);
    public void Unmap(IGpuBuffer b) => Native.Unmap(b);
    public void ResizeSwapchain(uint w, uint h) { }
    public void Present() { }
    public void Dispose() => Native.Dispose();
}
