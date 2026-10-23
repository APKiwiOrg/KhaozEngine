using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Public synthetic cases shared by the baseline and later optical acceptance tests.</summary>
public enum WaterOpticsScenario { FlatBed, SkinnedBody, SlopingBank, Ledge, DeepShelf, StackedBodies, AdjacentFields }

public readonly record struct WaterOpticsCase(float BedDepth = 2f, float ObjectDepth = 0.103f,
    Vector3 Offset = default, int Samples = 1, bool Temporal = false, bool ReverseOrder = false)
{
    public WaterOpticsCase() : this(2f, 0.103f) { }
}

public sealed class WaterOpticsImage(byte[] rgba, int width, int height,
    IReadOnlyDictionary<string, Vector2> samples)
{
    public byte[] Rgba { get; } = rgba;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public IReadOnlyDictionary<string, Vector2> Samples { get; } = samples;

    /// <summary>Average normalized RGB over a complete 5 by 5 patch in top-left readback coordinates.</summary>
    public Vector3 Sample(string name)
    {
        Vector2 p = Samples[name];
        int cx = (int)(p.X + 0.5f), cy = (int)(p.Y + 0.5f);
        if (cx < 2 || cy < 2 || cx >= Width - 2 || cy >= Height - 2)
            throw new ArgumentOutOfRangeException(nameof(name), "The complete 5 by 5 sample must be in frame.");
        Vector3 sum = Vector3.Zero;
        for (int y = cy - 2; y <= cy + 2; y++)
            for (int x = cx - 2; x <= cx + 2; x++)
            {
                int i = (y * Width + x) * 4;
                sum += new Vector3(Rgba[i], Rgba[i + 1], Rgba[i + 2]);
            }
        return sum / (25f * 255f);
    }
}

/// <summary>
/// Lazy shared-device optical proof. Every capture resets fixture-owned settings and submissions.
/// Prepare loads scene-owned resources once. The bind callback decorates each absolute water request,
/// so later optical tests can use the identical geometry without depending on future product types here.
/// No golden is baked and no private asset is loaded.
/// </summary>
public sealed class WaterOpticsProofScene : IDisposable
{
    public const int Width = 384;
    public const int Height = 384;
    const float BodyX = -1.2f;
    const float Radius = 0.55f;
    static readonly Vector3 Eye = new(0f, 4.8f, 8f);
    const int Frames = 2;

    GpuDeviceContext? _gpu;
    IGpuTexture? _target;
    IGpuFramebuffer? _framebuffer;
    IGpuCommandList? _commands;
    Scene3D? _scene;
    MeshHandle _plane, _box;
    SkinnedMeshHandle _body;
    SkinnedGltfMesh? _tube;
    FlyCamera3D? _camera;
    bool _disposed;

