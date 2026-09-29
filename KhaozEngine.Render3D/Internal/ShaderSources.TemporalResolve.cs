namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// The temporal resolve's fused entry point and its depth store (TEMPORAL-RESOLVE-UPSCALING-DESIGN section 3), two
    /// fullscreen fragment programs paired with <see cref="FullscreenVert"/> and recorded by
    /// <c>TemporalResolveRenderer</c>.
    /// <para><b>EVERY RULE LIVES ONCE.</b> The rules are GLSL functions in two shared blocks: the per-texel
    /// preparation (<see cref="TemporalPrepareGlsl"/>) and the per-pixel accumulation
    /// (<see cref="TemporalAccumulateGlsl"/>). The fused entry point here prepares its 3x3 and its centre texel inline
    /// and accumulates in one pass. The split entry point (<see cref="TemporalPrepareFrag"/> and
    /// <see cref="TemporalAccumulateFrag"/>) runs the same functions in two.</para>
    /// <para><b>ORIENTATION.</b> Both address pixels by <c>gl_FragCoord</c>, which is upper-left on every backend (the
    /// convention <c>DecalFrag</c> and <c>StarfieldFrag</c> rely on), and read every input by <c>texelFetch</c> or at the
    /// UV of a pixel centre. So neither flips: the resolve's output is upright like <c>ColorTex</c>, and the post
    /// chain's flip parities stay what they were.</para>
    /// <para><b>THE CORE IS SHARED.</b> <see cref="TemporalResolveCoreGlsl"/> holds the fused entry point's bindings,
    /// the uniform block, the rules and the per-pixel resolve as a function with no stage inputs or outputs, so the
    /// temporal debug views (<see cref="TemporalDebugFrag"/>) and the sampled temporal counts
    /// (<see cref="TemporalProbeFrag"/>) can re-evaluate exactly what the resolve decided, through the resource set it
    /// bound.</para>
    /// <para><b>THE UNIFORM BLOCKS ARE THE C# STRUCTS.</b> Each block's members are spliced from
    /// <see cref="TemporalResolveUniforms.GlslMembers"/> and <see cref="TemporalDepthStoreUniforms.GlslMembers"/>, so the
    /// text cannot drift from the fields, and the fields carry the meaning.</para>
    /// <para><b>A MOVING SURFACE SKIPS THE DEPTH TEST.</b> <see cref="TemporalResolveUniforms.CurrentToPrevious"/> maps a
    /// static point, so the expected depth is only meaningful where the motion of the texel that reprojects the pixel,
    /// the dilated nearest or, beside a fast edge, the centre texel, carries its own sample within
    /// <see cref="TemporalResolveTuning.MovingSurfaceInternalPixels"/>, plus
    /// <see cref="TemporalResolveTuning.MovingSurfaceMotionFraction"/> of that motion, of that sample's static previous
    /// UV. The motion target wrote the motion for exactly that surface point, so a static surface agrees up to float
    /// precision and the target's half-float rounding. Elsewhere the surface moved and neighbourhood clipping handles
    /// it. At a moving edge the stored depths are still read, and there they decide only whether the thin-feature lock
    /// is kept. The motion target never reads as background off screen: an x channel past
    /// <see cref="TemporalResolveTuning.MotionSentinel"/> is background, and a previous UV outside [0, 1], which every
    /// clamped or behind-the-camera motion gives, rejects history as off screen.</para>
    /// <para><b>D3D11 SIGNATURE.</b> Each <c>main</c> reads <c>vUv</c> with a <c>1e-30</c> weight, which changes no
    /// output, so the pixel-input signature is TEXCOORD0 then SV_Position, the shape <c>PaletteFrag</c> ships on every
    /// backend, rather than SV_Position alone after a vertex stage that writes TEXCOORD0.</para>
    /// <para>The constants mirror <see cref="TemporalResolveTuning"/> one for one, and the helpers mirror
    /// <see cref="TemporalResolveMath"/>. Change both together.</para>
    /// </summary>
    internal static partial class ShaderSources
    {
        // ---- Shared by every temporal program: the background sentinel, the depth linearisation, the view depth ----
        internal const string TemporalCommonGlsl = @"
const float MotionSentinel = 60000.0;
const float BackgroundLinearDepth = 1.0e30;
// The largest finite half float. A history texel outside it is not finite, and a scene colour is held inside it before
// the luma weighting, where an infinity would become NaN.
const float HalfMax = 65504.0;
// View distance from NDC depth: perspective as OutlineMath.LinearizeDepth, orthographic linear between near and far.
// depthParams = (1 for perspective else 0, near, far, 0).
float temporalLinearDepth(float ndcDepth, vec4 depthParams) {
    return depthParams.x > 0.5
        ? (depthParams.y * depthParams.z) / (depthParams.z - ndcDepth * (depthParams.z - depthParams.y))
        : depthParams.y + ndcDepth * (depthParams.z - depthParams.y);
}
// A texel's view distance from its motion and its NDC depth. Background is read from the motion sentinel on the x
// channel alone, because the depth attachment is cleared to the background colour, not to the far plane. Both the
// resolve's and the depth store's uniforms name their depth parameters CurrentDepth.
float temporalViewDepth(vec2 motion, float ndcDepth) {
    return abs(motion.x) > MotionSentinel ? BackgroundLinearDepth : temporalLinearDepth(ndcDepth, CurrentDepth);
}
";

        // ---- The resolve's tuning constants, one for one with TemporalResolveTuning ----
        internal const string TemporalResolveTuningGlsl = @"
const float DisocclusionTolerance = 0.02;
const float MovingSurfaceInternalPixels = 0.5;
const float MovingSurfaceMotionFraction = 0.0009765625;
const float MaxAccumulation = 15.0;
const float MovingAccumulation = 8.0;
const float MotionAccumulationPixels = 16.0;
const float GammaStill = 1.5;
const float GammaMoving = 0.75;
const float GammaMotionPixels = 3.0;
const float ReactiveGain = 4.0;
const float ReactiveStrength = 0.9;
const float LockRidgeAbsolute = 0.02;
const float LockRidgeRelative = 0.1;
const float LockMotionStartPixels = 1.0;
const float LockMotionEndPixels = 4.0;
const float LockReactiveRelease = 2.0;
const float LockDecay = 0.125;
const float LockHoldGain = 2.0;
const float LockEdgeRelease = 1.0;
const float LockEdgeMotionFraction = 0.001953125;
const float LockEdgeFloorInternalPixels = 0.001;
const float ClipFlagMinimumMove = 0.0009765625;
const float DilationReachInternalPixels = 1.25;
const float WorldMotionMetres = 0.001;
const float FollowedTravelRatio = 2.0;
const float FollowedHistoryMotionFraction = 0.5;
const float FollowedStillDisplayPixels = 0.1;
const float DisocclusionVisibleShare = 0.5;
const float MovingShareConfidence = 0.0;
const float DisplayKernelConfidenceStart = 0.5;
const float DisplayKernelMotionPixels = 2.0;
const float DisplayKernelFullWeight = 0.25;
";

        // ---- The fused entry point's core: its bindings, the uniforms, the per-texel preparation and per-pixel
        //      accumulation, and the per-pixel resolve that prepares its 3x3 inline, no stage inputs or outputs ----
        public const string TemporalResolveCoreGlsl = @"
layout(set=0, binding=0) uniform texture2D SceneColor;
layout(set=0, binding=1) uniform texture2D OpaqueColor;
layout(set=0, binding=2) uniform texture2D SceneDepth;
layout(set=0, binding=3) uniform texture2D MotionTex;
layout(set=0, binding=4) uniform texture2D PrevDepth;
layout(set=0, binding=5) uniform texture2D HistoryColor;
layout(set=0, binding=6) uniform texture2D HistoryConfidence;
layout(set=0, binding=7) uniform sampler LinearClamp;
// The members are TemporalResolveUniforms.GlslMembers, documented on the struct's fields.
layout(set=0, binding=8) uniform Resolve {" + TemporalResolveUniforms.GlslMembers + @"};
" + TemporalCommonGlsl + TemporalResolveTuningGlsl + TemporalNarrowBothGlsl + TemporalPrepareGlsl
            + TemporalAccumulateGlsl + @"
// The fused entry point: one display pixel, its 3x3 read from the scene's inputs and prepared inline
// (temporalWeighted, temporalReactiveDifference, temporalDilate), gathered (temporalGather), its centre texel's
// surface prepared (temporalPrepareSurface), and the accumulation (temporalAccumulatePixel).
TemporalPixel temporalResolvePixel(ivec2 displayPixel) {
    vec2 uv;
    vec2 pixelCentre;
    ivec2 maxTexel;
    ivec2 centreTexel;
    temporalPixelSite(displayPixel, uv, pixelCentre, maxTexel, centreTexel);
    TemporalKernels kernels = temporalKernels(pixelCentre, centreTexel, maxTexel);
    TemporalNeighbourhood neighbourhood = temporalNeighbourhood();
    float closestDepth = 2.0 * BackgroundLinearDepth;
    ivec2 closestTexel = centreTexel;
    afloat lumas[9];

    ivec2 nextTexel = temporalFirstTexel(centreTexel, maxTexel);
    vec4 nextScene = texelFetch(sampler2D(SceneColor, LinearClamp), nextTexel, 0);
    vec2 nextMotion = texelFetch(sampler2D(MotionTex, LinearClamp), nextTexel, 0).rg;
    float nextDepth = texelFetch(sampler2D(SceneDepth, LinearClamp), nextTexel, 0).r;
    vec3 nextOpaque = texelFetch(sampler2D(OpaqueColor, LinearClamp), nextTexel, 0).rgb;
    for (int y = -1; y <= 1; y++) {
        for (int x = -1; x <= 1; x++) {
            ivec2 texel = nextTexel;
            vec4 sceneColor = nextScene;
            vec2 motion = nextMotion;
            float ndcDepth = nextDepth;
            vec3 opaqueColor = nextOpaque;
            nextTexel = temporalNextTexel(centreTexel, x, y, maxTexel);
            nextScene = texelFetch(sampler2D(SceneColor, LinearClamp), nextTexel, 0);
            nextMotion = texelFetch(sampler2D(MotionTex, LinearClamp), nextTexel, 0).rg;
            nextDepth = texelFetch(sampler2D(SceneDepth, LinearClamp), nextTexel, 0).r;
            nextOpaque = texelFetch(sampler2D(OpaqueColor, LinearClamp), nextTexel, 0).rgb;
            vec3 weightedColor = temporalWeighted(sceneColor.rgb);
            vec4 prepared = temporalPrepared(rgbToYCoCg(weightedColor),
                temporalReactiveDifference(weightedColor, opaqueColor));
            lumas[(y + 1) * 3 + (x + 1)] = toAfloat(prepared.x);
            temporalGather(neighbourhood, kernels, x + 1, y + 1, toAvec3(prepared.xyz), toAfloat(sceneColor.a),
                toAfloat(prepared.w));
            temporalDilate(texel, temporalViewDepth(motion, ndcDepth), closestDepth, closestTexel);
        }
    }

    TemporalSurface surface = temporalPrepareSurface(centreTexel, closestTexel, closestDepth, maxTexel);
    bool ridge = temporalRidge(lumas[0], lumas[1], lumas[2], lumas[3], lumas[4], lumas[5], lumas[6], lumas[7],
        lumas[8]);
    return temporalAccumulatePixel(uv, centreTexel, maxTexel, neighbourhood, kernels, ridge, surface);
}
";

        // ---- The resolve: the core plus the two history outputs, at full precision (TemporalResolveHalfFrag is the
        //      same program at half) ----
        public const string TemporalResolveFrag = "#version 450\n" + TemporalFullPrecisionGlsl + TemporalResolveCoreGlsl
            + TemporalResolveMainGlsl;

        internal const string TemporalResolveMainGlsl = @"
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
layout(location=1) out vec4 oState;
void main() {
    TemporalPixel p = temporalResolvePixel(ivec2(gl_FragCoord.xy));
    oColor = vec4(p.color, p.alpha + vUv.x * 1.0e-30);   // the vUv read changes no output, see the class summary
    oState = vec4(p.confidence, temporalStoreLock(p.stability, p.moved), 0.0, 1.0);
}";

        // ---- The depth store: this frame's linear view depth for next frame's disocclusion test ----
        public const string TemporalDepthStoreFrag = @"#version 450
layout(set=0, binding=0) uniform texture2D SceneDepth;
layout(set=0, binding=1) uniform texture2D MotionTex;
layout(set=0, binding=2) uniform sampler Samp;
// The member is TemporalDepthStoreUniforms.GlslMembers, documented on the struct's field.
layout(set=0, binding=3) uniform DepthStore {" + TemporalDepthStoreUniforms.GlslMembers + @"};
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oDepth;
" + TemporalCommonGlsl + @"
void main() {
    ivec2 texel = ivec2(gl_FragCoord.xy);
    float ndcDepth = texelFetch(sampler2D(SceneDepth, Samp), texel, 0).r;
    vec2 motion = texelFetch(sampler2D(MotionTex, Samp), texel, 0).rg;
    float viewDepth = temporalViewDepth(motion, ndcDepth);
    oDepth = vec4(viewDepth + vUv.x * 1.0e-30, 0.0, 0.0, 1.0);   // the vUv read changes no output
}";
    }
}
