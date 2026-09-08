using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Native-device acceptance for rebuilding every live shadow receiver binding.</summary>
public sealed class ShadowReconfigureGpuTests : IClassFixture<ShadowReconfigureGpuScene>
{
    readonly ShadowReconfigureGpuScene _fixture;
    readonly ITestOutputHelper _output;

    public ShadowReconfigureGpuTests(ShadowReconfigureGpuScene fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [GpuFact]
    public void Low_default_high_low_rebuilds_the_live_atlas_and_keeps_every_receiver_drawable()
    {
        foreach ((ShadowMapDetail detail, int resolution) in new[]
        {
            (ShadowMapDetail.Low, 1024),
            (ShadowMapDetail.Default, 2048),
            (ShadowMapDetail.High, 3072),
            (ShadowMapDetail.Low, 1024),
        })
        {
            ShadowReconfigureFrame frame = _fixture.Capture(detail);

            _output.WriteLine(
                $"backend={frame.Backend} device='{frame.DeviceName}' detail={detail} " +
                $"resolution={frame.Resolution} cascades={frame.CascadeCount} atlasWidth={frame.AtlasWidth} " +
                $"draws={frame.Diagnostics.TotalDrawCalls} pixels=" +
                $"{frame.RigidPixels}/{frame.SkinnedPixels}/{frame.SplatPixels}/{frame.TileGroundPixels} " +
                $"shadowed={frame.ShadowedPixels} deviceLoss={frame.DeviceLossReason ?? "none"}");

            Assert.Equal(resolution, frame.Resolution);
            Assert.Equal(resolution * frame.CascadeCount, frame.AtlasWidth);
            Assert.Equal(frame.CascadeCount, frame.Diagnostics.CascadeCount);
            Assert.True(frame.Diagnostics.Rendered, $"{detail} did not record a fresh shadow pass");
            Assert.False(frame.Diagnostics.HadPrevious, $"{detail} retained the replaced atlas as prior state");
            Assert.True(frame.Diagnostics.TotalDrawCalls > 0, $"{detail} recorded no shadow depth draws");
            Assert.True(frame.RigidPixels > 0, $"{detail} lost the rigid receiver");
            Assert.True(frame.SkinnedPixels > 0, $"{detail} lost the skinned receiver");
            Assert.True(frame.SplatPixels > 0, $"{detail} lost the splat receiver");
            Assert.True(frame.TileGroundPixels > 0, $"{detail} lost the tile-ground receiver");
            Assert.True(frame.ShadowedPixels > 0, $"{detail} left no visible shadow coverage");
            Assert.Null(frame.DeviceLossReason);
        }
    }
}

internal readonly record struct ShadowReconfigureFrame(
    GpuBackendKind Backend,
    string DeviceName,
    int Resolution,
    int CascadeCount,
    int AtlasWidth,
    ShadowPassDiagnostics Diagnostics,
    int RigidPixels,
    int SkinnedPixels,
    int SplatPixels,
    int TileGroundPixels,
    int ShadowedPixels,
    string? DeviceLossReason);

/// <summary>
/// One lazily-created native scene for the whole transition sequence. The tracking device forwards every call to
/// the real backend and records only the texture descriptions needed to prove the allocated atlas dimensions.
/// </summary>
public sealed class ShadowReconfigureGpuScene : IDisposable
{
    const int Width = 320;
    const int Height = 240;
    const float ShadowStrength = 0.9f;

    GpuDeviceContext? _context;
    ShadowAtlasTrackingGpuDevice? _device;
    IGpuTexture? _target;
    IGpuFramebuffer? _framebuffer;
    IGpuCommandList? _commands;
    Scene3D? _scene;
    MeshHandle _rigid;
    MeshHandle _splat;
    MeshHandle _tileGround;
    SkinnedLimb? _limb;

