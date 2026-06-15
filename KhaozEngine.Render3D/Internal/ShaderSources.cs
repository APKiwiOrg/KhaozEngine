namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// GLSL #version 450 shader sources, compiled at load via Veldrid.SPIRV (GLSL -> SPIR-V -> MSL/HLSL/GLSL).
    /// Post shaders use Veldrid's separate texture2D + sampler style (not combined sampler2D) so the
    /// ResourceLayout binding order is unambiguous. The model pass writes 3 MRT color targets
    /// (lit color, encoded normal, linear-ish depth) so the edge pass never samples a depth texture.
    /// </summary>
    internal static class ShaderSources
    {
        // ---- Model pass. One combined UBO (both stages) avoids a cross-stage two-buffer binding
        //      issue in the Veldrid/SPIRV Metal mapping. Matrices uploaded row-major directly. ----
        public const string ModelVert = @"#version 450
layout(set=0, binding=0) uniform U {
    mat4 ViewProj; mat4 Model;
    vec4 LightDir; vec4 LightColor; vec4 Ambient; vec4 Params; vec4 Tint;
    vec4 FillDir; vec4 FillColor; vec4 CameraPos; vec4 Emissive; vec4 SpecParams;
};
layout(location=0) in vec3 Position;
layout(location=1) in vec3 Normal;
layout(location=2) in vec4 Color;
layout(location=0) out vec3 vNormalW;
layout(location=1) out vec4 vColor;
layout(location=2) out float vDepth;
layout(location=3) out vec3 vWorldPos;
void main() {
    vec4 world = Model * vec4(Position, 1.0);
    gl_Position = ViewProj * world;
    vNormalW = normalize(mat3(Model) * Normal);
    vColor = Color;
    vDepth = gl_Position.z / gl_Position.w; // 0..1 in Veldrid clip space; linear for ortho
    vWorldPos = world.xyz;
}";

        public const string ModelFrag = @"#version 450
layout(set=0, binding=0) uniform U {
    mat4 ViewProj; mat4 Model;
    vec4 LightDir;   // xyz = key light travel direction
    vec4 LightColor;
    vec4 Ambient;
    vec4 Params;     // x = CelBands
    vec4 Tint;       // per-instance RGBA, multiplies the lit color
    vec4 FillDir;    // xyz = fill light travel direction
    vec4 FillColor;  // fill light colour
    vec4 CameraPos;  // xyz = eye position
    vec4 Emissive;   // per-instance self-illumination, added after lighting
    vec4 SpecParams; // x = specular strength, y = shininess exponent
};
layout(location=0) in vec3 vNormalW;
layout(location=1) in vec4 vColor;
layout(location=2) in float vDepth;
layout(location=3) in vec3 vWorldPos;
layout(location=0) out vec4 oColor;
layout(location=1) out vec4 oNormal;
layout(location=2) out vec4 oDepth;
void main() {
    vec3 N = normalize(vNormalW);
    vec3 albedo = vColor.rgb * Tint.rgb;
    float ndlKey  = max(dot(N, -normalize(LightDir.xyz)), 0.0);
    float ndlFill = max(dot(N, -normalize(FillDir.xyz)), 0.0);
    float bands = Params.x;
    if (bands >= 1.0) { ndlKey = floor(ndlKey*bands+0.5)/bands; ndlFill = floor(ndlFill*bands+0.5)/bands; }
    vec3 diffuse = LightColor.rgb*ndlKey + FillColor.rgb*ndlFill;
    // Blinn-Phong specular from the key light only, gated by key ndl so back faces don't shine.
    vec3 V = normalize(CameraPos.xyz - vWorldPos);
    vec3 H = normalize(-normalize(LightDir.xyz) + V);
    float spec = pow(max(dot(N,H),0.0), max(SpecParams.y,1.0)) * SpecParams.x * step(0.0001, ndlKey);
    vec3 lit = albedo * (Ambient.rgb + diffuse) + LightColor.rgb*spec + Emissive.rgb;
    oColor = vec4(lit, 1.0);
    oNormal = vec4(N * 0.5 + 0.5, 1.0);
    oDepth = vec4(vDepth, vDepth, vDepth, 1.0);
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
        // texture in here tripped a Veldrid/Metal multi-resource binding bug).
        public const string BlitFrag = @"#version 450
layout(set=0, binding=0) uniform texture2D Src;
layout(set=0, binding=1) uniform sampler Samp;
layout(set=0, binding=2) uniform Final { vec4 BgColor; vec4 Params; }; // Params.x=starsOn
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
    oColor = vec4(col, 1.0);
}";
    }
}
