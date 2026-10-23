using System;

namespace KhaozEngine.Windowing;

internal static class FrameDiagnosticsOptions
{
    internal static bool ResolveRendererInitializesSwapchain(string? value, bool current) => value switch
    {
        null => current,
        "0" => false,
        "1" => true,
        _ => throw new InvalidOperationException(
            "KE_FRAME_DIAGNOSTICS_RENDERER_INITIALIZES_SWAPCHAIN must be exactly '0' or '1' when supplied."),
    };
}
