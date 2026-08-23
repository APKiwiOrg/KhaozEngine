namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// Overlay, fullscreen and post-processing passes (14 of the renderer's shader sources).
    /// Part of the <see cref="ShaderSources"/> partial: see ShaderSources.cs for the shared contract
    /// (GLSL #version 450, cross-compiled at load via the GPU seam's SPIR-V path).
    /// </summary>
    internal static partial class ShaderSources
    {

        // ---- Translucent unlit overlay mesh (collision proxies, nav/AoI/chunk-bounds later). Drawn INTO the model
        //      MRT (still bound) after the beams and before the post chain, with the depth test on (less-equal, no
        //      write) so a proxy is occluded by nearer scene geometry but still blends over farther geometry. Colour
        //      comes straight from the mesh's per-vertex ModelVertex.Color (unlit), alpha via the blend. ONE dynamic
        //      UBO per draw carries BOTH the frame ViewProj and the per-draw World (a single 128-byte slot selected
        //      by a dynamic offset). This deliberately does NOT split ViewProj/World into two UBO bindings: Veldrid/
        //      SPIRV-Cross on Metal mis-bound a SECOND uniform buffer in a set (it read the first buffer's bytes -
        //      the same trap the splat/model passes fold around by using one UBO), so both matrices ride in one
        //      buffer. The vertex layout declares the full ModelVertex (locations 0..4) so the same GPU vertex buffer
        //      the model pass uses binds unchanged, and only Position (0) and Color (2) carry any meaning here (the
        //      rest are held live by the sink below). Writes all 3 MRT targets so the SPIR-V output count matches
        //      the framebuffer. Only colour matters (attachments 1 and 2 use a PreserveDestination blend, so the
        //      meshes' normal/depth reach the edge pass untouched).
        //
        //      D3D11/FXC/WARP HAZARD (load-bearing sink below): with only Position and Color read for real,
        //      SPIRV-Cross dropped Normal (1), TexCoord (3) and Tangent (4) and emitted a HOLED vertex input
        //      signature, TEXCOORD0 then TEXCOORD2. FXC and WARP miscompile a holed TEXCOORD sequence SILENTLY,
        //      which is the class of defect that blanked the model and splat passes' colour (see the note above
        //      ShadowDepthVert) and blew the terrain to flat white (see ShaderSources.Terrain.cs). The FXC
        //      validation gate's first Windows run (cross-platform-gpu run 30798302196) caught this one before it
        //      could corrupt a frame. The `sink` reads every declared input with a negligible live weight, so
        //      SPIRV-Cross keeps a CONTIGUOUS TEXCOORD0..4 signature matching the declared vertex layout. Do NOT
        //      remove the sink or any of its reads. ----
        public const string OverlayUnlitVert = @"#version 450
layout(set=0, binding=0) uniform Draw { mat4 ViewProj; mat4 World; };
layout(location=0) in vec3 Position;
layout(location=1) in vec3 Normal;
layout(location=2) in vec4 Color;
layout(location=3) in vec2 TexCoord;
layout(location=4) in vec4 Tangent;
layout(location=0) out vec4 vColor;
void main() {
    vec4 world = World * vec4(Position, 1.0);
    // Negligible-but-live sink over the otherwise-unread per-vertex inputs, the same one ShadowDepthVert carries,
    // so SPIRV-Cross keeps the HLSL vertex-input signature gap-free (TEXCOORD0..4, no hole) and FXC/WARP cannot
    // miscompile it. The sink is the input SUM (NOT statically zero, so the optimizer cannot fold it away and drop
    // the inputs) scaled by 1e-30, numerically negligible in world space, and the projected position is unchanged
    // to the bit. See the hazard note above.
    float sink = Normal.x + TexCoord.x + Tangent.x;
    world.x += sink * 1e-30;
    gl_Position = ViewProj * world;
    vColor = Color;
}";

        public const string OverlayUnlitFrag = @"#version 450
layout(location=0) in vec4 vColor;
layout(location=0) out vec4 oColor;
layout(location=1) out vec4 oNormal;
layout(location=2) out vec4 oDepth;
void main() {
    oColor = vColor;       // alpha via the AlphaBlend attachment on target 0
    oNormal = vec4(0.0);   // discarded (PreserveDestination blend on attachment 1)
    oDepth  = vec4(0.0);   // discarded (PreserveDestination blend on attachment 2)
}";

        // ---- Shared fullscreen triangle ----
        public const string FullscreenVert = @"#version 450
layout(location=0) out vec2 vUv;
void main() {
    vec2 p = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
    vUv = p;
    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}";

        // ---- Screen-space distortion apply: the FIRST post-chain pass (both modes). Re-samples the chain source
        // through the accumulated half-res offset field so refraction warps the scene BEFORE every camera-response
        // pass (bloom halos follow the warped sources, the retro path quantizes the warped image). Preserves each
        // pixel's OWN alpha so the background marker (transparent background) never warps (warping it would corrupt the blit's
        // marker semantics, D-S5). Only ever run when a distortion sprite was queued this frame
        // (RenderResources.DistortAllocated), so a distortion-free frame is byte-identical to before distortion existed.
        public const string DistortionApplyFrag = @"#version 450
layout(set=0, binding=0) uniform texture2D Src;        // the chain source (ColorTex on the first pass)
layout(set=0, binding=1) uniform texture2D OffsetTex;  // half/quarter-res R16G16Float signed UV offset field
layout(set=0, binding=2) uniform sampler Samp;         // linear: bilinear offset upsample + colour resample
layout(set=0, binding=3) uniform Apply { vec4 Params; }; // .x = strength->UV scale, .y = max UV excursion clamp, zw reserved
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
void main() {
    // Own tap first (binding order Src then OffsetTex, the Metal static-sample rule), keeping this pixel's own alpha
    // so the background marker survives the warp.
    vec4 own = texture(sampler2D(Src, Samp), vUv);
    vec2 offset = texture(sampler2D(OffsetTex, Samp), vUv).rg;   // bilinear half-res upsample
    // World-ish offset -> UV excursion, clamped so a hot mess of stacked sprites cannot smear the whole screen.
    vec2 duv = clamp(offset * Params.x, -vec2(Params.y), vec2(Params.y));
    // Keep the warped sample inside the viewport (half a texel in from each edge).
    ivec2 sz = textureSize(sampler2D(Src, Samp), 0);
    vec2 halfTexel = 0.5 / vec2(sz);
    vec2 wuv = clamp(vUv + duv, halfTexel, vec2(1.0) - halfTexel);
    vec3 warped = texture(sampler2D(Src, Samp), wuv).rgb;
    oColor = vec4(warped, own.a);
}";

        // ---- Palette quantize (+ optional Bayer dither) ----
        public const string PaletteFrag = @"#version 450
layout(set=0, binding=0) uniform texture2D Src;
layout(set=0, binding=1) uniform sampler Samp;
layout(set=0, binding=2) uniform Pal { vec4 Colors[64]; vec4 Info; }; // Info.x=count, .y=ditherOn
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
const float bayer[16] = float[16](
    0.0, 8.0, 2.0, 10.0, 12.0, 4.0, 14.0, 6.0, 3.0, 11.0, 1.0, 9.0, 15.0, 7.0, 13.0, 5.0);
void main() {
    vec4 src = texture(sampler2D(Src, Samp), vUv);
    vec3 c = src.rgb;
    if (Info.y > 0.5) {
        ivec2 px = ivec2(gl_FragCoord.xy);
        float th = (bayer[(px.y & 3) * 4 + (px.x & 3)] / 16.0 - 0.5);
        c += th * (1.0 / 8.0);
    }
    int count = int(Info.x);
    float best = 1e9; vec3 bestC = c;
    for (int i = 0; i < count; i++) {
        vec3 pc = Colors[i].rgb;
        float d = dot(c - pc, c - pc);
        if (d < best) { best = d; bestC = pc; }
    }
    oColor = vec4(bestC, src.a); // preserve background alpha marker
}";

        // ---- Depth/normal edge outline ----
        // Bug B fix: sample ColorTex, NormalTex, DepthTex UP FRONT in BINDING ORDER. On Metal SPIRV-Cross assigns
        // MSL texture indices by first-sample order, so sampling Depth before Normal (the old order) swapped the
        // two samplers and the normal-edge term silently read depth data (mirrors the ModelFrag Albedo/NormalMap/
        // Roughness fix; D3D11/Vulkan bind by explicit decoration and are order-insensitive).
        // Fix C: under perspective the stored z/w is non-linear, so a fixed threshold pops on zoom/distance.
        // Linearize to view-space eye distance (Thresh.zw = near/far) and compare a depth delta RELATIVE to view
        // depth. Orthographic (Texel.z == 0) keeps the original raw abs(d-d0) > Thresh.x test, byte-identical.
        // linearizeDepth mirrors OutlineMath.LinearizeDepth (keep in sync).
        public const string EdgeFrag = @"#version 450
layout(set=0, binding=0) uniform texture2D ColorTex;
layout(set=0, binding=1) uniform texture2D NormalTex;
layout(set=0, binding=2) uniform texture2D DepthTex;
layout(set=0, binding=3) uniform sampler Samp;
layout(set=0, binding=4) uniform Edge { vec4 OutlineColor; vec4 Texel; vec4 Thresh; vec4 Fade; };
// Texel.xy=1/size, .z=isPerspective, .w=distanceFadeOn; Thresh.x=depth, .y=normal, .z=near, .w=far;
// Fade.x=start, .y=end, .z=MRT-flip parity: the chain source (ColorTex or a ping) flips vertically once per
// preceding fullscreen pass, while NormalTex/DepthTex are raw MRT attachments that never do. When an odd
// number of chain passes ran before this one (Fade.z=1, computed CPU-side per mode), sample normal/depth at
// the V-flipped coordinate so the edge field stays aligned with the image it outlines.
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
float linearizeDepth(float d, float near, float far) { return (near * far) / (far - d * (far - near)); }
void main() {
    vec2 nuv = (Fade.z > 0.5) ? vec2(vUv.x, 1.0 - vUv.y) : vUv;
    // Up-front, in binding order (Color, Normal, Depth) - see Bug B note above.
    vec4 baseSrc = texture(sampler2D(ColorTex, Samp), vUv);
    vec3 base = baseSrc.rgb;
    vec3 n0 = texture(sampler2D(NormalTex, Samp), nuv).rgb * 2.0 - 1.0;
    float d0 = texture(sampler2D(DepthTex, Samp), nuv).r;

    bool persp = Texel.z > 0.5;
    float near = Thresh.z, far = Thresh.w;
    vec2 ex = vec2(Texel.x, 0.0), ey = vec2(0.0, Texel.y);

    // Four-neighbour samples (binding order preserved: Normal first, then Depth).
    vec3 nL = texture(sampler2D(NormalTex, Samp), nuv - ex).rgb * 2.0 - 1.0;
    vec3 nR = texture(sampler2D(NormalTex, Samp), nuv + ex).rgb * 2.0 - 1.0;
    vec3 nU = texture(sampler2D(NormalTex, Samp), nuv + ey).rgb * 2.0 - 1.0;
    vec3 nD = texture(sampler2D(NormalTex, Samp), nuv - ey).rgb * 2.0 - 1.0;
    float dL = texture(sampler2D(DepthTex, Samp), nuv - ex).r;
    float dR = texture(sampler2D(DepthTex, Samp), nuv + ex).r;
    float dU = texture(sampler2D(DepthTex, Samp), nuv + ey).r;
    float dD = texture(sampler2D(DepthTex, Samp), nuv - ey).r;

    float edge = 0.0;
    // Normal-crease edge: fire if ANY neighbour's geometric normal turns by more than the threshold. Flat
    // surfaces (constant normal) never fire; this catches interior creases the depth term misses (Bug B).
    if ((1.0 - dot(nL, n0)) > Thresh.y || (1.0 - dot(nR, n0)) > Thresh.y ||
        (1.0 - dot(nU, n0)) > Thresh.y || (1.0 - dot(nD, n0)) > Thresh.y) edge = 1.0;

    float lin0 = persp ? linearizeDepth(d0, near, far) : d0;
    if (persp) {
        // Perspective depth edge: a SECOND difference (Laplacian) of view-space depth, relative to depth. It is
        // ~0 on smooth surfaces - including a steep grazing ground plane (constant slope => zero curvature), which
        // a first difference floods - and spikes at silhouettes / occlusion steps. Stable at any zoom.
        float lL = linearizeDepth(dL, near, far), lR = linearizeDepth(dR, near, far);
        float lU = linearizeDepth(dU, near, far), lD = linearizeDepth(dD, near, far);
        float lap = max(abs(lL + lR - 2.0 * lin0), abs(lU + lD - 2.0 * lin0));
        if (lap > Thresh.x * lin0) edge = 1.0;
        if (Texel.w > 0.5) edge *= 1.0 - smoothstep(Fade.x, Fade.y, lin0);   // optional distance fade (default off)
    } else {
        // Ortho: raw linear z/w, per-neighbour first difference (UNCHANGED, byte-identical to the original loop).
        if (abs(dL - d0) > Thresh.x || abs(dR - d0) > Thresh.x ||
            abs(dU - d0) > Thresh.x || abs(dD - d0) > Thresh.x) edge = 1.0;
    }

    oColor = vec4(mix(base, OutlineColor.rgb, edge), baseSrc.a); // preserve background alpha marker
}";

        // ---- Final upscale blit ----
        // Background is flagged by the color target's alpha (model + the sky/starfield background passes write a=1,
        // the clear sets a=0), which the palette/edge passes preserve. The starfield USED to be injected here, but a
        // pass that rebuilds the background AFTER the whole chain necessarily discards whatever was drawn at those
        // pixels, so it moved to StarfieldRenderer (a real background pass, before the decals). Keeps the blit to a
        // safe 3-binding set (the depth texture in here tripped a backend/Metal multi-resource binding bug).
        public const string BlitFrag = @"#version 450
layout(set=0, binding=0) uniform texture2D Src;
layout(set=0, binding=1) uniform sampler Samp;
layout(set=0, binding=2) uniform Final { vec4 Params; }; // Params.x=transparentBg, .y=flipV
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
void main() {
    // Bug A: each fullscreen post pass flips vertically, so the orientation depends on the parity of how many
    // ran. The blit cancels it (Params.y = flipV) so every config is upright.
    vec2 suv = (Params.y > 0.5) ? vec2(vUv.x, 1.0 - vUv.y) : vUv;
    vec4 s = texture(sampler2D(Src, Samp), suv);
    // Opaque on-screen by default; for an offscreen preview (Params.x) keep the alpha marker so the cleared
    // background composites transparently (geometry a=1 stays opaque, cleared background a=0 stays clear).
    float outA = (Params.x > 0.5) ? s.a : 1.0;
    oColor = vec4(s.rgb, outA);
}";

        // ---- Teleport transition: solid fullscreen fill (HardBlink) ----
        // A fullscreen quad of Fill.rgb at opacity Fill.a, drawn OVER the final image with standard src-alpha blend
        // (so result = fill*a + dst*(1-a)). Orientation-independent (no texture sample). Only ever drawn when a
        // transition is active with Cover > 0, so a frame with no active transition is byte-identical to before.
        public const string TransitionSolidFrag = @"#version 450
layout(set=0, binding=0) uniform Fill { vec4 ColorAlpha; }; // rgb = fill colour, a = opacity (the transition's Cover)
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
void main() { oColor = ColorAlpha; }";

        // ---- Teleport transition: frozen-frame crossfade (CameraDissolve) ----
        // Samples the captured pre-teleport frame (a raw copy of the resolved ColorTex) and draws it OVER the live
        // final image at opacity Params.x (the frozen weight = Cover), with src-alpha blend: result = frozen*Cover +
        // live*(1-Cover). The V flip is constant: the frozen texture is a ColorTex copy, and a fullscreen pass reading
        // ColorTex needs the same single flip the final blit applies for a ColorTex source (BlitFrag's even-parity
        // flipV=1 case), so it lands upright over the already-upright target. Only drawn while active, so inactive
        // frames stay byte-identical.
        public const string TransitionCrossfadeFrag = @"#version 450
layout(set=0, binding=0) uniform texture2D Src;
layout(set=0, binding=1) uniform sampler Samp;
layout(set=0, binding=2) uniform Params { vec4 P; }; // P.x = frozen-frame opacity (the transition's Cover)
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
void main() {
    vec2 suv = vec2(vUv.x, 1.0 - vUv.y);   // match the blit's ColorTex flip so the frozen frame aligns with the target
    vec4 s = texture(sampler2D(Src, Samp), suv);
    oColor = vec4(s.rgb, P.x);
}";

        // ---- HDR tonemap: map the float16 over-range scene colour to LDR [0,1] before the retro/AA passes.
        // Runs ONLY in HDR mode, directly after the (pre-tonemap) bloom composite. Preserves the source
        // alpha untouched so the blit's background marker (alpha < 0.5 for transparent background) survives.
        // Operator fit choices are pure ALU (no LUT) so cross-backend goldens stay stable.
        public const string TonemapFrag = @"#version 450
layout(set=0, binding=0) uniform texture2D Src;
layout(set=0, binding=1) uniform sampler Samp;
layout(set=0, binding=2) uniform Tone { vec4 Params; }; // Params.x = exposure, .y = operator (0 aces, 1 reinhard, 2 clamp), .z = ChromaPreservation (0..1)
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
// Rec.601 luma, matching the local luma() the rest of the post chain (FxaaFrag/bloom) uses.
float luma(vec3 c) { return dot(c, vec3(0.299, 0.587, 0.114)); }
// ACES filmic fit (Krzysztof Narkowicz 2015): filmic S-curve with highlight desaturation toward white.
vec3 acesFilm(vec3 x) {
    return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0);
}
float acesFilm(float x) {
    return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0);
}
// Mirrors KhaozEngine.Render3D.Internal.TonemapMath (keep the curve dispatch, luma, rescale, mix, and the
// factor-0 short-circuit in sync). This is the engine's most-shipped pixel: at Params.z == 0 the output must
// stay byte-identical to the pre-chroma tonemap, which the Metal golden gate proves on real hardware.
void main() {
    vec4 s = texture(sampler2D(Src, Samp), vUv);
    vec3 c = max(s.rgb, vec3(0.0)) * Params.x;
    int op = int(Params.y + 0.5);
    // Per-channel operator: the historical look. An over-range core desaturates toward white as its
    // brightest channel saturates first.
    vec3 perChannel;
    if (op == 0) perChannel = acesFilm(c);
    else if (op == 1) perChannel = c / (vec3(1.0) + c);
    else perChannel = clamp(c, 0.0, 1.0);
    // Params.z == 0 short-circuits to the EXACT per-channel expression above (a uniform branch, no divergence)
    // so the default output carries no blend re-association and stays byte-identical.
    if (Params.z <= 0.0) {
        oColor = vec4(perChannel, s.a);
        return;
    }
    // Hue-preserving path: map luminance through the same operator, then rescale RGB by mappedLuma / luma so
    // only brightness rolls off and the chromaticity (hue + saturation direction) is held.
    float l = luma(c);
    float lm;
    if (op == 0) lm = acesFilm(l);
    else if (op == 1) lm = l / (1.0 + l);
    else lm = clamp(l, 0.0, 1.0);
    vec3 huePreserving = c * (lm / max(l, 1e-5));
    vec3 mapped = clamp(mix(perChannel, huePreserving, Params.z), 0.0, 1.0);
    oColor = vec4(mapped, s.a);
}";

        // ---- FXAA (fast approximate anti-aliasing) ----
        // The classic Timothy Lottes FXAA3-console pass: read a 3x3 luma neighbourhood, skip near-flat areas
        // (contrast gate), otherwise estimate the edge direction from the luma gradient and blend two/four taps along
        // it. Softens high-contrast edges (geometry silhouettes AND shaded interiors) in one cheap fullscreen pass.
        // Preserves the CENTRE pixel's alpha so the blit's background marker (a < 0.5 for transparent background) still
        // works. Runs on the internal target BEFORE the blit; like the other post passes it flips V, so the blit's
        // flipV parity counts it. Rcp.xy = 1/targetSize.
        public const string FxaaFrag = @"#version 450
