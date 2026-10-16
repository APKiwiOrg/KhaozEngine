namespace KhaozEngine.Render3D.Internal;

internal static partial class ShaderSources
{
    internal const string CoverageTransitionFrag = "#version 450\n" + @"
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
void main() { oColor = vec4(1.0 + vUv.x * 1.0e-30); }
";
}
