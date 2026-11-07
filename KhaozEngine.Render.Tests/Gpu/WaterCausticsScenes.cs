using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Synthetic water arrangements. Every one sits inside the caustic window, well clear of its 4 m fade.</summary>
public enum WaterCausticsLayout
{
    /// <summary>One 12 m square body over a flat bed.</summary>
    Single,
    /// <summary>The same square cut in two at x = 0, both halves sharing the scene look.</summary>
    Split,
    /// <summary>A bed rising through the surface along +Z, so the waterline crosses the view at z = 0.</summary>
    Bank,
    /// <summary>Two overlapping bodies at different heights, each over its own half of the bed.</summary>
    Stacked,
    /// <summary>One body in the window plus two overlapping bodies 500 m away, outside it.</summary>
    SharedRiver,
    /// <summary>A body spanning x = -2 to 6 over a wider bed, so receivers near x = -2 enter beyond its edge.</summary>
    Narrow,
}

/// <summary>Immutable inputs of one capture. Positions are relative to <see cref="Origin"/>.</summary>
public sealed record WaterCausticsCase
{
    public WaterCausticsLayout Layout { get; init; }
    public bool Caustics { get; init; } = true;
    public float Strength { get; init; } = 1f;
    public float NormalStrength { get; init; } = 0.05f;
    /// <summary>The ripple's heading in radians, which is also its <c>RippleSeed</c>.</summary>
    public float Heading { get; init; } = 0.6f;
    /// <summary>The lower body's heading in <see cref="WaterCausticsLayout.Stacked"/>.</summary>
    public float LowerHeading { get; init; } = 2f;
    public float SunElevationDegrees { get; init; } = 50f;
    /// <summary>Direction the sunlight travels in XZ, in degrees from +X toward +Z.</summary>
    public float SunAzimuthDegrees { get; init; }
    public float ReceiverDepth { get; init; } = 1f;
    public float Time { get; init; }
    public float ShadowStrength { get; init; } = 1f;
    public bool Caster { get; init; }
    public bool Column { get; init; }
    public AntiAliasing Aa { get; init; } = AntiAliasing.Off;
    /// <summary>Absolute world position of the arrangement's centre on the still surface.</summary>
    public Vector3 Origin { get; init; }
    /// <summary>Render origin minus <see cref="Origin"/>.</summary>
    public Vector3 RenderOffset { get; init; }

    public Vector3 Sun
    {
        get
        {
            float elevation = SunElevationDegrees * MathF.PI / 180f, azimuth = SunAzimuthDegrees * MathF.PI / 180f;
            return Vector3.Normalize(new Vector3(MathF.Cos(elevation) * MathF.Cos(azimuth), -MathF.Sin(elevation),
                MathF.Cos(elevation) * MathF.Sin(azimuth)));
        }
    }

    /// <summary>The independent forward map of this case's ripple, or of the lower body's.</summary>
    internal CausticOracle Oracle(bool lower = false) => new(WaterCausticsScenes.Wavelength, NormalStrength,
        lower ? LowerHeading : Heading, Sun, Time * WaterCausticsScenes.WaveSpeed);
}

/// <summary>Lazy shared renderer for the caustic acceptance facts. A top-down orthographic camera maps world XZ to
/// pixels, so a band's world place reads straight off the image. No device is created when GPU facts are skipped.</summary>
public sealed class WaterCausticsScenes : IDisposable
{
    public const int Size = 512;
    public const float ViewMetres = 8f;
    /// <summary>Two metres, sixteen lattice cells and 32 map texels per wavelength, and resolved by the 0.125 m lattice
    /// footprint at four samples.</summary>
    public const float Wavelength = 2f;
    public const float WaveSpeed = 1f;
    /// <summary>Mean attenuation 0.375 per metre gives <c>D = 3 / 0.375 = 8</c> m, so slices sit at 1, 2, 4 and 8 m.
    /// The column look's clarity of 8 m derives the same mean extinction.</summary>
    public const float Attenuation = 0.375f, Clarity = 8f, SliceDepth = 8f;
    const float WaterHalf = 6f;
    static readonly Color BedColor = new(0.8f, 0.8f, 0.8f, 1f);
    GpuDeviceContext? _gpu;
    Scene3D? _scene;
    IGpuTexture? _color;
    IGpuFramebuffer? _target;
    IGpuCommandList? _commands;
    MeshHandle _plane, _box;

