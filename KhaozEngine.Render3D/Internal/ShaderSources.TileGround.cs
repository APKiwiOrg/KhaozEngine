namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// The tile-world ground pass (2 of the renderer's shader sources).
    /// Part of the <see cref="ShaderSources"/> partial: see ShaderSources.cs for the shared contract
    /// (GLSL #version 450, cross-compiled at load via the GPU seam's SPIR-V path).
    /// </summary>
    internal static partial class ShaderSources
    {

        // ---- Tile-ground vertex shader. The splat pass's shape (see ShaderSources.Terrain.cs) with the ModelVertex
        //      fields REPURPOSED for this pipeline only, per section 7.5 of the tile-world design:
        //        Color     = the four blend weights over the tile's four corner material slots
        //        Uv.xy     = corner slots 0 and 1, as floats holding integers, CONSTANT across a triangle
        //        Tangent.x = corner slot 2, Tangent.y = corner slot 3
        //        Tangent.z = the per-vertex brightness jitter, Tangent.w = 0 (unused)
        //        Normal    = the lattice normal, as today. No normal maps in R5, so the fragment lights off it.
        //      The mesher emits per-triangle vertices, so the four slots are the same at all three corners and
        //      interpolation cannot smear them. The fragment reads each back as int(x + 0.5), the same way the splat
        //      pass reads its packed values.
        //
        //      THE SHARED FRAME BLOCK AT SET 0 BINDING 0 is the only uniform buffer this stage reads, and it is the
        //      same buffer the model pass binds.
        //
        //      HISTORY, because the shape it replaced outlived the backend that needed it. The per-material params
        //      used to be APPENDED to this same block (vec4 TintTiling[64] then vec4 Misc, at offset 1008), so the
        //      whole pipeline bound exactly one uniform buffer. The retired Veldrid Metal backend numbered a
        //      pipeline's buffers by per-kind DECLARATION ORDER, so a stage referencing fewer of them than the
        //      declared array put before it read an index nothing had written: the per-layer tint came back zero
        //      and the ground rendered black. That backend went in 18.0.0, #604 unfolded the splat and skinned
        //      passes, and this one followed as the last combined frame-plus-params buffer in the tree
        //      (https://github.com/APKiwiOrg/KhaozEngine/issues/727). The params are their own fragment-only block
        //      at set 1 now, so nothing per-material is declared here at all.
        //
        //      INTERPOLANT LAYOUT, and it is load-bearing on D3D11 (the FXC rule the terrain pass paid for): the
        //      fragment reads EVERY output this vertex declares, so the pixel-input semantics are gap-free from
        //      location 0 by construction and there is no declared-but-unused output for SPIRV-Cross to drop.
        //        0 vWorldPos, 1 vNormalW, 2 vWeights, 3 vSlots, 4 vJitter, 5 vTint, 6 vEmissive
        //      Do NOT add an output here that the fragment does not read: a hole in that block miscompiles on
        //      FXC/WARP (the highest live interpolant reads garbage and blew the whole terrain to flat white, while
        //      Metal and Vulkan tolerated it). The VERTEX INPUT signature has the same rule from the other side, so
        //      every declared input below is read: locations 11..13 (ISpecParams, IDynamic, IDissolve) are left
        //      undeclared rather than declared and sunk, which leaves them TRAILING unused inputs on the shared
        //      instance layout and is valid on all three backends. ----
        public const string TileGroundVert = @"#version 450
layout(set=0, binding=0) uniform U {
    mat4 ViewProj;
    vec4 LightDir; vec4 LightColor; vec4 Ambient; vec4 Params;
    vec4 FillDir; vec4 FillColor; vec4 CameraPos;
    vec4 PointPosRadius[16];
    vec4 PointColorIntensity[16];
    mat4 ShadowMat[4];     // cascaded shadow tail (offset 688): per-cascade world->light-clip
    vec4 ShadowParams;     // x=cascadeCount, y=strength (0 => shadows off), z=constBias, w=slopeBias
    vec4 ShadowParams2;    // x=texelStep(1/perCascadeRes), y=maxDistance, z=borderFrac, w=cascadeBlendFrac
    vec4 ShadowNormalOffsets; // per-cascade normal-offset world size (texelWorld_i * ShadowNormalOffset): x=c0..w=c3
    vec4 RenderOrigin;     // camera-relative rendering: add to a render-frame position for the ABSOLUTE world one
};
layout(location=0) in vec3 Position;
layout(location=1) in vec3 Normal;
layout(location=2) in vec4 Color;      // the four corner weights
layout(location=3) in vec2 TexCoord;   // corner slots 0, 1
layout(location=4) in vec4 Tangent;    // x,y = corner slots 2 and 3, then z = jitter and w = 0
layout(location=5) in vec4 IModel0;
layout(location=6) in vec4 IModel1;
layout(location=7) in vec4 IModel2;
layout(location=8) in vec4 IModel3;
layout(location=9) in vec4 ITint;
layout(location=10) in vec4 IEmissive;
layout(location=0) out vec3 vWorldPos;
layout(location=1) out vec3 vNormalW;
layout(location=2) out vec4 vWeights;
layout(location=3) out vec4 vSlots;
layout(location=4) out float vJitter;
layout(location=5) out vec4 vTint;
layout(location=6) out vec4 vEmissive;
void main() {
    mat4 Model = mat4(IModel0, IModel1, IModel2, IModel3);
    vec4 world = Model * vec4(Position, 1.0);
    gl_Position = ViewProj * world;
    vWorldPos = world.xyz;
    vNormalW = normalize(mat3(Model) * Normal);
    vWeights = Color;
    vSlots = vec4(TexCoord.x, TexCoord.y, Tangent.x, Tangent.y);
    vJitter = Tangent.z;
    vTint = ITint;
    vEmissive = IEmissive;
}";

        // ---- Tile-ground fragment shader. Pairs with TileGroundVert. Reads ONE albedo texture array (one layer per
        //      catalog material, up to TileGroundMaterialConfig.MaxMaterials) plus a shared sampler, and blends FOUR
        //      of its layers: the tile's four corner materials, weighted by this vertex's four weights.
        //
        //      WHY FOUR SLOTS PER TILE RATHER THAN A PALETTE PER TRIANGLE: continuity. The slots are fixed per TILE
        //      and a corner vertex is one-hot on its own corner's material, so a shared lattice corner samples the
        //      same material at weight 1 from every triangle touching it and a shared edge interpolates the same two
        //      materials from both sides. The surface is C0 continuous everywhere at four samples per fragment.
        //
        //      THE WEIGHTS RENORMALISE BY THEIR OWN SUM. There is no one-minus-sum fifth layer here (the splat pass's
        //      idiom does not apply): all four weights ride in Color and nothing is implied by the remainder.
        //
        //      BINDINGS AND SAMPLE ORDER, in TWO sets since #727. Set 0 binding 0 is the SHARED frame block, the
        //      one the model pass binds, read by both stages. Everything the material owns is set 1: binding 0 the
        //      TileGroundParams block (fragment only, written once at load), then 1 = AlbedoArray, 2 = Samp,
        //      3 = ShadowMap, 4 = ShadowSamp. The arrays are still sampled in BINDING ORDER with the shadow map
        //      LAST, which is no longer a Metal constraint (the native backend authors its own indices) and stays
        //      as the engine's own convention. The blend loop always takes at least one albedo tap, because the
        //      renormalisation falls back to a one-hot weight when the four sum to nothing, so the shadow map can
        //      never be the first sample.
        //
        //      Every tap is a textureGrad with derivatives HOISTED into uniform control flow above the loop, because
        //      the taps run under a data-dependent branch. An implicit texture() there takes undefined derivatives
        //      once a quad diverges at that branch, which minified ground reads as distance shimmer.
        //
        //      Lighting is the shared LightingCommonGlsl block and the three MRT targets are exactly SplatFrag's
        //      (geometric normal to attachment 1 for the edge pass, NDC depth to attachment 2). ----
        public const string TileGroundFrag = @"#version 450
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
};
// The material's own uniforms, written ONCE at load and never re-uploaded. Declared here and NOT in
// TileGroundVert, because the vertex stage reads none of them (TileGroundMaterialConfig.BuildParams is the C#
// side of this block, MaxMaterials + 1 vec4).
layout(set=1, binding=0) uniform TileGroundParams {
    vec4 TintTiling[64];   // xyz = tint, w = tiles/metre, one entry per catalog material
    vec4 Misc;             // x = baseSpecStrength, yzw reserved (0)
};
layout(set=1, binding=1) uniform texture2DArray AlbedoArray;  // one layer per catalog material, sampled FIRST
layout(set=1, binding=2) uniform sampler Samp;
layout(set=1, binding=3) uniform texture2D ShadowMap;    // key-light depth map (R32F), sampled LAST
layout(set=1, binding=4) uniform sampler ShadowSamp;     // clamp/linear sampler for the shadow-map PCF taps
// Declare the interpolants gap-free from location 0, in the order TileGroundVert emits them. This fragment reads
// ALL of them, so there is no hole for FXC/WARP to miscompile (see the TileGroundVert note).
layout(location=0) in vec3 vWorldPos;
layout(location=1) in vec3 vNormalW;
layout(location=2) in vec4 vWeights;   // the four corner weights, renormalised below by their own sum
layout(location=3) in vec4 vSlots;     // the tile's four corner material slots, floats holding integers
layout(location=4) in float vJitter;   // per-vertex brightness jitter (the sharing-tile average)
layout(location=5) in vec4 vTint;
layout(location=6) in vec4 vEmissive;
layout(location=0) out vec4 oColor;
layout(location=1) out vec4 oNormal;
layout(location=2) out vec4 oDepth;

