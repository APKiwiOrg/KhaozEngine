namespace KhaozEngine.Render3D.Internal;

internal static partial class ShaderSources
{
    internal const string InteriorCutGlsl = @"
bool interiorCut(vec3 p) {                            // p render-relative
    if (InteriorCutWindow.w <= 0.0) return false;
    vec2 c = (p.xz - InteriorCutWindow.xy) * InteriorCutWindow.z;
    if (any(lessThan(c, vec2(0.0))) || any(greaterThanEqual(c, vec2(InteriorCutWindow.w)))) return false;
    uvec4 b = uvec4(round(texelFetch(sampler2D(InteriorCut, Samp), ivec2(c), 0) * 255.0));
    float lo = float(b.r * 256u + b.g), hi = float(b.b * 256u + b.a);
    float q = (p.y - InteriorCutHeights.x) * InteriorCutHeights.y;
    return hi > lo && q >= lo && q < hi;
}
";
}
