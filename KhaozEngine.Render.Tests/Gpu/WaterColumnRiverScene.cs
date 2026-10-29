using System;
using System.IO;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Lazy, synthetic river. Construction and disposal of a skipped class never create a GPU device.</summary>
public sealed class WaterColumnRiverScene : IDisposable
{
    public const int Size = 512;
    public static readonly Color Sand = new(0.65f, 0.58f, 0.42f);
    public static string Scratch => Path.Combine(OperatingSystem.IsWindows() ? Path.GetTempPath() : "/tmp",
        "khaozengine-water-column-task8");
    GpuDeviceContext? _gpu;
    Scene3D? _scene;
    IGpuTexture? _color;
    IGpuFramebuffer? _target;
    IGpuCommandList? _commands;
    MeshHandle _plane, _box;
    WaterOpticsHandle _field;
    float _fieldDepth = float.NaN;
    int _size, _shadowResolution = 2048, _shadowCascades = 1;
    WaterColumnCase? _previous;

    public WaterColumnImage CaptureFresh(WaterColumnCase settings)
    {
        using var fresh = new WaterColumnRiverScene();
        return fresh.Capture(settings);
    }

    public WaterColumnImage Capture(WaterColumnCase settings, int warmupFrames = 2)
    {
        Ensure(settings.Size);
        Scene3D scene = _scene!;
        IGpuDevice device = _gpu!.GpuDevice;
        IIsoCamera3D camera = Camera(settings);
        Configure(scene, settings, camera);
        for (int frame = 0; frame < Math.Max(1, warmupFrames); frame++)
        {
            scene.Begin();
            scene.EffectTimeSeconds = 0f;
            Draw(scene, settings);
            scene.PrepareFrame(settings.Size, settings.Size);
            using (GpuRecording.Open(device, _commands!, nameof(WaterColumnRiverScene)))
                scene.RenderInternal(_commands!, settings.Size, settings.Size, _target!);
            device.Submit(_commands!);
            device.WaitForIdle();
            device.Present();
        }
        _previous = settings;
        RenderResources resources = Borrow<RenderResources>("_res");
        Assert.Equal(settings.Aa.Mode == AntiAliasingMode.Msaa ? 4 : 1, resources.SampleCount);
        Assert.Equal(settings.Size, resources.Width);
        float[] linear = ReadRgb(resources.ColorTex);
        WaterColumnColorSource source = Borrow<WaterColumnColorSource>("_waterColumnColor");
        float[] copied = scene.WaterColumnLightDiagnostics.CopyRan ? ReadRgb(source.Texture!) : [];
        float[] resolved = settings.Aa.Mode == AntiAliasingMode.Temporal
            ? ReadRgb(scene.TemporalHistory.Color(scene.TemporalHistory.WriteIndex)) : linear;
        return new WaterColumnImage(GpuReadback.ToRgba(device, _color!, _size, _size), linear,
            camera.ViewProjection, scene.WaterColumnLightDiagnostics, settings, copied, resolved);
    }

    T Borrow<T>(string field) => (T)typeof(Scene3D).GetField(field,
        BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_scene)!;

    float[] ReadRgb(IGpuTexture texture)
    {
        int count = _size * _size;
        float[] rgb = new float[count * 3];
        if (texture.Format == GpuPixelFormat.R16G16B16A16Float)
        {
            float[] rgba = TemporalTextureIo.Read(_gpu!.GpuDevice, texture);
            for (int i = 0; i < count; i++)
                for (int c = 0; c < 3; c++) rgb[i * 3 + c] = rgba[i * 4 + c];
        }
        else
        {
            Assert.Equal(GpuPixelFormat.R8G8B8A8UNorm, texture.Format);
            byte[] rgba = GpuReadback.ToRgba(_gpu!.GpuDevice, texture, _size, _size);
            for (int i = 0; i < count; i++)
                for (int c = 0; c < 3; c++) rgb[i * 3 + c] = rgba[i * 4 + c] / 255f;
        }
        return rgb;
    }

    static IIsoCamera3D Camera(WaterColumnCase s)
    {
        Vector3 eye = s.Eye ?? (s.TopDown ? new Vector3(0f, 10f, 0f) : new Vector3(3f, 3f, 5f));
        Vector3 target = new(0f, -0.2f, 0f);
        return new RiverCamera(eye, target, s.Origin, s.Orthographic) { RenderOrigin = s.Origin };
    }

