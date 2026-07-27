using System;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.Render3D;

namespace KhaozEngine.MapEditor;

/// <summary>
/// Turns an <see cref="EditorSettings"/> into the host scene's look: the sky and lighting bundle plus the operator's
/// slider overrides on <see cref="PixelPostProcessSettings"/>, and the ocean bundle plus its overrides on
/// <see cref="WaterSettings"/>. Pure over its arguments (it touches no GPU and no scene state), so the whole
/// mapping is headless-testable, and <see cref="MapEditorScene"/> only decides WHEN to run it.
/// <para>The preset-introspection helpers are what keep the menu honest: instead of restating the presets' numbers
/// in the editor, the sliders read them back off a scratch settings object, so a retuned preset moves the sliders
/// with it. <see cref="SunAnglesOf"/> is the exact inverse of
/// <see cref="EnvironmentPresets.SunLightDirection"/>.</para>
/// </summary>
internal static class MapEditorEnvironment
{
    /// <summary>Target world size of one bathymetry texel. A depth field only drives shoaling and the surf band, so
    /// it is deliberately coarse: this is metres per texel, not a terrain resolution.</summary>
    const float BathymetryTexelMetres = 4f;

    /// <summary>Texel ceiling per side for the editor's bathymetry, so a large document cannot turn a rebuild into a
    /// million ground samples. Well under <see cref="WaterBathymetry.MaxResolution"/>.</summary>
    const int MaxBathymetryResolution = 256;

    /// <summary>Applies <paramref name="settings"/> to <paramref name="post"/>: the sky / lighting preset first,
    /// then the operator's overrides on top of it (sun direction from the azimuth and elevation pair, key and
    /// ambient colours scaled by their intensity multipliers), then the ocean preset and its swell and foam
    /// overrides. <paramref name="bathymetry"/> is assigned straight through, so passing null (surf off) clears any
    /// field a previous apply installed.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> or <paramref name="post"/> is null.</exception>
    public static void Apply(EditorSettings settings, PixelPostProcessSettings post, WaterBathymetry? bathymetry)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(post);

        EnvironmentPresets.Apply(settings.Environment, post);
        // The preset just wrote its own sun direction and colours, so the overrides read the preset's values back
        // out of `post` rather than needing a second copy of them here.
        post.LightDirection = EnvironmentPresets.SunLightDirection(
            settings.SunAzimuthDegrees, settings.SunElevationDegrees);
        post.LightColor = post.LightColor.ScaleRgb(settings.KeyLightIntensity);
        post.AmbientColor = post.AmbientColor.ScaleRgb(settings.AmbientIntensity);

        OceanPresets.Apply(settings.Ocean, post.Water);
        post.Water.SwellAmplitude = settings.SwellAmplitude;
        post.Water.FoamStrength = settings.FoamStrength;
        post.Water.Bathymetry = bathymetry;
    }

    /// <summary>The sun azimuth and elevation (degrees) <paramref name="kind"/>'s own key light sits at, read back
    /// off a scratch settings object so the menu never restates a preset's numbers.</summary>
    public static (float Azimuth, float Elevation) SunAnglesOf(EnvironmentPresetKind kind)
    {
        var scratch = new PixelPostProcessSettings();
        EnvironmentPresets.Apply(kind, scratch);
        return SunAngles(scratch.LightDirection);
    }

    /// <summary>The swell amplitude and foam strength <paramref name="kind"/>'s own bundle sets, read back off a
    /// scratch <see cref="WaterSettings"/> for the same reason as <see cref="SunAnglesOf"/>.</summary>
    public static (float Swell, float Foam) OceanValuesOf(OceanPresetKind kind)
    {
        var scratch = new WaterSettings();
        OceanPresets.Apply(kind, scratch);
        return (scratch.SwellAmplitude, scratch.FoamStrength);
    }

    /// <summary>The compass azimuth and elevation (degrees) a key-light TRAVEL direction corresponds to: the exact
    /// inverse of <see cref="EnvironmentPresets.SunLightDirection"/>, in the same Y-up, north = -Z, east = +X
    /// convention. Azimuth comes back in 0..360. A zero-length direction reads as straight overhead, since there is
    /// no compass bearing to recover from it.</summary>
    public static (float Azimuth, float Elevation) SunAngles(Vector3 lightDirection)
    {
        if (lightDirection.LengthSquared() <= 1e-12f) return (0f, EditorSettings.MaxSunElevationDegrees);

        // The direction TO the sun is the opposite of the direction the light travels.
        Vector3 toward = -Vector3.Normalize(lightDirection);
        float elevation = MathF.Asin(Math.Clamp(toward.Y, -1f, 1f)) * 180f / MathF.PI;
        float azimuth = MathF.Atan2(toward.X, -toward.Z) * 180f / MathF.PI;
        if (azimuth < 0f) azimuth += 360f;
        return (azimuth, elevation);
    }

    /// <summary>Builds a water-depth field over <paramref name="bounds"/> by sampling
    /// <paramref name="groundHeight"/> down from <paramref name="waterLevel"/>, which is the whole adoption path
    /// for shoaling and breaking surf (<see cref="WaterSettings.Bathymetry"/>). The resolution follows the document
    /// size at roughly <see cref="BathymetryTexelMetres"/> per texel, capped at
    /// <see cref="MaxBathymetryResolution"/>, so the one-off fill stays bounded however large the document is.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="groundHeight"/> is null.</exception>
    public static WaterBathymetry BuildBathymetry(MapBounds bounds, Func<float, float, float> groundHeight,
        float waterLevel)
    {
        ArgumentNullException.ThrowIfNull(groundHeight);

        // A degenerate or inverted bounds still has to produce a legal field rather than throw: the editor can hold
        // a half-authored document, and a black viewport is a worse failure than a coarse depth map.
        float halfX = MathF.Max(MathF.Abs(bounds.MaxX - bounds.MinX) * 0.5f, 1f);
        float halfZ = MathF.Max(MathF.Abs(bounds.MaxZ - bounds.MinZ) * 0.5f, 1f);
        int resolution = Math.Clamp(
            (int)MathF.Ceiling(MathF.Max(halfX, halfZ) * 2f / BathymetryTexelMetres),
            WaterBathymetry.MinResolution, MaxBathymetryResolution);

        var field = new WaterBathymetry(resolution,
            (bounds.MinX + bounds.MaxX) * 0.5f, (bounds.MinZ + bounds.MaxZ) * 0.5f, halfX, halfZ);
        field.FillFromGround(groundHeight, waterLevel);
        return field;
    }
}
