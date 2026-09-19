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

// ---- Point-light shadows (issue #1002) ----------------------------------------------------------------
// The atlas is six FACE columns by one row per shadowed light, each cell holding LINEAR distance from the light
// over that light's radius, cleared to 1.0. So the receiver needs no matrices at all: pick the face from the
// light-to-fragment direction, read the cell, and compare against its own normalized distance. No backend depth
// convention enters this, which is exactly why the pass stores distance rather than projected depth.

// THE CUBE-MAP FACE CONVENTION, mirrored VERBATIM from PointShadowMath.FaceAndUv (design doc decision 3). Faces
// 0..5 are +X, -X, +Y, -Y, +Z, -Z. d is the direction FROM the light TO the fragment.
void pointShadowFace(vec3 d, out float face, out vec2 uv) {
    vec3 a = abs(d);
    float ma; float sc; float tc;
    if (a.x >= a.y && a.x >= a.z) {
        ma = a.x; face = d.x > 0.0 ? 0.0 : 1.0;
        sc = d.x > 0.0 ? -d.z : d.z; tc = -d.y;
    } else if (a.y >= a.z) {
        ma = a.y; face = d.y > 0.0 ? 2.0 : 3.0;
        sc = d.x; tc = d.y > 0.0 ? d.z : -d.z;
    } else {
        ma = a.z; face = d.z > 0.0 ? 4.0 : 5.0;
        sc = d.z > 0.0 ? d.x : -d.x; tc = -d.y;
    }
    uv = (vec2(sc, tc) / max(ma, 1e-6)) * 0.5 + 0.5;
}

// ONE STORED DISTANCE FOR AN ARBITRARY DIRECTION out of the light, in the cell belonging to `slot`. This is the
// whole reason the soft filter can be wider than one texel without seaming: it runs the face select PER TAP, so a
// tap that leaves the +Z face lands on the +X face's own cell rather than on the clamped edge of the cell it
// started in. The clamp that is left is the half-texel inset inside the chosen cell, which keeps a cell's own
// bilinear footprint off its neighbours.
float pointShadowDepthAt(texture2D atlas, sampler samp, vec3 dir, float slot) {
    float face; vec2 uv;
    pointShadowFace(dir, face, uv);
    vec2 texel = PointShadowAtlas.xy;
    vec2 cellSize = vec2(1.0 / PointShadowAtlas.w, 1.0 / max(PointShadowAtlas.z, 1.0));
    vec2 cellMin = vec2(face, slot) * cellSize;
    vec2 tap = clamp(cellMin + uv * cellSize, cellMin + texel * 0.5, cellMin + cellSize - texel * 0.5);
    return texture(sampler2D(atlas, samp), tap).r;
}

// The per-fragment rotation of both discs below, off the ABSOLUTE world position (the render-frame one plus the
// render origin). Nailing it to the world rather than to the screen is what stops the dither crawling over a
// surface as the camera moves, and taking the origin in means a floating-origin shift does not re-roll it either.
// Each half is FOLDED into a 16 m cell before they are summed, never after: a render origin exists because the
// world is far from zero, and an unfolded sum at 1e5 m has a float step of 8 mm and feeds sin an argument in the
// millions, where neighbouring fragments collapse onto one rotation and the penumbra bands. Folded, the argument
// stays under 130 wherever the world is, and the phase repeats every 16 m, far wider than any one penumbra.
float pointShadowDither(vec3 worldPos) {
    vec3 w = fract(fract(worldPos * 0.0625) + fract(RenderOrigin.xyz * 0.0625));
    return fract(sin(dot(w, vec3(12.9898, 78.233, 37.719))) * 43758.5453);
}

// BLOCKER SEARCH: the average stored distance of the taps that stand in front of this receiver, or -1 when none
// of them does. Six taps (the centre plus five on two rings, so an occluder INSIDE the disc is not stepped over)
// across a disc whose angular radius is the emitter's apparent size, which is the region that could possibly
// shade this fragment at all. The centre tap is what keeps the soft path at least as occluding as the hard one:
// without it a thin caster dead ahead would be reported as nothing found and the light would leak through it.
float pointShadowBlockerSearch(texture2D atlas, sampler samp, vec3 dir, vec3 tx, vec3 ty, float slot,
                               float searchAngle, float d, float bias, float rotation) {
    float sum = 0.0; float hits = 0.0;
    for (int i = 0; i < 6; i++) {
        vec3 t = dir;
        if (i > 0) {
            float a = rotation + float(i) * 1.2566371;          // five taps, 72 degrees apart
            float r = (i % 2 == 0) ? 0.55 : 1.0;                // two rings on one pass
            t = normalize(dir + (tx * cos(a) + ty * sin(a)) * (searchAngle * r));
        }
        float stored = pointShadowDepthAt(atlas, samp, t, slot);
        if (stored + bias < d) { sum += stored; hits += 1.0; }  // this tap has something in front of the receiver
    }
    return hits > 0.0 ? sum / hits : -1.0;
}