    /// <summary>Run resource preparation outside recording, on the same scene Capture will use.</summary>
    public void Prepare(Action<Scene3D> prepare)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        Device();
        prepare(_scene!);
    }

    public WaterOpticsImage Capture(WaterOpticsScenario scenario, in WaterOpticsCase settings,
        Func<WaterPlane, WaterPlane>? bind = null)
    {
        if (!Enum.IsDefined(scenario)) throw new ArgumentOutOfRangeException(nameof(scenario));
        if (!float.IsFinite(settings.BedDepth) || settings.BedDepth <= 0f
            || !float.IsFinite(settings.ObjectDepth) || settings.ObjectDepth < 0f
            || !float.IsFinite(settings.Offset.X) || !float.IsFinite(settings.Offset.Y)
            || !float.IsFinite(settings.Offset.Z))
            throw new ArgumentOutOfRangeException(nameof(settings));
        if (settings.Samples is not (1 or 2 or 4 or 8) || (settings.Temporal && settings.Samples != 1))
            throw new ArgumentException("Select temporal rendering or a supported MSAA sample count.", nameof(settings));

        IGpuDevice device = Device();
        if (device.Capabilities.HighestSupportedMsaaSampleCountAtMost(settings.Samples) != settings.Samples)
            throw new NotSupportedException($"{device.Backend} cannot render {settings.Samples} samples.");
        Scene3D scene = _scene!;
        Configure(scene, settings);
        WaterPlane[] planes = Planes(scenario, settings);
        if (settings.ReverseOrder) Array.Reverse(planes);
        if (bind is not null)
            for (int i = 0; i < planes.Length; i++) planes[i] = bind(planes[i]);

        // Preserve the renderer's raw depth, backend Y correction and jittered inverse projection.
        // WorldToScreen below uses its public unjittered, top-left projection for patch centres.
        for (int frame = 0; frame < Frames; frame++)
        {
            scene.Begin();
            scene.EffectTimeSeconds = 0f;
            DrawGeometry(scene, scenario, settings);
            foreach (WaterPlane plane in planes) scene.DrawWater(plane);
            scene.PrepareFrame();
            using (GpuRecording.Open(device, _commands!, "WaterOpticsProofScene.Capture"))
                scene.RenderInternal(_commands!, Width, Height, _framebuffer!);
            device.Submit(_commands!);
            device.WaitForIdle();
            device.Present();
        }
        return new WaterOpticsImage(GpuReadback.ToRgba(device, _target!, Width, Height),
            Width, Height, ProjectSamples(scenario, settings));
    }

    void Configure(Scene3D scene, in WaterOpticsCase settings)
    {
        scene.Post.Quality.AntiAliasing = settings.Temporal ? AntiAliasing.Temporal
            : settings.Samples > 1 ? AntiAliasing.Msaa(settings.Samples) : AntiAliasing.Off;
        scene.Post.RenderScale = RenderScale.MatchViewport;
        scene.ForceTemporalForTests = settings.Temporal;
        scene.CameraCut();
        scene.Post.Water = new WaterSettings
        {
            WaveSource = WaterWaveSource.Procedural,
            SwellAmplitude = 0f,
            NormalStrength = 0.08f,
            ShoreFadeDistance = 0.20f,
            FoamStrength = 0f,
            GlintStrength = 0f,
            GridFocusBias = 1f
        };
        _camera!.Position = settings.Offset + Eye;
        Vector3 direction = Vector3.Normalize(new Vector3(0f, 0.1f, 0f) - Eye);
        _camera.Yaw = MathF.Atan2(direction.X, direction.Z);
        _camera.Pitch = MathF.Asin(direction.Y);
        // The automatic origin latch is exercised by Offset, just as in a normal scene.
        scene.RenderOrigin = null;
    }

    static WaterPlane[] Planes(WaterOpticsScenario scenario, in WaterOpticsCase settings)
    {
        Vector3 o = settings.Offset;
        if (scenario == WaterOpticsScenario.AdjacentFields)
            return [new(o.X - 2f, o.Y, o.Z, 2f, 3f), new(o.X + 2f, o.Y, o.Z, 2f, 3f)];
        if (scenario == WaterOpticsScenario.StackedBodies)
            return [new(o.X, o.Y, o.Z, 4f, 3f), new(o.X, o.Y + 1.4f, o.Z, 4f, 3f)];
        return [new(o.X, o.Y, o.Z, 4f, 3f)];
    }

    void DrawGeometry(Scene3D scene, WaterOpticsScenario scenario, in WaterOpticsCase settings)
    {
        Vector3 o = settings.Offset;
        DrawBed(scene, settings.BedDepth, o, shelf: false);

        if (scenario is WaterOpticsScenario.SkinnedBody or WaterOpticsScenario.SlopingBank
            or WaterOpticsScenario.Ledge or WaterOpticsScenario.AdjacentFields)
        {
            // Authored ramp y = (x - 2) / 2, crossing the waterline at x = 2.
            // Its signed land samples remain observable beside the wet 0.20 m feather.
            Matrix4x4 ramp = Matrix4x4.Identity;
            ramp.M12 = 0.5f;
            scene.Draw(_plane, Matrix4x4.CreateScale(3f, 1f, 6f) * ramp
                * Matrix4x4.CreateTranslation(o + new Vector3(2f, 0f, 0f)),
                new Color(0.62f, 0.46f, 0.25f, 1f));
        }
        if (scenario == WaterOpticsScenario.SkinnedBody)
            scene.DrawSkinned(_body, _tube!.RestPose,
                Matrix4x4.CreateTranslation(o + new Vector3(BodyX, -settings.ObjectDepth - 0.3f, 0f)),
                new Color(0.95f, 0.34f, 0.12f, 1f));
        if (scenario == WaterOpticsScenario.Ledge)
            DrawBox(scene, new Vector3(1.8f, 0.4f, 1.8f),
                o + new Vector3(BodyX, -settings.ObjectDepth - 0.2f, 0f),
                new Color(0.72f, 0.68f, 0.56f, 1f));
        if (scenario == WaterOpticsScenario.DeepShelf)
            DrawBed(scene, settings.BedDepth * 0.5f, o, shelf: true);
        if (scenario == WaterOpticsScenario.StackedBodies)
        {
            // An enclosed synthetic lower space shares XZ with the raised water request.
            DrawBox(scene, new Vector3(0.2f, 1.2f, 6f), o + new Vector3(-3.8f, 0.4f, 0f),
                new Color(0.55f, 0.49f, 0.38f, 1f));
            DrawBox(scene, new Vector3(3f, 0.2f, 6f), o + new Vector3(-2.4f, 1.1f, 0f),
                new Color(0.55f, 0.49f, 0.38f, 1f));
        }
    }

    void DrawPlane(Scene3D scene, Vector3 scale, Vector3 position, Color color)
        => scene.Draw(_plane, Matrix4x4.CreateScale(scale) * Matrix4x4.CreateTranslation(position), color);

    void DrawBed(Scene3D scene, float depth, Vector3 offset, bool shelf)
    {
        // Extend the opaque receiver to include the camera rays THROUGH the bounded water.
        // At 100 m those hit far behind its XZ footprint. A water-sized floor disappears from view.
        float spread = 1f + depth / Eye.Y;
        float width = 8f * spread;
        float length = 6f * spread + 2f * Eye.Z * depth / Eye.Y;
        DrawPlane(scene, new Vector3(shelf ? width * 0.5f : width, 1f, length),
            offset + new Vector3(shelf ? -width * 0.25f : 0f, -depth, 0f),
            shelf ? new Color(0.50f, 0.42f, 0.30f, 1f) : new Color(0.36f, 0.29f, 0.20f, 1f));
    }

    void DrawBox(Scene3D scene, Vector3 scale, Vector3 position, Color color)
        => scene.Draw(_box, Matrix4x4.CreateScale(scale) * Matrix4x4.CreateTranslation(position), color);

    IReadOnlyDictionary<string, Vector2> ProjectSamples(WaterOpticsScenario scenario, in WaterOpticsCase settings)
    {
        var world = new Dictionary<string, Vector3>
        {
            ["background"] = new(0f, 3.5f, 0f),
            ["open"] = new(-2.8f, 0f, 0.8f),
            ["far-water"] = new(0f, 0f, -2.5f),
            ["shelf"] = new(-2f, 0f, 1f),
            ["bed"] = new(2f, 0f, 1f),
            ["dry"] = new(BodyX, 1f - settings.ObjectDepth, Radius),
            ["submerged"] = new(BodyX, -settings.ObjectDepth, Radius),
            ["bank"] = new(2.4f, 0.2f, 0.5f),
            ["shore"] = new(1.8f, -0.1f, 0.5f),
            ["dry-bank"] = new(3f, 0.5f, 0.5f),
            ["seam-left"] = new(-0.1f, 0f, 1f),
            ["seam-right"] = new(0.1f, 0f, 1f)
        };
        if (scenario == WaterOpticsScenario.StackedBodies)
        {
            // Keep the entire 5 by 5 patch in front of the upper water's projected front edge.
            world["lower"] = new(1f, 0f, 2f);
            world["upper"] = new(1f, 1.4f, 1f);
        }
        var projected = new Dictionary<string, Vector2>();
        foreach (var (name, point) in world)
        {
            if (!_camera!.WorldToScreen(point + settings.Offset, Width, Height, out Vector2 pixel))
                throw new InvalidOperationException($"The {name} point is behind the proof camera.");
            projected.Add(name, pixel);
        }
        return projected;
    }

    IGpuDevice Device()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_gpu is not null) return _gpu.GpuDevice;
        try
        {
            _gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice device = _gpu.GpuDevice;
            _target = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                Width, Height, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            _framebuffer = device.Factory.CreateFramebuffer(null, _target);
            _commands = device.Factory.CreateCommandList();
            _scene = new Scene3D(device, _framebuffer.Outputs);
            _plane = _scene.LoadMesh(MeshPrimitives.Plane());
            _box = _scene.LoadMesh(MeshPrimitives.Box());
            // The real four-bone tube used by BlobDecalGhostHoleGoldenTests, with no rigid substitute.
            _tube = SkinnedMeshBuilder.BuildTube(Radius, 2f, 8, 12, 4, Axis.Y);
            _body = _scene.LoadSkinnedMesh(_tube);
            _camera = new FlyCamera3D { AspectRatio = 1f, NearPlane = 0.1f, FarPlane = 500f };
            _scene.CameraOverride = _camera;
            _scene.Post.Starfield = false;
            _scene.Post.Sky.Enabled = false;
            _scene.Post.Background = BackgroundMode.Solid;
            _scene.Post.BackgroundColor = new Color(0.04f, 0.05f, 0.08f, 1f);
            _scene.Post.LightDirection = Vector3.Normalize(new Vector3(-0.45f, -0.75f, -0.4f));
            return device;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gpu?.GpuDevice.WaitForIdle();
        _commands?.Dispose();
        _scene?.Dispose();
        _framebuffer?.Dispose();
        _target?.Dispose();
        _gpu?.Dispose();
    }
}
