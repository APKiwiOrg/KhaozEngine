namespace KhaozEngine.Render3D.Internal;

/// <summary>The ordinary actor-history mask and the shared resolve read. No coverage dependency.</summary>
internal static partial class ShaderSources
{
    internal const string HistoryRejectionVert = @"#version 450
layout(location=0) in vec2 Position;
layout(location=0) out vec2 vUv;
void main() {
    vUv = Position;
    gl_Position = vec4(Position, 0.0, 1.0);
}";

    internal const string HistoryRejectionFrag = @"#version 450
layout(location=0) in vec2 vUv;
layout(location=0) out float oRejection;
void main() { oRejection = 1.0 + vUv.x * 1.0e-30; }";

    internal const string HistoryRejectionReadGlsl = @"
bool temporalHistoryRejected(vec2 uv) {
    ivec2 size = textureSize(sampler2D(HistoryRejection, LinearClamp), 0);
    ivec2 site = clamp(ivec2(uv * vec2(size)), ivec2(0), size - ivec2(1));
    return texelFetch(sampler2D(HistoryRejection, LinearClamp), site, 0).r > 0.0;
}
";
}
