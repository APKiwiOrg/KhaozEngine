namespace KhaozEngine.Render3D.Internal;

/// <summary>Water's shared model-frame prefix and cascade wrappers, without the underwater receiver march.</summary>
internal static class WaterColumnShadowSource
{
    internal const string Glsl = @"
layout(set=0, binding=12) uniform texture2D ColumnShadowAtlas;
layout(set=0, binding=13) uniform sampler ColumnShadowSampler;
layout(std140, set=0, binding=14) uniform WaterLitFrame {
    vec4 LitPrefix[43];
    mat4 ColumnShadowMat[4];
    vec4 ColumnShadowParams;
    vec4 ColumnShadowParams2;
    vec4 ColumnShadowNormalOffsets;
};
" + WaterRefractionShaderSource.Glsl + CascadeShadowSelectionSource.Glsl + ShaderSources.CascadeShadowFilterGlsl + @"
bool waterColumnProject(int i, vec3 position, vec3 normal, float slopeSin, float margin, out vec2 uv, out float depth) {
    return cascadeProject(ColumnShadowMat[i], position, normal, ColumnShadowNormalOffsets[i], slopeSin, margin, uv, depth);
}

float waterColumnCompare(int cascade, int count, vec2 uv, float depth, float texelStep, bool surface) {
    if (surface)
        return pcfCascade(ColumnShadowAtlas, ColumnShadowSampler, cascade, count, uv, depth, texelStep);
    vec2 atlasUv = vec2((uv.x + float(cascade)) / float(count), uv.y);
    float stored = textureLod(sampler2D(ColumnShadowAtlas, ColumnShadowSampler), atlasUv, 0.0).r;
    return depth <= stored ? 1.0 : 0.0;
}

float waterColumnVisibility(vec3 position, vec3 normal, bool surface) {
    float strength = ColumnShadowParams.y;
    if (strength <= 0.0) return 1.0;
    int count = int(ColumnShadowParams.x + 0.5);
    float texelStep = ColumnShadowParams2.x;
    float ndl = max(dot(normal, -normalize(LightDir.xyz)), 0.0);
    float slopeSin = sqrt(max(0.0, 1.0 - ndl * ndl));
    float slope = clamp(1.0 - ndl, 0.0, 1.0);
    float bias = ColumnShadowParams.z + ColumnShadowParams.w * slope;
    float margin = texelStep * 2.0;
    int sel = -1; vec2 selUv = vec2(0.0); float selZ = 0.0;
    for (int i = 0; i < 4; i++) {
        if (i >= count) break;
        vec2 uv; float z;
        if (!waterColumnProject(i, position, normal, slopeSin, margin, uv, z)) continue;
        sel = i; selUv = uv; selZ = z; break;
    }
    if (sel < 0) return 1.0;
    float lit = waterColumnCompare(sel, count, selUv, selZ - bias, texelStep, surface);
    float edge = min(min(selUv.x, 1.0 - selUv.x), min(selUv.y, 1.0 - selUv.y));
    if (sel == count - 1) {
        float fade = cascadeCoverage(edge, ColumnShadowParams2.z);
        return mix(1.0, mix(1.0, lit, strength), fade);
    }
    float blend = ColumnShadowParams2.w;
    if (blend > 0.0 && edge < blend) {
        vec2 uv2; float z2;
        if (waterColumnProject(sel + 1, position, normal, slopeSin, margin, uv2, z2)) {
            float lit2 = waterColumnCompare(sel + 1, count, uv2, z2 - bias, texelStep, surface);
            lit = mix(lit2, lit, cascadeBlendWeight(edge, blend));
        }
    }
    return mix(1.0, lit, strength);
}

float waterColumnSurfaceVisibility(vec3 S, vec3 N) {
    return waterColumnVisibility(S, N, true);
}

float waterColumnEntryVisibility(vec3 E) {
    return waterColumnVisibility(E, vec3(0.0, 1.0, 0.0), false);
}
";
}
