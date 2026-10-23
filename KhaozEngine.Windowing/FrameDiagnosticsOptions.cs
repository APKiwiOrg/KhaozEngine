using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Windowing;

internal static class FrameDiagnosticsOptions
{
    // Called once at Run startup. Neither the render callback nor the recorder reads this environment value.
    internal static bool ReadNativePoolBatching(Func<string, string?> read, GpuBackendKind backend, bool isMacOS)
    {
        bool enabled = read("KE_FRAME_DIAGNOSTICS_NATIVE_POOL_BATCHING") switch
        {
            null or "0" => false,
            "1" => true,
            _ => throw new InvalidOperationException(
                "KE_FRAME_DIAGNOSTICS_NATIVE_POOL_BATCHING must be exactly '0' or '1' when supplied."),
        };
        if (enabled && (!isMacOS || backend != GpuBackendKind.MetalNative))
            throw new InvalidOperationException("KE_FRAME_DIAGNOSTICS_NATIVE_POOL_BATCHING=1 requires native Metal on macOS.");
        return enabled;
    }

    internal static bool ResolveRendererInitializesSwapchain(string? value, bool current) => value switch
    {
        null => current,
        "0" => false,
        "1" => true,
        _ => throw new InvalidOperationException(
            "KE_FRAME_DIAGNOSTICS_RENDERER_INITIALIZES_SWAPCHAIN must be exactly '0' or '1' when supplied."),
    };
}
