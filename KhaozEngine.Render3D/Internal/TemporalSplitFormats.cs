using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// The two-pass resolve's internal-resolution targets, which pass one
    /// (<see cref="ShaderSources.TemporalPrepareFrag"/>) writes once per internal texel and pass two reads. Pass one
    /// also writes the history's previous depth (<see cref="TemporalFormats.PreviousDepth"/>), which the single pass's
    /// depth store writes, so that store does not run on this path. Only the weighted colour and the reactive
    /// difference are rounded to half floats. The motion is the motion target's half float and the flags a small whole
    /// number, both held exactly, and the expected depth and the edge release keep single floats.
    /// </summary>
    internal static class TemporalSplitFormats
    {
        /// <summary>Weighted YCoCg and alpha.</summary>
        public const GpuPixelFormat Prepared = GpuPixelFormat.R16G16B16A16Float;

        /// <summary>The reactive difference, the motion the pixel reprojects by, and the flags.</summary>
        public const GpuPixelFormat Reproject = GpuPixelFormat.R16G16B16A16Float;

        /// <summary>The expected depth of the surface the pixel reprojects by.</summary>
        public const GpuPixelFormat Expected = GpuPixelFormat.R32Float;

        /// <summary>Step 6's edge release, the dilated and centre motions apart, capped at one.</summary>
        public const GpuPixelFormat Edge = GpuPixelFormat.R32Float;

        /// <summary>Bytes a texel across the four targets.</summary>
        public const int BytesPerTexel = 8 + 8 + 4 + 4;

        /// <summary>What the four targets hold at an internal size.</summary>
        public static long Bytes(int internalWidth, int internalHeight) =>
            (long)internalWidth * internalHeight * BytesPerTexel;
    }
}