    internal Scene3D Scene => _scene!;

    public WaterCausticsImage Capture(WaterCausticsCase settings, int frames = 2, bool cut = true)
    {
        Ensure();
        IGpuDevice device = _gpu!.GpuDevice;
        Scene3D scene = _scene!;
        Vector3 renderOrigin = settings.Origin + settings.RenderOffset;
        var camera = new TopDownCamera(settings.Origin) { RenderOrigin = renderOrigin };
        Configure(scene, settings, camera, renderOrigin, cut);
        for (int frame = 0; frame < frames; frame++)
        {
            scene.Begin();
            scene.EffectTimeSeconds = settings.Time;
            Draw(scene, settings);
            scene.PrepareFrame(Size, Size);
            using (GpuRecording.Open(device, _commands!, nameof(WaterCausticsScenes)))
                scene.RenderInternal(_commands!, Size, Size, _target!);
            device.Submit(_commands!);
            device.WaitForIdle();
            device.Present();
        }
        var targets = scene.ColorTargetsForTests;
        Assert.Equal(settings.Aa.Mode == AntiAliasingMode.Msaa ? 4 : 1, targets.SampleCount);
        IGpuTexture linear = settings.Aa.Mode == AntiAliasingMode.Temporal
            ? scene.TemporalHistory.Color(scene.TemporalHistory.WriteIndex) : targets.ColorTex;
        float[]? copy = settings.Column && scene.WaterColumnLightDiagnostics.CopyRan
            ? ReadFloat(scene.WaterColumnColorTextureForTests!) : null;
        Assert.NotNull(scene.ResolvedVisibilityTarget);
        return new WaterCausticsImage(GpuReadback.ToRgba(device, _color!, Size, Size), ReadFloat(linear),
            LitVisibilityGpuScene.ReadR8(device, scene.ResolvedVisibilityTarget!), copy, camera.ViewProjection,
            renderOrigin, settings, scene.WaterCausticDiagnostics);
    }

    float[] ReadFloat(IGpuTexture texture)
    {
        Assert.Equal(GpuPixelFormat.R16G16B16A16Float, texture.Format);
        Assert.Equal((uint)Size, texture.Width);
        return TemporalTextureIo.Read(_gpu!.GpuDevice, texture);
    }

    /// <summary>The latest frame's map atlas, four slices per texel, with the window that placed it.</summary>
    internal CausticMapReadback ReadMap()
    {
        IGpuTexture map = _scene!.WaterCausticMapForTests!;
        Assert.Equal(GpuPixelFormat.R16G16B16A16Float, map.Format);
        return new CausticMapReadback(TemporalTextureIo.Read(_gpu!.GpuDevice, map), (int)map.Width, (int)map.Height,
            _scene.WaterSurfaceWindowForTests);
    }

