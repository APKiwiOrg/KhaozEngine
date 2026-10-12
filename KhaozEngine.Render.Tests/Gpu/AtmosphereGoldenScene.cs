using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Lazy shared atmosphere scene. Each capture resets public post settings, camera and cycle,
/// while keeping the scene's resource and atmosphere caches alive for reuse tests.</summary>
public sealed class AtmosphereGoldenScene : IDisposable
{
    public const int Width = 480;
    public const int Height = 320;
    const int Frames = 2;
    static readonly FieldInfo[] PostFields = typeof(PixelPostProcessSettings).GetFields(BindingFlags.Public | BindingFlags.Instance);

    GpuDeviceContext? _gpu;
    IGpuTexture? _target;
    IGpuFramebuffer? _framebuffer;
    IGpuCommandList? _commands;
    Scene3D? _scene;
    MeshHandle _floor, _box;
    int _width = Width, _height = Height;
    (MeshHandle mesh, TerrainChunkRegion region)[]? _fogRegions;
    readonly Dictionary<string, MeshHandle> _receivers = new();
    SkinnedMeshHandle _skin;
    FoliageBatch? _foliage;
    bool _measurementPrimed;

    /// <summary>Reset before configuration. Edit this inside the capture callback.</summary>
    public SunCycleSettings Cycle { get; private set; } = EquatorialCycle();

    /// <summary>Reset to noon before configuration, then evaluated after the callback.</summary>
    public float TimeOfDay { get; set; } = .5f;

    internal Scene3D Scene { get { Device(); return _scene!; } }
    internal IGpuDevice GpuDevice => Device();
    public byte[] CaptureFog(Vector3 origin = default)
    {
        Device();
        if (_fogRegions is null)
        {
            var field = new TerrainField(TerrainPresets.Clearing());
            var material = _scene!.LoadTerrainMaterial(WhiteTerrain());
            _fogRegions = new (MeshHandle, TerrainChunkRegion)[3];
            for (int i = 0; i < 3; i++)
            {
                float distance = new[] { 100f, 1000f, 5000f }[i];
                float x = new[] { -150f, 200f, -2500f }[i];
                var region = new TerrainChunkRegion
                {
                    OriginX = 100000 + x,
                    OriginZ = 100000 + distance,
                    Size = distance,
                };
                _fogRegions[i] = (_scene!.LoadTerrainChunk(TerrainChunkBuilder.Build(field, region, lod: 2), material), region);
            }
        }
        return Capture(scene =>
        {
            var camera = (FlyCamera3D)scene.CameraOverride!;
            camera.Position = new Vector3(100000, 2, 100000);
            camera.Yaw = 0;
            camera.Pitch = -.03f;
            camera.FarPlane = 10000;
            scene.RenderOrigin = origin;
            scene.Post.Atmosphere.AerialDistanceScale = 20;
            scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        }, water: false, scene =>
        {
            foreach (var (mesh, region) in _fogRegions) scene.DrawTerrainChunk(mesh, region);
        });
    }

    public byte[] CaptureReceiver(string receiver, bool temporal = false, bool fog = true)
    {
        Device();
        EnsureReceivers();
        return Capture(scene =>
        {
            var camera = (FlyCamera3D)scene.CameraOverride!;
            camera.Position = new Vector3(0, 2, 0);
            camera.Yaw = camera.Pitch = 0;
            camera.FarPlane = 10000;
            scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
            scene.Post.FillLightDirection = Vector3.UnitY;
            scene.Post.Hdr.ChromaPreservation = 0;
            scene.Post.Quality.AntiAliasing = temporal ? AntiAliasing.Temporal : AntiAliasing.Off;
            scene.Post.Temporal.Upscale = TemporalUpscale.Native;
            scene.Post.Temporal.Sharpness = 0;
            scene.Post.Atmosphere.AerialDistanceScale = fog ? 20 : 0;
            scene.UseGpuSkinning = !receiver.StartsWith("cpu-skinned", StringComparison.Ordinal);
            scene.CameraCut();
        }, water: false, scene =>
        {
            if (receiver == "foliage")
            {
                scene.DrawFoliage(_foliage!, Vector3.Zero, new FoliageRenderSettings
                {
                    DrawRadius = 10000,
                    DensityRadius = 10000,
                    DistantDensity = 1,
                    WindStrength = 0,
                });
                return;
            }
            foreach ((float x, float z, float scale) in new[] { (32f, 100f, 20f), (-320f, 1000f, 200f) })
            {
                var world = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateTranslation(x, 2, z);
                if (receiver.Contains("skinned", StringComparison.Ordinal))
                    scene.DrawSkinned(new SkinnedInstanceDraw(_skin, world)
                    {
                        Tint = Color.White,
                        Dissolve = receiver.EndsWith("dissolve", StringComparison.Ordinal) ? .000001f : 0,
                    }, new[] { Matrix4x4.Identity });
                else if (receiver == "dissolve")
                    // A tiny positive threshold exercises dissolve without eroding the sampled white patches.
                    scene.Draw(new RigidInstanceDraw(_receivers["rigid"], world)
                    {
                        Tint = Color.White,
                        Dissolve = .000001f,
                        DissolveEdgeWidth = 0,
                    });
                else scene.Draw(_receivers[receiver], world, Color.White);
            }
        });
    }

