namespace KhaozEngine.Render3D.Internal;

/// <summary>Resource-independent projection and edge weights shared by lit receivers and water.</summary>
internal static class CascadeShadowSelectionSource
{
    // Split only at the caller's uniform operands. Lit keeps its original statements and identifiers,
    // while water wraps the same policy in a parameter-only function without changing non-water emissions.
    const string NormalOffsetPositionPrefix = @"
    vec3 samplePos = worldPos + Ngeo * (";
    const string ProjectionMatrixPrefix = @" * slopeSin);
    vec4 lc = ";
    const string ProjectionTail = @" * vec4(samplePos, 1.0);
    uv = vec2(0.0); z = 0.0;
    if (lc.w <= 0.0) return false;
    vec3 proj = lc.xyz / lc.w;                          // light-clip - xy in [-1,1], z in [0,1]
    uv = proj.xy * 0.5 + 0.5;                           // to [0,1] cascade-local texture space
    uv.y = 1.0 - uv.y;                                  // render-target SAMPLING flips V vs the clip-Y the depth
                                                        // pass rasterized with (the same Y-origin trap as before)
    z = proj.z;
    // Both depth bounds, matching the CPU mirror ShadowMapMath.SelectCascade exactly. z < 0 is a receiver in FRONT
    // of this cascade's near plane: it has no valid depth information here (nothing up-light of the near plane is
    // recorded at its own depth), so it must fall through to the next, wider cascade rather than read the map and
    // come back fully lit with a hard edge. The GPU used to test only z > 1.0, which is how a receiver could sit
    // just outside a cascade's depth range and still claim it (issue #394).
    return !(uv.x < margin || uv.x > 1.0 - margin || uv.y < margin || uv.y > 1.0 - margin || z < 0.0 || z > 1.0);
";

    internal const string LitProjectionGlsl = NormalOffsetPositionPrefix + "ShadowNormalOffsets[i]"
        + ProjectionMatrixPrefix + "ShadowMat[i]" + ProjectionTail;
    internal const string BlendWeightExpression = "smoothstep(0.0, blend, edge)";
    internal const string CoverageExpression = "smoothstep(0.0, border, edge)";

    internal const string Glsl = @"
bool cascadeProject(mat4 matrix, vec3 worldPos, vec3 Ngeo, float normalOffset, float slopeSin, float margin, out vec2 uv, out float z) {"
        + NormalOffsetPositionPrefix + "normalOffset" + ProjectionMatrixPrefix + "matrix" + ProjectionTail + @"}

float cascadeBlendWeight(float edge, float blend) {
    return " + BlendWeightExpression + @";
}

float cascadeCoverage(float edge, float border) {
    return " + CoverageExpression + @";
}
";
}
