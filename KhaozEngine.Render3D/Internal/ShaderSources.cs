namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// GLSL #version 450 shader sources, cross-compiled at load via the GPU seam's SPIR-V path
    /// (GLSL -> SPIR-V -> MSL/HLSL/GLSL). The model and post shaders use the separate texture2D + sampler style
    /// (not combined sampler2D) so the
    /// ResourceLayout binding order is unambiguous. The model pass writes 3 MRT color targets
    /// (lit color, encoded normal, linear-ish depth) so the edge pass never samples a depth texture.
    /// </summary>
    internal static class ShaderSources
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
// atlas: N concentric ortho cascades (tightest first) packed side-by-side in one R32F texture. Picks the tightest
// cascade whose light-clip projection of worldPos lands inside its map, manual-depth-compares a 3x3 PCF kernel in
// that cascade's atlas column, and fades the term to fully lit toward the outermost cascade's UV border AND beyond
// the view-distance limit, so the coverage edge is invisible (no hard box). worldPos is pushed off the surface
// along Ngeo by a PER-CASCADE normal offset (grows with the cascade's texel world size) before projecting - the
// standard normal-offset bias, scaled per cascade so far cascades do not acne and near ones do not detach, plus a
// constant + slope-scaled depth bias. This lives in the shared block so ModelFrag and SplatFrag shadow identically.
// It reads the frame UBO shadow tail directly (like computeLighting): ShadowMat[4] = per-cascade world->light-clip,
// ShadowParams = (cascadeCount, strength, constBias, slopeBias), ShadowParams2 = (texelStep, maxDistance,
// borderFrac, -), ShadowNormalOffsets = per-cascade normal-offset world size. Only the atlas texture + sampler are
// parameters, because their set/binding differ per fragment and GLSL cannot reference a
// fragment's own bindings from a shared function. Texture + sampler are passed SEPARATELY (Vulkan-style) and
// combined at the point of use inside. GLSL forbids a sampler2D(...) constructor as a call ARGUMENT.
float sampleKeyShadow(texture2D shadowAtlas, sampler shadowSamp, vec3 worldPos, vec3 Ngeo, float ndl) {
    float strength = ShadowParams.y;
    if (strength <= 0.0) return 1.0;                       // shadow atlas inactive this frame => fully lit
    int count = int(ShadowParams.x + 0.5);
    float texelStep = ShadowParams2.x;                     // 1/perCascadeResolution (a PCF step in cascade-local UV)
    float atlasScaleX = 1.0 / float(count);                // one cascade column's width in atlas U
    float slopeSin = sqrt(max(0.0, 1.0 - ndl * ndl));      // grazing factor: largest where acne is worst
    float slope = clamp(1.0 - ndl, 0.0, 1.0);
    float bias = ShadowParams.z + ShadowParams.w * slope;  // constant + slope-scaled depth bias

    // Select the tightest cascade containing the fragment (concentric, growing radius => lowest index wins). The
    // margin keeps the 3x3 PCF kernel inside this cascade's atlas column, so a near-border fragment falls OUTWARD to
    // the next (larger) cascade instead of a kernel that straddles the column seam.
    int sel = -1; vec2 selUv = vec2(0.0); float selDepth = 0.0;
    float margin = texelStep * 2.0;
    for (int i = 0; i < 4; i++) {
        if (i >= count) break;
        // Per-cascade normal offset: push the sample point off the surface along Ngeo, scaled by the grazing angle.
        vec3 samplePos = worldPos + Ngeo * (ShadowNormalOffsets[i] * slopeSin);
        vec4 lc = ShadowMat[i] * vec4(samplePos, 1.0);
        if (lc.w <= 0.0) continue;
        vec3 proj = lc.xyz / lc.w;                          // light-clip; xy in [-1,1], z in [0,1]
        vec2 uv = proj.xy * 0.5 + 0.5;                      // to [0,1] cascade-local texture space
        uv.y = 1.0 - uv.y;                                  // render-target SAMPLING flips V vs the clip-Y the depth
                                                            // pass rasterized with (the same Y-origin trap as before)
        if (uv.x < margin || uv.x > 1.0 - margin || uv.y < margin || uv.y > 1.0 - margin || proj.z > 1.0) continue;
        sel = i; selUv = uv; selDepth = proj.z - bias; break;
    }
    if (sel < 0) return 1.0;                                // beyond every cascade => lit (coverage edge)

    // 3x3 PCF within the selected cascade's atlas column. Offsets are in cascade-local UV, and each tap is CLAMPED inside
    // the column then mapped to atlas U (u = luv.x/count + sel/count) so it never bleeds into a neighbour cascade.
    float atlasBiasX = float(sel) * atlasScaleX;
    float halfTexel = texelStep * 0.5;
    float lit = 0.0;
    for (int oy = -1; oy <= 1; oy++) {
        for (int ox = -1; ox <= 1; ox++) {
            vec2 luv = selUv + vec2(float(ox), float(oy)) * texelStep;
            luv = clamp(luv, vec2(halfTexel), vec2(1.0 - halfTexel));
            vec2 auv = vec2(luv.x * atlasScaleX + atlasBiasX, luv.y);
            float d = texture(sampler2D(shadowAtlas, shadowSamp), auv).r;
            lit += (selDepth <= d) ? 1.0 : 0.0;             // receiver in front of the stored caster depth => lit
        }
    }
    lit /= 9.0;

    // Edge fade so the coverage limit is invisible: on the OUTERMOST cascade only (it has nothing beyond it), fade the
    // shadow to fully lit toward its UV border. That border sits at ShadowMaxDistance from the focus, so this is the
    // beyond-the-coverage-distance fade - precise to the cascade's own extent, no camera-distance guesswork. Inner
    // cascades hand off to the next (larger) cascade at their border, so they need no fade. A single cascade fades at
    // its own border (the pre-cascade single map, softened from a hard box edge). ShadowParams2.y (maxDistance) rides
    // in the UBO as the documented coverage distance for downstream effects. The fade itself is UV-border-driven.
    float fade = 1.0;
    if (sel == count - 1) {
        float border = ShadowParams2.z;
        float edge = min(min(selUv.x, 1.0 - selUv.x), min(selUv.y, 1.0 - selUv.y));
        fade = smoothstep(0.0, border, edge);
    }
    // strength 1 removes the key light fully in shadow, <1 leaves a partial key term. Fade eases both to fully lit.
    return mix(1.0, mix(1.0, lit, strength), fade);
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

        // ---- Model pass. Per-frame UBO (binding 0, both stages) holds only frame uniforms; per-instance data
        //      (Model matrix, Tint, Emissive, SpecParams) arrives via an instanced vertex stream (buffer slot 1,
        //      instanceStepRate 1). The Model matrix is reconstructed from 4 instance vec4 rows: InstanceData.Model
        //      is a System.Numerics Matrix4x4 stored row-major, read here as IModel0..3 (the rows). mat4(IModel0..3)
        //      builds the matrix COLUMNS from those rows = the transpose, which is exactly how GLSL read the old
        //      row-major UBO Model. So Model * vec4(pos) reproduces the previous world transform.
        //      Per-vertex layout: locations 0..4 are position/normal/color/texcoord/tangent. The tangent (location 4)
        //      carries model-space tangent xyz + handedness w; zero tangent = no TBN (primitives, untangented meshes).
        //      Per-instance data shifts to locations 5..11 (IModel0..3, ITint, IEmissive, ISpecParams). ----
        public const string ModelVert = @"#version 450
layout(set=0, binding=0) uniform U {
    mat4 ViewProj;
    vec4 LightDir; vec4 LightColor; vec4 Ambient; vec4 Params;
    vec4 FillDir; vec4 FillColor; vec4 CameraPos;
    vec4 PointPosRadius[16];
    vec4 PointColorIntensity[16];
    mat4 ShadowMat[4];     // cascaded shadow tail (offset 688): per-cascade world->light-clip (unused by the vertex stage)
    vec4 ShadowParams;     // x=cascadeCount, y=strength (0 => shadows off), z=constBias, w=slopeBias
    vec4 ShadowParams2;    // x=texelStep(1/perCascadeRes), y=maxDistance, z=borderFrac, w=reserved
    vec4 ShadowNormalOffsets; // per-cascade normal-offset world size (texelWorld_i * ShadowNormalOffset): x=c0..w=c3
};
layout(location=0) in vec3 Position;
layout(location=1) in vec3 Normal;
layout(location=2) in vec4 Color;
layout(location=3) in vec2 TexCoord;
layout(location=4) in vec4 Tangent;      // model-space tangent (xyz) + handedness (w); zero => no TBN
layout(location=5) in vec4 IModel0;      // per-instance model matrix rows
layout(location=6) in vec4 IModel1;
layout(location=7) in vec4 IModel2;
layout(location=8) in vec4 IModel3;
layout(location=9) in vec4 ITint;
layout(location=10) in vec4 IEmissive;
layout(location=11) in vec4 ISpecParams;
layout(location=0) out vec3 vNormalW;
layout(location=1) out vec4 vColor;
layout(location=2) out float vDepth;
layout(location=3) out vec3 vWorldPos;
layout(location=4) out vec2 vUv;
layout(location=5) out vec4 vTint;
layout(location=6) out vec4 vEmissive;
layout(location=7) out vec4 vSpecParams;
layout(location=8) out vec4 vTangent;
void main() {
    mat4 Model = mat4(IModel0, IModel1, IModel2, IModel3);
    vec4 world = Model * vec4(Position, 1.0);
    gl_Position = ViewProj * world;
    vNormalW = normalize(mat3(Model) * Normal);
    vColor = Color;
    vDepth = gl_Position.z / gl_Position.w; // 0..1 in clip space; linear for ortho
    vWorldPos = world.xyz;
    vUv = TexCoord;
    vTint = ITint;
    vEmissive = IEmissive;
    vSpecParams = ISpecParams;
    vTangent = vec4(mat3(Model) * Tangent.xyz, Tangent.w); // rotate tangent to world; preserve handedness
}";

        public const string ModelFrag = @"#version 450
layout(set=0, binding=0) uniform U {
    mat4 ViewProj;
    vec4 LightDir;   // xyz = key light travel direction
    vec4 LightColor;
    vec4 Ambient;
    vec4 Params;     // x = CelBands, y = active point-light count
    vec4 FillDir;    // xyz = fill light travel direction
    vec4 FillColor;
    vec4 CameraPos;  // xyz = eye position
    vec4 PointPosRadius[16];
    vec4 PointColorIntensity[16];
    mat4 ShadowMat[4];     // per-cascade world->light-clip for the cascaded shadow atlas (offset 688)
    vec4 ShadowParams;     // x=cascadeCount, y=strength (0 => shadows off), z=constBias, w=slopeBias
    vec4 ShadowParams2;    // x=texelStep(1/perCascadeRes), y=maxDistance, z=borderFrac, w=reserved
    vec4 ShadowNormalOffsets; // per-cascade normal-offset world size (texelWorld_i * ShadowNormalOffset): x=c0..w=c3
};
layout(set=0, binding=1) uniform texture2D Albedo;       // 1x1 white default keeps untextured meshes unchanged
layout(set=0, binding=2) uniform texture2D NormalMap;    // 1x1 flat default: texel (0.5,0.5,1.0) decodes to tangent-space (0,0,1); sampled up front, applied only when a tangent exists
layout(set=0, binding=3) uniform texture2D RoughnessMap; // 1x1 zero default => spec uses per-instance params
layout(set=0, binding=4) uniform sampler Samp;           // shared sampler for all three textures (EdgeFrag-style)
layout(set=0, binding=5) uniform texture2D ShadowMap;    // key-light depth map (R32F); sampled LAST, after the material maps (Metal first-sample-order rule); 1x1 default when shadows off
layout(set=0, binding=6) uniform sampler ShadowSamp;     // clamp/linear sampler for the shadow-map PCF taps
layout(location=0) in vec3 vNormalW;
layout(location=1) in vec4 vColor;
layout(location=2) in float vDepth;
layout(location=3) in vec3 vWorldPos;
layout(location=4) in vec2 vUv;
layout(location=5) in vec4 vTint;
layout(location=6) in vec4 vEmissive;
layout(location=7) in vec4 vSpecParams; // x = specular strength, y = shininess exponent, z = alpha-cutout threshold (0 = OPAQUE, no clip)
layout(location=8) in vec4 vTangent;    // world-space tangent (xyz) + handedness (w); zero => geometric normal
layout(location=0) out vec4 oColor;
layout(location=1) out vec4 oNormal;
layout(location=2) out vec4 oDepth;
" + LightingCommonGlsl + @"
void main() {
    vec3 Ngeo = normalize(vNormalW);
    // Sample ALL material maps up front, unconditionally, in binding order (Albedo, NormalMap, RoughnessMap).
    // This ordering is load-bearing on Metal: SPIRV-Cross assigns MSL texture indices in the order textures are
    // first sampled, so sampling a higher-binding map first (e.g. the normal map inside the TBN branch) made the
    // albedo sampler read the normal map - untextured meshes came out flat-normal coloured (R,G ~0.5). Sampling
    // binding 0 (Albedo) first and unconditionally keeps the indices matching the resource layout. (D3D11/Vulkan
    // bind by explicit decoration and are order-insensitive; this is purely the Metal path.) Mirrors EdgeFrag.
    vec4 texRgba = texture(sampler2D(Albedo, Samp), vUv);          // white (1,1,1,1) for untextured meshes
    vec3 texRgb = texRgba.rgb;
    vec3 normalTex = texture(sampler2D(NormalMap, Samp), vUv).xyz; // flat (0.5,0.5,1.0) default => (0,0,1)
    float rough = texture(sampler2D(RoughnessMap, Samp), vUv).g;   // 0 default => per-instance spec unchanged
    // Alpha cutout (MASK materials, e.g. foliage/leaf cards): vSpecParams.z carries the cutoff, and we discard a
    // texel whose baseColor alpha is below it so the quad reads as its silhouette instead of a solid (often black)
    // card. Done AFTER all three samples so the implicit-LOD derivatives stay well-defined and the Metal
    // first-sample-order is untouched. OPAQUE meshes carry cutoff 0, so the branch is never taken and the render
    // is byte-identical to the pre-cutout path (the 1x1 white default has alpha 1, so untextured meshes are safe).
    if (vSpecParams.z > 0.0 && texRgba.a < vSpecParams.z) discard;
    // Perturb the lighting normal via a TBN only when a tangent exists. Zero tangent (primitives, skinned,
    // untangented meshes) => geometric normal, bit-identical to the pre-PBR pass. A flat normal sample
    // (0,0,1) also yields Ngeo, so a tangent-bearing mesh with no normal map is unchanged too.
    vec3 N = Ngeo;
    if (dot(vTangent.xyz, vTangent.xyz) > 1e-10) {
        vec3 T = normalize(vTangent.xyz);
        T = normalize(T - Ngeo * dot(Ngeo, T));
        vec3 B = cross(Ngeo, T) * vTangent.w;
        vec3 nTS = normalTex * 2.0 - 1.0;
        N = normalize(mat3(T, B, Ngeo) * nTS);
    }
    vec3 albedo = vColor.rgb * vTint.rgb * texRgb;
    // TBN perturb + roughness->spec mirror the CPU helper SurfaceShading.cs (PerturbNormal/ApplyRoughness); keep in sync.
    // Roughness modulation (glTF metallic-roughness .g convention; metallic ignored). rough 0 (default)
    // collapses to today's per-instance spec exactly: strength*(1-0)=strength, mix(exp,8,0)=exp.
    float specStrength = vSpecParams.x * (1.0 - rough);
    float specExp = max(mix(vSpecParams.y, 8.0, rough), 1.0);
    // Key-light shadow: sampled AFTER the material maps (Metal first-sample-order: ShadowMap is binding 5, sampled
    // last). N.L to the key light scales the slope bias. keyShadow == 1 when the map is off (byte-stable with Off).
    float ndlKeyForShadow = max(dot(N, -normalize(LightDir.xyz)), 0.0);
    float keyShadow = sampleKeyShadow(ShadowMap, ShadowSamp, vWorldPos, Ngeo, ndlKeyForShadow);
    // Key+fill+cel+point-light accumulation is the shared block (ShaderSources.LightingCommonGlsl), spliced in above.
    vec3 diffuse; vec3 specColor;
    computeLighting(N, vWorldPos, specStrength, specExp, keyShadow, diffuse, specColor);
    vec3 lit = albedo * (Ambient.rgb + diffuse) + specColor + vEmissive.rgb;
    oColor = vec4(lit, 1.0);
    oNormal = vec4(Ngeo * 0.5 + 0.5, 1.0); // GEOMETRIC normal for the edge pass (not the perturbed one)
    oDepth = vec4(vDepth, vDepth, vDepth, 1.0);
}";

        // ---- CharDissolve variant of ModelFrag: noise-thresholded alpha clip + emissive edge ----
        // Identical lighting to ModelFrag, plus a world-space value-noise dissolve mask: fragments where the mask is
        // below the threshold (vSpecParams.z, 0=solid .. 1=gone; fed CharDissolve.Cover) are discarded, and a thin
        // band just above the threshold (width vSpecParams.w) glows with the edge colour (which rides vEmissive during
        // a dissolve). World-space noise so the pattern is stable as the avatar moves. Only skinned draws the consumer
        // marks as dissolving use this pipeline; the normal path keeps ModelFrag byte-identical.
        public const string ModelDissolveFrag = @"#version 450
layout(set=0, binding=0) uniform U {
    mat4 ViewProj;
    vec4 LightDir;
    vec4 LightColor;
    vec4 Ambient;
    vec4 Params;
    vec4 FillDir;
    vec4 FillColor;
    vec4 CameraPos;
    vec4 PointPosRadius[16];
    vec4 PointColorIntensity[16];
    mat4 ShadowMat[4];         // per-cascade world->light-clip for the cascaded shadow atlas
    vec4 ShadowParams;         // x=cascadeCount, y=strength, z=constBias, w=slopeBias
    vec4 ShadowParams2;        // x=texelStep(1/perCascadeRes), y=maxDistance, z=borderFrac, w=reserved
    vec4 ShadowNormalOffsets;  // per-cascade normal-offset world size (x=c0..w=c3)
};
layout(set=0, binding=1) uniform texture2D Albedo;
layout(set=0, binding=2) uniform texture2D NormalMap;
layout(set=0, binding=3) uniform texture2D RoughnessMap;
layout(set=0, binding=4) uniform sampler Samp;
layout(set=0, binding=5) uniform texture2D ShadowMap;
layout(set=0, binding=6) uniform sampler ShadowSamp;
layout(location=0) in vec3 vNormalW;
layout(location=1) in vec4 vColor;
layout(location=2) in float vDepth;
layout(location=3) in vec3 vWorldPos;
layout(location=4) in vec2 vUv;
layout(location=5) in vec4 vTint;
layout(location=6) in vec4 vEmissive;   // during a dissolve this carries the emissive EDGE colour
layout(location=7) in vec4 vSpecParams; // x=spec strength, y=shininess, z=dissolve threshold, w=dissolve edge width
layout(location=8) in vec4 vTangent;
layout(location=0) out vec4 oColor;
layout(location=1) out vec4 oNormal;
layout(location=2) out vec4 oDepth;
" + LightingCommonGlsl + @"
float dhash(vec3 p) { return fract(sin(dot(p, vec3(12.9898, 78.233, 37.719))) * 43758.5453); }
float dnoise(vec3 p) {
    vec3 i = floor(p); vec3 f = fract(p); f = f * f * (3.0 - 2.0 * f);
    float n000 = dhash(i + vec3(0,0,0)), n100 = dhash(i + vec3(1,0,0));
    float n010 = dhash(i + vec3(0,1,0)), n110 = dhash(i + vec3(1,1,0));
    float n001 = dhash(i + vec3(0,0,1)), n101 = dhash(i + vec3(1,0,1));
    float n011 = dhash(i + vec3(0,1,1)), n111 = dhash(i + vec3(1,1,1));
    return mix(mix(mix(n000, n100, f.x), mix(n010, n110, f.x), f.y),
               mix(mix(n001, n101, f.x), mix(n011, n111, f.x), f.y), f.z);
}
void main() {
    float threshold = clamp(vSpecParams.z, 0.0, 1.0);
    float edgeW = max(vSpecParams.w, 1e-3);
    float mask = dnoise(vWorldPos * 6.0);   // 0..1 world-space dissolve mask
    if (mask < threshold) discard;          // dissolved away

    vec3 Ngeo = normalize(vNormalW);
    vec3 texRgb = texture(sampler2D(Albedo, Samp), vUv).rgb;
    vec3 normalTex = texture(sampler2D(NormalMap, Samp), vUv).xyz;
    float rough = texture(sampler2D(RoughnessMap, Samp), vUv).g;
    vec3 N = Ngeo;
    if (dot(vTangent.xyz, vTangent.xyz) > 1e-10) {
        vec3 T = normalize(vTangent.xyz);
        T = normalize(T - Ngeo * dot(Ngeo, T));
        vec3 B = cross(Ngeo, T) * vTangent.w;
        vec3 nTS = normalTex * 2.0 - 1.0;
        N = normalize(mat3(T, B, Ngeo) * nTS);
    }
    vec3 albedo = vColor.rgb * vTint.rgb * texRgb;
    float specStrength = vSpecParams.x * (1.0 - rough);
    float specExp = max(mix(vSpecParams.y, 8.0, rough), 1.0);
    float ndlKeyForShadow = max(dot(N, -normalize(LightDir.xyz)), 0.0);
    float keyShadow = sampleKeyShadow(ShadowMap, ShadowSamp, vWorldPos, Ngeo, ndlKeyForShadow);
    vec3 diffuse; vec3 specColor;
    computeLighting(N, vWorldPos, specStrength, specExp, keyShadow, diffuse, specColor);
    vec3 lit = albedo * (Ambient.rgb + diffuse) + specColor;   // no base emissive: vEmissive is the edge colour here
    // Emissive edge: a bright band just above the discard threshold. step(threshold) suppresses any edge at
    // threshold 0 (a fully-solid avatar routed through this pipeline still reads clean).
    float edge = (1.0 - smoothstep(threshold, threshold + edgeW, mask)) * step(0.001, threshold);
    lit += vEmissive.rgb * edge;
    oColor = vec4(lit, 1.0);
    oNormal = vec4(Ngeo * 0.5 + 0.5, 1.0);
    oDepth = vec4(vDepth, vDepth, vDepth, 1.0);
}";

        // ---- GPU skinning (opt-in, Scene3D.UseGpuSkinning). The whole skinned pipeline reads EXACTLY ONE uniform
        //      buffer, a combined per-draw block at set 0 binding 0, read by BOTH stages (the proven model-pipeline
        //      shape - one UBO in vertex+fragment + textures). The block folds the per-draw matrices the VERTEX needs
        //      (Mvp = Model*clip-corrected-ViewProj; Model; P packing Tint/Emissive/SpecParams) AND the per-frame
        //      lighting the FRAGMENT needs (the frame UBO layout, mirrored exactly) AND the bone palette, per draw
        //      (the frame fields are duplicated into every slot - the cost of the one-buffer rule). A SECOND uniform
        //      buffer anywhere in the pipeline - a second vertex buffer, OR a fragment-only UBO whether in this set or
        //      a separate set 1 - mis-binds on Metal/Veldrid/SPIRV-Cross and reads zero (measured, see
        //      DEPENDENCY-SEAMS.md and GpuSkinningReproGpuTests variant 3). Material TEXTURES map fine in a second set,
        //      so the per-mesh maps live at set 1. The 4-bone blend + position/normal/tangent deform mirror
        //      SkinningMath.SkinVertex exactly, so a GPU-skinned draw is pixel-parity with the CPU path. Both stages
        //      declare the identical block. The vertex uses Mvp/Model/P/bones, the fragment uses the frame fields. ----
        public const string SkinnedModelVert = @"#version 450
layout(set=0, binding=0) uniform VBlock {
    mat4 Mvp;              // Model * clip-corrected ViewProj (folded per draw): gl_Position = Mvp * skinnedLocal
    mat4 Model;            // world transform (for worldPos + world normal/tangent the fragment lights with)
    mat4 P;                // columns: [0]=Tint, [1]=Emissive, [2]=SpecParams (per-draw constants, packed row-major)
    mat4 ViewProj;         // --- frame block (offset 192): mirrors the frame UBO layout so WriteFrameUniformsTo fills it ---
    vec4 LightDir; vec4 LightColor; vec4 Ambient; vec4 Params;
    vec4 FillDir; vec4 FillColor; vec4 CameraPos;
    vec4 PointPosRadius[16];
    vec4 PointColorIntensity[16];
    mat4 ShadowMat[4];         // per-cascade world->light-clip for the cascaded shadow atlas
    vec4 ShadowParams;         // x=cascadeCount, y=strength, z=constBias, w=slopeBias
    vec4 ShadowParams2;        // x=texelStep(1/perCascadeRes), y=maxDistance, z=borderFrac, w=reserved
    vec4 ShadowNormalOffsets;  // per-cascade normal-offset world size (x=c0..w=c3)
    mat4 bones[128];       // offset 976: this draw's composed palette (inverseBind*jointWorld), padded/validated to <=128
};
layout(location=0) in vec3 Position;
layout(location=1) in vec3 Normal;
layout(location=2) in vec4 Color;
layout(location=3) in vec2 TexCoord;
layout(location=4) in vec4 BoneIndices;   // 4 float-encoded palette indices (JOINTS_0)
layout(location=5) in vec4 BoneWeights;   // 4 blend weights (WEIGHTS_0), all-zero => identity (no deform)
layout(location=6) in vec4 Tangent;       // model-space tangent xyz + handedness w, zero => no TBN
layout(location=0) out vec3 vNormalW;
layout(location=1) out vec4 vColor;
layout(location=2) out float vDepth;
layout(location=3) out vec3 vWorldPos;
layout(location=4) out vec2 vUv;
layout(location=5) out vec4 vTint;
layout(location=6) out vec4 vEmissive;
layout(location=7) out vec4 vSpecParams;
layout(location=8) out vec4 vTangent;
void main() {
    // 4-bone blend, mirroring SkinningMath.BlendSkinMatrix: raw (un-renormalized) weights, identity on ~0 total so an
    // unrigged vertex stays in place. bones[i] uploaded raw (System.Numerics row-major) reads column-major here as its
    // transpose, so 'skin * vec4(pos,1)' reproduces the CPU 'Vector3.Transform(pos, skin)' bit-for-bit.
    float wsum = BoneWeights.x + BoneWeights.y + BoneWeights.z + BoneWeights.w;
    mat4 skin;
    if (wsum < 1e-8) {
        skin = mat4(1.0);
    } else {
        skin = bones[int(BoneIndices.x)] * BoneWeights.x
             + bones[int(BoneIndices.y)] * BoneWeights.y
             + bones[int(BoneIndices.z)] * BoneWeights.z
             + bones[int(BoneIndices.w)] * BoneWeights.w;
    }
    vec4 localPos = skin * vec4(Position, 1.0);
    // Normal: skin-rotate then renormalize (fallback to source on a degenerate skin), mirroring SkinVertex's local
    // normal, THEN rotate into world by Model and renormalize (mirroring ModelVert). Two-stage to match the CPU path.
    vec3 nLocal = mat3(skin) * Normal;
    float nlen = length(nLocal);
    nLocal = nlen > 1e-8 ? nLocal / nlen : Normal;
    // Tangent: zero source stays zero (no-TBN fallback). Non-zero => skin-rotate + renormalize the local tangent, then
    // rotate into world by Model (handedness w preserved). Matches SkinVertex + ModelVert composed.
    vec4 tLocal = vec4(0.0);
    if (dot(Tangent.xyz, Tangent.xyz) > 1e-12) {
        vec3 td = mat3(skin) * Tangent.xyz;
        float tl = length(td);
        td = tl > 1e-8 ? td / tl : Tangent.xyz;
        tLocal = vec4(td, Tangent.w);
    }
    vec4 world = Model * localPos;
    gl_Position = Mvp * localPos;
    vNormalW = normalize(mat3(Model) * nLocal);
    vColor = Color;
    vDepth = gl_Position.z / gl_Position.w;
    vWorldPos = world.xyz;
    vUv = TexCoord;
    vTint = P[0];
    vEmissive = P[1];
    vSpecParams = P[2];
    vTangent = vec4(mat3(Model) * tLocal.xyz, tLocal.w);   // zero tangent -> (0,0,0,0), so the fragment uses Ngeo
}";

        // Skinned fragment: byte-for-byte ModelFrag lighting. It reads the frame fields from the SAME combined VBlock
        // (set 0 binding 0) the vertex reads - one uniform buffer for the whole pipeline (the only Metal-safe shape).
        // Both stages declare the identical block. The fragment ignores Mvp/Model/P/bones. Material maps at set 1
        // (set-1 TEXTURES map fine on Metal). Sample order (Albedo first, ShadowMap last) preserves the first-sample rule.
        public const string SkinnedModelFrag = @"#version 450
layout(set=0, binding=0) uniform VBlock {
    mat4 Mvp;
    mat4 Model;
    mat4 P;
    mat4 ViewProj;
    vec4 LightDir;
    vec4 LightColor;
    vec4 Ambient;
    vec4 Params;
    vec4 FillDir;
    vec4 FillColor;
    vec4 CameraPos;
    vec4 PointPosRadius[16];
    vec4 PointColorIntensity[16];
    mat4 ShadowMat[4];         // per-cascade world->light-clip for the cascaded shadow atlas
    vec4 ShadowParams;         // x=cascadeCount, y=strength, z=constBias, w=slopeBias
    vec4 ShadowParams2;        // x=texelStep(1/perCascadeRes), y=maxDistance, z=borderFrac, w=reserved
    vec4 ShadowNormalOffsets;  // per-cascade normal-offset world size (x=c0..w=c3)
    mat4 bones[128];
};
layout(set=1, binding=0) uniform texture2D Albedo;
layout(set=1, binding=1) uniform texture2D NormalMap;
layout(set=1, binding=2) uniform texture2D RoughnessMap;
layout(set=1, binding=3) uniform sampler Samp;
layout(set=1, binding=4) uniform texture2D ShadowMap;
layout(set=1, binding=5) uniform sampler ShadowSamp;
layout(location=0) in vec3 vNormalW;
layout(location=1) in vec4 vColor;
layout(location=2) in float vDepth;
layout(location=3) in vec3 vWorldPos;
layout(location=4) in vec2 vUv;
layout(location=5) in vec4 vTint;
layout(location=6) in vec4 vEmissive;
layout(location=7) in vec4 vSpecParams;
layout(location=8) in vec4 vTangent;
layout(location=0) out vec4 oColor;
layout(location=1) out vec4 oNormal;
layout(location=2) out vec4 oDepth;
" + LightingCommonGlsl + @"
void main() {
    vec3 Ngeo = normalize(vNormalW);
    vec4 texRgba = texture(sampler2D(Albedo, Samp), vUv);
    vec3 texRgb = texRgba.rgb;
    vec3 normalTex = texture(sampler2D(NormalMap, Samp), vUv).xyz;
    float rough = texture(sampler2D(RoughnessMap, Samp), vUv).g;
    if (vSpecParams.z > 0.0 && texRgba.a < vSpecParams.z) discard;
    vec3 N = Ngeo;
    if (dot(vTangent.xyz, vTangent.xyz) > 1e-10) {
        vec3 T = normalize(vTangent.xyz);
        T = normalize(T - Ngeo * dot(Ngeo, T));
        vec3 B = cross(Ngeo, T) * vTangent.w;
        vec3 nTS = normalTex * 2.0 - 1.0;
        N = normalize(mat3(T, B, Ngeo) * nTS);
    }
    vec3 albedo = vColor.rgb * vTint.rgb * texRgb;
    float specStrength = vSpecParams.x * (1.0 - rough);
    float specExp = max(mix(vSpecParams.y, 8.0, rough), 1.0);
    float ndlKeyForShadow = max(dot(N, -normalize(LightDir.xyz)), 0.0);
    float keyShadow = sampleKeyShadow(ShadowMap, ShadowSamp, vWorldPos, Ngeo, ndlKeyForShadow);
    vec3 diffuse; vec3 specColor;
    computeLighting(N, vWorldPos, specStrength, specExp, keyShadow, diffuse, specColor);
    vec3 lit = albedo * (Ambient.rgb + diffuse) + specColor + vEmissive.rgb;
    oColor = vec4(lit, 1.0);
    oNormal = vec4(Ngeo * 0.5 + 0.5, 1.0);
    oDepth = vec4(vDepth, vDepth, vDepth, 1.0);
}";

        // Skinned CharDissolve variant: reads the frame fields from the same combined VBlock (set 0 binding 0),
        // material maps at set 1. Identical noise-thresholded alpha clip + emissive edge.
        public const string SkinnedModelDissolveFrag = @"#version 450
layout(set=0, binding=0) uniform VBlock {
    mat4 Mvp;
    mat4 Model;
    mat4 P;
    mat4 ViewProj;
    vec4 LightDir;
    vec4 LightColor;
    vec4 Ambient;
    vec4 Params;
    vec4 FillDir;
    vec4 FillColor;
    vec4 CameraPos;
    vec4 PointPosRadius[16];
    vec4 PointColorIntensity[16];
    mat4 ShadowMat[4];         // per-cascade world->light-clip for the cascaded shadow atlas
    vec4 ShadowParams;         // x=cascadeCount, y=strength, z=constBias, w=slopeBias
    vec4 ShadowParams2;        // x=texelStep(1/perCascadeRes), y=maxDistance, z=borderFrac, w=reserved
    vec4 ShadowNormalOffsets;  // per-cascade normal-offset world size (x=c0..w=c3)
    mat4 bones[128];
};
layout(set=1, binding=0) uniform texture2D Albedo;
layout(set=1, binding=1) uniform texture2D NormalMap;
layout(set=1, binding=2) uniform texture2D RoughnessMap;
layout(set=1, binding=3) uniform sampler Samp;
layout(set=1, binding=4) uniform texture2D ShadowMap;
layout(set=1, binding=5) uniform sampler ShadowSamp;
layout(location=0) in vec3 vNormalW;
layout(location=1) in vec4 vColor;
layout(location=2) in float vDepth;
layout(location=3) in vec3 vWorldPos;
layout(location=4) in vec2 vUv;
layout(location=5) in vec4 vTint;
layout(location=6) in vec4 vEmissive;
layout(location=7) in vec4 vSpecParams;
layout(location=8) in vec4 vTangent;
layout(location=0) out vec4 oColor;
layout(location=1) out vec4 oNormal;
layout(location=2) out vec4 oDepth;
" + LightingCommonGlsl + @"
float dhash(vec3 p) { return fract(sin(dot(p, vec3(12.9898, 78.233, 37.719))) * 43758.5453); }
float dnoise(vec3 p) {
    vec3 i = floor(p); vec3 f = fract(p); f = f * f * (3.0 - 2.0 * f);
    float n000 = dhash(i + vec3(0,0,0)), n100 = dhash(i + vec3(1,0,0));
    float n010 = dhash(i + vec3(0,1,0)), n110 = dhash(i + vec3(1,1,0));
    float n001 = dhash(i + vec3(0,0,1)), n101 = dhash(i + vec3(1,0,1));
    float n011 = dhash(i + vec3(0,1,1)), n111 = dhash(i + vec3(1,1,1));
    return mix(mix(mix(n000, n100, f.x), mix(n010, n110, f.x), f.y),
               mix(mix(n001, n101, f.x), mix(n011, n111, f.x), f.y), f.z);
}
void main() {
    float threshold = clamp(vSpecParams.z, 0.0, 1.0);
    float edgeW = max(vSpecParams.w, 1e-3);
    float mask = dnoise(vWorldPos * 6.0);
    if (mask < threshold) discard;

    vec3 Ngeo = normalize(vNormalW);
    vec3 texRgb = texture(sampler2D(Albedo, Samp), vUv).rgb;
    vec3 normalTex = texture(sampler2D(NormalMap, Samp), vUv).xyz;
    float rough = texture(sampler2D(RoughnessMap, Samp), vUv).g;
    vec3 N = Ngeo;
    if (dot(vTangent.xyz, vTangent.xyz) > 1e-10) {
        vec3 T = normalize(vTangent.xyz);
        T = normalize(T - Ngeo * dot(Ngeo, T));
        vec3 B = cross(Ngeo, T) * vTangent.w;
        vec3 nTS = normalTex * 2.0 - 1.0;
        N = normalize(mat3(T, B, Ngeo) * nTS);
    }
    vec3 albedo = vColor.rgb * vTint.rgb * texRgb;
    float specStrength = vSpecParams.x * (1.0 - rough);
    float specExp = max(mix(vSpecParams.y, 8.0, rough), 1.0);
    float ndlKeyForShadow = max(dot(N, -normalize(LightDir.xyz)), 0.0);
    float keyShadow = sampleKeyShadow(ShadowMap, ShadowSamp, vWorldPos, Ngeo, ndlKeyForShadow);
    vec3 diffuse; vec3 specColor;
    computeLighting(N, vWorldPos, specStrength, specExp, keyShadow, diffuse, specColor);
    vec3 lit = albedo * (Ambient.rgb + diffuse) + specColor;
    float edge = (1.0 - smoothstep(threshold, threshold + edgeW, mask)) * step(0.001, threshold);
    lit += vEmissive.rgb * edge;
    oColor = vec4(lit, 1.0);
    oNormal = vec4(Ngeo * 0.5 + 0.5, 1.0);
    oDepth = vec4(vDepth, vDepth, vDepth, 1.0);
}";

        // ---- Splat-terrain vertex shader. Identical to ModelVert, except the per-frame UBO (binding 0) carries the
        //      per-material splat params appended after the point-light arrays - so the splat pipeline binds exactly
        //      ONE uniform buffer. (Veldrid/SPIRV-Cross on Metal mis-binds a SECOND uniform buffer in a set: it reads
        //      the first buffer's bytes, which zeroed the per-layer tint and blacked out the terrain. One UBO total
        //      sidesteps it.) The params tail is unused by the vertex stage but declared so the block layout matches
        //      SplatFrag exactly. ----
        public const string SplatVert = @"#version 450
layout(set=0, binding=0) uniform U {
    mat4 ViewProj;
    vec4 LightDir; vec4 LightColor; vec4 Ambient; vec4 Params;
    vec4 FillDir; vec4 FillColor; vec4 CameraPos;
    vec4 PointPosRadius[16];
    vec4 PointColorIntensity[16];
    mat4 ShadowMat[4];     // cascaded shadow tail (offset 688): per-cascade world->light-clip
    vec4 ShadowParams;     // x=cascadeCount, y=strength (0 => shadows off), z=constBias, w=slopeBias
    vec4 ShadowParams2;    // x=texelStep(1/perCascadeRes), y=maxDistance, z=borderFrac, w=reserved
    vec4 ShadowNormalOffsets; // per-cascade normal-offset world size (texelWorld_i * ShadowNormalOffset): x=c0..w=c3
    vec4 TintTiling[5];   // per-material params appended (offset 992): xyz = tint, w = tiles/metre
    vec4 Roughness;       // x..w = roughness for layers 0..3
    vec4 Misc;            // x = layer4 roughness, y = triplanarSharpness, z = projectionMode, w = baseSpecStrength
};
layout(location=0) in vec3 Position;
layout(location=1) in vec3 Normal;
layout(location=2) in vec4 Color;
layout(location=3) in vec2 TexCoord;
layout(location=4) in vec4 Tangent;
layout(location=5) in vec4 IModel0;
layout(location=6) in vec4 IModel1;
layout(location=7) in vec4 IModel2;
layout(location=8) in vec4 IModel3;
layout(location=9) in vec4 ITint;
layout(location=10) in vec4 IEmissive;
layout(location=11) in vec4 ISpecParams;
// Interpolant locations are ordered so the SplatFrag-USED outputs (vNormalW,vColor,vDepth,vWorldPos,vTint,vEmissive)
// occupy a CONTIGUOUS 0..5 block, and the fragment-UNUSED outputs (vUv,vSpecParams,vTangent) sit above at 6..8.
// SplatFrag declares only the gap-free prefix 0..5. This is load-bearing on D3D11: a HOLE in the pixel-input
// semantics (the old layout had vUv at TEXCOORD4 declared-but-unused, dropped by SPIRV-Cross, leaving a gap between
// vWorldPos@3 and vTint@5) miscompiles on FXC/WARP - the highest live interpolant (vEmissive) read garbage and blew
// the whole terrain to flat white (Metal/Vulkan tolerated the gap). Keep the used interpolants contiguous from 0;
// do NOT reintroduce a fragment-unused interpolant below location 6.
layout(location=0) out vec3 vNormalW;
layout(location=1) out vec4 vColor;
layout(location=2) out float vDepth;
layout(location=3) out vec3 vWorldPos;
layout(location=4) out vec4 vTint;
layout(location=5) out vec4 vEmissive;
layout(location=6) out vec2 vUv;         // fragment-unused (world-space UV is used); kept above the live block
layout(location=7) out vec4 vSpecParams; // fragment-unused (base spec from Misc.w)
layout(location=8) out vec4 vTangent;    // fragment-unused (triplanar derives its own basis)
void main() {
    mat4 Model = mat4(IModel0, IModel1, IModel2, IModel3);
    vec4 world = Model * vec4(Position, 1.0);
    gl_Position = ViewProj * world;
    vNormalW = normalize(mat3(Model) * Normal);
    vColor = Color;
    vDepth = gl_Position.z / gl_Position.w;
    vWorldPos = world.xyz;
    vUv = TexCoord;
    vTint = ITint;
    vEmissive = IEmissive;
    vSpecParams = ISpecParams;
    vTangent = vec4(mat3(Model) * Tangent.xyz, Tangent.w);
}";

        // ---- Splat-terrain fragment shader. Pairs with SplatVert. Reads two 5-layer texture arrays (albedo,
        //      tangent-space normal) + a shared sampler; the per-material params (per-layer tint/tiling/roughness +
        //      globals) ride in the SAME UBO as the frame uniforms (binding 0, appended after the light arrays), so
        //      the pipeline binds ONE uniform buffer (see SplatVert). Blends the five layers by the per-vertex
        //      weights, tiles each in WORLD space with triplanar projection (no per-vertex tangent), and lights with
        //      the SAME key+fill+ambient+point-light+cel model as ModelFrag - via the shared LightingCommonGlsl block
        //      (single-sourced, not hand-duplicated), which both fragments splice in and call. Writes the same 3 MRT
        //      targets (geometric normal to attachment 1 for the edge pass). Sample the two arrays in binding order
        //      (Albedo then Normal) - the Metal SPIRV-Cross first-sample-order constraint. ----
        public const string SplatFrag = @"#version 450
layout(set=0, binding=0) uniform U {
    mat4 ViewProj;
    vec4 LightDir; vec4 LightColor; vec4 Ambient; vec4 Params;
    vec4 FillDir; vec4 FillColor; vec4 CameraPos;
    vec4 PointPosRadius[16];
    vec4 PointColorIntensity[16];
    mat4 ShadowMat[4];     // per-cascade world->light-clip for the cascaded shadow atlas (offset 688)
    vec4 ShadowParams;     // x=cascadeCount, y=strength (0 => shadows off), z=constBias, w=slopeBias
    vec4 ShadowParams2;    // x=texelStep(1/perCascadeRes), y=maxDistance, z=borderFrac, w=reserved
    vec4 ShadowNormalOffsets; // per-cascade normal-offset world size (texelWorld_i * ShadowNormalOffset): x=c0..w=c3
    vec4 TintTiling[5];   // xyz = tint, w = tiles/metre (offset 992)
    vec4 Roughness;       // x..w = roughness for layers 0..3
    vec4 Misc;            // x = layer4 roughness, y = triplanarSharpness, z = projectionMode, w = baseSpecStrength
};
layout(set=0, binding=1) uniform texture2DArray AlbedoArray;
layout(set=0, binding=2) uniform texture2DArray NormalArray;
layout(set=0, binding=3) uniform sampler Samp;
layout(set=0, binding=4) uniform texture2D ShadowMap;    // key-light depth map (R32F); sampled LAST, after the terrain arrays (Metal first-sample-order rule)
layout(set=0, binding=5) uniform sampler ShadowSamp;     // clamp/linear sampler for the shadow-map PCF taps
// Declare ONLY the interpolants this fragment reads, as a CONTIGUOUS 0..5 block (no gap). SplatVert emits these
// same six at 0..5 and the fragment-unused vUv/vSpecParams/vTangent at 6..8 (which this shader does not declare).
// A hole in the pixel-input semantics (e.g. declaring vUv@4 but never using it) makes FXC/WARP miscompile and the
// terrain renders flat white - the live interpolants must be gap-free from location 0. See the SplatVert note.
layout(location=0) in vec3 vNormalW;
layout(location=1) in vec4 vColor;       // packed weights (grass,dirt,rock,sand); snow = 1 - sum
layout(location=2) in float vDepth;
layout(location=3) in vec3 vWorldPos;
layout(location=4) in vec4 vTint;
layout(location=5) in vec4 vEmissive;
layout(location=0) out vec4 oColor;
layout(location=1) out vec4 oNormal;
layout(location=2) out vec4 oDepth;

// Explicit-gradient triplanar albedo. gN0/gN1 are the ddx/ddy of each plane's UV, computed once in uniform flow by
// the caller (see main) so textureGrad's mip/aniso LOD is well-defined even though this runs under the per-layer
// weight branch. An implicit texture() here would take undefined derivatives once a quad diverges at that branch.
vec3 sampleAlbedo(int layer, vec2 uvx, vec2 uvy, vec2 uvz, vec3 bw,
                  vec2 gx0, vec2 gx1, vec2 gy0, vec2 gy1, vec2 gz0, vec2 gz1) {
    vec3 ax = textureGrad(sampler2DArray(AlbedoArray, Samp), vec3(uvx, float(layer)), gx0, gx1).rgb;
    vec3 ay = textureGrad(sampler2DArray(AlbedoArray, Samp), vec3(uvy, float(layer)), gy0, gy1).rgb;
    vec3 az = textureGrad(sampler2DArray(AlbedoArray, Samp), vec3(uvz, float(layer)), gz0, gz1).rgb;
    return ax*bw.x + ay*bw.y + az*bw.z;
}

// Whiteout triplanar normal blend (reorient each plane's tangent-space normal into world space, no vertex tangent).
// Uses the same hoisted per-plane gradients as sampleAlbedo (textureGrad) so the LOD stays defined under the branch.
vec3 sampleNormal(int layer, vec2 uvx, vec2 uvy, vec2 uvz, vec3 bw, vec3 Ngeo,
                  vec2 gx0, vec2 gx1, vec2 gy0, vec2 gy1, vec2 gz0, vec2 gz1) {
    vec3 nx = textureGrad(sampler2DArray(NormalArray, Samp), vec3(uvx, float(layer)), gx0, gx1).xyz * 2.0 - 1.0;
    vec3 ny = textureGrad(sampler2DArray(NormalArray, Samp), vec3(uvy, float(layer)), gy0, gy1).xyz * 2.0 - 1.0;
    vec3 nz = textureGrad(sampler2DArray(NormalArray, Samp), vec3(uvz, float(layer)), gz0, gz1).xyz * 2.0 - 1.0;
    nx = vec3(nx.xy + Ngeo.zy, abs(nx.z) * Ngeo.x);
    ny = vec3(ny.xy + Ngeo.xz, abs(ny.z) * Ngeo.y);
    nz = vec3(nz.xy + Ngeo.xy, abs(nz.z) * Ngeo.z);
    return normalize(nx.zyx * bw.x + ny.xzy * bw.y + nz.xyz * bw.z);
}
" + LightingCommonGlsl + @"
void main() {
    vec3 Ngeo = normalize(vNormalW);

    // Reconstruct + renormalize the five weights (4 packed in vColor, snow = 1 - sum).
    float a0 = vColor.r, a1 = vColor.g, a2 = vColor.b, a3 = vColor.a;
    float a4 = clamp(1.0 - (a0 + a1 + a2 + a3), 0.0, 1.0);
    float wsum = a0 + a1 + a2 + a3 + a4;
    if (wsum > 1e-5) { a0/=wsum; a1/=wsum; a2/=wsum; a3/=wsum; a4/=wsum; } else { a0 = 1.0; a1 = a2 = a3 = a4 = 0.0; }
    float w[5] = float[5](a0, a1, a2, a3, a4);
    float rgh[5] = float[5](Roughness.x, Roughness.y, Roughness.z, Roughness.w, Misc.x);

    // Triplanar blend weights (planar mode forces the XZ plane).
    int projMode = int(Misc.z + 0.5);
    vec3 bw;
    if (projMode == 1) { bw = vec3(0.0, 1.0, 0.0); }
    else {
        bw = pow(abs(Ngeo), vec3(max(Misc.y, 0.001)));
        bw /= max(bw.x + bw.y + bw.z, 1e-5);
    }

    // Screen-space world derivatives, taken ONCE here in uniform control flow (before the per-layer loop's
    // data-dependent `continue`). The triplanar UVs are vWorldPos.{yz,xz,xy} * tile, so each plane's texture-space
    // gradient is the matching world derivative scaled by that layer's tile rate. Feeding these to textureGrad keeps
    // the mip/aniso LOD well-defined regardless of the branch; an implicit texture() under the branch would take
    // undefined derivatives on a diverging quad, which minified high-frequency ground reads as distance shimmer.
    vec3 dWx = dFdx(vWorldPos);
    vec3 dWy = dFdy(vWorldPos);

    vec3 albedo = vec3(0.0);
    vec3 Nsum = vec3(0.0);
    float rough = 0.0;
    for (int L = 0; L < 5; L++) {
        float wl = w[L];
        if (wl <= 0.001) continue;
        float tile = TintTiling[L].w;
        vec2 uvx = vWorldPos.yz * tile;
        vec2 uvy = vWorldPos.xz * tile;
        vec2 uvz = vWorldPos.xy * tile;
        vec2 gx0 = dWx.yz * tile, gx1 = dWy.yz * tile;
        vec2 gy0 = dWx.xz * tile, gy1 = dWy.xz * tile;
        vec2 gz0 = dWx.xy * tile, gz1 = dWy.xy * tile;
        albedo += wl * sampleAlbedo(L, uvx, uvy, uvz, bw, gx0, gx1, gy0, gy1, gz0, gz1) * TintTiling[L].xyz;
        Nsum   += wl * sampleNormal(L, uvx, uvy, uvz, bw, Ngeo, gx0, gx1, gy0, gy1, gz0, gz1);
        rough  += wl * rgh[L];
    }
    albedo *= vTint.rgb;
    vec3 N = (dot(Nsum, Nsum) > 1e-8) ? normalize(Nsum) : Ngeo;

    // Lighting via the shared block (ShaderSources.LightingCommonGlsl, spliced in above). Base specular strength
    // from Misc.w; the specular exponent is derived from the blended terrain roughness, NOT from per-instance
    // material params. This is an INTENTIONAL divergence from ModelFrag: blended terrain layers have no
    // per-instance material (vSpecParams), so the exponent eases from SPLAT_SPEC_EXP_SMOOTH (glossy) down to
    // SPLAT_SPEC_EXP_ROUGH (broad) across roughness instead of reading a per-instance shininess.
    const float SPLAT_SPEC_EXP_SMOOTH = 48.0; // exponent at roughness 0 (glossy)
    const float SPLAT_SPEC_EXP_ROUGH  = 8.0;  // exponent at roughness 1 (broad highlight)
    float specStrength = Misc.w * (1.0 - rough);
    float specExp = max(mix(SPLAT_SPEC_EXP_SMOOTH, SPLAT_SPEC_EXP_ROUGH, rough), 1.0);
    // Key-light shadow: sampled AFTER the terrain arrays (Metal first-sample-order: ShadowMap is binding 4, last).
    // Terrain RECEIVES shadows identically to models via the same shared helper. keyShadow == 1 when the map is off.
    float ndlKeyForShadow = max(dot(N, -normalize(LightDir.xyz)), 0.0);
    float keyShadow = sampleKeyShadow(ShadowMap, ShadowSamp, vWorldPos, Ngeo, ndlKeyForShadow);
    vec3 diffuse; vec3 specColor;
    computeLighting(N, vWorldPos, specStrength, specExp, keyShadow, diffuse, specColor);
    vec3 lit = albedo * (Ambient.rgb + diffuse) + specColor + vEmissive.rgb;
    oColor = vec4(lit, 1.0);
    oNormal = vec4(Ngeo * 0.5 + 0.5, 1.0); // GEOMETRIC normal for the edge pass
    oDepth = vec4(vDepth, vDepth, vDepth, 1.0);
}";

        // ---- Depth-only shadow pass. Renders the instanced casters into the key-light's ortho depth map: transform
        //      to light-clip with the light ViewProj (its own 64-byte UBO), and write the [0,1] light-clip depth into
        //      a single R32F colour target (NOT the hardware depth buffer, so the receivers sample it as a plain
        //      texture2D and do a MANUAL depth compare - portable across Metal/D3D11/Vulkan without a depth-sampling /
        //      comparison-sampler seam). The vertex reuses the model instance stream (locations 5..11 = the per-instance
        //      model matrix), so the shadow pass draws the SAME instance buffer the main pass uploaded, no second
        //      upload. Per-vertex needs only Position (location 0); the other per-vertex attributes are declared so the
        //      shared model vertex buffer binds unchanged.
        //
        //      D3D11/FXC/WARP HAZARD (load-bearing sink below): this shader only READS Position + IModel0..3, so
        //      SPIRV-Cross drops the unread inputs (Normal/Color/TexCoord/Tangent @1..4 and ITint/IEmissive/ISpecParams
        //      @9..11) from the HLSL vertex-input signature, leaving a HOLE (TEXCOORD0, then TEXCOORD5..8). FXC/WARP
        //      miscompiles a holed input signature (the same landmine the SplatVert interpolant note documents), and
        //      building THIS pipeline at scene-construction corrupted WARP so the MAIN model+splat passes rendered no
        //      colour (silhouette/normal/depth survived, only oColor was blank). The `sink` reads every declared input
        //      with a zero weight, so SPIRV-Cross keeps a CONTIGUOUS TEXCOORD0..11 signature (matching ModelVert) with
        //      no hole; gl_Position is unchanged (sink == 0). Do NOT drop the sink or reads of any input. ----
        public const string ShadowDepthVert = @"#version 450
layout(set=0, binding=0) uniform U { mat4 LightViewProj; };
layout(location=0) in vec3 Position;
layout(location=1) in vec3 Normal;
layout(location=2) in vec4 Color;
layout(location=3) in vec2 TexCoord;
layout(location=4) in vec4 Tangent;
layout(location=5) in vec4 IModel0;
layout(location=6) in vec4 IModel1;
layout(location=7) in vec4 IModel2;
layout(location=8) in vec4 IModel3;
layout(location=9) in vec4 ITint;
layout(location=10) in vec4 IEmissive;
layout(location=11) in vec4 ISpecParams;
layout(location=0) out float vLightDepth;
void main() {
    mat4 Model = mat4(IModel0, IModel1, IModel2, IModel3);
    vec4 world = Model * vec4(Position, 1.0);
    // Negligible-but-live sink over the otherwise-unread per-vertex + per-instance inputs, so SPIRV-Cross keeps the
    // HLSL vertex-input signature gap-free (TEXCOORD0..11, no hole) and FXC/WARP does not miscompile - see the note
    // above. The sink is the input SUM (NOT statically zero, so the optimizer cannot fold it away and drop the inputs)
    // scaled by 1e-30, so it is numerically negligible in world space; the projected position is unchanged to the bit.
    float sink = Normal.x + Color.x + TexCoord.x + Tangent.x + ITint.x + IEmissive.x + ISpecParams.x;
    world.x += sink * 1e-30;
    gl_Position = LightViewProj * world;
    vLightDepth = gl_Position.z / gl_Position.w;   // [0,1] light-clip depth, stored linearly in the R32F target
}";

        public const string ShadowDepthFrag = @"#version 450
layout(location=0) in float vLightDepth;
layout(location=0) out vec4 oDepth;               // single R32F target: .r carries the caster's light-space depth
void main() {
    oDepth = vec4(vLightDepth, 0.0, 0.0, 1.0);
}";

        // Skinned shadow depth vertex (GPU skinning shadow-pass mirror). Reads ONE combined resource buffer at set 0
        // ({ LightMvp; bones[128] }, LightMvp = Model * clip-corrected LightViewProj folded per draw), skins the vertex
        // exactly like SkinnedModelVert, and projects into light-clip. The fragment is the shared ShadowDepthFrag (no
        // resources). Reuses the ShadowDepthFrag output. The unread per-vertex inputs (Normal/Color/TexCoord/Tangent)
        // are summed into a 1e-30 sink so SPIRV-Cross keeps the HLSL vertex-input signature gap-free (no FXC/WARP
        // miscompile - the same trap ShadowDepthVert documents).
        public const string SkinnedShadowDepthVert = @"#version 450
layout(set=0, binding=0) uniform VBlock {
    mat4 LightMvp;         // Model * clip-corrected LightViewProj (folded per draw)
    mat4 bones[128];       // this draw's composed palette (inverseBind*jointWorld)
};
layout(location=0) in vec3 Position;
layout(location=1) in vec3 Normal;
layout(location=2) in vec4 Color;
layout(location=3) in vec2 TexCoord;
layout(location=4) in vec4 BoneIndices;
layout(location=5) in vec4 BoneWeights;
layout(location=6) in vec4 Tangent;
layout(location=0) out float vLightDepth;
void main() {
    float wsum = BoneWeights.x + BoneWeights.y + BoneWeights.z + BoneWeights.w;
    mat4 skin;
    if (wsum < 1e-8) {
        skin = mat4(1.0);
    } else {
        skin = bones[int(BoneIndices.x)] * BoneWeights.x
             + bones[int(BoneIndices.y)] * BoneWeights.y
             + bones[int(BoneIndices.z)] * BoneWeights.z
             + bones[int(BoneIndices.w)] * BoneWeights.w;
    }
    vec4 localPos = skin * vec4(Position, 1.0);
    float sink = Normal.x + Color.x + TexCoord.x + Tangent.x;   // keep the vertex-input signature gap-free
    localPos.x += sink * 1e-30;
    gl_Position = LightMvp * localPos;
    vLightDepth = gl_Position.z / gl_Position.w;
}";

        // ---- Debug line overlay. Standalone mat4 ViewProj UBO (64 bytes), its own layout/buffer,
        //      separate from the model UBO. Depth disabled + alpha blend = overlay. ----
        public const string LineVert = @"#version 450
layout(set=0, binding=0) uniform U { mat4 ViewProj; };
layout(location=0) in vec3 Position;
layout(location=1) in vec4 Color;
layout(location=0) out vec4 vColor;
void main() {
    gl_Position = ViewProj * vec4(Position, 1.0);
    vColor = Color;
}";

        public const string LineFrag = @"#version 450
layout(location=0) in vec4 vColor;
layout(location=0) out vec4 oColor;
void main() {
    oColor = vColor;
}";

        // ---- Camera-facing billboard overlay. Standalone mat4 ViewProj UBO (64 bytes), its own layout/buffer.
        //      Soft disc: alpha falls to 0 toward the corners so the quad reads as a round, feathered sprite.
        //      Depth disabled + alpha-or-additive blend = overlay; drawn after the line pass. ----
        public const string BillboardVert = @"#version 450
layout(set=0, binding=0) uniform U { mat4 ViewProj; };
layout(location=0) in vec3 Position;
layout(location=1) in vec2 Uv;
layout(location=2) in vec4 Color;
layout(location=0) out vec2 vUv;
layout(location=1) out vec4 vColor;
void main() {
    gl_Position = ViewProj * vec4(Position, 1.0);
    vUv = Uv;
    vColor = Color;
}";

        public const string BillboardFrag = @"#version 450
layout(location=0) in vec2 vUv;
layout(location=1) in vec4 vColor;
layout(location=0) out vec4 oColor;
void main() {
    float d = length(vUv * 2.0 - 1.0);
    float a = smoothstep(1.0, 0.55, d);
    oColor = vec4(vColor.rgb, vColor.a * a);
}";

        // ---- Textured, depth-interleaved billboard. Unlike the soft-disc overlay above (which draws AFTER the
        //      post chain with depth disabled), this draws INTO the model MRT alongside the meshes with the depth
        //      test on (no depth write), so a nearer mesh occludes the quad and the quad draws over a farther mesh.
        //      It reuses BillboardVert. It samples a texture sub-rect (vUv carries the source rect) times the tint,
        //      and writes all 3 MRT targets so the SPIR-V output count matches the framebuffer: only colour matters
        //      (the normal/depth attachments use a PreserveDestination blend, so what it writes there is discarded). ----
        public const string TexturedBillboardFrag = @"#version 450
layout(set=0, binding=1) uniform texture2D Tex;
layout(set=0, binding=2) uniform sampler Samp;
layout(location=0) in vec2 vUv;
layout(location=1) in vec4 vColor;
layout(location=0) out vec4 oColor;
layout(location=1) out vec4 oNormal;
layout(location=2) out vec4 oDepth;
void main() {
    vec4 t = texture(sampler2D(Tex, Samp), vUv);
    oColor = vec4(t.rgb * vColor.rgb, t.a * vColor.a);
    oNormal = vec4(0.0);   // discarded (PreserveDestination blend on attachment 1)
    oDepth  = vec4(0.0);   // discarded (PreserveDestination blend on attachment 2)
}";

        // ---- Modern particle sprites (ParticleRenderer). One instanced draw for the whole frame's queue:
        //      per-instance attributes carry each sprite (locations 0..4, no holes, every attribute consumed,
        //      the D3D11 contiguous-input contract), the two-triangle quad comes from gl_VertexIndex like the
        //      decal pass. Billboarding, spin, and velocity-aligned stretch happen in the vertex stage from the
        //      camera basis in the single Frame UBO (ONE uniform buffer per pipeline, declared identically in
        //      both stages, the Metal contract). The fragment evaluates a procedural SDF/noise shape per sprite
        //      and a soft depth fade against the reconstructed scene surface (the ground-decal texelFetch +
        //      RAW-InvViewProj recipe). Output is PREMULTIPLIED color with alpha scaled by (1 - additivity), so
        //      alpha and additive sprites composite correctly in one back-to-front sorted draw under a single
        //      (One, InverseSourceAlpha) blend state. Draws into ColorDepthFB (lit color + read-only scene
        //      depth) after the water pass and before the post chain, so sprites are occluded by geometry and
        //      additive glow feeds bloom. ----
        public const string ParticleVert = @"#version 450
layout(set=0, binding=0) uniform Frame {
    mat4 ViewProj;      // GpuClip-corrected world->clip (positions the quads per backend)
    mat4 InvViewProj;   // RAW inverse, matching Camera.ScreenToRay (soft-fade reconstruction only)
    vec4 CamRight;      // xyz camera right
    vec4 CamUp;         // xyz camera up
    vec4 CamPosTime;    // xyz eye position, w effect time seconds
    vec4 Params;        // x soft-fade distance (0 = off), y quality (1 full / 0 reduced), z background depth-clear marker, w reserved
};
layout(location=0) in vec4 ICenterSize;   // xyz world center, w half-size
layout(location=1) in vec4 IVelocityRot;  // xyz world velocity, w rotation (radians)
layout(location=2) in vec4 IColor;        // straight rgba tint (premultiplied by the fragment)
layout(location=3) in vec4 IShape;        // x shape id, y shape param, z life norm, w seed
layout(location=4) in vec4 IExtra;        // x stretch factor, y additivity (0 alpha / 1 additive), zw reserved
layout(location=0) out vec2 vLocal;
layout(location=1) out vec4 vColor;
layout(location=2) out vec4 vShape;
layout(location=3) out vec4 vExtra;       // x aspect (stretch elongation), y additivity, zw carried reserved lanes
layout(location=4) out vec3 vWorld;
void main() {
    // Two-triangle quad from gl_VertexIndex (0..5), the same instanced-quad path DecalVert uses.
    float u = (gl_VertexIndex == 1 || gl_VertexIndex == 3 || gl_VertexIndex == 4) ? 1.0 : 0.0;
    float v = (gl_VertexIndex == 2 || gl_VertexIndex == 4 || gl_VertexIndex == 5) ? 1.0 : 0.0;
    vec2 corner = vec2(u, v) * 2.0 - 1.0;

    float size = max(ICenterSize.w, 1e-5);
    // Velocity-aligned stretch: project the velocity onto the camera plane. When usable, the quad's local +X
    // follows the on-screen motion direction and lengthens with speed (so the Spark shape's bright head points
    // where the particle is going). Otherwise the quad rolls by its rotation like any round sprite.
    vec2 v2 = vec2(dot(IVelocityRot.xyz, CamRight.xyz), dot(IVelocityRot.xyz, CamUp.xyz));
    float speed2 = length(v2);
    float aspect = 1.0;
    vec2 ax;
    if (IExtra.x > 0.0 && speed2 > 1e-4) {
        ax = v2 / speed2;
        aspect = min(1.0 + IExtra.x * speed2 / size, 8.0);
    } else {
        float cr = cos(IVelocityRot.w);
        float sr = sin(IVelocityRot.w);
        ax = vec2(cr, sr);
    }
    vec2 ay = vec2(-ax.y, ax.x);
    vec3 axisX = CamRight.xyz * ax.x + CamUp.xyz * ax.y;
    vec3 axisY = CamRight.xyz * ay.x + CamUp.xyz * ay.y;
    vec3 world = ICenterSize.xyz + axisX * (corner.x * size * aspect) + axisY * (corner.y * size);
    gl_Position = ViewProj * vec4(world, 1.0);
    vLocal = corner;
    vColor = IColor;
    vShape = IShape;
    vExtra = vec4(aspect, IExtra.y, IExtra.z, IExtra.w);
    vWorld = world;
}";

        public const string ParticleFrag = @"#version 450
layout(set=0, binding=0) uniform Frame {
    mat4 ViewProj;
    mat4 InvViewProj;
    vec4 CamRight;
    vec4 CamUp;
    vec4 CamPosTime;
    vec4 Params;
};
layout(set=0, binding=1) uniform texture2D DepthTex;   // .r = scene NDC depth (single-channel R32F, resolved)
layout(set=0, binding=2) uniform sampler Samp;
layout(location=0) in vec2 vLocal;    // quad-local coords in [-1,1] (rotate/stretch with the quad)
layout(location=1) in vec4 vColor;
layout(location=2) in vec4 vShape;    // x shape id, y shape param, z life norm, w seed
layout(location=3) in vec4 vExtra;    // x aspect, y additivity, zw reserved
layout(location=4) in vec3 vWorld;    // fragment world position (flat across the quad's plane)
layout(location=0) out vec4 oColor;

// Texture-free value noise, the exact polynomial-hash idiom the decal pass ships cross-backend goldens with
// (sin-based hashes diverge between GPU compilers, this one does not).
float hash21(vec2 p) { p = fract(p * vec2(123.34, 345.45)); p += dot(p, p + 34.345); return fract(p.x * p.y); }
float vnoise(vec2 p)
{
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = hash21(i);
    float b = hash21(i + vec2(1.0, 0.0));
    float c = hash21(i + vec2(0.0, 1.0));
    float d = hash21(i + vec2(1.0, 1.0));
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

void main() {
    int shape = int(vShape.x + 0.5);
    float param = clamp(vShape.y, 0.0, 1.0);
    float life = clamp(vShape.z, 0.0, 1.0);
    float seed = vShape.w;
    float t = CamPosTime.w;
    float quality = Params.y;
    float d = length(vLocal);

    float mask;
    if (shape == 0) {
        // SoftGlow: normalized gaussian-like falloff. Param 0 is the wide classic blob, param 1 a hot center.
        float k = mix(5.0, 14.0, param);
        mask = max(exp(-d * d * k) - exp(-k), 0.0) / (1.0 - exp(-k));
    } else if (shape == 1) {
        // Ember: tight hot core + faint halo, subtle per-particle flicker (Full quality only).
        float coreK = mix(28.0, 10.0, param);
        float flicker = 1.0;
        if (quality > 0.5) flicker = 0.82 + 0.36 * vnoise(vec2(seed * 289.0, t * 7.0));
        mask = (exp(-d * d * coreK) + exp(-d * d * 3.5) * 0.30) * flicker;
    } else if (shape == 2) {
        // Spark: rounded capsule along local +X in aspect-corrected space (caps stay round on screen), with a
        // bright head toward +X (the motion direction under stretch) and a tail that dims by param.
        vec2 q = vec2(vLocal.x * vExtra.x, vLocal.y);
        float capR = 0.30;
        float halfLen = max(vExtra.x - capR, 0.0);
        float sd = length(q - vec2(clamp(q.x, -halfLen, halfLen), 0.0));
        float body = 1.0 - smoothstep(capR * 0.25, capR, sd);
        float along = clamp(q.x / max(vExtra.x, 1e-4) * 0.5 + 0.5, 0.0, 1.0);
        mask = body * (0.35 + 0.65 * pow(along, mix(1.0, 3.0, param)));
    } else if (shape == 3) {
        // Wisp: noise-eroded smoke. The erosion threshold rises with life so the sprite dissolves at its edges
        // instead of fading uniformly. Reduced quality drops the second octave.
        float base = 1.0 - smoothstep(0.25, 1.0, d);
        vec2 np = vLocal * 2.6 + vec2(seed * 157.0, seed * 93.0);
        float n = vnoise(np + vec2(t * 0.35, -t * 0.22));
        if (quality > 0.5) n = 0.65 * n + 0.35 * vnoise(np * 2.3 - vec2(t * 0.5, t * 0.3));
        float erode = mix(0.08, 0.85, life) + (param - 0.5) * 0.3;
        mask = base * smoothstep(erode, erode + 0.28, n * 0.7 + base * 0.5);
    } else if (shape == 4) {
        // Ring: soft annulus for shockwaves. Param widens the band.
        float th = mix(0.055, 0.24, param);
        mask = (1.0 - smoothstep(th * 0.35, th, abs(d - 0.70))) * (1.0 - smoothstep(0.92, 1.0, d));
    } else {
        // Star: four-point glint plus a small hot center. Param sharpens the rays.
        float ang = atan(vLocal.y, vLocal.x);
        float rays = pow(abs(cos(ang * 2.0)), mix(2.0, 10.0, param));
        mask = exp(-d * d * 5.0) * (0.22 + 0.78 * rays) + exp(-d * d * 30.0) * 0.6;
    }

    // Soft depth fade: reconstruct the scene surface behind this fragment (texelFetch at gl_FragCoord + RAW
    // InvViewProj, the backend-independent decal recipe) and fade coverage over Params.x world units of
    // approach. The depth-color attachment is CLEARED to the background color's red channel, not to the far
    // plane, so a sample equal to that marker (Params.z) means no geometry: skip the fade there instead of
    // reconstructing a bogus near-plane point that would dim sprites against empty sky.
    float fade = 1.0;
    if (Params.x > 0.0) {
        ivec2 sz = textureSize(sampler2D(DepthTex, Samp), 0);
        float depth = texelFetch(sampler2D(DepthTex, Samp), ivec2(gl_FragCoord.xy), 0).r;
        if (abs(depth - Params.z) > 1e-6) {
            vec4 ndc = vec4(gl_FragCoord.x / float(sz.x) * 2.0 - 1.0, 1.0 - gl_FragCoord.y / float(sz.y) * 2.0, depth, 1.0);
            vec4 wp = InvViewProj * ndc;
            vec3 sceneWorld = wp.xyz / wp.w;
            float sceneDist = distance(sceneWorld, CamPosTime.xyz);
            float fragDist = distance(vWorld, CamPosTime.xyz);
            fade = clamp((sceneDist - fragDist) / Params.x, 0.0, 1.0);
        }
    }

    // Premultiplied output under a (One, InverseSourceAlpha) blend: alpha sprites keep their coverage in the
    // alpha lane, additive sprites zero it (out = dst + rgb), so one sorted stream composites both correctly.
    float a = clamp(vColor.a * mask, 0.0, 1.0) * fade;
    oColor = vec4(vColor.rgb * a, a * (1.0 - vExtra.y));
}";

        // ---- Additive glowing beam (lasers/thrusters/tethers). Drawn INTO the model MRT alongside the meshes
        //      with the depth test on (no write), so geometry occludes it (like the textured billboard). A
        //      camera-facing strip carries (across,along) UV; the core+halo profile is computed in the fragment
        //      shader from the across coordinate, with optional end taper + time-driven pulse/scroll. Per-beam
        //      style is baked per-vertex (split core/glow colour + two packed param vectors); the only uniform is
        //      ViewProj + Time, so the whole frame's beams render in one draw. Writes all 3 MRT targets to match
        //      the framebuffer; only colour matters (normal/depth use a PreserveDestination blend). ----
        public const string BeamVert = @"#version 450
layout(set=0, binding=0) uniform U { mat4 ViewProj; vec4 Time; };
layout(location=0) in vec3 Position;
layout(location=1) in vec2 Uv;
layout(location=2) in vec4 CoreColor;
layout(location=3) in vec4 GlowColor;
layout(location=4) in vec4 Shape;   // x=coreFrac, y=glowSoftness, z=taper
layout(location=5) in vec4 Anim;    // x=pulseSpeed, y=pulseAmount, z=scrollSpeed
layout(location=0) out vec2 vUv;
layout(location=1) out vec4 vCoreColor;
layout(location=2) out vec4 vGlowColor;
layout(location=3) out vec4 vShape;
layout(location=4) out vec4 vAnim;
void main() {
    gl_Position = ViewProj * vec4(Position, 1.0);
    vUv = Uv;
    vCoreColor = CoreColor;
    vGlowColor = GlowColor;
    vShape = Shape;
    vAnim = Anim;
}";

        public const string BeamFrag = @"#version 450
layout(set=0, binding=0) uniform U { mat4 ViewProj; vec4 Time; };
layout(location=0) in vec2 vUv;
layout(location=1) in vec4 vCoreColor;
layout(location=2) in vec4 vGlowColor;
layout(location=3) in vec4 vShape;   // x=coreFrac, y=glowSoftness, z=taper
layout(location=4) in vec4 vAnim;    // x=pulseSpeed, y=pulseAmount, z=scrollSpeed
layout(location=0) out vec4 oColor;
layout(location=1) out vec4 oNormal;
layout(location=2) out vec4 oDepth;
void main() {
    float coreFrac = max(vShape.x, 0.02);
    float glowSoft = max(vShape.y, 0.5);
    float taper    = clamp(vShape.z, 0.0, 0.5);
    float d = abs(vUv.x * 2.0 - 1.0);                       // 0 at the axis, 1 at the edge
    float core = 1.0 - smoothstep(coreFrac * 0.6, coreFrac, d);
    float glow = pow(max(1.0 - d, 0.0), glowSoft);
    float taperFade = (taper > 0.0)
        ? smoothstep(0.0, taper, vUv.y) * smoothstep(0.0, taper, 1.0 - vUv.y)
        : 1.0;
    float pulse = 1.0 + vAnim.y * sin(Time.x * vAnim.x);
    float flow  = (vAnim.z != 0.0)
        ? 0.85 + 0.15 * sin((vUv.y - Time.x * vAnim.z) * 6.2831853)
        : 1.0;
    float master = max(taperFade * pulse, 0.0);
    vec3 rgb = vCoreColor.rgb * vCoreColor.a * core * flow
             + vGlowColor.rgb * vGlowColor.a * glow;
    oColor  = vec4(rgb, master);   // Additive (src.a / one): out.rgb = rgb*master + dst.rgb
    oNormal = vec4(0.0);           // discarded (PreserveDestination on attachment 1)
    oDepth  = vec4(0.0);           // discarded (PreserveDestination on attachment 2)
}";

        // ---- Motion-trail ribbon (weapon swings, thruster streaks, tracers): a tapered strip traced along a moving
        //      point, built by TrailGeometry. The tail fade + taper are baked into the vertex (Color.a, geometry);
        //      the fragment only feathers the across-width edge (like the beam does its core/glow). One vertex shader
        //      feeds both blend pipelines: the output vec4(rgb, a) is what Additive (src.a/1) and AlphaBlend
        //      (src.a/1-src.a) both consume. Writes all 3 MRT targets; attachments 1 & 2 preserve destination. ----
        public const string TrailVert = @"#version 450
layout(set=0, binding=0) uniform U { mat4 ViewProj; };
layout(location=0) in vec3 Position;
layout(location=1) in vec3 Uv;      // x=across, y=along, z=softEdge
layout(location=2) in vec4 Color;   // rgb tint, a = style.alpha * sample.alpha
layout(location=0) out vec3 vUv;
layout(location=1) out vec4 vColor;
void main() {
    gl_Position = ViewProj * vec4(Position, 1.0);
    vUv = Uv;
    vColor = Color;
}";

        public const string TrailFrag = @"#version 450
layout(location=0) in vec3 vUv;     // x=across, y=along, z=softEdge
layout(location=1) in vec4 vColor;
layout(location=0) out vec4 oColor;
layout(location=1) out vec4 oNormal;
layout(location=2) out vec4 oDepth;
void main() {
    float d = abs(vUv.x * 2.0 - 1.0);                  // 0 at the axis, 1 at the edge
    float soft = clamp(vUv.z, 0.0, 1.0);
    float edge = 1.0 - smoothstep(1.0 - soft, 1.0, d); // feather the across-width edge
    float a = vColor.a * edge;
    oColor  = vec4(vColor.rgb, a);   // Additive: rgb*a + dst; AlphaBlend: rgb*a + dst*(1-a)
    oNormal = vec4(0.0);             // discarded (PreserveDestination on attachment 1)
    oDepth  = vec4(0.0);             // discarded (PreserveDestination on attachment 2)
}";

        // ---- Translucent unlit overlay mesh (collision proxies, nav/AoI/chunk-bounds later). Drawn INTO the model
        //      MRT (still bound) after the beams and before the post chain, with the depth test on (less-equal, no
        //      write) so a proxy is occluded by nearer scene geometry but still blends over farther geometry. Colour
        //      comes straight from the mesh's per-vertex ModelVertex.Color (unlit), alpha via the blend. ONE dynamic
        //      UBO per draw carries BOTH the frame ViewProj and the per-draw World (a single 128-byte slot selected
        //      by a dynamic offset). This deliberately does NOT split ViewProj/World into two UBO bindings: Veldrid/
        //      SPIRV-Cross on Metal mis-binds a SECOND uniform buffer in a set (it reads the first buffer's bytes -
        //      the same trap the splat/model passes fold around by using one UBO), so both matrices ride in one
        //      buffer. The vertex layout declares the full ModelVertex (locations 0..4) so the same GPU vertex buffer
        //      the model pass uses binds unchanged; only Position (0) and Color (2) are read here. Writes all 3 MRT
        //      targets so the SPIR-V output count matches the framebuffer; only colour matters (attachments 1 and 2
        //      use a PreserveDestination blend, so the meshes' normal/depth reach the edge pass untouched). ----
        public const string OverlayUnlitVert = @"#version 450
layout(set=0, binding=0) uniform Draw { mat4 ViewProj; mat4 World; };
layout(location=0) in vec3 Position;
layout(location=1) in vec3 Normal;
layout(location=2) in vec4 Color;
layout(location=3) in vec2 TexCoord;
layout(location=4) in vec4 Tangent;
layout(location=0) out vec4 vColor;
void main() {
    gl_Position = ViewProj * (World * vec4(Position, 1.0));
    vColor = Color;
}";

        public const string OverlayUnlitFrag = @"#version 450
layout(location=0) in vec4 vColor;
layout(location=0) out vec4 oColor;
layout(location=1) out vec4 oNormal;
layout(location=2) out vec4 oDepth;
void main() {
    oColor = vColor;       // alpha via the AlphaBlend attachment on target 0
    oNormal = vec4(0.0);   // discarded (PreserveDestination blend on attachment 1)
    oDepth  = vec4(0.0);   // discarded (PreserveDestination blend on attachment 2)
}";

        // ---- Shared fullscreen triangle ----
        public const string FullscreenVert = @"#version 450
layout(location=0) out vec2 vUv;
void main() {
    vec2 p = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
    vUv = p;
    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}";

        // ---- Palette quantize (+ optional Bayer dither) ----
        public const string PaletteFrag = @"#version 450
layout(set=0, binding=0) uniform texture2D Src;
layout(set=0, binding=1) uniform sampler Samp;
layout(set=0, binding=2) uniform Pal { vec4 Colors[64]; vec4 Info; }; // Info.x=count, .y=ditherOn
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
const float bayer[16] = float[16](
    0.0, 8.0, 2.0, 10.0, 12.0, 4.0, 14.0, 6.0, 3.0, 11.0, 1.0, 9.0, 15.0, 7.0, 13.0, 5.0);
void main() {
    vec4 src = texture(sampler2D(Src, Samp), vUv);
    vec3 c = src.rgb;
    if (Info.y > 0.5) {
        ivec2 px = ivec2(gl_FragCoord.xy);
        float th = (bayer[(px.y & 3) * 4 + (px.x & 3)] / 16.0 - 0.5);
        c += th * (1.0 / 8.0);
    }
    int count = int(Info.x);
    float best = 1e9; vec3 bestC = c;
    for (int i = 0; i < count; i++) {
        vec3 pc = Colors[i].rgb;
        float d = dot(c - pc, c - pc);
        if (d < best) { best = d; bestC = pc; }
    }
    oColor = vec4(bestC, src.a); // preserve background alpha marker
}";

        // ---- Depth/normal edge outline ----
        // Bug B fix: sample ColorTex, NormalTex, DepthTex UP FRONT in BINDING ORDER. On Metal SPIRV-Cross assigns
        // MSL texture indices by first-sample order, so sampling Depth before Normal (the old order) swapped the
        // two samplers and the normal-edge term silently read depth data (mirrors the ModelFrag Albedo/NormalMap/
        // Roughness fix; D3D11/Vulkan bind by explicit decoration and are order-insensitive).
        // Fix C: under perspective the stored z/w is non-linear, so a fixed threshold pops on zoom/distance.
        // Linearize to view-space eye distance (Thresh.zw = near/far) and compare a depth delta RELATIVE to view
        // depth. Orthographic (Texel.z == 0) keeps the original raw abs(d-d0) > Thresh.x test, byte-identical.
        // linearizeDepth mirrors OutlineMath.LinearizeDepth (keep in sync).
        public const string EdgeFrag = @"#version 450
layout(set=0, binding=0) uniform texture2D ColorTex;
layout(set=0, binding=1) uniform texture2D NormalTex;
layout(set=0, binding=2) uniform texture2D DepthTex;
layout(set=0, binding=3) uniform sampler Samp;
layout(set=0, binding=4) uniform Edge { vec4 OutlineColor; vec4 Texel; vec4 Thresh; vec4 Fade; };
// Texel.xy=1/size, .z=isPerspective, .w=distanceFadeOn; Thresh.x=depth, .y=normal, .z=near, .w=far; Fade.xy=start/end
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
float linearizeDepth(float d, float near, float far) { return (near * far) / (far - d * (far - near)); }
void main() {
    // Up-front, in binding order (Color, Normal, Depth) - see Bug B note above.
    vec4 baseSrc = texture(sampler2D(ColorTex, Samp), vUv);
    vec3 base = baseSrc.rgb;
    vec3 n0 = texture(sampler2D(NormalTex, Samp), vUv).rgb * 2.0 - 1.0;
    float d0 = texture(sampler2D(DepthTex, Samp), vUv).r;

    bool persp = Texel.z > 0.5;
    float near = Thresh.z, far = Thresh.w;
    vec2 ex = vec2(Texel.x, 0.0), ey = vec2(0.0, Texel.y);

    // Four-neighbour samples (binding order preserved: Normal first, then Depth).
    vec3 nL = texture(sampler2D(NormalTex, Samp), vUv - ex).rgb * 2.0 - 1.0;
    vec3 nR = texture(sampler2D(NormalTex, Samp), vUv + ex).rgb * 2.0 - 1.0;
    vec3 nU = texture(sampler2D(NormalTex, Samp), vUv + ey).rgb * 2.0 - 1.0;
    vec3 nD = texture(sampler2D(NormalTex, Samp), vUv - ey).rgb * 2.0 - 1.0;
    float dL = texture(sampler2D(DepthTex, Samp), vUv - ex).r;
    float dR = texture(sampler2D(DepthTex, Samp), vUv + ex).r;
    float dU = texture(sampler2D(DepthTex, Samp), vUv + ey).r;
    float dD = texture(sampler2D(DepthTex, Samp), vUv - ey).r;

    float edge = 0.0;
    // Normal-crease edge: fire if ANY neighbour's geometric normal turns by more than the threshold. Flat
    // surfaces (constant normal) never fire; this catches interior creases the depth term misses (Bug B).
    if ((1.0 - dot(nL, n0)) > Thresh.y || (1.0 - dot(nR, n0)) > Thresh.y ||
        (1.0 - dot(nU, n0)) > Thresh.y || (1.0 - dot(nD, n0)) > Thresh.y) edge = 1.0;

    float lin0 = persp ? linearizeDepth(d0, near, far) : d0;
    if (persp) {
        // Perspective depth edge: a SECOND difference (Laplacian) of view-space depth, relative to depth. It is
        // ~0 on smooth surfaces - including a steep grazing ground plane (constant slope => zero curvature), which
        // a first difference floods - and spikes at silhouettes / occlusion steps. Stable at any zoom.
        float lL = linearizeDepth(dL, near, far), lR = linearizeDepth(dR, near, far);
        float lU = linearizeDepth(dU, near, far), lD = linearizeDepth(dD, near, far);
        float lap = max(abs(lL + lR - 2.0 * lin0), abs(lU + lD - 2.0 * lin0));
        if (lap > Thresh.x * lin0) edge = 1.0;
        if (Texel.w > 0.5) edge *= 1.0 - smoothstep(Fade.x, Fade.y, lin0);   // optional distance fade (default off)
    } else {
        // Ortho: raw linear z/w, per-neighbour first difference (UNCHANGED, byte-identical to the original loop).
        if (abs(dL - d0) > Thresh.x || abs(dR - d0) > Thresh.x ||
            abs(dU - d0) > Thresh.x || abs(dD - d0) > Thresh.x) edge = 1.0;
    }

    oColor = vec4(mix(base, OutlineColor.rgb, edge), baseSrc.a); // preserve background alpha marker
}";

        // ---- Final upscale blit (+ optional procedural starfield in the background) ----
        // Background is flagged by the color target's alpha (model writes a=1, the clear sets a=0),
        // which the palette/edge passes preserve. Keeps the blit to a safe 3-binding set (the depth
        // texture in here tripped a backend/Metal multi-resource binding bug).
        public const string BlitFrag = @"#version 450
layout(set=0, binding=0) uniform texture2D Src;
layout(set=0, binding=1) uniform sampler Samp;
layout(set=0, binding=2) uniform Final { vec4 BgColor; vec4 Params; }; // Params.x=starsOn, .y=transparentBg, .z=flipV
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
void main() {
    // Bug A: each fullscreen post pass flips vertically, so the orientation depends on the parity of how many
    // ran. The blit cancels it (Params.z = flipV) so every config is upright. Starfield stays in screen space.
    vec2 suv = (Params.z > 0.5) ? vec2(vUv.x, 1.0 - vUv.y) : vUv;
    vec4 s = texture(sampler2D(Src, Samp), suv);
    vec3 col = s.rgb;
    if (Params.x > 0.5 && s.a < 0.5) {                   // background (alpha marker) -> stars
        vec2 cell = floor(vUv * vec2(220.0, 124.0));
        float star = step(0.992, hash(cell)) * (0.55 + 0.45 * hash(cell + 3.7));
        col = BgColor.rgb + vec3(star);
    }
    // Opaque on-screen by default; for an offscreen preview (Params.y) keep the alpha marker so the cleared
    // background composites transparently (geometry a=1 stays opaque, cleared background a=0 stays clear).
    float outA = (Params.y > 0.5) ? s.a : 1.0;
    oColor = vec4(col, outA);
}";

        // ---- Teleport transition: solid fullscreen fill (HardBlink) ----
        // A fullscreen quad of Fill.rgb at opacity Fill.a, drawn OVER the final image with standard src-alpha blend
        // (so result = fill*a + dst*(1-a)). Orientation-independent (no texture sample). Only ever drawn when a
        // transition is active with Cover > 0, so a frame with no active transition is byte-identical to before.
        public const string TransitionSolidFrag = @"#version 450
layout(set=0, binding=0) uniform Fill { vec4 ColorAlpha; }; // rgb = fill colour, a = opacity (the transition's Cover)
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
void main() { oColor = ColorAlpha; }";

        // ---- Teleport transition: frozen-frame crossfade (CameraDissolve) ----
        // Samples the captured pre-teleport frame (a raw copy of the resolved ColorTex) and draws it OVER the live
        // final image at opacity Params.x (the frozen weight = Cover), with src-alpha blend: result = frozen*Cover +
        // live*(1-Cover). The V flip is constant: the frozen texture is a ColorTex copy, and a fullscreen pass reading
        // ColorTex needs the same single flip the final blit applies for a ColorTex source (BlitFrag's even-parity
        // flipV=1 case), so it lands upright over the already-upright target. Only drawn while active, so inactive
        // frames stay byte-identical.
        public const string TransitionCrossfadeFrag = @"#version 450
layout(set=0, binding=0) uniform texture2D Src;
layout(set=0, binding=1) uniform sampler Samp;
layout(set=0, binding=2) uniform Params { vec4 P; }; // P.x = frozen-frame opacity (the transition's Cover)
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
void main() {
    vec2 suv = vec2(vUv.x, 1.0 - vUv.y);   // match the blit's ColorTex flip so the frozen frame aligns with the target
    vec4 s = texture(sampler2D(Src, Samp), suv);
    oColor = vec4(s.rgb, P.x);
}";

        // ---- FXAA (fast approximate anti-aliasing) ----
        // The classic Timothy Lottes FXAA3-console pass: read a 3x3 luma neighbourhood, skip near-flat areas
        // (contrast gate), otherwise estimate the edge direction from the luma gradient and blend two/four taps along
        // it. Softens high-contrast edges (geometry silhouettes AND shaded interiors) in one cheap fullscreen pass.
        // Preserves the CENTRE pixel's alpha so the blit's background marker (a < 0.5 -> starfield / transparent) still
        // works. Runs on the internal target BEFORE the blit; like the other post passes it flips V, so the blit's
        // flipV parity counts it. Rcp.xy = 1/targetSize.
        public const string FxaaFrag = @"#version 450
layout(set=0, binding=0) uniform texture2D Src;
layout(set=0, binding=1) uniform sampler Samp;
layout(set=0, binding=2) uniform Fxaa { vec4 Rcp; }; // .xy = 1/size
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
float luma(vec3 c) { return dot(c, vec3(0.299, 0.587, 0.114)); }
void main() {
    vec2 inv = Rcp.xy;
    vec4 M = texture(sampler2D(Src, Samp), vUv);
    vec3 rgbM = M.rgb;
    vec3 rgbNW = texture(sampler2D(Src, Samp), vUv + vec2(-1.0, -1.0) * inv).rgb;
    vec3 rgbNE = texture(sampler2D(Src, Samp), vUv + vec2( 1.0, -1.0) * inv).rgb;
    vec3 rgbSW = texture(sampler2D(Src, Samp), vUv + vec2(-1.0,  1.0) * inv).rgb;
    vec3 rgbSE = texture(sampler2D(Src, Samp), vUv + vec2( 1.0,  1.0) * inv).rgb;
    float lM = luma(rgbM), lNW = luma(rgbNW), lNE = luma(rgbNE), lSW = luma(rgbSW), lSE = luma(rgbSE);
    float lMin = min(lM, min(min(lNW, lNE), min(lSW, lSE)));
    float lMax = max(lM, max(max(lNW, lNE), max(lSW, lSE)));
    float range = lMax - lMin;
    // Contrast gate: leave near-flat regions untouched (also keeps flat interiors crisp and cheap).
    if (range < max(0.0312, lMax * 0.125)) { oColor = vec4(rgbM, M.a); return; }
    vec2 dir;
    dir.x = -((lNW + lNE) - (lSW + lSE));
    dir.y =  ((lNW + lSW) - (lNE + lSE));
    float dirReduce = max((lNW + lNE + lSW + lSE) * (0.25 * 0.125), 1.0 / 128.0);
    float rcpDirMin = 1.0 / (min(abs(dir.x), abs(dir.y)) + dirReduce);
    dir = clamp(dir * rcpDirMin, vec2(-8.0), vec2(8.0)) * inv;
    vec3 rgbA = 0.5 * (texture(sampler2D(Src, Samp), vUv + dir * (1.0 / 3.0 - 0.5)).rgb
                     + texture(sampler2D(Src, Samp), vUv + dir * (2.0 / 3.0 - 0.5)).rgb);
    vec3 rgbB = rgbA * 0.5 + 0.25 * (texture(sampler2D(Src, Samp), vUv + dir * -0.5).rgb
                                   + texture(sampler2D(Src, Samp), vUv + dir *  0.5).rgb);
    float lB = luma(rgbB);
    vec3 outRgb = (lB < lMin || lB > lMax) ? rgbA : rgbB;   // reject an over-blurred tap
    oColor = vec4(outRgb, M.a);
}";

        // ---- Bloom: bright-pass -> separable gaussian blur (H then V) -> additive composite. LDR (R8G8B8A8UNorm
        // internal target, no HDR headroom), so the bright-pass thresholds the already-tonemapped-to-[0,1] lit
        // colour rather than an over-1.0 linear value - see BloomSettings' LDR-not-HDR doc note. kneeWeight mirrors
        // BloomMath.KneeWeight EXACTLY (keep in sync); the gaussian blur's weights are uploaded from
        // BloomMath.GaussianWeights so the shader never re-derives sigma. All three passes reuse FullscreenVert (a
        // fullscreen vUv in [0,1] independent of the target's resolution), so the bright-pass and blur passes -
        // which render into the HALF-RES bloom targets - need no extra scaling: the vertex shader, the framebuffer
        // viewport, and Src's sampling all resolve in the destination's own resolution.

        // ---- Bloom bright-pass: threshold the full-res lit colour into the half-res bright target ----
        public const string BloomBrightFrag = @"#version 450
layout(set=0, binding=0) uniform texture2D Src;
layout(set=0, binding=1) uniform sampler Samp;
layout(set=0, binding=2) uniform Bright { vec4 Params; }; // Params.x=threshold, .y=knee
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
float luma(vec3 c) { return dot(c, vec3(0.299, 0.587, 0.114)); }
// Mirrors BloomMath.KneeWeight exactly: knee<=0 is a hard threshold; else a smoothstep ramp of half-width `knee`
// centred on `threshold`.
float kneeWeight(float l, float threshold, float knee) {
    if (knee <= 0.0) return l >= threshold ? 1.0 : 0.0;
    float lo = threshold - knee;
    float hi = threshold + knee;
    float t = clamp((l - lo) / max(hi - lo, 1e-5), 0.0, 1.0);
    return t * t * (3.0 - 2.0 * t);
}
void main() {
    vec3 c = texture(sampler2D(Src, Samp), vUv).rgb;
    float w = kneeWeight(luma(c), Params.x, Params.y);
    oColor = vec4(c * w, 1.0);
}";

        // ---- Bloom separable gaussian blur: one axis per draw (horizontal pass writes to BloomB, vertical pass
        // reads BloomB and writes back to BloomA), radius taps per side (MaxRadius clamps the array). Weights[i].x
        // holds the 1D weight for offset i (0 = the centre tap); the shader mirrors the tap loop against a runtime
        // tap count (2*radius+1) so a radius smaller than MaxRadius just leaves the tail weights unused (0-init from
        // the C# array is never assumed - the loop bound (Params.x) is authoritative). ----
        public const string BloomBlurFrag = @"#version 450
layout(set=0, binding=0) uniform texture2D Src;
layout(set=0, binding=1) uniform sampler Samp;
layout(set=0, binding=2) uniform Blur {
    vec4 Texel;              // .xy = 1/halfResSize
    vec4 Params;             // .x = radius (taps per side), .y = dirX (texel units), .z = dirY (texel units)
    vec4 Weights[9];         // Weights[i].x = 1D gaussian weight for tap i (i=0..radius); MaxRadius=8 => 9 slots
};
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
void main() {
    int radius = int(Params.x);
    vec2 dir = vec2(Params.y, Params.z) * Texel.xy;
    vec3 sum = texture(sampler2D(Src, Samp), vUv).rgb * Weights[0].x;
    for (int i = 1; i <= radius; i++) {
        float w = Weights[i].x;
        vec2 o = dir * float(i);
        sum += texture(sampler2D(Src, Samp), vUv + o).rgb * w;
        sum += texture(sampler2D(Src, Samp), vUv - o).rgb * w;
    }
    oColor = vec4(sum, 1.0);
}";

        // ---- Bloom composite: additively blend the blurred (half-res, bilinear-upsampled by the sampler) bright
        // target onto the full-res colour chain. Sampled UP FRONT in binding order (Src, Bloom - see the Metal
        // first-sample-order rule in EdgeFrag/ModelFrag). Preserves Src's alpha UNCHANGED so the blit's background
        // marker (alpha<0.5 -> starfield / TransparentBackground) is untouched - bloom must never resurrect an
        // alpha-0 background pixel into an opaque one; adding a near-zero (thresholded-out) bloom colour to the
        // background also does not visibly brighten it in practice, since nothing exceeds the bright-pass threshold
        // there.
        // Bug A (the same vertical-flip parity Run/BlitFrag already correct for): every fullscreen pass flips the
        // image vertically relative to its input. The bloom branch is ALWAYS exactly 3 fullscreen passes removed
        // from Src at this point (bright-pass + blur-H + blur-V, independent of how many main-chain passes ran
        // before it) - an ODD number - so Bloom is always flipped by exactly one flip relative to Src, regardless of
        // which optional main-chain passes (quantize/outline) ran. Un-flip Bloom's V unconditionally (a fixed,
        // settings-independent correction) rather than threading a parity flag through like BlitFrag's Params.z. ----
        public const string BloomCompositeFrag = @"#version 450
layout(set=0, binding=0) uniform texture2D Src;
layout(set=0, binding=1) uniform texture2D Bloom;
layout(set=0, binding=2) uniform sampler Samp;
layout(set=0, binding=3) uniform Composite { vec4 Params; }; // Params.x = intensity
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
void main() {
    vec4 src = texture(sampler2D(Src, Samp), vUv);
    vec3 bloom = texture(sampler2D(Bloom, Samp), vec2(vUv.x, 1.0 - vUv.y)).rgb;
    oColor = vec4(src.rgb + bloom * Params.x, src.a);
}";

        // ---- Ground decal: paint an analytic danger-zone shape onto the surface under each pixel. Reconstructs the
        //      surface world position from the sampled linear depth (DepthTex) via InvViewProj, evaluates the shape
        //      SDF in shape-local space on the XZ plane, gates by a Y-band around the ground height (so it conforms
        //      to terrain but does not climb walls), and blends fill+outline with an fwidth AA edge. Renders into
        //      ColorTex (ColorOnlyFB) before the post chain, with alpha or additive blend. ----
        // BATCHED + FOOTPRINT-BOUNDED: one INSTANCED draw paints N decals of one blend (GroundDecalRenderer coalesces
        // consecutive same-blend decals into runs, preserving submission order so overlapping decals still composite
        // correctly). Each per-decal parameter moves from a per-draw UBO slot to a per-instance vertex ATTRIBUTE,
        // consumed directly (positioning the quad, then passed to the fragment stage) - never used to index a buffer,
        // the Metal-safe instancing invariant (ModelVert's IModel*/ITint are the production proof). Instead of a
        // fullscreen triangle per decal, each instance emits a screen-space QUAD covering the decal's projected ground
        // footprint (IScreenRect, computed CPU-side), at the FAR plane (z=1). The pass renders with the scene
        // depth-stencil bound read-only and a Greater depth test, so a fragment passes only where the stored depth is
        // nearer than the far plane - i.e. only on scene geometry (background at the cleared far plane fails). The
        // bounded quad only shrinks the RASTERIZED area (the fullscreen version discarded every pixel outside the
        // footprint anyway), so the shaded output is identical while fill scales with decal area, not viewport area.
        public const string DecalVert = @"#version 450
layout(location=0) in vec4 IScreenRect;  // per-instance ndc footprint rect (minX, minY, maxX, maxY)
layout(location=1) in vec4 ICenter;      // xyz world center, w = rotation
layout(location=2) in vec4 ISize;
layout(location=3) in vec4 IFill;
layout(location=4) in vec4 IOutline;
layout(location=5) in vec4 IParams;
layout(location=6) in vec4 IGate;
layout(location=7) in vec4 IPattern;   // x=pattern index, y=speed, z=cells per world unit, w=0
layout(location=8) in vec4 IEnergy;    // x=rimGlow, y=sweepGlow, z=sparkle, w=runner
layout(location=9) in vec4 IExtra;     // x=baseFill, yzw reserved 0
layout(location=0) out vec4 vCenter;
layout(location=1) out vec4 vSize;
layout(location=2) out vec4 vFill;
layout(location=3) out vec4 vOutline;
layout(location=4) out vec4 vParams;
layout(location=5) out vec4 vGate;
layout(location=6) out vec4 vPattern;
layout(location=7) out vec4 vEnergy;
layout(location=8) out vec4 vExtra;
void main() {
    // Two-triangle quad (gl_VertexIndex 0..5) spanning the instance's NDC footprint rect. Each per-instance attribute
    // is identical across the quad's six vertices, so the smooth varyings deliver the exact per-instance value to the
    // fragment stage (the same constant-across-the-primitive path ModelVert's per-instance outputs rely on).
    float u = (gl_VertexIndex == 1 || gl_VertexIndex == 3 || gl_VertexIndex == 4) ? 1.0 : 0.0;
    float v = (gl_VertexIndex == 2 || gl_VertexIndex == 4 || gl_VertexIndex == 5) ? 1.0 : 0.0;
    vec2 ndc = mix(IScreenRect.xy, IScreenRect.zw, vec2(u, v));
    gl_Position = vec4(ndc, 1.0, 1.0);   // far plane (z=1): the Greater read-only depth test passes over geometry only
    vCenter = ICenter;
    vSize = ISize;
    vFill = IFill;
    vOutline = IOutline;
    vParams = IParams;
    vGate = IGate;
    vPattern = IPattern;
    vEnergy = IEnergy;
    vExtra = IExtra;
}";

        public const string DecalFrag = @"#version 450
layout(set=0, binding=0) uniform texture2D DepthTex;   // .r = linear depth (single-channel R32F)
layout(set=0, binding=1) uniform sampler Samp;
// ONE uniform buffer per pipeline (Metal via Veldrid/SPIRV-Cross mis-binds a second): the RAW inverse view-projection
// and the time/quality value share this single Frame block, grown from 64 to 80 bytes.
layout(set=0, binding=2) uniform Frame {
    mat4 InvViewProj;   // RAW (un-clip-corrected) inverse view-projection, shared by every decal this frame
    vec4 TimeQ;         // x = effect time seconds, y = quality (1 full / 0 reduced), zw reserved
};
layout(location=0) in vec4 Center;    // xyz world center, w = rotation (radians about +Y)
layout(location=1) in vec4 Size;      // per-shape params (see GroundDecal.Size)
layout(location=2) in vec4 Fill;      // rgb, a = fill alpha (already opacity-scaled)
layout(location=3) in vec4 Outline;   // rgb, a = outline alpha
layout(location=4) in vec4 Params;    // x=edgeThickness, y=fillFraction, z=flashAdd, w=shapeIndex
layout(location=5) in vec4 Gate;      // x=groundY, y=yTolerance, z=maxStep, w=featherWidth (world units)
layout(location=6) in vec4 PatternP;  // x=pattern index, y=speed (cycles/s), z=cells per world unit, w=interiorDim
layout(location=7) in vec4 Energy;    // x=rimGlow, y=sweepGlow, z=sparkle, w=runner
layout(location=8) in vec4 Extra;     // x=baseFill, yzw reserved 0
layout(location=0) out vec4 oColor;

// 2D SDFs in shape-local space (origin at decal center, +x along the decal's facing for oriented shapes).
float sdCircle(vec2 p, float r) { return length(p) - r; }
float sdRing(vec2 p, float ri, float ro) { float d = length(p); return max(ri - d, d - ro); }
float sdBox(vec2 p, vec2 b) { vec2 d = abs(p) - b; return length(max(d, 0.0)) + min(max(d.x, d.y), 0.0); }

// Texture-free value noise for the animated fill patterns + sparkle. hash21 hashes a cell corner to a [0,1) scalar.
// vnoise smoothly interpolates the four corners of the unit cell containing p (Perlin-style smootherstep weights).
float hash21(vec2 p) { p = fract(p * vec2(123.34, 345.45)); p += dot(p, p + 34.345); return fract(p.x * p.y); }
float vnoise(vec2 p)
{
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = hash21(i);
    float b = hash21(i + vec2(1.0, 0.0));
    float c = hash21(i + vec2(0.0, 1.0));
    float d = hash21(i + vec2(1.0, 1.0));
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

void main() {
    // Skip pixels with no scene geometry. The single-channel depth target carries no usable alpha marker
    // (sampling R32F returns a=1 by default), and its .r at the background is just the clear color, so
    // reconstructing from it lands at arbitrary world points that smear the decal across the background. The
    // normal target IS RGBA8 and the model writes its alpha = 1 on geometry (the clear leaves 0), so use it.
    // No-geometry background pixels are already rejected by the hardware depth test (see DecalVert). Reconstruct
    // the surface world position from the linear depth. Sample by integer pixel (texelFetch at gl_FragCoord) and
    // build NDC from gl_FragCoord, NOT from an interpolated UV: render-target texture SAMPLING has a
    // backend-dependent Y origin (Veldrid does not normalize it; the post passes hide this because they sample and
    // write at the same UV so any flip cancels, but a reconstruction does not), whereas gl_FragCoord is upper-left
    // on every backend. Reconstruct with the RAW (un-clip-corrected) inverse view-projection, matching the
    // backend-independent Camera.ScreenToRay picking convention. This keeps the decal identical on Metal/D3D11/Vulkan.
    ivec2 sz = textureSize(sampler2D(DepthTex, Samp), 0);
    float depth = texelFetch(sampler2D(DepthTex, Samp), ivec2(gl_FragCoord.xy), 0).r;
    vec4 ndc = vec4(gl_FragCoord.x / float(sz.x) * 2.0 - 1.0, 1.0 - gl_FragCoord.y / float(sz.y) * 2.0, depth, 1.0);
    vec4 wp = InvViewProj * ndc;
    vec3 world = wp.xyz / wp.w;

    // Y-band gate: only paint surfaces near the ground plane (conform to terrain, not walls).
    float gateLo = Gate.x - Gate.y;
    float gateHi = Gate.x + Gate.z;
    if (world.y < gateLo || world.y > gateHi) discard;

    // Into shape-local XZ (translate by center, rotate by -rotation so +x is the facing axis).
    vec2 q = world.xz - Center.xz;
    float c = cos(-Center.w), s = sin(-Center.w);
    vec2 local = vec2(q.x * c - q.y * s, q.x * s + q.y * c);

    int shape = int(Params.w + 0.5);
    float edge = max(Params.x, 1e-4);
    float fillFrac = clamp(Params.y, 0.0, 1.0);
    float sd;        // signed distance to the shape boundary (negative inside)
    float swept;     // signed distance to the swept (animated) fill boundary

    if (shape == 0) {              // Circle: Size.x = radius
        sd = sdCircle(local, Size.x);
        swept = sdCircle(local, Size.x * fillFrac);
    } else if (shape == 1) {       // Ring: Size.x=innerR, Size.y=outerR
        sd = sdRing(local, Size.x, Size.y);
        swept = sdRing(local, Size.x, Size.x + (Size.y - Size.x) * fillFrac);
    } else if (shape == 2) {       // Beam: Size.x=halfLength, Size.y=halfWidth (origin at one end -> shift by halfLength)
        vec2 b = vec2(Size.x, Size.y);
        vec2 p = local - vec2(Size.x, 0.0);
        sd = sdBox(p, b);
        swept = sdBox(p, vec2(Size.x * fillFrac, Size.y));
    } else if (shape == 3) {       // Cone: Size.x=range, Size.y=halfAngle. Sector via radius + angle test.
        float ang = atan(local.y, local.x);
        float inAng = abs(ang) - Size.y;             // <=0 inside the angular wedge
        float inRad = length(local) - Size.x;        // <=0 inside the range
        sd = max(inRad, inAng);
        swept = max(length(local) - Size.x * fillFrac, inAng);
    } else {                       // Arc: Size.x=radius, Size.y=halfBandWidth, Size.z=startAngle, Size.w=sweep
        float ang = atan(local.y, local.x) - Size.z;
        ang = mod(ang + 6.2831853, 6.2831853);       // 0..2pi from start
        float band = abs(length(local) - Size.x) - Size.y;
        float halfSweep = Size.w * 0.5;
        float inAng = abs(ang - halfSweep) - halfSweep;  // <=0 within [0, sweep]
        sd = max(band, inAng);
        float sweptHalf = (Size.w * fillFrac) * 0.5;
        swept = max(band, abs(ang - sweptHalf) - sweptHalf);
    }

    // Feathered coverage. feather (Gate.w, world units) softens both boundaries. ZERO-NEUTRAL: with feather == 0,
    // smoothstep(-0.0, edge, swept) == smoothstep(0.0, edge, swept) and (edge * 2.0 + 0.0) == edge * 2.0, so these are
    // IEEE-identical to the legacy hard-edge lines - the committed telegraph_ground goldens depend on that.
    float feather = max(Gate.w, 0.0);
    // Fill: inside the swept boundary, AA across one edge width (widened by feather).
    float fillA = (1.0 - smoothstep(-feather, edge + feather, swept)) * Fill.a;
    // Outline: a band straddling the FULL shape boundary. The feather contribution is halved so soft styles do
    // not grow fat borders (feather == 0 keeps the exact legacy band, the zero-neutral contract).
    float outlineA = (1.0 - smoothstep(edge, edge * 2.0 + feather * 0.5, abs(sd))) * Outline.a;

    // Base fill (Extra.x, 0 = legacy, gated so it is zero-neutral): a faint tint across the ENTIRE shape from
    // progress 0, independent of the sweep. This is what lets a borderless (FillMode.Fill) telegraph read its
    // full danger extent immediately, the sweep then brightens across it.
    if (Extra.x > 0.0)
    {
        float baseA = (1.0 - smoothstep(-feather, edge + feather, sd)) * Fill.a * Extra.x;
        fillA = max(fillA, baseA);
    }

    // Animated noise fill. Gated on pattern index > 0 (Solid == 0 leaves fillA untouched, so zero-neutral) and a
    // non-empty fill. Reduced quality (TimeQ.y <= 0.5) drops the second octave.
    float patIdx = PatternP.x;
    if (patIdx > 0.5 && fillA > 0.0)
    {
        float cells = PatternP.z > 0.0 ? PatternP.z : 1.0;
        float t = TimeQ.x * PatternP.y;
        float n;
        if (patIdx < 1.5)
        {
            // ScrollingNoise: domain-warped value noise drifting across the decal-local XZ plane. The warp
            // vector (itself low-frequency noise) bends the drift into wispy filaments instead of round blobs.
            vec2 qp = local * cells;
            vec2 drift = vec2(t, t * 0.7);
            vec2 warp = vec2(vnoise(qp * 0.55 + drift * 0.6),
                             vnoise(qp * 0.55 - drift * 0.4 + 17.3)) - 0.5;
            n = vnoise(qp + warp * 2.6 + drift);
            if (TimeQ.y > 0.5)
                n = 0.62 * n + 0.38 * vnoise(qp * 2.3 + warp * 3.5 - vec2(t * 1.3, -t));
        }
        else
        {
            // RadialNoise: vortex swirl. Rotate the sample domain by an angle growing with radius (spiral
            // arms) and let the arms orbit over time. Sampling stays Cartesian, so there is no polar
            // singularity mushing the shape center (the old radius/angle sampling compressed all angular
            // cells into a blob at r -> 0).
            float rr = length(local);
            float twist = rr * cells * 0.5 - t * 1.6;
            float cs2 = cos(twist), sn2 = sin(twist);
            vec2 sp = vec2(local.x * cs2 - local.y * sn2, local.x * sn2 + local.y * cs2) * cells;
            n = vnoise(sp + vec2(0.0, t * 0.9));
            if (TimeQ.y > 0.5)
                n = 0.65 * n + 0.35 * vnoise(sp * 2.1 - vec2(t * 1.1, t * 0.5));
        }
        // Filament contrast: dark gaps between bright energy wisps, not a milky uniform modulation.
        float filaments = smoothstep(0.35, 0.75, n);
        fillA *= 0.35 + 0.95 * filaments;
    }

    // Hollow interior (PatternP.w = interiorDim, 0 = legacy uniform fill, gated so it is zero-neutral). Alpha
    // eases down deep inside the swept region while staying full within a band of the sweep front, so the
    // energy reads at the rim and the moving edge instead of pooling into a ball at the shape center.
    if (PatternP.w > 0.0 && fillA > 0.0)
    {
        float hollowBand = edge * 3.0 + feather * 2.0;
        float depthIn = clamp(-swept / max(hollowBand, 1e-4), 0.0, 1.0);
        fillA *= 1.0 - PatternP.w * depthIn * depthIn;
    }

    vec3 rgb = Fill.rgb;
    float a = fillA;
    // Composite the outline over the fill.
    rgb = mix(rgb, Outline.rgb, outlineA <= 0.0 ? 0.0 : outlineA / max(outlineA + fillA, 1e-4));
    a = max(a, outlineA);

    // Edge energy. Each term is gated by its own Energy lane, so a zero lane is arithmetically inert and the whole
    // block is a no-op when Energy == 0 (the trailing clamp is an identity on an already-in-range rgb) - zero-neutral.
    if (Energy.x > 0.0)
    {
        // Rim glow: a band straddling the full boundary, tinted toward the outline colour with a slow shimmer.
        float rim = (1.0 - smoothstep(0.0, edge * 1.5 + feather * 0.5, abs(sd))) * Energy.x;
        float shimmer = 0.85 + 0.15 * sin(TimeQ.x * 6.0 + Center.x + Center.z);
        rgb = mix(rgb, Outline.rgb, clamp(rim * 0.6, 0.0, 1.0));
        a = max(a, rim * shimmer * Outline.a * 0.8);
    }
    if (Energy.y > 0.0)
    {
        // Sweep glow: a leading-edge glow tracking the animated (swept) fill boundary. The band is kept to
        // roughly one edge-plus-feather width so an early-cast swept region is never fully engulfed (the
        // resolver additionally ramps the energy in over the first fifth of the cast).
        float lead = 1.0 - smoothstep(0.0, edge * 2.0 + feather, abs(swept));
        rgb += Outline.rgb * (lead * Energy.y * 0.7);
        a = max(a, lead * Energy.y * 0.6 * Fill.a);
    }
    if (Energy.z > 0.0 && TimeQ.y > 0.5)
    {
        // Edge sparkle: brief per-cell twinkles along the boundary (Full quality only). Small cells and a
        // smoothstepped threshold give soft glints rather than hard square flecks.
        float bmask = 1.0 - smoothstep(0.0, edge * 3.0 + feather, abs(sd));
        vec2 cell = floor(local * 11.0);
        float ph = hash21(cell + floor(TimeQ.x * 7.0));
        float tw = smoothstep(0.94, 0.995, ph);
        rgb += vec3(1.0) * (bmask * tw * Energy.z);
        a = max(a, bmask * tw * Energy.z * 0.9);
    }
    if (Energy.w > 0.0)
    {
        // Outline runner: eight soft dash segments orbiting the outline band (rune-ring feel). Angular dashes
        // are shape-agnostic: on radial shapes they orbit, on beams they stride along the length. The band mask
        // keeps them strictly on the boundary, so the shape center is untouched.
        float oband = 1.0 - smoothstep(edge, edge * 2.0 + feather, abs(sd));
        float seg = fract(atan(local.y, local.x) * 1.2732395 + TimeQ.x * 0.45);
        float dash = smoothstep(0.32, 0.42, seg) * (1.0 - smoothstep(0.78, 0.88, seg));
        rgb = mix(rgb, Outline.rgb, clamp(oband * dash * Energy.w * 0.85, 0.0, 1.0));
        a = max(a, oband * dash * Energy.w * Outline.a);
    }
    rgb = clamp(rgb, 0.0, 1.0);

    // Impact flash: brighten toward white. Kept exactly where the legacy shader had it.
    rgb = clamp(rgb + Params.z, 0.0, 1.0);

    if (a <= 0.001) discard;
    // Edge-energy lanes (rim/sweep glow, max-composited) can push a above 1 on float render targets. Legacy
    // decals (all-new lanes zero) already keep a = max(fillA, outlineA) in [0,1], so this clamp is an identity
    // for them and only bites the modern energy path.
    a = clamp(a, 0.0, 1.0);
    oColor = vec4(rgb, a);
}";

        // ---- Procedural sky (gradient + sun disc/halo). A fullscreen-triangle BACKGROUND pass rendered into the lit
        //      colour attachment + read-only scene depth (ColorDepthFB), like the ground-decal pass, but INVERTED: the
        //      triangle sits at the FAR plane (z=1) and the pipeline uses a GreaterEqual read-only depth test, so a
        //      fragment passes ONLY where the stored depth is still the cleared far plane - i.e. background pixels
        //      where no geometry was drawn. Geometry (depth < 1) rejects the sky, so it never overwrites the scene and
        //      never touches the MRT normal/linear-depth attachments (ColorDepthFB binds only colour + depth). It
        //      writes alpha = 1 so the blit's starfield "a < 0.5 == background" marker does not fire over sky pixels.
        //      No vertex inputs (gl_VertexIndex only), so the HLSL input signature is empty - no gap-free-holes hazard.
        //      The sky is drawn in SCREEN space (not by a world view ray): under the orthographic iso camera every
        //      view ray is parallel, so a world-ray sky would be a flat colour with no gradient and no localized sun.
        //      A vertical screen gradient + a sun disc placed at the CPU-projected screen position of the sun reads
        //      correctly under both the ortho iso camera and the perspective follow camera. ----
        public const string SkyVert = @"#version 450
void main() {
    vec2 p = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
    gl_Position = vec4(p * 2.0 - 1.0, 1.0, 1.0);   // far plane (z=1): passes the Equal depth test only on background
}";

        // SkyFrag mirrors SkyMath.Shade EXACTLY (keep in sync, like EdgeFrag mirrors OutlineMath). Screen-space: the
        // gradient is a vertical ramp over NDC.y and the sun disc/halo is a screen-space distance to the sun's
        // projected NDC position (SunNdc, computed on the CPU by SkyMath.ProjectSunToNdc so a DIRECTIONAL light with
        // no position still lands on-screen). NDC is rebuilt from gl_FragCoord (upper-left on EVERY backend) + the
        // render size (Res.xy = 1/width,1/height) - the same backend-independent convention DecalFrag uses, NOT an
        // interpolated vertex NDC (which would flip on Vulkan against the geometry). The single UBO holds the colours
        // + projected-sun params + render size; one uniform buffer per set (Metal mis-binds a second). No texture.
        public const string SkyFrag = @"#version 450
layout(set=0, binding=0) uniform Sky {
    vec4 Horizon;     // rgb gradient at the horizon (bottom)
    vec4 Zenith;      // rgb gradient at the zenith (top)
    vec4 SunColor;    // rgb sun disc + halo colour
    vec4 SunNdc;      // xy = sun screen NDC, z = sunVisible (1/0), w = aspect (width/height)
    vec4 Params;      // x=sunEnabled, y=sunRadius, z=haloStrength, w=haloFalloff
    vec4 Res;         // xy = 1/renderWidth, 1/renderHeight
};
layout(location=0) out vec4 oColor;
void main() {
    // NDC from gl_FragCoord (upper-left origin on every backend): x in [-1,1] rightward, y in [-1,1] UPWARD.
    vec2 ndc = vec2(gl_FragCoord.x * Res.x * 2.0 - 1.0, 1.0 - gl_FragCoord.y * Res.y * 2.0);
    // Vertical screen gradient: NDC.y in [-1,1] -> [0,1] (bottom -> top), smoothstep for a soft ramp.
    float up = clamp(ndc.y * 0.5 + 0.5, 0.0, 1.0);
    float t = smoothstep(0.0, 1.0, up);
    vec3 col = mix(Horizon.rgb, Zenith.rgb, t);

    if (Params.x > 0.5 && SunNdc.z > 0.5) {
        float sunRadius = Params.y, haloStrength = Params.z, haloFalloff = Params.w, aspect = SunNdc.w;
        float dx = (ndc.x - SunNdc.x) * aspect;   // aspect-correct so the disc is round in pixels
        float dy = ndc.y - SunNdc.y;
        float d = sqrt(dx * dx + dy * dy);
        float feather = max(haloFalloff * 0.25, 1e-4);
        float disc = 1.0 - smoothstep(sunRadius, sunRadius + feather, d);
        float halo = 0.0;
        if (haloStrength > 0.0 && haloFalloff > 0.0) {
            float beyond = max(0.0, d - sunRadius);
            halo = haloStrength * exp(-beyond / haloFalloff);
        }
        float sun = clamp(disc + halo, 0.0, 1.0);
        col = mix(col, SunColor.rgb, sun);
    }
    oColor = vec4(col, 1.0);   // alpha 1: NOT the starfield/transparent background marker
}";

        // ---- Animated water surface (Rendering gap #5). Drawn AFTER the sky and the ground decals into
        //      ColorDepthFB (lit colour + read-only scene depth), a CPU-tessellated flat grid (WaterMath.GridResolution)
        //      at the plane's world height. Depth test ON (Less, standard, so terrain/props above the surface occlude
        //      it, matching the textured-billboard/beam depth-interleave convention) but depth WRITE OFF: the outline
        //      pass reads the resolved normal/linear-depth MRT (ColorTex's siblings), and those are captured by the
        //      OPAQUE model pass alone (see RenderResources.ResolveDepth/ResolveColorNormal, which run BEFORE this
        //      pass in Scene3D.RenderInternal) - a water depth WRITE would need its own MRT write to keep that
        //      buffer meaningful, which reflections/probes (out of scope, roadmap #9) would want but this LDR pass
        //      does not attempt. No-write keeps the edge outline tracing the solid geometry's silhouette (a
        //      shore-line water edge is desirable per the brief; a corrupted normal/depth buffer that broke the
        //      outline pass for EVERYTHING behind the water is not). Two textures bound: the resolved scene depth
        //      (shore fade, decal-style gl_FragCoord reconstruction) and nothing else - no second material texture,
        //      so the Metal up-front-sample-order landmine does not apply here (only one texture total). Vertex
        //      inputs are Position only (no gap-free-signature hazard: everything declared is read). One UBO
        //      (fragment-only; the vertex only needs ViewProj, folded into the SAME buffer per the one-UBO-per-set
        //      rule, read by both stages). ----
        public const string WaterVert = @"#version 450
layout(set=0, binding=2) uniform Water {
    mat4 ViewProj;
    mat4 InvViewProj;   // RAW (not clip-corrected) inverse, for the fragment's depth reconstruction
    vec4 LightDir;      // xyz = key light travel direction
    vec4 LightColor;
    vec4 CameraPos;     // xyz = eye position
    vec4 DeepColor;     // rgb + alpha
    vec4 HorizonColor;  // rgb + alpha
    vec4 WaveParams;    // x=waveScale, y=waveSpeed, z=normalStrength, w=time
    vec4 ShoreGlint;    // x=shoreFadeDistance, y=glintStrength, z=glintExponent, w=opacity
    vec4 Res;           // xy = 1/renderWidth, 1/renderHeight
};
layout(location=0) in vec3 Position;
layout(location=0) out vec3 vWorldPos;
void main() {
    gl_Position = ViewProj * vec4(Position, 1.0);
    vWorldPos = Position;
}";

        public const string WaterFrag = @"#version 450
layout(set=0, binding=0) uniform texture2D DepthTex;   // .r = resolved scene linear depth (single-channel R32F)
layout(set=0, binding=1) uniform sampler Samp;
layout(set=0, binding=2) uniform Water {
    mat4 ViewProj;
    mat4 InvViewProj;
    vec4 LightDir;
    vec4 LightColor;
    vec4 CameraPos;
    vec4 DeepColor;
    vec4 HorizonColor;
    vec4 WaveParams;    // x=waveScale, y=waveSpeed, z=normalStrength, w=time
    vec4 ShoreGlint;    // x=shoreFadeDistance, y=glintStrength, z=glintExponent, w=opacity
    vec4 Res;
};
layout(location=0) in vec3 vWorldPos;
layout(location=0) out vec4 oColor;

// Mirrors WaterMath.WaveNormal exactly: two scrolling sine octaves, analytic slope -> tilted flat-up normal.
vec3 waterNormal(vec2 xz, float time, float waveScale, float waveSpeed, float normalStrength) {
    float invScale = 1.0 / max(waveScale, 1e-4);
    float t = time * waveSpeed;
    float p1x = xz.x * invScale + t;
    float p1z = xz.y * invScale + t * 0.7;
    float p2x = (xz.x - xz.y) * invScale * 2.0 - t * 1.3;
    float p2z = (xz.x + xz.y) * invScale * 2.0 + t * 0.9;
    float dHdx = cos(p1x) * invScale + cos(p2x) * invScale * 2.0 * 0.5;
    float dHdz = cos(p1z) * invScale + cos(p2z) * invScale * 2.0 * 0.5;
    vec3 n = vec3(-dHdx * normalStrength, 1.0, -dHdz * normalStrength);
    float len = length(n);
    return len > 1e-8 ? n / len : vec3(0.0, 1.0, 0.0);
}

void main() {
    float waveScale = WaveParams.x, waveSpeed = WaveParams.y, normalStrength = WaveParams.z, time = WaveParams.w;
    vec3 N = waterNormal(vWorldPos.xz, time, waveScale, waveSpeed, normalStrength);

    vec3 V = normalize(CameraPos.xyz - vWorldPos);
    float ndotv = clamp(dot(N, V), 0.0, 1.0);
    // Schlick-style fresnel: (1-ndotv)^5, mirrors WaterMath.Fresnel.
    float fx = clamp(1.0 - ndotv, 0.0, 1.0);
    float fresnel = fx * fx * fx * fx * fx;
    vec3 tint = mix(DeepColor.rgb, HorizonColor.rgb, fresnel);
    float tintAlpha = mix(DeepColor.a, HorizonColor.a, fresnel);

    // Key-light specular sun glint: small water-specific Blinn-Phong term (mirrors WaterMath.SunGlint), NOT routed
    // through the shared computeLighting block (water needs its own tight strength/exponent, distinct from any
    // mesh material).
    float glintStrength = ShoreGlint.y, glintExponent = ShoreGlint.z;
    vec3 Lsun = -normalize(LightDir.xyz);
    vec3 H = V + Lsun;
    float hLen = length(H);
    float glint = 0.0;
    if (glintStrength > 0.0 && hLen > 1e-8) {
        H /= hLen;
        float ndoth = max(dot(N, H), 0.0);
        glint = pow(ndoth, max(glintExponent, 1.0)) * glintStrength;
    }

    // Shore fade: reconstruct the ground surface under this pixel from the resolved scene depth (the ground-decal
    // pass's gl_FragCoord + raw-inverse-view-projection convention - backend-independent, unlike an interpolated
    // UV, because render-target texture SAMPLING has a backend-dependent Y origin while gl_FragCoord is upper-left
    // on every backend). depthBelowSurface = this water fragment's own world Y minus the ground's world Y (positive
    // when the ground sits below the surface, as it must for this fragment to have passed the water pass's OWN
    // depth test in the first place).
    ivec2 sz = textureSize(sampler2D(DepthTex, Samp), 0);
    float groundDepth = texelFetch(sampler2D(DepthTex, Samp), ivec2(gl_FragCoord.xy), 0).r;
    vec4 ndc = vec4(gl_FragCoord.x / float(sz.x) * 2.0 - 1.0, 1.0 - gl_FragCoord.y / float(sz.y) * 2.0, groundDepth, 1.0);
    vec4 wp = InvViewProj * ndc;
    vec3 groundWorld = wp.xyz / wp.w;
    float depthBelowSurface = vWorldPos.y - groundWorld.y;
    float shoreFadeDist = ShoreGlint.x;
    float shoreFade = shoreFadeDist <= 0.0 ? 1.0 : smoothstep(0.0, 1.0, clamp(depthBelowSurface / shoreFadeDist, 0.0, 1.0));

    float opacity = ShoreGlint.w;
    vec3 rgb = tint + LightColor.rgb * glint;
    float alpha = tintAlpha * opacity * shoreFade;
    if (alpha <= 0.001) discard;
    oColor = vec4(rgb, alpha);
}";
    }
}