    public double MeasureSingleFrame(bool enabled)
    {
        if (!_measurementPrimed)
        {
            if (_gpu is not null) throw new InvalidOperationException("Use a fresh fixture for the one-shot measurement.");
            _width = 1600;
            _height = 900;
            Device();
            Reset(_scene!);
            PrepareMeasurementFrame(enabled: true);
            MeasurementColdCacheMilliseconds = CompletedMeasurementFrame();
            _measurementPrimed = true;
            Console.WriteLine($"ATMOSPHERE_DEVICE {_gpu!.Capabilities.DeviceName}");
        }
        PrepareMeasurementFrame(enabled);
        return CompletedMeasurementFrame();
    }

    void PrepareMeasurementFrame(bool enabled)
    {
        var scene = _scene!;
        scene.Post.Atmosphere.Enabled = enabled;
        SunCycle.Apply(SunCycle.Evaluate(.5f, Cycle), scene.Post, scene.CameraOverride!.Eye);
        scene.Begin();
        scene.Draw(_floor, Matrix4x4.CreateTranslation(0, -.1f, 0), new Color(.3f, .3f, .3f));
        scene.Draw(_box, Matrix4x4.CreateTranslation(-3, .5f, 7), Color.White);
        scene.PrepareFrame();
    }

    double CompletedMeasurementFrame()
    {
        long start = Stopwatch.GetTimestamp();
        using (GpuRecording.Open(_gpu!.GpuDevice, _commands!, "AtmosphereGoldenScene.MeasureSingleFrame"))
            _scene!.RenderInternal(_commands!, _width, _height, _framebuffer!);
        _gpu.GpuDevice.Submit(_commands!);
        _gpu.GpuDevice.WaitForIdle();
        return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    public double MeasurementColdCacheMilliseconds { get; private set; }

    internal static void WriteCapture(string name, byte[] rgba, int width = Width, int height = Height,
        [CallerFilePath] string source = "")
    {
        string directory = Environment.GetEnvironmentVariable("KE_GOLDEN_EVIDENCE_DIR")
            ?? Path.Combine(Path.GetDirectoryName(source)!, "goldens-evidence");
        Directory.CreateDirectory(directory);
        string backend = GoldenCompare.GoldenBackendToken(GpuBackendSelector.Select());
        KhaozEngine.Imaging.PngWriter.Save(Path.Combine(directory, $"{name}.{backend}.got.png"), rgba, width, height);
    }

    static TerrainLayeredMaterial WhiteTerrain()
    {
        static TerrainMaterialLayer Layer() => new()
        {
            AlbedoRgba = new byte[] { 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255 },
            NormalRgba = new byte[] { 128, 128, 255, 255, 128, 128, 255, 255, 128, 128, 255, 255, 128, 128, 255, 255 },
            Tint = Color.White,
            Roughness = 1,
        };
        return new TerrainLayeredMaterial
        {
            Width = 2,
            Height = 2,
            Grass = Layer(),
            Dirt = Layer(),
            Rock = Layer(),
            Sand = Layer(),
            Snow = Layer(),
            BaseSpecStrength = 0,
        };
    }

    void EnsureReceivers()
    {
        if (_receivers.Count > 0) return;
        var scene = _scene!;
        var vertices = new ModelVertex[]
        {
            new(new(-1, -1, 0), Vector3.UnitY, Vector4.One),
            new(new(1, -1, 0), Vector3.UnitY, Vector4.One),
            new(new(1, 1, 0), Vector3.UnitY, Vector4.One),
            new(new(-1, 1, 0), Vector3.UnitY, Vector4.One),
        };
        ushort[] indices = { 0, 2, 1, 0, 3, 2 };
        _receivers["rigid"] = scene.LoadMesh(new GltfMesh(vertices, indices));
        var skinVertices = new SkinnedVertex[vertices.Length];
        for (int i = 0; i < vertices.Length; i++) skinVertices[i] = new SkinnedVertex
        {
            Position = vertices[i].Position,
            Normal = Vector3.UnitY,
            Color = Vector4.One,
            BoneWeights = Vector4.UnitX,
        };
        var bones = new[] { Matrix4x4.Identity };
        _skin = scene.LoadSkinnedMesh(new SkinnedGltfMesh(skinVertices, indices, bones, bones));
        var splat = (ModelVertex[])vertices.Clone();
        for (int i = 0; i < splat.Length; i++) splat[i].Color = Vector4.UnitX;
        _receivers["splat"] = scene.LoadMesh(new GltfMesh(splat, indices), scene.LoadTerrainMaterial(WhiteTerrain()));
        var tile = (ModelVertex[])splat.Clone();
        for (int i = 0; i < tile.Length; i++) tile[i].Tangent = new Vector4(0, 0, 1, 0);
        var material = scene.LoadTileGroundMaterial(2, 2,
            new[] { new TileGroundLayerImage { AlbedoRgba = WhiteTerrain().Grass.AlbedoRgba } }, baseSpecStrength: 0);
        _receivers["tile"] = scene.LoadMesh(new GltfMesh(tile, indices), material);
        _foliage = scene.CreateFoliageBatch(new[]
        {
            new FoliageInstance(_receivers["rigid"], Matrix4x4.CreateScale(20) * Matrix4x4.CreateTranslation(32, 2, 100), 0),
            new FoliageInstance(_receivers["rigid"], Matrix4x4.CreateScale(200) * Matrix4x4.CreateTranslation(-320, 2, 1000), 0),
        });
    }

    public byte[] Capture(float timeOfDay, bool atmosphere = true, Vector3 origin = default)
        => Capture(scene =>
        {
            TimeOfDay = timeOfDay;
            scene.Post.Atmosphere.Enabled = atmosphere;
            scene.RenderOrigin = origin;
            if (timeOfDay == .5f && atmosphere)
                ((FlyCamera3D)scene.CameraOverride!).Pitch = 1.25f;
            if (timeOfDay == 0)
            {
                Cycle.MoonHourOffset = 7.5f;
                ((FlyCamera3D)scene.CameraOverride!).Yaw = MathF.PI / 2;
            }
        });

    /// <summary>Configure a reset scene, apply its cycle at the absolute camera position, then capture.</summary>
    public byte[] Capture(Action<Scene3D> configure)
        => Capture(configure, water: false);

    /// <summary>Capture a Y=0 sea above a submerged floor, with the same reset and cycle as sky captures.</summary>
    public byte[] CaptureWater(Action<Scene3D> configure)
        => Capture(configure, water: true);

    /// <summary>Capture submitted effects, optionally over an opaque receiver instead of the sky.</summary>
    public byte[] CaptureEffects(Action<Scene3D> configure, Action<Scene3D> draw, bool opaqueReceiver = false)
        => Capture(configure, water: false, scene =>
        {
            // Beams and trails precede the sky without writing depth. Keep #1153 outside this fixture.
            if (opaqueReceiver)
                scene.Draw(_box, Matrix4x4.CreateScale(26000, 22000, 1000)
                    * Matrix4x4.CreateTranslation(0, 0, 22000), Color.Black);
            draw(scene);
        });

    byte[] Capture(Action<Scene3D> configure, bool water, Action<Scene3D>? draw = null)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var gd = Device();
        var scene = _scene!;
        Reset(scene);
        configure(scene);
        SunCycle.Apply(SunCycle.Evaluate(TimeOfDay, Cycle), scene.Post, scene.CameraOverride!.Eye);
        for (int i = 0; i < Frames; i++)
        {
            scene.Begin();
            if (draw is not null) draw(scene);
            else if (water)
            {
                scene.Draw(_floor, Matrix4x4.CreateScale(7.5f) * Matrix4x4.CreateTranslation(0, -6, -20),
                    new Color(.1f, .12f, .14f));
                scene.DrawWater(new WaterPlane(0, 0, -20, 30));
            }
            else
            {
                scene.Draw(_floor, Matrix4x4.CreateTranslation(0, -.1f, 0), new Color(.3f, .3f, .3f));
                scene.Draw(_box, Matrix4x4.CreateTranslation(-3, .5f, 7), Color.White);
            }
            scene.PrepareFrame();
            using (GpuRecording.Open(gd, _commands!, "AtmosphereGoldenScene.Capture"))
                scene.RenderInternal(_commands!, _width, _height, _framebuffer!);
            gd.Submit(_commands!);
        }
        gd.WaitForIdle();
        return GpuReadback.ToRgba(gd, _target!, _width, _height);
    }

