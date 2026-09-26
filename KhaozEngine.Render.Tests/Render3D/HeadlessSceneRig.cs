using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// A <see cref="Scene3D"/> on a <see cref="FakeGpuDevice"/>, rendered through its real frame path with every GPU command
/// dropped. The temporal foundations tests read the CPU state a frame leaves behind (the view snapshot, the cascade
/// fit, the culled set, the history), never pixels, so they run on the ordinary push-path suite with no device. The
/// internal target tracks the viewport, so a test sizes it by the viewport it renders at.
/// </summary>
internal sealed class HeadlessSceneRig : IDisposable
{
    internal const int Width = 64, Height = 48;

    readonly IGpuTexture _color;
    readonly IGpuFramebuffer _target;
    readonly IGpuCommandList _commands;

    internal HeadlessSceneRig()
    {
        Device = new FakeGpuDevice();
        _color = Device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
            Width, Height, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        _target = Device.Factory.CreateFramebuffer(null, _color);
        _commands = Device.Factory.CreateCommandList();
        Scene = new Scene3D(Device, _target.Outputs);
        Scene.Post.Starfield = false;
        Scene.Post.RenderScale = RenderScale.MatchViewport;
    }

    internal FakeGpuDevice Device { get; }
    internal Scene3D Scene { get; }
    internal FakeGpuResourceFactory Factory => (FakeGpuResourceFactory)Device.Factory;

    /// <summary>One whole frame at the default viewport: Begin, the caller's draws, PrepareFrame, one render.</summary>
    internal void Frame(Action<Scene3D>? draw = null, IGpuCommandList? commands = null)
        => Frame(Width, Height, draw, commands);

    /// <summary>One whole frame at a <paramref name="viewportWidth"/> by <paramref name="viewportHeight"/> viewport.</summary>
    internal void Frame(int viewportWidth, int viewportHeight, Action<Scene3D>? draw = null,
        IGpuCommandList? commands = null)
    {
        Scene.Begin();
        draw?.Invoke(Scene);
        Scene.PrepareFrame();
        Render(viewportWidth, viewportHeight, commands);
    }

    /// <summary>A render with no <see cref="Scene3D.Begin"/> before it: a second render inside the same frame.</summary>
    internal void Render(int viewportWidth = Width, int viewportHeight = Height, IGpuCommandList? commands = null)
    {
        IGpuCommandList cl = commands ?? _commands;
        cl.Begin();
        Scene.RenderInternal(cl, viewportWidth, viewportHeight, _target);
        cl.End();
    }

    public void Dispose()
    {
        Scene.Dispose();
        _commands.Dispose();
        _target.Dispose();
        _color.Dispose();
        Device.Dispose();
    }
}