    static void Configure(Scene3D scene, WaterCausticsCase s, TopDownCamera camera, Vector3 renderOrigin, bool cut)
    {
        scene.CameraOverride = camera;
        scene.RenderOrigin = renderOrigin;
        if (cut) scene.CameraCut();
        scene.Post.RenderScale = RenderScale.MatchViewport;
        scene.Post.Quality.AntiAliasing = s.Aa;
        scene.Post.Temporal.Upscale = TemporalUpscale.Native;
        scene.Post.Hdr.Enabled = true;
        scene.Post.Hdr.Exposure = 1f;
        scene.Post.Bloom.Enabled = false;
        scene.Post.Quantize = scene.Post.Dither = scene.Post.Outline = false;
        scene.Post.CelBands = 0;
        scene.Post.Starfield = scene.Post.Sky.Enabled = false;
        scene.Post.Atmosphere.Enabled = false;
        scene.Post.Background = BackgroundMode.Solid;
        scene.Post.BackgroundColor = new Color(0.1f, 0.05f, 0.15f, 1f);
        scene.Post.LightDirection = s.Sun;
        scene.Post.LightColor = Color.White;
        scene.Post.FillLightColor = Color.Black;
        // A little ambient keeps a key shadow measurable without adding anything the term multiplies.
        scene.Post.AmbientColor = new Color(0.05f, 0.05f, 0.05f, 1f);
        ShadowSettings shadows = scene.Post.Quality.Shadows;
        shadows.Mode = ShadowMode.ShadowMap;
        shadows.ShadowStrength = s.ShadowStrength;
        shadows.ShadowNearDistance = 20f;
        shadows.ShadowMaxDistance = 40f;
        shadows.ShadowNormalOffset = 0.5f;
        shadows.ShadowConstantBias = shadows.ShadowSlopeBias = 0.00005f;
        scene.Post.Water = new WaterSettings
        {
            UnderwaterSunlight = true,
            UnderwaterCaustics = s.Caustics,
            CausticStrength = s.Strength,
            // Softening would blur the bands with depth. The facts measure the slices themselves.
            UnderwaterPenumbraPerMetre = 0f,
            AbsorptionPerMetre = new Color(Attenuation, Attenuation, Attenuation, 0f),
            WaterColumnLight = s.Column,
            WaterColor = new Color(0.10f, 0.13f, 0.10f, 1f),
            Clarity = Clarity,
            // Zero opacity with no glint or foam discards the surface, so only the receivers carry the bands.
            Opacity = 0f,
            GlintStrength = 0f,
            FoamStrength = 0f,
            SkyReflectionStrength = 0f,
            SkyReflectionSunStrength = 0f,
            SwellAmplitude = 0f,
            SwellSteepness = 0f,
            SurfStrength = 0f,
            ShoalingStrength = 0f,
            WaveScale = Wavelength / (2f * MathF.PI),
            WaveSpeed = WaveSpeed,
            NormalStrength = s.NormalStrength,
            WaveWarpStrength = 0f,
            RippleComponents = 1,
            RippleSeed = s.Heading,
            GridMode = WaterGridMode.Clipmap
        };
    }

    void Draw(Scene3D scene, WaterCausticsCase s)
    {
        Vector3 o = s.Origin;
        float depth = s.ReceiverDepth;
        void Bed(float centerX, float width, float y) => scene.Draw(_plane, Matrix4x4.CreateScale(width, 1f, 20f)
            * Matrix4x4.CreateTranslation(o + new Vector3(centerX, y, 0f)), BedColor, Material.None, castsShadows: false);
        void Water(float x, float halfX, float surfaceY, float? floorY, WaterLook? look = null, float z = 0f, float halfZ = WaterHalf) =>
            scene.DrawWater(new WaterPlane(o.X + x, o.Y + surfaceY, o.Z + z, halfX, halfZ, look,
                floorY: floorY is float floor ? o.Y + floor : null));

        switch (s.Layout)
        {
            case WaterCausticsLayout.Split:
                Bed(0f, 20f, -depth);
                Water(-WaterHalf / 2f, WaterHalf / 2f, 0f, -depth - 0.1f);
                Water(WaterHalf / 2f, WaterHalf / 2f, 0f, -depth - 0.1f);
                break;
            case WaterCausticsLayout.Bank:
                scene.Draw(_plane, Matrix4x4.CreateScale(20f, 1f, 20f) * Matrix4x4.CreateRotationX(-MathF.Atan(0.25f))
                    * Matrix4x4.CreateTranslation(o), BedColor, Material.None, castsShadows: false);
                Water(0f, WaterHalf, 0f, null);
                break;
            case WaterCausticsLayout.Stacked:
                // The upper slab ends at -1.5 m, above the lower body's surface at -2 m.
                Bed(-5f, 10f, -depth);
                Bed(5f, 10f, -2f - depth);
                Water(0f, WaterHalf, 0f, -1.5f, new WaterLook { RippleSeed = s.Heading });
                Water(0f, WaterHalf, -2f, -2f - depth - 0.1f, new WaterLook { RippleSeed = s.LowerHeading });
                break;
            case WaterCausticsLayout.SharedRiver:
                Bed(0f, 20f, -depth);
                Water(0f, WaterHalf, 0f, -depth - 0.1f);
                Water(500f, 2f, 0f, -depth - 0.1f);
                Water(501f, 2f, 0f, -depth - 0.1f);
                break;
            case WaterCausticsLayout.Narrow:
                Bed(0f, 20f, -depth);
                Water(2f, 4f, 0f, -depth - 0.1f);
                break;
            default:
                Bed(0f, 20f, -depth);
                Water(0f, WaterHalf, 0f, -depth - 0.1f);
                break;
        }
        // Invisible to the camera, so the shadow it casts on the bed is not hidden under it.
        if (s.Caster)
            scene.DrawShadowOnly(_box, Matrix4x4.CreateScale(2f, 0.05f, 2f) * Matrix4x4.CreateTranslation(o + new Vector3(-1f, 2f, 0f)));
    }

