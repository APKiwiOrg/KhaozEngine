using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public enum UnderwaterSunlightGeometry { FlatBed, ThinSlab, StraddlingHull, SubmergedRock, Cave, Bank, AboveWater }

public readonly record struct UnderwaterSunlightCase(UnderwaterSunlightGeometry Geometry = UnderwaterSunlightGeometry.FlatBed,
    float Depth = 1f, bool Enabled = true, bool Orthographic = false, float ShadowStrength = 1f,
    bool Caster = true, bool Absorption = true, bool Water = true, float SlabBottom = 0.05f, bool AmbientOnly = false,
    bool SpecularOnly = false, float SpecularStrength = 0.8f, bool BedCastsShadows = false,
    float PenumbraPerMetre = UnderwaterSunlightScenes.PenumbraPerMetre)
{
    public UnderwaterSunlightCase() : this(UnderwaterSunlightGeometry.FlatBed) { }
    public float BedY => Geometry == UnderwaterSunlightGeometry.Cave ? -3f
        : Geometry == UnderwaterSunlightGeometry.AboveWater ? 0.25f : -Depth;
}

/// <summary>Lazy shared renderer. All geometry is synthetic and all captures reset the same settings.
/// No device is created when the GPU facts are skipped.</summary>
public sealed class UnderwaterSunlightScenes : IDisposable
{
    public const int Size = 512;
    public const float PenumbraPerMetre = 0.03f;
    public const float AirTangent = 1.428148f;
    // Snell's law at 35 degrees elevation, n = 1.333. Independent of shader helpers.
    public const float WaterTangent = 0.778952f;
    public const float PathPerMetre = 1.267583f;
    static readonly Color BedColor = new(0.65f, 0.65f, 0.65f, 1f);
    GpuDeviceContext? _gpu;
    Scene3D? _scene;
    IGpuTexture? _color;
    IGpuFramebuffer? _target;
    IGpuCommandList? _commands;
    MeshHandle _plane, _box;

    internal Vector3 CaptureCasterFace(GltfMesh mesh)
    {
        Ensure();
        _box = _scene!.LoadMesh(mesh);
        Capture(new UnderwaterSunlightCase(UnderwaterSunlightGeometry.StraddlingHull));
        Matrix4x4 matrix = _scene.CascadeFitAbsoluteForTests[0];
        Vector4 clip = Vector4.Transform(new Vector4(0f, 0f, 0f, 1f), matrix);
        float[] atlas = _scene.DebugReadShadowMap(out int width, out int height);
        int x = (int)((clip.X / clip.W * 0.5f + 0.5f) * width);
        int y = (int)((0.5f - clip.Y / clip.W * 0.5f) * height);
        Assert.True(Matrix4x4.Invert(matrix, out Matrix4x4 inverse));
        Vector4 stored = Vector4.Transform(new Vector4((x + 0.5f) / width * 2f - 1f,
            1f - (y + 0.5f) / height * 2f, atlas[y * width + x], 1f), inverse);
        return new Vector3(stored.X, stored.Y, stored.Z) / stored.W;
    }

    public UnderwaterSunlightImage Capture(UnderwaterSunlightCase settings)
    {
        Ensure();
        IGpuDevice device = _gpu!.GpuDevice;
        Scene3D scene = _scene!;
        IIsoCamera3D camera = Camera(settings);
        Configure(scene, settings, camera);
        for (int frame = 0; frame < 2; frame++)
        {
            scene.Begin();
            scene.EffectTimeSeconds = 0f;
            Draw(scene, settings);
            scene.PrepareFrame(Size, Size);
            using (GpuRecording.Open(device, _commands!, nameof(UnderwaterSunlightScenes)))
                scene.RenderInternal(_commands!, Size, Size, _target!);
            device.Submit(_commands!);
            device.WaitForIdle();
            device.Present();
        }
        Assert.NotNull(scene.ResolvedVisibilityTarget);
        var image = new UnderwaterSunlightImage(GpuReadback.ToRgba(device, _color!, Size, Size),
            LitVisibilityGpuScene.ReadR8(device, scene.ResolvedVisibilityTarget!), camera.ViewProjection, settings);
        return image;
    }