    /// <summary>The same capture through a new device and scene, without retained resource history.</summary>
    public byte[] CaptureFresh(float timeOfDay, bool atmosphere = true, Vector3 origin = default)
    {
        using var fresh = new AtmosphereGoldenScene();
        return fresh.Capture(timeOfDay, atmosphere, origin);
    }

    /// <summary>The same effect controls and receiver on a new scene without retained HDR or fog state.</summary>
    public byte[] CaptureFresh(Action<Scene3D> configure, Action<Scene3D> draw, bool opaqueReceiver = false)
    {
        using var fresh = new AtmosphereGoldenScene();
        return fresh.CaptureEffects(configure, draw, opaqueReceiver);
    }

    void Reset(Scene3D scene)
    {
        // Copy every public settings field so later fixture users cannot leak an unrelated post knob.
        // Private CPU caches remain owned by the scene and refresh from the new live settings.
        var defaults = new PixelPostProcessSettings();
        foreach (var field in PostFields) field.SetValue(scene.Post, field.GetValue(defaults));
        scene.Post.AtmosphereCycle = null;
        scene.Post.UseSmoothPreset();
        scene.Post.RenderScale = RenderScale.FixedInternal;
        scene.Post.RenderWidth = _width;
        scene.Post.RenderHeight = _height;
        scene.Post.Quality.AntiAliasing = AntiAliasing.Off;
        scene.Post.Hdr.Enabled = true;
        scene.Post.Hdr.Exposure = 1;
        scene.Post.Hdr.Operator = TonemapOperator.AcesFilmic;
        scene.Post.Sky.Enabled = true;
        scene.Post.Atmosphere.Enabled = true;
        scene.CameraOverride = new FlyCamera3D
        {
            Position = new Vector3(0, 2, 9),
            Yaw = -MathF.PI / 2,
            Pitch = -.08f,
            FieldOfView = MathF.PI / 3,
            AspectRatio = (float)_width / _height,
        };
        scene.RenderOrigin = Vector3.Zero;
        scene.EffectTimeSeconds = 0;
        TimeOfDay = .5f;
        Cycle = EquatorialCycle();
    }

