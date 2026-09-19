using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// Pixel coverage for dense point-light scenes. The visible receiver has one light in range and 39 lights whose
/// finite radii cannot reach it. The contributing light must therefore render the same picture by itself, first in
/// the queue, or last in the queue, including after the camera moves.
/// </summary>
public sealed class ManyPointLightsGpuTests(ManyPointLightsScene scene) : IClassFixture<ManyPointLightsScene>
{
    const float LeftAzimuth = -0.55f;
    const float RightAzimuth = 0.65f;

    [GpuTheory]
    [InlineData(LeftAzimuth)]
    [InlineData(RightAzimuth)]
    public void Model_receiver_keeps_its_only_contributing_light_across_queue_order_and_camera_pose(float azimuth)
    {
        ManyPointLightsScene.Shot alone = scene.Capture(
            ManyPointLightsScene.Surface.Model, azimuth, ManyPointLightsScene.LightQueue.RelevantOnly);
        ManyPointLightsScene.Shot relevantLast = scene.Capture(
            ManyPointLightsScene.Surface.Model, azimuth, ManyPointLightsScene.LightQueue.RelevantLast);
        ManyPointLightsScene.Shot relevantFirst = scene.Capture(
            ManyPointLightsScene.Surface.Model, azimuth, ManyPointLightsScene.LightQueue.RelevantFirst);

        AssertMatchesAlone(alone, relevantFirst, $"model, camera azimuth {azimuth}, relevant light first");
        AssertMatchesAlone(alone, relevantLast, $"model, camera azimuth {azimuth}, relevant light last");
    }

    [GpuTheory]
    [InlineData(ManyPointLightsScene.Surface.TileGround)]
    [InlineData(ManyPointLightsScene.Surface.SplatTerrain)]
    [InlineData(ManyPointLightsScene.Surface.Foliage)]
    [InlineData(ManyPointLightsScene.Surface.CpuSkinned)]
    [InlineData(ManyPointLightsScene.Surface.GpuSkinned)]
    public void Every_other_receiver_keeps_its_only_contributing_light_when_it_is_queued_last(
        ManyPointLightsScene.Surface surface)
    {
        ManyPointLightsScene.Shot alone = scene.Capture(surface, LeftAzimuth,
            ManyPointLightsScene.LightQueue.RelevantOnly);
        ManyPointLightsScene.Shot crowded = scene.Capture(surface, LeftAzimuth,
            ManyPointLightsScene.LightQueue.RelevantLast);

        AssertMatchesAlone(alone, crowded, $"{surface}, relevant light last");
    }

    [GpuFact]
    public void Reused_scene_grows_again_past_256_lights_without_losing_the_last_record()
    {
        using var growth = new ManyPointLightsScene();
        ManyPointLightsScene.Shot alone = growth.Capture(
            ManyPointLightsScene.Surface.Model, LeftAzimuth, ManyPointLightsScene.LightQueue.RelevantOnly);
        ManyPointLightsScene.Shot firstGrowth = growth.Capture(
            ManyPointLightsScene.Surface.Model, LeftAzimuth, ManyPointLightsScene.LightQueue.RelevantLast);
        ManyPointLightsScene.Shot secondGrowth = growth.Capture(
            ManyPointLightsScene.Surface.Model, LeftAzimuth, ManyPointLightsScene.LightQueue.RelevantAfter300);

        AssertMatchesAlone(alone, firstGrowth, "model after the first dense-light growth");
        AssertMatchesAlone(alone, secondGrowth, "model with the contributing light in record 301");
    }