    static IIsoCamera3D Camera(UnderwaterSunlightCase settings)
    {
        if (settings.Orthographic)
            return new IsoCamera3D
            {
                Target = new Vector3(0f, -1f, 1.5f), Distance = 11f, OrthoSize = 8f,
                Azimuth = MathF.PI, Elevation = 1.55f, AspectRatio = 1f, NearPlane = 0.1f, FarPlane = 25f
            };
        return new FlyCamera3D
        {
            Position = settings.Geometry == UnderwaterSunlightGeometry.Cave
                ? new Vector3(0f, -2.1f, 0f) : new Vector3(0f, 10f, 1.5f),
            Yaw = MathF.PI, Pitch = -MathF.PI / 2f, FieldOfView = 0.7f,
            AspectRatio = 1f, NearPlane = 0.1f, FarPlane = 25f
        };
    }

    static void Configure(Scene3D scene, UnderwaterSunlightCase settings, IIsoCamera3D camera)
    {
        scene.CameraOverride = camera;
        scene.RenderOrigin = Vector3.Zero;
        scene.CameraCut();
        scene.Post.RenderScale = RenderScale.MatchViewport;
        scene.Post.Quality.AntiAliasing = AntiAliasing.Off;
        scene.Post.Hdr.Enabled = false;
        scene.Post.Starfield = false;
        scene.Post.Sky.Enabled = false;
        scene.Post.Background = BackgroundMode.Solid;
        scene.Post.BackgroundColor = new Color(0.1f, 0.05f, 0.15f, 1f);
        scene.Post.Outline = false;
        scene.Post.LightDirection = new Vector3(0f, -0.57357645f, 0.81915204f);
        scene.Post.LightColor = settings.AmbientOnly ? Color.Black : Color.White;
        scene.Post.FillLightColor = Color.Black;
        scene.Post.AmbientColor = settings.AmbientOnly ? new Color(0.4f, 0.3f, 0.2f, 1f)
            : settings.Geometry == UnderwaterSunlightGeometry.Cave ? new Color(0.03f, 0.03f, 0.03f, 1f) : Color.Black;
        scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
        scene.Post.Quality.Shadows.ShadowStrength = settings.ShadowStrength;
        scene.Post.Quality.Shadows.ShadowNearDistance = 20f;
        // Fixed small biases keep the slab's 5 cm air clearance measurable. Receivers do not cast.
        scene.Post.Quality.Shadows.ShadowNormalOffset = 0.5f;
        scene.Post.Quality.Shadows.ShadowConstantBias = 0.00005f;
        scene.Post.Quality.Shadows.ShadowSlopeBias = 0.00005f;
        scene.Post.Water = new WaterSettings
        {
            UnderwaterSunlight = settings.Enabled,
            UnderwaterPenumbraPerMetre = settings.PenumbraPerMetre,
            AbsorptionPerMetre = settings.Absorption ? new Color(0.6f, 0.3f, 0.15f, 0f) : Color.Transparent,
            Opacity = 0f, GlintStrength = 0f, FoamStrength = 0f,
            SwellAmplitude = 0f, SwellSteepness = 0f, NormalStrength = 0f,
            SurfStrength = 0f, ShoalingStrength = 0f, GridMode = WaterGridMode.Clipmap
        };
    }

