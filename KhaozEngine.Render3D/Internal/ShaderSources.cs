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

        // ---- Skinned model vertex shader. Same per-frame UBO (set 0, binding 0) and same per-instance stream as
        //      ModelVert, plus per-vertex bone indices + weights AND a tangent (location 6, mirroring ModelVert's
        //      per-vertex tangent). The bone palette is a DYNAMIC-OFFSET uniform buffer (set 1, binding 0): each
        //      skinned draw rebinds it with a per-draw byte offset, so the shader reads bones[0..N] for THIS draw
        //      without any per-instance index. (A per-instance bone-offset attribute into a single shared buffer
        //      mis-fetched for every draw past the first on the Metal/Veldrid backend; dynamic-offset rebasing avoids
        //      it.) The per-instance stream shifts from locations 6..12 to 7..13 to make room for the tangent.
        //      Outputs match ModelVert (locations 0..8) so the shared ModelFrag links from either shader. The tangent
        //      rides the skin+model rotation like the normal and keeps its handedness w; a zero tangent (untangented
        //      skinned mesh) carries through as zero, so ModelFrag falls back to the geometric normal
        //      (bit-identical to the pre-PBR pass). NB Scene3D draws skinned meshes via CPU skinning through the rigid
        //      ModelRenderer pipeline (the GPU bone read corrupts past element 0 in the windowed Veldrid/Metal
        //      context); this shader is the revivable GPU-skinning reference and stays in sync with SkinningMath. ----
        public const string SkinnedModelVert = @"#version 450
layout(set=0, binding=0) uniform U {
    mat4 ViewProj;
    vec4 LightDir; vec4 LightColor; vec4 Ambient; vec4 Params;
    vec4 FillDir; vec4 FillColor; vec4 CameraPos;
    vec4 PointPosRadius[16];
    vec4 PointColorIntensity[16];
};
layout(set=1, binding=0) uniform Bones { mat4 bones[128]; }; // per-draw dynamic-offset window (see MaxBonesPerDraw)
layout(location=0) in vec3 Position;
layout(location=1) in vec3 Normal;
layout(location=2) in vec4 Color;
layout(location=3) in vec2 TexCoord;
layout(location=4) in vec4 BoneIndices;  // up to 4 bone indices, float-encoded
layout(location=5) in vec4 BoneWeights;  // 4 weights, normalized at load
layout(location=6) in vec4 Tangent;      // model-space tangent (xyz) + handedness (w); zero => no TBN
layout(location=7)  in vec4 IModel0;     // per-instance model matrix rows
layout(location=8)  in vec4 IModel1;
layout(location=9)  in vec4 IModel2;
layout(location=10) in vec4 IModel3;
layout(location=11) in vec4 ITint;
layout(location=12) in vec4 IEmissive;
layout(location=13) in vec4 ISpecParams;
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
    float total = BoneWeights.x + BoneWeights.y + BoneWeights.z + BoneWeights.w;
    mat4 skin;
    if (total < 1e-6) {
        skin = mat4(1.0);
    } else {
        skin = BoneWeights.x * bones[int(BoneIndices.x)]
             + BoneWeights.y * bones[int(BoneIndices.y)]
             + BoneWeights.z * bones[int(BoneIndices.z)]
             + BoneWeights.w * bones[int(BoneIndices.w)];
    }
    mat4 Model = mat4(IModel0, IModel1, IModel2, IModel3);
    vec4 local = skin * vec4(Position, 1.0);
    vec4 world = Model * local;
    gl_Position = ViewProj * world;
    mat3 deform = mat3(Model) * mat3(skin); // rotate normal + tangent through skin then model
    vNormalW = normalize(deform * Normal);
    vColor = Color;
    vDepth = gl_Position.z / gl_Position.w;
    vWorldPos = world.xyz;
    vUv = TexCoord;
    vTint = ITint;
    vEmissive = IEmissive;
    vSpecParams = ISpecParams;
    // Carry the tangent through the same deform; preserve handedness. Zero tangent stays zero (ModelFrag then
    // lights with the geometric normal), matching SkinningMath.SkinVertex (the CPU path Scene3D actually uses).
    vTangent = (dot(Tangent.xyz, Tangent.xyz) > 1e-10)
        ? vec4(deform * Tangent.xyz, Tangent.w)
        : vec4(0.0);
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
        public const string EdgeFrag = @"#version 450
layout(set=0, binding=0) uniform texture2D ColorTex;
layout(set=0, binding=1) uniform texture2D NormalTex;
layout(set=0, binding=2) uniform texture2D DepthTex;
layout(set=0, binding=3) uniform sampler Samp;
layout(set=0, binding=4) uniform Edge { vec4 OutlineColor; vec4 Texel; vec4 Thresh; }; // Texel.xy=1/size; Thresh.x=depth,.y=normal
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
void main() {
    vec4 baseSrc = texture(sampler2D(ColorTex, Samp), vUv);
    vec3 base = baseSrc.rgb;
    float d0 = texture(sampler2D(DepthTex, Samp), vUv).r;
    vec3 n0 = texture(sampler2D(NormalTex, Samp), vUv).rgb * 2.0 - 1.0;
    float edge = 0.0;
    vec2 offs[4] = vec2[4](vec2(Texel.x, 0), vec2(-Texel.x, 0), vec2(0, Texel.y), vec2(0, -Texel.y));
    for (int i = 0; i < 4; i++) {
        float d = texture(sampler2D(DepthTex, Samp), vUv + offs[i]).r;
        vec3 n = texture(sampler2D(NormalTex, Samp), vUv + offs[i]).rgb * 2.0 - 1.0;
        if (abs(d - d0) > Thresh.x) edge = 1.0;
        if ((1.0 - dot(n, n0)) > Thresh.y) edge = 1.0;
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
layout(set=0, binding=2) uniform Final { vec4 BgColor; vec4 Params; }; // Params.x=starsOn, .y=transparentBg
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
void main() {
    vec4 s = texture(sampler2D(Src, Samp), vUv);
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
        public const string DecalFrag = @"#version 450
layout(set=0, binding=0) uniform texture2D DepthTex;
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
    float depth = texture(sampler2D(DepthTex, Samp), vUv).r;
    // Reconstruct world position from NDC (xy from screen UV, z from sampled depth). The depth texture's
    // sampling origin is top-left (v=0 maps to NDC y=+1), so the y term is negated; without this the
    // reconstructed world Y ramps across the screen and the Y-band gate clips every shape to a strip.
    vec4 ndc = vec4(vUv.x * 2.0 - 1.0, 1.0 - vUv.y * 2.0, depth, 1.0);
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
