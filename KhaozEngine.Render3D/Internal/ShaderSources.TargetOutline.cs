namespace KhaozEngine.Render3D.Internal;

internal static partial class ShaderSources
{
    public const string TargetOutlineMaskVert = @"#version 450
layout(set=0, binding=0) uniform Draw { mat4 ViewProj; mat4 World; vec4 Params; vec4 RenderOrigin; };
layout(location=0) in vec3 Position;
layout(location=1) in vec3 Normal;
layout(location=2) in vec4 Color;
layout(location=3) in vec2 TexCoord;
layout(location=0) out vec2 vUv;
layout(location=1) out vec3 vWorldPos;
void main() {
    vUv = TexCoord;
    vec4 world = World * vec4(Position, 1.0);
    float sink = Normal.x + Color.x;
    world.x += sink * 1e-30;
    vWorldPos = world.xyz + RenderOrigin.xyz;
    gl_Position = ViewProj * world;
}";

    public const string TargetOutlineFullMaskFrag = @"#version 450
layout(set=0, binding=0) uniform Draw { mat4 ViewProj; mat4 World; vec4 Params; vec4 RenderOrigin; };
layout(set=1, binding=0) uniform texture2D Albedo;
layout(set=1, binding=1) uniform sampler Samp;
layout(location=0) in vec2 vUv;
layout(location=1) in vec3 vWorldPos;
layout(location=0) out float oCoverage;
void main() {
    if (Params.x > 0.0 && texture(sampler2D(Albedo, Samp), vUv).a < Params.x) discard;
    oCoverage = 1.0;
}";

    public const string TargetOutlineVisibleMaskFrag = @"#version 450
layout(set=0, binding=0) uniform Draw { mat4 ViewProj; mat4 World; vec4 Params; vec4 RenderOrigin; };
layout(set=1, binding=0) uniform texture2D Albedo;
layout(set=1, binding=1) uniform sampler Samp;
layout(location=0) in vec2 vUv;
layout(location=1) in vec3 vWorldPos;
layout(location=0) out vec2 oCoverage;
layout(location=1) out float oDepth;
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
    if (Params.x > 0.0 && texture(sampler2D(Albedo, Samp), vUv).a < Params.x) discard;
    if (Params.y > 0.0 || Params.z > 0.5) {
        float mask = dnoise(vWorldPos * 6.0);
        bool keep = Params.z > 0.5 ? mask < Params.y : mask >= Params.y;
        if (!keep) discard;
    }
    oCoverage = vec2(1.0, 0.0);
    oDepth = gl_FragCoord.z;
    // The scene wrote this surface's depth through the model program, and this program does not reproduce it
    // bit for bit. The mismatch grows with the depth slope of the grazing faces that form a silhouette, so the
    // test against the scene depth takes a constant plus a slope-scaled bias. The composite keeps the true depth.
    gl_FragDepth = gl_FragCoord.z - (2.5e-7 + fwidth(gl_FragCoord.z));
}";

    public const string TargetOutlineCompositeFrag = @"#version 450
layout(set=0, binding=0) uniform texture2D FullCoverage;
layout(set=0, binding=1) uniform texture2D VisibleCoverage;
layout(set=0, binding=2) uniform texture2D VisibleDepth;
layout(set=0, binding=3) uniform texture2D SceneDepth;
layout(set=0, binding=4) uniform sampler PointSamp;
layout(set=0, binding=5) uniform sampler LinearSamp;
layout(set=0, binding=6) uniform Composite { vec4 OutlineColor; vec4 Params; vec4 Mode; };
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
void main() {
    vec2 uv = vec2(vUv.x, 1.0 - vUv.y);
    float centerCoverage = Mode.x > 0.5
        ? textureLod(sampler2D(FullCoverage, PointSamp), uv, 0.0).r
        : texture(sampler2D(FullCoverage, LinearSamp), uv).r;
    if (centerCoverage > 0.001) {
        oColor = vec4(0.0);
        return;
    }

    vec2 pixel = Params.xy;
    float width = Params.z + Mode.y;
    float backgroundDepth = Params.w;
    float sceneAtDestination = texture(sampler2D(SceneDepth, PointSamp), uv).r;
    bool destinationIsBackground = abs(sceneAtDestination - backgroundDepth) < 0.00001;
    float coverage = 0.0;
    int radius = int(ceil(width + 0.5));
    for (int y = -radius; y <= radius; y++) {
        for (int x = -radius; x <= radius; x++) {
            if (x == 0 && y == 0) continue;
            vec2 pixelDistance = max(abs(vec2(x, y)) - vec2(0.5), vec2(0.0));
            float radial = clamp(width + 0.5 - length(pixelDistance), 0.0, 1.0);
            if (radial <= 0.0) continue;
            vec2 sourceUv = uv + vec2(x, y) * pixel;
            float visible = Mode.x > 0.5
                ? textureLod(sampler2D(VisibleCoverage, PointSamp), sourceUv, 0.0).r
                : textureLod(sampler2D(VisibleCoverage, LinearSamp), sourceUv, Mode.z).r;
            if (visible <= 0.001) continue;
            float pointVisible = textureLod(sampler2D(VisibleCoverage, PointSamp), sourceUv, 0.0).r;
            if (pointVisible <= 0.001) continue;
            float resolvedDepth = textureLod(sampler2D(VisibleDepth, PointSamp), sourceUv, 0.0).r;
            float targetDepth = (resolvedDepth - (1.0 - pointVisible) * backgroundDepth) / pointVisible;
            if (!destinationIsBackground && targetDepth > sceneAtDestination + 0.0000002) continue;
            coverage = max(coverage, sqrt(visible) * radial);
        }
    }
    oColor = vec4(OutlineColor.rgb, OutlineColor.a * coverage);
}";
}
