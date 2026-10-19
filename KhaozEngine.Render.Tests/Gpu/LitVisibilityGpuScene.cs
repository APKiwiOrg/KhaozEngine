using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>One lazy device and scene for the visibility readbacks, including explicit visibility-only MSAA.</summary>
public sealed class LitVisibilityGpuScene : IDisposable
{
    internal const int W = 160, H = 120;
    GpuDeviceContext? _gpu;
    IGpuDevice? _device;
    readonly int? _maxAttachments;

    public LitVisibilityGpuScene() { }
    internal LitVisibilityGpuScene(int maxAttachments) => _maxAttachments = maxAttachments;
    Scene3D? _scene;
    IGpuTexture? _color;
    IGpuFramebuffer? _target;
    IGpuCommandList? _commands;
    MeshHandle _plane, _splat, _tile, _box, _cutout, _handoffFloor, _handoffBox;
    SkinnedMeshHandle _skin, _skinCutout;
    FoliageBatch? _foliage, _foliageCutout;
    Scene3D.TextureHandle _white;

    internal IGpuDevice Device { get { Ensure(); return _device!; } }
    internal Scene3D Scene { get { Ensure(); return _scene!; } }

    void Ensure()
    {
        if (_scene is not null) return;
        _gpu = GpuDeviceContext.CreateHeadless();
        IGpuDevice device = _device = _maxAttachments is int max
            ? new SpyGpuDevice(_gpu.GpuDevice, maxColorAttachments: max) : _gpu.GpuDevice;
        _color = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(W, H,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        _target = device.Factory.CreateFramebuffer(null, _color);
        _commands = device.Factory.CreateCommandList();
        _scene = new Scene3D(device, _target.Outputs,
            new ShadowSettings { ShadowMapResolution = 512, ShadowCascadeCount = 3 }, requestLitVisibility: true);
        GltfMesh plane = MeshPrimitives.Plane(12f, 12f);
        _plane = _scene.LoadMesh(plane);
        _box = _scene.LoadMesh(MeshPrimitives.Box(1f));
        _handoffFloor = _scene.LoadMesh(MeshPrimitives.Tile(160f, .1f));
        _handoffBox = _scene.LoadMesh(MeshPrimitives.Box(1.6f));
        _white = _scene.LoadTexture([255, 255, 255, 255], 1, 1);
        Scene3D.TextureHandle clear = _scene.LoadTexture([255, 255, 255, 0], 1, 1);
        _cutout = _scene.LoadMesh(plane, new Scene3D.SurfaceMaps(clear, alphaCutoff: .5f));
        var vertices = plane.Vertices.Select(v => new SkinnedVertex
        {
            Position = v.Position,
            Normal = v.Normal,
            Color = v.Color,
            Uv = v.Uv,
            Tangent = v.Tangent,
            BoneWeights = new Vector4(1, 0, 0, 0),
        }).ToArray();
        var skinMesh = new SkinnedGltfMesh(vertices, plane.Indices32, [Matrix4x4.Identity], [Matrix4x4.Identity]);
        _skin = _scene.LoadSkinnedMesh(skinMesh);
        _skinCutout = _scene.LoadSkinnedMesh(skinMesh, new Scene3D.SurfaceMaps(clear, alphaCutoff: .5f));
        var splat = _scene.LoadSplatMaterial(4, 4, GroundLayerImages.FlatSplatLayers(4));
        var tile = _scene.LoadTileGroundMaterial(4, 4, GroundLayerImages.FlatGroundLayers(4));
        var groundVertices = plane.Vertices.Select(v => new ModelVertex(v.Position, v.Normal,
            new Vector4(1, 0, 0, 0), v.Uv, Vector4.Zero)).ToArray();
        _splat = _scene.LoadMesh(new GltfMesh(groundVertices, plane.Indices32), splat);
        _tile = _scene.LoadMesh(new GltfMesh(groundVertices, plane.Indices32), tile);
        var foliageVertices = plane.Vertices.Select(v => new ModelVertex(
            v.Position + Vector3.UnitY, v.Normal, v.Color, v.Uv, v.Tangent))
            .Append(new ModelVertex(Vector3.Zero, Vector3.UnitY, Vector4.One)).ToArray();
        var foliageMesh = new GltfMesh(foliageVertices, plane.Indices32);
        MeshHandle blade = _scene.LoadMesh(foliageMesh);
        MeshHandle clippedBlade = _scene.LoadMesh(foliageMesh, new Scene3D.SurfaceMaps(clear, alphaCutoff: .5f));
        _foliage = _scene.CreateFoliageBatch([new FoliageInstance(blade, Matrix4x4.Identity, .1f)]);
        _foliageCutout = _scene.CreateFoliageBatch([new FoliageInstance(clippedBlade, Matrix4x4.Identity, .1f)]);
    }

    internal void Configure(bool motion, int samples = 1)
    {
        Scene3D s = Scene;
        s.CameraOverride = null;
        s.Post.UseSmoothPreset();
        s.Post.RenderScale = RenderScale.MatchViewport;
        s.Post.Quality.AntiAliasing = samples > 1 ? AntiAliasing.Msaa(samples) : AntiAliasing.Off;
        s.ForceTemporalForTests = motion;
        s.Post.Starfield = false;
        s.Post.Outline = false;
        s.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
        s.Post.Quality.Shadows.ShadowStrength = 1f;
        s.Post.Quality.Shadows.ShadowNearDistance = 20f;
        s.Post.Quality.Shadows.ShadowMaxDistance = 70f;
        s.Post.LightDirection = Vector3.Normalize(new Vector3(.2f, -1f, .2f));
        s.RenderOrigin = Vector3.Zero;
        s.Camera.Target = Vector3.Zero;
        s.Camera.OrthoSize = 6f;
        s.Camera.Zoom = 1f;
        s.Camera.Distance = 15f;
        s.Camera.Elevation = 1.1f;
        s.Camera.Azimuth = .6f;
        s.CameraCut();
    }

    internal void Receiver(int family, float dissolve = 0f, float complement = 0f, bool cutout = false, float height = 0f)
    {
        Scene3D s = Scene;
        s.UseGpuSkinning = family != 2;
        if (family is 1 or 2)
            s.DrawSkinned(new SkinnedInstanceDraw(cutout ? _skinCutout : _skin, Matrix4x4.CreateTranslation(0, height, 0))
            {
                CastsShadows = false,
                Dissolve = dissolve,
                DissolveComplement = complement,
                Motion = MotionKey.From(1)
            }, [Matrix4x4.Identity]);
        else if (family == 3)
            Assert.Equal(1, s.DrawFoliage(cutout ? _foliageCutout! : _foliage!, Vector3.Zero,
                new FoliageRenderSettings { DrawRadius = 100f, DistantDensity = 1f, WindStrength = .1f }));
        else
            s.Draw(new RigidInstanceDraw(family == 4 ? _splat : family == 5 ? _tile : cutout ? _cutout : _plane, Matrix4x4.CreateTranslation(0, height, 0))
            {
                CastsShadows = false,
                Dissolve = dissolve,
                DissolveComplement = complement,
                Motion = MotionKey.From(1)
            });
    }

    internal void Caster() => Scene.Draw(new RigidInstanceDraw(_box,
        Matrix4x4.CreateScale(3, 1, 3) * Matrix4x4.CreateTranslation(0, 2, 0))
    { ShadowOnly = true });

    internal void Handoff()
    {
        Scene.Draw(_handoffFloor, Matrix4x4.Identity, new Color(.60f, .61f, .63f));
        foreach (float z in new[] { 6f, 10f, 15f, 21f, 28f, 36f, 44f })
            Scene.Draw(_handoffBox, Matrix4x4.CreateScale(1f, 2f, 1f) * Matrix4x4.CreateTranslation(0, 1.6f, z));
    }

    internal void Preserver(int writer)
    {
        Scene3D s = Scene;
        var above = new Vector3(0, .5f, 0);
        switch (writer)
        {
            case 0: s.DrawBillboard(_white, above, 4f, new Color(1f, 0f, 0f, .5f)); break;
            case 1: s.DrawBeam(new Vector3(-4, 1, 0), new Vector3(4, 1, 0), 2f, Color.White); break;
            case 2:
                s.DrawTrail([new TrailSample(new Vector3(-4, 1, 0), 2, 1),
                new TrailSample(new Vector3(4, 1, 0), 2, 1)], TrailStyle.Default); break;
            case 3: s.DrawOverlayMesh(_plane, Matrix4x4.CreateTranslation(above)); break;
            case 4: s.DrawMeshSilhouette(_box, Matrix4x4.CreateTranslation(0, 1, 0), Color.White, .4f); break;
            case 5: s.DrawBillboard(above, 4, Color.White); break;
            case 6: s.Draw(_cutout, Matrix4x4.CreateTranslation(above), Color.White, Material.None, false); break;
            case 7:
                s.Draw(new RigidInstanceDraw(_plane, Matrix4x4.CreateTranslation(above))
                { Dissolve = 1f, CastsShadows = false }); break;
            case 8:
                s.Draw(new RigidInstanceDraw(_plane, Matrix4x4.CreateTranslation(above))
                { Dissolve = 0f, DissolveComplement = 1f, CastsShadows = false }); break;
            case 9: s.DrawBillboard(_white, above, 4f, Color.White, BillboardBlend.Additive); break;
            case 10:
                s.DrawTrail([new TrailSample(new Vector3(-4, 1, 0), 2, 1),
                new TrailSample(new Vector3(4, 1, 0), 2, 1)], TrailStyle.Default with { Blend = TrailBlend.Alpha }); break;
        }
    }

    internal (byte[] Visibility, byte[] Color) Capture(Action<LitVisibilityGpuScene> draw, bool begin = true, bool cameraCut = true)
    {
        Scene3D s = Scene;
        if (begin)
        {
            if (cameraCut) s.CameraCut();
            s.Begin();
        }
        s.EffectTimeSeconds = 1;
        draw(this);
        s.PrepareFrame();
        using (GpuRecording.Open(Device, _commands!, nameof(LitVisibilityGpuScene)))
            s.RenderInternal(_commands!, W, H, _target!);
        Device.Submit(_commands!);
        Device.WaitForIdle();
        byte[] visibility = s.ResolvedVisibilityTarget is { } target ? ReadR8(Device, target) : [];
        return (visibility, GpuReadback.ToRgba(Device, _color!, W, H));
    }

    internal static byte[] ReadR8(IGpuDevice device, IGpuTexture source)
    {
        using IGpuTexture staging = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
            source.Width, source.Height, GpuPixelFormat.R8UNorm, GpuTextureUsage.Staging));
        using IGpuCommandList commands = device.Factory.CreateCommandList();
        commands.Begin(); commands.CopyTexture(source, staging); commands.End();
        device.Submit(commands);
        MappedData map = device.Map(staging, GpuMapMode.Read);
        var bytes = new byte[source.Width * source.Height];
        unsafe
        {
            for (int y = 0; y < source.Height; y++)
                new ReadOnlySpan<byte>((byte*)map.Data + y * map.RowPitch, (int)source.Width)
                    .CopyTo(bytes.AsSpan(y * (int)source.Width));
        }
        device.Unmap(staging);
        return bytes;
    }

    public void Dispose()
    {
        _foliage?.Dispose(); _foliageCutout?.Dispose(); _scene?.Dispose(); _commands?.Dispose(); _target?.Dispose(); _color?.Dispose(); _gpu?.Dispose();
    }
}