layout(set=0, binding=0) uniform texture2D Src;
layout(set=0, binding=1) uniform sampler Samp;
layout(set=0, binding=2) uniform Fxaa { vec4 Rcp; }; // .xy = 1/size
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
float luma(vec3 c) { return dot(c, vec3(0.299, 0.587, 0.114)); }
void main() {
    vec2 inv = Rcp.xy;
    vec4 M = texture(sampler2D(Src, Samp), vUv);
    vec3 rgbM = M.rgb;
    vec3 rgbNW = texture(sampler2D(Src, Samp), vUv + vec2(-1.0, -1.0) * inv).rgb;
    vec3 rgbNE = texture(sampler2D(Src, Samp), vUv + vec2( 1.0, -1.0) * inv).rgb;
    vec3 rgbSW = texture(sampler2D(Src, Samp), vUv + vec2(-1.0,  1.0) * inv).rgb;
    vec3 rgbSE = texture(sampler2D(Src, Samp), vUv + vec2( 1.0,  1.0) * inv).rgb;
    float lM = luma(rgbM), lNW = luma(rgbNW), lNE = luma(rgbNE), lSW = luma(rgbSW), lSE = luma(rgbSE);
    float lMin = min(lM, min(min(lNW, lNE), min(lSW, lSE)));
    float lMax = max(lM, max(max(lNW, lNE), max(lSW, lSE)));
    float range = lMax - lMin;
    // Contrast gate: leave near-flat regions untouched (also keeps flat interiors crisp and cheap).
    if (range < max(0.0312, lMax * 0.125)) { oColor = vec4(rgbM, M.a); return; }
    vec2 dir;
    dir.x = -((lNW + lNE) - (lSW + lSE));
    dir.y =  ((lNW + lSW) - (lNE + lSE));
    float dirReduce = max((lNW + lNE + lSW + lSE) * (0.25 * 0.125), 1.0 / 128.0);
    float rcpDirMin = 1.0 / (min(abs(dir.x), abs(dir.y)) + dirReduce);
    dir = clamp(dir * rcpDirMin, vec2(-8.0), vec2(8.0)) * inv;
    vec3 rgbA = 0.5 * (texture(sampler2D(Src, Samp), vUv + dir * (1.0 / 3.0 - 0.5)).rgb
                     + texture(sampler2D(Src, Samp), vUv + dir * (2.0 / 3.0 - 0.5)).rgb);
    vec3 rgbB = rgbA * 0.5 + 0.25 * (texture(sampler2D(Src, Samp), vUv + dir * -0.5).rgb
                                   + texture(sampler2D(Src, Samp), vUv + dir *  0.5).rgb);
    float lB = luma(rgbB);
    vec3 outRgb = (lB < lMin || lB > lMax) ? rgbA : rgbB;   // reject an over-blurred tap
    oColor = vec4(outRgb, M.a);
}";

        // ---- Bloom: bright-pass -> separable gaussian blur (H then V) -> additive composite. LDR (R8G8B8A8UNorm
        // internal target, no HDR headroom), so the bright-pass thresholds the already-tonemapped-to-[0,1] lit
        // colour rather than an over-1.0 linear value - see BloomSettings' LDR-not-HDR doc note. kneeWeight mirrors
        // BloomMath.KneeWeight EXACTLY (keep in sync); the gaussian blur's weights are uploaded from
        // BloomMath.GaussianWeights so the shader never re-derives sigma. All three passes reuse FullscreenVert (a
        // fullscreen vUv in [0,1] independent of the target's resolution), so the bright-pass and blur passes -
        // which render into the HALF-RES bloom targets - need no extra scaling: the vertex shader, the framebuffer
        // viewport, and Src's sampling all resolve in the destination's own resolution.

        // ---- Bloom bright-pass: threshold the full-res lit colour into the half-res bright target ----
        public const string BloomBrightFrag = @"#version 450
