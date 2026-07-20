namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// The splat-mapped terrain pass (2 of the renderer's shader sources).
    /// Part of the <see cref="ShaderSources"/> partial: see ShaderSources.cs for the shared contract
    /// (GLSL #version 450, cross-compiled at load via the GPU seam's SPIR-V path).
    /// </summary>
    internal static partial class ShaderSources
    {

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
    vec4 ShadowParams2;    // x=texelStep(1/perCascadeRes), y=maxDistance, z=borderFrac, w=cascadeBlendFrac
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
    vec4 ShadowParams2;    // x=texelStep(1/perCascadeRes), y=maxDistance, z=borderFrac, w=cascadeBlendFrac
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
    }
}
