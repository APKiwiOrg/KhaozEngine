namespace KhaozEngine.Render3D.Internal;

internal static partial class ShaderSources
{
    public const string TargetOutlineMaskVert = @"#version 450
layout(set=0, binding=0) uniform Draw { mat4 ViewProj; mat4 World; vec4 Params; };
layout(location=0) in vec3 Position;
layout(location=1) in vec3 Normal;
layout(location=2) in vec4 Color;
layout(location=3) in vec2 TexCoord;
layout(location=0) out vec2 vUv;
void main() {
    vUv = TexCoord;
    gl_Position = ViewProj * World * vec4(Position, 1.0);
}";

    public const string TargetOutlineFullMaskFrag = @"#version 450
layout(set=0, binding=0) uniform Draw { mat4 ViewProj; mat4 World; vec4 Params; };
layout(set=1, binding=0) uniform texture2D Albedo;
layout(set=1, binding=1) uniform sampler Samp;
layout(location=0) in vec2 vUv;
layout(location=0) out float oCoverage;
layout(location=1) out float oDepth;
void main() {
    if (Params.x > 0.0 && texture(sampler2D(Albedo, Samp), vUv).a < Params.x) discard;
    oCoverage = 1.0;
    oDepth = gl_FragCoord.z;
}";

    public const string TargetOutlineVisibleMaskFrag = @"#version 450
layout(set=0, binding=0) uniform Draw { mat4 ViewProj; mat4 World; vec4 Params; };
layout(set=1, binding=0) uniform texture2D Albedo;
layout(set=1, binding=1) uniform sampler Samp;
layout(location=0) in vec2 vUv;
layout(location=0) out float oCoverage;
void main() {
    if (Params.x > 0.0 && texture(sampler2D(Albedo, Samp), vUv).a < Params.x) discard;
    oCoverage = 1.0;
}";

    public const string TargetOutlineCompositeFrag = @"#version 450
layout(set=0, binding=0) uniform texture2D FullCoverage;
layout(set=0, binding=1) uniform texture2D FullDepth;
layout(set=0, binding=2) uniform texture2D VisibleCoverage;
layout(set=0, binding=3) uniform texture2D SceneDepth;
layout(set=0, binding=4) uniform sampler PointSamp;
layout(set=0, binding=5) uniform sampler LinearSamp;
layout(set=0, binding=6) uniform Composite { vec4 OutlineColor; vec4 Params; };
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
void main() {
    vec2 uv = vec2(vUv.x, 1.0 - vUv.y);
    if (texture(sampler2D(FullCoverage, LinearSamp), uv).r > 0.001) {
        oColor = vec4(0.0);
        return;
    }

    vec2 pixel = Params.xy;
    float width = Params.z;
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
            float visible = texture(sampler2D(VisibleCoverage, LinearSamp), sourceUv).r;
            if (visible <= 0.001) continue;
            float targetDepth = texture(sampler2D(FullDepth, PointSamp), sourceUv).r;
            if (!destinationIsBackground && targetDepth > sceneAtDestination + 0.00002) continue;
            coverage = max(coverage, visible * radial);
        }
    }
    oColor = vec4(OutlineColor.rgb, OutlineColor.a * coverage);
}";
}
