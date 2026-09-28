using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// The split entry point's internal-resolution targets, which its first pass
    /// (<see cref="ShaderSources.TemporalPrepareFrag"/>) writes once per internal texel and its second pass reads, in
    /// the first pass's output order. The first pass also writes the history's previous depth
    /// (<see cref="TemporalFormats.PreviousDepth"/>), which the depth store writes on the fused entry point.
    /// <para><b>EXACT, SO BOTH ENTRY POINTS MATCH.</b> The weighted Y, Co and Cg, the reactive difference, the expected
    /// depth and the edge motion are single floats, as the fused resolve holds them. The surface target is half float:
    /// its motion is the motion target's own half float and its flags a whole number below 256, both exact. The seam
    /// has no four-channel single-float format, so each single float has its own target. Alpha is not stored, the
    /// second pass reads it from the scene colour, so it stays exact under an 8-bit colour target too. The targets
    /// live for one frame, written by the first pass and read by the second, so they need no pair.</para>
    /// </summary>
    internal static class TemporalSplitFormats
    {
        /// <summary>Each of the weighted Y, Co and Cg.</summary>
        public const GpuPixelFormat Colour = GpuPixelFormat.R32Float;

        /// <summary>The reactive difference (step 7).</summary>
        public const GpuPixelFormat Reactive = GpuPixelFormat.R32Float;

        /// <summary>The motion the display pixels centred on the texel reproject by, and the surface's flags.</summary>
        public const GpuPixelFormat Surface = GpuPixelFormat.R16G16B16A16Float;

        /// <summary>The expected depth of the surface those pixels reproject by.</summary>
        public const GpuPixelFormat Expected = GpuPixelFormat.R32Float;

        /// <summary>Step 6's edge motion, how far the dilated and centre motions lie apart.</summary>
        public const GpuPixelFormat Edge = GpuPixelFormat.R32Float;

        /// <summary>The targets in the first pass's output order: Y, Co, Cg, reactive, surface, expected and
        /// edge.</summary>
        public static readonly GpuPixelFormat[] Targets =
            [Colour, Colour, Colour, Reactive, Surface, Expected, Edge];

        /// <summary>Bytes a texel across the targets.</summary>
        public const int BytesPerTexel = 4 + 4 + 4 + 4 + 8 + 4 + 4;

        /// <summary>What the targets hold at an internal size.</summary>
        public static long Bytes(int internalWidth, int internalHeight) =>
            (long)internalWidth * internalHeight * BytesPerTexel;
    }
}
