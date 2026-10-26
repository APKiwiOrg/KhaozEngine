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

    // The weighted dry footprint is widened in place. A second filter and result blend would double the cost
    // over shallow water. The six lit fragments declare ShadowMap and ShadowSamp before this block.
    internal const string UnderwaterShadowKernelGlsl = @"
float underwaterKernelRadius(UnderwaterKey key, int cascade) {
    mat4 m = ShadowMat[cascade];
    vec3 rowX = vec3(m[0].x, m[1].x, m[2].x);
    float texelsPerMetre = length(rowX) / (2.0 * ShadowParams2.x);
    return min(8.0, 1.0 + key.pathLength * key.penumbraPerMetre * texelsPerMetre);
}

float underwaterKernelNoise(vec2 pixel) {
    return fract(52.9829189 * fract(dot(pixel, vec2(0.06711056, 0.00583715))));
}

mat2 underwaterKernelRotation(float radiusTexels, vec2 pixel) {
    float amount = clamp(radiusTexels - 1.0, 0.0, 1.0);
    float angle = (underwaterKernelNoise(pixel) - 0.5) * 6.28318530718 * amount;
    float c = cos(angle), s = sin(angle);
    return mat2(c, s, -s, c);
}

vec2 underwaterKernelJitter(float radiusTexels, vec2 pixel) {
    vec2 noise = vec2(underwaterKernelNoise(pixel + vec2(19.0, 47.0)),
                      underwaterKernelNoise(pixel + vec2(73.0, 11.0)));
    return (noise - vec2(0.5)) * (0.5 * clamp(radiusTexels - 1.0, 0.0, 1.0));
}

// Concentric square-to-disc mapping of a centred 4x4 cell grid. Equal-area strata retain sixteen useful
// comparisons after widening, rather than rotating the dry filter's phase-dependent nine effective taps.
const vec2 UnderwaterKernelPattern[16] = vec2[16](
    vec2(-0.53033009, -0.53033009), vec2(-0.19411428, -0.72444437),
    vec2( 0.19411428, -0.72444437), vec2( 0.53033009, -0.53033009),
    vec2(-0.72444437, -0.19411428), vec2(-0.17677670, -0.17677670),
    vec2( 0.17677670, -0.17677670), vec2( 0.72444437, -0.19411428),
    vec2(-0.72444437,  0.19411428), vec2(-0.17677670,  0.17677670),
    vec2( 0.17677670,  0.17677670), vec2( 0.72444437,  0.19411428),
    vec2(-0.53033009,  0.53033009), vec2(-0.19411428,  0.72444437),
    vec2( 0.19411428,  0.72444437), vec2( 0.53033009,  0.53033009));

