namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// The cascaded shadow-atlas depth passes (3 of the renderer's shader sources).
    /// Part of the <see cref="ShaderSources"/> partial: see ShaderSources.cs for the shared contract
    /// (GLSL #version 450, cross-compiled at load via the GPU seam's SPIR-V path).
    /// </summary>
    internal static partial class ShaderSources
    {

        // ---- Depth-only shadow pass. Renders the instanced casters into the key-light's ortho depth map: transform
        //      to light-clip with the light ViewProj (its own 64-byte UBO), and write the [0,1] light-clip depth into
        //      a single R32F colour target (NOT the hardware depth buffer, so the receivers sample it as a plain
        //      texture2D and do a MANUAL depth compare - portable across Metal/D3D11/Vulkan without a depth-sampling /
        //      comparison-sampler seam). The vertex reuses the model instance stream (locations 5..11 = the per-instance
        //      model matrix), so the shadow pass draws the SAME instance buffer the main pass uploaded, no second
        //      upload. Per-vertex needs only Position (location 0); the other per-vertex attributes are declared so the
        //      shared model vertex buffer binds unchanged.
        //
        //      D3D11/FXC/WARP HAZARD (load-bearing sink below): this shader only READS Position + IModel0..3, so
        //      SPIRV-Cross drops the unread inputs (Normal/Color/TexCoord/Tangent @1..4 and ITint/IEmissive/ISpecParams
        //      @9..11) from the HLSL vertex-input signature, leaving a HOLE (TEXCOORD0, then TEXCOORD5..8). FXC/WARP
        //      miscompiles a holed input signature (the same landmine the SplatVert interpolant note documents), and
        //      building THIS pipeline at scene-construction corrupted WARP so the MAIN model+splat passes rendered no
        //      colour (silhouette/normal/depth survived, only oColor was blank). The `sink` reads every declared input
        //      with a zero weight, so SPIRV-Cross keeps a CONTIGUOUS TEXCOORD0..11 signature (matching ModelVert) with
        //      no hole; gl_Position is unchanged (sink == 0). Do NOT drop the sink or reads of any input.
        //
        //      NEAR-PLANE PANCAKE (issue #394, shared by all three depth vertices below). Each cascade puts its light
        //      eye 2r up-light of the slice centre with the ortho near plane AT the eye, and a caster and the ground
        //      it shades sit h / sin(elevation) apart along the light ray - so at a grazing sun a tall caster lands in
        //      FRONT of the near plane and used to be clipped away, leaving the ground it should shade reading the
        //      atlas clear value (fully lit) with no fall-through to the next cascade. Clamping is the right answer
        //      for a DIRECTIONAL light: a caster up-light of the near plane shadows the whole depth range below it,
        //      so recording it at the near plane with its silhouette intact is exactly correct.
        //
        //      It is done HERE, in the vertex, rather than by flipping the pipeline's depthClipEnabled, and the reason
        //      that still holds is the FAR plane: the rasterizer flag turns off BOTH clip planes at once, and this
        //      pass wants the near one clamped and the far one still clipping. Two supporting reasons are gone as of
        //      17.39.0 and are recorded here so nobody re-derives them: the flag used to be a silent NO-OP on Metal
        //      (both backends derived MTLDepthClipMode from DepthStencilState.DepthTestEnabled and read the flag
        //      nowhere), fixed under issue #598, and Vulkan's depthClampEnable is a DEVICE FEATURE that may be absent,
        //      which VulkanFeatureChain now enables by name where it is present. Clamping clip-space z at the vertex
        //      still needs nothing from the device. The light projection is ORTHOGRAPHIC, so w == 1 and
        //      clamping clip z is exactly "clamp NDC depth to >= 0". Every vertex of a triangle ends up at z >= 0 and
        //      the clipper interpolates linearly, so no interior point can fall in front of the near plane either.
        //      The FAR plane still clips, deliberately: geometry past it is down-light of every receiver in the
        //      cascade and cannot shadow anything, so clipping it is free.
        //
        //      The stored depth is a VARYING (this pass writes depth to an R32F colour target, not the depth buffer),
        //      and it is fed from the UNCLAMPED value so the interpolation across a triangle crossing the near plane
        //      stays exact - clamping the vertex value instead would tilt the interpolated depth away from the light
        //      and under-shadow. The per-fragment clamp lives in the fragments below, which is both exact and
        //      independent of draw order (every pancaked fragment ties at hardware depth 0). ----
        public const string ShadowDepthVert = @"#version 450
layout(set=0, binding=0) uniform U { mat4 LightViewProj; };
layout(location=0) in vec3 Position;
layout(location=1) in vec3 Normal;
layout(location=2) in vec4 Color;
layout(location=3) in vec2 TexCoord;
layout(location=4) in vec4 Tangent;
layout(location=5) in vec4 IModel0;
layout(location=6) in vec4 IModel1;
layout(location=7) in vec4 IModel2;
layout(location=8) in vec4 IModel3;
layout(location=9) in vec4 ITint;
layout(location=10) in vec4 IEmissive;
layout(location=11) in vec4 ISpecParams;
layout(location=0) out float vLightDepth;
void main() {
    mat4 Model = mat4(IModel0, IModel1, IModel2, IModel3);
    vec4 world = Model * vec4(Position, 1.0);
    // Negligible-but-live sink over the otherwise-unread per-vertex + per-instance inputs, so SPIRV-Cross keeps the
    // HLSL vertex-input signature gap-free (TEXCOORD0..11, no hole) and FXC/WARP does not miscompile - see the note
    // above. The sink is the input SUM (NOT statically zero, so the optimizer cannot fold it away and drop the inputs)
    // scaled by 1e-30, so it is numerically negligible in world space; the projected position is unchanged to the bit.
    float sink = Normal.x + Color.x + TexCoord.x + Tangent.x + ITint.x + IEmissive.x + ISpecParams.x;
    world.x += sink * 1e-30;
    vec4 lightClip = LightViewProj * world;
    vLightDepth = lightClip.z / lightClip.w;       // TRUE light-clip depth (unclamped), clamped per fragment below
    lightClip.z = max(lightClip.z, 0.0);           // near-plane pancake, see the note above
    gl_Position = lightClip;
}";

        public const string ShadowDepthFrag = @"#version 450
layout(location=0) in float vLightDepth;
layout(location=0) out vec4 oDepth;               // single R32F target: .r carries the caster's light-space depth
void main() {
    // Per-fragment near-plane pancake: a caster up-light of the near plane records AT it (depth 0), which shadows
    // every receiver below it, instead of being clipped away. Keeps the atlas a [0,1] depth map, and is exact
    // (the varying carries the true interpolated depth, so only the clamped part is flattened).
    oDepth = vec4(max(vLightDepth, 0.0), 0.0, 0.0, 1.0);
}";

        // ---- Dissolve-aware depth pass (issue #287). Same transform + same R32F depth write as ShadowDepthVert/Frag,
        //      plus the per-instance rigid dissolve threaded through so a fading caster's SHADOW thins with it instead
        //      of staying fully solid until the hard cull. Bound only for the caster spans that actually carry a
        //      dissolve (Scene3D classifies them), so a scene that queues no dissolve never builds a fragment discard
        //      into its depth pass and renders through the plain pipeline exactly as before.
        //
        //      The instance layout is the MODEL pass's, extended to locations 12..13 (IDynamic + IDissolve), so the
        //      already-uploaded instance buffer binds unchanged - and the same sink applies: EVERY declared input is
        //      read with a 1e-30 weight so SPIRV-Cross keeps the HLSL vertex-input signature contiguous
        //      (TEXCOORD0..13, no hole) and FXC/WARP does not miscompile. See the ShadowDepthVert note above.
        //
        //      The mask is the SAME world-space value noise as ModelFrag's rigid dissolve, so the shadow erodes with
        //      the mesh in one visual language. The noise must be evaluated in ABSOLUTE world space (the pattern is
        //      world-anchored, and a camera-relative one would re-roll on every render-origin rebase), so the light
        //      UBO carries this frame's RenderOrigin beside the cascade matrix and the vertex adds it back.
        //      Reconstructing in the vertex (not the fragment) keeps the UBO vertex-stage-only: at island scale the
        //      float32 error is sub-millimetre against a 16 cm noise cell.
        //
        //      The SCALE, however, is PER-CASCADE (issue #391), not the colour pass's fixed base. A cascade's texel
        //      world size grows with the cascade, and once a noise cell is smaller than a texel the dither stops
        //      being a dither: the depth pass scatters surviving fragments into isolated texels with no shape left
        //      for the receiver's 3x3 kernel. So the light UBO carries that cascade's scale beside the origin (see
        //      ShadowDissolveNoise.ScaleForCascade), and the vertex hands the fragment a pre-SCALED noise position -
        //      which also keeps the UBO vertex-only, since scaling before interpolation is the same as after. ----
        public const string ShadowDepthDissolveVert = @"#version 450
layout(set=0, binding=0) uniform U {
    mat4 LightViewProj;
    vec4 RenderOrigin;
    vec4 DissolveParams;                          // x = this cascade's dissolve noise scale (1/x = cell size, world units)
};
layout(location=0) in vec3 Position;
layout(location=1) in vec3 Normal;
layout(location=2) in vec4 Color;
layout(location=3) in vec2 TexCoord;
layout(location=4) in vec4 Tangent;
layout(location=5) in vec4 IModel0;
layout(location=6) in vec4 IModel1;
layout(location=7) in vec4 IModel2;
layout(location=8) in vec4 IModel3;
layout(location=9) in vec4 ITint;
layout(location=10) in vec4 IEmissive;
layout(location=11) in vec4 ISpecParams;
layout(location=12) in float IDynamic;
layout(location=13) in vec2 IDissolve;            // x = threshold (0 = solid .. 1 = gone), y = edge width (unused here)
layout(location=0) out float vLightDepth;
layout(location=1) out vec3 vNoisePos;            // ABSOLUTE world position pre-scaled by this cascade's noise scale
layout(location=2) out vec2 vDissolve;
void main() {
    mat4 Model = mat4(IModel0, IModel1, IModel2, IModel3);
    vec4 world = Model * vec4(Position, 1.0);
    // Same negligible-but-live sink as ShadowDepthVert, extended over IDynamic (IDissolve is genuinely read below),
    // so the vertex-input signature stays gap-free. Numerically inert: the projected position is unchanged to the bit.
    float sink = Normal.x + Color.x + TexCoord.x + Tangent.x + ITint.x + IEmissive.x + ISpecParams.x + IDynamic;
    world.x += sink * 1e-30;
    vec4 lightClip = LightViewProj * world;
    vLightDepth = lightClip.z / lightClip.w;       // TRUE light-clip depth (unclamped), clamped per fragment below
    lightClip.z = max(lightClip.z, 0.0);           // near-plane pancake, see the note above ShadowDepthVert
    gl_Position = lightClip;
    vNoisePos = (world.xyz + RenderOrigin.xyz) * DissolveParams.x;
    vDissolve = IDissolve;
}";

        // The dissolve depth fragments' shared prologue: the interpolants plus the SAME hash/noise as ModelFrag's
        // rigid dissolve (and ModelDissolveFrag's character one), so a caster's shadow holes match the holes punched
        // in the caster itself. Keep the three in sync. Spliced into both fragment variants below so the noise
        // itself exists once here.
        const string ShadowDissolveFragPrologue = @"#version 450
layout(location=0) in float vLightDepth;
layout(location=1) in vec3 vNoisePos;
layout(location=2) in vec2 vDissolve;
layout(location=0) out vec4 oDepth;
float dhash(vec3 p) { return fract(sin(dot(p, vec3(12.9898, 78.233, 37.719))) * 43758.5453); }
float dnoise(vec3 p) {
    vec3 i = floor(p); vec3 f = fract(p); f = f * f * (3.0 - 2.0 * f);
    float n000 = dhash(i + vec3(0,0,0)), n100 = dhash(i + vec3(1,0,0));
    float n010 = dhash(i + vec3(0,1,0)), n110 = dhash(i + vec3(1,1,0));
    float n001 = dhash(i + vec3(0,0,1)), n101 = dhash(i + vec3(1,0,1));
    float n011 = dhash(i + vec3(0,1,1)), n111 = dhash(i + vec3(1,1,1));
    return mix(mix(mix(n000, n100, f.x), mix(n010, n110, f.x), f.y),
               mix(mix(n001, n101, f.x), mix(n011, n111, f.x), f.y), f.z);
}
";

        public const string ShadowDepthDissolveFrag = ShadowDissolveFragPrologue + @"
void main() {
    // Gated exactly like ModelFrag: threshold 0 writes depth unconditionally, so an instance carrying no dissolve
    // records the same depth this pipeline's plain sibling would.
    if (vDissolve.x > 0.0) {
        float threshold = clamp(vDissolve.x, 0.0, 1.0);
        float mask = dnoise(vNoisePos);
        if (mask < threshold) discard;            // dissolved away: no depth, so no shadow from this fragment
    }
    oDepth = vec4(max(vLightDepth, 0.0), 0.0, 0.0, 1.0);   // near-plane pancake, as ShadowDepthFrag
}";

        // ---- INVERTED dissolve depth fragment (issue #391). Identical to ShadowDepthDissolveFrag except that it
        //      keeps exactly what that one discards, so the two halves of an HLOD crossfade cover the mask between
        //      them instead of nesting.
        //
        //      Derivation. The props half fades OUT on threshold t and keeps { mask >= t }. The merged half fades IN
        //      on threshold d = 1 - t, and the plain rule would keep { mask >= 1 - t }: for t < 0.5 that CONTAINS
        //      the props' keep-set rather than complementing it, so the union is the larger of the two and bottoms
        //      out at 50 percent of the mask at t = 0.5. The complement of { mask >= t } is { mask < t }, and with
        //      this half's own threshold that reads { mask < 1 - d }. Hence the test below. Union coverage is then
        //      the whole mask at every t, and both ends stay continuous with the single-half draws that bracket the
        //      band (t -> 0 keeps nothing here and everything in the props, t -> 1 the reverse).
        //
        //      Only the SHADOW half is inverted. The colour pass keeps both halves on the plain rule: they are
        //      different geometry at different positions, so their colour dithers do not have to complement, and
        //      inverting one there would change what the crossfade looks like. ----
        public const string ShadowDepthDissolveInvertedFrag = ShadowDissolveFragPrologue + @"
void main() {
    if (vDissolve.x > 0.0) {
        float threshold = clamp(vDissolve.x, 0.0, 1.0);
        float mask = dnoise(vNoisePos);
        if (mask >= 1.0 - threshold) discard;     // keep the complement of the plain half's keep-set
    }
    oDepth = vec4(max(vLightDepth, 0.0), 0.0, 0.0, 1.0);   // near-plane pancake, as ShadowDepthFrag
}";

        // Skinned shadow depth vertex (GPU skinning shadow-pass mirror). TWO vertex-only dynamic-offset buffers since
        // #407: `VBlock` at set 0 holds this (caster, cascade) pair's LightMvp = Model * clip-corrected LightViewProj,
        // folded per draw, and `Palette` at set 1 holds the CASTER's composed bones. They are separate sets because a
        // set carries exactly one dynamic offset and the two are indexed differently: the matrix per caster-cascade,
        // the palette per caster. The palette buffer and its set are the very ones SkinnedModelVert binds, so a
        // caster's bones upload ONCE a frame and every cascade reads that upload instead of a copy of it. It skins the
        // vertex exactly like SkinnedModelVert, then projects into light-clip. The fragment is the shared
        // ShadowDepthFrag (no resources). Reuses the ShadowDepthFrag output. The unread per-vertex inputs
        // (Normal/Color/TexCoord/Tangent) are summed into a 1e-30 sink so SPIRV-Cross keeps the HLSL vertex-input
        // signature gap-free (no FXC/WARP miscompile - the same trap ShadowDepthVert documents).
        public const string SkinnedShadowDepthVert = @"#version 450
layout(set=0, binding=0) uniform VBlock {
    mat4 LightMvp;         // Model * clip-corrected LightViewProj (folded per draw)
};
layout(set=1, binding=0) uniform Palette {
    mat4 bones[128];       // this CASTER's composed palette (inverseBind*jointWorld), shared with the main pass
};
layout(location=0) in vec3 Position;
layout(location=1) in vec3 Normal;
layout(location=2) in vec4 Color;
layout(location=3) in vec2 TexCoord;
layout(location=4) in vec4 BoneIndices;
layout(location=5) in vec4 BoneWeights;
layout(location=6) in vec4 Tangent;
layout(location=0) out float vLightDepth;
void main() {
    float wsum = BoneWeights.x + BoneWeights.y + BoneWeights.z + BoneWeights.w;
    mat4 skin;
    if (wsum < 1e-8) {
        skin = mat4(1.0);
    } else {
        skin = bones[int(BoneIndices.x)] * BoneWeights.x
             + bones[int(BoneIndices.y)] * BoneWeights.y
             + bones[int(BoneIndices.z)] * BoneWeights.z
             + bones[int(BoneIndices.w)] * BoneWeights.w;
    }
    vec4 localPos = skin * vec4(Position, 1.0);
    float sink = Normal.x + Color.x + TexCoord.x + Tangent.x;   // keep the vertex-input signature gap-free
    localPos.x += sink * 1e-30;
    vec4 lightClip = LightMvp * localPos;
    vLightDepth = lightClip.z / lightClip.w;       // TRUE light-clip depth (unclamped), clamped per fragment below
    lightClip.z = max(lightClip.z, 0.0);           // near-plane pancake, see the note above ShadowDepthVert
    gl_Position = lightClip;
}";
    }
}
