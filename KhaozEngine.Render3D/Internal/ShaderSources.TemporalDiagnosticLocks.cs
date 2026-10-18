namespace KhaozEngine.Render3D.Internal;

internal static partial class ShaderSources
{
    // Diagnostic archive only. The normal shader preprocesses this entire block away.
    internal const string TemporalDiagnosticLockGlsl = @"
#ifdef TemporalDiagnostics
const int DiagShowLocked = 512;
const int DiagLockHistoryRidge = 1024;
const int DiagLockShadeRelease = 2048;
const int DiagLockBoundCurrent = 4096;

float temporalDiagnosticHistoryLuma(vec2 uv) {
    return rgbToYCoCg(temporalWeighted(textureLod(sampler2D(HistoryColor, LinearClamp), uv, 0.0).rgb)).x;
}
float temporalDiagnosticCurrentLuma(ivec2 p, ivec2 maxTexel) {
    return rgbToYCoCg(temporalWeighted(texelFetch(sampler2D(SceneColor, LinearClamp),
        clamp(p, ivec2(0), maxTexel), 0).rgb)).x;
}
// The same pair direction and polarity must ridge in current and reprojected history. History's pair spacing
// matches the current internal texel in display space. Each side uses its own centre's absolute/relative threshold.
bool temporalDiagnosticHistoryRidge(ivec2 centre, ivec2 maxTexel, vec2 previousUv) {
    float c = temporalDiagnosticCurrentLuma(centre, maxTexel);
    float h = temporalDiagnosticHistoryLuma(previousUv);
    float ct = max(LockRidgeAbsolute, LockRidgeRelative * c);
    float ht = max(LockRidgeAbsolute, LockRidgeRelative * h);
    for (int axis = 0; axis < 4; axis++) {
        ivec2 d = axis == 0 ? ivec2(1, 0) : axis == 1 ? ivec2(0, 1)
            : axis == 2 ? ivec2(1, 1) : ivec2(1, -1);
        float ca = temporalDiagnosticCurrentLuma(centre - d, maxTexel);
        float cb = temporalDiagnosticCurrentLuma(centre + d, maxTexel);
        vec2 offset = vec2(d) / Sizes.xy;
        float ha = temporalDiagnosticHistoryLuma(previousUv - offset);
        float hb = temporalDiagnosticHistoryLuma(previousUv + offset);
        if ((c - max(ca, cb) > ct && h - max(ha, hb) > ht)
            || (min(ca, cb) - c > ct && min(ha, hb) - h > ht)) return true;
    }
    return false;
}
#endif
";
}
