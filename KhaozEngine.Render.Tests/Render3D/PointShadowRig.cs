using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;

namespace KhaozEngine.Tests.Render3D;

/// <summary>A headless point-shadow scene over the fake device, for tests that read what a frame decided (rebuild
/// counts, draw counts, the caster index) rather than what it drew. The key light is off so its pass stays out of
/// every reading, and frustum culling is off so every queued instance keeps the slot its queue position gives it,
/// which is what lets a test regroup the same queue itself.</summary>
internal sealed class PointShadowRig : IDisposable
{
    readonly FakeGpuDevice _device;
    readonly FakeGpuResourceFactory _factory;
    readonly IGpuTexture _targetTexture;
    readonly IGpuFramebuffer _target;

    internal PointShadowRig()
    {
        _device = new FakeGpuDevice();
        _factory = (FakeGpuResourceFactory)_device.Factory;
        _targetTexture = _factory.CreateTexture(GpuTextureDescription.Texture2D(
            16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        _target = _factory.CreateFramebuffer(null, _targetTexture);
        Settings = new ShadowSettings { Mode = ShadowMode.Off };
        Scene = new Scene3D(_device, _target.Outputs, Settings) { FrustumCulling = false };
    }

    internal ShadowSettings Settings { get; }
    internal Scene3D Scene { get; }

    /// <summary>One whole frame of <paramref name="describe"/>, returning the shadow diagnostics it published.</summary>
    internal ShadowPassDiagnostics RenderFrame(Action<Scene3D> describe)
    {
        Scene.Begin();
        describe(Scene);
        Scene.PrepareFrame();
        using IGpuCommandList commands = _factory.CreateCommandList();
        commands.Begin();
        Scene.RenderInternal(commands, 16, 16, _target);
        commands.End();
        return Scene.LastShadowPassDiagnostics;
    }

    public void Dispose()
    {
        Scene.Dispose();
        _target.Dispose();
        _targetTexture.Dispose();
        _device.Dispose();
    }
}