    void Draw(Scene3D scene, UnderwaterSunlightCase settings)
    {
        Color bedColor = settings.SpecularOnly ? Color.Black : BedColor;
        Material bedMaterial = settings.SpecularOnly ? Material.Shiny(settings.SpecularStrength, 1f) : Material.None;
        if (settings.Geometry == UnderwaterSunlightGeometry.Bank)
            scene.Draw(_plane, Matrix4x4.CreateScale(12f, 1f, 12f)
                * Matrix4x4.CreateRotationX(-MathF.Atan(0.25f)), bedColor, bedMaterial, castsShadows: false);
        else
            scene.Draw(_plane, Matrix4x4.CreateScale(12f, 1f, 12f)
                * Matrix4x4.CreateTranslation(0f, settings.BedY, 0f), bedColor, bedMaterial,
                castsShadows: settings.BedCastsShadows);

        if (settings.Caster)
        {
            switch (settings.Geometry)
            {
                case UnderwaterSunlightGeometry.ThinSlab:
                    Box(scene, new Vector3(1.8f, 0.02f, 0.4f), new Vector3(0f, settings.SlabBottom + 0.01f, 0f));
                    break;
                case UnderwaterSunlightGeometry.StraddlingHull:
                    Box(scene, new Vector3(1.8f, 0.3f, 0.8f), Vector3.Zero);
                    break;
                case UnderwaterSunlightGeometry.SubmergedRock:
                    Box(scene, new Vector3(0.8f, 0.2f, 0.16f), new Vector3(0f, settings.BedY + 0.5f, 0f));
                    break;
                case UnderwaterSunlightGeometry.Cave:
                    // Closed room. Camera is inside, below the lake bed at the roof's top (-1.4 m).
                    Box(scene, new Vector3(12f, 0.2f, 12f), new Vector3(0f, -1.5f, 0f));
                    Box(scene, new Vector3(0.2f, 1.5f, 6f), new Vector3(-3f, -2.25f, 0f));
                    Box(scene, new Vector3(0.2f, 1.5f, 6f), new Vector3(3f, -2.25f, 0f));
                    Box(scene, new Vector3(6f, 1.5f, 0.2f), new Vector3(0f, -2.25f, -3f));
                    Box(scene, new Vector3(6f, 1.5f, 0.2f), new Vector3(0f, -2.25f, 3f));
                    break;
                case UnderwaterSunlightGeometry.AboveWater:
                    Box(scene, Vector3.One * 0.5f, new Vector3(0f, 0.75f, 0f));
                    break;
            }
        }
        if (settings.Water)
            scene.DrawWater(new WaterPlane(0f, 0f, 0f, 6f,
                floorY: settings.Geometry == UnderwaterSunlightGeometry.Cave ? null : MathF.Min(-0.1f, settings.BedY - 0.1f)));
    }

    void Box(Scene3D scene, Vector3 scale, Vector3 position) => scene.Draw(_box,
        Matrix4x4.CreateScale(scale) * Matrix4x4.CreateTranslation(position), new Color(0.15f, 0.7f, 0.2f, 1f), Material.None);

    /// <summary>World size of one near-cascade texel, from the camera geometry rather than a lighting shader.</summary>
    public static float ShadowTexelWorld(UnderwaterSunlightCase settings)
    {
        IIsoCamera3D camera = Camera(settings);
        Span<Vector3> corners = stackalloc Vector3[8];
        Assert.True(ShadowMapMath.FrustumCornersWorld(camera.ViewProjection, corners));
        Vector3 near = (corners[0] + corners[1] + corners[2] + corners[3]) * 0.25f;
        Vector3 far = (corners[4] + corners[5] + corners[6] + corners[7]) * 0.25f;
        float nearDistance = Vector3.Dot(near - camera.Eye, camera.Forward);
        float farDistance = Vector3.Dot(far - camera.Eye, camera.Forward);
        ShadowMapMath.SliceBoundingSphere(corners, 0f, (20f - nearDistance) / (farDistance - nearDistance),
            out _, out float radius);
        return 2f * radius / 2048f;
    }

    public static float KernelRadiusTexels(float depth, float texelWorld) =>
        MathF.Min(8f, 1f + depth * PathPerMetre * PenumbraPerMetre / texelWorld);

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
}

/// <summary>Pixel measurements in world metres. Projection is inverted at pixel centres so perspective
/// and orthographic images measure the same bed, without assuming a screen-to-world scale.</summary>
public sealed class UnderwaterSunlightImage
{
    public byte[] Rgba { get; }
    public byte[] Visibility { get; }
    public UnderwaterSunlightCase Settings { get; }
    readonly Matrix4x4 _viewProjection, _inverse;
    float? _litBedBrightness;
    const int Size = UnderwaterSunlightScenes.Size;

    public UnderwaterSunlightImage(byte[] rgba, byte[] visibility, Matrix4x4 viewProjection, UnderwaterSunlightCase settings)
    {
        Rgba = rgba;
        Visibility = visibility;
        Settings = settings;
        _viewProjection = viewProjection;
        Assert.True(Matrix4x4.Invert(viewProjection, out _inverse));
    }

    public Vector2 Project(Vector3 world)
    {
        Vector4 clip = Vector4.Transform(new Vector4(world, 1f), _viewProjection);
        Assert.True(clip.W > 0f, "The measurement must be in front of the camera.");
        return new Vector2((clip.X / clip.W + 1f) * 0.5f * Size, (1f - clip.Y / clip.W) * 0.5f * Size);
    }

