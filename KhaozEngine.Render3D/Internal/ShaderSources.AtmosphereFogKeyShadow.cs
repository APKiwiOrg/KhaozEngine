namespace KhaozEngine.Render3D.Internal;

internal static partial class ShaderSources
{
    // Lit receivers splice this after AtmosphereFogGlsl, with LightingCommonGlsl already available.
    internal const string AtmosphereFogKeyShadowGlsl = @"
// Mirrors AtmosphereFogKeyShadow.TapDistance. Equal in-scatter shares in a uniform-density grey segment.
float atmosphereFogTapDistance(float distanceWorld, float onsetWorld, float greyTransmission, float u) {
    if (distanceWorld == 0.0 || greyTransmission <= 0.0) return 0.0;
    float fraction = greyTransmission >= 1.0 - 1e-6 ? u
        : -log(1.0 - u * (1.0 - greyTransmission)) / -log(greyTransmission);
    float opticalDistance = atmosphereOnsetDistance(distanceWorld, onsetWorld) * fraction;
    return onsetWorld == 0.0 ? opticalDistance
        : min(distanceWorld, (opticalDistance + sqrt(opticalDistance * opticalDistance
            + 4.0 * opticalDistance * onsetWorld)) / 2.0);
}
// Mirrors AtmosphereFogKeyShadow.Visibility. One POINT comparison per tap, never a derivative or normal offset.
float atmosphereFogKeyVisibility(vec3 fragmentRelative, vec3 transmission, texture2D shadowAtlas, sampler shadowSamp) {
    int taps = int(Atmo.FogShadow.z + 0.5);
    if (taps == 0 || ShadowParams.y <= 0.0 || Atmo.FogShadow.x + Atmo.FogShadow.y == 0.0) return 1.0;
    float distanceWorld;
    vec3 view = atmosphereSegmentView(fragmentRelative, distanceWorld);
    float grey = (transmission.r + transmission.g + transmission.b) / 3.0;
    int count = int(ShadowParams.x + 0.5), previous = 0;
    float margin = ShadowParams2.x * 2.0, sum = 0.0;
    for (int i = 0; i < 16; i++) {
        if (i >= taps) break;
        float s = atmosphereFogTapDistance(distanceWorld, Atmo.FogSource.w, grey, (float(i) + 0.5) / float(taps));
        vec3 point = Atmo.Camera.xyz + view * s;
        float lit = 1.0;
        for (int j = 0; j < 4; j++) {
            int c = previous + j;
            if (c >= count) break;
            vec2 uv; float z;
            if (!projectCascade(c, point, vec3(0.0), 0.0, margin, uv, z)) continue;
            previous = c;
            float atlasScale = 1.0 / float(count);
            vec2 atlasUv = vec2(uv.x * atlasScale + float(c) * atlasScale, uv.y);
            float storedDepth = textureLod(sampler2D(shadowAtlas, shadowSamp), atlasUv, 0.0).r;
            lit = z - (ShadowParams.z + ShadowParams.w) <= storedDepth ? 1.0 : 0.0;
            if (lit < 1.0 && c == count - 1) {
                float edge = min(min(uv.x, 1.0 - uv.x), min(uv.y, 1.0 - uv.y));
                float border = ShadowParams2.z;
                float fade = " + CascadeShadowSelectionSource.CoverageExpression + @";
                lit = 1.0 - fade;
            }
            break;
        }
        sum += lit;
    }
    float visibility = sum / float(taps);
    return visibility < 1.0 ? mix(1.0, visibility, ShadowParams.y) : 1.0;
}
// Mirrors AtmosphereFog.Shade's non-additive path, with visibility estimated by AtmosphereFogKeyShadow.Visibility.
vec3 atmosphereFogKeyShadowed(vec3 colour, vec3 fragmentRelative, bool enabled, texture2D shadowAtlas, sampler shadowSamp) {
    if (!enabled) return colour;
    vec3 transmission = atmosphereTransmittance(fragmentRelative);
    if (all(equal(transmission, vec3(1.0)))) return colour;
    float visibility = atmosphereFogKeyVisibility(fragmentRelative, transmission, shadowAtlas, shadowSamp);
    vec3 haze = atmosphereFogColour(fragmentRelative, visibility);
    return colour * transmission + haze * (vec3(1.0) - transmission);
}
";
}
