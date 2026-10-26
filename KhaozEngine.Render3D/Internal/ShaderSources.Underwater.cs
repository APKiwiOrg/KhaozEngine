namespace KhaozEngine.Render3D.Internal;

/// <summary>Clustered water containment, refracted key shadows and path transmittance for lit receivers.</summary>
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

    // Spliced after projectCascade. The six lit fragments all declare ShadowMap and ShadowSamp before this block.
    internal const string UnderwaterSubmergedOcclusionGlsl = @"
// One POINT depth, never a filtered depth. The horizontal water plane is evaluated on the fetched texel's
// light ray, not the unsnapped lookup ray. ShadowMat is orthographic with orthogonal, scaled basis rows.
float underwaterSliceAtCascade(int cascade, int count, vec3 endPos, vec3 Ngeo, float slopeSin,
                              float surfaceY, float startDepth, float endDepth, float endBias,
                              vec2 uv, float z) {
    float stepSize = ShadowParams2.x;
    vec2 tapUv = (floor(uv / stepSize) + vec2(0.5)) * stepSize;
    tapUv = clamp(tapUv, vec2(stepSize * 0.5), vec2(1.0 - stepSize * 0.5));
    vec2 atlasUv = vec2((float(cascade) + tapUv.x) / float(count), tapUv.y);
    float stored = textureLod(sampler2D(ShadowMap, ShadowSamp), atlasUv, 0.0).r;

    mat4 m = ShadowMat[cascade];
    vec3 rowX = vec3(m[0].x, m[1].x, m[2].x);
    vec3 rowY = vec3(m[0].y, m[1].y, m[2].y);
    vec3 rowZ = vec3(m[0].z, m[1].z, m[2].z);
    vec2 worldYPerClipXY = vec2(rowX.y / dot(rowX, rowX), rowY.y / dot(rowY, rowY));
    vec2 snapClip = (tapUv - uv) * vec2(2.0, -2.0);
    float normalY = Ngeo.y * ShadowNormalOffsets[cascade] * slopeSin;
    float rayY = endPos.y + normalY + dot(worldYPerClipXY, snapClip);
    // Use the atlas's fitted axis. ShadowLightHold can keep it fixed while LightDir moves between fits.
    float zPerY = dot(rowZ, rowZ) / rowZ.y;
    float surfaceZ = z + (surfaceY - rayY) * zPerY;
    float startZ = surfaceZ - (startDepth - normalY) * zPerY;
    float endZ = surfaceZ - (endDepth - normalY) * zPerY - endBias;
    bool occluded = stored < 1.0 && stored > surfaceZ && stored >= startZ && stored < endZ;
    return occluded ? 0.0 : 1.0;
}

float underwaterSubmergedOcclusion(UnderwaterKey key, vec3 renderPos, vec3 Ngeo, float ndl) {
    if (!key.submerged || key.depth <= 0.0) return 1.0;
    int count = int(ShadowParams.x + 0.5);
    float margin = ShadowParams2.x * 2.0;
    float slopeSin = sqrt(max(0.0, 1.0 - ndl * ndl));
    float receiverBias = ShadowParams.z + ShadowParams.w * clamp(1.0 - ndl, 0.0, 1.0);
    // Shared boundaries leave no gaps between jittered bands. The last end remains at the biased receiver.
    const int sliceCount = 4;
    float jitter = fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715)))) - 0.5;
    float start = 0.0;
    float visibility = 1.0;
    for (int slice = 0; slice < sliceCount; slice++) {
        bool last = slice == sliceCount - 1;
        float end = last ? 1.0 : (float(slice + 1) + jitter) / float(sliceCount);
        vec3 endPos = mix(key.entry, renderPos, end);
        float endBias = last ? receiverBias : 0.0;
        int selected = -1;
        vec2 uv = vec2(0.0); float z = 0.0;
        for (int cascade = 0; cascade < 4; cascade++) {
            if (cascade >= count) break;
            if (!projectCascade(cascade, endPos, Ngeo, slopeSin, margin, uv, z)) continue;
            selected = cascade;
            break;
        }
        if (selected >= 0) {
            float lit = underwaterSliceAtCascade(selected, count, endPos, Ngeo, slopeSin, key.entry.y,
                start * key.depth, end * key.depth, endBias, uv, z);
            float edge = min(min(uv.x, 1.0 - uv.x), min(uv.y, 1.0 - uv.y));
            if (selected < count - 1 && ShadowParams2.w > 0.0 && edge < ShadowParams2.w) {
                vec2 uv2; float z2;
                if (projectCascade(selected + 1, endPos, Ngeo, slopeSin, margin, uv2, z2)) {
                    float lit2 = underwaterSliceAtCascade(selected + 1, count, endPos, Ngeo, slopeSin,
                        key.entry.y, start * key.depth, end * key.depth, endBias, uv2, z2);
                    lit = mix(lit2, lit, smoothstep(0.0, ShadowParams2.w, edge));
                }
            }
            visibility *= lit;
        }
        start = end;
    }
    // Coverage fades belong to the real receiver. Fading each virtual slice would leak key light into a cave
    // whose roof is near the map border even though its floor is safely covered. Blend slice cascades above,
    // then apply the ordinary outer coverage fade once, at P, before sampleKeyShadow applies strength.
    for (int cascade = 0; cascade < 4; cascade++) {
        if (cascade >= count) break;
        vec2 uv; float z;
        if (!projectCascade(cascade, renderPos, Ngeo, slopeSin, margin, uv, z)) continue;
        if (cascade < count - 1) return visibility;
        float edge = min(min(uv.x, 1.0 - uv.x), min(uv.y, 1.0 - uv.y));
        return mix(1.0, visibility, smoothstep(0.0, ShadowParams2.z, edge));
    }
    return 1.0;
}
";
}
