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
const int DiagShowGate = 8192;
const int DiagShowLockAge = 16384;
const int DiagShowGateExtent = 32768;
const int DiagLockTrust2 = 65536;
const int DiagLockTrust3 = 131072;
const int DiagLockTrust4 = 262144;
const int DiagLockLumaKill10 = 524288;
const int DiagLockLumaKill20 = 1048576;
const int DiagLockLumaKillWide = 2097152;

// The lock-trust step a ridge adds, 0 where no trust token is set and a ridge sets the lock whole.
float temporalDiagnosticTrustStep(int diag) {
    return (diag & DiagLockTrust2) != 0 ? 0.5 : (diag & DiagLockTrust3) != 0 ? 1.0 / 3.0
        : (diag & DiagLockTrust4) != 0 ? 0.25 : 0.0;
}

// The lock-luma-kill threshold on 1 - min/max of low-frequency luma, 0 where no kill token is set.
float temporalDiagnosticKillThreshold(int diag) {
    return (diag & DiagLockLumaKill10) != 0 ? 0.1 : (diag & DiagLockLumaKill20) != 0 ? 0.2 : 0.0;
}

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

// Low-frequency luma change, 1 - min/max of this frame's and last frame's. The narrow footprint is this frame's 3x3
// mean (meanLuma, from the gathered moments) against last frame's four ring taps (temporalHistoryRingShift's) plus a
// bilinear centre at the reprojected position. The wide footprint is a 5x5 of internal texels around the centre
// texel against a 5x5 of bilinear history taps one internal texel apart in display space around the reprojected
// position, the same world footprint on both sides.
float temporalDiagnosticLumaChange(bool wide, float meanLuma, ivec2 centre, ivec2 maxTexel, vec2 previousUv) {
    vec2 displaySize = Sizes.zw;
    float current = meanLuma;
    float previous = temporalDiagnosticHistoryLuma(previousUv);
    if (wide) {
        current = 0.0;
        vec2 texelStep = vec2(Jitter.z) / displaySize;
        for (int y = -2; y <= 2; y++) {
            for (int x = -2; x <= 2; x++) {
                current += temporalDiagnosticCurrentLuma(centre + ivec2(x, y), maxTexel);
                if (x != 0 || y != 0) previous += temporalDiagnosticHistoryLuma(previousUv + vec2(x, y) * texelStep);
            }
        }
        current /= 25.0;
        previous /= 25.0;
    } else {
        vec2 reach = vec2(ShadingRingPixels) / displaySize;
        for (int corner = 0; corner < 4; corner++) {
            vec2 offset = reach * vec2((corner & 1) == 0 ? -1.0 : 1.0, (corner >> 1) == 0 ? -1.0 : 1.0);
            previous += temporalDiagnosticHistoryLuma(previousUv + offset);
        }
        previous /= 5.0;
    }
    return 1.0 - min(current, previous) / max(max(current, previous), 1.0e-4);
}

// The show-lock-age overlay's source: 1 a ridge refreshed it this frame, else the carried lock's frames since its last
// ridge, read from the stored value, 2 for one, 3 for two or three, 4 for four or more. 0 unlocked.
float temporalDiagnosticLockAge(float lockValue, bool refreshRidge, float storedLock) {
    if (!(lockValue > 0.0)) return 0.0;
    float age = floor((1.0 - storedLock) / LockDecay + 0.5) + 1.0;
    return refreshRidge ? 1.0 : age < 1.5 ? 2.0 : age < 3.5 ? 3.0 : 4.0;
}

// On a held pixel the shading release left, x is the first condition that blocked it, in the release's own order:
// 1 the upscale gate, 2 the ridge or box-width gate, 3 history within the reset extents of the box centre, 4 an
// upscaled pixel whose own shift stayed under its extents, 5 the ring threshold, 6 the opposite-pair requirement,
// 7 no history. For gates 3 to 6, y is 1 where the box half-width is at most ridgeStep, so releaseExtent is ridgeStep
// already, else 2 or 3 up to three ridgeSteps and 4 or 5 beyond, the lower of each pair where the same evidence would
// fire with releaseExtent at ridgeStep.
vec2 temporalDiagnosticGate(bool useHistory, bool ridge, bool carriedLock, bool upscaled, bool strict, bool ringRun,
    bool ringPass, vec2 ringShift, float boxExtent, float ridgeStep, float releaseExtent, float fromCentre,
    float ownShift, vec2 previousUv, float aroundLuma) {
    float gate = !useHistory ? 7.0
        : !(Jitter.z < ShadingMaxUpscale) ? 1.0
        : !(ridge || boxExtent > (carriedLock ? ShadingLockFlatShare : 1.0) * ridgeStep) ? 2.0
        : !(abs(fromCentre) > (1.0 + ShadingResetExtents) * releaseExtent) ? 3.0
        : !ringRun ? 4.0 : !ringPass ? 5.0 : 6.0;
    if (gate < 2.5 || gate > 6.5) return vec2(gate, 0.0);
    float ratio = boxExtent / ridgeStep;
    float alt = ridgeStep;
    bool fires = false;
    if (abs(fromCentre) > (1.0 + ShadingResetExtents) * alt) {
        bool own = ownShift > ShadingChangeExtents * Jitter.z * alt;
        fires = own && !upscaled && !strict;
        if (!fires && (own || !upscaled)) {
            vec2 ring = ringRun ? ringShift : temporalHistoryRingShift(previousUv, Sizes.zw, aroundLuma, fromCentre);
            fires = ring.x > (strict ? max(ShadingStrictRingExtents * alt, carriedLock ? ShadingLockRingShare * ownShift
                : 0.0) : ShadingRingExtents * alt) && (!carriedLock || ring.y > ShadingLockPairExtents * alt);
        }
    }
    return vec2(gate, ratio <= 1.0 ? 1.0 : ratio <= 3.0 ? (fires ? 2.0 : 3.0) : (fires ? 4.0 : 5.0));
}
#endif
";
}
