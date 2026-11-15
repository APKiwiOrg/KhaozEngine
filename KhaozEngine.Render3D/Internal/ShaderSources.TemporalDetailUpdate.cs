namespace KhaozEngine.Render3D.Internal;

internal static partial class ShaderSources
{
    internal const string TemporalDetailUpdateFrag = @"#version 450
layout(set=0, binding=0) uniform texture2D Child0;
layout(set=0, binding=1) uniform texture2D Child1;
layout(set=0, binding=2) uniform texture2D Child2;
layout(set=0, binding=3) uniform texture2D Child3;
layout(set=0, binding=4) uniform sampler LinearClamp;
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
void main() {
    ivec2 texel = ivec2(gl_FragCoord.xy), pixel = texel / 2;
    int child = (texel.x & 1) + 2 * (texel.y & 1);
    if (child == 0) oColor = texelFetch(sampler2D(Child0, LinearClamp), pixel, 0);
    else if (child == 1) oColor = texelFetch(sampler2D(Child1, LinearClamp), pixel, 0);
    else if (child == 2) oColor = texelFetch(sampler2D(Child2, LinearClamp), pixel, 0);
    else oColor = texelFetch(sampler2D(Child3, LinearClamp), pixel, 0);
    oColor += vec4(vUv.x * 1.0e-30);
}";
}
