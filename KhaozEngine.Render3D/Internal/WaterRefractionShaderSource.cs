using System;
using System.Globalization;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal;

/// <summary>The common air-to-water refraction constant and still-surface sun travel.</summary>
internal static class WaterRefractionShaderSource
{
    // The one spelling of the index: the GLSL text splices it and the CPU value parses it.
    const string RefractiveIndexText = "1.333";

    internal static readonly float RefractiveIndex = float.Parse(RefractiveIndexText, CultureInfo.InvariantCulture);

    internal const string RefractiveIndexGlsl = @"
const float UnderwaterRefractiveIndex = " + RefractiveIndexText + ";";

    internal const string Glsl = RefractiveIndexGlsl + @"
vec3 waterSunTravel(vec3 lightDirection) {
    return refract(normalize(lightDirection), vec3(0.0, 1.0, 0.0), 1.0 / UnderwaterRefractiveIndex);
}
";

    /// <summary>CPU form of <c>waterSunTravel</c> for a normalized downward <paramref name="sun"/>.</summary>
    internal static Vector3 SunTravel(Vector3 sun)
    {
        float eta = 1f / RefractiveIndex;
        float cosine = sun.Y;
        float k = 1f - eta * eta * (1f - cosine * cosine);
        return eta * sun - (eta * cosine + MathF.Sqrt(k)) * Vector3.UnitY;
    }
}
