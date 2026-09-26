using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>The two device capabilities the motion target's wiring depends on, read with no device: an MSAA-capable
/// device renders single-sample while temporal rendering is active, and a backend that inverts clip Y still gets the
/// motion block's matrices exactly as the frame view holds them.</summary>
public sealed class MotionTargetCapabilityTests
{
    const int Width = 32, Height = 24;

    /// <summary>A <see cref="FakeGpuDevice"/> reporting <paramref name="caps"/> in place of its own. Local to this file,
    /// like the other wrapped fakes, so the shared counting harness keeps its single least-capable shape.</summary>
    sealed class CapabilityGpuDevice(GpuCapabilities caps) : IGpuDevice
    {
        readonly FakeGpuDevice _inner = new();

        public GpuBackendKind Backend => _inner.Backend;
        public GpuCapabilities Capabilities => caps;
        public IGpuResourceFactory Factory => _inner.Factory;
        public IGpuFramebuffer? SwapchainFramebuffer => _inner.SwapchainFramebuffer;
        public IGpuSampler PointSampler => _inner.PointSampler;
        public IGpuSampler LinearSampler => _inner.LinearSampler;
        public bool SyncToVerticalBlank
        {
            get => _inner.SyncToVerticalBlank;
            set => _inner.SyncToVerticalBlank = value;
        }

        public void Submit(IGpuCommandList cl) => _inner.Submit(cl);
        public void Submit(IGpuCommandList cl, IGpuFence fence) => _inner.Submit(cl, fence);
        public void WaitForIdle() => _inner.WaitForIdle();
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged
            => _inner.UpdateBuffer(b, offsetBytes, data);
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, T[] data) where T : unmanaged
            => _inner.UpdateBuffer(b, offsetBytes, data);
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged
            => _inner.UpdateBuffer(b, offsetBytes, in data);
        public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height)
            => _inner.UpdateTexture(texture, data, x, y, width, height);
        public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height,
            uint mipLevel, uint arrayLayer)
            => _inner.UpdateTexture(texture, data, x, y, width, height, mipLevel, arrayLayer);
        public MappedData Map(IGpuTexture staging, GpuMapMode mode) => _inner.Map(staging, mode);
        public void Unmap(IGpuTexture staging) => _inner.Unmap(staging);
        public MappedData Map(IGpuBuffer staging, GpuMapMode mode) => _inner.Map(staging, mode);
        public void Unmap(IGpuBuffer staging) => _inner.Unmap(staging);
        public void ResizeSwapchain(uint w, uint h) => _inner.ResizeSwapchain(w, h);
        public void Present() => _inner.Present();
        public void Dispose() => _inner.Dispose();
    }

    sealed class Rig : IDisposable
    {
        readonly CapabilityGpuDevice _device;
        readonly IGpuTexture _colour;
        readonly IGpuFramebuffer _target;
        readonly MeshHandle _box;

        internal Rig(GpuCapabilities caps)
        {
            _device = new CapabilityGpuDevice(caps);
            _colour = _device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                Width, Height, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            _target = _device.Factory.CreateFramebuffer(null, _colour);
            Scene = new Scene3D(_device, _target.Outputs);
            Scene.Post.UseSmoothPreset();
            Scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
            Scene.Camera.Frame(Vector3.Zero, new Vector3(4f, 3f, 4f));
            _box = Scene.LoadMesh(MeshPrimitives.Box(1f));
        }

        internal Scene3D Scene { get; }

        internal RecordingGpuCommandList Frame()
        {
            Scene.Begin();
            Scene.Draw(_box, Matrix4x4.Identity);
            Scene.PrepareFrame();
            var cl = new RecordingGpuCommandList(new NullGpuCommandList()) { CapturePayloads = true };
            Scene.RenderInternal(cl, Width, Height, _target);
            return cl;
        }

        public void Dispose()
        {
            Scene.Dispose();
            _target.Dispose();
            _colour.Dispose();
            _device.Dispose();
        }
    }

    [Fact]
    public void AnMsaaRequestResolvesToFxaaAndRendersSingleSampleWhileTemporalRenderingIsActive()
    {
        using var rig = new Rig(new GpuCapabilities(clipSpaceYInverted: false, depthRangeZeroToOne: true,
            maxMsaaSampleCount: 4));
        Scene3D scene = rig.Scene;
        scene.Post.Quality.AntiAliasing = AntiAliasing.Msaa(4);
        rig.Frame().Dispose();
        Assert.Equal(4, scene.ModelSampleCountForTests);
        Assert.Null(scene.MotionResourcesForTests);

        scene.ForceTemporalForTests = true;
        rig.Frame().Dispose();
        Assert.Equal(1, scene.ModelSampleCountForTests);
        Assert.NotNull(scene.MotionResourcesForTests);
        rig.Frame().Dispose();
        Assert.NotNull(scene.PreviousFrameView);

        // While temporal, the MSAA request already resolves to FXAA, so asking for FXAA itself changes no reset key
        // and the history carries on.
        scene.Post.Quality.AntiAliasing = AntiAliasing.Fxaa;
        rig.Frame().Dispose();
        Assert.NotNull(scene.PreviousFrameView);

        scene.Post.Quality.AntiAliasing = AntiAliasing.Msaa(4);
        scene.ForceTemporalForTests = false;
        rig.Frame().Dispose();
        Assert.Equal(4, scene.ModelSampleCountForTests);
        Assert.Null(scene.MotionResourcesForTests);
    }

    [Fact]
    public void TheMotionBlockCarriesTheFrameViewsMatricesUncorrectedOnABackendThatInvertsClipY()
    {
        var caps = new GpuCapabilities(clipSpaceYInverted: true, depthRangeZeroToOne: true);
        using var rig = new Rig(caps);
        Scene3D scene = rig.Scene;
        scene.ForceTemporalForTests = true;
        rig.Frame().Dispose();
        scene.Camera.Target = new Vector3(.5f, 0f, 0f);   // a moving camera, so the two matrices differ

        using RecordingGpuCommandList cl = rig.Frame();
        ModelMotionResources motion = scene.MotionResourcesForTests!;
        RecordingGpuCommandList.Upload upload = cl.Uploads.Last(u => ReferenceEquals(u.Buffer, motion.FrameBuffer));
        MotionFrameUbo block = MemoryMarshal.Read<MotionFrameUbo>(upload.Data);

        // The variants turn the clip-space difference into UV motion in the authored clip convention, so a corrected
        // pair would flip the motion's Y on this backend.
        Matrix4x4 current = scene.CurrentFrameView.ViewProjection, previous = scene.PreviousFrameView!.Value.ViewProjection;
        Assert.NotEqual(current, previous);
        Assert.NotEqual(current, GpuClip.Correct(current, caps));
        Assert.Equal(current, block.CurViewProj);
        Assert.Equal(previous, block.PrevViewProj);
    }
}
