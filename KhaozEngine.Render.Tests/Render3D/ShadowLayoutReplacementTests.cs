using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// Device-free coverage of the shadow-layout transaction. The recording factory keeps the real binding
/// descriptions handed to <see cref="IGpuResourceFactory.CreateResourceSet"/>, so these tests inspect the atlas
/// actually bound by every receiver rather than a parallel test-only model of the cache.
/// </summary>
public sealed class ShadowLayoutReplacementTests
{
    [Fact]
    public void Replacement_rebinds_every_shadow_sampling_set_and_commits_last()
    {
        using ReplacementRig rig = ReplacementRig.WithEveryReceiver();
        rig.RenderFrame();
        rig.RenderFrame();
        Assert.True(rig.LastShadowPass.Skipped);
        IGpuTexture oldAtlas = rig.AtlasHandle;
        IReadOnlyList<RecordedSet> oldSets = rig.SamplingSets;
        Assert.Equal(7, oldSets.Count);
        int waits = rig.Device.WaitForIdleCalls;

        bool replaced = rig.Replace(resolution: 3072, cascades: 4);

        Assert.True(replaced);
        Assert.Equal(waits + 1, rig.Device.WaitForIdleCalls);
        Assert.NotSame(oldAtlas, rig.AtlasHandle);
        Assert.True(((FakeTexture)oldAtlas).Disposed);
        Assert.Equal(3072, rig.Settings.ShadowMapResolution);
        Assert.Equal(4, rig.Settings.ShadowCascadeCount);
        Assert.All(oldSets, set => Assert.True(set.Disposed));
        Assert.Equal(7, rig.SamplingSets.Count);
        Assert.All(rig.SamplingSets, set => Assert.Same(rig.AtlasHandle, set.ShadowTexture));
        rig.RenderFrame();
        Assert.True(rig.LastShadowPass.Rendered);
        Assert.False(rig.LastShadowPass.HadPrevious);
    }

    [Fact]
    public void Failed_sampling_set_allocation_retains_the_old_graph_and_committed_settings()
    {
        using ReplacementRig rig = ReplacementRig.WithEveryReceiver();
        rig.RenderFrame();
        Assert.True(rig.LastShadowPass.Rendered);
        rig.RenderFrame();
        Assert.True(rig.LastShadowPass.Skipped);
        IGpuTexture oldAtlas = rig.AtlasHandle;
        IReadOnlyList<RecordedSet> oldSets = rig.SamplingSets;
        int texturesBefore = rig.Factory.TextureCreates;
        int setsBefore = rig.Factory.ResourceSetCreates;
        int waits = rig.Device.WaitForIdleCalls;
        rig.Factory.FailResourceSetCreateAfter(successfulCreates: 3);

        bool replaced = rig.Replace(resolution: 3072, cascades: 4);

        Assert.False(replaced);
        Assert.Equal(waits + 1, rig.Device.WaitForIdleCalls);
        Assert.Same(oldAtlas, rig.AtlasHandle);
        Assert.False(((FakeTexture)oldAtlas).Disposed);
        Assert.Equal(2048, rig.Settings.ShadowMapResolution);
        Assert.Equal(3, rig.Settings.ShadowCascadeCount);
        Assert.All(oldSets, set => Assert.False(set.Disposed));
        Assert.Equal(oldSets, rig.SamplingSets);
        Assert.Equal(2, rig.Factory.Textures.Skip(texturesBefore).Count());
        Assert.All(rig.Factory.Textures.Skip(texturesBefore), texture => Assert.True(texture.Disposed));
        Assert.Equal(3, rig.Factory.ResourceSetCreates - setsBefore);
        Assert.All(rig.Factory.Sets.Skip(setsBefore), set => Assert.True(set.Disposed));
        rig.RenderFrame();
        Assert.True(rig.LastShadowPass.Skipped);
        Assert.True(rig.LastShadowPass.HadPrevious);
    }