layout(set=0, binding=0) uniform texture2D Src;
layout(set=0, binding=1) uniform sampler Samp;
layout(set=0, binding=2) uniform Bright { vec4 Params; }; // Params.x=threshold, .y=knee
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
float luma(vec3 c) { return dot(c, vec3(0.299, 0.587, 0.114)); }
// Mirrors BloomMath.KneeWeight exactly: knee<=0 is a hard threshold; else a smoothstep ramp of half-width `knee`
// centred on `threshold`.
float kneeWeight(float l, float threshold, float knee) {
    if (knee <= 0.0) return l >= threshold ? 1.0 : 0.0;
    float lo = threshold - knee;
    float hi = threshold + knee;
    float t = clamp((l - lo) / max(hi - lo, 1e-5), 0.0, 1.0);
    return t * t * (3.0 - 2.0 * t);
}
void main() {
    vec3 c = texture(sampler2D(Src, Samp), vUv).rgb;
    float w = kneeWeight(luma(c), Params.x, Params.y);
    oColor = vec4(c * w, 1.0);
}";

        // ---- Bloom separable gaussian blur: one axis per draw (horizontal pass writes to BloomB, vertical pass
        // reads BloomB and writes back to BloomA), radius taps per side (MaxRadius clamps the array). Weights[i].x
        // holds the 1D weight for offset i (0 = the centre tap); the shader mirrors the tap loop against a runtime
        // tap count (2*radius+1) so a radius smaller than MaxRadius just leaves the tail weights unused (0-init from
        // the C# array is never assumed - the loop bound (Params.x) is authoritative). ----
        public const string BloomBlurFrag = @"#version 450
