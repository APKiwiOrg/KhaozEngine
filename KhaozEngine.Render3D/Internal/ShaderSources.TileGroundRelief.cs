namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// The relief variant of the tile-world ground pass's fragment (TileGroundRelief in the Render3D README). Part of
    /// the <see cref="ShaderSources"/> partial: see ShaderSources.cs for the shared contract.
    /// </summary>
    internal static partial class ShaderSources
    {
        // ---- Tile-ground RELIEF fragment shader. Pairs with the UNCHANGED TileGroundVert (and TileGroundMotionVert for
        //      its motion variant): world-aligned projections need no per-vertex tangent, the heights live in the
        //      textures and the overlay weight and slot are the ones the plain fragment reads, so only the fragment
        //      differs. A set whose layers use no relief member never reaches this program (TileGroundRelief.UsesRelief),
        //      which is what keeps TileGroundFrag, its output and its cost exactly as they were.
        //
        //      What it adds, per catalog material (TileGroundParams.Surface and .Blend):
        //        - a SURFACE layer (normal in rgb, roughness in a) in a second array, indexed through Surface.x, and
        //          the HEIGHT in the albedo array's alpha, read with the albedo tap it already takes;
        //        - height-resolved weights where materials meet (corner blends and feathered overlay edges);
        //        - a world-space breakup of a feathered overlay's edge;
        //        - hex-tiling of the top-down taps.
        //      A material that uses none of them takes the plain formulas inside this program: the lattice normal,
        //      roughness 0.5 (exactly the plain exponent 28 and strength Misc.x) and a linear blend against other
        //      map-less materials.
        //
        //      TANGENT FRAMES. The ground's uv is a world-axis projection, so each projection's frame is its own uv
        //      axes: top-down u = +X and the image top (-v) = -Z, the side along X u = +Z and top = +Y, the side
        //      along Z u = +X and top = +Y. Each is orthonormalised against the interpolated lattice normal, so the
        //      frame is the same on every tile, region and ground LOD and the perturbed normal is seamless wherever
        //      the lattice normal is. The normal map convention is glTF's (+x right, +y toward the image top), which
        //      is the convention the model pass's normal maps use, so one texture authoring path serves both.
        //
        //      BINDINGS, set 1, in the splat pass's order: 0 the TileGroundParams block (TileGroundFrag's members
        //      then Surface, Blend and Relief, TileGroundMaterialConfig.BuildReliefParams), 1 AlbedoArray, 2
        //      SurfaceArray, 3 Samp, 4 ShadowMap, 5 ShadowSamp, 6 PointShadowMap, 7 PointShadowTransientMap. The
        //      interpolants are TileGroundVert's eight and every one is read (the FXC rule in TileGroundVert's
        //      note). Every tap is a textureGrad off the hoisted world derivatives, as in TileGroundFrag. ----
        public const string TileGroundReliefFrag = @"#version 450
layout(set=0, binding=0) uniform U {
    mat4 ViewProj;
    vec4 LightDir; vec4 LightColor; vec4 Ambient; vec4 Params;
    vec4 FillDir; vec4 FillColor; vec4 CameraPos;
    vec4 PointPosRadius[16];
    vec4 PointColorIntensity[16];
    mat4 ShadowMat[4];     // per-cascade world->light-clip for the cascaded shadow atlas (offset 688)
    vec4 ShadowParams;     // x=cascadeCount, y=strength (0 => shadows off), z=constBias, w=slopeBias
    vec4 ShadowParams2;    // x=texelStep(1/perCascadeRes), y=maxDistance, z=borderFrac, w=cascadeBlendFrac
    vec4 ShadowNormalOffsets; // per-cascade normal-offset world size (texelWorld_i * ShadowNormalOffset): x=c0..w=c3
    vec4 RenderOrigin;     // camera-relative rendering: add to a render-frame position for the ABSOLUTE world one
    vec4 PointShadowParams[16];  // per point light: x = atlas row or -1 for none, y = bias, z = slope bias, w = transient row or -1
    vec4 PointShadowAtlas;       // xy = one atlas texel in UV, z = rows (lights), w = face columns (6)
    vec4 PointShadowFilter;      // x = filter mode (0 hard, 1 soft), y = light size m, z = max penumbra texels, w = face resolution
    vec4 PointShadowTransientAtlas;
    vec4 ClusterDepth;            // x=near, y=far, z=log(far/near), w=1 perspective, 0 orthographic, -1 invalid
    vec4 ClusterCamera;           // xyz = camera forward in the render frame
    vec4 InteriorCutWindow; vec4 InteriorCutHeights;
    vec4 IndirectParams;
    vec4 IrradianceSH[9];
};
" + PointLightBufferGlsl + PointLightClusterBufferGlsl + WaterVolumesAt6Glsl + @"
// The material's own uniforms, written ONCE at load. The first four members are TileGroundFrag's block member for
// member, so the plain packer's indices hold here too.
layout(set=1, binding=0) uniform TileGroundParams {
    vec4 TintTiling[64];   // xyz = tint, w = tiles/metre, one entry per catalog material
    vec4 Misc;             // x = baseSpecStrength, y = material layer count, zw reserved (0)
    vec4 Triplanar;        // x = cos(start), y = cos(full), z = sharpness, w reserved (0). TileGroundSteepShading
    vec4 SteepBand[64];    // x = companion slot + 1 (0 = none), y = cos(toDegrees), z = cos(fromDegrees), w reserved
    vec4 Surface[64];      // x = surface layer + 1 (0 = none), y = normal mapped, z = roughness mapped, w = anti-tiling (0 none, 1 offset, 2 rotate)
    vec4 Blend[64];        // x = height mapped, y = height blend sharpness, z = feather breakup, w reserved (0)
    vec4 Relief;           // x = height influence, y = neutral height, z = 1 / breakup wavelength (1/m), w = hex lattice scale
};
layout(set=1, binding=1) uniform texture2DArray AlbedoArray;   // rgb albedo, a = height where Blend.x says so
layout(set=1, binding=2) uniform texture2DArray SurfaceArray;  // rgb tangent-space normal, a = roughness
layout(set=1, binding=3) uniform sampler Samp;
layout(set=1, binding=4) uniform texture2D ShadowMap;    // key-light depth map (R32F), sampled LAST
layout(set=1, binding=5) uniform sampler ShadowSamp;     // clamp/linear sampler for the shadow-map PCF taps
layout(set=1, binding=6) uniform texture2D PointShadowMap;  // point-light distance atlas (R32F), sampled through ShadowSamp
layout(set=1, binding=7) uniform texture2D PointShadowTransientMap;
// TileGroundVert's interpolants, gap-free from location 0, every one read below.
layout(location=0) in vec3 vWorldPos;
layout(location=1) in vec3 vNormalW;
layout(location=2) in vec4 vWeights;   // the four corner weights (feather meshes: scaled by the overlay's complement)
layout(location=3) in vec4 vSlots;     // the tile's four corner material slots, floats holding integers
layout(location=4) in float vJitter;   // per-vertex brightness jitter (the sharing-tile average)
layout(location=5) in vec4 vTint;
layout(location=6) in vec4 vEmissive;
layout(location=7) in float vOverlaySlot;
layout(location=0) out vec4 oColor;
layout(location=1) out vec4 oNormal;
layout(location=2) out vec4 oDepth;

vec4 tapAlbedo(int slot, vec2 uv, vec2 g0, vec2 g1) {
    return textureGrad(sampler2DArray(AlbedoArray, Samp), vec3(uv, float(slot)), g0, g1);
}

vec4 tapSurface(int layer, vec2 uv, vec2 g0, vec2 g1) {
    return textureGrad(sampler2DArray(SurfaceArray, Samp), vec3(uv, float(layer)), g0, g1);
}

vec3 safeNormalize(vec3 v, vec3 fallback) {
    float l = dot(v, v);
    return l > 1e-12 ? v * inversesqrt(l) : fallback;
}

// Integer hash, the same bits on every backend: no sin, no float reinterpretation, unsigned wrap only.
uint reliefHash(ivec2 c) {
    uint h = uint(c.x) * 0x8DA6B343u + uint(c.y) * 0xD8163841u;
    h ^= h >> 16; h *= 0x7FEB352Du; h ^= h >> 15; h *= 0x846CA68Bu; h ^= h >> 16;
    return h;
}

float reliefUnit(uint h) { return float(h >> 8) * (1.0 / 16777216.0); }

// Value noise in [-1, 1] on the integer lattice, with a quintic fade so the warp it drives shows no lattice creases.
float reliefValueNoise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = p - i;
    vec2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    ivec2 c = ivec2(i);
    float v00 = reliefUnit(reliefHash(c));
    float v10 = reliefUnit(reliefHash(c + ivec2(1, 0)));
    float v01 = reliefUnit(reliefHash(c + ivec2(0, 1)));
    float v11 = reliefUnit(reliefHash(c + ivec2(1, 1)));
    return mix(mix(v00, v10, u.x), mix(v01, v11, u.x), u.y) * 2.0 - 1.0;
}

