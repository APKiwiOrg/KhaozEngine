using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>Celestial and art state shared by CPU lighting and atmosphere rendering. Directions point
/// toward the bodies. MoonFraction is moon illuminance relative to the sun, including any art scaling.
/// KeyVisibility fades the selected key independently of scattering. Opacities control the discs only.</summary>
public readonly record struct AtmosphereFrame(Vector3 SunDirection, Vector3 MoonDirection, float MoonFraction,
    Vector3 Tint, KeyLightSource KeySource = KeyLightSource.Sun, float KeyVisibility = 1f,
    float SunOpacity = 1f, float MoonOpacity = 1f);

/// <summary>Atmosphere-derived colours in the engine's Lambert lighting units. Key, ambient, fill and
/// horizon include the frame tint once. Source transmittances are untinted transmission fractions.</summary>
public readonly record struct AtmosphereLighting(Vector3 Key, Vector3 Ambient, Vector3 Fill, Vector3 Horizon,
    Vector3 SunTransmittance, Vector3 MoonTransmittance);
