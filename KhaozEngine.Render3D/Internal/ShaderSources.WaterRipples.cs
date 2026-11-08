namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// The band-limited ripple field, shared by the water fragment and the caustic surface field so both read one
    /// copy of the maths and any later change to the spectrum reaches both. Part of the <see cref="ShaderSources"/>
    /// partial. It mirrors <see cref="RippleSpectrum"/> and <see cref="WaterMath"/> exactly.
    /// <para>
    /// A user supplies <c>KE_TWO_PI</c> by splicing <c>WaterSwellCommonGlsl</c> first and declares the
    /// <c>RippleSpectrum</c> uniform member. The block is the lifted text of the water fragment, in place, so composed
    /// water stays byte-identical. That is why <c>detailScaleFor</c> travels with it: the function sits between
    /// <c>domainWarp</c> and <c>rippleResolve</c>, and a caller with no fade simply never calls it.
    /// </para>
    /// </summary>
    internal static partial class ShaderSources
    {
        const string WaterRippleCommonGlsl = @"const int   KE_MAX_RIPPLES = 12;           // mirrors RippleSpectrum.MaxComponents
const float KE_GOLDEN_ANGLE = 2.39996323;  // mirrors RippleSpectrum.GoldenAngle
const float KE_PHASE_STRIDE = 4.74311;     // mirrors RippleSpectrum.PhaseStride
const float KE_LEGACY_SLOPE_VARIANCE = 2.72317;   // mirrors RippleSpectrum.LegacySlopeVariance

// Mirrors WaterMath.DomainWarp exactly: a slow, large-scale displacement of the sample position applied BEFORE the
// ripple layers, so their pattern is bent over a distance several times their own wavelength. Its Jacobian is
// deliberately NOT folded into the analytic slope below (see the WaterMath.DomainWarp note).
vec2 domainWarp(vec2 xz, float scrollTime, float waveScale, float warpStrength) {
    if (warpStrength <= 0.0) return xz;
    float scale = max(waveScale, 1e-4);
    float k = 0.21 / scale;
    float wt = scrollTime * 0.23;
    float ax = sin(xz.y * k + wt) + 0.7 * sin(xz.x * k * 0.57 - wt * 1.31);
    float az = cos(xz.x * k - wt * 0.79) + 0.7 * cos(xz.y * k * 0.57 + wt * 1.17);
    return xz + vec2(ax, az) * (warpStrength * scale);
}

// Mirrors WaterMath.DetailScale exactly: 1 at the camera, falling to distantScale at/after fadeDistance, so the
// fine ripple layers stop aliasing into a crawling moire in the far field.
float detailScaleFor(float camDist, float fadeDistance, float distantScale) {
    if (fadeDistance <= 0.0) return 1.0;
    float s = smoothstep(0.0, fadeDistance, camDist);
    return mix(1.0, clamp(distantScale, 0.0, 1.0), s);
}

// Mirrors RippleSpectrum.Resolve exactly: how much of a component of this wavelength survives at this pixel.
// 1 while the wavelength is comfortably wider than `samples` footprints, smoothly to 0 as it drops below. This is
// the half of band-limiting 14.24.0 left out: that release widened the specular LOBE by footprint, but left the
// normal FIELD oscillating at frequencies the pixel cannot resolve, and that is precisely what moire is.
float rippleResolve(float wavelength, float footprint, float samples) {
    if (footprint <= 0.0 || samples <= 0.0) return 1.0;
    float need = footprint * samples;
    if (need <= 1e-8) return 1.0;
    return smoothstep(0.0, 1.0, clamp(wavelength / need, 0.0, 1.0));
}

// Mirrors RippleSpectrum.Build + Slope exactly: generate the ripple spectrum from four scalars and evaluate its
// band-limited slope. Returns (dH/dx, dH/dz, removed slope variance).
//
// This replaced three fixed cosines. Three coherent cosines do not make a surface, they make a RULED pattern:
// their summed slope is constant along a family of parallel lines, the domain warp only bends those lines rather
// than breaking them, and at distance they beat against the pixel grid into moire. Headings here step by the
// golden angle so no two are parallel and no subset lines up at any count, wave numbers climb geometrically over
// several octaves, and amplitudes are normalized so the whole set carries the same slope variance the old field
// did - which is what keeps NormalStrength meaning what it meant.
vec3 waterSlope(vec2 xz, float time, float waveScale, float waveSpeed, float warpStrength, float detail,
                float footprint, float samples) {
    float scale = max(waveScale, 1e-4);
    float k0 = 1.0 / scale;
    float t = time * waveSpeed;
    vec2 p = domainWarp(xz, t, waveScale, warpStrength);

    int n = clamp(int(RippleSpectrum.x + 0.5), 1, KE_MAX_RIPPLES);
    float lac = max(RippleSpectrum.y, 1.01);
    float g = clamp(RippleSpectrum.z, 0.05, 1.5);
    float seed = RippleSpectrum.w;

    // Closed-form geometric sum, matched literally by the CPU mirror so the two round alike.
    float r = g * lac;
    float rr = r * r;
    float sumSq = abs(1.0 - rr) < 1e-6 ? float(n) : (1.0 - pow(rr, float(n))) / (1.0 - rr);
    float norm = sqrt(KE_LEGACY_SLOPE_VARIANCE / max(sumSq, 1e-6));

    float dhdx = 0.0, dhdz = 0.0, lost = 0.0;
    for (int i = 0; i < KE_MAX_RIPPLES; i++) {
        if (i >= n) break;
        float fi = float(i);
        float angle = seed + fi * KE_GOLDEN_ANGLE;
        float k = k0 * pow(lac, fi);
        float slopeAmp = norm * k0 * pow(r, fi);
        float scroll = sqrt(pow(lac, fi));             // omega ~ sqrt(k), normalized to 1 at i = 0
        float phase = fi * KE_PHASE_STRIDE + seed * (fi + 1.0) * 1.61803399;
        vec2 d = vec2(cos(angle), sin(angle));

        float keep = rippleResolve(KE_TWO_PI / max(k, 1e-8), footprint, samples);
        if (i > 0) keep *= detail;                     // DetailFadeDistance stays an artistic extra, on top

        float gg = slopeAmp * keep * cos(dot(d, p) * k + t * scroll + phase);
        dhdx += gg * d.x;
        dhdz += gg * d.y;
        lost += slopeAmp * slopeAmp * (1.0 - keep * keep) * 0.5;   // variance of a cosine is A^2/2
    }
    return vec3(dhdx, dhdz, lost);
}

// Mirrors WaterMath.SlopeToNormal exactly.
vec3 slopeToNormal(float dhdx, float dhdz, float normalStrength) {
    vec3 n = vec3(-dhdx * normalStrength, 1.0, -dhdz * normalStrength);
    float len = length(n);
    return len > 1e-8 ? n / len : vec3(0.0, 1.0, 0.0);
}";
    }
}
