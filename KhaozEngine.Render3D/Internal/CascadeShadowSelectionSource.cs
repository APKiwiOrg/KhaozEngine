namespace KhaozEngine.Render3D.Internal;

/// <summary>Resource-independent projection and edge weights shared by lit receivers and water.</summary>
internal static class CascadeShadowSelectionSource
{
    internal const string Glsl = @"
bool cascadeProject(mat4 matrix, vec3 position, vec3 normal, float normalOffset, float slopeSin, float margin, out vec2 uv, out float depth) {
    vec3 samplePos = position + normal * (normalOffset * slopeSin);
    vec4 lc = matrix * vec4(samplePos, 1.0);
    uv = vec2(0.0); depth = 0.0;
    if (lc.w <= 0.0) return false;
    vec3 proj = lc.xyz / lc.w;
    uv = proj.xy * 0.5 + 0.5;
    // Convert clip Y to the atlas's render-target sampling origin.
    uv.y = 1.0 - uv.y;
    depth = proj.z;
    // Both depth bounds match ShadowMapMath.SelectCascade. In front of the near plane there is no valid
    // caster information, so try the next cascade instead of returning fully lit at a hard edge (#394).
    return !(uv.x < margin || uv.x > 1.0 - margin || uv.y < margin || uv.y > 1.0 - margin || depth < 0.0 || depth > 1.0);
}

float cascadeBlendWeight(float edge, float blend) {
    return smoothstep(0.0, blend, edge);
}

float cascadeCoverage(float edge, float border) {
    return smoothstep(0.0, border, edge);
}
";
}
