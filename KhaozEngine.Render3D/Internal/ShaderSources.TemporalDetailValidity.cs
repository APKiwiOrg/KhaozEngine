namespace KhaozEngine.Render3D.Internal;

internal static partial class ShaderSources
{
    // Only validity crosses the production boundary. A strict finite-history check still needs its candidate
    // fetch, including at the format maximum. Diagnostic masks retain the full observation replay.
    internal const string TemporalDetailValidityGlsl = @"
void temporalDetailClassifyPixel(ivec2 pixel) {
    if (detailDistrust(31u) != 1u) { temporalResolvePixel(pixel); return; }
    vec2 uv, pixelCentre;
    ivec2 maxTexel, centreTexel;
    temporalPixelSite(pixel, uv, pixelCentre, maxTexel, centreTexel);
    float closestDepth = 2.0 * BackgroundLinearDepth;
    ivec2 closestTexel = centreTexel;
    for (int y = -1; y <= 1; y++) {
        for (int x = -1; x <= 1; x++) {
            ivec2 texel = temporalNeighbourTexel(centreTexel, x, y, maxTexel);
            float depth = temporalViewDepth(texelFetch(sampler2D(MotionTex, LinearClamp), texel, 0).rg,
                texelFetch(sampler2D(SceneDepth, LinearClamp), texel, 0).r);
            temporalDilate(texel, depth, closestDepth, closestTexel);
        }
    }
    TemporalSurface surface = temporalPrepareSurface(centreTexel, closestTexel, closestDepth, maxTexel, 0u);
    vec2 internalSize = Sizes.xy, displaySize = Sizes.zw, jitter = Jitter.xy;
    bool historyValid = Jitter.w > 0.5;
" + TemporalAccumulateReprojectionGlsl + @"
    if (useHistory) {
        vec4 fetched = detailStill(motionPixels) ? sampleHistoryCatmullRom(previousUv, displaySize)
            : detailMeanAt(previousUv, displaySize);
" + TemporalAccumulateFiniteGlsl + @"        useHistory = finite;
    }
    detailClassification = vec4(previousUv, motionPixels, useHistory ? 0.0 : 1.0);
}
";
}