// The feather breakup's noise at an ABSOLUTE world position, two octaves with the second offset so their lattices
// never line up. World-anchored, so two tiles or regions meeting at a point read the same value there.
float breakupNoise(vec2 xz) {
    vec2 p = xz * Relief.z;
    return 0.65 * reliefValueNoise(p) + 0.35 * reliefValueNoise(p * 2.9 + vec2(17.3, -9.1));
}

// A tangent-space normal as a world normal, on a projection whose +u runs along T and whose image top (-v) runs
// along B, orthonormalised against the surface normal N. A frame seen edge on, where the projection's weight is
// already negligible, keeps N.
vec3 reliefNormal(vec3 nTS, vec3 T, vec3 B, vec3 N) {
    vec3 t = T - N * dot(N, T);
    float tl = dot(t, t);
    if (tl < 1e-6) { return N; }
    t *= inversesqrt(tl);
    vec3 b = B - N * dot(N, B) - t * dot(t, B);
    float bl = dot(b, b);
    if (bl < 1e-6) { return N; }
    b *= inversesqrt(bl);
    return safeNormalize(t * nTS.x + b * nTS.y + N * nTS.z, N);
}

// One planar projection's taps of one material: the albedo tap (rgb, a = height) and, for a material with a surface
// layer, its normal and roughness. A material without the map keeps N and roughness 0.5.
void projectionTaps(int sl, int surfaceLayer, vec2 uv, vec2 g0, vec2 g1, vec3 T, vec3 B, vec3 N,
                    out vec4 albedoHeight, out vec3 normal, out float rough) {
    albedoHeight = tapAlbedo(sl, uv, g0, g1);
    normal = N;
    rough = 0.5;
    if (surfaceLayer >= 0) {
        vec4 s = tapSurface(surfaceLayer, uv, g0, g1);
        if (Surface[sl].y > 0.5) { normal = reliefNormal(s.rgb * 2.0 - 1.0, T, B, N); }
        if (Surface[sl].z > 0.5) { rough = s.a; }
    }
}