    internal ShadowReconfigureFrame Capture(ShadowMapDetail detail)
    {
        EnsureScene();
        Scene3D scene = _scene!;
        ShadowAtlasTrackingGpuDevice device = _device!;

        scene.Post.Quality.Shadows.ShadowStrength = ShadowStrength;
        scene.RequestShadowMapDetail(detail);
        byte[] shadowed = RenderFrame();
        ShadowPassDiagnostics diagnostics = scene.LastShadowPassDiagnostics;
        int resolution = scene.Post.Quality.Shadows.ShadowMapResolution;
        int cascades = scene.Post.Quality.Shadows.ShadowCascadeCount;
        int atlasWidth = device.LatestShadowAtlasWidth;

        scene.Post.Quality.Shadows.ShadowStrength = 0f;
        byte[] unshadowed = RenderFrame();
        scene.Post.Quality.Shadows.ShadowStrength = ShadowStrength;

        return new ShadowReconfigureFrame(
            device.Backend,
            device.Capabilities.DeviceName,
            resolution,
            cascades,
            atlasWidth,
            diagnostics,
            CountPixels(shadowed, PixelKind.Red),
            CountPixels(shadowed, PixelKind.Magenta),
            CountPixels(shadowed, PixelKind.Green),
            CountPixels(shadowed, PixelKind.Yellow),
            CountShadowedPixels(shadowed, unshadowed),
            device.Diagnostics.DeviceLossReason);
    }

    byte[] RenderFrame()
    {
        Scene3D scene = _scene!;
        scene.Begin();
        scene.Draw(_splat, Matrix4x4.Identity);
        scene.Draw(_tileGround, Matrix4x4.Identity);
        scene.Draw(_rigid, Matrix4x4.CreateTranslation(-2.3f, 0.7f, 0.2f),
            new Color(0.95f, 0.08f, 0.05f, 1f));
        _limb!.Update(Vector3.Zero, Vector3.UnitY, Vector3.UnitZ, 0.4f);
        _limb.Draw(scene, Matrix4x4.CreateTranslation(2.3f, 0.15f, 0.2f),
            new Color(0.85f, 0.05f, 0.8f, 1f));
        scene.PrepareFrame();
        using (GpuRecording.Open(_device!, _commands!, "ShadowReconfigureGpuScene.RenderFrame"))
            scene.RenderInternal(_commands!, Width, Height, _framebuffer!);
        _device!.Submit(_commands!);
        _device.WaitForIdle();
        return GpuReadback.ToRgba(_device, _target!, Width, Height);
    }

    void EnsureScene()
    {
        if (_scene is not null) return;

        _context = GpuDeviceContext.CreateHeadless();
        _device = new ShadowAtlasTrackingGpuDevice(_context.GpuDevice);
        IGpuResourceFactory factory = _device.Factory;
        _target = factory.CreateTexture(GpuTextureDescription.Texture2D(
            Width, Height, GpuPixelFormat.R8G8B8A8UNorm,
            GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        _framebuffer = factory.CreateFramebuffer(null, _target);
        _commands = factory.CreateCommandList();
        var shadows = new ShadowSettings { Mode = ShadowMode.ShadowMap };
        _scene = new Scene3D(_device, _framebuffer.Outputs, shadows);
        ConfigureScene(_scene);

        _rigid = _scene.LoadMesh(MeshPrimitives.Box(1.4f));
        _limb = new SkinnedLimb(_scene, radius: 0.35f, length: 2.8f, ringSegments: 8, radialSegments: 8,
            boneCount: 5, ChainConfig.Writhe, Axis.Y);
        Scene3D.SplatMaterialHandle splatMaterial = _scene.LoadSplatMaterial(2, 2, SplatLayers());
        _splat = _scene.LoadMesh(GroundQuad(-5f, 0f, splat: true), splatMaterial);
        Scene3D.TileGroundMaterialHandle tileMaterial = _scene.LoadTileGroundMaterial(2, 2, TileGroundLayers());
        _tileGround = _scene.LoadMesh(GroundQuad(0f, 5f, splat: false), tileMaterial);
    }

    static void ConfigureScene(Scene3D scene)
    {
        scene.Post.Starfield = false;
        scene.Post.Outline = false;
        scene.Post.BackgroundColor = new Color(0.015f, 0.02f, 0.025f, 1f);
        scene.Post.AmbientColor = new Color(0.25f, 0.25f, 0.25f, 1f);
        scene.Post.FillLightColor = new Color(0.08f, 0.08f, 0.08f, 1f);
        scene.Post.LightDirection = Vector3.Normalize(new Vector3(-0.55f, -0.8f, -0.35f));
        scene.Post.Quality.Shadows.ShadowNearDistance = 12f;
        scene.Post.Quality.Shadows.ShadowMaxDistance = 40f;
        scene.Post.Quality.Shadows.ShadowStrength = ShadowStrength;
        scene.Camera.Frame(new Vector3(-5f, 0f, -4f), new Vector3(5f, 3.5f, 4f));
        scene.UseGpuSkinning = true;
    }

    static GltfMesh GroundQuad(float minX, float maxX, bool splat)
    {
        const float minZ = -4f;
        const float maxZ = 4f;
        Vector4 color = Vector4.UnitX;
        Vector2 uv = Vector2.Zero;
        Vector4 tangent = splat ? Vector4.Zero : new Vector4(0f, 0f, 1f, 0f);
        var vertices = new[]
        {
            new ModelVertex(new Vector3(minX, 0f, minZ), Vector3.UnitY, color, uv, tangent),
            new ModelVertex(new Vector3(maxX, 0f, minZ), Vector3.UnitY, color, uv, tangent),
            new ModelVertex(new Vector3(maxX, 0f, maxZ), Vector3.UnitY, color, uv, tangent),
            new ModelVertex(new Vector3(minX, 0f, maxZ), Vector3.UnitY, color, uv, tangent),
        };
        return new GltfMesh(vertices, new ushort[] { 0, 1, 2, 0, 2, 3 });
    }

    static IReadOnlyList<SplatLayerImage> SplatLayers()
    {
        var layers = new List<SplatLayerImage>();
        for (int i = 0; i < SplatMaterialConfig.LayerCount; i++)
        {
            layers.Add(new SplatLayerImage
            {
                AlbedoRgba = SolidPixels(25, 210, 45),
                NormalRgba = FlatNormalPixels(),
                TilesPerMetre = 0.25f,
                Roughness = 1f,
            });
        }
        return layers;
    }

    static IReadOnlyList<TileGroundLayerImage> TileGroundLayers() =>
        new[] { new TileGroundLayerImage { AlbedoRgba = SolidPixels(230, 190, 25), TilesPerMetre = 0.25f } };

    static byte[] SolidPixels(byte r, byte g, byte b)
    {
        var pixels = new byte[16];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = 255;
        }
        return pixels;
    }

    static byte[] FlatNormalPixels()
    {
        var pixels = new byte[16];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 128;
            pixels[i + 1] = 128;
            pixels[i + 2] = 255;
            pixels[i + 3] = 255;
        }
        return pixels;
    }