    public Vector3 BedPoint(int x, int y)
    {
        float nx = (x + 0.5f) / Size * 2f - 1f, ny = 1f - (y + 0.5f) / Size * 2f;
        Vector4 n = Vector4.Transform(new Vector4(nx, ny, 0f, 1f), _inverse);
        Vector4 f = Vector4.Transform(new Vector4(nx, ny, 1f, 1f), _inverse);
        Vector3 near = new Vector3(n.X, n.Y, n.Z) / n.W;
        Vector3 direction = new Vector3(f.X, f.Y, f.Z) / f.W - near;
        return near + direction * ((Settings.BedY - near.Y) / direction.Y);
    }

    public Vector3 Pixel(int x, int y)
    {
        Assert.InRange(x, 0, Size - 1);
        Assert.InRange(y, 0, Size - 1);
        int i = (y * Size + x) * 4;
        return new Vector3(Rgba[i], Rgba[i + 1], Rgba[i + 2]) / 255f;
    }

    public Vector3 Sample(Vector3 world)
    {
        Vector2 p = Project(world);
        int x = (int)p.X, y = (int)p.Y;
        Vector3 sum = Vector3.Zero;
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++) sum += Pixel(x + dx, y + dy);
        return sum / 25f;
    }

    public static float Brightness(Vector3 rgb) => Vector3.Dot(rgb, new Vector3(0.299f, 0.587f, 0.114f));

    public Vector3 ChannelRatio(UnderwaterSunlightImage control, Vector3 world)
    {
        Vector3 denominator = control.Sample(world);
        Assert.True(denominator.X > 0.1f && denominator.Y > 0.1f && denominator.Z > 0.1f,
            $"The lit control bed must be visible, RGB {denominator}.");
        return Sample(world) / denominator;
    }

    public float ShadowContrast(int x, int y)
    {
        Assert.True(LitBedBrightness > 0.02f, $"Unlit bed reference, brightness {LitBedBrightness}.");
        return Brightness(Pixel(x, y)) / LitBedBrightness;
    }

    float LitBedBrightness => _litBedBrightness ??= Brightness(Sample(new Vector3(-2f, Settings.BedY, 1f)));

    /// <summary>Both frames are checked at exclusive shadow patches. Ref is shadowed only with refraction,
    /// dry only without it. Each extremum covers every pixel, rather than averaging away a hole.</summary>
    public (int RefCount, int DryCount, float OnRefMax, float OffRefMin, float OnDryMin, float OffDryMax,
        float RefOnOffMax, float MinimumChange) ShadowSwitch(UnderwaterSunlightImage off,
        float refMinZ, float refMaxZ, float dryMinZ, float dryMaxZ)
    {
        int refCount = 0, dryCount = 0;
        float onRefMax = 0f, offRefMin = float.PositiveInfinity, onDryMin = float.PositiveInfinity,
            offDryMax = 0f, refOnOffMax = 0f, minimumChange = float.PositiveInfinity;
        for (int y = 2; y < Size - 2; y++)
            for (int x = 2; x < Size - 2; x++)
            {
                Vector3 p = BedPoint(x, y);
                if (MathF.Abs(p.X) > 0.2f) continue;
                bool refracted = p.Z >= refMinZ && p.Z <= refMaxZ;
                bool dry = p.Z >= dryMinZ && p.Z <= dryMaxZ;
                if (!refracted && !dry) continue;
                float onContrast = ShadowContrast(x, y), offContrast = off.ShadowContrast(x, y);
                if (refracted)
                {
                    onRefMax = MathF.Max(onRefMax, onContrast);
                    offRefMin = MathF.Min(offRefMin, offContrast);
                    float denominator = Brightness(off.Pixel(x, y));
                    Assert.True(denominator > 0.02f, $"The off receiver must be lit at ({x}, {y}), got {denominator}.");
                    refOnOffMax = MathF.Max(refOnOffMax, Brightness(Pixel(x, y)) / denominator);
                    minimumChange = MathF.Min(minimumChange, offContrast - onContrast);
                    refCount++;
                }
                if (dry)
                {
                    onDryMin = MathF.Min(onDryMin, onContrast);
                    offDryMax = MathF.Max(offDryMax, offContrast);
                    minimumChange = MathF.Min(minimumChange, onContrast - offContrast);
                    dryCount++;
                }
            }
        Assert.True(refCount > 30 && dryCount > 30, $"Exclusive shadow patches are too small, ref {refCount}, dry {dryCount}.");
        return (refCount, dryCount, onRefMax, offRefMin, onDryMin, offDryMax, refOnOffMax, minimumChange);
    }

    public (float Offset, float Weight) ShadowCentroid(float minZ = 0.25f)
    {
        float sum = 0f, weighted = 0f;
        for (int y = 2; y < Size - 2; y++)
            for (int x = 2; x < Size - 2; x++)
            {
                Vector3 p = BedPoint(x, y);
                if (MathF.Abs(p.X) > 0.3f || p.Z < minZ || p.Z > 4.2f) continue;
                float deficit = 1f - ShadowContrast(x, y);
                if (deficit < 0.05f) continue;
                sum += deficit;
                weighted += p.Z * deficit;
            }
        Assert.True(sum > 5f, $"No bed shadow along the sun azimuth, deficit weight {sum}.");
        return (weighted / sum, sum);
    }

    public (float Maximum, int Count) HullInteriorRatio()
    {
        // Common footprint of the upper and lower hull faces, then erode by two screen pixels.
        float minZ = 0.15f * UnderwaterSunlightScenes.AirTangent + UnderwaterSunlightScenes.WaterTangent - 0.4f;
        float maxZ = 0.85f * UnderwaterSunlightScenes.WaterTangent + 0.4f;
        Vector2 a = Project(new Vector3(-0.9f, Settings.BedY, minZ));
        Vector2 b = Project(new Vector3(0.9f, Settings.BedY, maxZ));
        int x0 = (int)MathF.Ceiling(MathF.Min(a.X, b.X)) + 2, x1 = (int)MathF.Floor(MathF.Max(a.X, b.X)) - 2;
        int y0 = (int)MathF.Ceiling(MathF.Min(a.Y, b.Y)) + 2, y1 = (int)MathF.Floor(MathF.Max(a.Y, b.Y)) - 2;
        float max = 0f;
        int count = 0;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                max = MathF.Max(max, ShadowContrast(x, y));
                count++;
            }
        Assert.True(count > 100, $"Hull footprint is empty or too small ({count} pixels).");
        return (max, count);
    }

    public float EdgeWidth10To90(float z)
    {
        Vector2 a = Project(new Vector3(0.3f, Settings.BedY, z));
        Vector2 b = Project(new Vector3(1.5f, Settings.BedY, z));
        int row = (int)a.Y;
        float low = float.NaN, high = float.NaN, previous = 0f, previousX = 0f;
        float minimum = 1f, maximum = 0f;
        for (int x = (int)MathF.Ceiling(a.X); x <= (int)b.X; x++)
        {
            float ratio = 0f;
            for (int dy = -2; dy <= 2; dy++) ratio += ShadowContrast(x, row + dy) / 5f;
            float worldX = BedPoint(x, row).X;
            minimum = MathF.Min(minimum, ratio);
            maximum = MathF.Max(maximum, ratio);
            if (previous < 0.1f && ratio >= 0.1f && float.IsNaN(low))
                low = previousX + (worldX - previousX) * (0.1f - previous) / (ratio - previous);
            if (previous < 0.9f && ratio >= 0.9f && float.IsNaN(high))
                high = previousX + (worldX - previousX) * (0.9f - previous) / (ratio - previous);
            previous = ratio;
            previousX = worldX;
        }
        Assert.True(minimum < 0.1f && maximum > 0.9f && float.IsFinite(low) && high > low,
            $"No complete 10 to 90 percent shadow edge at z={z}, range {minimum}..{maximum}, crossings {low}..{high}.");
        return high - low;
    }

    public (float AtWaterline, float Beside) AdjacentRowStep()
    {
        // Pixel centres on either side of the projected analytic y=0 crossing.
        int row = (int)MathF.Floor(Project(Vector3.Zero).Y - 0.5f);
        float Step(int y)
        {
            float maximum = 0f;
            for (int x = Size / 4; x < Size * 3 / 4; x++)
                maximum = MathF.Max(maximum, MathF.Abs(Brightness(Pixel(x, y + 1)) - Brightness(Pixel(x, y))));
            return maximum;
        }
        return (Step(row), MathF.Max(Step(row - 1), Step(row + 1)));
    }
}
