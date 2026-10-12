using System;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

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

    /// <summary>Reset before configuration. Edit this inside the capture callback.</summary>
    public SunCycleSettings Cycle { get; private set; } = EquatorialCycle();

    /// <summary>Reset to noon before configuration, then evaluated after the callback.</summary>
    public float TimeOfDay { get; set; } = .5f;

    public byte[] Capture(float timeOfDay, bool atmosphere = true, Vector3 origin = default)
        => Capture(scene =>
        {
            TimeOfDay = timeOfDay;
            scene.Post.Atmosphere.Enabled = atmosphere;
            scene.RenderOrigin = origin;
            if (timeOfDay == 0)
            {
                Cycle.MoonHourOffset = 7.5f;
                ((FlyCamera3D)scene.CameraOverride!).Yaw = MathF.PI / 2;
            }
        });

    /// <summary>Configure a reset scene, apply its cycle at the absolute camera position, then capture.</summary>
    public byte[] Capture(Action<Scene3D> configure)
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
            scene.Draw(_floor, Matrix4x4.CreateTranslation(0, -.1f, 0), new Color(.3f, .3f, .3f));
            scene.Draw(_box, Matrix4x4.CreateTranslation(-3, .5f, 7), Color.White);
            scene.PrepareFrame();
            using (GpuRecording.Open(gd, _commands!, "AtmosphereGoldenScene.Capture"))
                scene.RenderInternal(_commands!, Width, Height, _framebuffer!);
            gd.Submit(_commands!);
        }
        gd.WaitForIdle();
        return GpuReadback.ToRgba(gd, _target!, Width, Height);
    }

    /// <summary>The same capture through a new device and scene, without retained resource history.</summary>
    public byte[] CaptureFresh(float timeOfDay, bool atmosphere = true, Vector3 origin = default)
    {
        using var fresh = new AtmosphereGoldenScene();
        return fresh.Capture(timeOfDay, atmosphere, origin);
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
        scene.Post.RenderWidth = Width;
        scene.Post.RenderHeight = Height;
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
            AspectRatio = (float)Width / Height,
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
        _target = factory.CreateTexture(GpuTextureDescription.Texture2D(Width, Height,
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
