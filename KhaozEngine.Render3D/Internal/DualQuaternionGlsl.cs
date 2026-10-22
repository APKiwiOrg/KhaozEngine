using System;

namespace KhaozEngine.Render3D.Internal;

/// <summary>The single ordinary version-1 DQ graph. A palette is 256 interleaved real/dual vec4 words.
/// Palette-specific symbols allow current and previous evaluations in the same vertex program.</summary>
internal static class DualQuaternionGlsl
{
    internal static string Source(string paletteName) => Graph.Replace("__PALETTE__", paletteName, StringComparison.Ordinal);

    // Transcribes DualQuaternionSkinning, with scalar precise intermediates to prohibit contraction.
    // Per positive slot: four products for the dot, three ordered adds, eight weighted products and eight adds.
    // Rotation: four squares, six cross products, three diagonals and six doubled off-diagonals.
    // Each rotated row has three products, two ordered adds and one inverse-norm multiplication.
    // Translation has four products, three ordered adds/subtracts and one doubling per component.
    // One reciprocal serves position and both directions. Direction roots/divisions match the CPU graph.
    const string Graph = @"
struct __PALETTE__Rotation {
    float m11; float m12; float m13;
    float m21; float m22; float m23;
    float m31; float m32; float m33;
    float inverseNorm;
};
bool __PALETTE__Finite(float v) { return !isnan(v) && !isinf(v); }
bool __PALETTE__Finite3(vec3 v) {
    return __PALETTE__Finite(v.x) && __PALETTE__Finite(v.y) && __PALETTE__Finite(v.z);
}
vec3 __PALETTE__Rotate(vec3 v, __PALETTE__Rotation r) {
    precise float x = ((r.m11 * v.x) + (r.m12 * v.y)) + (r.m13 * v.z);
    precise float y = ((r.m21 * v.x) + (r.m22 * v.y)) + (r.m23 * v.z);
    precise float z = ((r.m31 * v.x) + (r.m32 * v.y)) + (r.m33 * v.z);
    precise float rx = x * r.inverseNorm;
    precise float ry = y * r.inverseNorm;
    precise float rz = z * r.inverseNorm;
    return vec3(rx, ry, rz);
}
bool __PALETTE__Normalize(vec3 v, vec3 fallback, out vec3 result) {
    precise float squared = ((v.x * v.x) + (v.y * v.y)) + (v.z * v.z);
    if (!__PALETTE__Finite(squared)) return false;
    precise float len = sqrt(squared);
    result = fallback;
    if (len > 1e-8) {
        precise float x = v.x / len;
        precise float y = v.y / len;
        precise float z = v.z / len;
        result = vec3(x, y, z);
    }
    return true;
}
bool __PALETTE__Skin(vec3 position, vec3 normal, vec4 tangent, vec4 indices, vec4 weights,
    out vec3 posedPosition, out vec3 posedNormal, out vec4 posedTangent) {
    posedPosition = position;
    posedNormal = normal;
    posedTangent = vec4(0.0);
    precise float total = ((weights.x + weights.y) + weights.z) + weights.w;
    precise vec4 R = vec4(0.0);
    precise vec4 D = vec4(0.0);
    bool identity = true;
    if (total >= 1e-8) {
        for (int slot = 0; slot < 4; slot++) {
            float weight = weights[slot];
            if (weight <= 0.0) continue;
            int index = int(indices[slot]) * 2;
            vec4 r = __PALETTE__[index];
            vec4 d = __PALETTE__[index + 1];
            identity = identity && r.x == 0.0 && r.y == 0.0 && r.z == 0.0 && (r.w == 1.0 || r.w == -1.0)
                && d.x == 0.0 && d.y == 0.0 && d.z == 0.0 && d.w == 0.0;
            precise float alignment = (((r.x * R.x) + (r.y * R.y)) + (r.z * R.z)) + (r.w * R.w);
            if (alignment < 0.0) {
                r.x = -r.x; r.y = -r.y; r.z = -r.z; r.w = -r.w;
                d.x = -d.x; d.y = -d.y; d.z = -d.z; d.w = -d.w;
            }
            R.x = R.x + (r.x * weight);
            R.y = R.y + (r.y * weight);
            R.z = R.z + (r.z * weight);
            R.w = R.w + (r.w * weight);
            D.x = D.x + (d.x * weight);
            D.y = D.y + (d.y * weight);
            D.z = D.z + (d.z * weight);
            D.w = D.w + (d.w * weight);
        }
    }
    __PALETTE__Rotation rotation;
    if (!identity) {
        precise float xx = R.x * R.x; precise float yy = R.y * R.y;
        precise float zz = R.z * R.z; precise float ww = R.w * R.w;
        precise float n = ((xx + yy) + zz) + ww;
        // S2.6 proves this floor for admitted prepared palettes and rounded stored weights.
        if (!__PALETTE__Finite(n) || n < 0.125) return false;
        precise float inverseNorm = 1.0 / n;
        precise float xy = R.x * R.y; precise float xz = R.x * R.z; precise float yz = R.y * R.z;
        precise float wx = R.w * R.x; precise float wy = R.w * R.y; precise float wz = R.w * R.z;
        precise float m11 = ((ww + xx) - yy) - zz;
        precise float m12 = 2.0 * (xy - wz);
        precise float m13 = 2.0 * (xz + wy);
        precise float m21 = 2.0 * (xy + wz);
        precise float m22 = ((ww - xx) + yy) - zz;
        precise float m23 = 2.0 * (yz - wx);
        precise float m31 = 2.0 * (xz - wy);
        precise float m32 = 2.0 * (yz + wx);
        precise float m33 = ((ww - xx) - yy) + zz;
        rotation = __PALETTE__Rotation(m11, m12, m13, m21, m22, m23, m31, m32, m33, inverseNorm);
        vec3 rotated = __PALETTE__Rotate(position, rotation);
        precise float tx = 2.0 * ((((D.x * R.w) - (D.w * R.x)) - (D.y * R.z)) + (D.z * R.y));
        precise float ty = 2.0 * ((((D.y * R.w) - (D.w * R.y)) - (D.z * R.x)) + (D.x * R.z));
        precise float tz = 2.0 * ((((D.z * R.w) - (D.w * R.z)) - (D.x * R.y)) + (D.y * R.x));
        precise float px = rotated.x + (tx * inverseNorm);
        precise float py = rotated.y + (ty * inverseNorm);
        precise float pz = rotated.z + (tz * inverseNorm);
        posedPosition = vec3(px, py, pz);
        if (!__PALETTE__Finite3(posedPosition)) return false;
    }
    vec3 normalDirection = normal;
    if (!identity) normalDirection = __PALETTE__Rotate(normal, rotation);
    if (!__PALETTE__Normalize(normalDirection, normal, posedNormal)) return false;
    precise float tangentSquared = ((tangent.x * tangent.x) + (tangent.y * tangent.y)) + (tangent.z * tangent.z);
    if (!__PALETTE__Finite(tangentSquared)) return false;
    if (tangentSquared > 1e-12) {
        vec3 tangentDirection = tangent.xyz;
        if (!identity) tangentDirection = __PALETTE__Rotate(tangent.xyz, rotation);
        vec3 normalizedTangent;
        if (!__PALETTE__Normalize(tangentDirection, tangent.xyz, normalizedTangent)) return false;
        posedTangent = vec4(normalizedTangent, tangent.w);
    }
    return true;
}
";
}
