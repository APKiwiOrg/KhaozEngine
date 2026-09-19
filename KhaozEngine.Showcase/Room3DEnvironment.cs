using System;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Showcase;

/// <summary>The overworld's running lighting demonstration and its shared-scene restore boundary.</summary>
internal sealed class Room3DEnvironment : IDisposable
{
    internal const float DayLengthSeconds = 180f;
    static readonly SunCycleSettings Cycle = new()
    {
        LatitudeDegrees = 52f,
        SolarDeclinationDegrees = 15f,
        HeadingDegrees = -48.4f,
        NightKey = NightKeyMode.Moon,
        MoonKeyColor = new Color(0f, 0f, 0f, 1f),
    };

    readonly PixelPostProcessSettings _post;
    readonly SkySettings _previousSky;
    readonly SunCycleState _previousLight;
    readonly ShadowMode _previousShadows;
    readonly bool _previousStars;
    float _timeOfDay = 0.3f;
    bool _disposed;

    internal bool Paused { get; set; }

    internal Room3DEnvironment(PixelPostProcessSettings post)
    {
        _post = post;
        _previousSky = post.Sky;
        _previousLight = new SunCycleState(post.LightDirection, 0f, post.Sky.HorizonColor,
            post.Sky.ZenithColor, post.Sky.SunColor, post.Sky.SunEnabled, post.LightColor,
            post.AmbientColor, post.FillLightColor, discDirectionOverride: post.Sky.SunDirectionOverride);
        _previousShadows = post.Quality.Shadows.Mode;
        _previousStars = post.Starfield;
        post.Sky = new SkySettings { Enabled = true };
        post.Starfield = false;
        post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
        SunCycle.Apply(SunCycle.Evaluate(_timeOfDay, Cycle), post);
    }

    internal void Advance(float dt)
    {
        if (_disposed || Paused || !float.IsFinite(dt) || dt <= 0f) return;
        _timeOfDay = (_timeOfDay + dt / DayLengthSeconds) % 1f;
        SunCycle.Apply(SunCycle.Evaluate(_timeOfDay, Cycle), _post);
    }

    internal void ToggleBackground()
    {
        _post.Sky.Enabled = !_post.Sky.Enabled;
        _post.Starfield = !_post.Sky.Enabled;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _post.Sky = _previousSky;
        _post.Starfield = _previousStars;
        _post.Quality.Shadows.Mode = _previousShadows;
        SunCycle.Apply(_previousLight, _post);
    }
}
