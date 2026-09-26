using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu;

/// <summary>The model framebuffer's two output descriptions: the HDR base target, and the same target with the motion
/// attachment every temporal variant is built for.</summary>
internal static class ModelTargets
{
    internal static GpuOutputDescription Base => new(GpuPixelFormat.D32FloatS8UInt,
        GpuPixelFormat.R16G16B16A16Float, GpuPixelFormat.R8G8B8A8UNorm, GpuPixelFormat.R32Float);

    internal static GpuOutputDescription Temporal => new(GpuPixelFormat.D32FloatS8UInt,
        GpuPixelFormat.R16G16B16A16Float, GpuPixelFormat.R8G8B8A8UNorm, GpuPixelFormat.R32Float,
        GpuPixelFormat.R16G16Float);
}
