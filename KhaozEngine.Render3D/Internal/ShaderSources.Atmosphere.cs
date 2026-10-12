namespace KhaozEngine.Render3D.Internal;

internal static partial class ShaderSources
{
    // One std140 declaration for producers and consumers. The instance avoids collisions with existing blocks.
    const string AtmosphereBlockGlsl = @"
    vec4 Planet;
    vec4 Rayleigh;
    vec4 Mie;
    vec4 Ozone;
    vec4 GroundHaze;
    vec4 Sun;
    vec4 Moon;
    vec4 Tint;
    vec4 Floors;
    vec4 Camera;
    vec4 Origin;
    vec4 SunT;
    vec4 MoonT;
    vec4 Right;
    vec4 Up;
    vec4 Back;
    vec4 Ray;
    vec4 Res;
    vec4 Tone;
} Atmo;
";

    // Consumer prefixes differ, while the block instance and resource names stay binding-agnostic.
    internal const string AtmosphereBindingsAt1Glsl = @"
layout(std140, set=0, binding=1) uniform Atmosphere {
" + AtmosphereBlockGlsl + @"
layout(set=0, binding=2) uniform texture2D AtmoSkyView;
layout(set=0, binding=3) uniform sampler AtmoSkySampler;
";

    internal const string AtmosphereBindingsAt3Glsl = @"
layout(std140, set=0, binding=3) uniform Atmosphere {
" + AtmosphereBlockGlsl + @"
layout(set=0, binding=4) uniform texture2D AtmoSkyView;
layout(set=0, binding=5) uniform sampler AtmoSkySampler;
";

    internal const string AtmosphereBindingsAt4Glsl = @"
layout(std140, set=0, binding=4) uniform Atmosphere {
" + AtmosphereBlockGlsl + @"
layout(set=0, binding=5) uniform texture2D AtmoSkyView;
layout(set=0, binding=6) uniform sampler AtmoSkySampler;
";

    internal const string AtmosphereBindingsAt6Glsl = @"
layout(std140, set=0, binding=6) uniform Atmosphere {
" + AtmosphereBlockGlsl + @"
layout(set=0, binding=7) uniform texture2D AtmoSkyView;
layout(set=0, binding=8) uniform sampler AtmoSkySampler;
";

    internal const string AtmosphereBindingsAt7Glsl = @"
layout(std140, set=0, binding=7) uniform Atmosphere {
" + AtmosphereBlockGlsl + @"
layout(set=0, binding=8) uniform texture2D AtmoSkyView;
layout(set=0, binding=9) uniform sampler AtmoSkySampler;
";

    internal const string AtmosphereBindingsAt11Glsl = @"
layout(std140, set=0, binding=11) uniform Atmosphere {
" + AtmosphereBlockGlsl + @"
layout(set=0, binding=12) uniform texture2D AtmoSkyView;
layout(set=0, binding=13) uniform sampler AtmoSkySampler;
";

    const string AtmosphereProducerUniformsGlsl = @"
layout(std140, set=0, binding=0) uniform Atmosphere {
" + AtmosphereBlockGlsl;

    const string AtmosphereMultiBindingsGlsl = AtmosphereProducerUniformsGlsl + @"
layout(set=0, binding=1) uniform texture2D AtmoTransmittance;
layout(set=0, binding=2) uniform sampler AtmoClampSampler;
";

    const string AtmosphereSkyBindingsGlsl = AtmosphereProducerUniformsGlsl + @"
layout(set=0, binding=1) uniform texture2D AtmoTransmittance;
layout(set=0, binding=2) uniform texture2D AtmoMultiScattering;
layout(set=0, binding=3) uniform sampler AtmoClampSampler;
";

    // Geometry, density and mappings shared by producers and later fog/sky consumers.
    internal const string AtmosphereTransportGlsl = @"
const float ATMO_PI = 3.14159265358979323846;

// Mirrors AtmosphereLutMapping.Sub.
float AtmoSub(float x, float size) { return (clamp(x, 0.0, 1.0) * (size - 1.0) + 0.5) / size; }
// Mirrors AtmosphereLutMapping.Unsub.
float AtmoUnsub(float x, float size) { return clamp((x * size - 0.5) / (size - 1.0), 0.0, 1.0); }

// Mirrors AtmosphereModel.SphereRoots. Factored c reduces cancellation near the planet surface in float.
bool AtmoSphereRoots(vec3 p, vec3 d, float radius, out vec2 roots) {
    float projection = dot(p, d);
    float r = length(p);
    float c = (r - radius) * (r + radius);
    float discriminant = projection * projection - c;
    roots = vec2(0.0);
    if (discriminant < 0.0) return false;
    float q = -projection - (projection < 0.0 ? -1.0 : 1.0) * sqrt(discriminant);
    float other = q == 0.0 ? 0.0 : c / q;
    roots = vec2(min(q, other), max(q, other));
    return true;
}

// Mirrors AtmosphereShaderMath.PlanetOccludes. Require penetration beyond length/dot float32 roundoff.
bool AtmoPlanetOccludes(vec3 p, vec3 d) {
    float r = length(p), projection = dot(p, d);
    float c = (r - Atmo.Planet.x) * (r + Atmo.Planet.x);
    float epsilon = (8.0 * 1.1920929e-7) * max(r * r, Atmo.Planet.x * Atmo.Planet.x);
    return projection < 0.0 && projection * projection - c > epsilon;
}

// Mirrors AtmosphereModel.DensityAtHeight. Haze is a frame-only aerial control, outside static transport.
vec3 AtmoDensity(float height) {
    float h = max(height, 0.0);
    return vec3(exp(-h / Atmo.Rayleigh.w), exp(-h / Atmo.Mie.z),
        max(0.0, 1.0 - abs(h - Atmo.Ozone.w) / Atmo.GroundHaze.w));
}

// Mirrors AtmosphereLutMapping.TransmittanceUv.
vec2 AtmoTransmittanceUv(float radius, float mu) {
    float bottom = Atmo.Planet.x, top = Atmo.Planet.y;
    float r = clamp(radius, bottom, top);
    mu = clamp(mu, -1.0, 1.0);
    float h = sqrt(max((top - bottom) * (top + bottom), 0.0));
    float rho = sqrt(max((r - bottom) * (r + bottom), 0.0));
    float d = -r * mu + sqrt(max((top - r) * (top + r) + r * r * mu * mu, 0.0));
    float minimum = top - r, range = rho + h - minimum;
    return vec2(AtmoSub(range > 0.0 ? (d - minimum) / range : 0.0, 256.0),
        AtmoSub(h > 0.0 ? rho / h : 0.0, 64.0));
}

// Mirrors AtmosphereShaderMath.TransmittanceParameters, the float32 AtmosphereLutMapping counterpart.
vec2 AtmoTransmittanceParameters(vec2 uv) {
    float bottom = Atmo.Planet.x, top = Atmo.Planet.y;
    float x = AtmoUnsub(uv.x, 256.0), y = AtmoUnsub(uv.y, 64.0);
    float h = sqrt(max((top - bottom) * (top + bottom), 0.0));
    float rho = h * y, r = sqrt(bottom * bottom + rho * rho);
    float minimum = top - r, d = minimum + x * (rho + h - minimum);
    float mu = d > 0.0 ? (h * h - rho * rho - d * d) / (2.0 * r * d) : 1.0;
    return vec2(r, clamp(mu, -1.0, 1.0));
}

// Mirrors AtmosphereLutMapping.MultiUv.
vec2 AtmoMultiUv(float height, float mu) {
    return vec2(AtmoSub((mu + 1.0) * 0.5, 32.0), AtmoSub(height / (Atmo.Planet.y - Atmo.Planet.x), 32.0));
}
// Mirrors AtmosphereLutMapping.MultiParameters.
vec2 AtmoMultiParameters(vec2 uv) {
    return vec2(AtmoUnsub(uv.y, 32.0) * (Atmo.Planet.y - Atmo.Planet.x), AtmoUnsub(uv.x, 32.0) * 2.0 - 1.0);
}
// Mirrors AtmosphereLutMapping.HorizonAngle.
float AtmoHorizonAngle(float radius) {
    float r = max(radius, Atmo.Planet.x);
    return acos(clamp(-sqrt(max((r - Atmo.Planet.x) * (r + Atmo.Planet.x), 0.0)) / r, -1.0, 1.0));
}
// Mirrors AtmosphereLutMapping.SkyUv, including periodic azimuth (AtmosphereLutMapping.Wrap).
vec2 AtmoSkyUv(vec3 direction, float radius) {
    vec3 d = normalize(direction);
    float u = fract(atan(d.z, d.x) / (2.0 * ATMO_PI));
    float theta = acos(clamp(d.y, -1.0, 1.0)), horizon = AtmoHorizonAngle(radius);
    float v = theta <= horizon ? 0.5 * (1.0 - sqrt(max(1.0 - theta / horizon, 0.0)))
        : 0.5 + 0.5 * sqrt(clamp((theta - horizon) / (ATMO_PI - horizon), 0.0, 1.0));
    return vec2(u, AtmoSub(v, 108.0));
}
// Mirrors AtmosphereLutMapping.SkyDirection.
vec3 AtmoSkyDirection(vec2 uv, float radius) {
    float u = fract(uv.x), v = AtmoUnsub(uv.y, 108.0), horizon = AtmoHorizonAngle(radius);
    float theta = v <= 0.5 ? horizon * (1.0 - (1.0 - 2.0 * v) * (1.0 - 2.0 * v))
        : horizon + (ATMO_PI - horizon) * (2.0 * v - 1.0) * (2.0 * v - 1.0);
    float azimuth = 2.0 * ATMO_PI * u;
    return vec3(sin(theta) * cos(azimuth), cos(theta), sin(theta) * sin(azimuth));
}

// Mirrors AtmosphereModel.RayleighPhase.
float AtmoRayleighPhase(float mu) { return 3.0 * (1.0 + mu * mu) / (16.0 * ATMO_PI); }
// Mirrors AtmosphereModel.MiePhase (Cornette-Shanks).
float AtmoMiePhase(float mu) {
    float g = Atmo.Mie.w, gg = g * g;
    mu = clamp(mu, -1.0, 1.0);
    return 3.0 * (1.0 - gg) * (1.0 + mu * mu)
        / (8.0 * ATMO_PI * (2.0 + gg) * pow(1.0 + gg - 2.0 * g * mu, 1.5));
}
// Mirrors AtmosphereModel.SegmentWeight, including the zero-extinction Taylor limit.
float AtmoSegmentWeight(float extinction, float width) {
    float tau = extinction * width;
    if (tau < 1e-4) return width * (1.0 - tau * 0.5 + tau * tau / 6.0 - tau * tau * tau / 24.0);
    return (1.0 - exp(-tau)) / extinction;
}
// Mirrors AtmosphereModel.SegmentIntegral.
vec3 AtmoSegmentIntegral(vec3 source, vec3 extinction, float width) {
    return source * vec3(AtmoSegmentWeight(extinction.x, width), AtmoSegmentWeight(extinction.y, width),
        AtmoSegmentWeight(extinction.z, width));
}
// Mirrors AtmosphereModel.SegmentTransmittance.
vec3 AtmoSegmentTransmittance(vec3 extinction, float width) { return exp(-extinction * width); }

// Mirrors AtmosphereModel.ScatteringInterval, conservatively keeping float32 grazes unblocked.
bool AtmoScatteringInterval(vec3 p, vec3 d, out float start, out float end, out bool groundHit) {
    start = 0.0; end = 0.0; groundHit = false;
    if (length(p) < Atmo.Planet.x) return false;
    vec2 topRoots, groundRoots;
    if (!AtmoSphereRoots(p, d, Atmo.Planet.y, topRoots) || topRoots.y <= 0.0) return false;
    start = max(topRoots.x, 0.0); end = topRoots.y;
    if (AtmoPlanetOccludes(p, d) && AtmoSphereRoots(p, d, Atmo.Planet.x, groundRoots) && groundRoots.x >= start
        && groundRoots.x <= end && groundRoots.y > groundRoots.x) {
        end = groundRoots.x;
        groundHit = true;
    }
    return true;
}
";

    // Sky and later reflection consumers share the same directional LUT and solar radiance.
    internal const string AtmosphereSkyConsumersGlsl = AtmosphereTransmittanceIntegralGlsl + @"
// Mirrors AtmosphereModel.SkyValue. Ground radiance is already present below the geometric horizon.
vec3 atmosphereSkyValue(vec3 direction) {
    float radius = Atmo.Planet.x + max(Atmo.Camera.w, 0.0);
    vec3 radiance = textureLod(sampler2D(AtmoSkyView, AtmoSkySampler), AtmoSkyUv(direction, radius), 0.0).rgb;
    return max(radiance * (Atmo.Sun.w * ATMO_PI), vec3(Atmo.Floors.w)) * Atmo.Tint.rgb;
}
// Mirrors AtmosphereModel.SunDiscValue, with an additional one-pixel edge feather.
vec3 atmosphereSunDisc(vec3 direction) {
    vec3 d = normalize(direction), sun = normalize(Atmo.Sun.xyz);
    float angle = atan(length(cross(d, sun)), clamp(dot(d, sun), -1.0, 1.0));
    float radius = Atmo.Res.w, feather = max(fwidth(angle), 1e-6);
    float edge = 1.0 - smoothstep(radius - feather * 0.5, radius + feather * 0.5, angle);
    vec3 p = vec3(0.0, Atmo.Planet.x + max(Atmo.Camera.w, 0.0), 0.0);
    if (edge <= 0.0 || AtmoPlanetOccludes(p, d)) return vec3(0.0);
    float relative = angle / radius;
    float limb = (0.4 + 0.6 * sqrt(max(0.0, 1.0 - relative * relative))) / 0.8;
    float discScale = Atmo.Sun.w / (sin(radius) * sin(radius));
    // Integrate only the visible disc footprint. A set centre must not black out an unoccluded upper limb.
    return AtmoTransmittanceIntegral(p, d) * Atmo.Tint.rgb
        * (discScale * limb * edge * clamp(Atmo.SunT.w, 0.0, 1.0));
}
";

    // Only scattering passes read static input textures. Transmittance needs geometry and density alone.
    const string AtmosphereScatteringGlsl = @"
// Mirrors AtmosphereTables.SampleTransmittance.
vec3 AtmoSampleTransmittance(float radius, float mu) {
    return textureLod(sampler2D(AtmoTransmittance, AtmoClampSampler), AtmoTransmittanceUv(radius, mu), 0.0).rgb;
}
#ifdef ATMO_MULTI_SCATTERING
// Mirrors AtmosphereTables.SampleMulti.
vec3 AtmoSampleMulti(float height, float mu) {
    return textureLod(sampler2D(AtmoMultiScattering, AtmoClampSampler), AtmoMultiUv(height, mu), 0.0).rgb;
}
#endif
// Mirrors AtmosphereModel.SourceTransmittance. Definite penetration blocks, a rounded tangent remains visible.
vec3 AtmoSourceTransmittance(vec3 p, vec3 light) {
    float r = length(p), projection = dot(p, light);
    if (AtmoPlanetOccludes(p, light)) return vec3(0.0);
    return AtmoSampleTransmittance(r, projection / r);
}
// Mirrors AtmosphereModel.DirectGroundBounce.
vec3 AtmoDirectGroundBounce(vec3 point, vec3 light, vec3 throughput) {
    float mu = max(dot(point, light) / length(point), 0.0);
    return throughput * AtmoSampleTransmittance(Atmo.Planet.x, mu) * (mu * Atmo.GroundHaze.x / ATMO_PI);
}
// Mirrors AtmosphereModel.IntegrateScattering, used by SingleScattering and SkyRadiance.
vec3 AtmoIntegrateScattering(vec3 p, vec3 view, vec3 light, int steps, bool isotropic,
    out vec3 response) {
    response = vec3(0.0);
    float start, end; bool groundHit;
    if (!AtmoScatteringInterval(p, view, start, end, groundHit)) return vec3(0.0);
    float mu = clamp(dot(view, light), -1.0, 1.0);
    float rp = isotropic ? 1.0 / (4.0 * ATMO_PI) : AtmoRayleighPhase(mu);
    float mp = isotropic ? rp : AtmoMiePhase(mu);
    vec3 radiance = vec3(0.0), throughput = vec3(1.0);
    float previous = start;
    for (int i = 0; i < steps; i++) {
        float fraction = float(i + 1) / float(steps);
        float next = start + (end - start) * fraction * fraction;
        vec3 point = p + view * ((previous + next) * 0.5);
        float r = length(point), height = r - Atmo.Planet.x;
        float lightMu = clamp(dot(point, light) / r, -1.0, 1.0);
        vec3 density = AtmoDensity(height), rayleigh = Atmo.Rayleigh.rgb * density.x;
        float mie = Atmo.Mie.x * density.y;
        vec3 scattering = rayleigh + vec3(mie);
        vec3 extinction = rayleigh + vec3(Atmo.Mie.y * density.y) + Atmo.Ozone.rgb * density.z;
        vec3 source = AtmoSourceTransmittance(point, light) * (rayleigh * rp + vec3(mie * mp));
#ifdef ATMO_MULTI_SCATTERING
        source += AtmoSampleMulti(height, lightMu) * scattering;
#endif
        float width = next - previous;
        radiance += throughput * AtmoSegmentIntegral(source, extinction, width);
        response += throughput * AtmoSegmentIntegral(scattering, extinction, width);
        throughput *= AtmoSegmentTransmittance(extinction, width);
        previous = next;
    }
    if (groundHit) radiance += AtmoDirectGroundBounce(p + view * end, light, throughput);
    return radiance;
}
";

    const string AtmosphereTransmittanceIntegralGlsl = @"
// Mirrors AtmosphereShaderMath.TransmittanceIntegral with 256 quadratic midpoint segments.
// Callers clip planet-intersecting rays or use the transmittance producer's unoccluded mapping.
// A float32 ground-intersection retest can falsely reject its tangent endpoint, so omit it here.
vec3 AtmoTransmittanceIntegral(vec3 p, vec3 d) {
    vec2 topRoots;
    if (!AtmoSphereRoots(p, d, Atmo.Planet.y, topRoots) || topRoots.y <= 0.0) return vec3(1.0);
    float start = max(topRoots.x, 0.0), end = topRoots.y;
    vec3 depth = vec3(0.0);
    float previous = start;
    for (int i = 0; i < 256; i++) {
        float fraction = float(i + 1) / 256.0;
        float next = start + (end - start) * fraction * fraction;
        vec3 point = p + d * ((previous + next) * 0.5);
        depth += AtmoDensity(length(point) - Atmo.Planet.x) * (next - previous);
        previous = next;
    }
    return exp(-(Atmo.Rayleigh.rgb * depth.x + vec3(Atmo.Mie.y * depth.y) + Atmo.Ozone.rgb * depth.z));
}
";

    internal const string AtmosphereTransmittanceFrag = "#version 450\n" + AtmosphereProducerUniformsGlsl
        + AtmosphereTransportGlsl + AtmosphereTransmittanceIntegralGlsl + @"
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
// Mirrors AtmosphereShaderMath.TransmittanceDirection. The mapped domain ends at the planet tangent.
vec3 AtmoUnoccludedTransmittanceDirection(float r, float mu) {
    float horizon = -sqrt(max((r - Atmo.Planet.x) * (r + Atmo.Planet.x), 0.0)) / r;
    mu = max(mu, horizon);
    return normalize(vec3(sqrt(max(1.0 - mu * mu, 0.0)), mu, 0.0));
}
// Mirrors AtmosphereShaderMath.TransmittanceTexel, the float32 AtmosphereTables.BuildTransmittance counterpart.
void main() {
    vec2 parameters = AtmoTransmittanceParameters(vUv);
    oColor = vec4(AtmoTransmittanceIntegral(vec3(0.0, parameters.x, 0.0),
        AtmoUnoccludedTransmittanceDirection(parameters.x, parameters.y)), 1.0);
}
";

    internal const string AtmosphereMultiScatteringFrag = "#version 450\n" + AtmosphereMultiBindingsGlsl
        + AtmosphereTransportGlsl + AtmosphereScatteringGlsl + @"
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
// Mirrors AtmosphereTables.EqualSolidAngleDirection (8 by 8 stratified directions).
vec3 AtmoEqualSolidAngleDirection(int zCell, int azimuthCell) {
    float z = -1.0 + 2.0 * (float(zCell) + 0.5) / 8.0;
    float azimuth = 2.0 * ATMO_PI * (float(azimuthCell) + 0.5) / 8.0;
    float horizontal = sqrt(1.0 - z * z);
    return vec3(horizontal * cos(azimuth), z, horizontal * sin(azimuth));
}
// Mirrors AtmosphereTables.MultiScatteringSeries.
vec3 AtmoMultiScatteringSeries(vec3 radiance, vec3 response) {
    return radiance / (vec3(1.0) - min(response, vec3(0.999)));
}
// Mirrors AtmosphereTables.BuildMultiScattering, with 20 segments and no recursive source.
void main() {
    vec2 parameters = AtmoMultiParameters(vUv);
    vec3 p = vec3(0.0, Atmo.Planet.x + parameters.x, 0.0);
    vec3 light = vec3(sqrt(max(1.0 - parameters.y * parameters.y, 0.0)), parameters.y, 0.0);
    vec3 radiance = vec3(0.0), response = vec3(0.0);
    for (int z = 0; z < 8; z++) {
        for (int a = 0; a < 8; a++) {
            vec3 integratedResponse;
            radiance += AtmoIntegrateScattering(p, AtmoEqualSolidAngleDirection(z, a), light, 20, true, integratedResponse);
            response += integratedResponse;
        }
    }
    oColor = vec4(AtmoMultiScatteringSeries(radiance / 64.0, response / 64.0), 1.0);
}
";

    internal const string AtmosphereSkyViewFrag = "#version 450\n#define ATMO_MULTI_SCATTERING\n" + AtmosphereSkyBindingsGlsl
        + AtmosphereTransportGlsl + AtmosphereScatteringGlsl + @"
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
// Mirrors AtmosphereModel.SkyRadiance for each independent body. SkyValue applies art controls later.
void main() {
    float radius = Atmo.Planet.x + max(Atmo.Camera.w, 0.0);
    vec3 p = vec3(0.0, radius, 0.0), d = AtmoSkyDirection(vUv, radius), response;
    vec3 radiance = AtmoIntegrateScattering(p, d, Atmo.Sun.xyz, 32, false, response);
    if (Atmo.Moon.w > 0.0)
        radiance += Atmo.Moon.w * AtmoIntegrateScattering(p, d, Atmo.Moon.xyz, 32, false, response);
    oColor = vec4(radiance, 1.0);
}
";
}
