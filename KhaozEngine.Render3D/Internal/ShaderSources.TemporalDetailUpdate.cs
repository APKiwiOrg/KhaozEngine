namespace KhaozEngine.Render3D.Internal;

internal static partial class ShaderSources
{
    internal const string TemporalDetailUpdateFrag = @"#version 450
layout(set=0, binding=0) uniform texture2D DetailColor;
layout(set=0, binding=1) uniform texture2D Result;
layout(set=0, binding=2) uniform texture2D Mean;
layout(set=0, binding=3) uniform texture2D Transport;
layout(set=0, binding=4) uniform sampler LinearClamp;
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
" + TemporalDetailMath.Glsl + TemporalDetailFetchGlsl + @"
void main() {
    ivec2 texel = ivec2(gl_FragCoord.xy), pixel = texel / 2;
    vec4 r = texelFetch(sampler2D(Result, LinearClamp), pixel, 0);
    vec4 t = texelFetch(sampler2D(Transport, LinearClamp), pixel, 0);
    if (t.w < 0.5 || !(t.z > 0.0)) { oColor = r; return; }
    vec4 mean = texelFetch(sampler2D(Mean, LinearClamp), pixel, 0);
    vec2 size = vec2(textureSize(sampler2D(Result, LinearClamp), 0));
    vec4 a = detailFetch(t.xy + vec2(-0.25,-0.25) / size, size * 2.0);
    vec4 b = detailFetch(t.xy + vec2( 0.25,-0.25) / size, size * 2.0);
    vec4 c = detailFetch(t.xy + vec2(-0.25, 0.25) / size, size * 2.0);
    vec4 d = detailFetch(t.xy + vec2( 0.25, 0.25) / size, size * 2.0);
    if (!detailFinite4(r) || !detailFinite4(mean) || !detailFinite4(a) || !detailFinite4(b)
        || !detailFinite4(c) || !detailFinite4(d) || any(lessThan(r, vec4(0.0)))
        || any(greaterThan(r, vec4(65504.0)))) { oColor = r; return; }
    int child = (texel.x & 1) + 2 * (texel.y & 1);
    oColor = vec4(detailChannel(r.r, vec4(a.r,b.r,c.r,d.r), mean.r, t.z)[child],
        detailChannel(r.g, vec4(a.g,b.g,c.g,d.g), mean.g, t.z)[child],
        detailChannel(r.b, vec4(a.b,b.b,c.b,d.b), mean.b, t.z)[child],
        detailChannel(r.a, vec4(a.a,b.a,c.a,d.a), mean.a, t.z)[child]) + vec4(vUv.x * 1.0e-30);
}";
}