// The texture rotation of one hex, applied to a uv offset and gradient AND to a tangent-space normal's xy: the same
// matrix serves both because tangent y runs toward the image top, against v, which flips the rotation's sense back.
vec2 hexTurn(vec2 v, vec2 cs) { return vec2(cs.x * v.x + cs.y * v.y, -cs.y * v.x + cs.x * v.y); }

// HEX-TILING of a top-down tap (after Mikkelsen 2022, Practical Real-Time Hex-Tiling). The uv is cut into a
// triangle grid whose vertices are hex centres. Each of the three hexes covering the point gives the texture a
// random offset, and in Rotate mode a random turn about its centre, and the three taps blend by their barycentric
// weights sharpened to the 7th power and biased toward the higher height, or the brighter tap for a material
// without a height map, so neighbouring copies meet along stones rather than in a smear.
void hexTaps(int sl, int surfaceLayer, vec2 uv, vec2 g0, vec2 g1, vec3 N, bool rotate,
             out vec4 albedoHeight, out vec3 normal, out float rough) {
    vec2 st = uv * Relief.w;
    vec2 skewed = vec2(st.x - 0.57735027 * st.y, 1.15470054 * st.y);
    vec2 cell = floor(skewed);
    vec3 f = vec3(skewed - cell, 0.0);
    f.z = 1.0 - f.x - f.y;
    float s = step(0.0, -f.z);
    float s2 = 2.0 * s - 1.0;
    vec3 bw = vec3(-f.z * s2, s - f.y * s2, s - f.x * s2);
    ivec2 base = ivec2(cell);
    int si = int(s);
    ivec2 vtx[3] = ivec2[3](base + ivec2(si, si), base + ivec2(si, 1 - si), base + ivec2(1 - si, si));
    vec4 a[3]; vec3 n[3]; float r[3];
    for (int i = 0; i < 3; i++) {
        ivec2 v = vtx[i];
        vec2 centre = vec2(float(v.x) + 0.5 * float(v.y), 0.8660254 * float(v.y)) / Relief.w;
        vec2 offset = vec2(reliefUnit(reliefHash(v)), reliefUnit(reliefHash(v + ivec2(1291, 3307))));
        float angle = rotate ? 6.28318531 * reliefUnit(reliefHash(v + ivec2(-457, 9173))) : 0.0;
        vec2 cs = vec2(cos(angle), sin(angle));
        vec2 tuv = centre + hexTurn(uv - centre, cs) + offset;
        vec2 t0 = hexTurn(g0, cs), t1 = hexTurn(g1, cs);
        a[i] = tapAlbedo(sl, tuv, t0, t1);
        n[i] = N;
        r[i] = 0.5;
        if (surfaceLayer >= 0) {
            vec4 sf = tapSurface(surfaceLayer, tuv, t0, t1);
            if (Surface[sl].y > 0.5) {
                vec3 nTS = sf.rgb * 2.0 - 1.0;
                nTS.xy = hexTurn(nTS.xy, cs);
                n[i] = reliefNormal(nTS, vec3(1.0, 0.0, 0.0), vec3(0.0, 0.0, -1.0), N);
            }
            if (Surface[sl].z > 0.5) { r[i] = sf.a; }
        }
    }
    vec3 bias = Blend[sl].x > 0.5 ? vec3(a[0].a, a[1].a, a[2].a)
        : vec3(dot(a[0].rgb, vec3(0.299, 0.587, 0.114)), dot(a[1].rgb, vec3(0.299, 0.587, 0.114)),
               dot(a[2].rgb, vec3(0.299, 0.587, 0.114)));
    // Clamped first: a barycentric weight can land a rounding error below 0, and pow of a negative is undefined.
    vec3 W = mix(vec3(1.0), bias, 0.6) * pow(max(bw, vec3(0.0)), vec3(7.0));
    W /= max(W.x + W.y + W.z, 1e-6);
    albedoHeight = W.x * a[0] + W.y * a[1] + W.z * a[2];
    normal = safeNormalize(W.x * n[0] + W.y * n[1] + W.z * n[2], N);
    rough = W.x * r[0] + W.y * r[1] + W.z * r[2];
}

