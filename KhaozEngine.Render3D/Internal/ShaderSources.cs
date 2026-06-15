namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// GLSL #version 450 shader sources, compiled at load via Veldrid.SPIRV (GLSL -> SPIR-V -> MSL/HLSL/GLSL).
    /// The model pass writes 3 MRT color targets (lit color, encoded normal, linear-ish depth) so the edge
    /// pass needs no depth-texture sampling (portable on the Metal backend).
    /// </summary>
    internal static class ShaderSources
    {
        // ---- Model pass ----
        public const string ModelVert = @"#version 450
layout(set=0, binding=0) uniform Cam { mat4 ViewProj; mat4 Model; };
layout(location=0) in vec3 Position;
layout(location=1) in vec3 Normal;
layout(location=2) in vec4 Color;
layout(location=0) out vec3 vNormalW;
layout(location=1) out vec4 vColor;
layout(location=2) out float vDepth;
void main() {
    vec4 world = Model * vec4(Position, 1.0);
    gl_Position = ViewProj * world;
    vNormalW = normalize(mat3(Model) * Normal);
    vColor = Color;
    vDepth = gl_Position.z / gl_Position.w; // 0..1 in Veldrid clip space; linear for ortho
}";

        public const string ModelFrag = @"#version 450
layout(set=0, binding=1) uniform Light {
    vec4 LightDir;   // xyz = travel direction
    vec4 LightColor;
    vec4 Ambient;
    vec4 Params;     // x = CelBands
};
layout(location=0) in vec3 vNormalW;
layout(location=1) in vec4 vColor;
layout(location=2) in float vDepth;
layout(location=0) out vec4 oColor;
layout(location=1) out vec4 oNormal;
layout(location=2) out vec4 oDepth;
void main() {
    vec3 N = normalize(vNormalW);
    float ndl = max(dot(N, -normalize(LightDir.xyz)), 0.0);
    float bands = Params.x;
    if (bands >= 1.0) ndl = floor(ndl * bands + 0.5) / bands; // cel
    vec3 lit = vColor.rgb * (Ambient.rgb + LightColor.rgb * ndl);
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
layout(set=0, binding=0) uniform sampler2D Src;
layout(set=0, binding=1) uniform Pal { vec4 Colors[64]; vec4 Info; }; // Info.x=count, .y=ditherOn
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
const float bayer[16] = float[16](
    0.0, 8.0, 2.0, 10.0, 12.0, 4.0, 14.0, 6.0, 3.0, 11.0, 1.0, 9.0, 15.0, 7.0, 13.0, 5.0);
void main() {
    vec3 c = texture(Src, vUv).rgb;
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
    oColor = vec4(bestC, 1.0);
}";

        // ---- Depth/normal edge outline ----
        public const string EdgeFrag = @"#version 450
layout(set=0, binding=0) uniform sampler2D Color;
layout(set=0, binding=1) uniform sampler2D NormalTex;
layout(set=0, binding=2) uniform sampler2D DepthTex;
layout(set=0, binding=3) uniform Edge { vec4 OutlineColor; vec4 Texel; vec4 Thresh; }; // Texel.xy=1/size; Thresh.x=depth,.y=normal
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
void main() {
    vec3 base = texture(Color, vUv).rgb;
    float d0 = texture(DepthTex, vUv).r;
    vec3 n0 = texture(NormalTex, vUv).rgb * 2.0 - 1.0;
    float edge = 0.0;
    vec2 offs[4] = vec2[4](vec2(Texel.x, 0), vec2(-Texel.x, 0), vec2(0, Texel.y), vec2(0, -Texel.y));
    for (int i = 0; i < 4; i++) {
        float d = texture(DepthTex, vUv + offs[i]).r;
        vec3 n = texture(NormalTex, vUv + offs[i]).rgb * 2.0 - 1.0;
        if (abs(d - d0) > Thresh.x) edge = 1.0;
        if ((1.0 - dot(n, n0)) > Thresh.y) edge = 1.0;
    }
    oColor = vec4(mix(base, OutlineColor.rgb, edge), 1.0);
}";

        // ---- Point upscale blit ----
        public const string BlitFrag = @"#version 450
layout(set=0, binding=0) uniform sampler2D Src;
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
void main() { oColor = texture(Src, vUv); }";
    }
}