// The filter itself: nine taps on a disc of the given angular radius, COMPARE FIRST and FILTER AFTER exactly as
// the cascade PCF does, so the atlas clear value can never be averaged into a tap next to a gap. Two rings again,
// rotated per fragment, which is what turns nine taps into something that reads as a gradient rather than as nine
// shadows.
float pointShadowFilterDisc(texture2D atlas, sampler samp, vec3 dir, vec3 tx, vec3 ty, float slot,
                            float angle, float d, float bias, float rotation) {
    float lit = step(d, pointShadowDepthAt(atlas, samp, dir, slot) + bias);
    for (int i = 0; i < 8; i++) {
        float a = rotation + float(i) * 0.7853982;              // 45 degrees apart
        float r = (i % 2 == 0) ? 0.6 : 1.0;
        vec3 t = normalize(dir + (tx * cos(a) + ty * sin(a)) * (angle * r));
        lit += step(d, pointShadowDepthAt(atlas, samp, t, slot) + bias);
    }
    return lit / 9.0;
}

// THE SOFT PATH, which is contact hardening rather than a blur: the blocker search says how far in front of this
// receiver the occluder stands, and the kernel is widened in proportion, so the shadow is crisp where it touches
// what casts it (a door frame) and spreads the deeper into the room the receiver stands. The penumbra is
// lightSize * (dReceiver - dBlocker) / dBlocker in metres AT THE RECEIVER, which becomes an angle off the light by
// dividing by the receiver's own distance, and the whole thing is held under maxPenumbraTexels of the face's
// angular texel size (a 90 degree face over faceResolution texels) so nine taps always still describe one edge.
float samplePointShadowSoft(texture2D atlas, sampler samp, vec3 toL, float dist, float radius, float ndlRaw,
                            vec4 params, vec3 worldPos) {
    if (dist <= 1e-4) return 1.0;                               // on the light itself: no direction to sample by
    vec3 dir = -toL / max(dist, 1e-6);                          // light -> fragment, unit
    float d = dist / max(radius, 1e-6);
    float bias = params.y + params.z * (1.0 - ndlRaw);
    float lightSize = PointShadowFilter.y;
    float texelAngle = 1.5707963 / max(PointShadowFilter.w, 1.0);
    float maxAngle = PointShadowFilter.z * texelAngle;

    // A basis PERPENDICULAR TO THE RAY, so every offset below is an angle away from the direction being sampled
    // rather than a step across one face's UV. The up vector is swapped near the poles for the usual reason: a
    // cross product with a parallel vector is zero and normalize would divide by it.
    vec3 up = abs(dir.y) < 0.999 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
    vec3 tx = normalize(cross(up, dir));
    vec3 ty = cross(dir, tx);
    float rotation = pointShadowDither(worldPos) * 6.2831853;

    float searchAngle = min(lightSize / max(dist, 1e-3), maxAngle);
    float blocker = pointShadowBlockerSearch(atlas, samp, dir, tx, ty, params.x, searchAngle, d, bias, rotation);
    if (blocker < 0.0) return 1.0;                              // nothing occluding: fully lit, nothing filtered

    float dBlocker = max(blocker * radius, 1e-3);               // the atlas stores distance over radius
    float width = lightSize * max(dist - dBlocker, 0.0) / dBlocker;
    float angle = min(width / max(dist, 1e-3), maxAngle);
    return pointShadowFilterDisc(atlas, samp, dir, tx, ty, params.x, angle, d, bias, rotation);
}