layout(set=0, binding=0) uniform texture2D Src;
layout(set=0, binding=1) uniform sampler Samp;
layout(set=0, binding=2) uniform Blur {
    vec4 Texel;              // .xy = 1/halfResSize
    vec4 Params;             // .x = radius (taps per side), .y = dirX (texel units), .z = dirY (texel units)
    vec4 Weights[9];         // Weights[i].x = 1D gaussian weight for tap i (i=0..radius); MaxRadius=8 => 9 slots
};
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
void main() {
    int radius = int(Params.x);
    vec2 dir = vec2(Params.y, Params.z) * Texel.xy;
    vec3 sum = texture(sampler2D(Src, Samp), vUv).rgb * Weights[0].x;
    for (int i = 1; i <= radius; i++) {
        float w = Weights[i].x;
        vec2 o = dir * float(i);
        sum += texture(sampler2D(Src, Samp), vUv + o).rgb * w;
        sum += texture(sampler2D(Src, Samp), vUv - o).rgb * w;
    }
    oColor = vec4(sum, 1.0);
}";

        // ---- Bloom composite: additively blend the blurred (half-res, bilinear-upsampled by the sampler) bright
        // target onto the full-res colour chain. Sampled UP FRONT in binding order (Src, Bloom - see the Metal
        // first-sample-order rule in EdgeFrag/ModelFrag). Preserves Src's alpha UNCHANGED so the blit's background
        // marker (alpha < 0.5 for transparent background) is untouched - bloom must never resurrect an
        // alpha-0 background pixel into an opaque one; adding a near-zero (thresholded-out) bloom colour to the
        // background also does not visibly brighten it in practice, since nothing exceeds the bright-pass threshold
        // there.
        // Bug A (the same vertical-flip parity Run/BlitFrag already correct for): every fullscreen pass flips the
        // image vertically relative to its input. The bloom branch is ALWAYS exactly 3 fullscreen passes removed
        // from Src at this point (bright-pass + blur-H + blur-V, independent of how many main-chain passes ran
        // before it) - an ODD number - so Bloom is always flipped by exactly one flip relative to Src, regardless of
        // which optional main-chain passes (quantize/outline) ran. Un-flip Bloom's V unconditionally (a fixed,
        // settings-independent correction) rather than threading a parity flag through like BlitFrag's Params.z. ----
        public const string BloomCompositeFrag = @"#version 450
layout(set=0, binding=0) uniform texture2D Src;
layout(set=0, binding=1) uniform texture2D Bloom;
layout(set=0, binding=2) uniform sampler Samp;
layout(set=0, binding=3) uniform Composite { vec4 Params; }; // Params.x = intensity
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
void main() {
    vec4 src = texture(sampler2D(Src, Samp), vUv);
    vec3 bloom = texture(sampler2D(Bloom, Samp), vec2(vUv.x, 1.0 - vUv.y)).rgb;
    oColor = vec4(src.rgb + bloom * Params.x, src.a);
}";
    }
}
