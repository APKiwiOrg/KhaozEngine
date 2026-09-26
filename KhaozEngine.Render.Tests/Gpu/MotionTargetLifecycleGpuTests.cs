using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Turning temporal rendering on and off on a real device: nothing of it survives once it is off, and an
/// MSAA request renders single-sample while it is on.</summary>
public sealed class MotionTargetLifecycleGpuTests
{
    const int W = 128, H = 72;

    sealed class Rig : IDisposable
    {
        readonly GpuDeviceContext _gpu = GpuDeviceContext.CreateHeadless();
        readonly IGpuTexture _target;
        readonly IGpuFramebuffer _framebuffer;
        readonly IGpuCommandList _commands;
        readonly MeshHandle _box;

        internal Rig()
        {
            IGpuResourceFactory f = _gpu.GpuDevice.Factory;
            _target = f.CreateTexture(GpuTextureDescription.Texture2D(W, H, GpuPixelFormat.R8G8B8A8UNorm,
                GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            _framebuffer = f.CreateFramebuffer(null, _target);
            _commands = f.CreateCommandList();
            Scene = new Scene3D(_gpu.GpuDevice, _framebuffer.Outputs);
            Scene.Post.UseSmoothPreset();
            Scene.Post.RenderWidth = W;
            Scene.Post.RenderHeight = H;
            Scene.RenderOrigin = Vector3.Zero;
            Scene.Camera.Target = Vector3.Zero;
            Scene.Camera.OrthoSize = 6f;
            _box = Scene.LoadMesh(MeshPrimitives.Box(2f));
        }

        internal Scene3D Scene { get; }

        internal byte[] Frame()
        {
            Scene.Begin();
            Scene.Draw(_box, Matrix4x4.CreateRotationY(.4f));
            Scene.PrepareFrame();
            using (GpuRecording.Open(_gpu.GpuDevice, _commands, nameof(MotionTargetLifecycleGpuTests)))
                Scene.RenderInternal(_commands, W, H, _framebuffer);
            _gpu.GpuDevice.Submit(_commands);
            _gpu.GpuDevice.WaitForIdle();
            return GpuReadback.ToRgba(_gpu.GpuDevice, _target, W, H);
        }

        public void Dispose()
        {
            Scene.Dispose();
            _commands.Dispose();
            _framebuffer.Dispose();
            _target.Dispose();
            _gpu.Dispose();
        }
    }

    [GpuFact]
    public void TemporalRenderingLeavesNoTraceOnceItIsOff()
    {
        using var rig = new Rig();
        byte[] before = rig.Frame();

        rig.Scene.ForceTemporalForTests = true;
        rig.Frame();
        rig.Scene.DebugView = SceneDebugView.MotionVectors;
        rig.Frame();
        Assert.NotNull(rig.Scene.MotionResourcesForTests);
        Assert.True(rig.Scene.TransparentMotionShadersHeldForTests);
        Assert.True(rig.Scene.MotionVectorsViewBuiltForTests);

        rig.Scene.ForceTemporalForTests = false;
        rig.Scene.DebugView = SceneDebugView.None;
        byte[] after = rig.Frame();
        Assert.Null(rig.Scene.MotionResourcesForTests);
        Assert.False(rig.Scene.TransparentMotionShadersHeldForTests);
        Assert.False(rig.Scene.MotionVectorsViewBuiltForTests);
        Assert.Equal(before, after);
    }

    [GpuFact]
    public void AnMsaaRequestRendersSingleSampleWhileTemporalRenderingIsActive()
    {
        using var rig = new Rig();
        rig.Scene.Post.Quality.AntiAliasing = AntiAliasing.Msaa(4);
        rig.Frame();
        Assert.True(rig.Scene.ModelSampleCountForTests > 1, "the device must honour MSAA for this case to mean anything");

        rig.Scene.ForceTemporalForTests = true;
        rig.Frame();
        Assert.Equal(1, rig.Scene.ModelSampleCountForTests);
        MotionTargetReadback motion = rig.Scene.ReadMotionTargetForTests();
        Assert.Equal((W, H), (motion.Width, motion.Height));

        rig.Scene.ForceTemporalForTests = false;
        rig.Frame();
        Assert.True(rig.Scene.ModelSampleCountForTests > 1);
    }
}
