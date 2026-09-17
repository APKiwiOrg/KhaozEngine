namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// The omnidirectional POINT-light shadow pass's shader sources: three caster vertex/fragment pairs mirroring
    /// the key light's three, plus the per-row clear quad. Part of the <see cref="ShaderSources"/> partial: see
    /// ShaderSources.cs for the shared contract (GLSL #version 450, cross-compiled at load via the GPU seam's
    /// SPIR-V path).
    /// </summary>
    internal static partial class ShaderSources
    {
        // ---- Point-light depth pass. The same instanced caster draw the key light's pass runs (same vertex
        //      stream, same instance stream, same R32F single-target write), with two things changed.
        //
        //      ONE: the matrix is a 90 degree PERSPECTIVE for one cube face of one light, with the atlas cell
        //      already baked into it (PointShadowMath.FaceViewProjection), so there is no viewport and a per-cell
        //      scissor clips the overflow, exactly as the cascade columns do.
        //
        //      TWO, AND THIS IS THE ONE THAT MATTERS: the stored value is a LINEAR DISTANCE OVER THE LIGHT RADIUS,
        //      not the projected clip depth (design decision 2). The receiver then needs no matrices at all: it
        //      selects the face from the light-to-fragment vector, reads the cell and compares against its own
        //      normalized distance, so no backend's depth convention ever enters the compare and no second matrix
        //      has to be kept in step with this one. The hardware depth buffer still carries clip depth, because
        //      that is what resolves the nearest surface when two casters cover one texel.
        //
        //      There is no near-plane pancake here and there must not be one. The cascade pass clamps a caster in
        //      front of the light's near plane to the near plane because a DIRECTIONAL caster up-light of the near
        //      plane shadows the whole depth range below it. A point light's near plane is 5 cm from the bulb, and
        //      geometry closer than that is behind the fixture rather than in front of the scene, so clipping it is
        //      correct.
        //
        //      D3D11/FXC/WARP HAZARD: the same one ShadowDepthVert documents at length. This vertex reads only
        //      Position and IModel0..3, so SPIRV-Cross would drop the unread inputs and leave a HOLE in the HLSL
        //      vertex-input signature, which FXC/WARP miscompiles. The 1e-30 `sink` reads every declared input so
        //      the signature stays contiguous. Do NOT drop it. ----
        const string PointShadowUniformBlock = @"layout(set=0, binding=0) uniform U {
    mat4 LightVp;             // world (render space) -> this face's atlas cell, clip-corrected by the caller
    vec4 LightPosRadius;      // xyz = light position in render space, w = light radius (the far plane)
    vec4 Noise;               // x = this light's dissolve noise scale, yzw = this frame's render origin
};
";

        public const string PointShadowRigidVert = @"#version 450
" + PointShadowUniformBlock + @"layout(location=0) in vec3 Position;
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
layout(location=0) out vec3 vWorldPos;
void main() {
    mat4 Model = mat4(IModel0, IModel1, IModel2, IModel3);
    vec4 world = Model * vec4(Position, 1.0);
    // Negligible-but-live sink over the otherwise-unread inputs, so SPIRV-Cross keeps the HLSL vertex-input
    // signature gap-free (TEXCOORD0..11, no hole) - see the note above and ShaderSources.Shadow.cs.
    float sink = Normal.x + Color.x + TexCoord.x + Tangent.x + ITint.x + IEmissive.x + ISpecParams.x;
    world.x += sink * 1e-30;
    vWorldPos = world.xyz;
    gl_Position = LightVp * world;
}";

        // The caster fragments' shared distance write. The clamp keeps a caster just past the far plane (which the
        // rasterizer would have clipped anyway) from storing above the cleared 1.0 and reading as "nothing here".
        const string PointShadowDistanceWrite =
            @"    float dist = length(vWorldPos - LightPosRadius.xyz) / max(LightPosRadius.w, 1e-4);
    oDist = vec4(clamp(dist, 0.0, 1.0), 0.0, 0.0, 1.0);";

        public const string PointShadowRigidFrag = @"#version 450
" + PointShadowUniformBlock + @"layout(location=0) in vec3 vWorldPos;
layout(location=0) out vec4 oDist;            // single R32F target: .r carries distance / radius
void main() {
" + PointShadowDistanceWrite + @"
}";

        // ---- Dissolve-aware point-light depth pass. The sibling of ShadowDepthDissolveVert/Frag, and it exists for
        //      the same reason: a caster fading out under the rigid dissolve must thin its SHADOW with the same
        //      noise mask that thins the mesh, instead of staying solid until the hard cull.
        //
        //      The noise is evaluated in ABSOLUTE world space (the pattern is world-anchored, so a camera-relative
        //      one would re-roll on every render-origin rebase), which is why the block carries this frame's render
        //      origin beside the light. The SCALE is the light's own rather than the colour pass's base, for the
        //      cascade pass's reason one geometry over: a noise cell smaller than an atlas texel stops being a
        //      dither (see ShadowDissolveNoise.ScaleForCascade, which this pass feeds the light radius and the face
        //      resolution). ----
        public const string PointShadowRigidDissolveVert = @"#version 450
