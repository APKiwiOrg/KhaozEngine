namespace KhaozEngine.Render3D.Internal;

internal static partial class ShaderSources
{
    // Binding-agnostic local fog. Each consumer supplies its dense atmosphere suffix separately.
    internal const string AtmosphereFogGlsl = AtmosphereTransportGlsl + AtmosphereSkyConsumersGlsl + @"
// Mirrors AtmosphereFog.ExponentialSegmentAverage. The argument is always non-negative.
float atmosphereSegmentAverage(float heightChange) {
    return heightChange < 1e-4
        ? 1.0 - heightChange / 2.0 + heightChange * heightChange / 6.0
        : (1.0 - exp(-heightChange)) / heightChange;
}
// Mirrors AtmosphereFog.OpticalDepthTerm. Reversal avoids descending-ray exponential overflow.
float atmosphereOpticalDepthTerm(float distanceKm, float startHeightKm, float endHeightKm, float scaleHeightKm) {
    if (distanceKm == 0.0) return 0.0;
    if (isinf(distanceKm)) return distanceKm;
    float start = max(startHeightKm, 0.0), end = max(endHeightKm, 0.0);
    float heightChange = (end - start) / scaleHeightKm;
    float lowerHeight = heightChange < 0.0 ? end : start;
    float lowerEndpointDensity = exp(-lowerHeight / scaleHeightKm);
    return (distanceKm * lowerEndpointDensity) * atmosphereSegmentAverage(abs(heightChange));
}
// Mirrors AtmosphereFog.CoefficientOpticalDepth. Vacuum stays zero even for an infinite integral.
float atmosphereCoefficientDepth(float coefficient, float depth) {
    return coefficient == 0.0 ? 0.0 : coefficient * depth;
}
// Mirrors AtmosphereFog.SegmentView. Scale the relative displacement to avoid length-square overflow.
vec3 atmosphereSegmentView(vec3 fragmentRelative, out float distanceWorld) {
    vec3 displacement = fragmentRelative - Atmo.Camera.xyz;
    float scale = max(max(abs(displacement.x), abs(displacement.y)), abs(displacement.z));
    if (scale == 0.0) {
        distanceWorld = 0.0;
        return vec3(1.0, 0.0, 0.0);
    }
    vec3 scaled = displacement / scale;
    float scaledLength = length(scaled);
    distanceWorld = scale * scaledLength;
    return scaled / scaledLength;
}
// Mirrors AtmosphereFog.Transmittance. Settings are validated before the shared UBO is uploaded.
vec3 atmosphereTransmittance(vec3 fragmentRelative) {
    if (Atmo.Tint.w < 0.5 || Atmo.Planet.w == 0.0) return vec3(1.0);
    float distanceWorld;
    atmosphereSegmentView(fragmentRelative, distanceWorld);
    if (distanceWorld == 0.0) return vec3(1.0);
    float k = Atmo.Planet.w / Atmo.Planet.z;
    float distanceKm = distanceWorld * k;
    float startHeightKm = max(Atmo.Camera.y + Atmo.Origin.y, 0.0) * k;
    float endHeightKm = max(fragmentRelative.y + Atmo.Origin.y, 0.0) * k;
    float rayleighDepth = all(equal(Atmo.Rayleigh.rgb, vec3(0.0))) ? 0.0
        : atmosphereOpticalDepthTerm(distanceKm, startHeightKm, endHeightKm, Atmo.Rayleigh.w);
    float mieDepth = Atmo.Mie.y == 0.0 ? 0.0
        : atmosphereOpticalDepthTerm(distanceKm, startHeightKm, endHeightKm, Atmo.Mie.z);
    float hazeDepth = Atmo.GroundHaze.y == 0.0 ? 0.0
        : atmosphereOpticalDepthTerm(distanceKm, startHeightKm, endHeightKm, Atmo.GroundHaze.z);
    float greyDepth = atmosphereCoefficientDepth(Atmo.Mie.y, mieDepth)
        + atmosphereCoefficientDepth(Atmo.GroundHaze.y, hazeDepth);
    vec3 tau = vec3(atmosphereCoefficientDepth(Atmo.Rayleigh.r, rayleighDepth) + greyDepth,
        atmosphereCoefficientDepth(Atmo.Rayleigh.g, rayleighDepth) + greyDepth,
        atmosphereCoefficientDepth(Atmo.Rayleigh.b, rayleighDepth) + greyDepth);
    return exp(-tau);
}
// Mirrors AtmosphereFog.ClampDirectionToGeometricHorizon. Height here is physical, without the aerial scale.
vec3 atmosphereFogSkyDirection(vec3 view, float cameraHeightKm) {
    float radiusRatio = Atmo.Planet.x / (Atmo.Planet.x + cameraHeightKm);
    float horizonY = -sqrt(max(0.0, 1.0 - radiusRatio * radiusRatio));
    if (view.y >= horizonY) return view;
    float horizontalLength = length(view.xz);
    vec2 azimuth = horizontalLength == 0.0 ? vec2(1.0, 0.0) : view.xz / horizontalLength;
    float horizontal = sqrt(max(0.0, 1.0 - horizonY * horizonY));
    return vec3(azimuth.x * horizontal, horizonY, azimuth.y * horizontal);
}
// Mirrors AtmosphereFog.Shade's physical sky lookup.
vec3 atmosphereFogColour(vec3 fragmentRelative) {
    float distanceWorld;
    vec3 view = atmosphereSegmentView(fragmentRelative, distanceWorld);
    float cameraHeightKm = max((Atmo.Camera.y + Atmo.Origin.y) / Atmo.Planet.z, 0.0);
    vec3 skyDirection = atmosphereFogSkyDirection(view, cameraHeightKm);
    return atmosphereSkyValue(skyDirection);
}
// Mirrors AtmosphereFog.Shade and AtmosphereFog.Compose. SkyValue already applies tint exactly once.
// Enabled comes from the consumer's current frame upload, never a retained atmosphere ring segment.
vec3 atmosphereFog(vec3 colour, vec3 fragmentRelative, bool additive, bool enabled) {
    if (!enabled) return colour;
    vec3 transmission = atmosphereTransmittance(fragmentRelative);
    if (additive) return colour * transmission;
    if (all(equal(transmission, vec3(1.0)))) return colour;
    vec3 haze = atmosphereFogColour(fragmentRelative);
    return colour * transmission + haze * (vec3(1.0) - transmission);
}
// Mirrors AtmosphereFog.ComposePremultiplied. Coverage remains in the caller's alpha lane.
vec3 atmosphereFogPremultiplied(vec3 rgb, float coverage, vec3 fragmentRelative, float additivity, bool enabled) {
    if (!enabled) return rgb;
    vec3 transmission = atmosphereTransmittance(fragmentRelative);
    if (all(equal(transmission, vec3(1.0)))) return rgb;
    if (coverage == 0.0 || additivity == 1.0) return rgb * transmission;
    vec3 haze = atmosphereFogColour(fragmentRelative);
    return rgb * transmission + coverage * (1.0 - additivity) * haze * (vec3(1.0) - transmission);
}
";

    // Only post-tonemap overlays include this chunk, after TonemapCommonGlsl.
    internal const string AtmosphereDisplayFogGlsl = @"
// Mirrors AtmosphereFog.ComposeDisplay. Only haze is mapped into display space.
vec3 atmosphereFogDisplay(vec3 rgb, vec3 fragmentRelative, bool additive, bool enabled) {
    if (!enabled) return rgb;
    vec3 transmission = atmosphereTransmittance(fragmentRelative);
    if (additive) return rgb * transmission;
    if (all(equal(transmission, vec3(1.0)))) return rgb;
    vec3 haze = atmosphereFogColour(fragmentRelative);
    vec3 displayHaze = Atmo.Tone.x > 0.5 ? tonemapColour(haze, Atmo.Tone.yzw) : clamp(haze, 0.0, 1.0);
    return rgb * transmission + displayHaze * (vec3(1.0) - transmission);
}
";
}
