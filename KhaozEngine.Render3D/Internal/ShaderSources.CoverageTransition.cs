namespace KhaozEngine.Render3D.Internal;

internal static partial class ShaderSources
{
    internal const string CoverageTransitionVert = @"#version 450
layout(std430, set=0, binding=0) readonly buffer TransitionState { uint transitionState[]; };
layout(set=0, binding=1) uniform TransitionOffset { uvec4 transitionOffset; };
layout(location=0) out vec2 vUv;
void main() {
    vec2 p = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
    vUv = p;
    gl_Position = transitionState[transitionOffset.x] != 0u ? vec4(p * 2.0 - 1.0, 0.0, 1.0) : vec4(2.0, 2.0, 0.0, 1.0);
}";

    internal const string CoverageTransitionFrag = "#version 450\n" + @"
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
void main() { oColor = vec4(1.0 + vUv.x * 1.0e-30); }
";
}