    // Build orientation before translation. Adding a unit Forward to an eye at 2^22 rounds its direction.
    // A fixed look-at also permits a genuinely vertical calibration ray without a fly-camera pitch clamp.
    sealed class RiverCamera(Vector3 eye, Vector3 target, Vector3 origin, bool orthographic) : IIsoCamera3D, IRenderOriginAware
    {
        readonly Vector3 _up = MathF.Abs(Vector3.Normalize(target - eye).Y) > 0.9999f ? -Vector3.UnitZ : Vector3.UnitY;
        public Vector3 RenderOrigin { get; set; }
        public Vector3 Eye => eye + origin;
        public Vector3 Forward => Vector3.Normalize(target - eye);
        public Matrix4x4 Projection => orthographic ? Matrix4x4.CreateOrthographic(6f, 6f, 0.05f, 80f)
            : Matrix4x4.CreatePerspectiveFieldOfView(0.7f, 1f, 0.05f, 80f);
        public Matrix4x4 View => Matrix4x4.CreateLookAt(eye + (origin - RenderOrigin), target + (origin - RenderOrigin), _up);
        public Matrix4x4 ViewProjection => View * Projection;
        public Matrix4x4 AbsoluteViewProjection => Matrix4x4.CreateTranslation(-origin)
            * Matrix4x4.CreateLookAt(eye, target, _up) * Projection;
        public bool WorldToScreen(Vector3 world, int width, int height, out Vector2 pixel)
        {
            Vector4 p = Vector4.Transform(new Vector4(world - origin, 1f),
                Matrix4x4.CreateLookAt(eye, target, _up) * Projection);
            pixel = new Vector2((p.X / p.W + 1f) * width * 0.5f, (1f - p.Y / p.W) * height * 0.5f);
            return p.W > 0f;
        }
    }

    void Configure(Scene3D scene, WaterColumnCase s, IIsoCamera3D camera)
    {
        if (_previous != s) scene.CameraCut();
        scene.CameraOverride = camera;
        scene.RenderOrigin = s.Origin;
        scene.Post.RenderScale = RenderScale.MatchViewport;
        scene.Post.Quality.AntiAliasing = s.Aa;
        scene.Post.Temporal.Upscale = TemporalUpscale.Native;
        scene.Post.Hdr.Enabled = s.Hdr;
        scene.Post.Hdr.Exposure = 1f;
        scene.Post.Bloom.Enabled = false;
        scene.Post.Quantize = scene.Post.Dither = scene.Post.Outline = false;
        scene.Post.CelBands = 0;
        scene.Post.Starfield = scene.Post.Sky.Enabled = false;
        scene.Post.Background = BackgroundMode.Solid;
        scene.Post.BackgroundColor = new Color(0.1f, 0.05f, 0.15f);
        scene.Post.Atmosphere.Enabled = s.Atmosphere;
        float elevation = (s.ReferenceLight ? 90f : s.SunElevationDegrees) * MathF.PI / 180f;
        scene.Post.LightDirection = new Vector3(0f, -MathF.Sin(elevation), MathF.Cos(elevation));
        scene.Post.LightColor = s.AmbientOnly || elevation < 0f ? Color.Black : Color.White;
        scene.Post.AmbientColor = s.ReferenceLight ? Color.White : new Color(0.1f, 0.1f, 0.1f);
        scene.Post.FillLightColor = Color.Black;
        if (_shadowResolution != s.ShadowResolution || _shadowCascades != s.ShadowCascades)
        {
            Assert.True(scene.ReplaceShadowLayout(s.ShadowResolution, s.ShadowCascades));
            _shadowResolution = s.ShadowResolution;
            _shadowCascades = s.ShadowCascades;
        }
        ShadowSettings shadows = scene.Post.Quality.Shadows;
        shadows.Mode = ShadowMode.ShadowMap;
        shadows.ShadowStrength = s.ShadowStrength;
        shadows.ShadowNearDistance = 20f;
        shadows.ShadowMaxDistance = 70f;
        shadows.ShadowNormalOffset = 0.5f;
        shadows.ShadowConstantBias = shadows.ShadowSlopeBias = 0.00005f;
        scene.Post.Water = new WaterSettings
        {
            UnderwaterSunlight = !s.DryReference,
            WaterColumnLight = false,
            WaveSource = s.WaveSource,
            WaveScale = 0.8f,
            NormalStrength = s.Ripples && !s.ReferenceLight ? 0.05f : 0f,
            SwellAmplitude = 0f,
            SwellSteepness = 0f,
            ShoalingStrength = 0f,
            SurfStrength = 0f,
            GlintStrength = s.ReferenceLight ? 0f : s.GlintStrength,
            FoamStrength = s.ReferenceLight ? 0f : s.FoamStrength,
            FoamShoreWidth = 2f,
            SkyReflectionStrength = 0f,
            HorizonColor = Color.Black,
            ShoreFadeDistance = s.BedOnly ? float.MaxValue : 0f,
            Opacity = 0.5f,
            GridMode = WaterGridMode.Clipmap
        };
        if (s.WaveSource == WaterWaveSource.FftOcean)
            scene.Post.Water.SeaState = new WaterSeaState
            { WindSpeed = 0f, SwellAmount = 0f, Choppiness = 0f, CascadeCount = 1, CascadeResolution = 32 };
    }

