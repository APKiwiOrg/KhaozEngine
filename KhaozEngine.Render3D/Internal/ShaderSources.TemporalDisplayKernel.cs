namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// STEP 4 FOR A CONVERGED PIXEL (TEMPORAL-RESOLVE-UPSCALING-DESIGN section 3, The resolve). The reconstruction's
    /// Lanczos 2 is sized in internal pixels, which band-limits each frame's current sample to the internal Nyquist, so
    /// below Native the accumulated image stays softer than the jittered samples allow. The 3x3 also gathers its
    /// reconstruction with the Lanczos 2 sized in display pixels, whose kernels the sample weight already evaluates
    /// (<c>temporalGather</c>), and a pixel whose history is converged and still takes its colour in proportion to
    /// <c>temporalDisplayShare</c>. Alpha keeps the internal reconstruction. A fresh, moving or reactive pixel, where
    /// few samples have landed near it, keeps the internal kernel. Included in <see cref="TemporalAccumulateGlsl"/>.
    /// The split's second pass at the display's own size compiles without the gather's display-sized sum and the share
    /// (<see cref="TemporalAccumulateAtDisplaySizeFrag"/>).
    /// </summary>
    internal static partial class ShaderSources
    {
        internal const string TemporalDisplayKernelGlsl = @"
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

// The current sample with the display-sized reconstruction (its weighted YCoCg sum and its weight, gathered with the
// 3x3) taken in by the pixel's share. Where the jitter puts the pixel between samples the weight falls towards zero,
// or below it past the kernel's first zero, and the sum says little, so the share scales by the weight up to
// DisplayKernelFullWeight. The result is held to the neighbourhood's range as the internal reconstruction is.
vec3 temporalDisplayCurrent(vec3 current, vec4 display, float share, vec3 neighbourMin, vec3 neighbourMax) {
    share *= clamp(display.w / DisplayKernelFullWeight, 0.0, 1.0);
    return mix(current, clamp(display.xyz / max(display.w, 1.0e-4), neighbourMin, neighbourMax), share);
}
";
    }
}
