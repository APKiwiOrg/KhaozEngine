namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// The shared lighting block spliced into the model and terrain fragments (1 of the renderer's shader sources).
    /// Part of the <see cref="ShaderSources"/> partial: see ShaderSources.cs for the shared contract
    /// (GLSL #version 450, cross-compiled at load via the GPU seam's SPIR-V path).
    /// </summary>
    internal static partial class ShaderSources
    {
        // ---- Shared lighting block, single-sourced into ModelFrag and SplatFrag (const-string concatenation is
        //      compile-time, so both remain `public const string`). This is the ONE copy of the key+fill directional
        //      lighting, cel banding, Blinn-Phong specular, and the up-to-16 dynamic point-light accumulation. Both
        //      fragments splice this in verbatim and call computeLighting(), so a lighting edit is single-place by
        //      construction (no more hand-kept "KEEP IN SYNC" comments). The two things that legitimately differ per
        //      caller - the specular strength source and the specular exponent - are function PARAMETERS: ModelFrag
        //      passes its per-instance vSpecParams-derived values, SplatFrag passes the terrain-roughness-derived
        //      values (blended terrain layers carry no per-instance material). The function reads the frame UBO
        //      globals (LightDir/LightColor/FillDir/FillColor/Params/CameraPos/PointPosRadius/PointColorIntensity),
        //      which are declared identically in both fragments' `U` block, and takes the lit normal N, the world
        //      position, and the two spec params; it returns the diffuse and specular accumulation via out params.
        //      The caller keeps the final `lit = albedo*(Ambient+diffuse)+specColor+emissive` line because albedo /
        //      ambient / emissive are derived differently per pass. The statement text and float op order here are
        //      copied byte-for-byte from the old duplicated blocks (only vWorldPos was renamed to the `worldPos`
        //      parameter), so behaviour is bit-identical on every backend.
        public const string LightingCommonGlsl = @"
// Cascaded 3x3 PCF shadow lookup. Returns 1 = fully lit, 0 = fully in shadow, from the key light's CASCADED depth
// atlas: N frustum-slice ortho cascades (tightest first) packed side-by-side in one R32F texture. Picks the tightest
// cascade whose light-clip projection of worldPos lands inside its map, manual-depth-compares a 3x3 PCF kernel in
// that cascade's atlas column, and either cross-fades toward the NEXT cascade's result near an INNER cascade's
// border (so the texel-density step at a hand-off is invisible) or fades the term to fully lit toward the
// OUTERMOST cascade's UV border AND beyond the view-distance limit (so the coverage edge is invisible, no hard
// box). worldPos is pushed off the surface along Ngeo by a PER-CASCADE normal offset (grows with the cascade's
// texel world size) before projecting - the standard normal-offset bias, scaled per cascade so far cascades do not
// acne and near ones do not detach, plus a constant + slope-scaled depth bias. This lives in the shared block so
// ModelFrag and SplatFrag shadow identically, and is factored into two helpers: projectCascade (light-clip
// projection + map-bounds test) and pcfCascade (the 3x3 atlas-column average), so sampleKeyShadow can call either
// twice, once for the selected cascade and once more for its neighbour inside the blend band. It reads the frame
// UBO shadow tail directly (like computeLighting): ShadowMat[4] = per-cascade world->light-clip, ShadowParams =
// (cascadeCount, strength, constBias, slopeBias), ShadowParams2 = (texelStep, maxDistance, borderFrac,
// cascadeBlendFrac), ShadowNormalOffsets = per-cascade normal-offset world size. Only the atlas texture + sampler
// are parameters, because their set/binding differ per fragment and GLSL cannot reference a fragment's own
// bindings from a shared function. Texture + sampler are passed SEPARATELY (Vulkan-style) and combined at the
// point of use inside. GLSL forbids a sampler2D(...) constructor as a call ARGUMENT.
bool projectCascade(int i, vec3 worldPos, vec3 Ngeo, float slopeSin, float margin, out vec2 uv, out float z) {
    vec3 samplePos = worldPos + Ngeo * (ShadowNormalOffsets[i] * slopeSin);
    vec4 lc = ShadowMat[i] * vec4(samplePos, 1.0);
    uv = vec2(0.0); z = 0.0;
    if (lc.w <= 0.0) return false;
    vec3 proj = lc.xyz / lc.w;                          // light-clip - xy in [-1,1], z in [0,1]
    uv = proj.xy * 0.5 + 0.5;                           // to [0,1] cascade-local texture space
    uv.y = 1.0 - uv.y;                                  // render-target SAMPLING flips V vs the clip-Y the depth
                                                        // pass rasterized with (the same Y-origin trap as before)
    z = proj.z;
    // Both depth bounds, matching the CPU mirror ShadowMapMath.SelectCascade exactly. z < 0 is a receiver in FRONT
    // of this cascade's near plane: it has no valid depth information here (nothing up-light of the near plane is
    // recorded at its own depth), so it must fall through to the next, wider cascade rather than read the map and
    // come back fully lit with a hard edge. The GPU used to test only z > 1.0, which is how a receiver could sit
    // just outside a cascade's depth range and still claim it (issue #394).
    return !(uv.x < margin || uv.x > 1.0 - margin || uv.y < margin || uv.y > 1.0 - margin || z < 0.0 || z > 1.0);
}

// One cascade's 3x3 PCF average inside its atlas column. uv is cascade-local, depth is already biased. Each
// tap is CLAMPED inside the column then mapped to atlas U so it never bleeds into a neighbour cascade.
// COMPARE FIRST, FILTER AFTER. The nine taps each fetch ONE stored depth (the atlas sampler is POINT, see
// ShadowMapRenderer's ctor), compare it, and only the 0/1 comparison RESULTS are averaged. Averaging stored
// depths instead would blend the atlas clear value (1.0 = no caster) into every tap next to a gap, which can only
// ever lighten the result, and erases a dithered caster's shadow outright outside cascade 0 (issue #391).
float pcfCascade(texture2D shadowAtlas, sampler shadowSamp, int cascade, int count, vec2 uv, float depth, float texelStep) {
    float atlasScaleX = 1.0 / float(count);
    float atlasBiasX = float(cascade) * atlasScaleX;
    float halfTexel = texelStep * 0.5;
    float lit = 0.0;
    for (int oy = -1; oy <= 1; oy++) {
        for (int ox = -1; ox <= 1; ox++) {
            vec2 luv = uv + vec2(float(ox), float(oy)) * texelStep;
            luv = clamp(luv, vec2(halfTexel), vec2(1.0 - halfTexel));
            vec2 auv = vec2(luv.x * atlasScaleX + atlasBiasX, luv.y);
            float d = texture(sampler2D(shadowAtlas, shadowSamp), auv).r;
            lit += (depth <= d) ? 1.0 : 0.0;            // receiver in front of the stored caster depth => lit
        }
    }
    return lit / 9.0;
}

float sampleKeyShadow(texture2D shadowAtlas, sampler shadowSamp, vec3 worldPos, vec3 Ngeo, float ndl) {
    float strength = ShadowParams.y;
    if (strength <= 0.0) return 1.0;                    // shadow atlas inactive this frame => fully lit
    int count = int(ShadowParams.x + 0.5);
    float texelStep = ShadowParams2.x;                  // 1/perCascadeResolution (a PCF step in cascade-local UV)
    float slopeSin = sqrt(max(0.0, 1.0 - ndl * ndl));   // grazing factor: largest where acne is worst
    float slope = clamp(1.0 - ndl, 0.0, 1.0);
    float bias = ShadowParams.z + ShadowParams.w * slope;
    float margin = texelStep * 2.0;

    // Select the tightest cascade containing the fragment (slice cascades ordered near to far => lowest
    // index wins). A fragment past its slice border falls outward to the next cascade's coverage.
    int sel = -1; vec2 selUv = vec2(0.0); float selZ = 0.0;
    for (int i = 0; i < 4; i++) {
        if (i >= count) break;
        vec2 uv; float z;
        if (!projectCascade(i, worldPos, Ngeo, slopeSin, margin, uv, z)) continue;
        sel = i; selUv = uv; selZ = z; break;
    }
    if (sel < 0) return 1.0;                            // beyond every cascade => lit (coverage edge)

    float lit = pcfCascade(shadowAtlas, shadowSamp, sel, count, selUv, selZ - bias, texelStep);
    float edge = min(min(selUv.x, 1.0 - selUv.x), min(selUv.y, 1.0 - selUv.y));

    if (sel == count - 1) {
        // Outermost cascade: nothing beyond it, so fade to fully lit toward its UV border (the coverage-limit
        // fade, sitting at ShadowMaxDistance - ShadowParams2.y documents that distance for downstream effects).
        float border = ShadowParams2.z;
        float fade = smoothstep(0.0, border, edge);
        return mix(1.0, mix(1.0, lit, strength), fade);
    }

    // Inner cascade near its border: cross-fade toward the NEXT cascade's result so the texel-density step at
    // a hand-off is invisible (the hard cut showed as a square seam sliding with the camera). If the next
    // cascade does not cover this fragment (a slice-sphere overlap gap at an extreme angle), keep this
    // cascade's result: a hard fallback beats sampling garbage.
    float blend = ShadowParams2.w;
    if (blend > 0.0 && edge < blend) {
        vec2 uv2; float z2;
        if (projectCascade(sel + 1, worldPos, Ngeo, slopeSin, margin, uv2, z2)) {
            float lit2 = pcfCascade(shadowAtlas, shadowSamp, sel + 1, count, uv2, z2 - bias, texelStep);
            lit = mix(lit2, lit, smoothstep(0.0, blend, edge));   // at the border (edge 0) fully the next cascade
        }
    }
    // strength 1 removes the key light fully in shadow, below 1 leaves a partial key term, and the fade/blend paths above ease the result toward fully lit.
    return mix(1.0, lit, strength);
}

void computeLighting(vec3 N, vec3 worldPos, float specStrength, float specExp, float keyShadow, out vec3 diffuse, out vec3 specColor) {
    float ndlKey  = max(dot(N, -normalize(LightDir.xyz)), 0.0);
    float ndlFill = max(dot(N, -normalize(FillDir.xyz)), 0.0);
    float bands = Params.x;
    if (bands >= 1.0) { ndlKey = floor(ndlKey*bands+0.5)/bands; ndlFill = floor(ndlFill*bands+0.5)/bands; }
    // Shadow multiplies ONLY the key light's diffuse (fill + ambient + point lights are untouched), so a shadow
    // reads as shade rather than blackness. keyShadow == 1 (no shadow map) is bit-identical to the pre-shadow term.
    diffuse = LightColor.rgb*(ndlKey*keyShadow) + FillColor.rgb*ndlFill;
    vec3 V = normalize(CameraPos.xyz - worldPos);
    vec3 H = normalize(-normalize(LightDir.xyz) + V);
    float spec = pow(max(dot(N,H),0.0), specExp) * specStrength * step(0.0001, ndlKey) * keyShadow;
    specColor = LightColor.rgb*spec;
    // Dynamic point/effect lights (muzzle flashes, explosions, thrusters): accumulate diffuse (+ cheap
    // specular) with a windowed distance attenuation, on top of the key+fill term and back-face gated by
    // max(dot(N,L),0). Params.y is the host-capped active count; zero leaves diffuse/specColor untouched,
    // so the lit term stays bit-identical to the key+fill+ambient path.
    int npl = int(Params.y);
    for (int i = 0; i < npl; i++) {
        vec3 toL = PointPosRadius[i].xyz - worldPos;
        float radius = PointPosRadius[i].w;
        float dist = length(toL);
        vec3 L = (dist > 1e-4) ? toL / dist : vec3(0.0);
        float ndl = max(dot(N, L), 0.0);
        if (bands >= 1.0) ndl = floor(ndl*bands+0.5)/bands;
        // Smooth falloff: 1 at the light, easing to exactly 0 at its radius; scaled by intensity.
        float f = clamp(1.0 - (dist*dist)/max(radius*radius, 1e-6), 0.0, 1.0);
        float att = f * f * PointColorIntensity[i].w;
        vec3 lc = PointColorIntensity[i].rgb;
        diffuse += lc * (ndl * att);
        vec3 Hp = normalize(L + V);
        float sp = pow(max(dot(N,Hp),0.0), specExp) * specStrength * step(0.0001, ndl);
        specColor += lc * (sp * att);
    }
}
";
    }
}
