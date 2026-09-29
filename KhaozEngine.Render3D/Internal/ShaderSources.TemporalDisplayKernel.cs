namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// STEP 4 FOR A CONVERGED PIXEL (TEMPORAL-RESOLVE-UPSCALING-DESIGN section 3, amendment 26). The reconstruction's
    /// Lanczos 2 is sized in internal pixels, which band-limits each frame's current sample to the internal Nyquist,
    /// so below Native the accumulated image stays softer than the jittered samples allow. A pixel whose history is
    /// converged and still also takes the same 3x3 reconstructed with the Lanczos 2 sized in display pixels, whose
    /// kernels the sample weight already evaluates, in proportion to <c>temporalDisplayShare</c>. A fresh, moving or
    /// reactive pixel, where few samples have landed near it, keeps the internal kernel. Included in
    /// <see cref="TemporalAccumulateGlsl"/>. Each entry point defines <c>temporalCurrentYcc</c>, the prepared colour
    /// of one internal texel as its 3x3 gathered it, so both reconstruct from the same values.
    /// </summary>
    internal static partial class ShaderSources
    {
        internal const string TemporalDisplayKernelGlsl = @"
// The weighted YCoCg of an internal texel as its entry point's 3x3 gathers it, rounded to half (temporalPrepared).
vec3 temporalCurrentYcc(ivec2 texel);

// How much of the display-sized reconstruction a pixel takes: none at Native, where the two kernels are one, or where
// the pixel restarts, and otherwise as its carried confidence passes DisplayKernelConfidenceStart, falling to none as
// it moves DisplayKernelMotionPixels a frame or its content turns reactive.
float temporalDisplayShare(bool useHistory, float confidence, float motionPixels, float reactive) {
    if (!useHistory || !(Jitter.z > 1.0)) return 0.0;
    float converged = clamp((confidence - DisplayKernelConfidenceStart) / (1.0 - DisplayKernelConfidenceStart),
        0.0, 1.0);
    float still = 1.0 - clamp(motionPixels / DisplayKernelMotionPixels, 0.0, 1.0);
    return converged * still * (1.0 - reactive);
}

// The 3x3 reconstructed with the display-sized kernels, its weighted YCoCg sum and its weight. Where the jitter puts
// the pixel between samples the weight falls towards zero, or below it past the kernel's first zero, and the sum
// says little, so the caller takes it in proportion to its weight up to DisplayKernelFullWeight.
vec4 temporalDisplayReconstruction(TemporalKernels kernels, ivec2 centreTexel, ivec2 maxTexel) {
    vec4 sum = vec4(0.0);
    for (int y = -1; y <= 1; y++) {
        for (int x = -1; x <= 1; x++) {
            float weight = toFloat(kernels.displayX[x + 1] * kernels.displayY[y + 1]);
            sum += vec4((temporalCurrentYcc(temporalNeighbourTexel(centreTexel, x, y, maxTexel))), 1.0)
                * weight;
        }
    }
    return sum;
}
";
    }
}
