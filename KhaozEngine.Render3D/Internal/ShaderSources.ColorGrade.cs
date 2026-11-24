namespace KhaozEngine.Render3D.Internal;

internal static partial class ShaderSources
{
    // Two bilinear taps interpolate red and green within each blue slice. The remaining lerp
    // interpolates blue. The half-texel offset keeps each tap inside its tile even at 0 and 1.
    internal static string ColorGradeTonemap() => ShaderText.After(
        ShaderText.After(TonemapFrag, "layout(location=0) out vec4 oColor;", @"
layout(set=0, binding=3) uniform texture2D Grade;
vec2 gradeUv(vec2 rg, float slice) {
    vec2 tile = vec2(mod(slice, 8.0), floor(slice / 8.0));
    return (tile * 32.0 + rg + 0.5) / vec2(256.0, 128.0);
}
vec3 gradeColour(vec3 rgb) {
    vec3 p = clamp(rgb, 0.0, 1.0) * 31.0;
    float slice = floor(p.b);
    vec3 a = textureLod(sampler2D(Grade, Samp), gradeUv(p.rg, slice), 0.0).rgb;
    vec3 b = textureLod(sampler2D(Grade, Samp), gradeUv(p.rg, min(slice + 1.0, 31.0)), 0.0).rgb;
    return mix(rgb, mix(a, b, fract(p.b)), Params.w);
}"), "    oColor = vec4(tonemapColour(s.rgb, Params.xyz), s.a);",
        "\n    oColor.rgb = gradeColour(oColor.rgb);");
}