    static SunCycleSettings EquatorialCycle() => new()
    {
        LatitudeDegrees = 0,
        SolarDeclinationDegrees = 0,
        MoonDeclinationDegrees = 0,
        MoonHourOffset = 12,
        NightKey = NightKeyMode.Moon,
    };

    IGpuDevice Device()
    {
        if (_gpu is not null) return _gpu.GpuDevice;
        _gpu = GpuDeviceContext.CreateHeadless();
        var gd = _gpu.GpuDevice;
        var factory = gd.Factory;
        _target = factory.CreateTexture(GpuTextureDescription.Texture2D((uint)_width, (uint)_height,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        _framebuffer = factory.CreateFramebuffer(null, _target);
        _commands = factory.CreateCommandList();
        _scene = new Scene3D(gd, _framebuffer.Outputs, null);
        _floor = _scene.LoadMesh(MeshPrimitives.Tile(8));
        _box = _scene.LoadMesh(MeshPrimitives.Box());
        return gd;
    }

    public void Dispose()
    {
        _foliage?.Dispose();
        _commands?.Dispose();
        _scene?.Dispose();
        _framebuffer?.Dispose();
        _target?.Dispose();
        _gpu?.Dispose();
        _commands = null;
        _scene = null;
        _framebuffer = null;
        _target = null;
        _gpu = null;
    }
}