    [Fact]
    public void Unchanged_layout_creates_and_disposes_nothing()
    {
        using ReplacementRig rig = ReplacementRig.WithEveryReceiver();
        int textureCreates = rig.Factory.TextureCreates;
        int setCreates = rig.Factory.ResourceSetCreates;
        int disposedTextures = rig.Factory.DisposedTextureCount;
        int disposedSets = rig.Factory.DisposedResourceSetCount;
        int waits = rig.Device.WaitForIdleCalls;

        bool replaced = rig.Replace(resolution: 2048, cascades: 3);

        Assert.False(replaced);
        Assert.Equal(textureCreates, rig.Factory.TextureCreates);
        Assert.Equal(setCreates, rig.Factory.ResourceSetCreates);
        Assert.Equal(disposedTextures, rig.Factory.DisposedTextureCount);
        Assert.Equal(disposedSets, rig.Factory.DisposedResourceSetCount);
        Assert.Equal(waits, rig.Device.WaitForIdleCalls);
    }

    sealed class ReplacementRig : IDisposable
    {
        static readonly byte[] Pixel = { 255, 255, 255, 255 };
        readonly IGpuTexture _targetTexture;
        readonly IGpuFramebuffer _target;
        readonly Scene3D _scene;

        ReplacementRig()
        {
            Device = new RecordingGpuDevice();
            Factory = Device.RecordingFactory;
            _targetTexture = Factory.CreateTexture(GpuTextureDescription.Texture2D(
                16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            _target = Factory.CreateFramebuffer(null, _targetTexture);
            Settings = new ShadowSettings
            {
                Mode = ShadowMode.ShadowMap,
                ShadowMapResolution = 2048,
                ShadowCascadeCount = 3,
            };
            _scene = new Scene3D(Device, _target.Outputs, Settings);
        }

        internal RecordingGpuDevice Device { get; }
        internal RecordingGpuResourceFactory Factory { get; }
        internal ShadowSettings Settings { get; }
        internal ShadowPassDiagnostics LastShadowPass => _scene.LastShadowPassDiagnostics;
        internal IGpuTexture AtlasHandle => Factory.ActiveShadowAtlas(Settings.ShadowMapResolution, Settings.ShadowCascadeCount);
        internal IReadOnlyList<RecordedSet> SamplingSets => Factory.ActiveSamplingSets(AtlasHandle);

        internal static ReplacementRig WithEveryReceiver()
        {
            var rig = new ReplacementRig();
            Scene3D.TextureHandle texture = rig._scene.LoadTexture(Pixel, 1, 1);
            rig._scene.LoadMesh(Triangle(), texture);
            rig._scene.LoadSkinnedMesh(SkinnedMeshBuilder.BuildTube(0.5f, 2f, 4, 4, 2, Axis.Z), texture);
            rig._scene.LoadSplatMaterial(1, 1, SplatLayers());
            rig._scene.LoadTileGroundMaterial(1, 1, GroundLayers());
            return rig;
        }

        internal bool Replace(int resolution, int cascades) => _scene.ReplaceShadowLayout(resolution, cascades);

        internal void RenderFrame()
        {
            _scene.Begin();
            _scene.PrepareFrame();
            using IGpuCommandList commands = Factory.CreateCommandList();
            commands.Begin();
            _scene.RenderInternal(commands, 16, 16, _target);
            commands.End();
        }

        public void Dispose()
        {
            _scene.Dispose();
            _target.Dispose();
            _targetTexture.Dispose();
            Device.Dispose();
        }

        static GltfMesh Triangle()
        {
            var vertices = new[]
            {
                new ModelVertex(Vector3.Zero, Vector3.UnitZ, Vector4.One),
                new ModelVertex(Vector3.UnitX, Vector3.UnitZ, Vector4.One),
                new ModelVertex(Vector3.UnitY, Vector3.UnitZ, Vector4.One),
            };
            return new GltfMesh(vertices, new uint[] { 0, 1, 2 });
        }

        static List<SplatLayerImage> SplatLayers()
        {
            var layers = new List<SplatLayerImage>();
            for (int i = 0; i < SplatMaterialConfig.LayerCount; i++)
                layers.Add(new SplatLayerImage { AlbedoRgba = Pixel, NormalRgba = Pixel });
            return layers;
        }

        static List<TileGroundLayerImage> GroundLayers() =>
            new() { new TileGroundLayerImage { AlbedoRgba = Pixel } };
    }

    sealed class RecordingGpuDevice : IGpuDevice
    {
        readonly FakeGpuDevice _inner = new();

        internal RecordingGpuDevice() => RecordingFactory = new RecordingGpuResourceFactory(_inner.Factory);
        internal RecordingGpuResourceFactory RecordingFactory { get; }
        internal int WaitForIdleCalls { get; private set; }
        public GpuBackendKind Backend => _inner.Backend;
        public GpuCapabilities Capabilities => _inner.Capabilities;
        public IGpuResourceFactory Factory => RecordingFactory;
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

    sealed class RecordingGpuResourceFactory : IGpuResourceFactory
    {
        readonly IGpuResourceFactory _inner;
        readonly List<RecordedSet> _sets = new();
        int _resourceSetCreateAttempts;
        int _failResourceSetCreateAttempt = -1;

        internal RecordingGpuResourceFactory(IGpuResourceFactory inner) => _inner = inner;
        internal List<FakeTexture> Textures => ((FakeGpuResourceFactory)_inner).Textures;
        internal IReadOnlyList<RecordedSet> Sets => _sets;
        internal int TextureCreates => Textures.Count;
        internal int ResourceSetCreates => _sets.Count;
        internal int DisposedTextureCount => Textures.Count(texture => texture.Disposed);
        internal int DisposedResourceSetCount => _sets.Count(set => set.Disposed);

        internal IGpuTexture ActiveShadowAtlas(int resolution, int cascades) =>
            Assert.Single(Textures, texture => !texture.Disposed
                && texture.Width == (uint)(resolution * cascades)
                && texture.Height == (uint)resolution
                && texture.Format == GpuPixelFormat.R32Float
                && texture.Usage.HasFlag(GpuTextureUsage.RenderTarget)
                && texture.Usage.HasFlag(GpuTextureUsage.Sampled));

        internal IReadOnlyList<RecordedSet> ActiveSamplingSets(IGpuTexture atlas) =>
            _sets.Where(set => !set.Disposed && ReferenceEquals(set.ShadowTexture, atlas)).ToArray();

        internal void FailResourceSetCreateAfter(int successfulCreates) =>
            _failResourceSetCreateAttempt = _resourceSetCreateAttempts + successfulCreates + 1;

        public IGpuBuffer CreateBuffer(in GpuBufferDescription d) => _inner.CreateBuffer(in d);
        public IGpuTexture CreateTexture(in GpuTextureDescription d) => _inner.CreateTexture(in d);
        public IGpuFramebuffer CreateFramebuffer(IGpuTexture? depth, params IGpuTexture[] colour) => _inner.CreateFramebuffer(depth, colour);
        public IGpuSampler CreateSampler(in GpuSamplerDescription d) => _inner.CreateSampler(in d);
        public IGpuResourceLayout CreateResourceLayout(in GpuResourceLayoutDescription d) => _inner.CreateResourceLayout(in d);

        public IGpuResourceSet CreateResourceSet(in GpuResourceSetDescription d)
        {
            _resourceSetCreateAttempts++;
            if (_resourceSetCreateAttempts == _failResourceSetCreateAttempt)
                throw new InvalidOperationException("injected resource-set allocation failure");

            IGpuResourceSet set = _inner.CreateResourceSet(in d);
            _sets.Add(new RecordedSet(set, d.Resources.ToArray()));
            return set;
        }

        public IGpuShaderSet CreateShadersFromSpirv(string vertGlsl, string fragGlsl) => _inner.CreateShadersFromSpirv(vertGlsl, fragGlsl);
        public IGpuPipeline CreateGraphicsPipeline(in GpuPipelineDescription d) => _inner.CreateGraphicsPipeline(in d);
        public IGpuCommandList CreateCommandList() => _inner.CreateCommandList();
        public IGpuComputeShader CreateComputeShaderFromSpirv(string computeGlsl) => _inner.CreateComputeShaderFromSpirv(computeGlsl);
        public IGpuComputePipeline CreateComputePipeline(in GpuComputePipelineDescription d) => _inner.CreateComputePipeline(in d);
        public IGpuFence CreateFence() => _inner.CreateFence();
    }

    sealed class RecordedSet
    {
        readonly IGpuResourceSet _set;
        readonly IGpuBindableResource[] _resources;

        internal RecordedSet(IGpuResourceSet set, IGpuBindableResource[] resources)
        {
            _set = set;
            _resources = resources;
        }

        internal bool Disposed => ((FakeResourceSet)_set).Disposed;
        internal IGpuTexture? ShadowTexture => _resources.OfType<IGpuTexture>().LastOrDefault(texture =>
            texture is FakeTexture fake && fake.Format == GpuPixelFormat.R32Float
                && fake.Usage.HasFlag(GpuTextureUsage.RenderTarget));
    }
}
