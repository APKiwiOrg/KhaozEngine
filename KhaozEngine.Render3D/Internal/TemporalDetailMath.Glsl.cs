namespace KhaozEngine.Render3D.Internal;

internal static partial class TemporalDetailMath
{
    internal const string GateGlsl = @"
const float DetailStillEpsilon = 0.00006103515625;
bool detailStill(float motion) { return !(motion >= DetailStillEpsilon); }
bool detailUseFine(float motion, bool locked, uint distrust, uint neighbourDistrust) {
    const uint distrustMask = 1u;
    return !detailStill(motion) && !(locked && motion <= ShimmerStillPixels)
        && ((distrust | neighbourDistrust) & distrustMask) == 0u;
}
";

    // Pure transport and trust rules. CPU mirrors are in Transport.cs. R is rounded before retention is computed,
    // and the update point-loads that same stored R. The fourth child uses the three already-rounded values.
    internal const string Glsl = @"
bool detailFinite(float v) { return (floatBitsToUint(v) & 0x7f800000u) != 0x7f800000u; }
bool detailFinite4(vec4 v) { return detailFinite(v.x) && detailFinite(v.y) && detailFinite(v.z) && detailFinite(v.w); }
float detailHalf(float v) {
    if (abs(v) < 0.00006103515625)
        return roundEven(v * 16777216.0) * 0.000000059604644775390625;
    uint bits = floatBitsToUint(v);
    return uintBitsToFloat((bits + 0xfffu + ((bits >> 13) & 1u)) & 0xffffe000u);
}
vec4 detailRound(vec4 v) { return vec4(detailHalf(v.x), detailHalf(v.y), detailHalf(v.z), detailHalf(v.w)); }
float detailRetention(vec3 r, vec3 current, vec3 mean, float weight, bool accepted) {
    if (!accepted || !detailFinite4(vec4(r, 0.0)) || !detailFinite4(vec4(current, 0.0))
        || !detailFinite4(vec4(mean, 0.0))) return 0.0;
    vec3 h = mean - current;
    float denominator = dot(h, h);
    float cap = clamp(1.0 - weight, 0.0, 1.0);
    return denominator == 0.0 ? cap : clamp(dot(r - current, h) / denominator, 0.0, cap);
}
vec4 detailChannel(float r, vec4 fetched, float mean, float scalar) {
    if (!(r >= 0.0 && r <= 65504.0) || !detailFinite4(fetched) || !detailFinite(mean) || !(scalar > 0.0))
        return vec4(r);
    float k = scalar;
    vec4 delta = fetched - vec4(mean);
    for (int i = 0; i < 4; i++) {
        if (delta[i] < 0.0) k = min(k, r / -delta[i]);
        if (delta[i] > 0.0) k = min(k, (65504.0 - r) / delta[i]);
    }
    float remainderDelta = -((delta.x + delta.y) + delta.z);
    if (remainderDelta < 0.0) k = min(k, r / -remainderDelta);
    if (remainderDelta > 0.0) k = min(k, (65504.0 - r) / remainderDelta);
    vec4 children = vec4(r) + k * delta;
    float remainderChild = r + k * remainderDelta;
    if (any(lessThan(children, vec4(0.0))) || any(greaterThan(children, vec4(65504.0)))
        || remainderChild < 0.0 || remainderChild > 65504.0) {
        k = k > 0.0 ? uintBitsToFloat(floatBitsToUint(k) - 1u) : 0.0;
        children = vec4(r) + k * delta;
    }
    if (k == 0.0) return vec4(r);
    children = clamp(children, vec4(0.0), vec4(65504.0));
    vec4 stored = detailRound(children);
    stored.w = detailHalf(clamp(4.0 * r - ((stored.x + stored.y) + stored.z), 0.0, 65504.0));
    return stored;
}
";
}
