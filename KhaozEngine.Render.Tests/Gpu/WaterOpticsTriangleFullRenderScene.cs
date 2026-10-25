using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using KhaozEngine.Gpu;
using KhaozEngine.Imaging;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

public enum WaterOpticsFullRenderMode { Opaque, Bilinear, Triangles2, Legacy, Triangles32 }

public sealed record WaterOpticsFullFrameTiming(double PrepareCpuMs, double EncodeCpuMs,
    double SubmitAndCompletionWallMs, double? CompletedFrameGpuMs, string GpuTimingStatus,
    double CpuTransparentsMs, double CpuPostMs);

public sealed record WaterOpticsFullCapture(WaterOpticsFullRenderMode Mode, WaterOpticsImage Image,
    WaterOpticsFullFrameTiming ColdFrame, WaterOpticsFullFrameTiming WarmFrame, int MaximumBinOccupancy);

/// <summary>Fixed synthetic full-scene acceptance captures. One cold and one measured frame per mode,
/// with no benchmark rounds or repeated readiness frames. The scene is initialized lazily for GPU facts.</summary>
public sealed class WaterOpticsTriangleFullRenderScene : IDisposable
{
    public const int Size = 384;
    public const float FeatherMetres = .20f;
    public static readonly Vector3 WaterRgb = new(.05f, .40f, .75f);
    public const string OutputDirectory = "/tmp/khaozengine-water-triangles/task-2/full-render";
    static readonly Color GroundColor = new(.36f, .29f, .20f, 1);
    static readonly Vector3 FieldOrigin = new(-6, 0, -6);
    static readonly Vector2 FieldSize = new(12);

    GpuDeviceContext? _gpu;
    Scene3D? _scene;
    FlyCamera3D? _camera;
    IGpuCommandList? _commands;
    IGpuTexture? _target;
    IGpuFramebuffer? _framebuffer;
    MeshHandle _plane, _box;
    WaterOpticsHandle _bilinear, _two, _thirtyTwo;
    IReadOnlyDictionary<WaterOpticsFullRenderMode, WaterOpticsFullCapture>? _captures;
    ExceptionDispatchInfo? _captureFailure;
    bool _disposed;

    public string Backend => Device.Backend.ToString();
    public string DeviceName => Device.Capabilities.DeviceName;
    public double DeviceCreationMs { get; private set; }
    public double SceneConstructionIncludingShaderCompilationMs { get; private set; }
    public double FieldConstructionAndUploadMs { get; private set; }
    public string DirectoryPath => Path.Combine(OutputDirectory, Backend);
    IGpuDevice Device { get { EnsureScene(); return _gpu!.GpuDevice; } }

    public IReadOnlyDictionary<WaterOpticsFullRenderMode, WaterOpticsFullCapture> Captures
    {
        get
        {
            _captureFailure?.Throw();
            if (_captures is not null) return _captures;
            try
            {
                var captures = new Dictionary<WaterOpticsFullRenderMode, WaterOpticsFullCapture>();
                // Fixed order also defines the aged state used by the fresh-scene parity fact.
                foreach (WaterOpticsFullRenderMode mode in Enum.GetValues<WaterOpticsFullRenderMode>())
                    captures.Add(mode, Capture(mode));
                _captures = captures;
                WriteContactSheet(captures.Values);
                return captures;
            }
            catch (Exception error)
            {
                // The other fact reports the same failure without repeating a failed capture set.
                _captureFailure = ExceptionDispatchInfo.Capture(error);
                throw;
            }
        }
    }