    void Draw(Scene3D scene, WaterColumnCase s)
    {
        if (!s.MissingBed)
            scene.Draw(_plane, Matrix4x4.CreateScale(80f, 1f, 80f)
                * Matrix4x4.CreateTranslation(s.Origin + new Vector3(0f, -s.Depth, 0f)),
                s.BlackBed || s.ReferenceLight ? Color.Black : Sand, Material.None, castsShadows: false);
        if (s.Caster)
            scene.Draw(_box, Matrix4x4.CreateScale(1f, s.CasterTop - s.CasterBottom, 1f)
                * Matrix4x4.CreateTranslation(s.Origin + new Vector3(0f, (s.CasterTop + s.CasterBottom) * 0.5f, 0f)),
                Color.Black, Material.None);
        float depth = s.AuthoredDepth ?? s.Depth;
        if (s.Bound && _fieldDepth != depth)
        {
            if (float.IsFinite(_fieldDepth)) scene.UnloadWaterOpticsField(_field);
            _field = scene.LoadWaterOpticsField(new WaterOpticsField(2, 2, [depth, depth, depth, depth]));
            _fieldDepth = depth;
        }
        WaterLook Look(Color color, float clarity, bool enabled) => s.NullColumnFields ? new WaterLook() :
            new WaterLook { WaterColumnLight = enabled, WaterColor = color, Clarity = clarity };
        void Water(float x, float extent, WaterLook look)
        {
            var plane = new WaterPlane(s.Origin.X + x, s.Origin.Y, s.Origin.Z, extent, 6f, look,
                floorY: s.MissingBed ? null : s.Origin.Y - s.Depth - 0.1f);
            if (s.Bound)
                plane = plane.WithOptics(new WaterOpticsBinding(_field,
                    s.Origin + new Vector3(x - extent, 0f, -6f), new Vector2(extent * 2f, 12f)));
            scene.DrawWater(plane);
        }
        if (s.MultiplePlanes)
        {
            Water(-2f, 1f, Look(s.WaterColor, s.Clarity, s.Enabled));
            Water(0f, 1f, Look(new Color(0.04f, 0.35f, 0.65f), 0.4f, s.Enabled));
            Water(2f, 1f, Look(s.WaterColor, s.Clarity, false));
        }
        else Water(0f, 6f, Look(s.WaterColor, s.Clarity, s.Enabled));
    }

    void Ensure(int size)
    {
        if (_gpu is null)
        {
            _gpu = GpuDeviceContext.CreateHeadless();
            _commands = _gpu.GpuDevice.Factory.CreateCommandList();
        }
        if (_size == size) return;
        IGpuDevice device = _gpu.GpuDevice;
        device.WaitForIdle();
        _target?.Dispose();
        _color?.Dispose();
        _color = device.Factory.CreateTexture(GpuTextureDescription.Texture2D((uint)size, (uint)size,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        _target = device.Factory.CreateFramebuffer(null, _color);
        _size = size;
        if (_scene is not null) return;
        _scene = new Scene3D(device, _target.Outputs,
            new ShadowSettings { ShadowMapResolution = 2048, ShadowCascadeCount = 1 });
        _plane = _scene.LoadMesh(MeshPrimitives.Plane());
        _box = _scene.LoadMesh(MeshPrimitives.Box());
    }

    public void Dispose()
    {
        _gpu?.GpuDevice.WaitForIdle();
        _commands?.Dispose();
        _scene?.Dispose();
        _target?.Dispose();
        _color?.Dispose();
        _gpu?.Dispose();
    }
}
