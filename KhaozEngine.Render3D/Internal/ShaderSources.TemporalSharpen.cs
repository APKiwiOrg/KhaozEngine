namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// The temporal sharpen (1 of the renderer's shader sources, TEMPORAL-RESOLVE-UPSCALING-DESIGN section 4). Part of
    /// the <see cref="ShaderSources"/> partial: see ShaderSources.cs for the shared contract.
    /// </summary>
    internal static partial class ShaderSources
    {
        // ---- Temporal sharpen (RCAS). One fullscreen pass after the tonemap, at display resolution, only while
        //      temporal anti-aliasing runs with a sharpness above zero. Mirrors TemporalSharpenMath: keep the noise
        //      term, the limiter, the no-bound rule for a ring channel at 0 or 1 and the weighted blend in sync. The
        //      five taps are a symmetric cross, so the vertical flip every fullscreen pass applies does not change
        //      the result, and the pass counts in the chain's flip parity like any other. Every tap is textureLod at
        //      level 0 because only the base level of a post target is current mid-chain (the FXAA note). The sampler
        //      is point with clamp addressing, so the ring at the frame edge repeats the edge rather than wrapping to
        //      the far side. ----
        public const string TemporalSharpenFrag = @"#version 450
layout(set=0, binding=0) uniform texture2D Src;
layout(set=0, binding=1) uniform sampler Samp;
layout(set=0, binding=2) uniform Sharpen { vec4 Params; }; // .xy = 1/source size, .z = sharpness 0..1, .w reserved
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
const float RCAS_LIMIT = 0.1875;   // 0.25 - 1/16, the largest lobe RCAS allows
const float RCAS_NO_BOUND = 0.25;  // past the limit, so a channel carrying it restricts nothing
float luma2(vec3 c) { return c.b * 0.5 + (c.r * 0.5 + c.g); }
void main() {
    vec2 t = Params.xy;
    vec4 centre = textureLod(sampler2D(Src, Samp), vUv, 0.0);
    vec3 e = centre.rgb;
    vec3 b = textureLod(sampler2D(Src, Samp), vUv - vec2(0.0, t.y), 0.0).rgb;
    vec3 d = textureLod(sampler2D(Src, Samp), vUv - vec2(t.x, 0.0), 0.0).rgb;
    vec3 f = textureLod(sampler2D(Src, Samp), vUv + vec2(t.x, 0.0), 0.0).rgb;
    vec3 h = textureLod(sampler2D(Src, Samp), vUv + vec2(0.0, t.y), 0.0).rgb;
    // Noise: a centre standing alone against its ring gets at most half the lobe.
    float bL = luma2(b), dL = luma2(d), eL = luma2(e), fL = luma2(f), hL = luma2(h);
    float nz = 0.25 * (bL + dL + fL + hL) - eL;
    float range = max(max(max(bL, dL), max(eL, fL)), hL) - min(min(min(bL, dL), min(eL, fL)), hL);
    nz = clamp(abs(nz) / max(range, 1e-5), 0.0, 1.0);
    nz = 1.0 - 0.5 * nz;
    // The limiter: the largest negative lobe that keeps the result inside 0 to 1 given the ring and the centre.
    vec3 mn4 = min(min(b, d), min(f, h));
    vec3 mx4 = max(max(b, d), max(f, h));
    vec3 hitMin = min(mn4, e) / max(4.0 * mx4, vec3(1e-5));
    vec3 hitMax = (vec3(1.0) - max(mx4, e)) / min(4.0 * mn4 - vec3(4.0), vec3(-1e-5));
    // A channel whose four ring taps all sit at 0 sets no lower bound, and one whose four all sit at 1 no upper bound.
    hitMin = mix(hitMin, vec3(RCAS_NO_BOUND), equal(mx4, vec3(0.0)));
    hitMax = mix(hitMax, vec3(-RCAS_NO_BOUND), equal(mn4, vec3(1.0)));
    vec3 lobeRgb = max(-hitMin, hitMax);
    float lobe = max(-RCAS_LIMIT, min(max(lobeRgb.r, max(lobeRgb.g, lobeRgb.b)), 0.0)) * Params.z;
    lobe *= nz;
    vec3 c = (lobe * (b + d + f + h) + e) / (4.0 * lobe + 1.0);
    oColor = vec4(clamp(c, 0.0, 1.0), centre.a);   // keeps the background marker alpha
}";
    }
}
