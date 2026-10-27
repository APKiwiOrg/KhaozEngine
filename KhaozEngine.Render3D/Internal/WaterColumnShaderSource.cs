namespace KhaozEngine.Render3D.Internal;

/// <summary>Opt-in water transport. Keep the integrals, H estimate and fog weights matched to the CPU mirror.</summary>
internal static class WaterColumnShaderSource
{
    internal const string Glsl = @"
layout(set=0, binding=15) uniform texture2D ColumnSceneColor;
layout(set=0, binding=16) uniform sampler ColumnColorSampler;

float waterColumnQ(float k, float d) {
    float x = k * d;
    if (abs(x) < 0.0001) return d * (1.0 - x * 0.5 + x * x / 6.0);
    return (1.0 - exp(-x)) / k;
}

float waterColumnInvertQ(float k, float mass) {
    float x = k * mass;
    if (abs(x) < 0.0001) return mass * (1.0 + x * 0.5 + x * x / 3.0);
    return -log(max(1.0 - x, 1e-30)) / k;
}

float waterColumnIntegral(float sigma, float mu, float mu0, float offset, float start, float end) {
    float split = clamp(-offset / mu, start, end);
    float above = exp(-sigma * start) * waterColumnQ(sigma, split - start);
    float h = max(0.0, offset + mu * split);
    float below = exp(-sigma * (split + h / mu0)) * waterColumnQ(sigma * (1.0 + mu / mu0), end - split);
    return above + below;
}

vec3 waterColumnWeights(vec3 sigma, float mu, float mu0, float offset, float start, float end) {
    return vec3(waterColumnIntegral(sigma.r, mu, mu0, offset, start, end),
                waterColumnIntegral(sigma.g, mu, mu0, offset, start, end),
                waterColumnIntegral(sigma.b, mu, mu0, offset, start, end));
}

// Constant attenuation at S cancels from the normalized CDF. Removing it prevents underflow in troughs.
float waterColumnQuantile(float sigma, float mu, float mu0, float d, float offset, float quantile) {
    if (quantile <= 0.0) return 0.0;
    if (quantile >= 1.0) return d;
    float k = sigma * (1.0 + mu / mu0);
    if (abs(k * d) < 0.0001) return quantile * d;
    float split = clamp(-offset / mu, 0.0, d);
    float above = waterColumnQ(sigma, split), belowScale = exp(-sigma * split);
    float mass = quantile * (above + belowScale * waterColumnQ(k, d - split));
    float s = mass <= above ? waterColumnInvertQ(sigma, mass)
        : split + waterColumnInvertQ(k, (mass - above) / belowScale);
    return clamp(s, 0.0, d);
}

vec3 waterColumnH(vec3 a, float mu) {
    return vec3(1.0 + 2.0 * mu) / (vec3(1.0) + 2.0 * mu * sqrt(vec3(1.0) - a));
}

float waterColumnFiniteDepthChannel(float sigma, float mu, float mu0, float d, float offset) {
    float h0 = max(0.0, offset), hd = max(0.0, offset + mu * d);
    float x = sigma * (d + (hd - h0) / mu0);
    float finite = abs(x) < 0.0001 ? x * (1.0 - x * 0.5 + x * x / 6.0) : 1.0 - exp(-x);
    return exp(-sigma * h0 / mu0) * finite;
}

vec3 waterColumnFiniteDepth(vec3 sigma, float mu, float mu0, float d, float offset) {
    return vec3(waterColumnFiniteDepthChannel(sigma.r, mu, mu0, d, offset),
                waterColumnFiniteDepthChannel(sigma.g, mu, mu0, d, offset),
                waterColumnFiniteDepthChannel(sigma.b, mu, mu0, d, offset));
}

bool waterColumnUnproject(vec2 xy, float depth, out vec3 point) {
    point = vec3(0.0);
    vec4 p = InvViewProj * vec4(xy, depth, 1.0);
    if (any(isnan(p)) || any(isinf(p)) || abs(p.w) <= 1e-8) return false;
    point = p.xyz / p.w;
    return !any(isnan(point)) && !any(isinf(point));
}

vec3 waterColumnIncident(vec2 xy) {
    vec3 nearPoint, farPoint;
    if (!waterColumnUnproject(xy, 0.0, nearPoint) || !waterColumnUnproject(xy, 0.5, farPoint))
        return vec3(0.0, -1.0, 0.0);
    vec3 delta = farPoint - nearPoint;
    float len = length(delta);
    return isnan(len) || isinf(len) || len <= 1e-8 ? vec3(0.0, -1.0, 0.0) : delta / len;
}

vec3 waterColumnGround(vec2 xy, float depth, vec3 S) {
    vec3 point;
    if (depth >= 0.0 && depth < 1.0 && waterColumnUnproject(xy, depth, point)) return point;
    // A finite far depth also works for an infinite-far projection. The original colour remains the bed source.
    if (waterColumnUnproject(xy, 0.9999, point)) return point;
    return S;
}

float waterColumnDepth(float rawDepth, float bedDepth) {
    float meanSigma = (ColumnExtinction.r + ColumnExtinction.g + ColumnExtinction.b) / 3.0;
    float limit = 8.0 * (3.0 / meanSigma);
    if (OpticsParams.y < 0.5 && (isnan(rawDepth) || isinf(rawDepth) || rawDepth < 0.0 || rawDepth >= 1.0))
        return min(max(bedDepth, 0.0), limit);
    return max(bedDepth, 0.0);
}

float waterColumnJitter(vec2 pixel, float phase) {
    return fract(52.9829189 * fract(dot(pixel + phase * vec2(5.588238, 5.588238), vec2(0.06711056, 0.00583715))));
}

vec3 waterColumnComposite(vec3 S, vec3 N, vec3 groundWorld, float bedDepth, vec3 reflection,
                          float fresnel, float glint, float foam, float shoreFade) {
    vec3 copiedBed = texelFetch(sampler2D(ColumnSceneColor, ColumnColorSampler), ivec2(gl_FragCoord.xy), 0).rgb;
    ivec2 size = textureSize(sampler2D(ColumnSceneColor, ColumnColorSampler), 0);
    vec2 xy = vec2(gl_FragCoord.x / float(size.x) * 2.0 - 1.0, 1.0 - gl_FragCoord.y / float(size.y) * 2.0);
    vec3 v = refract(waterColumnIncident(xy), N, 1.0 / UnderwaterRefractiveIndex);
    float mu = max(-v.y, 0.0001);
    float d = max(bedDepth, 0.0) / mu;
    vec3 sigma = ColumnExtinction.rgb, a = ColumnAlbedo.rgb;
    vec3 T = exp(-sigma * d);
    vec3 t = waterSunTravel(LightDir.xyz);
    float mu0 = max(-t.y, 0.0001), offset = SurfShape.z - S.y;
    bool sunActive = LightDir.y < 0.0;
    vec3 sun = sunActive ? LightColor.rgb : vec3(0.0), ambient = LitPrefix[6].rgb;
    float surfaceVisibility = sunActive ? waterColumnSurfaceVisibility(S, N) : 0.0;
    glint *= surfaceVisibility;
    float jitter = waterColumnJitter(gl_FragCoord.xy, ColumnAlbedo.w);
    float meanSigma = (sigma.r + sigma.g + sigma.b) / 3.0;
    vec3 sunWeights = vec3(0.0);
    float start = 0.0, meanVisibility = 0.0;
    for (int i = 0; i < 4; i++) {
        float end = i == 3 ? d : waterColumnQuantile(meanSigma, mu, mu0, d, offset, float(i + 1) / 4.0);
        float s = waterColumnQuantile(meanSigma, mu, mu0, d, offset, (float(i) + jitter) / 4.0);
        vec3 X = S + v * s;
        float depth = max(0.0, SurfShape.z - X.y);
        vec3 E = X - t * (depth / mu0);
        E.y = max(X.y, SurfShape.z);
        float visibility = sunActive ? waterColumnEntryVisibility(E) : 1.0;
        sunWeights += waterColumnWeights(sigma, mu, mu0, offset, start, end) * visibility;
        meanVisibility += visibility * 0.25;
        start = end;
    }
    // /4 is the isotropic single-scatter phase in engine light units, pi times physical irradiance units.
    vec3 singleSun = sun * ColumnScattering.rgb * 0.25 * sunWeights;
    vec3 singleAmbient = ambient * ColumnScattering.rgb * 0.25 * waterColumnWeights(sigma, mu, 1.0, offset, 0.0, d);
    vec3 h = waterColumnH(a, mu);
    vec3 multiSun = sun * a * mu0 / (4.0 * (mu + mu0)) * (h * waterColumnH(a, mu0) - vec3(1.0))
        * waterColumnFiniteDepth(sigma, mu, mu0, d, offset);
    vec3 multiSky = ambient * a / (4.0 * (mu + 1.0)) * (h * waterColumnH(a, 1.0) - vec3(1.0))
        * waterColumnFiniteDepth(sigma, mu, 1.0, d, offset);
    vec3 scatter = singleSun + singleAmbient + (multiSun + multiSky) * meanVisibility;
    float q = foam * clamp(FoamColor.a, 0.0, 1.0);
    vec3 foamLight = ambient + sun * max(dot(N, -normalize(LightDir.xyz)), 0.0) * surfaceVisibility;
    vec3 own = (1.0 - q) * ((1.0 - fresnel) * scatter + fresnel * reflection + sun * glint)
        + q * FoamColor.rgb * foamLight;
    vec3 W = (1.0 - q) * (1.0 - fresnel) * T;
    vec3 fogT = vec3(1.0), fogColor = vec3(0.0);
    if (SkyParams.y > 0.5) {
        fogT = atmosphereTransmittance(S);
        fogColor = atmosphereFogColour(S);
    }
    vec3 inside = copiedBed * W + own * fogT + fogColor * (vec3(1.0) - fogT) * (vec3(1.0) - W);
    return mix(copiedBed, inside, shoreFade);
}
";
}
