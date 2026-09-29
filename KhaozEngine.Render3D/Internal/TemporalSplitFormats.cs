using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// The split entry point's internal-resolution targets, which its first pass
    /// (<see cref="ShaderSources.TemporalPrepareFrag"/>) writes once per internal texel and its second pass reads, in
    /// the first pass's output order. The first pass also writes the history's previous depth
    /// (<see cref="TemporalFormats.PreviousDepth"/>), which the depth store writes on the fused entry point.
    /// <para><b>BOTH ENTRY POINTS MATCH.</b> Both round the weighted Y, Co and Cg and the reactive difference to half
    /// float (<c>temporalPrepared</c>), so the half-float prepared target holds them exactly. The expected depth and
    /// the edge motion are single floats, as the fused resolve holds them. The surface target is half float: its
    /// motion is the motion target's own half float and its flags a whole number below 256, both exact. Alpha is not
    /// stored, the second pass reads it from the scene colour, so it stays exact under an 8-bit colour target too. The
    /// targets live for one frame, written by the first pass and read by the second, so they need no pair.</para>
    /// </summary>
    internal static class TemporalSplitFormats
    {
        /// <summary>The weighted Y, Co and Cg and the reactive difference (step 7), rounded to half float by both
        /// entry points.</summary>
        public const GpuPixelFormat Prepared = GpuPixelFormat.R16G16B16A16Float;

        /// <summary>The motion the display pixels centred on the texel reproject by, and the surface's flags.</summary>
        public const GpuPixelFormat Surface = GpuPixelFormat.R16G16B16A16Float;

        /// <summary>The expected depth of the surface those pixels reproject by.</summary>
        public const GpuPixelFormat Expected = GpuPixelFormat.R32Float;

        /// <summary>Step 6's edge motion, how far the dilated and centre motions lie apart.</summary>
        public const GpuPixelFormat Edge = GpuPixelFormat.R32Float;

        /// <summary>The targets in the first pass's output order: prepared, surface, expected and edge.</summary>
        public static readonly GpuPixelFormat[] Targets = [Prepared, Surface, Expected, Edge];

        /// <summary>Bytes a texel across the targets.</summary>
        public const int BytesPerTexel = 8 + 8 + 4 + 4;

        /// <summary>What the targets hold at an internal size.</summary>
        public static long Bytes(int internalWidth, int internalHeight) =>
            (long)internalWidth * internalHeight * BytesPerTexel;
    }
}