    void Ensure()
    {
        if (_scene is not null) return;
        _gpu = GpuDeviceContext.CreateHeadless();
        IGpuDevice device = _gpu.GpuDevice;
        _color = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(Size, Size,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        _target = device.Factory.CreateFramebuffer(null, _color);
        _commands = device.Factory.CreateCommandList();
        _scene = new Scene3D(device, _target.Outputs,
            new ShadowSettings { ShadowCascadeCount = 1, ShadowMapResolution = 2048 }, requestLitVisibility: true);
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

    /// <summary>Straight down, with -Z up the screen. Orientation is built before translation and the view is render
    /// relative, so a far origin does not round the direction.</summary>
    sealed class TopDownCamera(Vector3 target) : IIsoCamera3D, IRenderOriginAware
    {
        static readonly Vector3 Up = -Vector3.UnitZ;
        static readonly Vector3 Lift = new(0f, 20f, 0f);
        public Vector3 RenderOrigin { get; set; }
        public Vector3 Eye => target + Lift;
        public Vector3 Forward => -Vector3.UnitY;
        public Matrix4x4 Projection => Matrix4x4.CreateOrthographic(ViewMetres, ViewMetres, 1f, 40f);
        public Matrix4x4 View => Matrix4x4.CreateLookAt(target - RenderOrigin + Lift, target - RenderOrigin, Up);
        public Matrix4x4 ViewProjection => View * Projection;
        public Matrix4x4 AbsoluteViewProjection => Matrix4x4.CreateTranslation(-target) * Matrix4x4.CreateLookAt(Lift, Vector3.Zero, Up) * Projection;
        public bool WorldToScreen(Vector3 world, int width, int height, out Vector2 pixel)
        {
            Vector4 p = Vector4.Transform(new Vector4(world - RenderOrigin, 1f), ViewProjection);
            pixel = new Vector2((p.X / p.W + 1f) * width * 0.5f, (1f - p.Y / p.W) * height * 0.5f);
            return p.W > 0f;
        }
    }
}

/// <summary>A map atlas read back as floats, with the window its texels address.</summary>
internal sealed record CausticMapReadback(float[] Values, int Width, int Height, WaterSurfaceWindow Window)
{
    public int Layers => Width / Height;

    public float Slice(int layer, int column, int row, int slice) =>
        Values[(row * Width + layer * Height + column) * 4 + slice];

    /// <summary>The first texel column whose centre is at or past render-relative <paramref name="x"/>.</summary>
    public int Column(float x) => (int)MathF.Ceiling((x - Window.MinX) / WaterSurfaceWindow.MapTexelMetres - 0.5f);

    public int Row(float z) => (int)MathF.Ceiling((z - Window.MinZ) / WaterSurfaceWindow.MapTexelMetres - 0.5f);
}

/// <summary>One capture: the presented bytes, the linear HDR colour (the temporal resolve when temporal AA is on), key
/// visibility and, with the column on, the scene colour copy the composite reads. Pixels map to absolute world XZ
/// through the inverse view projection at pixel centres.</summary>
public sealed class WaterCausticsImage
{
    const int Size = WaterCausticsScenes.Size;
    const int Border = 4;
    /// <summary>Profile bins of 1/128 m, an eighth of a map texel, fine enough that one bin is no part of a bound.</summary>
    internal const double BinMetres = 1d / 128d;
    readonly double _originX, _originZ, _x0, _z0, _xPerColumn, _zPerColumn, _xPerRow, _zPerRow;

    public byte[] Rgba { get; }
    public float[] Linear { get; }
    public byte[] Visibility { get; }
    public float[]? Copy { get; }
    public WaterCausticsCase Settings { get; }
    public WaterCausticDiagnostics Diagnostics { get; }
    public Matrix4x4 ViewProjection { get; }

    public WaterCausticsImage(byte[] rgba, float[] linear, byte[] visibility, float[]? copy, Matrix4x4 viewProjection,
        Vector3 renderOrigin, WaterCausticsCase settings, WaterCausticDiagnostics diagnostics)
    {
        Rgba = rgba;
        Linear = linear;
        Visibility = visibility;
        Copy = copy;
        Settings = settings;
        Diagnostics = diagnostics;
        ViewProjection = viewProjection;
        _originX = renderOrigin.X;
        _originZ = renderOrigin.Z;
        Assert.True(Matrix4x4.Invert(viewProjection, out Matrix4x4 inverse));
        Vector3 At(float x, float y)
        {
            Vector4 p = Vector4.Transform(new Vector4((x + 0.5f) / Size * 2f - 1f, 1f - (y + 0.5f) / Size * 2f, 0.5f, 1f), inverse);
            return new Vector3(p.X, p.Y, p.Z) / p.W;
        }
        // Orthographic: a pixel's XZ is affine in its column and row.
        Vector3 o = At(0f, 0f), right = At(Size, 0f) - o, down = At(0f, Size) - o;
        (_x0, _z0) = (o.X, o.Z);
        (_xPerColumn, _zPerColumn, _xPerRow, _zPerRow) = (right.X / Size, right.Z / Size, down.X / Size, down.Z / Size);
    }

    /// <summary>Absolute world XZ of a pixel centre.</summary>
    public (double X, double Z) World(int x, int y) =>
        (_originX + _x0 + x * _xPerColumn + y * _xPerRow, _originZ + _z0 + x * _zPerColumn + y * _zPerRow);

    /// <summary>World XZ of a pixel centre relative to the arrangement's centre.</summary>
    public (double X, double Z) Local(int x, int y)
    {
        var (wx, wz) = World(x, y);
        return (wx - Settings.Origin.X, wz - Settings.Origin.Z);
    }

    public static double Luma(float[] rgba, int pixel) =>
        0.299d * rgba[pixel * 4] + 0.587d * rgba[pixel * 4 + 1] + 0.114d * rgba[pixel * 4 + 2];

    /// <summary>Interior pixels, away from a border the temporal and MSAA resolves treat differently.</summary>
    public static System.Collections.Generic.IEnumerable<(int X, int Y, int Index)> Interior()
    {
        for (int y = Border; y < Size - Border; y++)
            for (int x = Border; x < Size - Border; x++) yield return (x, y, y * Size + x);
    }

    /// <summary>
    /// Fit each band of the on-minus-off luminance against the oracle. Pixels are binned by their heading coordinate
    /// <c>u</c>, so a band is one peak of the profile whatever its angle. Each predicted band searches a quarter
    /// wavelength either side, takes the brightest bin and refines it with a weighted parabola over a map texel either
    /// side. A sign error moves a band half a wavelength and lands its search at an end, far outside any bound.
    /// </summary>
    internal BandFit Bands(WaterCausticsImage off, CausticOracle oracle, double scaledDepth, double receiverDepth,
        Func<double, double, bool>? region = null, bool copy = false)
    {
        float[] on = copy ? Copy! : Linear, control = copy ? off.Copy! : off.Linear;
        double hx = oracle.Heading.X, hz = oracle.Heading.Y;
        double minimum = double.PositiveInfinity, maximum = double.NegativeInfinity, offSum = 0d;
        int pixels = 0;
        foreach (var (x, y, _) in Interior())
        {
            var (lx, lz) = Local(x, y);
            if (region is not null && !region(lx, lz)) continue;
            var (wx, wz) = World(x, y);
            double u = hx * wx + hz * wz;
            minimum = Math.Min(minimum, u);
            maximum = Math.Max(maximum, u);
        }
        int bins = (int)Math.Ceiling((maximum - minimum) / BinMetres) + 1;
        var sum = new double[bins];
        var count = new int[bins];
        foreach (var (x, y, i) in Interior())
        {
            var (lx, lz) = Local(x, y);
            if (region is not null && !region(lx, lz)) continue;
            var (wx, wz) = World(x, y);
            int bin = (int)((hx * wx + hz * wz - minimum) / BinMetres);
            sum[bin] += Luma(on, i) - Luma(control, i);
            count[bin]++;
            offSum += Luma(control, i);
            pixels++;
        }
        double Centre(int bin) => minimum + (bin + 0.5d) * BinMetres;
        double Mean(int bin) => sum[bin] / count[bin];
        bool Usable(int bin) => bin >= 0 && bin < bins && count[bin] >= 3;

        double predicted = oracle.BrightestBand(scaledDepth, receiverDepth), lambda = oracle.Wavelength;
        double texel = WaterSurfaceWindow.MapTexelMetres;
        var errors = new System.Collections.Generic.List<double>();
        double excess = double.NegativeInfinity;
        for (double c = predicted + Math.Floor((minimum - predicted) / lambda) * lambda; c < maximum; c += lambda)
        {
            if (c - lambda / 4d - texel < minimum || c + lambda / 4d + texel > maximum) continue;
            int best = -1;
            for (int bin = (int)((c - lambda / 4d - minimum) / BinMetres); Centre(bin) <= c + lambda / 4d; bin++)
                if (Usable(bin) && (best < 0 || Mean(bin) > Mean(best))) best = bin;
            Assert.True(best >= 0, $"No profile bins near the predicted band at u={c:F4}.");
            // Weighted least squares of a + b x + c x^2 over a map texel either side of the brightest bin.
            double s0 = 0, s1 = 0, s2 = 0, s3 = 0, s4 = 0, t0 = 0, t1 = 0, t2 = 0;
            int reach = (int)Math.Round(texel / BinMetres);
            for (int bin = best - reach; bin <= best + reach; bin++)
            {
                if (!Usable(bin)) continue;
                double dx = Centre(bin) - Centre(best), w = count[bin], v = Mean(bin);
                s0 += w; s1 += w * dx; s2 += w * dx * dx; s3 += w * dx * dx * dx; s4 += w * dx * dx * dx * dx;
                t0 += w * v; t1 += w * v * dx; t2 += w * v * dx * dx;
            }
            double det = s0 * (s2 * s4 - s3 * s3) - s1 * (s1 * s4 - s3 * s2) + s2 * (s1 * s3 - s2 * s2);
            double b = (s0 * (t1 * s4 - s3 * t2) - t0 * (s1 * s4 - s3 * s2) + s2 * (s1 * t2 - t1 * s2)) / det;
            double a2 = (s0 * (s2 * t2 - t1 * s3) - s1 * (s1 * t2 - t1 * s2) + t0 * (s1 * s3 - s2 * s2)) / det;
            double vertex = a2 < 0d && Math.Abs(-b / (2d * a2)) <= texel ? -b / (2d * a2) : 0d;
            errors.Add(oracle.Distance(Centre(best) + vertex, predicted));
            excess = Math.Max(excess, Mean(best));
        }
        double offLuma = offSum / Math.Max(pixels, 1);
        double maxError = 0d;
        foreach (double e in errors) maxError = Math.Max(maxError, Math.Abs(e));
        return new BandFit(errors.Count, maxError, errors.ToArray(), excess,
            offLuma * (oracle.PeakTerm(scaledDepth) - 1d), predicted, pixels);
    }
}

/// <summary>Measured bands against the oracle. <see cref="PredictedExcess"/> assumes the off luminance is all key light,
/// which the small ambient makes a slight overestimate.</summary>
internal readonly record struct BandFit(int Bands, double MaxError, double[] Errors, double PeakExcess,
    double PredictedExcess, double Predicted, int Pixels)
{
    public override string ToString() =>
        $"bands={Bands}, errors=[{string.Join(", ", Array.ConvertAll(Errors, e => e.ToString("F4")))}] m, max |error|={MaxError:F4} m, measured peak excess={PeakExcess:F5}, predicted={PredictedExcess:F5}, predicted u={Predicted:F4} m, pixels={Pixels}";
}
