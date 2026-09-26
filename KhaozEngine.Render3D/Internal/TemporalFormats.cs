using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// THE TEMPORAL TARGET FORMATS, decided by Task E1 of the round 2 plan and recorded in the design's plan amendments.
    /// Each is a seam member every backend already renders and samples: half float RGBA is the HDR colour target, half
    /// float RG the distortion field, single float R the linear-depth MRT attachment.
    /// <para><b>WHY NOT R11G11B10 FLOAT FOR THE HISTORY COLOUR.</b> The seam has no such member, Vulkan does not
    /// guarantee it as a colour attachment, and its 6-bit and 5-bit mantissas stall a one-in-sixteen accumulation 12.5
    /// and 25 percent short of its target. Half float stalls at 0.8 percent, and its alpha carries the
    /// transparent-background marker.</para>
    /// </summary>
    internal static class TemporalFormats
    {
        /// <summary>The two display-resolution history colour targets: resolved HDR colour and coverage alpha.</summary>
        public const GpuPixelFormat HistoryColor = GpuPixelFormat.R16G16B16A16Float;

        /// <summary>The two display-resolution confidence and stability targets: the accumulated sample weight over its
        /// cap in red, the thin feature lock in green.</summary>
        public const GpuPixelFormat HistoryConfidence = GpuPixelFormat.R16G16Float;

        /// <summary>The two internal-resolution previous depth targets: linear view depth, background at 1e30.</summary>
        public const GpuPixelFormat PreviousDepth = GpuPixelFormat.R32Float;

        public const int HistoryColorBytesPerPixel = 8;
        public const int HistoryConfidenceBytesPerPixel = 4;
        public const int PreviousDepthBytesPerPixel = 4;

        /// <summary>What the history owner holds for a display and internal size: two colour and two confidence targets
        /// at the display size, two previous depths at the internal size.</summary>
        public static long HistoryBytes(int displayWidth, int displayHeight, int internalWidth, int internalHeight) =>
            2L * displayWidth * displayHeight * (HistoryColorBytesPerPixel + HistoryConfidenceBytesPerPixel)
            + 2L * internalWidth * internalHeight * PreviousDepthBytesPerPixel;
    }
}