    [GpuTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnOverflowedClusterFallsBackToTheCompleteQueueWithoutLosingLight(bool perspective)
    {
        ManyPointLightsScene.Shot alone = scene.Capture(
            ManyPointLightsScene.Surface.Model, LeftAzimuth, ManyPointLightsScene.LightQueue.RelevantOnly,
            perspective: perspective);
        ManyPointLightsScene.Shot overflow = scene.Capture(
            ManyPointLightsScene.Surface.Model, LeftAzimuth, ManyPointLightsScene.LightQueue.ClusterOverflow,
            perspective: perspective);

        Assert.True(overflow.Clusters.IsValid);
        Assert.Equal(perspective ? PointLightClusterProjection.Perspective : PointLightClusterProjection.Orthographic,
            overflow.Clusters.Projection);
        Assert.False(overflow.Clusters.UsesFullFallback);
        Assert.True(overflow.Clusters.OverflowedClusterCount > 0,
            "65 colocated lights must overflow at least one 64-entry cluster");
        Assert.True(overflow.Clusters.HasFallbackClusters);
        int tolerance = Math.Max(8, alone.Brightness / 25);
        Assert.True(Math.Abs(overflow.Brightness - alone.Brightness) <= tolerance,
            $"overflow fallback changed brightness from {alone.Brightness} to {overflow.Brightness}, "
            + $"outside tolerance {tolerance}");
    }

    [GpuFact]
    public void An_empty_frame_after_a_lit_frame_has_no_stale_cluster_light_or_diagnostics()
    {
        ManyPointLightsScene.Shot lit = scene.Capture(ManyPointLightsScene.Surface.Model, LeftAzimuth,
            ManyPointLightsScene.LightQueue.RelevantLast);
        ManyPointLightsScene.Shot empty = scene.Capture(ManyPointLightsScene.Surface.Model, LeftAzimuth,
            ManyPointLightsScene.LightQueue.None);
        Assert.Equal(0, empty.Clusters.SubmittedLightCount);
        Assert.Equal(0, empty.Clusters.LightReferenceCount);
        Assert.False(empty.Clusters.HasFallbackClusters);
        Assert.True(empty.Brightness < lit.Brightness / 3);
    }

    [GpuFact]
    public void Invalid_light_geometry_cannot_change_illumination_when_a_cluster_overflows()
    {
        ManyPointLightsScene.Shot alone = scene.Capture(ManyPointLightsScene.Surface.Model, LeftAzimuth,
            ManyPointLightsScene.LightQueue.RelevantOnly);
        ManyPointLightsScene.Shot sparse = scene.Capture(ManyPointLightsScene.Surface.Model, LeftAzimuth,
            ManyPointLightsScene.LightQueue.InvalidGeometryAlongsideRelevant);
        ManyPointLightsScene.Shot dense = scene.Capture(ManyPointLightsScene.Surface.Model, LeftAzimuth,
            ManyPointLightsScene.LightQueue.ClusterOverflowWithInvalidGeometry);

        Assert.Equal(1, sparse.Clusters.SubmittedLightCount);
        Assert.Equal(65, dense.Clusters.SubmittedLightCount);
        AssertMatchesAlone(alone, sparse, "invalid light geometry in a sparse cluster");
        Assert.True(dense.Clusters.HasFallbackClusters);
        Assert.InRange(Math.Abs(dense.Brightness - alone.Brightness), 0, 8);
    }

    [GpuTheory]
    [InlineData(ManyPointLightsScene.Surface.Model)]
    [InlineData(ManyPointLightsScene.Surface.TileGround)]
    [InlineData(ManyPointLightsScene.Surface.SplatTerrain)]
    [InlineData(ManyPointLightsScene.Surface.Foliage)]
    [InlineData(ManyPointLightsScene.Surface.CpuSkinned)]
    [InlineData(ManyPointLightsScene.Surface.GpuSkinned)]
    public void Perspective_clusters_preserve_receivers_during_orbit_and_near_plane_motion(
        ManyPointLightsScene.Surface surface)
    {
        foreach (float azimuth in new[] { LeftAzimuth, RightAzimuth })
        foreach (float distance in new[] { 4f, 8f })
        {
            ManyPointLightsScene.Shot alone = scene.Capture(surface, azimuth,
                ManyPointLightsScene.LightQueue.RelevantOnly, perspective: true, cameraDistance: distance);
            ManyPointLightsScene.Shot crowded = scene.Capture(surface, azimuth,
                ManyPointLightsScene.LightQueue.RelevantLast, perspective: true, cameraDistance: distance);
            AssertMatchesAlone(alone, crowded, $"{surface}, perspective orbit {azimuth}, distance {distance}",
                PointLightClusterProjection.Perspective);
        }
    }

    [GpuFact]
    public void Reused_many_light_scene_matches_a_fresh_scene_after_camera_and_order_changes()
    {
        scene.Capture(ManyPointLightsScene.Surface.Model, LeftAzimuth, ManyPointLightsScene.LightQueue.RelevantLast);
        scene.Capture(ManyPointLightsScene.Surface.TileGround, RightAzimuth, ManyPointLightsScene.LightQueue.RelevantFirst);
        scene.Capture(ManyPointLightsScene.Surface.SplatTerrain, LeftAzimuth, ManyPointLightsScene.LightQueue.RelevantLast);
        scene.Capture(ManyPointLightsScene.Surface.Foliage, RightAzimuth, ManyPointLightsScene.LightQueue.RelevantLast);
        scene.Capture(ManyPointLightsScene.Surface.CpuSkinned, LeftAzimuth, ManyPointLightsScene.LightQueue.RelevantLast);
        scene.Capture(ManyPointLightsScene.Surface.GpuSkinned, RightAzimuth, ManyPointLightsScene.LightQueue.RelevantAfter300);
        scene.Capture(ManyPointLightsScene.Surface.Model, LeftAzimuth,
            ManyPointLightsScene.LightQueue.ClusterOverflow, perspective: true);

        ManyPointLightsScene.Shot aged = scene.Capture(
            ManyPointLightsScene.Surface.Model, LeftAzimuth, ManyPointLightsScene.LightQueue.RelevantLast);
        using var fresh = new ManyPointLightsScene();
        ManyPointLightsScene.Shot alone = fresh.Capture(
            ManyPointLightsScene.Surface.Model, LeftAzimuth, ManyPointLightsScene.LightQueue.RelevantLast);

        Assert.Equal(alone.Rgba, aged.Rgba);
    }

    static void AssertMatchesAlone(ManyPointLightsScene.Shot alone, ManyPointLightsScene.Shot crowded, string what,
        PointLightClusterProjection projection = PointLightClusterProjection.Orthographic)
    {
        AssertClustered(alone.Clusters, $"{what}, one-light control", projection);
        AssertClustered(crowded.Clusters, what, projection);
        Assert.True(alone.Brightness > 100,
            $"{what}: the one-light control is too dark to prove that the contributing light rendered " +
            $"(brightness {alone.Brightness})");
        int tolerance = Math.Max(6, alone.Brightness / 30);
        Assert.True(Math.Abs(crowded.Brightness - alone.Brightness) <= tolerance,
            $"{what}: receiver brightness changed from the one-light control {alone.Brightness} to " +
            $"{crowded.Brightness}, outside tolerance {tolerance}");
    }

    static void AssertClustered(PointLightClusterDiagnostics diagnostics, string what,
        PointLightClusterProjection projection)
    {
        Assert.True(diagnostics.IsValid, $"{what}: cluster geometry was invalid");
        Assert.Equal(projection, diagnostics.Projection);
        Assert.False(diagnostics.UsesFullFallback, $"{what}: every fragment used the complete-list fallback");
        Assert.Equal(0, diagnostics.OverflowedClusterCount);
        Assert.True(diagnostics.LightReferenceCount > 0, $"{what}: no light references reached the cluster grid");
        Assert.Equal(16 * 9 * 24, diagnostics.ClusterCount);
    }
}

/// <summary>
/// One lazily created scene shared by <see cref="ManyPointLightsGpuTests"/>. It carries both the model and
/// tile-ground receivers through the same frame uniforms, and exposes a fresh-instance parity check in the test
/// class so configuration history cannot justify any image difference.
/// </summary>
public sealed class ManyPointLightsScene : IDisposable
{
    public const int Width = 160;
    public const int Height = 160;

