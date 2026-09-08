using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class ShadowReconfigureRequestTests
{
    [Fact]
    public void Latest_request_wins_at_the_next_frame_boundary()
    {
        using RequestRig rig = new();
        int replacements = rig.ReplacementCount;

        rig.Scene.RequestShadowMapDetail(ShadowMapDetail.Low);
        rig.Scene.RequestShadowMapDetail(ShadowMapDetail.High);

        Assert.Equal(2048, rig.Settings.ShadowMapResolution);
        rig.BeginFrame();
        Assert.Equal(3072, rig.Settings.ShadowMapResolution);
        Assert.Equal(3, rig.Settings.ShadowCascadeCount);
        Assert.Equal(replacements + 1, rig.ReplacementCount);
    }

    [Fact]
    public void Requested_layout_stays_uncommitted_until_the_next_frame_begins()
    {
        using RequestRig rig = new();

        rig.Scene.RequestShadowMapLayout(256, 1);

        Assert.Equal(2048, rig.Settings.ShadowMapResolution);
        Assert.Equal(3, rig.Settings.ShadowCascadeCount);
        rig.BeginFrame();
        Assert.Equal(256, rig.Settings.ShadowMapResolution);
        Assert.Equal(1, rig.Settings.ShadowCascadeCount);
    }

    [Fact]
    public void Same_layout_request_allocates_and_disposes_nothing()
    {
        using RequestRig rig = new();
        int textures = rig.TextureCreates;
        int sets = rig.ResourceSetCreates;
        int pipelines = rig.PipelineCreates;
        int disposedTextures = rig.DisposedTextureCount;
        int disposedSets = rig.DisposedResourceSetCount;
        int replacements = rig.ReplacementCount;

        rig.Scene.RequestShadowMapLayout(2048, 3);
        rig.BeginFrame();

        Assert.Equal(textures, rig.TextureCreates);
        Assert.Equal(sets, rig.ResourceSetCreates);
        Assert.Equal(pipelines, rig.PipelineCreates);
        Assert.Equal(disposedTextures, rig.DisposedTextureCount);
        Assert.Equal(disposedSets, rig.DisposedResourceSetCount);
        Assert.Equal(replacements, rig.ReplacementCount);
    }

    [Fact]
    public void Failed_request_keeps_the_committed_layout_and_is_not_retried()
    {
        using RequestRig rig = new();
        rig.FailNextResourceSetCreate();
        rig.Scene.RequestShadowMapDetail(ShadowMapDetail.High);

        rig.BeginFrame();

        Assert.Equal(2048, rig.Settings.ShadowMapResolution);
        Assert.Equal(3, rig.Settings.ShadowCascadeCount);
        string error = Assert.Single(rig.Logger.Errors);
        Assert.Contains("3072", error, StringComparison.Ordinal);
        Assert.Contains("2048", error, StringComparison.Ordinal);
        int textures = rig.TextureCreates;
        int sets = rig.ResourceSetCreates;
        int replacements = rig.ReplacementCount;

        rig.BeginFrame();

        Assert.Equal(2048, rig.Settings.ShadowMapResolution);
        Assert.Equal(3, rig.Settings.ShadowCascadeCount);
        Assert.Equal(textures, rig.TextureCreates);
        Assert.Equal(sets, rig.ResourceSetCreates);
        Assert.Equal(replacements, rig.ReplacementCount);
        Assert.Single(rig.Logger.Errors);

        rig.Scene.RequestShadowMapLayout(2048, 3);
        rig.BeginFrame();
        Assert.Single(rig.Logger.Errors);
    }

    [Fact]
    public void Disposal_drops_a_pending_request_and_refuses_new_requests()
    {
        using RequestRig rig = new();
        rig.Scene.RequestShadowMapDetail(ShadowMapDetail.High);

        rig.DisposeScene();

        Assert.Equal(2048, rig.Settings.ShadowMapResolution);
        Assert.Throws<ObjectDisposedException>(() => rig.Scene.RequestShadowMapDetail(ShadowMapDetail.Low));
        Assert.Throws<ObjectDisposedException>(() => rig.Scene.RequestShadowMapLayout(1024, 1));
    }

    [Theory]
    [InlineData(255, 3)]
    [InlineData(-1, 3)]
    [InlineData(2048, 0)]
    [InlineData(2048, 5)]
    public void Invalid_raw_layout_does_not_replace_an_already_queued_request(int resolution, int cascades)
    {
        using RequestRig rig = new();
        rig.Scene.RequestShadowMapDetail(ShadowMapDetail.High);

        Assert.Throws<ArgumentOutOfRangeException>(() => rig.Scene.RequestShadowMapLayout(resolution, cascades));

        rig.BeginFrame();
        Assert.Equal(3072, rig.Settings.ShadowMapResolution);
        Assert.Equal(3, rig.Settings.ShadowCascadeCount);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(99)]
    public void Unsupported_detail_does_not_replace_an_already_queued_request(int detail)
    {
        using RequestRig rig = new();
        rig.Scene.RequestShadowMapDetail(ShadowMapDetail.Low);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            rig.Scene.RequestShadowMapDetail((ShadowMapDetail)detail));

        rig.BeginFrame();
        Assert.Equal(1024, rig.Settings.ShadowMapResolution);
        Assert.Equal(3, rig.Settings.ShadowCascadeCount);
    }

    sealed class RequestRig : IDisposable
    {
        readonly CountingGpuDevice _device = new();
        readonly IGpuTexture _targetTexture;
        readonly IGpuFramebuffer _target;
        bool _sceneDisposed;

        internal RequestRig()
        {
            _targetTexture = _device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget));
            _target = _device.Factory.CreateFramebuffer(null, _targetTexture);
            Settings = new ShadowSettings
            {
                Mode = ShadowMode.ShadowMap,
                ShadowMapResolution = 2048,
                ShadowCascadeCount = 3,
            };
            Logger = new RecordingLogger();
            Scene = new Scene3D(_device, _target.Outputs, Settings, Logger);
        }

        internal Scene3D Scene { get; }
        internal ShadowSettings Settings { get; }
        internal RecordingLogger Logger { get; }
        internal int ReplacementCount => _device.WaitForIdleCalls;
        internal int TextureCreates => _device.CountingFactory.TextureCreates;
        internal int ResourceSetCreates => _device.CountingFactory.ResourceSetCreates;
        internal int PipelineCreates => _device.CountingFactory.PipelineCreates;
        internal int DisposedTextureCount => _device.CountingFactory.DisposedTextureCount;
        internal int DisposedResourceSetCount => _device.CountingFactory.DisposedResourceSetCount;

        internal void BeginFrame() => Scene.Begin();

        internal void FailNextResourceSetCreate() => _device.CountingFactory.FailNextResourceSetCreate = true;

        internal void DisposeScene()
        {
            if (_sceneDisposed) return;
            Scene.Dispose();
            _sceneDisposed = true;
        }

        public void Dispose()
        {
            DisposeScene();
            _target.Dispose();
            _targetTexture.Dispose();
            _device.Dispose();
        }
    }

    sealed class CountingGpuDevice : IGpuDevice
    {
        readonly FakeGpuDevice _inner = new();

        internal CountingGpuDevice() => CountingFactory = new CountingGpuFactory(_inner.Factory);
        internal CountingGpuFactory CountingFactory { get; }
        internal int WaitForIdleCalls { get; private set; }
        public GpuBackendKind Backend => _inner.Backend;
        public GpuCapabilities Capabilities => _inner.Capabilities;
        public IGpuResourceFactory Factory => CountingFactory;
        public IGpuFramebuffer? SwapchainFramebuffer => _inner.SwapchainFramebuffer;
        public IGpuSampler PointSampler => _inner.PointSampler;
        public IGpuSampler LinearSampler => _inner.LinearSampler;
        public bool SyncToVerticalBlank { get => _inner.SyncToVerticalBlank; set => _inner.SyncToVerticalBlank = value; }
        public void Submit(IGpuCommandList cl) => _inner.Submit(cl);
        public void Submit(IGpuCommandList cl, IGpuFence fence) => _inner.Submit(cl, fence);
        public void WaitForIdle() { WaitForIdleCalls++; _inner.WaitForIdle(); }
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged => _inner.UpdateBuffer(b, offsetBytes, data);
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, T[] data) where T : unmanaged => _inner.UpdateBuffer(b, offsetBytes, data);
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged => _inner.UpdateBuffer(b, offsetBytes, in data);
        public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height) => _inner.UpdateTexture(texture, data, x, y, width, height);
        public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height, uint mipLevel, uint arrayLayer) => _inner.UpdateTexture(texture, data, x, y, width, height, mipLevel, arrayLayer);
        public MappedData Map(IGpuTexture staging, GpuMapMode mode) => _inner.Map(staging, mode);
        public void Unmap(IGpuTexture staging) => _inner.Unmap(staging);
        public MappedData Map(IGpuBuffer staging, GpuMapMode mode) => _inner.Map(staging, mode);
        public void Unmap(IGpuBuffer staging) => _inner.Unmap(staging);
        public void ResizeSwapchain(uint w, uint h) => _inner.ResizeSwapchain(w, h);
        public void Present() => _inner.Present();
        public void Dispose() => _inner.Dispose();
    }

    sealed class CountingGpuFactory : IGpuResourceFactory
    {
        readonly IGpuResourceFactory _inner;
        readonly FakeGpuResourceFactory _recording;

        internal CountingGpuFactory(IGpuResourceFactory inner)
        {
            _inner = inner;
            _recording = (FakeGpuResourceFactory)inner;
        }

        internal bool FailNextResourceSetCreate { get; set; }
        internal int TextureCreates => _recording.Textures.Count;
        internal int ResourceSetCreates => _recording.ResourceSets.Count;
        internal int PipelineCreates => _recording.GraphicsPipelines.Count;
        internal int DisposedTextureCount => _recording.Textures.FindAll(texture => texture.Disposed).Count;
        internal int DisposedResourceSetCount => _recording.DisposedResourceSetCount;
        public IGpuBuffer CreateBuffer(in GpuBufferDescription d) => _inner.CreateBuffer(in d);
        public IGpuTexture CreateTexture(in GpuTextureDescription d) => _inner.CreateTexture(in d);
        public IGpuFramebuffer CreateFramebuffer(IGpuTexture? depth, params IGpuTexture[] colour) => _inner.CreateFramebuffer(depth, colour);
        public IGpuSampler CreateSampler(in GpuSamplerDescription d) => _inner.CreateSampler(in d);
        public IGpuResourceLayout CreateResourceLayout(in GpuResourceLayoutDescription d) => _inner.CreateResourceLayout(in d);

        public IGpuResourceSet CreateResourceSet(in GpuResourceSetDescription d)
        {
            if (FailNextResourceSetCreate)
            {
                FailNextResourceSetCreate = false;
                throw new InvalidOperationException("injected resource-set allocation failure");
            }

            return _inner.CreateResourceSet(in d);
        }

        public IGpuShaderSet CreateShadersFromSpirv(string vertGlsl, string fragGlsl) => _inner.CreateShadersFromSpirv(vertGlsl, fragGlsl);
        public IGpuPipeline CreateGraphicsPipeline(in GpuPipelineDescription d) => _inner.CreateGraphicsPipeline(in d);
        public IGpuCommandList CreateCommandList() => _inner.CreateCommandList();
        public IGpuComputeShader CreateComputeShaderFromSpirv(string computeGlsl) => _inner.CreateComputeShaderFromSpirv(computeGlsl);
        public IGpuComputePipeline CreateComputePipeline(in GpuComputePipelineDescription d) => _inner.CreateComputePipeline(in d);
        public IGpuFence CreateFence() => _inner.CreateFence();
    }
}