" + PointShadowUniformBlock + @"layout(location=0) in vec3 Position;
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
layout(location=13) in vec2 IDissolve;            // x = threshold (0 = solid .. 1 = gone), y = edge width (unused)
layout(location=14) in float IDissolveComplement; // 0 = ordinary keep set, 1 = exact complement for a LOD handoff
layout(location=0) out vec3 vWorldPos;
layout(location=1) out vec3 vNoisePos;            // ABSOLUTE world position pre-scaled by this light's noise scale
layout(location=2) out vec2 vDissolve;
layout(location=3) out float vDissolveComplement;
void main() {
    mat4 Model = mat4(IModel0, IModel1, IModel2, IModel3);
    vec4 world = Model * vec4(Position, 1.0);
    float sink = Normal.x + Color.x + TexCoord.x + Tangent.x + ITint.x + IEmissive.x + ISpecParams.x + IDynamic;
    world.x += sink * 1e-30;
    vWorldPos = world.xyz;
    gl_Position = LightVp * world;
    vNoisePos = (world.xyz + Noise.yzw) * Noise.x;
    vDissolve = IDissolve;
    vDissolveComplement = IDissolveComplement;
}";

        // The dissolve fragments' shared prologue: the interpolants plus the SAME hash/noise as ModelFrag's rigid
        // dissolve and ShadowDepthDissolveFrag's, so a caster's point-light shadow holes match the holes punched in
        // the caster itself and in its sun shadow. Keep the three in sync.
        const string PointShadowDissolveFragPrologue = @"#version 450
" + PointShadowUniformBlock + @"layout(location=0) in vec3 vWorldPos;
layout(location=1) in vec3 vNoisePos;
layout(location=2) in vec2 vDissolve;
layout(location=3) in float vDissolveComplement;
layout(location=0) out vec4 oDist;
float pdhash(vec3 p) { return fract(sin(dot(p, vec3(12.9898, 78.233, 37.719))) * 43758.5453); }
float pdnoise(vec3 p) {
    vec3 i = floor(p); vec3 f = fract(p); f = f * f * (3.0 - 2.0 * f);
    float n000 = pdhash(i + vec3(0,0,0)), n100 = pdhash(i + vec3(1,0,0));
    float n010 = pdhash(i + vec3(0,1,0)), n110 = pdhash(i + vec3(1,1,0));
    float n001 = pdhash(i + vec3(0,0,1)), n101 = pdhash(i + vec3(1,0,1));
    float n011 = pdhash(i + vec3(0,1,1)), n111 = pdhash(i + vec3(1,1,1));
    return mix(mix(mix(n000, n100, f.x), mix(n010, n110, f.x), f.y),
               mix(mix(n001, n101, f.x), mix(n011, n111, f.x), f.y), f.z);
}
";

        public const string PointShadowRigidDissolveFrag = PointShadowDissolveFragPrologue + @"
void main() {
    // Gated exactly like ModelFrag: threshold 0 writes unconditionally, so an instance carrying no dissolve
    // records the same distance this pipeline's plain sibling would.
    if (vDissolve.x > 0.0 || vDissolveComplement > 0.5) {
        float threshold = clamp(vDissolve.x, 0.0, 1.0);
        float mask = pdnoise(vNoisePos);
        bool keep = vDissolveComplement > 0.5 ? mask < threshold : mask >= threshold;
        if (!keep) discard;
    }
" + PointShadowDistanceWrite + @"
}";

        // ---- INVERTED dissolve point-light depth fragment. Identical to the one above except that it keeps exactly
        //      what that one discards, so the two halves of an HLOD crossfade cover the noise mask between them
        //      instead of nesting. The derivation is the same as ShadowDepthDissolveInvertedFrag's, which documents
        //      it in full. ----
        public const string PointShadowRigidDissolveInvertedFrag = PointShadowDissolveFragPrologue + @"
void main() {
    if (vDissolve.x > 0.0) {
        float threshold = clamp(vDissolve.x, 0.0, 1.0);
        float mask = pdnoise(vNoisePos);
        if (mask >= 1.0 - threshold) discard;     // keep the complement of the plain half's keep-set
    }
" + PointShadowDistanceWrite + @"
}";

        // ---- The per-row CLEAR (design decision 7). One light row is cleared by drawing a fullscreen triangle at
        //      z = 1 under the row's scissor, through a depth-ALWAYS depth-writing pipeline, rather than by
        //      ClearColorTarget. The reason is portability: whether a clear honours the scissor rect differs per
        //      backend, and a scissored draw does not. It resets both the stored distance (to the cleared 1.0, which
        //      reads as "no caster anywhere on this ray") and the row's depth, so the casters that follow resolve
        //      nearest-first against a clean buffer.
        //
        //      The whole atlas is cleared once, by an ordinary whole-framebuffer clear, when the pass first binds
        //      it. That is not a per-row clear and no scissor is in force for it, so decision 7 does not reach it
        //      and a row nobody has rendered still reads 1.0.
        //
        //      The uniform block is declared and sunk rather than omitted: the pipeline's declared resource layout
        //      is the caster layout (one buffer, one set, one pipeline family), and a shader that declared none
        //      would leave the compiled binding set and the declared one disagreeing on some backends. The sink is
        //      1e-30 of three floats added to the clip position, which cannot move a vertex off a corner. ----
        public const string PointShadowClearVert = @"#version 450
" + PointShadowUniformBlock + @"void main() {
    vec2 p = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
    float sink = (LightVp[0][0] + LightPosRadius.x + Noise.x) * 1e-30;
    gl_Position = vec4(p * 2.0 - 1.0 + vec2(sink), 1.0, 1.0);
}";

        public const string PointShadowClearFrag = @"#version 450
layout(location=0) out vec4 oDist;
void main() {
    oDist = vec4(1.0);                            // 1.0 = nothing on this ray inside the light radius
}";
    }
}