// One point light's shadow term: 1 = fully lit, 0 = fully occluded. toL is the fragment-to-light vector (so the
// face select negates it), dist its length, radius the light's, ndl the already-computed N.L, and params is that
// light's PointShadowParams entry - (slot, bias, slopeBias, 0). The caller has already checked slot >= 0.
// COMPARE FIRST, FILTER AFTER, exactly as the cascade PCF does: four taps at half-texel offsets each fetch one
// stored distance, compare it, and only the 0/1 results are averaged. Every tap is CLAMPED inside its own cell so
// it can never bleed into the neighbouring face column or the next light's row.
// ndlRaw is the UNBANDED dot(N, L): the slope bias is an acne remedy and has to read the real grazing angle, not
// the cel-quantised one the diffuse term uses.
// THIS BODY IS FROZEN. It is what PointShadowFilter.Hard renders, and Hard is the pre-soft picture byte for byte,
// so it shares no helper with the soft path above however much the two look alike: a helper factored out of both
// is one line away from moving a picture that is pinned not to move (PointShadowFilterShaderTests).
float samplePointShadowHard(texture2D atlas, sampler samp, vec3 toL, float dist, float radius, float ndlRaw, vec4 params) {
    float face; vec2 uv;
    pointShadowFace(-toL, face, uv);
    vec2 texel = PointShadowAtlas.xy;                      // one ATLAS texel, in atlas UV
    vec2 cellSize = vec2(1.0 / PointShadowAtlas.w, 1.0 / max(PointShadowAtlas.z, 1.0));
    vec2 cellMin = vec2(face, params.x) * cellSize;
    vec2 base = cellMin + uv * cellSize;
    vec2 lo = cellMin + texel * 0.5;
    vec2 hi = cellMin + cellSize - texel * 0.5;
    float d = dist / max(radius, 1e-6);
    float bias = params.y + params.z * (1.0 - ndlRaw);
    float lit = 0.0;
    for (int oy = 0; oy < 2; oy++) {
        for (int ox = 0; ox < 2; ox++) {
            vec2 tap = clamp(base + (vec2(float(ox), float(oy)) - 0.5) * texel, lo, hi);
            float stored = texture(sampler2D(atlas, samp), tap).r;
            lit += step(d, stored + bias);                 // receiver nearer than the stored caster => lit
        }
    }
    return lit * 0.25;
}

// The mode is read off the frame block rather than compiled in, so one program serves both filters and a settings
// change costs no pipeline rebuild. PointShadowFilter.x is 0 for Hard and 1 for Soft.
float samplePointShadow(texture2D atlas, sampler samp, vec3 toL, float dist, float radius, float ndlRaw,
                        vec4 params, vec3 worldPos) {
    if (PointShadowFilter.x < 0.5) return samplePointShadowHard(atlas, samp, toL, dist, radius, ndlRaw, params);
    return samplePointShadowSoft(atlas, samp, toL, dist, radius, ndlRaw, params, worldPos);
}

// pointAtlas/pointSamp are parameters for the same reason sampleKeyShadow's are: their set/binding differ per
// fragment and GLSL cannot reference a fragment's own bindings from a shared function.
void computeLighting(texture2D pointAtlas, sampler pointSamp, vec3 N, vec3 worldPos, float specStrength, float specExp, float keyShadow, out vec3 diffuse, out vec3 specColor) {
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
        // The UNBANDED grazing angle, kept for the slope bias alone. Cel banding quantises ndl below, and a bias
        // computed off a stepped angle steps with it, so the acne it exists to hide comes back in bands. Read
        // only inside the gated branch, so the unshadowed path is the arithmetic it always was.
        float ndlRaw = ndl;
        if (bands >= 1.0) ndl = floor(ndl*bands+0.5)/bands;
        // Smooth falloff: 1 at the light, easing to exactly 0 at its radius; scaled by intensity.
        float f = clamp(1.0 - (dist*dist)/max(radius*radius, 1e-6), 0.0, 1.0);
        float att = f * f * PointColorIntensity[i].w;
        // A slot below zero is every light that carries no shadow map, which is every light in a scene that never
        // asked for one: the branch is not taken, nothing is sampled, and the accumulation below is the pre-shadow
        // arithmetic byte for byte.
        // ZERO ATTENUATION IS THE OTHER HALF of that gate and it is free: a fragment past this light's radius has
        // att = 0, so whatever the sample answered it would be multiplied by nothing. Skipping it keeps the cost
        // of the soft filter on the one to three lights that actually reach a fragment, and it moves no picture,
        // because 0 * lit is 0 for every lit in 0..1.
        if (att > 0.0 && PointShadowParams[i].x >= 0.0)
            att *= samplePointShadow(pointAtlas, pointSamp, toL, dist, radius, ndlRaw, PointShadowParams[i],
                                     worldPos);
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
