namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// THE SPLIT ENTRY POINT of the temporal resolve: the fused resolve's rules (<see cref="TemporalPrepareGlsl"/> and
    /// <see cref="TemporalAccumulateGlsl"/>) in two fullscreen programs paired with <see cref="FullscreenVert"/>,
    /// recorded by <c>TemporalSplitResolve</c> where <see cref="TemporalResolvePolicy"/> picks it.
    /// <para><b>THE FIRST PASS</b> (<see cref="TemporalPrepareFrag"/>) runs once per internal texel. Every display
    /// pixel's 3x3 is centred on the internal texel its jittered sample lands in, so what the fused resolve derives
    /// from a texel alone is derived here once: its weighted YCoCg and reactive difference, the nearest surface of its
    /// 3x3 and the surface every display pixel centred on it reprojects by (<c>temporalPrepareSurface</c>). It writes
    /// them to <see cref="TemporalSplitFormats"/>'s targets, and the texel's view depth to the history's previous
    /// depth, the depth store's value, so the depth store does not run.</para>
    /// <para><b>THE SECOND PASS</b> (<see cref="TemporalAccumulateFrag"/>) runs once per display pixel. It gathers its
    /// 3x3 from the first pass's colours, the scene's own alpha and the reactive differences, reads its centre texel's
    /// surface back (<c>temporalStoredSurface</c>) and accumulates (<c>temporalAccumulatePixel</c>). The rare branches
    /// that read the scene's inputs, the moving share and the followed mark's check, read them as the fused resolve
    /// does.</para>
    /// <para><b>EXACT BY CONSTRUCTION.</b> Every value the first pass stores is held exactly: the colours and the
    /// reactive difference in half floats, which both entry points round them to in integer steps
    /// (<c>temporalHalf</c>, since packHalf2x16 rounded three ways on NVIDIA Vulkan), the expected depth and the edge
    /// motion in single floats, the motion in the half float the motion target itself holds, and the flags as a whole
    /// number below 256. Alpha is read from the scene colour, whatever its format. So both entry points apply every
    /// rule to the same values. <c>TemporalEntryIdentityGpuTests</c> compares the two histories bit for bit after every
    /// frame of a follow walk, and passes on Metal, on a Tesla T4 on Direct3D 11 and Vulkan, and on WARP. On a software
    /// Vulkan device (llvmpipe) only the state is identical, the colour within a measured bound.</para>
    /// </summary>
    internal static partial class ShaderSources
    {
        // ---- The first pass: once per internal texel ----
        public const string TemporalPrepareFrag = "#version 450\n" + @"
layout(set=0, binding=0) uniform texture2D SceneColor;
layout(set=0, binding=1) uniform texture2D OpaqueColor;
layout(set=0, binding=2) uniform texture2D SceneDepth;
layout(set=0, binding=3) uniform texture2D MotionTex;
layout(set=0, binding=4) uniform sampler LinearClamp;
// The members are TemporalResolveUniforms.GlslMembers, documented on the struct's fields.
layout(set=0, binding=5) uniform Resolve {" + TemporalResolveUniforms.GlslMembers + @"};
" + TemporalCommonGlsl + TemporalResolveTuningGlsl + TemporalNarrowCurrentGlsl + TemporalPrepareGlsl + @"
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oPrepared;
layout(location=1) out vec4 oSurface;
layout(location=2) out vec4 oExpected;
layout(location=3) out vec4 oEdge;
layout(location=4) out vec4 oDepth;
void main() {
    ivec2 maxTexel = ivec2(Sizes.xy) - ivec2(1);
    ivec2 texel = clamp(ivec2(gl_FragCoord.xy), ivec2(0), maxTexel);
    vec3 weightedColor = temporalWeighted(texelFetch(sampler2D(SceneColor, LinearClamp), texel, 0).rgb);
    vec3 ycc = rgbToYCoCg(weightedColor);
    float reactiveDifference = temporalReactiveDifference(weightedColor,
        texelFetch(sampler2D(OpaqueColor, LinearClamp), texel, 0).rgb);
    float closestDepth = 2.0 * BackgroundLinearDepth;
    ivec2 closestTexel = texel;
    for (int y = -1; y <= 1; y++) {
        for (int x = -1; x <= 1; x++) {
            ivec2 at = temporalNeighbourTexel(texel, x, y, maxTexel);
            temporalDilate(at, temporalViewDepth(texelFetch(sampler2D(MotionTex, LinearClamp), at, 0).rg,
                texelFetch(sampler2D(SceneDepth, LinearClamp), at, 0).r), closestDepth, closestTexel);
        }
    }
    TemporalSurface surface = temporalPrepareSurface(texel, closestTexel, closestDepth, maxTexel);
    float viewDepth = temporalViewDepth(texelFetch(sampler2D(MotionTex, LinearClamp), texel, 0).rg,
        texelFetch(sampler2D(SceneDepth, LinearClamp), texel, 0).r);
    oPrepared = temporalPrepared(ycc, reactiveDifference);
    oSurface = temporalStoreSurface(surface);
    oExpected = vec4(surface.expectedDepth, 0.0, 0.0, 1.0);
    oEdge = vec4(surface.edgeMotion, 0.0, 0.0, 1.0);
    oDepth = vec4(viewDepth + vUv.x * 1.0e-30, 0.0, 0.0, 1.0);   // the vUv read changes no output
}";

        // ---- The second pass: once per display pixel, into the history pair's write targets ----
        public const string TemporalAccumulateFrag = "#version 450\n" + @"
layout(set=0, binding=0) uniform texture2D SceneColor;
layout(set=0, binding=1) uniform texture2D SceneDepth;
layout(set=0, binding=2) uniform texture2D MotionTex;
layout(set=0, binding=3) uniform texture2D PrevDepth;
layout(set=0, binding=4) uniform texture2D HistoryColor;
layout(set=0, binding=5) uniform texture2D HistoryConfidence;
layout(set=0, binding=6) uniform texture2D PreparedColour;
layout(set=0, binding=7) uniform texture2D PreparedSurface;
layout(set=0, binding=8) uniform texture2D PreparedExpected;
layout(set=0, binding=9) uniform texture2D PreparedEdge;
layout(set=0, binding=10) uniform sampler LinearClamp;
// The members are TemporalResolveUniforms.GlslMembers, documented on the struct's fields.
layout(set=0, binding=11) uniform Resolve {" + TemporalResolveUniforms.GlslMembers + @"};
" + TemporalCommonGlsl + TemporalResolveTuningGlsl + TemporalNarrowBothGlsl + TemporalPrepareGlsl
            + TemporalAccumulateGlsl + @"
// The split's per-pixel resolve: its 3x3 gathered from the first pass's targets (temporalGather), its centre texel's
// surface read back (temporalStoredSurface), and the accumulation (temporalAccumulatePixel).
TemporalPixel temporalSplitPixel(ivec2 displayPixel) {
    vec2 uv;
    vec2 pixelCentre;
    ivec2 maxTexel;
    ivec2 centreTexel;
    temporalPixelSite(displayPixel, uv, pixelCentre, maxTexel, centreTexel);
    TemporalKernels kernels = temporalKernels(pixelCentre, centreTexel, maxTexel);
    TemporalNeighbourhood neighbourhood = temporalNeighbourhood();
    float lumas[9];

    ivec2 nextTexel = temporalFirstTexel(centreTexel, maxTexel);
    vec4 nextPrepared = texelFetch(sampler2D(PreparedColour, LinearClamp), nextTexel, 0);
    float nextAlpha = texelFetch(sampler2D(SceneColor, LinearClamp), nextTexel, 0).a;
    for (int y = -1; y <= 1; y++) {
        for (int x = -1; x <= 1; x++) {
            vec4 prepared = nextPrepared;
            float alpha = nextAlpha;
            nextTexel = temporalNextTexel(centreTexel, x, y, maxTexel);
            nextPrepared = texelFetch(sampler2D(PreparedColour, LinearClamp), nextTexel, 0);
            nextAlpha = texelFetch(sampler2D(SceneColor, LinearClamp), nextTexel, 0).a;
            lumas[(y + 1) * 3 + (x + 1)] = prepared.x;
            temporalGather(neighbourhood, kernels, x + 1, y + 1, prepared.xyz, alpha, prepared.w);
        }
    }

    TemporalSurface surface = temporalStoredSurface(
        texelFetch(sampler2D(PreparedSurface, LinearClamp), centreTexel, 0),
        texelFetch(sampler2D(PreparedExpected, LinearClamp), centreTexel, 0).r,
        texelFetch(sampler2D(PreparedEdge, LinearClamp), centreTexel, 0).r);
    bool ridge = temporalRidge(lumas[0], lumas[1], lumas[2], lumas[3], lumas[4], lumas[5], lumas[6], lumas[7],
        lumas[8]);
    return temporalAccumulatePixel(uv, centreTexel, maxTexel, neighbourhood, kernels, ridge, surface);
}

layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
layout(location=1) out vec4 oState;
void main() {
    TemporalPixel p = temporalSplitPixel(ivec2(gl_FragCoord.xy));
    oColor = vec4(p.color, p.alpha + vUv.x * 1.0e-30);   // the vUv read changes no output
    oState = vec4(p.confidence, temporalStoreLock(p.stability, p.moved), 0.0, 1.0);
}";
    }
}