// The top-down projection of one material: hex-tiled when its Surface.w asks, the single tap otherwise.
void topTaps(int sl, int surfaceLayer, vec2 uv, vec2 g0, vec2 g1, vec3 N,
             out vec4 albedoHeight, out vec3 normal, out float rough) {
    if (Surface[sl].w > 0.5) {
        hexTaps(sl, surfaceLayer, uv, g0, g1, N, Surface[sl].w > 1.5, albedoHeight, normal, rough);
    } else {
        projectionTaps(sl, surfaceLayer, uv, g0, g1, vec3(1.0, 0.0, 0.0), vec3(0.0, 0.0, -1.0), N,
                       albedoHeight, normal, rough);
    }
}

// One material at this fragment over the projections TileGroundFrag samples, tinted: albedo (rgb) with height (a),
// world normal and roughness. tw and tri are main's, so a fragment below the triplanar start takes only the top-down
// projection, exactly as the plain fragment does.
void reliefMaterial(int sl, vec3 wpAbs, vec3 dWx, vec3 dWy, vec3 tw, bool tri, vec3 N,
                    out vec4 albedoHeight, out vec3 normal, out float rough) {
    float tile = TintTiling[sl].w;
    int surfaceLayer = int(Surface[sl].x + 0.5) - 1;
    if (!tri) {
        topTaps(sl, surfaceLayer, wpAbs.xz * tile, dWx.xz * tile, dWy.xz * tile, N, albedoHeight, normal, rough);
    } else {
        albedoHeight = vec4(0.0);
        vec3 nsum = vec3(0.0);
        rough = 0.0;
        vec4 a; vec3 n; float r;
        if (tw.y > 0.0) {
            topTaps(sl, surfaceLayer, wpAbs.xz * tile, dWx.xz * tile, dWy.xz * tile, N, a, n, r);
            albedoHeight += tw.y * a; nsum += tw.y * n; rough += tw.y * r;
        }
        if (tw.x > 0.0) {
            projectionTaps(sl, surfaceLayer, vec2(wpAbs.z, -wpAbs.y) * tile, vec2(dWx.z, -dWx.y) * tile,
                           vec2(dWy.z, -dWy.y) * tile, vec3(0.0, 0.0, 1.0), vec3(0.0, 1.0, 0.0), N, a, n, r);
            albedoHeight += tw.x * a; nsum += tw.x * n; rough += tw.x * r;
        }
        if (tw.z > 0.0) {
            projectionTaps(sl, surfaceLayer, vec2(wpAbs.x, -wpAbs.y) * tile, vec2(dWx.x, -dWx.y) * tile,
                           vec2(dWy.x, -dWy.y) * tile, vec3(1.0, 0.0, 0.0), vec3(0.0, 1.0, 0.0), N, a, n, r);
            albedoHeight += tw.z * a; nsum += tw.z * n; rough += tw.z * r;
        }
        normal = safeNormalize(nsum, N);
    }
    albedoHeight.rgb *= TintTiling[sl].xyz;
}
" + MaterialLodGradGlsl + LightingCommonGlsl + AtmosphereBindingsAt3Glsl + AtmosphereFogGlsl + AtmosphereFogKeyShadowGlsl + @"
void main() {
    vec3 Ngeo = normalize(vNormalW);
    vec3 wpAbs = vWorldPos + RenderOrigin.xyz;
    int maxSlot = min(63, max(0, int(Misc.y + 0.5) - 1));
    int overlaySlot = clamp(int(vOverlaySlot - 0.5), 0, maxSlot);

    // The plain fragment's weights, then the BREAKUP of a feathered overlay's edge: o becomes o ^ (1 + 6 b m), m in
    // [0, 1] from the world-anchored noise. The exponent never drops below 1, so the overlay never reaches further out
    // than the straight feather and the fringe at the tile boundary stays as soft as it was: the edge wanders INTO
    // the overlay, as deep as the 0.9 contour of its ramp, and never sharpens against the grid line. 0 (where the
    // overlay meets a tile it does not paint) and 1 (its interior) are fixed points. The underlay weights keep their
    // split and take the warped remainder.
    float a0 = vWeights.x, a1 = vWeights.y, a2 = vWeights.z, a3 = vWeights.w;
    float wsum = a0 + a1 + a2 + a3;
    float overlayWeight = vOverlaySlot > 0.5 ? clamp(1.0 - wsum, 0.0, 1.0) : 0.0;
    float breakup = vOverlaySlot > 0.5 ? Blend[overlaySlot].z : 0.0;
    if (breakup > 0.0 && overlayWeight > 0.0 && overlayWeight < 1.0) {
        // Two summed octaves of value noise rarely leave the middle half of their range, so it is stretched to
        // span 0 to 1 and the edge reaches both ends of its wander rather than hovering in between.
        float m = smoothstep(0.2, 0.8, 0.5 + 0.5 * breakupNoise(wpAbs.xz));
        float warped = exp2(log2(overlayWeight) * (1.0 + 6.0 * breakup * m));
        float underlayScale = (1.0 - warped) / (1.0 - overlayWeight);
        a0 *= underlayScale; a1 *= underlayScale; a2 *= underlayScale; a3 *= underlayScale;
        wsum = a0 + a1 + a2 + a3;
        overlayWeight = warped;
    }
    if (vOverlaySlot > 0.5) { wsum = max(1.0, wsum); }
    if (wsum > 1e-5) { a0/=wsum; a1/=wsum; a2/=wsum; a3/=wsum; } else { a0 = 1.0; a1 = a2 = a3 = 0.0; }
    float w[5] = float[5](a0, a1, a2, a3, overlayWeight);
    int slot[5] = int[5](clamp(int(vSlots.x + 0.5), 0, maxSlot), clamp(int(vSlots.y + 0.5), 0, maxSlot),
                         clamp(int(vSlots.z + 0.5), 0, maxSlot), clamp(int(vSlots.w + 0.5), 0, maxSlot),
                         overlaySlot);

    // Hoisted derivatives and slope, exactly as TileGroundFrag takes them.
    vec3 dWx = dFdx(vWorldPos) * materialGradScale();
    vec3 dWy = dFdy(vWorldPos) * materialGradScale();
    float ny = abs(Ngeo.y);
    float triFade = 1.0 - smoothstep(Triplanar.y, Triplanar.x, ny);
    bool tri = triFade > 0.0;
    vec3 tw = vec3(0.0, 1.0, 0.0);
    if (tri) {
        vec3 b = pow(abs(Ngeo), vec3(Triplanar.z));
        b /= max(b.x + b.y + b.z, 1e-5);
        tw = vec3(b.x * triFade, mix(1.0, b.y, triFade), b.z * triFade);
        if (tw.x <= 0.001) { tw.x = 0.0; }
        if (tw.y <= 0.001) { tw.y = 0.0; }
        if (tw.z <= 0.001) { tw.z = 0.0; }
        tw /= tw.x + tw.y + tw.z;
    }

    // Each active role's material, once per distinct slot (firstOf names the role that sampled it). The steep
    // companion takes over the look on a cliff, never the height: the height blend stays the material's own.
    float P[5];
    int firstOf[5];
    vec4 roleAlbedo[5];
    vec3 roleNormal[5];
    float roleRough[5];
    for (int L = 0; L < 5; L++) {
        P[L] = w[L] > 0.001 ? w[L] : 0.0;
        firstOf[L] = L;
        for (int Q = 0; Q < L; Q++) {
            if (P[Q] > 0.0 && slot[Q] == slot[L]) { firstOf[L] = Q; break; }
        }
        roleAlbedo[L] = vec4(0.0);
        roleNormal[L] = Ngeo;
        roleRough[L] = 0.5;
        if (P[L] <= 0.0) { continue; }
        if (firstOf[L] != L) {
            roleAlbedo[L] = roleAlbedo[firstOf[L]];
            roleNormal[L] = roleNormal[firstOf[L]];
            roleRough[L] = roleRough[firstOf[L]];
            continue;
        }
        int sl = slot[L];
        vec4 a; vec3 n; float r;
        reliefMaterial(sl, wpAbs, dWx, dWy, tw, tri, Ngeo, a, n, r);
        vec4 sb = SteepBand[sl];
        if (sb.x > 0.5) {
            float s = 1.0 - smoothstep(sb.y, sb.z, ny);
            if (s > 0.0) {
                vec4 ca; vec3 cn; float cr;
                reliefMaterial(clamp(int(sb.x - 0.5), 0, maxSlot), wpAbs, dWx, dWy, tw, tri, Ngeo, ca, cn, cr);
                a.rgb = mix(a.rgb, ca.rgb, s);
                n = safeNormalize(mix(n, cn, s), Ngeo);
                r = mix(r, cr, s);
            }
        }
        roleAlbedo[L] = a;
        roleNormal[L] = n;
        roleRough[L] = r;
    }

    // HEIGHT BLEND (TileGroundRelief). Map-less roles form one flat group at the neutral height whose members keep
    // their linear split. The group and each distinct height-mapped material take the logit ln(weight) +
    // influence * height, and the resolved weights are their softmax at the fragment's sharpness, the weight-mean of
    // the mapped materials' own. Weight 0 stays 0, so shared corners and edges stay seamless. With no mapped role
    // active the weights are exactly the plain ones.
    float R[5] = float[5](P[0], P[1], P[2], P[3], P[4]);
    float wFlat = 0.0, wMapped = 0.0, sharpSum = 0.0;
    float slotWeight[5] = float[5](0.0, 0.0, 0.0, 0.0, 0.0);
    for (int L = 0; L < 5; L++) {
        if (P[L] <= 0.0) { continue; }
        if (Blend[slot[L]].x > 0.5) {
            wMapped += P[L];
            sharpSum += P[L] * Blend[slot[L]].y;
            slotWeight[firstOf[L]] += P[L];
        } else {
            wFlat += P[L];
        }
    }
    if (wMapped > 0.0) {
        float sharp = sharpSum / wMapped;
        float flatLogit = wFlat > 0.0 ? log(wFlat) + Relief.x * Relief.y : 0.0;
        float top = wFlat > 0.0 ? flatLogit : -3.0e38;
        float logit[5] = float[5](0.0, 0.0, 0.0, 0.0, 0.0);
        for (int L = 0; L < 5; L++) {
            if (slotWeight[L] <= 0.0) { continue; }
            logit[L] = log(slotWeight[L]) + Relief.x * roleAlbedo[L].a;
            top = max(top, logit[L]);
        }
        // The largest logit contributes exp(0) = 1, so the sum is never below 1.
        float flatShare = wFlat > 0.0 ? exp(sharp * (flatLogit - top)) : 0.0;
        float total = flatShare;
        float share[5] = float[5](0.0, 0.0, 0.0, 0.0, 0.0);
        for (int L = 0; L < 5; L++) {
            if (slotWeight[L] > 0.0) { share[L] = exp(sharp * (logit[L] - top)); }
            total += share[L];
        }
        for (int L = 0; L < 5; L++) {
            if (P[L] <= 0.0) { continue; }
            int f = firstOf[L];
            R[L] = Blend[slot[L]].x > 0.5 ? share[f] / total * (P[L] / slotWeight[f])
                                          : flatShare / total * (P[L] / wFlat);
        }
    }

    vec3 albedo = vec3(0.0);
    vec3 Nsum = vec3(0.0);
    float roughSum = 0.0, weightSum = 0.0;
    for (int L = 0; L < 5; L++) {
        if (P[L] <= 0.0) { continue; }
        albedo += R[L] * roleAlbedo[L].rgb;
        Nsum += R[L] * roleNormal[L];
        roughSum += R[L] * roleRough[L];
        weightSum += R[L];
    }
    albedo *= vJitter * vTint.rgb;
    vec3 N = safeNormalize(Nsum, Ngeo);
    // Divided by the weights' own sum, so a fragment of map-less roles is exactly 0.5 however many weights were
    // skipped, and its specular exactly the plain fragment's.
    float rough = weightSum > 0.0 ? roughSum / weightSum : 0.5;

    // Blinn-Phong from roughness until physically based lighting takes the same roughness: strength
    // Misc.x * 2 * (1 - r) and exponent mix(48, 8, r), which at r = 0.5 are exactly the plain fragment's Misc.x and
    // 28. The shadow slope bias keeps the geometric normal, so a bumpy map cannot open acne on flat ground.
    float specStrength = Misc.x * 2.0 * (1.0 - rough);
    float specExp = mix(48.0, 8.0, rough);
    float ndlKeyForShadow = max(dot(Ngeo, -normalize(LightDir.xyz)), 0.0);
    float keyShadow = sampleKeyShadow(ShadowMap, ShadowSamp, vWorldPos, Ngeo, ndlKeyForShadow);
    vec3 diffuse; vec3 specColor;
    computeLighting(PointShadowMap, PointShadowTransientMap, ShadowSamp, N, vWorldPos, specStrength, specExp, keyShadow, diffuse, specColor);
    vec3 lit = albedo * (indirectDiffuse(N, vWorldPos) + diffuse) + specColor + vEmissive.rgb;
    lit = atmosphereFogKeyShadowed(lit, vWorldPos, RenderOrigin.w > 0.5, ShadowMap, ShadowSamp);
    oColor = vec4(lit, 1.0);
    oNormal = vec4(Ngeo * 0.5 + 0.5, 1.0); // GEOMETRIC normal for the edge pass
    // Per-fragment NDC depth, never a varying: see the note at the top of ShaderSources.Model.cs (issue #301).
    oDepth = vec4(gl_FragCoord.z, gl_FragCoord.z, gl_FragCoord.z, 1.0);
}";
    }
}