    static int CountPixels(byte[] pixels, PixelKind kind)
    {
        int count = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int r = pixels[i];
            int g = pixels[i + 1];
            int b = pixels[i + 2];
            bool matches = kind switch
            {
                PixelKind.Red => r > 30 && r > g * 3 / 2 && r > b * 3 / 2,
                PixelKind.Magenta => r > 25 && b > 25 && r > g * 3 / 2 && b > g * 3 / 2,
                PixelKind.Green => g > 30 && g > r * 3 / 2 && g > b * 3 / 2,
                PixelKind.Yellow => r > 30 && g > 30 && r > b * 3 / 2 && g > b * 3 / 2,
                _ => false,
            };
            if (matches) count++;
        }
        return count;
    }

    static int CountShadowedPixels(byte[] shadowed, byte[] unshadowed)
    {
        int count = 0;
        for (int i = 0; i < shadowed.Length; i += 4)
        {
            int shadowLuma = shadowed[i] + shadowed[i + 1] + shadowed[i + 2];
            int litLuma = unshadowed[i] + unshadowed[i + 1] + unshadowed[i + 2];
            if (litLuma - shadowLuma >= 18) count++;
        }
        return count;
    }

    public void Dispose()
    {
        _limb?.Dispose();
        _commands?.Dispose();
        _scene?.Dispose();
        _framebuffer?.Dispose();
        _target?.Dispose();
        _context?.Dispose();
    }

    enum PixelKind
    {
        Red,
        Magenta,
        Green,
        Yellow,
    }
}

/// <summary>
/// Non-owning native-device decorator that remembers the latest allocated shadow atlas width while preserving the
/// backend's real resource identities.
/// </summary>
internal sealed class ShadowAtlasTrackingGpuDevice : IGpuDevice
{
    readonly IGpuDevice _inner;
    readonly TrackingFactory _factory;

    internal ShadowAtlasTrackingGpuDevice(IGpuDevice inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _factory = new TrackingFactory(inner.Factory);
    }

