namespace KhaozEngine.Render3D.Internal;

/// <summary>The common air-to-water refraction constant and still-surface sun travel.</summary>
internal static class WaterRefractionShaderSource
{
    internal const string Glsl = @"
const float UnderwaterRefractiveIndex = 1.333;
vec3 waterSunTravel(vec3 lightDirection) {
    return refract(normalize(lightDirection), vec3(0.0, 1.0, 0.0), 1.0 / UnderwaterRefractiveIndex);
}
";
}
