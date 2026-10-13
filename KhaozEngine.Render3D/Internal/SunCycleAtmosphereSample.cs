using System.Numerics;

namespace KhaozEngine.Render3D.Internal;

/// <summary>Untinted solar geometry, elevation weights and body fades retained before palette application.</summary>
internal readonly record struct SunCycleAtmosphereSample(
    Vector3 SunDirection, Vector3 PaletteWeights, float KeyVisibility, NightKeyMode NightKey,
    float SunOpacity, float MoonOpacity);
