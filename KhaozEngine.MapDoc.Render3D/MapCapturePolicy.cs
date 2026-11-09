using System;
using System.Numerics;
using System.Text.Json;
using System.Reflection;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Value snapshot of every directional and point-shadow option. Getters return detached settings.</summary>
public sealed record MapCaptureLighting
{
    readonly ShadowSettings _shadows;
    static readonly JsonSerializerOptions Options = CreateOptions();
    static JsonSerializerOptions CreateOptions()
    { var options = MapJsonTextOptions.CreateSerializer(false); options.IncludeFields = true; options.IgnoreReadOnlyProperties = true; return options; }
    public Vector3 DirectionToLight { get; }
    public Color SunColour { get; }
    public float SunIntensity { get; }
    public float MaxCasterDistance { get; }
    public ShadowSettings Shadows => Copy(_shadows);
    internal string ShadowDigest => JsonSerializer.Serialize(_shadows, Options);
    public MapCaptureLighting(Vector3 DirectionToLight, Color SunColour, float SunIntensity, ShadowSettings Shadows,
        float MaxCasterDistance = 128f)
    {
        ArgumentNullException.ThrowIfNull(Shadows);
        if (!float.IsFinite(DirectionToLight.X) || !float.IsFinite(DirectionToLight.Y) || !float.IsFinite(DirectionToLight.Z) ||
            !float.IsFinite(DirectionToLight.LengthSquared()) || DirectionToLight.LengthSquared() < 1e-12f || !float.IsFinite(SunIntensity) || SunIntensity < 0 ||
            !float.IsFinite(MaxCasterDistance) || MaxCasterDistance < 0 || !float.IsFinite(SunColour.R) || !float.IsFinite(SunColour.G) ||
            !float.IsFinite(SunColour.B) || !float.IsFinite(SunColour.A))
            throw new ArgumentException("Invalid capture lighting.");
        this.DirectionToLight = Vector3.Normalize(DirectionToLight); this.SunColour = SunColour;
        this.SunIntensity = SunIntensity; this.MaxCasterDistance = MaxCasterDistance;
        _shadows = Copy(Shadows);
    }
    public void ApplyTo(Scene3D scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        scene.Post.LightDirection = -DirectionToLight; scene.Post.LightColor = SunColour.ScaleRgb(SunIntensity);
        var settings = Shadows; var live = scene.Post.Quality.Shadows;
        foreach (var field in typeof(ShadowSettings).GetFields(BindingFlags.Public | BindingFlags.Instance))
            if (field.Name != nameof(ShadowSettings.PointShadows)) field.SetValue(live, field.GetValue(settings));
        scene.RequestShadowMapLayout(settings.ShadowMapResolution, settings.ShadowCascadeCount);
        scene.RequestPointShadowSettings(settings.PointShadows);
    }
    static ShadowSettings Copy(ShadowSettings source)
    {
        var copy = new ShadowSettings { ShadowMapResolution = source.ShadowMapResolution, ShadowCascadeCount = source.ShadowCascadeCount };
        foreach (var field in typeof(ShadowSettings).GetFields(BindingFlags.Public | BindingFlags.Instance)) field.SetValue(copy, field.GetValue(source));
        copy.PointShadows = source.PointShadows.Clone(); return copy;
    }
}
public sealed record MapCapturePolicy(MapRenderPolicy Render, MapCaptureLighting Lighting, MapCaptureAa Aa,
    int WarmupFrames = 16, int MaxSettleOperations = 1024);