// Explicit-gradient array tap. g0/g1 are the ddx/ddy of the world-space UV, computed once in uniform flow by the
// caller (see main) so textureGrad's mip/aniso LOD is well-defined even though this runs under the per-slot weight
// branch. An implicit texture() here would take undefined derivatives once a quad diverges at that branch.
vec3 sampleSlot(int slot, vec2 uv, vec2 g0, vec2 g1) {
    return textureGrad(sampler2DArray(AlbedoArray, Samp), vec3(uv, float(slot)), g0, g1).rgb;
}
" + LightingCommonGlsl + @"
void main() {
    vec3 Ngeo = normalize(vNormalW);

    // Renormalise the four corner weights by THEIR OWN SUM. No fifth remainder layer: every weight is carried.
    float a0 = vWeights.x, a1 = vWeights.y, a2 = vWeights.z, a3 = vWeights.w;
    float wsum = a0 + a1 + a2 + a3;
    if (wsum > 1e-5) { a0/=wsum; a1/=wsum; a2/=wsum; a3/=wsum; } else { a0 = 1.0; a1 = a2 = a3 = 0.0; }
    float w[4] = float[4](a0, a1, a2, a3);

    // The four corner slots. Held as floats and read back with the +0.5 round the splat pass uses, because a float
    // that carries an integer can arrive a hair under it and truncation would then pick the wrong material. The
    // clamp to 0..63 is not decoration: the index reaches BOTH the TintTiling array and the texture array's layer,
    // and an out-of-range index into a uniform block array is undefined behaviour rather than a wrap, which on one
    // backend reads whatever follows the block. A mesher bug should show as the wrong material, not as garbage.
    int slot[4] = int[4](clamp(int(vSlots.x + 0.5), 0, 63), clamp(int(vSlots.y + 0.5), 0, 63),
                         clamp(int(vSlots.z + 0.5), 0, 63), clamp(int(vSlots.w + 0.5), 0, 63));

    // Screen-space world derivatives, taken ONCE here in uniform control flow (before the loop's data-dependent
    // `continue`). The UV is wpAbs.xz * tile, so its texture-space gradient is the matching world derivative scaled
    // by that slot's tile rate. Feeding these to textureGrad keeps the mip/aniso LOD well-defined regardless of the
    // branch.
    vec3 dWx = dFdx(vWorldPos);
    vec3 dWy = dFdy(vWorldPos);

    // The tiling pattern is ANCHORED TO THE WORLD, so it reads the ABSOLUTE position: with a render origin in force
    // vWorldPos is camera-relative, and tiling off that would slide the whole ground texture every time the origin
    // stepped. Lighting, the eye vector and the shadow lookup all stay render-relative below: those are differences
    // and the origin cancels.
    vec3 wpAbs = vWorldPos + RenderOrigin.xyz;

    vec3 albedo = vec3(0.0);
    for (int L = 0; L < 4; L++) {
        float wl = w[L];
        if (wl <= 0.001) continue;
        int sl = slot[L];
        float tile = TintTiling[sl].w;
        vec2 uv = wpAbs.xz * tile;
        vec2 g0 = dWx.xz * tile, g1 = dWy.xz * tile;
        albedo += wl * sampleSlot(sl, uv, g0, g1) * TintTiling[sl].xyz;
    }
    // The per-vertex jitter is the soft tile-to-tile brightness variation the colour path already had, kept here so
    // a textured ground reads the same way. Per-instance tint rides on top, as it does in the splat pass.
    albedo *= vJitter * vTint.rgb;

    // Lighting via the shared block (ShaderSources.LightingCommonGlsl, spliced in above). Base specular strength
    // from Misc.x. The exponent is a CONSTANT rather than roughness-derived: a tile-ground layer carries no
    // roughness channel (the set is albedo-only, section 7.5), so there is nothing to ease it across. 28 is the
    // midpoint of the splat pass's SPLAT_SPEC_EXP_SMOOTH 48 to SPLAT_SPEC_EXP_ROUGH 8 range, which is the exponent
    // that pass would reach at roughness 0.5, so ground under this pipeline highlights like middling terrain.
    const float TILEGROUND_SPEC_EXP = 28.0;
    float specStrength = Misc.x;
    // Key-light shadow: sampled AFTER the albedo array, ShadowMap being the last texture of set 1.
    // Tile ground RECEIVES shadows identically to models and terrain, via the same shared helper.
    float ndlKeyForShadow = max(dot(Ngeo, -normalize(LightDir.xyz)), 0.0);
    float keyShadow = sampleKeyShadow(ShadowMap, ShadowSamp, vWorldPos, Ngeo, ndlKeyForShadow);
    vec3 diffuse; vec3 specColor;
    computeLighting(Ngeo, vWorldPos, specStrength, TILEGROUND_SPEC_EXP, keyShadow, diffuse, specColor);
    vec3 lit = albedo * (Ambient.rgb + diffuse) + specColor + vEmissive.rgb;
    oColor = vec4(lit, 1.0);
    oNormal = vec4(Ngeo * 0.5 + 0.5, 1.0); // GEOMETRIC normal for the edge pass
    // Per-fragment NDC depth, never a varying: see the note at the top of ShaderSources.Model.cs (issue #301).
    oDepth = vec4(gl_FragCoord.z, gl_FragCoord.z, gl_FragCoord.z, 1.0);
}";
    }
}
