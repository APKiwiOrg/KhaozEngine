using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Diagnostics;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CascadeWorkingDepthIntentTests
{
    // #595. Pass observers may record timestamp markers between caster families and split native encoders.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyAnUnobservedCascadeDeclaresItsWorkingDepthDead(bool observed)
    {
        using var device = new FakeGpuDevice();
        using var texture = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(64, 64,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        using var target = device.Factory.CreateFramebuffer(null, texture);
        using var scene = new Scene3D(device, target.Outputs,
            new ShadowSettings { ShadowMapResolution = 128, ShadowCascadeCount = 2 });
        scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
        scene.Post.Starfield = false;
        if (observed) scene.PassObserver = new Observer();
        var mesh = scene.LoadMesh(MeshPrimitives.Box());
        scene.Begin();
        scene.Draw(mesh, Matrix4x4.Identity, Color.White);
        scene.PrepareFrame(64, 64);
        using var commands = new RecordingGpuCommandList(device.Factory.CreateCommandList());
        using (GpuRecording.Open(device, commands, nameof(CascadeWorkingDepthIntentTests)))
            scene.RenderInternal(commands, 64, 64, target);
        var binding = Assert.Single(commands.AttachmentStoreBindings,
            candidate => candidate.Framebuffer.Outputs.Colour is [GpuPixelFormat.R32Float]);
        Assert.Equal(!observed, binding.Stores.DiscardDepth);
        Assert.Equal(!observed, binding.Stores.DiscardStencil);
        Assert.Equal(0ul, binding.Stores.DiscardColourMask);
    }

    sealed class Observer : IRenderPassObserver
    {
        public void BeginPass(IGpuCommandList commands, RenderPassKey key) { }
        public void EndPass(IGpuCommandList commands, RenderPassKey key) { }
        public void SkipPass(IGpuCommandList commands, RenderPassKey key) { }
        public void BeginCpuPhase(string id) { }
        public void EndCpuPhase(string id) { }
    }
}
