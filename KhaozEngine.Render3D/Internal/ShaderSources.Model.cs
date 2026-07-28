namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// Opaque model and skinned-model passes, including their dissolve variants (6 of the renderer's shader sources).
    /// Part of the <see cref="ShaderSources"/> partial: see ShaderSources.cs for the shared contract
    /// (GLSL #version 450, cross-compiled at load via the GPU seam's SPIR-V path).
    /// </summary>
    internal static partial class ShaderSources
    {

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
    vec4 ShadowParams2;    // x=texelStep(1/perCascadeRes), y=maxDistance, z=borderFrac, w=cascadeBlendFrac
    vec4 ShadowNormalOffsets; // per-cascade normal-offset world size (texelWorld_i * ShadowNormalOffset): x=c0..w=c3
    vec4 RenderOrigin;     // camera-relative rendering: add to a render-frame position for the ABSOLUTE world one
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
layout(location=12) in float IDynamic;   // dynamic-geometry decal mask (0 static world / 1 skinned); see InstanceData
layout(location=13) in vec2 IDissolve;   // per-instance rigid dissolve (issue #253): x = threshold, y = edge width
layout(location=0) out vec3 vNormalW;
layout(location=1) out vec4 vColor;
layout(location=2) out float vDepth;
layout(location=3) out vec3 vWorldPos;
layout(location=4) out vec2 vUv;
layout(location=5) out vec4 vTint;
layout(location=6) out vec4 vEmissive;
layout(location=7) out vec4 vSpecParams;
layout(location=8) out vec4 vTangent;
layout(location=9) out float vDynamic;
layout(location=10) out vec2 vDissolve;
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
    vDynamic = IDynamic;
    vDissolve = IDissolve;
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
    vec4 ShadowParams2;    // x=texelStep(1/perCascadeRes), y=maxDistance, z=borderFrac, w=cascadeBlendFrac
    vec4 ShadowNormalOffsets; // per-cascade normal-offset world size (texelWorld_i * ShadowNormalOffset): x=c0..w=c3
    vec4 RenderOrigin;     // camera-relative rendering: add to a render-frame position for the ABSOLUTE world one
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
layout(location=9) in float vDynamic;   // dynamic-geometry decal mask (0 static / 1 skinned); written to oNormal.a
layout(location=10) in vec2 vDissolve;  // per-instance rigid dissolve (issue #253): x = threshold (0 = solid .. 1 = gone), y = edge width
layout(location=0) out vec4 oColor;
layout(location=1) out vec4 oNormal;
layout(location=2) out vec4 oDepth;
" + LightingCommonGlsl + @"
// World-space value noise for the per-instance dissolve mask (issue #253). The SAME hash/noise + scale as
// ModelDissolveFrag (the skinned CharDissolve path), so a prop and a character dissolve with one visual language.
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
    vec3 lit = albedo * (Ambient.rgb + diffuse) + specColor;
    // Per-instance rigid dissolve (issue #253), gated with an if (NOT a multiply) so a draw carrying no dissolve is
    // byte-identical to the pre-dissolve path: the else branch is exactly `lit + vEmissive.rgb`, the old expression.
    // When dissolving, vEmissive carries the emissive EDGE colour (substituted engine-side in the Draw overload), so
    // the base emissive is dropped and only a bright band just above the discard threshold is added - the same trade
    // ModelDissolveFrag makes. World-space noise so the pattern is stable as instances move.
    if (vDissolve.x > 0.0) {
        float threshold = clamp(vDissolve.x, 0.0, 1.0);
        float edgeW = max(vDissolve.y, 1e-3);
        float mask = dnoise((vWorldPos + RenderOrigin.xyz) * " + ShadowDissolveNoise.BaseScaleGlsl + @");
        if (mask < threshold) discard;          // dissolved away
        float edge = 1.0 - smoothstep(threshold, threshold + edgeW, mask);
        lit += vEmissive.rgb * edge;
    } else {
        lit += vEmissive.rgb;                   // base emissive, unchanged old path
    }
    oColor = vec4(lit, 1.0);
    // rgb: GEOMETRIC normal for the edge pass (not the perturbed one). a: dynamic-geometry decal mask - 1 for the
    // static world (unchanged), 0 for skinned/dynamic geometry so the main ground-decal pass rejects it (issue #235).
    oNormal = vec4(Ngeo * 0.5 + 0.5, 1.0 - clamp(vDynamic, 0.0, 1.0));
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
    vec4 ShadowParams2;        // x=texelStep(1/perCascadeRes), y=maxDistance, z=borderFrac, w=cascadeBlendFrac
    vec4 ShadowNormalOffsets;  // per-cascade normal-offset world size (x=c0..w=c3)
    vec4 RenderOrigin;     // camera-relative rendering: add to a render-frame position for the ABSOLUTE world one
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
layout(location=9) in float vDynamic;   // dynamic-geometry decal mask (0 static / 1 skinned); written to oNormal.a
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
    float mask = dnoise((vWorldPos + RenderOrigin.xyz) * " + ShadowDissolveNoise.BaseScaleGlsl + @");   // 0..1 world-space dissolve mask
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
    oNormal = vec4(Ngeo * 0.5 + 0.5, 1.0 - clamp(vDynamic, 0.0, 1.0)); // a: dynamic-geometry decal mask (issue #235)
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
    vec4 ShadowParams2;        // x=texelStep(1/perCascadeRes), y=maxDistance, z=borderFrac, w=cascadeBlendFrac
    vec4 ShadowNormalOffsets;  // per-cascade normal-offset world size (x=c0..w=c3)
    vec4 RenderOrigin;     // camera-relative rendering: add to a render-frame position for the ABSOLUTE world one
    mat4 bones[128];       // offset 1200: this draw's composed palette (inverseBind*jointWorld), padded/validated to <=128
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
layout(location=9) out float vDynamic;
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
    vDynamic = P[3].x;   // dynamic-geometry decal mask (issue #235): GPU-skinned draws default to 1 (see PackSkinnedMainSlot)
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
    vec4 ShadowParams2;        // x=texelStep(1/perCascadeRes), y=maxDistance, z=borderFrac, w=cascadeBlendFrac
    vec4 ShadowNormalOffsets;  // per-cascade normal-offset world size (x=c0..w=c3)
    vec4 RenderOrigin;     // camera-relative rendering: add to a render-frame position for the ABSOLUTE world one
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
layout(location=9) in float vDynamic;   // dynamic-geometry decal mask (0 static / 1 skinned); written to oNormal.a
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
    oNormal = vec4(Ngeo * 0.5 + 0.5, 1.0 - clamp(vDynamic, 0.0, 1.0)); // a: dynamic-geometry decal mask (issue #235)
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
    vec4 ShadowParams2;        // x=texelStep(1/perCascadeRes), y=maxDistance, z=borderFrac, w=cascadeBlendFrac
    vec4 ShadowNormalOffsets;  // per-cascade normal-offset world size (x=c0..w=c3)
    vec4 RenderOrigin;     // camera-relative rendering: add to a render-frame position for the ABSOLUTE world one
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
layout(location=9) in float vDynamic;   // dynamic-geometry decal mask (0 static / 1 skinned); written to oNormal.a
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
    float mask = dnoise((vWorldPos + RenderOrigin.xyz) * " + ShadowDissolveNoise.BaseScaleGlsl + @");
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
    oNormal = vec4(Ngeo * 0.5 + 0.5, 1.0 - clamp(vDynamic, 0.0, 1.0)); // a: dynamic-geometry decal mask (issue #235)
    oDepth = vec4(vDepth, vDepth, vDepth, 1.0);
}";
    }
}