    public WaterOpticsFullCapture Capture(WaterOpticsFullRenderMode mode, string? label = null)
    {
        EnsureScene();
        WaterOpticsFullFrameTiming cold = Frame(mode, measured: false);
        WaterOpticsFullFrameTiming warm = Frame(mode, measured: true);
        var image = new WaterOpticsImage(GpuReadback.ToRgba(Device, _target!, Size, Size), Size, Size, Samples());
        int occupancy = mode == WaterOpticsFullRenderMode.Triangles32 ? 32
            : mode == WaterOpticsFullRenderMode.Triangles2 ? 2 : 0;
        var result = new WaterOpticsFullCapture(mode, image, cold, warm, occupancy);
        string name = label ?? mode.ToString();
        Directory.CreateDirectory(DirectoryPath);
        PngWriter.Save(Path.Combine(DirectoryPath, name + ".png"), image.Rgba, Size, Size);
        File.WriteAllText(Path.Combine(DirectoryPath, name + ".json"), JsonSerializer.Serialize(new
        {
            Backend,
            DeviceName,
            Width = Size,
            Height = Size,
            Mode = mode.ToString(),
            MaximumBinOccupancy = occupancy,
            SupportsTimestamps = Device.Capabilities.SupportsTimestamps,
            SupportsCompletedFrameTail = _commands is IGpuCommandListTimestampEnd,
            DeviceCreationMs,
            SceneConstructionIncludingShaderCompilationMs,
            FieldConstructionAndUploadMs,
            ColdFrame = cold,
            WarmFrame = warm,
            PerWaterPassGpuMs = (double?)null,
            PerWaterPassLimitation = "Scene3D exposes no per-water GPU bracket. CompletedFrameGpuMs covers the complete frame, not a water-only interval.",
            FramesRendered = 2,
            WarmupFrames = 1,
            MeasuredFrames = 1,
            FieldOrigin = new[] { -6f, 0f, -6f },
            FieldSize = new[] { 12f, 12f },
            WaterHalfExtents = new[] { 3.75f, 2.75f },
            FeatherMetres,
            Samples = image.Samples,
            OpticalStats = _scene!.WaterOpticsStats
        }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        return result;
    }

    public WaterOpticsImage CaptureMixed(WaterGridMode grid, bool reverse, bool bilinearControl)
    {
        EnsureScene();
        Frame(WaterOpticsFullRenderMode.Triangles2, measured: false, grid, reverse, bilinearControl);
        var image = new WaterOpticsImage(GpuReadback.ToRgba(Device, _target!, Size, Size), Size, Size, Samples());
        string name = $"Mixed-{grid}-{reverse}-{bilinearControl}";
        Directory.CreateDirectory(DirectoryPath);
        PngWriter.Save(Path.Combine(DirectoryPath, name + ".png"), image.Rgba, Size, Size);
        return image;
    }

    WaterOpticsFullFrameTiming Frame(WaterOpticsFullRenderMode mode, bool measured,
        WaterGridMode? mixedGrid = null, bool reverse = false, bool bilinearControl = false)
    {
        IGpuDevice device = Device;
        Scene3D scene = _scene!;
        scene.Begin();
        scene.EffectTimeSeconds = 0;
        scene.Post.Water.GridMode = mixedGrid ?? WaterGridMode.CameraFocused;
        Matrix4x4 ramp = Matrix4x4.Identity;
        ramp.M12 = .5f;
        // The same original affine bed is rendered in every capture. Neither topology nor feather changes.
        scene.Draw(_plane, Matrix4x4.CreateScale(12, 1, 12) * ramp
            * Matrix4x4.CreateTranslation(0, -1, 0), GroundColor);
        // Its top is 0.08 m underwater, but its underlying affine optical bed is 1.5 m deep.
        scene.Draw(_box, Matrix4x4.CreateScale(.8f, .4f, .8f)
            * Matrix4x4.CreateTranslation(-1, -.28f, .5f), GroundColor);
        if (mode != WaterOpticsFullRenderMode.Opaque)
        {
            WaterOpticsHandle handle = mode switch
            {
                WaterOpticsFullRenderMode.Bilinear => _bilinear,
                WaterOpticsFullRenderMode.Triangles2 => _two,
                WaterOpticsFullRenderMode.Triangles32 => _thirtyTwo,
                _ => default
            };
            var binding = handle.IsValid ? new WaterOpticsBinding(handle, FieldOrigin, FieldSize) : default;
            if (mixedGrid is null) scene.DrawWater(new(0, 0, 0, 3.75f, 2.75f, optics: binding));
            else
            {
                // Four adjacent strips exercise legacy, triangle, bilinear, triangle on ONE geometry route.
                // The control replaces only triangle payloads with the independent affine bilinear field.
                for (int ordinal = 0; ordinal < 4; ordinal++)
                {
                    int i = reverse ? 3 - ordinal : ordinal;
                    WaterOpticsHandle field = i switch
                    {
                        0 => default,
                        2 => _bilinear,
                        _ => bilinearControl ? _bilinear : _two
                    };
                    var optics = field.IsValid ? new WaterOpticsBinding(field, FieldOrigin, FieldSize) : default;
                    scene.DrawWater(new(-2.8125f + i * 1.875f, 0, 0, .9375f, 2.75f,
                        look: new WaterLook { SwellAmplitude = .01f, SwellSteepness = 0f }, optics: optics));
                }
            }
        }

        long start = Stopwatch.GetTimestamp();
        scene.PrepareFrame(Size, Size);
        double prepare = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        bool timestamp = measured && device.Capabilities.SupportsTimestamps
            && _commands is IGpuCommandListTimestampEnd;
        using IGpuTimestampPool? pool = timestamp ? device.Factory.CreateTimestampPool(2) : null;
        using IGpuFence? completion = timestamp ? device.Factory.CreateFence() : null;
        start = Stopwatch.GetTimestamp();
        using (GpuRecording.Open(device, _commands!, "WaterOpticsTriangleFullRenderScene.Frame"))
        {
            if (pool is not null)
            {
                _commands!.BeginTimestamps(pool, 2);
                _commands.WriteTimestamp(pool, 0);
            }
            scene.RenderInternal(_commands!, Size, Size, _framebuffer!);
            if (pool is not null)
                ((IGpuCommandListTimestampEnd)_commands!).FinishTimestampsOnEnd(pool, 1);
        }
        double encode = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        start = Stopwatch.GetTimestamp();
        if (completion is not null) device.Submit(_commands!, completion);
        else device.Submit(_commands!);
        device.WaitForIdle();
        double wall = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        double? gpuMs = null;
        string status = measured ? "Unsupported completed-frame timestamps" : "Cold frame, not timestamped";
        if (pool is not null)
        {
            Span<ulong> ticks = stackalloc ulong[2];
            GpuTimestampStatus resolved = pool.TryResolve(completion!, ticks, out double period);
            status = resolved.ToString();
            if (resolved == GpuTimestampStatus.Ready && double.IsFinite(period) && period > 0)
                gpuMs = ticks[1] * period / 1_000_000;
        }
        device.Present();
        return new(prepare, encode, wall, gpuMs, status, scene.PassTimingsMs.TransparentsMs, scene.PassTimingsMs.PostMs);
    }

    IReadOnlyDictionary<string, Vector2> Samples()
    {
        var positions = new Dictionary<string, Vector3>
        {
            ["wet"] = new(-2, 0, 1.2f),       // literal rest depth 2, expected coverage 1
            ["object"] = new(-1, 0, .5f),    // literal bed 1.5, object top only 0.08 below water
            ["feather"] = new(1.84f, 0, .5f), // literal rest depth 0.08, smoothstep(0.4) = 0.352
            ["dry"] = new(2.5f, .25f, .5f), // literal rest depth -0.25, opaque terrain occludes water
            ["coverage"] = new(-3, 0, 1.5f), // inside the submitted water footprint, depth 2.5
            ["outside"] = new(0, 0, -3.2f) // a camera ray outside the submitted water footprint
        };
        var samples = new Dictionary<string, Vector2>();
        foreach ((string name, Vector3 point) in positions)
        {
            if (!_camera!.WorldToScreen(point, Size, Size, out Vector2 pixel))
                throw new InvalidOperationException($"The {name} acceptance probe is behind the camera.");
            samples.Add(name, pixel);
        }
        return samples;
    }

    void EnsureScene()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_scene is not null) return;
        long start = Stopwatch.GetTimestamp();
        _gpu = GpuDeviceContext.CreateHeadless();
        DeviceCreationMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        IGpuDevice device = _gpu.GpuDevice;
        _target = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(Size, Size,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        _framebuffer = device.Factory.CreateFramebuffer(null, _target);
        _commands = device.Factory.CreateCommandList();
        start = Stopwatch.GetTimestamp();
        _scene = new Scene3D(device, _framebuffer.Outputs);
        SceneConstructionIncludingShaderCompilationMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        _plane = _scene.LoadMesh(MeshPrimitives.Plane());
        _box = _scene.LoadMesh(MeshPrimitives.Box());
        _camera = new FlyCamera3D { AspectRatio = 1, NearPlane = .1f, FarPlane = 100, Position = new(0, 6, 8) };
        Vector3 direction = Vector3.Normalize(new Vector3(0, -.2f, 0) - _camera.Position);
        _camera.Yaw = MathF.Atan2(direction.X, direction.Z);
        _camera.Pitch = MathF.Asin(direction.Y);
        _scene.CameraOverride = _camera;
        _scene.RenderOrigin = Vector3.Zero;
        _scene.EnableTiming = true;
        _scene.Post.Hdr.Enabled = false;
        _scene.Post.Atmosphere.Enabled = false;
        _scene.Post.Bloom.Enabled = false;
        _scene.Post.Sky.Enabled = false;
        _scene.Post.Starfield = false;
        _scene.Post.Outline = false;
        _scene.Post.Quantize = _scene.Post.Dither = false;
        _scene.Post.Background = BackgroundMode.Solid;
        _scene.Post.BackgroundColor = new(.04f, .05f, .08f, 1);
        _scene.Post.RenderScale = RenderScale.MatchViewport;
        _scene.Post.Quality.AntiAliasing = AntiAliasing.Off;
        _scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        _scene.Post.LightColor = _scene.Post.FillLightColor = new(0, 0, 0, 1);
        _scene.Post.AmbientColor = Color.White;
        var waterColor = new Color(WaterRgb.X, WaterRgb.Y, WaterRgb.Z, 1);
        _scene.Post.Water = new WaterSettings
        {
            WaveSource = WaterWaveSource.Procedural,
            SwellAmplitude = 0,
            NormalStrength = 0,
            ShoreFadeDistance = FeatherMetres,
            FoamStrength = 0,
            GlintStrength = 0,
            SkyReflectionStrength = 0,
            DeepColor = waterColor,
            ShallowColor = waterColor,
            HorizonColor = waterColor,
            Opacity = 1,
            AbsorptionPerMetre = new Color(0, 0, 0, 0)
        };
        start = Stopwatch.GetTimestamp();
        _bilinear = _scene.LoadWaterOpticsField(new(2, 2, [4, -2, 4, -2]));
        WaterOpticsTriangleField two = TriangleField(1);
        WaterOpticsTriangleField thirtyTwo = TriangleField(16);
        if (two.MaximumBinOccupancy != 2 || thirtyTwo.MaximumBinOccupancy != 32)
            throw new InvalidOperationException("The full-render source candidate counts changed.");
        _two = _scene.LoadWaterOpticsTriangles(two);
        _thirtyTwo = _scene.LoadWaterOpticsTriangles(thirtyTwo);
        FieldConstructionAndUploadMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    static WaterOpticsTriangleField TriangleField(int strips)
    {
        var triangles = new WaterOpticsTriangle[2 * strips];
        for (int i = 0; i < strips; i++)
        {
            float x = 12f * i / strips, next = 12f * (i + 1) / strips;
            float a = 4 - x * .5f, b = 4 - next * .5f;
            triangles[2 * i] = new(new(x, a, 0), new(next, b, 0), new(next, b, 12));
            triangles[2 * i + 1] = new(new(x, a, 0), new(next, b, 12), new(x, a, 12));
        }
        // One bin contains all genuine partitions. For K32, the first six triangles lie entirely
        // left of the submitted water, so every visible water hit must traverse beyond ordinal zero.
        return new(FieldSize, 1, 1, triangles, [new(Vector2.Zero, FieldSize)]);
    }

    public void WriteContactSheet(IEnumerable<WaterOpticsFullCapture> captures, WaterOpticsFullCapture? fresh = null)
    {
        const int columns = 3, rows = 2;
        var rgba = new byte[columns * rows * Size * Size * 4];
        var names = new List<string>();
        void Add(WaterOpticsFullCapture capture, string name)
        {
            int index = names.Count;
            names.Add(name);
            for (int y = 0; y < Size; y++)
                Buffer.BlockCopy(capture.Image.Rgba, y * Size * 4, rgba,
                    (((index / columns) * Size + y) * columns * Size + (index % columns) * Size) * 4, Size * 4);
        }
        foreach (WaterOpticsFullCapture capture in captures) Add(capture, capture.Mode.ToString());
        if (fresh is not null) Add(fresh, "Fresh Triangles32");
        Directory.CreateDirectory(DirectoryPath);
        PngWriter.Save(Path.Combine(DirectoryPath, "contactsheet.png"), rgba, columns * Size, rows * Size);
        File.WriteAllText(Path.Combine(DirectoryPath, "contactsheet-order.json"), JsonSerializer.Serialize(names));
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
