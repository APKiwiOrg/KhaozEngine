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
};
layout(set=0, binding=1) uniform texture2D Albedo;       // 1x1 white default keeps untextured meshes unchanged
layout(set=0, binding=2) uniform texture2D NormalMap;    // 1x1 flat default: texel (0.5,0.5,1.0) decodes to tangent-space (0,0,1); sampled up front, applied only when a tangent exists
layout(set=0, binding=3) uniform texture2D RoughnessMap; // 1x1 zero default => spec uses per-instance params
layout(set=0, binding=4) uniform sampler Samp;           // shared sampler for all three textures (EdgeFrag-style)
layout(location=0) in vec3 vNormalW;
layout(location=1) in vec4 vColor;
layout(location=2) in float vDepth;
layout(location=3) in vec3 vWorldPos;
layout(location=4) in vec2 vUv;
layout(location=5) in vec4 vTint;
layout(location=6) in vec4 vEmissive;
layout(location=7) in vec4 vSpecParams; // x = specular strength, y = shininess exponent
layout(location=8) in vec4 vTangent;    // world-space tangent (xyz) + handedness (w); zero => geometric normal
layout(location=0) out vec4 oColor;
layout(location=1) out vec4 oNormal;
layout(location=2) out vec4 oDepth;
void main() {
    vec3 Ngeo = normalize(vNormalW);
    // Sample ALL material maps up front, unconditionally, in binding order (Albedo, NormalMap, RoughnessMap).
    // This ordering is load-bearing on Metal: SPIRV-Cross assigns MSL texture indices in the order textures are
    // first sampled, so sampling a higher-binding map first (e.g. the normal map inside the TBN branch) made the
    // albedo sampler read the normal map - untextured meshes came out flat-normal coloured (R,G ~0.5). Sampling
    // binding 0 (Albedo) first and unconditionally keeps the indices matching the resource layout. (D3D11/Vulkan
    // bind by explicit decoration and are order-insensitive; this is purely the Metal path.) Mirrors EdgeFrag.
    vec3 texRgb = texture(sampler2D(Albedo, Samp), vUv).rgb;       // white (1,1,1) for untextured meshes
    vec3 normalTex = texture(sampler2D(NormalMap, Samp), vUv).xyz; // flat (0.5,0.5,1.0) default => (0,0,1)
    float rough = texture(sampler2D(RoughnessMap, Samp), vUv).g;   // 0 default => per-instance spec unchanged
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
    float ndlKey  = max(dot(N, -normalize(LightDir.xyz)), 0.0);
    float ndlFill = max(dot(N, -normalize(FillDir.xyz)), 0.0);
    float bands = Params.x;
    if (bands >= 1.0) { ndlKey = floor(ndlKey*bands+0.5)/bands; ndlFill = floor(ndlFill*bands+0.5)/bands; }
    vec3 diffuse = LightColor.rgb*ndlKey + FillColor.rgb*ndlFill;
    vec3 V = normalize(CameraPos.xyz - vWorldPos);
    vec3 H = normalize(-normalize(LightDir.xyz) + V);
    float spec = pow(max(dot(N,H),0.0), specExp) * specStrength * step(0.0001, ndlKey);
    vec3 specColor = LightColor.rgb*spec;
    // Dynamic point/effect lights (muzzle flashes, explosions, thrusters): accumulate diffuse (+ cheap
    // specular) with a windowed distance attenuation, on top of the key+fill term and back-face gated by
    // max(dot(N,L),0). Params.y is the host-capped active count; zero leaves diffuse/specColor untouched,
    // so the lit term stays bit-identical to the key+fill+ambient path.
    int npl = int(Params.y);
    for (int i = 0; i < npl; i++) {
        vec3 toL = PointPosRadius[i].xyz - vWorldPos;
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
    vec3 lit = albedo * (Ambient.rgb + diffuse) + specColor + vEmissive.rgb;
    oColor = vec4(lit, 1.0);
    oNormal = vec4(Ngeo * 0.5 + 0.5, 1.0); // GEOMETRIC normal for the edge pass (not the perturbed one)
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
    vec4 TintTiling[5];   // per-material params appended (offset 688): xyz = tint, w = tiles/metre
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
        //      the SAME key+fill+ambient+point-light+cel model as ModelFrag. Writes the same 3 MRT targets (geometric
        //      normal to attachment 1 for the edge pass). KEEP THE LIGHTING IN SYNC WITH ModelFrag. Sample the two
        //      arrays in binding order (Albedo then Normal) - the Metal SPIRV-Cross first-sample-order constraint. ----
        public const string SplatFrag = @"#version 450
layout(set=0, binding=0) uniform U {
    mat4 ViewProj;
    vec4 LightDir; vec4 LightColor; vec4 Ambient; vec4 Params;
    vec4 FillDir; vec4 FillColor; vec4 CameraPos;
    vec4 PointPosRadius[16];
    vec4 PointColorIntensity[16];
    vec4 TintTiling[5];   // xyz = tint, w = tiles/metre
    vec4 Roughness;       // x..w = roughness for layers 0..3
    vec4 Misc;            // x = layer4 roughness, y = triplanarSharpness, z = projectionMode, w = baseSpecStrength
};
layout(set=0, binding=1) uniform texture2DArray AlbedoArray;
layout(set=0, binding=2) uniform texture2DArray NormalArray;
layout(set=0, binding=3) uniform sampler Samp;
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

vec3 sampleAlbedo(int layer, vec2 uvx, vec2 uvy, vec2 uvz, vec3 bw) {
    vec3 ax = texture(sampler2DArray(AlbedoArray, Samp), vec3(uvx, float(layer))).rgb;
    vec3 ay = texture(sampler2DArray(AlbedoArray, Samp), vec3(uvy, float(layer))).rgb;
    vec3 az = texture(sampler2DArray(AlbedoArray, Samp), vec3(uvz, float(layer))).rgb;
    return ax*bw.x + ay*bw.y + az*bw.z;
}

// Whiteout triplanar normal blend (reorient each plane's tangent-space normal into world space, no vertex tangent).
vec3 sampleNormal(int layer, vec2 uvx, vec2 uvy, vec2 uvz, vec3 bw, vec3 Ngeo) {
    vec3 nx = texture(sampler2DArray(NormalArray, Samp), vec3(uvx, float(layer))).xyz * 2.0 - 1.0;
    vec3 ny = texture(sampler2DArray(NormalArray, Samp), vec3(uvy, float(layer))).xyz * 2.0 - 1.0;
    vec3 nz = texture(sampler2DArray(NormalArray, Samp), vec3(uvz, float(layer))).xyz * 2.0 - 1.0;
    nx = vec3(nx.xy + Ngeo.zy, abs(nx.z) * Ngeo.x);
    ny = vec3(ny.xy + Ngeo.xz, abs(ny.z) * Ngeo.y);
    nz = vec3(nz.xy + Ngeo.xy, abs(nz.z) * Ngeo.z);
    return normalize(nx.zyx * bw.x + ny.xzy * bw.y + nz.xyz * bw.z);
}

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
        albedo += wl * sampleAlbedo(L, uvx, uvy, uvz, bw) * TintTiling[L].xyz;
        Nsum   += wl * sampleNormal(L, uvx, uvy, uvz, bw, Ngeo);
        rough  += wl * rgh[L];
    }
    albedo *= vTint.rgb;
    vec3 N = (dot(Nsum, Nsum) > 1e-8) ? normalize(Nsum) : Ngeo;

    // Lighting: mirror ModelFrag. Base specular from Misc.w, modulated by the blended roughness.
    float specStrength = Misc.w * (1.0 - rough);
    float specExp = max(mix(48.0, 8.0, rough), 1.0);
    float ndlKey  = max(dot(N, -normalize(LightDir.xyz)), 0.0);
    float ndlFill = max(dot(N, -normalize(FillDir.xyz)), 0.0);
    float bands = Params.x;
    if (bands >= 1.0) { ndlKey = floor(ndlKey*bands+0.5)/bands; ndlFill = floor(ndlFill*bands+0.5)/bands; }
    vec3 diffuse = LightColor.rgb*ndlKey + FillColor.rgb*ndlFill;
    vec3 V = normalize(CameraPos.xyz - vWorldPos);
    vec3 H = normalize(-normalize(LightDir.xyz) + V);
    float spec = pow(max(dot(N,H),0.0), specExp) * specStrength * step(0.0001, ndlKey);
    vec3 specColor = LightColor.rgb*spec;
    int npl = int(Params.y);
    for (int i = 0; i < npl; i++) {
        vec3 toL = PointPosRadius[i].xyz - vWorldPos;
        float radius = PointPosRadius[i].w;
        float dist = length(toL);
        vec3 L = (dist > 1e-4) ? toL / dist : vec3(0.0);
        float ndl = max(dot(N, L), 0.0);
        if (bands >= 1.0) ndl = floor(ndl*bands+0.5)/bands;
        float f = clamp(1.0 - (dist*dist)/max(radius*radius, 1e-6), 0.0, 1.0);
        float att = f * f * PointColorIntensity[i].w;
        vec3 lc = PointColorIntensity[i].rgb;
        diffuse += lc * (ndl * att);
        vec3 Hp = normalize(L + V);
        float sp = pow(max(dot(N,Hp),0.0), specExp) * specStrength * step(0.0001, ndl);
        specColor += lc * (sp * att);
    }
    vec3 lit = albedo * (Ambient.rgb + diffuse) + specColor + vEmissive.rgb;
    oColor = vec4(lit, 1.0);
    oNormal = vec4(Ngeo * 0.5 + 0.5, 1.0); // GEOMETRIC normal for the edge pass
    oDepth = vec4(vDepth, vDepth, vDepth, 1.0);
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

        // ---- Ground decal: paint an analytic danger-zone shape onto the surface under each pixel. Reconstructs the
        //      surface world position from the sampled linear depth (DepthTex) via InvViewProj, evaluates the shape
        //      SDF in shape-local space on the XZ plane, gates by a Y-band around the ground height (so it conforms
        //      to terrain but does not climb walls), and blends fill+outline with an fwidth AA edge. One draw per
        //      decal (per-decal UBO). Renders into ColorTex (ColorOnlyFB) before the post chain, with alpha or
        //      additive blend. ----
        // Fullscreen triangle at the FAR plane (z=1). The ground-decal pass renders this with the scene
        // depth-stencil bound read-only and a Greater depth test, so a fragment passes only where the stored
        // depth is nearer than the far plane - i.e. only where scene geometry was drawn. Background pixels
        // (cleared to the far plane) fail the test and are never shaded, independent of the background color.
        public const string DecalVert = @"#version 450
layout(location=0) out vec2 vUv;
void main() {
    vec2 p = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
    vUv = p;
    gl_Position = vec4(p * 2.0 - 1.0, 1.0, 1.0);
}";

        public const string DecalFrag = @"#version 450
layout(set=0, binding=0) uniform texture2D DepthTex;   // .r = linear depth (single-channel R32F)
layout(set=0, binding=1) uniform sampler Samp;
layout(set=0, binding=2) uniform Decal {
    mat4 InvViewProj;
    vec4 Center;    // xyz world center, w = rotation (radians about +Y)
    vec4 Size;      // per-shape params (see GroundDecal.Size)
    vec4 Fill;      // rgb, a = fill alpha (already opacity-scaled)
    vec4 Outline;   // rgb, a = outline alpha
    vec4 Params;    // x=edgeThickness, y=fillFraction, z=flashAdd, w=shapeIndex
    vec4 Gate;      // x=groundY, y=yTolerance, z=maxStep, w=unused
};
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;

// 2D SDFs in shape-local space (origin at decal center, +x along the decal's facing for oriented shapes).
float sdCircle(vec2 p, float r) { return length(p) - r; }
float sdRing(vec2 p, float ri, float ro) { float d = length(p); return max(ri - d, d - ro); }
float sdBox(vec2 p, vec2 b) { vec2 d = abs(p) - b; return length(max(d, 0.0)) + min(max(d.x, d.y), 0.0); }

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

    // Fill: inside the swept boundary, AA across one edge width.
    float fillA = (1.0 - smoothstep(0.0, edge, swept)) * Fill.a;
    // Outline: a band straddling the FULL shape boundary.
    float outlineA = (1.0 - smoothstep(edge, edge * 2.0, abs(sd))) * Outline.a;

    vec3 rgb = Fill.rgb;
    float a = fillA;
    // Composite the outline over the fill.
    rgb = mix(rgb, Outline.rgb, outlineA <= 0.0 ? 0.0 : outlineA / max(outlineA + fillA, 1e-4));
    a = max(a, outlineA);
    // Impact flash: brighten toward white.
    rgb = clamp(rgb + Params.z, 0.0, 1.0);

    if (a <= 0.001) discard;
    oColor = vec4(rgb, a);
}";
    }
}
