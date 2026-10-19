namespace KhaozEngine.Render3D.Internal;

internal static partial class ShaderSources
{
    // One header decision chooses both the table and payload. No input assembler attributes are declared.
    internal const string CoveragePulling = @"
layout(std430, set=2, binding=0) readonly buffer CoverageCurrent { uint coverageCurrent[]; };
layout(std430, set=2, binding=1) readonly buffer CoverageStatic { uint coverageStatic[]; };
layout(set=2, binding=2) uniform CoverageDraw { uvec4 coverageDraw; };
struct CoverageVertex { vec3 position; vec3 normal; vec4 color; vec2 uv; vec4 tangent; };
float coverageDot4(vec4 a, vec4 b) {
    precise float x = a.x * b.x;
    precise float y = a.y * b.y;
    precise float z = a.z * b.z;
    precise float w = a.w * b.w;
    precise float xy = x + y;
    precise float xyz = xy + z;
    precise float xyzw = xyz + w;
    return xyzw;
}
vec4 coverageTransform(mat4 m, vec4 v) {
    float x = coverageDot4(vec4(m[0][0], m[1][0], m[2][0], m[3][0]), v);
    float y = coverageDot4(vec4(m[0][1], m[1][1], m[2][1], m[3][1]), v);
    float z = coverageDot4(vec4(m[0][2], m[1][2], m[2][2], m[3][2]), v);
    float w = coverageDot4(vec4(m[0][3], m[1][3], m[2][3], m[3][3]), v);
    return vec4(x, y, z, w);
}
CoverageVertex coverageVertex() {
    uint currentBase = coverageDraw.x, staticBase = coverageDraw.y;
    uint corner = uint(gl_VertexIndex) + coverageDraw.z;
    bool fallback = coverageCurrent[currentBase + 14u] != 0u;
    uint table = coverageStatic[staticBase + (fallback ? 50u : 48u)];
    uint index = coverageStatic[staticBase + table + corner];
    uint sourceCount = coverageStatic[staticBase];
    uint base = currentBase + 16u + (fallback ? 0u : sourceCount * 16u) + index * 16u;
    CoverageVertex v;
    v.position = vec3(uintBitsToFloat(coverageCurrent[base]), uintBitsToFloat(coverageCurrent[base+1u]), uintBitsToFloat(coverageCurrent[base+2u]));
    v.normal = vec3(uintBitsToFloat(coverageCurrent[base+3u]), uintBitsToFloat(coverageCurrent[base+4u]), uintBitsToFloat(coverageCurrent[base+5u]));
    v.color = vec4(uintBitsToFloat(coverageCurrent[base+6u]), uintBitsToFloat(coverageCurrent[base+7u]), uintBitsToFloat(coverageCurrent[base+8u]), uintBitsToFloat(coverageCurrent[base+9u]));
    v.uv = vec2(uintBitsToFloat(coverageCurrent[base+10u]), uintBitsToFloat(coverageCurrent[base+11u]));
    v.tangent = vec4(uintBitsToFloat(coverageCurrent[base+12u]), uintBitsToFloat(coverageCurrent[base+13u]), uintBitsToFloat(coverageCurrent[base+14u]), uintBitsToFloat(coverageCurrent[base+15u]));
    return v;
}
";

    internal static readonly string CoverageModelVert = SkinnedModelVert[..SkinnedModelVert.IndexOf("// This CASTER", System.StringComparison.Ordinal)]
        + CoveragePulling + @"
layout(location=0) out vec3 vNormalW;
layout(location=1) out vec4 vColor;
layout(location=2) out vec3 vWorldPos;
layout(location=3) out vec2 vUv;
layout(location=4) out vec4 vTint;
layout(location=5) out vec4 vEmissive;
layout(location=6) out vec4 vSpecParams;
layout(location=7) out vec4 vTangent;
layout(location=8) out float vDynamic;
layout(location=9) out vec3 vDissolve;
void main() {
    CoverageVertex v = coverageVertex();
    vec4 world = coverageTransform(Model, vec4(v.position, 1.0));
    vec4 clip = coverageTransform(ViewProj, world);
    gl_Position = clip;
    vNormalW = normalize(mat3(Model) * v.normal);
    vColor = v.color; vWorldPos = world.xyz; vUv = v.uv;
    vTint = P[0]; vEmissive = P[1]; vSpecParams = P[2];
    vTangent = vec4(mat3(Model) * v.tangent.xyz, v.tangent.w);
    vDynamic = P[3].x;
    vDissolve = vec3(P[3].z, P[3].w, P[3].y);
}";

    internal const string CoverageOutlineVert = @"#version 450
layout(set=0, binding=0) uniform Draw { mat4 ViewProj; mat4 World; vec4 Params; vec4 RenderOrigin; };
" + CoveragePulling + @"
layout(location=0) out vec2 vUv;
layout(location=1) out vec3 vWorldPos;
void main() {
    CoverageVertex v = coverageVertex();
    vec4 world = coverageTransform(World, vec4(v.position, 1.0));
    vec4 clip = coverageTransform(ViewProj, world);
    gl_Position = clip;
    vUv = v.uv;
    vWorldPos = world.xyz + RenderOrigin.xyz;
}";
}
