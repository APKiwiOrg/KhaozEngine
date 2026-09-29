using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>The precision of the resolve's per-pixel 3x3 (<see cref="ShaderSources.TemporalFullPrecisionGlsl"/>
    /// and <see cref="ShaderSources.TemporalHalfPrecisionGlsl"/>).</summary>
    internal enum TemporalResolvePrecision
    {
        /// <summary>Single floats throughout.</summary>
        Full,

        /// <summary>The 3x3's range, reconstruction and weights in half floats.</summary>
        Half,
    }

    /// <summary>
    /// WHICH PRECISION A DEVICE'S TEMPORAL PROGRAMS TAKE, from measurement. Metal runs half floats natively, and the
    /// half 3x3 frees registers in the per-pixel pass (TEMPORAL-RESOLVE-UPSCALING-DESIGN amendment 25). Every other
    /// backend keeps full precision. Forced to half on an NVIDIA Tesla T4 (hosted run 36567912437), the split at
    /// 3456x2234 Native cost more than at full (Direct3D 11 +0.34 ms, Vulkan +0.66 on Windows and +0.97 on Linux) and
    /// the two entry points no longer agreed on any of the three (Windows Vulkan colour by up to 218 half steps), since
    /// scalar half on that GPU adds conversions without packed arithmetic. A device takes one precision for all of its
    /// temporal programs, so its two entry points stay identical.
    /// </summary>
    internal static class TemporalResolvePrecisionPolicy
    {
        /// <summary>The precision for a device.</summary>
        public static TemporalResolvePrecision For(IGpuDevice device) => For(device.Backend);

        /// <summary>The precision for a backend.</summary>
        public static TemporalResolvePrecision For(GpuBackendKind backend) =>
            backend.IsMetal() ? TemporalResolvePrecision.Half : TemporalResolvePrecision.Full;
    }
}
