namespace KhaozEngine.Render3D.Internal;

/// <summary>Shared clustered water containment, refracted key entry and path transmittance for lit receivers.</summary>
internal static partial class ShaderSources
{
    internal const string WaterVolumesAt15Glsl = @"
layout(std430, set=0, binding=15) readonly buffer WaterVolumeBuffer { uvec4 WaterVolumes[]; };
";
    internal const string WaterVolumesAt7Glsl = @"
layout(std430, set=0, binding=7) readonly buffer WaterVolumeBuffer { uvec4 WaterVolumes[]; };
";
    internal const string WaterVolumesAt6Glsl = @"
layout(std430, set=0, binding=6) readonly buffer WaterVolumeBuffer { uvec4 WaterVolumes[]; };
";

    // Shared with point lights. The caller reuses the query made for water, including its full-list fallback.
    internal const string ClusterForFragmentGlsl = @"
bool clusterForFragment(vec3 worldPos, out uint clusterIndex) {
    clusterIndex = 0u;
    if (ClusterDepth.w < -0.5) return false;

    vec4 clip = ViewProj * vec4(worldPos, 1.0);
    if (any(isnan(clip)) || any(isinf(clip)) || clip.w <= 0.0) return false;
    vec2 ndc = clip.xy / clip.w;
    if (any(isnan(ndc)) || any(isinf(ndc))) return false;
    const float ndcEpsilon = 1e-5;
    if (ndc.x < -1.0 - ndcEpsilon || ndc.x > 1.0 + ndcEpsilon ||
        ndc.y < -1.0 - ndcEpsilon || ndc.y > 1.0 + ndcEpsilon) return false;

    float depth = dot(worldPos - CameraPos.xyz, ClusterCamera.xyz);
    if (isnan(depth) || isinf(depth)) return false;
    float depthEpsilon = 1e-4 * max(1.0, max(abs(ClusterDepth.x), abs(ClusterDepth.y)));
    if (depth < ClusterDepth.x - depthEpsilon || depth > ClusterDepth.y + depthEpsilon) return false;
    float clampedDepth = clamp(depth, ClusterDepth.x, ClusterDepth.y);

    float zNorm;
    if (ClusterDepth.w > 0.5) {
        if (ClusterDepth.x <= 0.0 || ClusterDepth.z <= 0.0) return false;
        zNorm = log(max(clampedDepth, ClusterDepth.x) / ClusterDepth.x) / ClusterDepth.z;
    } else {
        float span = ClusterDepth.y - ClusterDepth.x;
        if (span <= 0.0) return false;
        zNorm = (clampedDepth - ClusterDepth.x) / span;
    }
    if (isnan(zNorm) || isinf(zNorm)) return false;

    int tileX = clamp(int(floor((ndc.x * 0.5 + 0.5) * 16.0)), 0, 15);
    int tileY = clamp(int(floor((ndc.y * 0.5 + 0.5) * 9.0)), 0, 8);
    int tileZ = clamp(int(floor(clamp(zNorm, 0.0, 1.0) * 24.0)), 0, 23);
    clusterIndex = uint((tileZ * 9 + tileY) * 16 + tileX);
    return true;
}

";

    internal const string UnderwaterKeyGlsl = @"
const float UnderwaterRefractiveIndex = 1.333;
struct UnderwaterKey {
    bool submerged;
    float depth;
    vec3 entry;
    float pathLength;
    vec3 transmittance;
    float penumbraPerMetre;
};
// sampleKeyShadow initializes this before its strength early-out. computeLighting shares the same result,
// so shadows-off receivers still attenuate and the cluster and record list are queried only once per fragment.
UnderwaterKey fragmentUnderwaterKey;
uint underwaterClusterIndex = 0u;
bool underwaterClusterValid = false;

uint waterVolumeWord(uint word) {
    return WaterVolumes[word >> 2u][int(word & 3u)];
}

UnderwaterKey underwaterKeyForFragment(vec3 renderPos) {
    UnderwaterKey result;
    result.submerged = false;
    result.depth = 0.0;
    result.entry = renderPos;
    result.pathLength = 0.0;
    result.transmittance = vec3(1.0);
    result.penumbraPerMetre = 0.0;
    // No buffer read on an empty or opted-out frame. ClusterCamera.w is independent of the point-light count.
    uint recordCount = uint(ClusterCamera.w);
    if (recordCount == 0u) return result;

    underwaterClusterValid = clusterForFragment(renderPos, underwaterClusterIndex);
    uint header = underwaterClusterValid ? waterVolumeWord(underwaterClusterIndex) : 255u;
    bool fullScan = header > 32u;
    uint candidateCount = fullScan ? recordCount : header;
    float surfaceY = 0.0;
    vec3 absorption = vec3(0.0);
    for (uint candidate = 0u; candidate < candidateCount; candidate++) {
        uint record = fullScan ? candidate
            : waterVolumeWord(3456u + underwaterClusterIndex * 32u + candidate);
        if (record >= recordCount) continue;
        // WaterVolumeClusterBuilder is authoritative: 114048 uints precede three uvec4 per record.
        uint baseVector = 28512u + record * 3u;
        vec4 bounds = uintBitsToFloat(WaterVolumes[baseVector]);
        vec4 level = uintBitsToFloat(WaterVolumes[baseVector + 1u]);
        if (renderPos.x < bounds.x || renderPos.z < bounds.y ||
            renderPos.x >= bounds.z || renderPos.z >= bounds.w ||
            renderPos.y < level.y || renderPos.y >= level.x) continue;
        // Lists and full scans both preserve submission order. Equal surfaces retain the first record.
        if (result.submerged && level.x >= surfaceY) continue;
        vec4 optics = uintBitsToFloat(WaterVolumes[baseVector + 2u]);
        result.submerged = true;
        surfaceY = level.x;
        absorption = vec3(level.zw, optics.x);
        result.penumbraPerMetre = optics.y;
    }
    if (!result.submerged) return result;

    vec3 travel = refract(normalize(LightDir.xyz), vec3(0.0, 1.0, 0.0), 1.0 / UnderwaterRefractiveIndex);
    result.depth = surfaceY - renderPos.y;
    result.pathLength = result.depth / -travel.y;
    result.entry = renderPos - travel * result.pathLength;
    result.entry.y = surfaceY;
    result.transmittance = exp(-absorption * result.pathLength);
    return result;
}
";
}
