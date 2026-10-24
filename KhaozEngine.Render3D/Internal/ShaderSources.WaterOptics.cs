namespace KhaozEngine.Render3D.Internal;

internal static partial class ShaderSources
{
    // Signed rest depths remain signed through interpolation. Only the final physical bed depth clamps land.
    internal const string WaterOpticsGlsl = @"
layout(set=0, binding=10) uniform texture2D OpticsTex;
layout(set=0, binding=11) uniform sampler OpticsSamp;

float opticalBedDepth(vec3 waterRelative, float legacyDepth) {
    if (OpticsParams.y < 0.5) return legacyDepth;
    vec2 uv = clamp((waterRelative.xz - OpticsRect.xy) * OpticsRect.zw, vec2(0.0), vec2(1.0));
    ivec2 dimensions = ivec2(OpticsParams.zw);
    vec2 lattice = uv * vec2(dimensions - ivec2(1));
    ivec2 p = min(ivec2(floor(lattice)), dimensions - ivec2(2));
    vec2 t = lattice - vec2(p);
    float a = texelFetch(sampler2D(OpticsTex, OpticsSamp), p, 0).r;
    float b = texelFetch(sampler2D(OpticsTex, OpticsSamp), p + ivec2(1, 0), 0).r;
    float c = texelFetch(sampler2D(OpticsTex, OpticsSamp), p + ivec2(0, 1), 0).r;
    float d = texelFetch(sampler2D(OpticsTex, OpticsSamp), p + ivec2(1, 1), 0).r;
    float restDepth = a * (1.0 - t.x) * (1.0 - t.y) + b * t.x * (1.0 - t.y)
        + c * (1.0 - t.x) * t.y + d * t.x * t.y;
    return max(0.0, restDepth + waterRelative.y - OpticsParams.x);
}
";

    // Only the triangle program needs this sink. Default water retains its original reconstruction body.
    internal const string WaterTriangleDepthBindingGuardGlsl = @"
    // Raw scene depth is finite. Retain bindings 4/5 when exact optics does not consume legacy depth.
    // Removing them would compact HLSL registers against the shared twelve-element layout.
    if (isnan(groundDepth)) discard;
";

    internal const string WaterTriangleOpticsGlsl = @"
layout(set=0, binding=10) uniform texture2D OpticsTex;
layout(set=0, binding=11) uniform sampler OpticsSamp;
" + WaterOpticsTriangleGlsl + @"
float opticalBedDepth(vec3 waterRelative, float legacyDepth) {
    uvec2 localBits = uvec2(0u);
    [[dont_unroll]] for (int axis = 0; axis < 2; axis++) {
        uint left = axis == 0 ? floatBitsToUint(waterRelative.x) : floatBitsToUint(waterRelative.z);
        uint right = axis == 0 ? floatBitsToUint(OpticsRect.x) : floatBitsToUint(OpticsRect.y);
        uint result = opticsAdd(left,right^0x80000000u);
        if (axis == 0) localBits.x = result;
        else localBits.y = result;
    }
    vec2 local = uintBitsToFloat(localBits);
    float signedDepth;
    // An admitted point must hit. Discard is an invariant guard, never a fallback depth.
    if (!tryOpticsTriangle(local,signedDepth)) discard;
    return max(0.0,signedDepth + waterRelative.y - OpticsParams.x);
}
";
}
