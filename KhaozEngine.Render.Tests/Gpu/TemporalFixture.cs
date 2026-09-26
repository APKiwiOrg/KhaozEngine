using System;
using System.Diagnostics;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// A real-device <see cref="Scene3D"/> driven frame after frame the way a game drives it: Begin, a deterministic effect
/// clock, the caller's draws, PrepareFrame, one recording into the fixture's own display target, Submit and a drain.
/// The internal size follows the display size (<see cref="RenderScale.MatchViewport"/>), the render origin is pinned at
/// zero until a test moves it, and the camera looks at the world origin. Round 2 renders its acceptance scenes through
/// it (TEMPORAL-RESOLVE-UPSCALING-DESIGN-2026-09-24).
/// </summary>
public sealed class TemporalFixture : IDisposable
{
    readonly GpuDeviceContext _gpu;
    readonly IGpuCommandList _commands;
    IGpuTexture _targetTexture;
    int _frame;

    /// <summary>320 x 180 with temporal rendering forced on, the shape an <c>IClassFixture</c> needs.</summary>
    public TemporalFixture() : this(320, 180, static s => s.ForceTemporalForTests = true) { }

    /// <summary>A fixture presenting at <paramref name="displayWidth"/> x <paramref name="displayHeight"/>.
    /// <paramref name="setup"/> runs once after the defaults. Temporal rendering stays off unless it asks.</summary>
    public TemporalFixture(int displayWidth, int displayHeight, Action<Scene3D>? setup = null)
    {
        _gpu = GpuDeviceContext.CreateHeadless();
        IGpuResourceFactory f = _gpu.GpuDevice.Factory;
        _targetTexture = CreateTarget(f, displayWidth, displayHeight);
        Target = f.CreateFramebuffer(null, _targetTexture);
        _commands = f.CreateCommandList();
        DisplayWidth = displayWidth;
        DisplayHeight = displayHeight;
        Scene = new Scene3D(_gpu.GpuDevice, Target.Outputs);
        Scene.Post.UseSmoothPreset();
        Scene.Post.RenderScale = RenderScale.MatchViewport;
        Scene.RenderOrigin = Vector3.Zero;
        Scene.Camera.Target = Vector3.Zero;
        setup?.Invoke(Scene);
    }

    public IGpuDevice Device => _gpu.GpuDevice;

    public Scene3D Scene { get; }

    /// <summary>The display framebuffer every frame presents into.</summary>
    public IGpuFramebuffer Target { get; private set; }

    public int DisplayWidth { get; private set; }

    public int DisplayHeight { get; private set; }

    /// <summary>Wall time of the last frame's Submit and drain, in milliseconds: the GPU work plus its submission.</summary>
    public double LastSubmitMilliseconds { get; private set; }

    /// <summary>One frame: Begin, a deterministic effect clock of n / 60 seconds, the draw callback with the frame
    /// number n (every Begin the fixture issued, skipped ones included), PrepareFrame, the recording into
    /// <see cref="Target"/>, Submit and a drain, then the RGBA8 readback.</summary>
    public byte[] Frame(Action<Scene3D, int> draw)
    {
        Render(draw);
        return GpuReadback.ToRgba(Device, _targetTexture, DisplayWidth, DisplayHeight);
    }

    /// <summary><see cref="Frame"/> without the readback, <paramref name="count"/> times.</summary>
    public void Frames(int count, Action<Scene3D, int> draw)
    {
        for (int i = 0; i < count; i++) Render(draw);
    }

    void Render(Action<Scene3D, int> draw)
    {
        int n = _frame++;
        Scene.Begin();
        Scene.EffectTimeSeconds = n / 60f;
        draw(Scene, n);
        Scene.PrepareFrame();
        using (GpuRecording.Open(Device, _commands, "TemporalFixture.Frame"))
            Scene.RenderInternal(_commands, DisplayWidth, DisplayHeight, Target);
        long submitted = Stopwatch.GetTimestamp();
        Device.Submit(_commands);
        Device.WaitForIdle();
        LastSubmitMilliseconds = Stopwatch.GetElapsedTime(submitted).TotalMilliseconds;
    }

    /// <summary>Begin <paramref name="count"/> frames without rendering them, so a fresh fixture can meet another at
    /// the same frame index and jitter phase.</summary>
    public void SkipFrames(int count)
    {
        for (int i = 0; i < count; i++) { Scene.Begin(); _frame++; }
    }

    /// <summary>Change the display size: drain, then rebuild <see cref="Target"/> at the same format.</summary>
    public void Resize(int displayWidth, int displayHeight)
    {
        Device.WaitForIdle();
        Target.Dispose();
        _targetTexture.Dispose();
        _targetTexture = CreateTarget(Device.Factory, displayWidth, displayHeight);
        Target = Device.Factory.CreateFramebuffer(null, _targetTexture);
        DisplayWidth = displayWidth;
        DisplayHeight = displayHeight;
    }

    static IGpuTexture CreateTarget(IGpuResourceFactory f, int width, int height) =>
        f.CreateTexture(GpuTextureDescription.Texture2D((uint)width, (uint)height, GpuPixelFormat.R8G8B8A8UNorm,
            GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));

    public void Dispose()
    {
        Scene.Dispose();
        _commands.Dispose();
        Target.Dispose();
        _targetTexture.Dispose();
        _gpu.Dispose();
    }
}
