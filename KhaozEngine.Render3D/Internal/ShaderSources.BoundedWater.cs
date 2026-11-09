using System;

namespace KhaozEngine.Render3D.Internal;

internal static partial class ShaderSources
{
    const string BoundedWaterTransformGlsl = @"
layout(set=1, binding=0) uniform BoundedWaterTransform {
    mat4 LocalToRender;
    mat4 RenderToLocal;
};
";
    // Only the bounded program has set 1. Released water programs keep their source bytes and bindings.
    internal static string BoundedWaterVert => WaterVert
        .Replace("layout(location=0) in vec3 Position;", BoundedWaterTransformGlsl + "layout(location=0) in vec3 Position;", StringComparison.Ordinal)
        .Replace("vec2 sxz = Position.xz;", "vec3 boundedPosition = (LocalToRender * vec4(Position, 1.0)).xyz;\n        vec2 sxz = boundedPosition.xz;", StringComparison.Ordinal)
        .Replace("vec3(sxz.x, Position.y, sxz.y)", "vec3(sxz.x, boundedPosition.y, sxz.y)", StringComparison.Ordinal);

    // R8b replaces the optical inputs behind this original-domain seam. Its current shading is WaterFrag.
    internal static string BoundedWaterFrag => WaterFrag
        .Replace("layout(location=0) out vec4 oColor;", "layout(location=0) out vec4 oColor;\n" + BoundedWaterTransformGlsl + @"
layout(std430, set=1, binding=1) readonly buffer BoundedWaterDomain { vec4 DomainPoints[]; };
float boundedCross(vec2 a, vec2 b, vec2 p) {
    return (b.x-a.x)*(p.y-a.y) - (b.y-a.y)*(p.x-a.x);
}
bool boundedDomainHolds() {
    vec2 p = (RenderToLocal * vec4(vWorldPos, 1.0)).xz;
    int count = int(DomainPoints[0].y);
    for (int i = 0; i < count; ++i) {
        vec2 a = DomainPoints[1+3*i].xy;
        vec2 b = DomainPoints[2+3*i].xy;
        vec2 c = DomainPoints[3+3*i].xy;
        vec3 sides = vec3(boundedCross(a,b,p), boundedCross(b,c,p), boundedCross(c,a,p));
        if (all(greaterThanEqual(sides, vec3(0.0))) || all(lessThanEqual(sides, vec3(0.0)))) return true;
    }
    return false;
}
", StringComparison.Ordinal)
        .Replace("oColor =", "if (!boundedDomainHolds()) discard;\n    oColor =", StringComparison.Ordinal);
}
