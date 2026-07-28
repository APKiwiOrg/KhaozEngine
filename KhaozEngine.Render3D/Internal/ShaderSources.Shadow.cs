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
        //      no hole; gl_Position is unchanged (sink == 0). Do NOT drop the sink or reads of any input. ----
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
    gl_Position = LightViewProj * world;
    vLightDepth = gl_Position.z / gl_Position.w;   // [0,1] light-clip depth, stored linearly in the R32F target
}";

        public const string ShadowDepthFrag = @"#version 450
layout(location=0) in float vLightDepth;
layout(location=0) out vec4 oDepth;               // single R32F target: .r carries the caster's light-space depth
void main() {
    oDepth = vec4(vLightDepth, 0.0, 0.0, 1.0);
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
        //      The mask is the SAME world-space value noise at the SAME scale as ModelFrag's rigid dissolve, so the
        //      shadow erodes with the mesh in one visual language. The noise must be evaluated in ABSOLUTE world
        //      space (the pattern is world-anchored, and a camera-relative one would re-roll on every render-origin
        //      rebase), so the light UBO carries this frame's RenderOrigin beside the cascade matrix and the vertex
        //      adds it back. Reconstructing in the vertex (not the fragment) keeps the UBO vertex-stage-only: at
        //      island scale the float32 error is sub-millimetre against a 16 cm noise cell. ----
        public const string ShadowDepthDissolveVert = @"#version 450
layout(set=0, binding=0) uniform U { mat4 LightViewProj; vec4 RenderOrigin; };
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
layout(location=1) out vec3 vWorldAbs;            // ABSOLUTE world position: the dissolve noise is world-anchored
layout(location=2) out vec2 vDissolve;
void main() {
    mat4 Model = mat4(IModel0, IModel1, IModel2, IModel3);
    vec4 world = Model * vec4(Position, 1.0);
    // Same negligible-but-live sink as ShadowDepthVert, extended over IDynamic (IDissolve is genuinely read below),
    // so the vertex-input signature stays gap-free. Numerically inert: the projected position is unchanged to the bit.
    float sink = Normal.x + Color.x + TexCoord.x + Tangent.x + ITint.x + IEmissive.x + ISpecParams.x + IDynamic;
    world.x += sink * 1e-30;
    gl_Position = LightViewProj * world;
    vLightDepth = gl_Position.z / gl_Position.w;
    vWorldAbs = world.xyz + RenderOrigin.xyz;
    vDissolve = IDissolve;
}";

        public const string ShadowDepthDissolveFrag = @"#version 450
layout(location=0) in float vLightDepth;
layout(location=1) in vec3 vWorldAbs;
layout(location=2) in vec2 vDissolve;
layout(location=0) out vec4 oDepth;
// The SAME hash/noise/scale as ModelFrag's rigid dissolve (and ModelDissolveFrag's character one), so a caster's
// shadow holes match the holes punched in the caster itself. Keep the three in sync.
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
void main() {
    // Gated exactly like ModelFrag: threshold 0 writes depth unconditionally, so an instance carrying no dissolve
    // records the same depth this pipeline's plain sibling would.
    if (vDissolve.x > 0.0) {
        float threshold = clamp(vDissolve.x, 0.0, 1.0);
        float mask = dnoise(vWorldAbs * 6.0);
        if (mask < threshold) discard;            // dissolved away: no depth, so no shadow from this fragment
    }
    oDepth = vec4(vLightDepth, 0.0, 0.0, 1.0);
}";

        // Skinned shadow depth vertex (GPU skinning shadow-pass mirror). Reads ONE combined resource buffer at set 0
        // ({ LightMvp; bones[128] }, LightMvp = Model * clip-corrected LightViewProj folded per draw), skins the vertex
        // exactly like SkinnedModelVert, and projects into light-clip. The fragment is the shared ShadowDepthFrag (no
        // resources). Reuses the ShadowDepthFrag output. The unread per-vertex inputs (Normal/Color/TexCoord/Tangent)
        // are summed into a 1e-30 sink so SPIRV-Cross keeps the HLSL vertex-input signature gap-free (no FXC/WARP
        // miscompile - the same trap ShadowDepthVert documents).
        public const string SkinnedShadowDepthVert = @"#version 450
layout(set=0, binding=0) uniform VBlock {
    mat4 LightMvp;         // Model * clip-corrected LightViewProj (folded per draw)
    mat4 bones[128];       // this draw's composed palette (inverseBind*jointWorld)
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
    gl_Position = LightMvp * localPos;
    vLightDepth = gl_Position.z / gl_Position.w;
}";
    }
}
