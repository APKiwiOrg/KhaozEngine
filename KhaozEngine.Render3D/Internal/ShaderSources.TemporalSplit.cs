namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// THE TWO-PASS RESOLVE, a measured alternative to <see cref="TemporalResolveFrag"/> that
    /// <see cref="TemporalResolvePath"/> selects. It decides what the single pass decides, in two fullscreen programs
    /// paired with <see cref="FullscreenVert"/>.
    /// <para><b>PASS ONE</b> (<see cref="TemporalPrepareFrag"/>) runs once per internal texel. Every display pixel's
    /// 3x3 is centred on the internal texel its jittered sample lands in, so what the single pass derives from that
    /// texel alone, not from the display pixel's position, is derived here once: the texel's weighted YCoCg colour and
    /// alpha, its reactive difference, its view depth (the depth store's value, written straight into the history's
    /// previous depth), and the dilated nearest surface with steps 1 and 3's rules for it (the reach, the moved and
    /// band tests, the narrow test in this frame's depth). Its outputs are <see cref="TemporalSplitFormats"/>'s.</para>
    /// <para><b>PASS TWO</b> (<see cref="TemporalSplitFrag"/>) runs once per display pixel: the Lanczos reconstruction
    /// and the neighbourhood statistics over pass one's colours, the previous position from pass one's motion, the
    /// state and footprint reads there, the history, the clip, the lock, the moving share and the blend, with the
    /// single pass's expressions in the single pass's order.</para>
    /// <para>The pure helpers are copies of the single pass's, since that program's text stays as it is.</para>
    /// </summary>
    internal static partial class ShaderSources
    {
        // ---- The pure helpers both passes use, copies of TemporalResolveCoreGlsl's ----
        const string TemporalSplitPureGlsl = @"
float temporalLuma(vec3 c) { return dot(c, vec3(0.2126, 0.7152, 0.0722)); }
vec3 toWeighted(vec3 c) { return c / (1.0 + temporalLuma(c)); }
vec3 fromWeighted(vec3 w) { return w / max(1.0 - temporalLuma(w), 1.0e-4); }
vec3 rgbToYCoCg(vec3 c) {
    return vec3(0.25 * c.r + 0.5 * c.g + 0.25 * c.b, 0.5 * c.r - 0.5 * c.b, -0.25 * c.r + 0.5 * c.g - 0.25 * c.b);
}
vec3 yCoCgToRgb(vec3 c) { return vec3(c.x + c.y - c.z, c.x + c.z, c.x - c.y - c.z); }
float temporalViewDepth(vec2 motion, float ndcDepth) {
    return abs(motion.x) > MotionSentinel ? BackgroundLinearDepth : temporalLinearDepth(ndcDepth, CurrentDepth);
}
// Pass one's flags for the centre texel, one bit each, stored as a small whole number that a half float holds exactly.
const int SplitBackground = 1;
const int SplitDepthTested = 2;
const int SplitNearerMoved = 4;
const int SplitBand = 8;
const int SplitOwnReprojected = 16;
const int SplitNarrowMoving = 32;
const int SplitMovingEdge = 64;
";

        // ---- Pass one: once per internal texel ----
        public const string TemporalPrepareFrag = "#version 450\n" + @"
layout(set=0, binding=0) uniform texture2D SceneColor;
layout(set=0, binding=1) uniform texture2D OpaqueColor;
layout(set=0, binding=2) uniform texture2D SceneDepth;
layout(set=0, binding=3) uniform texture2D MotionTex;
layout(set=0, binding=4) uniform sampler LinearClamp;
// The members are TemporalResolveUniforms.GlslMembers, documented on the struct's fields.
layout(set=0, binding=5) uniform Resolve {" + TemporalResolveUniforms.GlslMembers + @"};
" + TemporalCommonGlsl + TemporalResolveTuningGlsl + TemporalSplitPureGlsl + @"
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oPrepared;    // weighted YCoCg, alpha
layout(location=1) out vec4 oReproject;   // reactive difference, the chosen motion, the flags
layout(location=2) out vec4 oExpected;    // the expected depth of the surface the pixel reprojects by
layout(location=3) out vec4 oEdge;        // the lock's edge release
layout(location=4) out vec4 oDepth;       // this frame's view depth, the depth store's value

float prepareViewDepth(ivec2 texel) {
    return temporalViewDepth(texelFetch(sampler2D(MotionTex, LinearClamp), texel, 0).rg,
        texelFetch(sampler2D(SceneDepth, LinearClamp), texel, 0).r);
}

// temporalReproject's tests for one texel's surface point, without the display pixel's previous position, which
// pass two takes from the motion this pass stores.
void prepareReproject(vec2 sampleInternal, vec2 motion, float depth, bool isBackground, vec2 internalSize,
    out float expectedDepth, out bool depthTested, out float travel) {
    expectedDepth = BackgroundLinearDepth;
    depthTested = true;
    travel = 0.0;
    if (!isBackground) {
        vec2 sampleUv = sampleInternal / internalSize;
        vec2 sampleNdc = vec2(sampleUv.x * 2.0 - 1.0, 1.0 - sampleUv.y * 2.0);
        float clipW = CurrentDepth.x > 0.5 ? depth : 1.0;
        vec4 previousView = CurrentToPrevious * vec4(sampleNdc * clipW, depth, 1.0);
        expectedDepth = -previousView.z;
        vec4 staticClip = PreviousProjection * previousView;
        depthTested = false;
        travel = 1.0e30;   // beyond every bound, in internal pixels
        if (staticClip.w > 1.0e-6) {
            vec2 staticUv = vec2(staticClip.x / staticClip.w * 0.5 + 0.5, 0.5 - staticClip.y / staticClip.w * 0.5);
            vec2 surfaceMotion = (staticUv - (sampleUv - motion)) * internalSize;
            float movingThreshold = MovingSurfaceInternalPixels
                + length(motion * internalSize) * MovingSurfaceMotionFraction;
            travel = length(surfaceMotion);
            depthTested = travel <= movingThreshold;
        }
    }
}

// temporalNarrowDepth in this frame's depth.
bool prepareNarrowDepth(ivec2 texel, float depth, ivec2 maxTexel) {
    float limit = 1.0 - DisocclusionTolerance;
    bool run[8];   // left 1 and 2, right 1 and 2, up 1 and 2, down 1 and 2: the texel there is at this depth
    for (int i = 0; i < 8; i++) {
        int stride = (i & 1) + 1;
        ivec2 offset = i < 2 ? ivec2(-stride, 0) : i < 4 ? ivec2(stride, 0)
            : i < 6 ? ivec2(0, -stride) : ivec2(0, stride);
        float there = prepareViewDepth(clamp(texel + offset, ivec2(0), maxTexel));
        run[i] = !(depth < there * limit || there < depth * limit);
    }
    bool narrowRow = !(run[0] && run[2]) && !(run[0] && run[1]) && !(run[2] && run[3]);
    bool narrowColumn = !(run[4] && run[6]) && !(run[4] && run[5]) && !(run[6] && run[7]);
    return narrowRow || narrowColumn;
}
" + TemporalPrepareMainGlsl;

        // Pass one's body: the single pass's texel conversions for the texel itself, its 3x3 dilation, and the lines of
        // temporalResolvePixel from the nearest texel's read back to the own-motion reprojection, in the same order.
        const string TemporalPrepareMainGlsl = @"
void main() {
    vec2 internalSize = Sizes.xy;
    vec2 jitter = Jitter.xy;
    bool historyValid = Jitter.w > 0.5;
    ivec2 maxTexel = ivec2(internalSize) - ivec2(1);
    ivec2 centreTexel = clamp(ivec2(gl_FragCoord.xy), ivec2(0), maxTexel);

    vec4 sceneColor = texelFetch(sampler2D(SceneColor, LinearClamp), centreTexel, 0);
    vec3 opaqueColor = texelFetch(sampler2D(OpaqueColor, LinearClamp), centreTexel, 0).rgb;
    vec3 weightedColor = toWeighted(min(max(sceneColor.rgb, vec3(0.0)), vec3(HalfMax)));
    float reactiveDifference = abs(temporalLuma(weightedColor)
        - temporalLuma(toWeighted(min(max(opaqueColor, vec3(0.0)), vec3(HalfMax)))));

    // Step 1: the nearest surface in the 3x3, in the single pass's order.
    float closestDepth = 2.0 * BackgroundLinearDepth;
    ivec2 closestTexel = centreTexel;
    for (int y = -1; y <= 1; y++) {
        for (int x = -1; x <= 1; x++) {
            ivec2 texel = clamp(centreTexel + ivec2(x, y), ivec2(0), maxTexel);
            float viewDepth = prepareViewDepth(texel);
            if (viewDepth < closestDepth) {
                closestDepth = viewDepth;
                closestTexel = texel;
            }
        }
    }
    vec2 closestSample = vec2(centreTexel) + 0.5 - jitter;
    vec2 closestMotion = vec2(0.0);
    bool closestIsBackground = true;
    if (closestDepth < 2.0 * BackgroundLinearDepth) {
        closestSample = vec2(closestTexel) + 0.5 - jitter;
        closestMotion = texelFetch(sampler2D(MotionTex, LinearClamp), closestTexel, 0).rg;
        closestIsBackground = abs(closestMotion.x) > MotionSentinel;
    }
    vec2 centreSample = vec2(centreTexel) + 0.5 - jitter;
    vec2 centreMotion = texelFetch(sampler2D(MotionTex, LinearClamp), centreTexel, 0).rg;
    bool centreIsBackground = abs(centreMotion.x) > MotionSentinel;
    float centreDepth = temporalViewDepth(centreMotion,
        texelFetch(sampler2D(SceneDepth, LinearClamp), centreTexel, 0).r);

    float expectedDepth;
    bool depthTested;
    float travel;
    prepareReproject(closestSample, closestMotion, closestDepth, closestIsBackground, internalSize, expectedDepth,
        depthTested, travel);


    // Step 6's edge signal and steps 1 and 3's reach and band, as the single pass decides them.
    float edgeMotion = 0.0;
    float centreScreenMotion = 0.0;
    if (!closestIsBackground) {
        vec2 centreOwn = centreMotion;
        if (centreIsBackground) {
            vec2 centreUv = (vec2(centreTexel) + 0.5 - jitter) / internalSize;
            vec4 centrePrevious = BackgroundToPrevious * vec4(centreUv.x * 2.0 - 1.0, 1.0 - centreUv.y * 2.0, 1.0, 1.0);
            centreOwn = centrePrevious.w > 1.0e-6
                ? centreUv - vec2(centrePrevious.x / centrePrevious.w * 0.5 + 0.5,
                    0.5 - centrePrevious.y / centrePrevious.w * 0.5)
                : vec2(2.0);   // behind last frame's camera: as far apart as motion goes
        }
        edgeMotion = length((closestMotion - centreOwn) * internalSize);
        centreScreenMotion = length(centreOwn * internalSize);
    }
    bool movingEdge = edgeMotion > max(LockEdgeFloorInternalPixels,
        length(closestMotion * internalSize) * LockEdgeMotionFraction);
    bool centreFarther = centreDepth > closestDepth * (1.0 + DisocclusionTolerance);
    bool ownReprojected = !depthTested && edgeMotion > DilationReachInternalPixels && centreFarther;
    float closestScreenMotion = length(closestMotion * internalSize);
    bool nearerMoved = !depthTested || travel > WorldMotionMetres * PreviousProjection[0][0] * internalSize.x * 0.5
        / (CurrentDepth.x > 0.5 ? closestDepth : 1.0) + closestScreenMotion * MovingSurfaceMotionFraction;
    bool band = historyValid && !ownReprojected && nearerMoved
        && (movingEdge ? centreScreenMotion > closestScreenMotion && centreFarther
            : travel > 2.0 * closestScreenMotion);
    bool nearerNarrow = (ownReprojected || band) && prepareNarrowDepth(closestTexel, closestDepth, maxTexel);
    bool narrowMoving = ownReprojected && nearerNarrow;
    band = band && !nearerNarrow;
    vec2 chosenMotion = closestMotion;
    bool chosenBackground = closestIsBackground;
    if (ownReprojected) {
        prepareReproject(centreSample, centreMotion, centreDepth, centreIsBackground, internalSize, expectedDepth,
            depthTested, travel);
        chosenMotion = centreMotion;
        chosenBackground = centreIsBackground;
    }

    int flags = (chosenBackground ? SplitBackground : 0) | (depthTested ? SplitDepthTested : 0)
        | (nearerMoved ? SplitNearerMoved : 0) | (band ? SplitBand : 0) | (ownReprojected ? SplitOwnReprojected : 0)
        | (narrowMoving ? SplitNarrowMoving : 0) | (movingEdge ? SplitMovingEdge : 0);
    oPrepared = vec4(rgbToYCoCg(weightedColor), sceneColor.a);
    oReproject = vec4(reactiveDifference, chosenMotion, float(flags));
    oExpected = vec4(expectedDepth, 0.0, 0.0, 1.0);
    oEdge = vec4(clamp(edgeMotion * LockEdgeRelease, 0.0, 1.0), 0.0, 0.0, 1.0);
    oDepth = vec4(centreDepth + vUv.x * 1.0e-30, 0.0, 0.0, 1.0);   // the vUv read changes no output
}
";
    }
}