    static readonly Vector3 Receiver = Vector3.Zero;
    static readonly Vector3 RelevantLightPosition = new(0f, 4f, 0f);

    GpuDeviceContext? _gpu;
    IGpuTexture? _target;
    IGpuFramebuffer? _framebuffer;
    IGpuCommandList? _commands;
    Scene3D? _scene;
    MeshHandle _model;
    MeshHandle _tileGround;
    MeshHandle _splatTerrain;
    MeshHandle _foliageMesh;
    FoliageBatch? _foliage;
    SkinnedMeshHandle _skinned;
    Matrix4x4[]? _skinnedRestPose;

    public enum Surface
    {
        Model,
        TileGround,
        SplatTerrain,
        Foliage,
        CpuSkinned,
        GpuSkinned,
    }

    public enum LightQueue
    {
        None,
        RelevantOnly,
        RelevantLast,
        RelevantFirst,
        RelevantAfter300,
        ClusterOverflow,
        InvalidGeometryAlongsideRelevant,
        ClusterOverflowWithInvalidGeometry,
    }

    public sealed record Shot(byte[] Rgba, int Brightness, PointLightClusterDiagnostics Clusters);

    public Shot Capture(Surface surface, float cameraAzimuth, LightQueue lights,
        bool perspective = false, float cameraDistance = 8f)
    {
        IGpuDevice device = Device();
        Scene3D scene = _scene!;

        scene.Camera.Azimuth = cameraAzimuth;
        scene.Camera.Elevation = 1.15f;
        scene.Camera.Frame(Receiver, new Vector3(7f, 0.2f, 7f), margin: 1f);
        scene.CameraOverride = perspective ? new FlyCamera3D
        {
            Position = new Vector3(-cameraDistance * MathF.Sin(cameraAzimuth), cameraDistance,
                -cameraDistance * MathF.Cos(cameraAzimuth)),
            Yaw = cameraAzimuth,
            Pitch = -MathF.PI / 4f,
            FieldOfView = MathF.PI / 3f,
            AspectRatio = (float)Width / Height,
            NearPlane = .1f,
            FarPlane = 500f,
        } : null;

        scene.Begin();
        scene.UseGpuSkinning = surface == Surface.GpuSkinned;
        DrawReceiver(scene, surface);
        QueueLights(scene, lights);
        scene.PrepareFrame();
        using (GpuRecording.Open(device, _commands!, "ManyPointLightsScene.Capture"))
            scene.RenderInternal(_commands!, Width, Height, _framebuffer!);
        device.Submit(_commands!);
        device.WaitForIdle();

        byte[] rgba = GpuReadback.ToRgba(device, _target!, Width, Height);
        return new Shot(rgba, ReceiverBrightness(scene, rgba), scene.PointLightClusters);
    }