    internal int LatestShadowAtlasWidth => checked((int)_factory.LatestShadowAtlasWidth);
    public GpuBackendKind Backend => _inner.Backend;
    public GpuCapabilities Capabilities => _inner.Capabilities;
    public IGpuResourceFactory Factory => _factory;
    public IGpuFramebuffer? SwapchainFramebuffer => _inner.SwapchainFramebuffer;
    public IGpuSampler PointSampler => _inner.PointSampler;
    public IGpuSampler LinearSampler => _inner.LinearSampler;
    public GpuDeviceDiagnostics Diagnostics => _inner.Diagnostics;
    public GpuDeviceCounters Counters => _inner.Counters;
    public bool SyncToVerticalBlank { get => _inner.SyncToVerticalBlank; set => _inner.SyncToVerticalBlank = value; }
    public void Submit(IGpuCommandList cl) => _inner.Submit(cl);
    public void Submit(IGpuCommandList cl, IGpuFence fence) => _inner.Submit(cl, fence);
    public void WaitForIdle() => _inner.WaitForIdle();
    public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged =>
        _inner.UpdateBuffer(b, offsetBytes, data);
    public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, T[] data) where T : unmanaged =>
        _inner.UpdateBuffer(b, offsetBytes, data);
    public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged =>
        _inner.UpdateBuffer(b, offsetBytes, in data);
    public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height) =>
        _inner.UpdateTexture(texture, data, x, y, width, height);
    public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height,
        uint mipLevel, uint arrayLayer) =>
        _inner.UpdateTexture(texture, data, x, y, width, height, mipLevel, arrayLayer);
    public MappedData Map(IGpuTexture staging, GpuMapMode mode) => _inner.Map(staging, mode);
    public void Unmap(IGpuTexture staging) => _inner.Unmap(staging);
    public MappedData Map(IGpuBuffer staging, GpuMapMode mode) => _inner.Map(staging, mode);
    public void Unmap(IGpuBuffer staging) => _inner.Unmap(staging);
    public void ResizeSwapchain(uint w, uint h) => _inner.ResizeSwapchain(w, h);
    public void Present() => _inner.Present();
    public void Dispose() { }

    sealed class TrackingFactory : IGpuResourceFactory
    {
        readonly IGpuResourceFactory _inner;

        internal TrackingFactory(IGpuResourceFactory inner) => _inner = inner;
        internal uint LatestShadowAtlasWidth { get; private set; }

        public IGpuBuffer CreateBuffer(in GpuBufferDescription d) => _inner.CreateBuffer(in d);

        public IGpuTexture CreateTexture(in GpuTextureDescription d)
        {
            IGpuTexture texture = _inner.CreateTexture(in d);
            if (d.Format == GpuPixelFormat.R32Float
                && d.Usage.HasFlag(GpuTextureUsage.RenderTarget)
                && d.Usage.HasFlag(GpuTextureUsage.Sampled)
                && d.Height >= ShadowSettings.MinShadowMapResolution)
            {
                LatestShadowAtlasWidth = d.Width;
            }
            return texture;
        }

        public IGpuFramebuffer CreateFramebuffer(IGpuTexture? depth, params IGpuTexture[] colour) =>
            _inner.CreateFramebuffer(depth, colour);
        public IGpuSampler CreateSampler(in GpuSamplerDescription d) => _inner.CreateSampler(in d);
        public IGpuResourceLayout CreateResourceLayout(in GpuResourceLayoutDescription d) =>
            _inner.CreateResourceLayout(in d);
        public IGpuResourceSet CreateResourceSet(in GpuResourceSetDescription d) => _inner.CreateResourceSet(in d);
        public IGpuShaderSet CreateShadersFromSpirv(string vertGlsl, string fragGlsl) =>
            _inner.CreateShadersFromSpirv(vertGlsl, fragGlsl);
        public IGpuComputeShader CreateComputeShaderFromSpirv(string computeGlsl) =>
            _inner.CreateComputeShaderFromSpirv(computeGlsl);
        public IGpuPipeline CreateGraphicsPipeline(in GpuPipelineDescription d) =>
            _inner.CreateGraphicsPipeline(in d);
        public IGpuComputePipeline CreateComputePipeline(in GpuComputePipelineDescription d) =>
            _inner.CreateComputePipeline(in d);
        public IGpuCommandList CreateCommandList() => _inner.CreateCommandList();
        public IGpuFence CreateFence() => _inner.CreateFence();
    }
}