// Exactly sixteen POINT depth comparisons at every radius. The bilinear 3x3 kernel's folded 4x4 footprint
// and tent weights are unchanged at radius one. Tap positions and weights morph within this single kernel,
// reaching the centred, equal-weight pattern over the first texel of widening. Rotation and jitter grow with
// screen-fixed interleaved gradient noise sampled by the TAA-jittered receiver. Compare first, filter after.
float filterKeyShadowWidened(vec2 uv, float depth, int cascade, float radiusTexels, vec2 pixel) {
    int count = int(ShadowParams.x + 0.5);
    float stepSize = ShadowParams2.x;
    float radius = clamp(radiusTexels, 1.0, 8.0);
    if (radius <= 1.0)
        return pcfCascade(ShadowMap, ShadowSamp, cascade, count, uv, depth, stepSize);
    vec2 texel = uv / stepSize - vec2(0.5);
    vec2 base = floor(texel);
    vec2 f = texel - base;
    vec4 wx = vec4(1.0 - f.x, 1.0, 1.0, f.x);
    vec4 wy = vec4(1.0 - f.y, 1.0, 1.0, f.y);
    float widening = clamp(radius - 1.0, 0.0, 1.0);
    mat2 rotation = underwaterKernelRotation(radius, pixel);
    vec2 jitter = underwaterKernelJitter(radius, pixel);
    mat4 m = ShadowMat[cascade];
    vec3 rowX = vec3(m[0].x, m[1].x, m[2].x);
    vec3 rowY = vec3(m[0].y, m[1].y, m[2].y);
    vec3 rowZ = vec3(m[0].z, m[1].z, m[2].z);
    float zPerY = rowZ.y == 0.0 ? 0.0 : dot(rowZ, rowZ) / rowZ.y;
    vec2 depthPerUv = -vec2(rowX.y / dot(rowX, rowX), rowY.y / dot(rowY, rowY))
        * vec2(2.0, -2.0) * zPerY;
    float lit = 0.0;
    for (int y = 0; y < 4; y++) {
        for (int x = 0; x < 4; x++) {
            vec2 offset = vec2(float(x), float(y)) - vec2(1.0) - f;
            vec2 spread = rotation * mix(offset, UnderwaterKernelPattern[y * 4 + x] * radius, widening) + jitter;
            vec2 luv = (floor(uv / stepSize + spread) + vec2(0.5)) * stepSize;
            luv = clamp(luv, vec2(stepSize * 0.5), vec2(1.0 - stepSize * 0.5));
            vec2 dryUv = (base + vec2(float(x), float(y)) - vec2(0.5)) * stepSize;
            dryUv = clamp(dryUv, vec2(stepSize * 0.5), vec2(1.0 - stepSize * 0.5));
            // Widen along the horizontal entry plane. A constant comparison depth would move the outer
            // rays above shallow casters. Correct only displacement from the dry tap, retaining its bias
            // and exact comparison at zero widening. Evaluate the actual snapped and clamped texel ray.
            float tapDepth = depth + dot(depthPerUv, luv - dryUv);
            vec2 atlasUv = vec2((float(cascade) + luv.x) / float(count), luv.y);
            float stored = textureLod(sampler2D(ShadowMap, ShadowSamp), atlasUv, 0.0).r;
            float weight = mix(wx[x] * wy[y] / 9.0, 1.0 / 16.0, widening);
            lit += (stored >= 1.0 || tapDepth <= stored ? 1.0 : 0.0) * weight;
        }
    }
    return lit;
}

// Four slice comparisons keep their existing budget. Spread their rays over the same widened footprint,
// returning to the original centre ray at zero widening. Recompute the radius in each selected cascade.
vec2 underwaterMarchOffset(UnderwaterKey key, int cascade, int slice, vec2 pixel) {
    float radius = underwaterKernelRadius(key, cascade);
    vec2 quadrant = vec2((slice & 1) == 0 ? -1.0 : 1.0, (slice & 2) == 0 ? -1.0 : 1.0);
    return (underwaterKernelRotation(radius, pixel) * (quadrant * (0.53033009 * (radius - 1.0)))
        + underwaterKernelJitter(radius, pixel)) * ShadowParams2.x;
}
";

    // Spliced after projectCascade and the key kernel.
    internal const string UnderwaterSubmergedOcclusionGlsl = @"
// One POINT depth, never a filtered depth. The horizontal water plane is evaluated on the fetched texel's
// light ray, not the unsnapped lookup ray. ShadowMat is orthographic with orthogonal, scaled basis rows.
float underwaterSliceAtCascade(int cascade, int count, vec3 endPos, vec3 Ngeo, float slopeSin,
                              float surfaceY, float startDepth, float endDepth, float endBias,
                              vec2 uv, float z, vec2 tapOffset) {
    float stepSize = ShadowParams2.x;
    vec2 tapUv = (floor((uv + tapOffset) / stepSize) + vec2(0.5)) * stepSize;
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
                start * key.depth, end * key.depth, endBias, uv, z,
                underwaterMarchOffset(key, selected, slice, gl_FragCoord.xy));
            float edge = min(min(uv.x, 1.0 - uv.x), min(uv.y, 1.0 - uv.y));
            if (selected < count - 1 && ShadowParams2.w > 0.0 && edge < ShadowParams2.w) {
                vec2 uv2; float z2;
                if (projectCascade(selected + 1, endPos, Ngeo, slopeSin, margin, uv2, z2)) {
                    float lit2 = underwaterSliceAtCascade(selected + 1, count, endPos, Ngeo, slopeSin,
                        key.entry.y, start * key.depth, end * key.depth, endBias, uv2, z2,
                        underwaterMarchOffset(key, selected + 1, slice, gl_FragCoord.xy));
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