    IGpuDevice Device()
    {
        if (_gpu is not null) return _gpu.GpuDevice;

        _gpu = GpuDeviceContext.CreateHeadless();
        IGpuDevice device = _gpu.GpuDevice;
        IGpuResourceFactory factory = device.Factory;
        _target = factory.CreateTexture(GpuTextureDescription.Texture2D(
            Width, Height, GpuPixelFormat.R8G8B8A8UNorm,
            GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        _framebuffer = factory.CreateFramebuffer(null, _target);
        _commands = factory.CreateCommandList();
        _scene = new Scene3D(device, _framebuffer.Outputs, null);
        _model = _scene.LoadMesh(MeshPrimitives.Plane(8f, 8f));
        var tileMaterial = _scene.LoadTileGroundMaterial(4, 4, [GreyLayer()], baseSpecStrength: 0f);
        _tileGround = _scene.LoadMesh(TileGroundPlane(), tileMaterial);
        var splatMaterial = _scene.LoadSplatMaterial(4, 4, SplatLayers(), baseSpecStrength: 0f);
        GltfMesh splatPlane = MeshPrimitives.Plane(8f, 8f);
        foreach (ref ModelVertex vertex in splatPlane.Vertices.AsSpan()) vertex.Color = Vector4.UnitX;
        _splatTerrain = _scene.LoadMesh(splatPlane, splatMaterial);
        _foliageMesh = _scene.LoadMesh(MeshPrimitives.Tile(8f, 0.05f));
        _foliage = _scene.CreateFoliageBatch([new FoliageInstance(_foliageMesh, Matrix4x4.Identity, 0.1f)]);
        SkinnedGltfMesh skinnedPlane = SkinnedPlane();
        _skinned = _scene.LoadSkinnedMesh(skinnedPlane);
        _skinnedRestPose = skinnedPlane.RestPose;
        Setup(_scene);
        return device;
    }

    void DrawReceiver(Scene3D scene, Surface surface)
    {
        switch (surface)
        {
            case Surface.Model:
                scene.Draw(_model, Matrix4x4.Identity, Color.White, Material.None);
                break;
            case Surface.TileGround:
                scene.Draw(_tileGround, Matrix4x4.Identity, Color.White);
                break;
            case Surface.SplatTerrain:
                scene.Draw(_splatTerrain, Matrix4x4.Identity, Color.White);
                break;
            case Surface.Foliage:
                scene.DrawFoliage(_foliage!, Vector3.Zero,
                    new FoliageRenderSettings { DrawRadius = 100f, DistantDensity = 1f });
                break;
            case Surface.CpuSkinned:
            case Surface.GpuSkinned:
                scene.DrawSkinned(_skinned, _skinnedRestPose!, Matrix4x4.Identity, Color.White);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(surface));
        }
    }

    static void Setup(Scene3D scene)
    {
        scene.Post.Starfield = false;
        scene.Post.Outline = false;
        scene.Post.TransparentBackground = false;
        scene.Post.BackgroundColor = Color.Black;
        scene.Post.LightColor = Color.Black;
        scene.Post.FillLightColor = Color.Black;
        scene.Post.AmbientColor = new Color(0.01f, 0.01f, 0.01f, 1f);
        scene.Post.CelBands = 0;
        scene.Post.Hdr.Enabled = false;
        scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        scene.Post.Quality.AntiAliasing = AntiAliasing.Off;
        scene.EffectTimeSeconds = 0f;
        scene.Camera.AspectRatio = (float)Width / Height;
    }

    static void QueueLights(Scene3D scene, LightQueue lights)
    {
        if (lights == LightQueue.None) return;
        if (lights is LightQueue.InvalidGeometryAlongsideRelevant or LightQueue.ClusterOverflowWithInvalidGeometry)
        {
            foreach (float radius in new[] { -6f, 0f, float.NaN, float.PositiveInfinity })
                scene.AddLight(RelevantLightPosition, Color.White, radius, intensity: 2f);
            scene.AddLight(new Vector3(float.NaN, 4f, 0f), Color.White, radius: 6f, intensity: 2f);
            if (lights == LightQueue.InvalidGeometryAlongsideRelevant)
            {
                AddRelevant(scene);
                return;
            }
        }
        if (lights is LightQueue.ClusterOverflow or LightQueue.ClusterOverflowWithInvalidGeometry)
        {
            for (int i = 0; i < 65; i++)
                scene.AddLight(RelevantLightPosition, Color.White, radius: 6f, intensity: 2f / 65f);
            return;
        }
        if (lights == LightQueue.RelevantFirst) AddRelevant(scene);
        if (lights != LightQueue.RelevantOnly)
        {
            int distractionCount = lights == LightQueue.RelevantAfter300 ? 300 : 39;
            int first = lights == LightQueue.RelevantFirst ? distractionCount - 1 : 0;
            int end = lights == LightQueue.RelevantFirst ? -1 : distractionCount;
            int step = lights == LightQueue.RelevantFirst ? -1 : 1;
            for (int i = first; i != end; i += step)
            {
                float x = 160f + i % 8 * 6f;
                float z = 140f + i / 8 * 6f;
                scene.AddLight(new Vector3(x, 2f + i % 3, z), Color.White, radius: 3f, intensity: 2f);
            }
        }
        if (lights != LightQueue.RelevantFirst) AddRelevant(scene);
    }

    static void AddRelevant(Scene3D scene) =>
        scene.AddLight(RelevantLightPosition, Color.White, radius: 6f, intensity: 2f);

    static int ReceiverBrightness(Scene3D scene, byte[] rgba)
    {
        Vector4 clip = Vector4.Transform(new Vector4(Receiver, 1f),
            (scene.CameraOverride ?? scene.Camera).ViewProjection);
        Assert.True(clip.W > 0f, "the receiver must project in front of the active camera");
        var pixel = new Vector2((clip.X / clip.W * .5f + .5f) * Width,
            (.5f - clip.Y / clip.W * .5f) * Height);
        int cx = Math.Clamp((int)pixel.X, 3, Width - 4);
        int cy = Math.Clamp((int)pixel.Y, 3, Height - 4);
        int sum = 0;
        int samples = 0;
        for (int y = cy - 3; y <= cy + 3; y++)
        {
            for (int x = cx - 3; x <= cx + 3; x++)
            {
                int index = (y * Width + x) * 4;
                sum += rgba[index] + rgba[index + 1] + rgba[index + 2];
                samples++;
            }
        }
        return sum / samples;
    }

    static GltfMesh TileGroundPlane()
    {
        var weights = new Vector4(1f, 0f, 0f, 0f);
        var slots = Vector2.Zero;
        var tangent = new Vector4(0f, 0f, 1f, 0f);
        var vertices = new[]
        {
            new ModelVertex(new Vector3(-4f, 0f, -4f), Vector3.UnitY, weights, slots, tangent),
            new ModelVertex(new Vector3(4f, 0f, -4f), Vector3.UnitY, weights, slots, tangent),
            new ModelVertex(new Vector3(4f, 0f, 4f), Vector3.UnitY, weights, slots, tangent),
            new ModelVertex(new Vector3(-4f, 0f, 4f), Vector3.UnitY, weights, slots, tangent),
        };
        return new GltfMesh(vertices, new ushort[] { 0, 1, 2, 0, 2, 3 });
    }

    static TileGroundLayerImage GreyLayer()
    {
        var rgba = new byte[4 * 4 * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = 220;
            rgba[i + 1] = 220;
            rgba[i + 2] = 220;
            rgba[i + 3] = 255;
        }
        return new TileGroundLayerImage { AlbedoRgba = rgba, TilesPerMetre = 0.5f };
    }

    static SplatLayerImage[] SplatLayers() =>
        [SplatLayer(), SplatLayer(), SplatLayer(), SplatLayer(), SplatLayer()];

    static SplatLayerImage SplatLayer()
    {
        var albedo = new byte[4 * 4 * 4];
        var normal = new byte[4 * 4 * 4];
        for (int i = 0; i < albedo.Length; i += 4)
        {
            albedo[i] = 220;
            albedo[i + 1] = 220;
            albedo[i + 2] = 220;
            albedo[i + 3] = 255;
            normal[i] = 128;
            normal[i + 1] = 128;
            normal[i + 2] = 255;
            normal[i + 3] = 255;
        }
        return new SplatLayerImage { AlbedoRgba = albedo, NormalRgba = normal, Roughness = 1f };
    }

    static SkinnedGltfMesh SkinnedPlane()
    {
        static SkinnedVertex Vertex(float x, float z) => new()
        {
            Position = new Vector3(x, 0f, z),
            Normal = Vector3.UnitY,
            Color = Vector4.One,
            BoneIndices = Vector4.Zero,
            BoneWeights = Vector4.UnitX,
        };

        SkinnedVertex[] vertices =
        [
            Vertex(-4f, -4f),
            Vertex(4f, -4f),
            Vertex(4f, 4f),
            Vertex(-4f, 4f),
        ];
        Matrix4x4[] bones = [Matrix4x4.Identity];
        return new SkinnedGltfMesh(vertices, new ushort[] { 0, 1, 2, 0, 2, 3 }, bones, bones);
    }

    public void Dispose()
    {
        _commands?.Dispose();
        _foliage?.Dispose();
        _scene?.Dispose();
        _framebuffer?.Dispose();
        _target?.Dispose();
        _gpu?.Dispose();
        _commands = null;
        _foliage = null;
        _scene = null;
        _framebuffer = null;
        _target = null;
        _gpu = null;
    }
}
